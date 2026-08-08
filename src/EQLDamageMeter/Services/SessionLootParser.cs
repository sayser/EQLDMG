using System.Globalization;
using System.Text.RegularExpressions;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public static partial class SessionLootParser
{
    private static readonly object RuntimeGate = new();
    private static readonly Dictionary<string, DateTime> MobActivity = new(StringComparer.OrdinalIgnoreCase);
    private static long _pendingCoinCopper;
    private static DateTime _pendingCoinAt;
    private static DateTime _lastItemLootAt;

    public static void ResetRuntime()
    {
        lock (RuntimeGate)
        {
            MobActivity.Clear();
            _pendingCoinCopper = 0;
            _pendingCoinAt = default;
            _lastItemLootAt = default;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> while holding the loot-runtime lock so backfill
    /// cannot interleave with live Observe against the shared static state.
    /// </summary>
    public static T WithExclusiveRuntime<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (RuntimeGate)
            return action();
    }

    public static bool TryObserve(SessionRecord session, DateTime timestamp, string message)
    {
        lock (RuntimeGate)
        {
            if (TryReadSold(message, out var soldItem, out var soldMob, out var soldCount, out var soldValue))
                return RecordItem(session, timestamp, soldMob, soldItem, "Sold", soldCount, soldValue, null);

            if (TryReadStored(message, out var storedItem, out var storedMob, out var storedCount))
                return RecordItem(session, timestamp, storedMob, storedItem, "Stored", storedCount, 0, "currency");

            if (TryReadMerged(message, out var mergedItem, out var mergedMob, out var created))
                return RecordItem(session, timestamp, mergedMob, mergedItem, "Merged", 1, 0, created);

            if (TryReadKept(message, out var keptItem, out var keptMob, out var keptCount))
                return RecordItem(session, timestamp, keptMob, keptItem, "Kept", keptCount, 0, null);

            if (TryReadCoin(message, out var copper))
                return RecordCoin(session, timestamp, copper);

            return false;
        }
    }

    public static bool TryReadLootedItemName(string message, out string itemName)
    {
        itemName = string.Empty;
        if (TryReadSold(message, out var soldItem, out _, out _, out _))
        {
            itemName = soldItem;
            return true;
        }

        if (TryReadStored(message, out var storedItem, out _, out _))
        {
            itemName = storedItem;
            return true;
        }

        if (TryReadMerged(message, out var mergedItem, out _, out _))
        {
            itemName = mergedItem;
            return true;
        }

        if (TryReadKept(message, out var keptItem, out _, out _))
        {
            itemName = keptItem;
            return true;
        }

        return false;
    }

    public static SessionLootData Clone(SessionLootData source) => new()
    {
        CoinCopper = source.CoinCopper,
        LastMobName = source.LastMobName,
        Mobs = source.Mobs.Select(CloneMob).ToList()
    };

    public static string FormatCopper(long copper)
    {
        if (copper <= 0) return "0c";
        var plat = copper / 1000;
        var gold = copper % 1000 / 100;
        var silver = copper % 100 / 10;
        var cop = copper % 10;
        var parts = new List<string>();
        if (plat > 0) parts.Add($"{plat}p");
        if (gold > 0) parts.Add($"{gold}g");
        if (silver > 0) parts.Add($"{silver}s");
        if (cop > 0 || parts.Count == 0) parts.Add($"{cop}c");
        return string.Join(" ", parts);
    }

    private static SessionMobLoot CloneMob(SessionMobLoot mob) => new()
    {
        Name = mob.Name,
        CorpsesLooted = mob.CorpsesLooted,
        CoinCopper = mob.CoinCopper,
        Items = mob.Items.Select(CloneItem).ToList(),
        Kills = mob.Kills.Select(kill => new SessionMobKill
        {
            Timestamp = kill.Timestamp,
            CoinCopper = kill.CoinCopper,
            Items = kill.Items.Select(CloneItem).ToList()
        }).ToList()
    };

    private static SessionLootItem CloneItem(SessionLootItem item) => new()
    {
        Name = item.Name,
        Disposition = item.Disposition,
        Count = item.Count,
        ValueCopper = item.ValueCopper,
        Note = item.Note
    };

    private static bool RecordItem(SessionRecord session, DateTime timestamp, string mobName, string itemName,
        string disposition, int count, long valueCopper, string? note)
    {
        var mob = GetOrCreateMob(session, mobName);
        var kill = GetOrStartKill(mob, timestamp);
        TouchMob(mob.Name, timestamp);
        session.Loot.LastMobName = mob.Name;
        _lastItemLootAt = timestamp;
        ApplyPendingCoin(mob, kill, timestamp);

        AddOrStackItem(kill.Items, itemName, disposition, count, valueCopper, note);
        AddOrStackItem(mob.Items, itemName, disposition, count, valueCopper, note);
        return true;
    }

    private static bool RecordCoin(SessionRecord session, DateTime timestamp, long copper)
    {
        session.Loot.CoinCopper += copper;
        _pendingCoinCopper += copper;
        _pendingCoinAt = timestamp;

        // Coin lines do not name the mob. Default: keep pending for the next RecordItem.
        // Only attach immediately when loot items for this corpse just landed (typical
        // "item then coin" order). A tight 2s window prevents the next corpse's early
        // coin line from crediting the previous mob.
        var mobName = session.Loot.LastMobName;
        if (string.IsNullOrWhiteSpace(mobName) || _lastItemLootAt == default ||
            timestamp - _lastItemLootAt > TimeSpan.FromSeconds(2))
            return true;

        var key = NormalizeMobKey(mobName);
        var mob = session.Loot.Mobs.FirstOrDefault(item =>
            NormalizeMobKey(item.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (mob is null || mob.Kills.Count == 0) return true;
        var kill = mob.Kills[^1];
        if (kill.Items.Count == 0) return true;

        ApplyPendingCoin(mob, kill, timestamp);
        TouchMob(mob.Name, timestamp);
        return true;
    }

    private static void ApplyPendingCoin(SessionMobLoot mob, SessionMobKill kill, DateTime timestamp)
    {
        if (_pendingCoinCopper <= 0) return;
        if (_pendingCoinAt != default && timestamp - _pendingCoinAt > TimeSpan.FromSeconds(30))
        {
            _pendingCoinCopper = 0;
            return;
        }

        mob.CoinCopper += _pendingCoinCopper;
        kill.CoinCopper += _pendingCoinCopper;
        _pendingCoinCopper = 0;
        _pendingCoinAt = default;
    }

    private static void AddOrStackItem(List<SessionLootItem> items, string itemName, string disposition,
        int count, long valueCopper, string? note)
    {
        var existing = items.FirstOrDefault(item =>
            item.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase) &&
            item.Disposition.Equals(disposition, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Note, note, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            items.Add(new SessionLootItem
            {
                Name = itemName,
                Disposition = disposition,
                Count = count,
                ValueCopper = valueCopper,
                Note = note
            });
            return;
        }

        existing.Count += count;
        existing.ValueCopper += valueCopper;
    }

    private static SessionMobLoot GetOrCreateMob(SessionRecord session, string mobName)
    {
        var key = NormalizeMobKey(mobName);
        var existing = session.Loot.Mobs.FirstOrDefault(item =>
            NormalizeMobKey(item.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var created = new SessionMobLoot { Name = mobName.Trim() };
        session.Loot.Mobs.Add(created);
        return created;
    }

    private static SessionMobKill GetOrStartKill(SessionMobLoot mob, DateTime timestamp)
    {
        var key = NormalizeMobKey(mob.Name);
        var isNewWindow = !MobActivity.TryGetValue(key, out var last) ||
                          timestamp - last > TimeSpan.FromSeconds(20);
        if (!isNewWindow && mob.Kills.Count > 0)
            return mob.Kills[^1];

        mob.CorpsesLooted++;
        var kill = new SessionMobKill { Timestamp = timestamp };
        mob.Kills.Add(kill);
        return kill;
    }

    private static void TouchMob(string mobName, DateTime timestamp)
    {
        MobActivity[NormalizeMobKey(mobName)] = timestamp;
        if (MobActivity.Count < 32) return;
        foreach (var stale in MobActivity
                     .Where(pair => timestamp - pair.Value > TimeSpan.FromMinutes(2))
                     .Select(pair => pair.Key)
                     .ToArray())
            MobActivity.Remove(stale);
    }

    private static string NormalizeMobKey(string mobName)
    {
        var name = mobName.Trim();
        name = ArticlePrefixRegex().Replace(name, string.Empty);
        return WhitespaceRegex().Replace(name, " ").Trim();
    }

    private static bool TryReadSold(string message, out string item, out string mob, out int count, out long value)
    {
        item = mob = string.Empty;
        count = 1;
        value = 0;
        var match = SoldRegex().Match(message);
        if (!match.Success) return false;
        count = match.Groups["count"].Success
            ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture)
            : 1;
        item = CleanItemName(match.Groups["item"].Value);
        mob = CleanMobName(match.Groups["mob"].Value);
        value = ParseCoinText(match.Groups["price"].Value);
        return item.Length > 0 && mob.Length > 0;
    }

    private static bool TryReadStored(string message, out string item, out string mob, out int count)
    {
        item = mob = string.Empty;
        count = 1;
        var match = StoredRegex().Match(message);
        if (!match.Success) return false;
        count = match.Groups["count"].Success
            ? int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture)
            : 1;
        item = CleanItemName(match.Groups["item"].Value);
        mob = CleanMobName(match.Groups["mob"].Value);
        return item.Length > 0 && mob.Length > 0;
    }

    private static bool TryReadMerged(string message, out string item, out string mob, out string created)
    {
        item = mob = created = string.Empty;
        var match = MergedRegex().Match(message);
        if (!match.Success) return false;
        item = CleanItemName(match.Groups["item"].Value);
        mob = CleanMobName(match.Groups["mob"].Value);
        created = match.Groups["created"].Value.Trim();
        return item.Length > 0 && mob.Length > 0;
    }

    private static bool TryReadKept(string message, out string item, out string mob, out int count)
    {
        item = mob = string.Empty;
        count = 1;
        var bracket = BracketKeptRegex().Match(message);
        if (bracket.Success)
        {
            count = bracket.Groups["count"].Success
                ? int.Parse(bracket.Groups["count"].Value, CultureInfo.InvariantCulture)
                : 1;
            item = CleanItemName(bracket.Groups["item"].Value);
            mob = CleanMobName(bracket.Groups["mob"].Value);
            return item.Length > 0 && mob.Length > 0;
        }

        // Avoid matching sold/stored/merged lines that share the same prefix.
        if (message.Contains(" and sold it ", StringComparison.Ordinal) ||
            message.Contains(" and stored it ", StringComparison.Ordinal) ||
            message.Contains(" to create ", StringComparison.Ordinal))
            return false;

        var plain = PlainKeptRegex().Match(message);
        if (!plain.Success) return false;
        count = plain.Groups["count"].Success
            ? int.Parse(plain.Groups["count"].Value, CultureInfo.InvariantCulture)
            : 1;
        item = CleanItemName(plain.Groups["item"].Value);
        mob = CleanMobName(plain.Groups["mob"].Value);
        return item.Length > 0 && mob.Length > 0;
    }

    private static bool TryReadCoin(string message, out long copper)
    {
        copper = 0;
        var match = CoinRegex().Match(message);
        if (!match.Success) return false;
        copper = ParseCoinText(match.Groups["coin"].Value);
        return true;
    }

    private static string CleanItemName(string value)
    {
        var item = value.Trim();
        item = ItemArticleRegex().Replace(item, string.Empty);
        return item.Trim();
    }

    private static string CleanMobName(string value)
    {
        var mob = value.Trim();
        mob = PossessiveCorpseRegex().Replace(mob, string.Empty);
        return mob.Trim();
    }

    private static long ParseCoinText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            text.Contains("free", StringComparison.OrdinalIgnoreCase))
            return 0;

        long total = 0;
        foreach (Match match in CoinPartRegex().Matches(text))
        {
            var amount = long.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            total += match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "p" or "platinum" or "platinums" => amount * 1000,
                "g" or "gold" or "golds" => amount * 100,
                "s" or "silver" or "silvers" => amount * 10,
                "c" or "copper" or "coppers" => amount,
                _ => 0
            };
        }

        return total;
    }

    [GeneratedRegex(@"^You looted (?:(?<count>\d+) )?(?:an? )?(?<item>.+?) from (?<mob>.+?)(?:'s)? corpse and sold it for (?<price>.+?)\.?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SoldRegex();

    [GeneratedRegex(@"^You looted (?:(?<count>\d+) )?(?:an? )?(?<item>.+?) from (?<mob>.+?)(?:'s)? corpse and stored it in your currency\.?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StoredRegex();

    [GeneratedRegex(@"^You looted (?:an? )?(?<item>.+?) from (?<mob>.+?)(?:'s)? corpse to create (?<created>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MergedRegex();

    [GeneratedRegex(@"^--You have looted (?:(?<count>\d+) )?(?:an? )?(?<item>.+?) from (?<mob>.+?)(?:'s)? corpse\.--$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BracketKeptRegex();

    [GeneratedRegex(@"^You looted (?:(?<count>\d+) )?(?:an? )?(?<item>.+?) from (?<mob>.+?)(?:'s)? corpse\.?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlainKeptRegex();

    [GeneratedRegex(@"^You receive (?<coin>.+?) from the corpse\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex CoinRegex();

    // EQ logs use either full words ("2 platinum") or compact Form ("2p 3g 4s 5c").
    [GeneratedRegex(@"(?<n>\d+)\s*(?<unit>platinum|platinums|gold|golds|silver|silvers|copper|coppers|[pgsc])\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CoinPartRegex();

    [GeneratedRegex(@"^(a|an|the)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArticlePrefixRegex();

    [GeneratedRegex(@"^(an?|the)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemArticleRegex();

    [GeneratedRegex(@"'s$", RegexOptions.CultureInvariant)]
    private static partial Regex PossessiveCorpseRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
