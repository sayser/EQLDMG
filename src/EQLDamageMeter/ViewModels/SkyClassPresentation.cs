using EQLDamageMeter.Models;

namespace EQLDamageMeter.ViewModels;

public static class SkyClassPresentation
{
    public static string Glyph(string className) => className.Trim().ToLowerInvariant() switch
    {
        "magician" => "✦",
        "bard" => "♪",
        "druid" => "❀",
        "ranger" => "➶",
        "wizard" => "✶",
        "beastlord" => "◈",
        "berserker" => "⚒",
        "rogue" => "†",
        "shadow knight" => "☠",
        "cleric" => "✚",
        "monk" => "◉",
        "paladin" => "☩",
        "necromancer" => "☽",
        "shaman" => "☯",
        "enchanter" => "✧",
        "warrior" => "⚔",
        _ => "◆"
    };

    public static string ShortQuestTitle(string questName, string className)
    {
        if (string.IsNullOrWhiteSpace(questName)) return "Quest";
        var trimmed = questName.Trim();
        if (!string.IsNullOrWhiteSpace(className) &&
            trimmed.StartsWith(className, StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[className.Length..].TrimStart(' ', '-', '·');
            if (rest.Length > 0) return rest;
        }

        return trimmed;
    }

    public static (bool IsUnlocked, bool HasReadyTurnIn) QuestFlags(IReadOnlyList<string> statuses)
    {
        var unlocked = statuses.Count > 0 &&
                       statuses.All(status =>
                           status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase));
        var ready = !unlocked &&
                    statuses.Any(status =>
                        status.Equals("READY", StringComparison.OrdinalIgnoreCase));
        return (unlocked, ready);
    }
}

public sealed class SkyClassRowViewModel : ObservableObject
{
    private bool _hasReadyTurnIn;
    private bool _isUnlocked;

    public required string ClassName { get; init; }
    public required string QuestGiver { get; init; }
    public required string Glyph { get; init; }

    public bool HasReadyTurnIn
    {
        get => _hasReadyTurnIn;
        set => SetProperty(ref _hasReadyTurnIn, value);
    }

    public bool IsUnlocked
    {
        get => _isUnlocked;
        set => SetProperty(ref _isUnlocked, value);
    }
}

public sealed class SkyQuestRowViewModel : ObservableObject
{
    private string _statusLabel = string.Empty;

    public required SkyRewardCatalog Reward { get; init; }
    public required string Title { get; init; }

    public string StatusLabel
    {
        get => _statusLabel;
        set
        {
            if (!SetProperty(ref _statusLabel, value)) return;
            RaisePropertyChanged(nameof(HasStatus));
            RaisePropertyChanged(nameof(IsReady));
            RaisePropertyChanged(nameof(IsCompleted));
            RaisePropertyChanged(nameof(IsInProgress));
        }
    }

    public bool HasStatus => StatusLabel.Length > 0;
    public bool IsReady => StatusLabel.Equals("READY", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => StatusLabel.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress => StatusLabel.Equals("IN PROGRESS", StringComparison.OrdinalIgnoreCase);
}
