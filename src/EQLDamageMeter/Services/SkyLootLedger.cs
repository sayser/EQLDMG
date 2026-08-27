using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class SkyItemBalance
{
    public int InventoryCount { get; set; }
    public int CurrencyCount { get; set; }
    public int HoardCount { get; set; }
    public int BankCount { get; set; }
    public int OtherCount { get; set; }
    public int DestroyedCount { get; set; }
    public int SoldCount { get; set; }
    public int TurnedInCount { get; set; }
    public SkyItemLocation LastLocation { get; set; } = SkyItemLocation.Unknown;

    public int Owned => InventoryCount + CurrencyCount + HoardCount + BankCount + OtherCount;
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
            if (LastLocation == SkyItemLocation.Hoard && HoardCount > 0)
                return SkyItemLocation.Hoard;
            if (LastLocation == SkyItemLocation.Bank && BankCount > 0)
                return SkyItemLocation.Bank;
            if (LastLocation == SkyItemLocation.Other && OtherCount > 0)
                return SkyItemLocation.Other;
            return PrimaryLocation();
        }
    }

    public SkyItemLocation PrimaryLocation()
    {
        var max = Math.Max(Math.Max(HoardCount, BankCount),
            Math.Max(Math.Max(InventoryCount, OtherCount), CurrencyCount));
        if (max <= 0) return SkyItemLocation.Unknown;
        if (HoardCount == max) return SkyItemLocation.Hoard;
        if (BankCount == max) return SkyItemLocation.Bank;
        if (InventoryCount == max) return SkyItemLocation.Inventory;
        if (OtherCount == max) return SkyItemLocation.Other;
        if (CurrencyCount == max) return SkyItemLocation.Currency;
        return SkyItemLocation.Unknown;
    }

    public SkyItemBalance Clone() => new()
    {
        InventoryCount = InventoryCount,
        CurrencyCount = CurrencyCount,
        HoardCount = HoardCount,
        BankCount = BankCount,
        OtherCount = OtherCount,
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
    private readonly List<(string ClassName, string RewardName)> _newlyCompleted = [];

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
        _newlyCompleted.Clear();
    }

    public bool Observe(string message)
    {
        if (SessionLootParser.TryReadLootEvent(message, out var lootName, out var disposition, out var lootCount))
            return ApplyLoot(lootName, disposition, lootCount);

        if (SkyLogEvents.TryReadInventoryMerge(message, out var mergedItem))
            return ApplyInventoryMerge(mergedItem);

        if (SkyLogEvents.TryReadDestroyed(message, out var destroyed, out var destroyedCount))
            return ApplyDestroyed(destroyed, destroyedCount);

        if (SkyLogEvents.TryReadReceived(message, out var receivedName, out var receivedCount))
            return ApplyLoot(receivedName, "Kept", receivedCount);

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

    /// <summary>
    /// Overwrite bags, bank, and Hoard from an /out inventory dump.
    /// Currency-tab Wind Runes and Motes are not in that dump, so log CurrencyCount is kept.
    /// </summary>
    public (int SkyItemsFound, int Copies) ApplyInventorySnapshot(
        IReadOnlyDictionary<string, SkyInventoryPiles> dump)
    {
        var found = 0;
        var copies = 0;
        foreach (var key in _tracked)
        {
            dump.TryGetValue(key, out var piles);
            var inventory = piles?.Inventory ?? 0;
            var bank = piles?.Bank ?? 0;
            var hoard = piles?.Hoard ?? 0;
            var other = piles?.Other ?? 0;
            var dumpTotal = inventory + bank + hoard + other;
            if (dumpTotal > 0)
            {
                found++;
                copies += dumpTotal;
            }

            var balance = GetOrCreate(key);
            balance.InventoryCount = inventory;
            balance.BankCount = bank;
            balance.HoardCount = hoard;
            balance.OtherCount = other;
            if (!SkyItemName.IsCurrencyItem(key))
                balance.CurrencyCount = 0;
            balance.LastLocation = balance.PrimaryLocation();
        }

        return (found, copies);
    }

    public IReadOnlyList<(string ClassName, string RewardName)> TakeNewlyCompleted()
    {
        if (_newlyCompleted.Count == 0)
            return [];
        var completed = _newlyCompleted.ToArray();
        _newlyCompleted.Clear();
        return completed;
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
                return ApplyLootMerge(balance);
            default:
                return false;
        }
    }

    private static bool ApplyLootMerge(SkyItemBalance balance)
    {
        // Corpse loot "to create Item +N" upgrades a copy already in bags. Do not add
        // a second owned pile. If we never saw the first copy, this line proves it exists.
        if (balance.Owned <= 0)
        {
            balance.InventoryCount = 1;
            balance.LastLocation = SkyItemLocation.Inventory;
            return true;
        }

        if (balance.InventoryCount <= 0 && balance.HoardCount > 0)
        {
            balance.LastLocation = SkyItemLocation.Inventory;
            return true;
        }

        balance.LastLocation = SkyItemLocation.Inventory;
        return true;
    }

    private bool ApplyInventoryMerge(string itemName)
    {
        if (!IsTracked(itemName)) return false;
        var balance = GetOrCreate(itemName);
        if (balance.InventoryCount >= 2)
            balance.InventoryCount--;
        else if (balance.InventoryCount <= 0)
            balance.InventoryCount = 1;
        balance.LastLocation = SkyItemLocation.Inventory;
        return true;
    }

    private bool ApplyDestroyed(string itemName, int count)
    {
        if (!IsTracked(itemName)) return false;
        count = Math.Max(1, count);
        var balance = GetOrCreate(itemName);
        DrainOwned(balance, count);
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
            {
                _completed.Add(CompletionKey(match.Value.ClassName, match.Value.Reward.RewardName));
                _newlyCompleted.Add((match.Value.ClassName, match.Value.Reward.RewardName));
            }
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

    private void Consume(string itemName, int count) =>
        DrainOwned(GetOrCreate(itemName), count);

    private static void DrainOwned(SkyItemBalance balance, int count)
    {
        var remaining = Math.Max(1, count);
        var fromInventory = Math.Min(balance.InventoryCount, remaining);
        balance.InventoryCount -= fromInventory;
        remaining -= fromInventory;
        var fromCurrency = Math.Min(balance.CurrencyCount, remaining);
        balance.CurrencyCount -= fromCurrency;
        remaining -= fromCurrency;
        var fromBank = Math.Min(balance.BankCount, remaining);
        balance.BankCount -= fromBank;
        remaining -= fromBank;
        var fromHoard = Math.Min(balance.HoardCount, remaining);
        balance.HoardCount -= fromHoard;
        remaining -= fromHoard;
        var fromOther = Math.Min(balance.OtherCount, remaining);
        balance.OtherCount -= fromOther;
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
