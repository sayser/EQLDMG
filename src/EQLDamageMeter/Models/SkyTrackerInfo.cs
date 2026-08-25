namespace EQLDamageMeter.Models;

public enum SkyItemLocation
{
    Unknown,
    Inventory,
    Bank,
    Currency,
    Other
}

public sealed class SkyCatalogDocument
{
    public DateTime FetchedAtUtc { get; set; }
    public List<SkyClassCatalog> Classes { get; set; } = [];
}

public sealed class SkyClassCatalog
{
    public string ClassName { get; set; } = string.Empty;
    public string QuestGiver { get; set; } = string.Empty;
    public List<SkyRewardCatalog> Rewards { get; set; } = [];
}

public sealed class SkyRewardCatalog
{
    public string RewardName { get; set; } = string.Empty;
    public string QuestName { get; set; } = string.Empty;
    public string TriggerPhrase { get; set; } = string.Empty;
    public List<SkyRequiredItemCatalog> RequiredItems { get; set; } = [];

    public override string ToString() => RewardName;
}

public sealed class SkyRequiredItemCatalog
{
    public string ItemName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public int NeededCount { get; set; } = 1;
}

public sealed class SkyTrackerDocument
{
    public List<SkyTrackedGoal> Goals { get; set; } = [];
    public List<SkyLootWatch> LootWatches { get; set; } = [];
}

public sealed class SkyLootWatch
{
    public string ClassName { get; set; } = string.Empty;
    public string RewardName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
}

public sealed class SkyTrackedGoal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ClassName { get; set; } = string.Empty;
    public string RewardName { get; set; } = string.Empty;
    public string QuestName { get; set; } = string.Empty;
    public string TriggerPhrase { get; set; } = string.Empty;
    public string QuestGiver { get; set; } = string.Empty;
    public string RewardStats { get; set; } = string.Empty;
    public List<SkyTrackedPart> Parts { get; set; } = [];
}

public sealed class SkyTrackedPart
{
    public string ItemName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public int NeededCount { get; set; } = 1;
    public int FoundCount { get; set; }
    public SkyItemLocation Location { get; set; } = SkyItemLocation.Unknown;
    public bool AlertEnabled { get; set; } = true;
    public BuffAlertMode AlertMode { get; set; } = BuffAlertMode.Sound;
    public BuffSoundKind Sound { get; set; } = BuffSoundKind.Chime;
    public string VoiceText { get; set; } = string.Empty;
    public string LastDropText { get; set; } = string.Empty;
}
