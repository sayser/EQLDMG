using System.Windows.Media;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.ViewModels;

public sealed class BuffOverlayEntryViewModel : ObservableObject
{
    private string _remainingText = string.Empty;
    private bool _isExpiringSoon;
    private bool _isOverdue;

    public BuffOverlayEntryViewModel(BuffInstanceSnapshot snapshot,
        SpellTrackerCategory category = SpellTrackerCategory.Buff,
        ControlEffectType controlType = ControlEffectType.Other,
        ImageSource? icon = null)
    {
        RuleId = snapshot.RuleId;
        RuntimeInstanceKey = snapshot.InstanceKey;
        SpellName = snapshot.SpellName;
        TargetName = snapshot.TargetName;
        IsSelf = snapshot.IsSelf;
        Category = category;
        ControlType = controlType;
        Icon = icon;
        Update(snapshot);
    }

    public Guid RuleId { get; }
    public string RuntimeInstanceKey { get; }
    public string SpellName { get; }
    public string TargetName { get; }
    public bool IsSelf { get; }
    public SpellTrackerCategory Category { get; }
    public ControlEffectType ControlType { get; }
    public ImageSource? Icon { get; }
    public string EffectTypeLabel => Category switch
    {
        SpellTrackerCategory.DamageOverTime => "DoT",
        SpellTrackerCategory.Control => ControlType.ToString().ToUpperInvariant(),
        SpellTrackerCategory.Hostile => "HOSTILE",
        _ => IsSelf ? "SELF" : "OTHER"
    };
    public string InstanceKey => $"{RuleId:N}|{RuntimeInstanceKey}";
    public string TargetLabel => IsSelf ? "SELF" : $"OTHER  ·  {TargetName}";
    public string StatusText => IsOverdue ? "Past expected duration" : IsExpiringSoon ? "Expiring soon" : "Active";
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

    public void Update(BuffInstanceSnapshot snapshot)
    {
        RemainingText = snapshot.IsOverdue ? $"+{FormatDuration(snapshot.Remaining)}" : FormatDuration(snapshot.Remaining);
        IsExpiringSoon = snapshot.IsExpiringSoon;
        IsOverdue = snapshot.IsOverdue;
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
