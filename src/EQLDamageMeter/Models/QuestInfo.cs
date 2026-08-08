namespace EQLDamageMeter.Models;

public sealed class QuestCatalogDocument
{
    public DateTime FetchedAtUtc { get; set; }
    public List<string> Titles { get; set; } = [];
}

public sealed class QuestDetails
{
    public string Title { get; set; } = string.Empty;
    public string WikiUrl { get; set; } = string.Empty;
    public string StartZone { get; set; } = string.Empty;
    public string QuestGiver { get; set; } = string.Empty;
    public string RecommendedLevel { get; set; } = string.Empty;
    public string Classes { get; set; } = string.Empty;
    public string RelatedZones { get; set; } = string.Empty;
    public string RelatedNpcs { get; set; } = string.Empty;
    public IReadOnlyList<string> ChecklistLines { get; set; } = [];
    public IReadOnlyList<string> SuggestedItems { get; set; } = [];
}

public sealed class QuestTrackerDocument
{
    public List<TrackedQuestItem> TrackedItems { get; set; } = [];
}

public sealed class TrackedQuestItem
{
    public string ItemName { get; set; } = string.Empty;
    public string QuestTitle { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public BuffAlertMode AlertMode { get; set; } = BuffAlertMode.Sound;
    public BuffSoundKind Sound { get; set; } = BuffSoundKind.Chime;
    public string VoiceText { get; set; } = string.Empty;
}
