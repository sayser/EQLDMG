using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class SkyItemBalance
{
    public int InventoryCount { get; set; }
    public int CurrencyCount { get; set; }
    public int DestroyedCount { get; set; }
    public int SoldCount { get; set; }
    public int TurnedInCount { get; set; }
    public SkyItemLocation LastLocation { get; set; } = SkyItemLocation.Unknown;

    public int Owned => InventoryCount + CurrencyCount;
    public bool IsDeleted => Owned == 0 && DestroyedCount > 0;

    public SkyItemLocation Location
    {
        get
        {
            if (Owned <= 0) return SkyItemLocation.Unknown;
            if (LastLocation == SkyItemLocation.Inventory && InventoryCount > 0)
                return SkyItemLocation.Inventory;
            if (LastLocation == SkyItemLocation.Currency && CurrencyCount > 0)
                return SkyItemLocation.Currency;
            if (LastLocation == SkyItemLocation.Bank && InventoryCount + CurrencyCount > 0)
                return SkyItemLocation.Bank;
            if (CurrencyCount > 0) return SkyItemLocation.Currency;
            if (InventoryCount > 0) return SkyItemLocation.Inventory;
            return SkyItemLocation.Unknown;
        }
    }

    public SkyItemBalance Clone() => new()
    {
        InventoryCount = InventoryCount,
        CurrencyCount = CurrencyCount,
        DestroyedCount = DestroyedCount,
        SoldCount = SoldCount,
        TurnedInCount = TurnedInCount,
        LastLocation = LastLocation
    };
}

