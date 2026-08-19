using System.Windows.Media;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.ViewModels;

public sealed class BuffOverlayEntryViewModel : ObservableObject
{
    private string _remainingText = string.Empty;
    private bool _isExpiringSoon;
    private bool _isOverdue;
    private bool _showsPlayingLabel;
    private int _stackCount = 1;

    public BuffOverlayEntryViewModel(BuffInstanceSnapshot snapshot,
        SpellTrackerCategory category = SpellTrackerCategory.Buff,
        ControlEffectType controlType = ControlEffectType.Other,
        ImageSource? icon = null,
        int stackCount = 1)
    {
        RuleId = snapshot.RuleId;
        RuntimeInstanceKey = snapshot.InstanceKey;
        SpellName = snapshot.SpellName;
        TargetName = snapshot.TargetName;
        IsSelf = snapshot.IsSelf;
        Category = category;
        ControlType = controlType;
        Icon = icon;
        _showsPlayingLabel = snapshot.ShowsPlayingLabel;
        Update(snapshot, stackCount);
    }

    public Guid RuleId { get; }
    public string RuntimeInstanceKey { get; }
    public string SpellName { get; }
    public string TargetName { get; }
    public bool IsSelf { get; }
    public SpellTrackerCategory Category { get; }
    public ControlEffectType ControlType { get; }
    public ImageSource? Icon { get; }
    public bool ShowsPlayingLabel => _showsPlayingLabel;
    public string EffectTypeLabel => Category switch
    {
        SpellTrackerCategory.DamageOverTime => "DoT",
        SpellTrackerCategory.Control => ControlType.ToString().ToUpperInvariant(),
        SpellTrackerCategory.Hostile => "HOSTILE",
        _ => IsSelf ? "SELF" : "OTHER"
    };
    public string InstanceKey => $"{RuleId:N}|{RuntimeInstanceKey}";
    public string TargetLabel => _showsPlayingLabel
        ? string.Empty
        : IsSelf ? "SELF" : $"OTHER  ·  {TargetName}";
    public string CompactRightText => _showsPlayingLabel
        ? "Playing"
        : IsSelf ? "SELF" : TargetName;
    public int StackCount => _stackCount;
    public bool HasStackCount => _stackCount > 1;
    public string StackCountText => $"X{_stackCount}";
    public string OverlayTargetText => HasStackCount ? $"{TargetName}  {StackCountText}" : TargetName;
    public string StatusText => _showsPlayingLabel ? "Playing"
        : IsOverdue ? "Past expected duration"
        : IsExpiringSoon ? "Expiring soon" : "Active";
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
    public bool IsExpiringSoon
    {
        get => _isExpiringSoon;
        private set
        {
            if (SetProperty(ref _isExpiringSoon, value)) RaisePropertyChanged(nameof(StatusText));
        }
    }
    public bool IsOverdue
    {
        get => _isOverdue;
        private set
        {
            if (SetProperty(ref _isOverdue, value)) RaisePropertyChanged(nameof(StatusText));
        }
    }

    public void Update(BuffInstanceSnapshot snapshot, int stackCount = 1)
    {
        _showsPlayingLabel = snapshot.ShowsPlayingLabel;
        RaisePropertyChanged(nameof(ShowsPlayingLabel));
        RaisePropertyChanged(nameof(TargetLabel));
        RaisePropertyChanged(nameof(CompactRightText));
        RemainingText = _showsPlayingLabel
            ? string.Empty
            : snapshot.IsOverdue ? $"+{FormatDuration(snapshot.Remaining)}" : FormatDuration(snapshot.Remaining);
        IsExpiringSoon = snapshot.IsExpiringSoon;
        IsOverdue = snapshot.IsOverdue;
        RaisePropertyChanged(nameof(StatusText));
        var count = Math.Max(1, stackCount);
        if (!SetProperty(ref _stackCount, count, nameof(StackCount))) return;
        RaisePropertyChanged(nameof(HasStackCount));
        RaisePropertyChanged(nameof(StackCountText));
        RaisePropertyChanged(nameof(OverlayTargetText));
    }

    public static string CreateKey(BuffInstanceSnapshot snapshot) =>
        $"{snapshot.RuleId:N}|{snapshot.InstanceKey}";

    private static string FormatDuration(TimeSpan value)
    {
        var totalSeconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
        var hours = totalSeconds / 3_600;
        var minutes = (totalSeconds % 3_600) / 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }
}
