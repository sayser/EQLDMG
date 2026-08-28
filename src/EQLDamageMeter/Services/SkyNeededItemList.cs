using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class SkyNeededItem
{
    public required string ItemName { get; init; }
    public required int Remaining { get; init; }
    public required IReadOnlyList<string> Classes { get; init; }
    public string ClassesText => string.Join(", ", Classes);
    public string RemainingText => Remaining <= 1 ? "need 1" : $"need {Remaining}";
}

public sealed class SkyNeededBossGroup
{
    public required string BossName { get; init; }
    public required IReadOnlyList<SkyNeededItem> Items { get; init; }
    public bool HasBoss => BossName.Length > 0;
}

public sealed class SkyNeededIslandGroup
{
    public required int IslandOrder { get; init; }
    public required string IslandName { get; init; }
    public required IReadOnlyList<SkyNeededBossGroup> Bosses { get; init; }
    public int ItemCount => Bosses.Sum(group => group.Items.Count);
}

public static class SkyNeededItemList
{
    public static IReadOnlyList<SkyNeededIslandGroup> Build(
        IReadOnlyList<SkyClassCatalog> classes,
        SkyLootLedger ledger)
    {
        var bag = new Dictionary<string, NeededDraft>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in classes)
        {
            foreach (var reward in cls.Rewards)
            {
                if (ledger.QuestStatus(cls.ClassName, reward)
                    .Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var item in reward.RequiredItems)
                {
                    var needed = item.NeededCount < 1 ? 1 : item.NeededCount;
                    var remaining = Math.Max(0, needed - ledger.Snapshot(item.ItemName).Owned);
                    if (remaining <= 0) continue;

                    var name = SkyItemName.Normalize(item.ItemName);
                    if (name.Length == 0) continue;
                    var place = SkyDropSource.Parse(item.Note, item.ItemName);
                    var key = $"{place.IslandOrder}|{place.IslandName}|{place.BossName}|{name}";
                    if (!bag.TryGetValue(key, out var draft))
                    {
                        draft = new NeededDraft(name, place);
                        bag[key] = draft;
                    }

                    draft.Remaining = Math.Max(draft.Remaining, remaining);
                    draft.Classes.Add(cls.ClassName);
                }
            }
        }

        return bag.Values
            .GroupBy(entry => (entry.Place.IslandOrder, entry.Place.IslandName))
            .OrderBy(group => group.Key.IslandOrder)
            .ThenBy(group => group.Key.IslandName, StringComparer.OrdinalIgnoreCase)
            .Select(island => new SkyNeededIslandGroup
            {
                IslandOrder = island.Key.IslandOrder,
                IslandName = island.Key.IslandName,
                Bosses = island
                    .GroupBy(entry => entry.Place.BossName)
                    .OrderBy(group => group.Key.Length == 0 ? 0 : 1)
                    .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(boss => new SkyNeededBossGroup
                    {
                        BossName = boss.Key,
                        Items = boss
                            .OrderBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase)
                            .Select(entry => new SkyNeededItem
                            {
                                ItemName = entry.ItemName,
                                Remaining = entry.Remaining,
                                Classes = entry.Classes
                                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                    .ToArray()
                            })
                            .ToArray()
                    })
                    .ToArray()
            })
            .ToArray();
    }

    private sealed class NeededDraft(string itemName, SkyDropPlace place)
    {
        public string ItemName { get; } = itemName;
        public SkyDropPlace Place { get; } = place;
        public int Remaining { get; set; }
        public HashSet<string> Classes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