public sealed class SkyLootLedger
{
    private readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string ClassName, SkyRewardCatalog Reward)>> _questsByGiver =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkyItemBalance> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Npc, string Item, int Count)> _pendingOffers = [];

    public void LoadCatalog(IReadOnlyList<SkyClassCatalog> classes)
    {
        _tracked.Clear();
        _questsByGiver.Clear();
        foreach (var cls in classes)
        {
            var giver = cls.QuestGiver?.Trim() ?? string.Empty;
            foreach (var reward in cls.Rewards)
            {
                Track(reward.RewardName);
                foreach (var item in reward.RequiredItems)
                    Track(item.ItemName);

                if (giver.Length == 0) continue;
                if (!_questsByGiver.TryGetValue(giver, out var list))
                {
                    list = [];
                    _questsByGiver[giver] = list;
                }

                list.Add((cls.ClassName, reward));
            }
        }
    }

    public void CopyFrom(SkyLootLedger other)
    {
        _tracked.Clear();
        foreach (var name in other._tracked)
            _tracked.Add(name);

        _questsByGiver.Clear();
        foreach (var pair in other._questsByGiver)
            _questsByGiver[pair.Key] = [.. pair.Value];

        _items.Clear();
        foreach (var pair in other._items)
            _items[pair.Key] = pair.Value.Clone();

        _completed.Clear();
        foreach (var key in other._completed)
            _completed.Add(key);

        _pendingOffers.Clear();
        _pendingOffers.AddRange(other._pendingOffers);
    }

    public bool Observe(string message)
    {
        if (SessionLootParser.TryReadLootEvent(message, out var lootName, out var disposition, out var lootCount))
            return ApplyLoot(lootName, disposition, lootCount);

        if (SkyLogEvents.TryReadDestroyed(message, out var destroyed, out var destroyedCount))
            return ApplyDestroyed(destroyed, destroyedCount);

        if (SkyLogEvents.TryReadOffered(message, out var offered, out var offeredCount, out var npc))
        {
            if (!IsTracked(offered)) return false;
            _pendingOffers.Add((npc, SkyItemName.Normalize(offered), Math.Max(1, offeredCount)));
            return false;
        }

        if (SkyLogEvents.IsTradeCancelled(message))
        {
            if (_pendingOffers.Count == 0) return false;
            _pendingOffers.Clear();
            return false;
        }

        if (SkyLogEvents.TryReadTradeComplete(message, out var trader))
            return ApplyTrade(trader);

        return false;
    }

    public SkyItemBalance Snapshot(string itemName)
    {
        var key = SkyItemName.Normalize(itemName);
        return key.Length > 0 && _items.TryGetValue(key, out var balance)
            ? balance.Clone()
            : new SkyItemBalance();
    }

    public string QuestStatus(string? className, SkyRewardCatalog reward)
    {
        if (reward is null) return string.Empty;
        if (IsCompleted(className, reward.RewardName) || Snapshot(reward.RewardName).Owned > 0)
            return "COMPLETED";

        var required = reward.RequiredItems;
        if (required.Count == 0) return string.Empty;

        var allReady = true;
        var anyOwned = false;
        var anyDeleted = false;
        foreach (var item in required)
        {
            var balance = Snapshot(item.ItemName);
            if (balance.Owned < item.NeededCount) allReady = false;
            if (balance.Owned > 0) anyOwned = true;
            if (balance.IsDeleted) anyDeleted = true;
        }

        if (allReady) return "READY";
        return anyOwned || anyDeleted ? "IN PROGRESS" : string.Empty;
    }

    private bool ApplyLoot(string itemName, string disposition, int count)
    {
        if (!IsTracked(itemName)) return false;
        count = Math.Max(1, count);
        var balance = GetOrCreate(itemName);
        switch (disposition)
        {
            case "Kept":
                if (SkyItemName.IsCurrencyItem(itemName))
                {
                    balance.CurrencyCount += count;
                    balance.LastLocation = SkyItemLocation.Currency;
                }
                else
                {
                    balance.InventoryCount += count;
                    balance.LastLocation = SkyItemLocation.Inventory;
                }
                return true;
            case "Stored":
                balance.CurrencyCount += count;
                balance.LastLocation = SkyItemLocation.Currency;
                return true;
            case "Sold":
                balance.SoldCount += count;
                return true;
            case "Merged":
                return false;
            default:
                return false;
        }
    }

    private bool ApplyDestroyed(string itemName, int count)
    {
        if (!IsTracked(itemName)) return false;
        count = Math.Max(1, count);
        var balance = GetOrCreate(itemName);
        var remaining = count;
        var fromInventory = Math.Min(balance.InventoryCount, remaining);
        balance.InventoryCount -= fromInventory;
        remaining -= fromInventory;
        var fromCurrency = Math.Min(balance.CurrencyCount, remaining);
        balance.CurrencyCount -= fromCurrency;
        remaining -= fromCurrency;
        balance.DestroyedCount += count;
        return true;
    }

    private bool ApplyTrade(string npc)
    {
        var offered = _pendingOffers
            .Where(entry => entry.Npc.Equals(npc, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _pendingOffers.RemoveAll(entry => entry.Npc.Equals(npc, StringComparison.OrdinalIgnoreCase));
        if (offered.Count == 0) return false;

        var changed = false;
        if (_questsByGiver.TryGetValue(npc, out var quests))
        {
            var match = FindBestQuestMatch(quests, offered);
            if (match is not null)
                _completed.Add(CompletionKey(match.Value.ClassName, match.Value.Reward.RewardName));
        }

        foreach (var entry in offered)
        {
            if (!IsTracked(entry.Item)) continue;
            Consume(entry.Item, entry.Count);
            GetOrCreate(entry.Item).TurnedInCount += entry.Count;
            changed = true;
        }

        return changed;
    }

    private static (string ClassName, SkyRewardCatalog Reward)? FindBestQuestMatch(
        List<(string ClassName, SkyRewardCatalog Reward)> quests,
        List<(string Npc, string Item, int Count)> offered)
    {
        (string ClassName, SkyRewardCatalog Reward)? best = null;
        var bestCount = -1;
        foreach (var quest in quests)
        {
            if (quest.Reward.RequiredItems.Count == 0) continue;
            if (!quest.Reward.RequiredItems.All(item =>
                    offered.Any(entry => SkyItemName.EqualsNormalized(entry.Item, item.ItemName) &&
                                         entry.Count >= item.NeededCount)))
                continue;

            var size = quest.Reward.RequiredItems.Count;
            if (size <= bestCount) continue;
            best = quest;
            bestCount = size;
        }

        return best;
    }

    private void Consume(string itemName, int count)
    {
        var balance = GetOrCreate(itemName);
        var remaining = Math.Max(1, count);
        var fromInventory = Math.Min(balance.InventoryCount, remaining);
        balance.InventoryCount -= fromInventory;
        remaining -= fromInventory;
        var fromCurrency = Math.Min(balance.CurrencyCount, remaining);
        balance.CurrencyCount -= fromCurrency;
    }

    private bool IsCompleted(string? className, string rewardName)
    {
        if (string.IsNullOrWhiteSpace(className))
            return _completed.Any(key => key.EndsWith("|" + SkyItemName.Normalize(rewardName),
                StringComparison.OrdinalIgnoreCase));
        return _completed.Contains(CompletionKey(className, rewardName));
    }

    private bool IsTracked(string itemName)
    {
        var key = SkyItemName.Normalize(itemName);
        return key.Length > 0 && _tracked.Contains(key);
    }

    private SkyItemBalance GetOrCreate(string itemName)
    {
        var key = SkyItemName.Normalize(itemName);
        if (_items.TryGetValue(key, out var balance)) return balance;
        balance = new SkyItemBalance();
        _items[key] = balance;
        return balance;
    }

    private void Track(string name)
    {
        var key = SkyItemName.Normalize(name);
        if (key.Length > 0) _tracked.Add(key);
    }

    private static string CompletionKey(string className, string rewardName) =>
        $"{className.Trim()}|{SkyItemName.Normalize(rewardName)}";
}
