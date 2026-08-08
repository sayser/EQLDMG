using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.ViewModels;

public sealed class BuffRuleViewModel : ObservableObject
{
    private string _spellName;
    private string _durationText;
    private string _castTimeText;
    private bool _isEnabled;
    private bool _showInOverlay;
    private bool _trackSelf;
    private bool _trackOthers;
    private SpellTrackerCategory _category;
    private ControlEffectType _controlType;
    private BuffAlertMode _alertMode;
    private BuffSoundKind _sound;
    private string _voiceText;
    private string _remainingText = "Waiting";
    private string _statusText = "Not detected yet";
    private bool _isActive;
    private bool _isExpiringSoon;
    private bool _isOverdue;
    private string _spellValidationText = string.Empty;
    private ImageSource? _icon;

    public BuffRuleViewModel(BuffRuleSettings settings)
    {
        Id = settings.Id;
        _spellName = settings.SpellName;
        _durationText = FormatDuration(TimeSpan.FromSeconds(settings.DurationSeconds));
        _castTimeText = settings.CastTimeSeconds.ToString("0.0#", CultureInfo.InvariantCulture);
        _isEnabled = settings.IsEnabled;
        _showInOverlay = settings.ShowInOverlay;
        _trackSelf = settings.TrackSelf;
        _trackOthers = settings.TrackOthers;
        _category = settings.Category;
        _controlType = settings.ControlType;
        _alertMode = BuffAlertModeOptions.Normalize(settings.AlertMode);
        _sound = settings.Sound;
        _voiceText = settings.VoiceText;
    }

    public Guid Id { get; }
    public string SpellName
    {
        get => _spellName;
        set
        {
            if (!SetProperty(ref _spellName, value)) return;
            SpellValidationText = string.Empty;
            Icon = null;
        }
    }
    public ImageSource? Icon { get => _icon; private set => SetProperty(ref _icon, value); }

    public void SetIcon(ImageSource? icon) => Icon = icon;
    public string DurationText { get => _durationText; set => SetProperty(ref _durationText, value); }
    public string CastTimeText { get => _castTimeText; set => SetProperty(ref _castTimeText, value); }
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            RaisePropertyChanged(nameof(OverlayVisibility));
        }
    }
    public bool ShowInOverlay
    {
        get => _showInOverlay;
        set
        {
            if (!SetProperty(ref _showInOverlay, value)) return;
            RaisePropertyChanged(nameof(OverlayVisibility));
        }
    }
    public bool TrackSelf
    {
        get => _trackSelf;
        set
        {
            if (SetProperty(ref _trackSelf, value)) RaisePropertyChanged(nameof(TargetSummary));
        }
    }
    public bool TrackOthers
    {
        get => _trackOthers;
        set
        {
            if (SetProperty(ref _trackOthers, value)) RaisePropertyChanged(nameof(TargetSummary));
        }
    }
    public SpellTrackerCategory Category
    {
        get => _category;
        set
        {
            if (SetProperty(ref _category, value)) RaisePropertyChanged(nameof(TargetSummary));
        }
    }
    public ControlEffectType ControlType
    {
        get => _controlType;
        set
        {
            if (SetProperty(ref _controlType, value)) RaisePropertyChanged(nameof(TargetSummary));
        }
    }
    public BuffAlertMode AlertMode
    {
        get => _alertMode;
        set
        {
            var normalized = BuffAlertModeOptions.Normalize(value);
            if (!SetProperty(ref _alertMode, normalized)) return;
            RaisePropertyChanged(nameof(AlertSummary));
            RaisePropertyChanged(nameof(SoundPickerVisibility));
            RaisePropertyChanged(nameof(VoiceTextVisibility));
        }
    }
    public BuffSoundKind Sound
    {
        get => _sound;
        set
        {
            if (SetProperty(ref _sound, value)) RaisePropertyChanged(nameof(AlertSummary));
        }
    }
    public string VoiceText
    {
        get => _voiceText;
        set
        {
            if (!SetProperty(ref _voiceText, value)) return;
            RaisePropertyChanged(nameof(AlertSummary));
        }
    }
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public bool IsExpiringSoon { get => _isExpiringSoon; private set => SetProperty(ref _isExpiringSoon, value); }
    public bool IsOverdue { get => _isOverdue; private set => SetProperty(ref _isOverdue, value); }
    public string SpellValidationText
    {
        get => _spellValidationText;
        private set => SetProperty(ref _spellValidationText, value);
    }
    public Visibility OverlayVisibility => IsEnabled && ShowInOverlay ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SoundPickerVisibility =>
        AlertMode == BuffAlertMode.Sound ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VoiceTextVisibility =>
        AlertMode == BuffAlertMode.TextToSpeech ? Visibility.Visible : Visibility.Collapsed;

    public string AlertSummary => AlertMode == BuffAlertMode.TextToSpeech
        ? (string.IsNullOrWhiteSpace(VoiceText) ? "Voice" : "Voice (custom)")
        : Sound.ToString();
    public string TargetSummary => Category switch
    {
        SpellTrackerCategory.DamageOverTime => "Enemy targets",
        SpellTrackerCategory.Control => $"{ControlType} · Enemy targets",
        _ => (TrackSelf, TrackOthers) switch
        {
            (true, true) => "Self + Others",
            (true, false) => "Self",
            (false, true) => "Others",
            _ => "No targets"
        }
    };

    public void SetSpellValidation(string? error) => SpellValidationText = error ?? string.Empty;

    public bool TryCreateSettings(out BuffRuleSettings? settings, out string error)
    {
        settings = null;
        if (string.IsNullOrWhiteSpace(SpellName))
        {
            error = "Enter a spell name.";
            return false;
        }
        if (!TryParseDuration(DurationText, out var duration) || duration <= TimeSpan.Zero)
        {
            error = "Duration must use minutes:seconds or minutes.seconds, such as 9:06 or 9.06.";
            return false;
        }
        if (!TrackSelf && !TrackOthers)
        {
            error = "Enable Self, Others, or both under Track Targets.";
            return false;
        }
        var normalizedCastTime = CastTimeText.Trim().Replace(':', '.');
        if (!double.TryParse(normalizedCastTime, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var castTime) || castTime < 0 || castTime > 120)
        {
            error = "Cast time must be from 0 to 120 seconds; use either 3.4 or 3:4.";
            return false;
        }

        var voice = string.IsNullOrWhiteSpace(VoiceText)
            ? $"{SpellName.Trim()} has expired"
            : VoiceText.Trim();
        settings = new BuffRuleSettings(Id, SpellName.Trim(), checked((int)duration.TotalSeconds), castTime,
            IsEnabled, ShowInOverlay, BuffAlertModeOptions.Normalize(AlertMode), Sound, voice, TrackSelf,
            TrackOthers, Category, ControlType);
        error = string.Empty;
        return true;
    }

    public void ApplyRuntime(BuffRuntimeSnapshot snapshot)
    {
        IsActive = snapshot.IsActive;
        IsExpiringSoon = snapshot.IsExpiringSoon;
        IsOverdue = snapshot.IsOverdue;
        if (!IsEnabled)
        {
            RemainingText = "Disabled";
            StatusText = "Disabled";
        }
        else if (snapshot.IsCasting)
        {
            RemainingText = "Casting";
            StatusText = "Casting";
        }
        else if (snapshot.IsOverdue)
        {
            RemainingText = "Past due";
            StatusText = "Awaiting break";
        }
        else if (snapshot.IsActive)
        {
            RemainingText = FormatDuration(snapshot.Remaining);
            StatusText = snapshot.IsExpiringSoon ? "Expiring soon" : "Active";
        }
        else if (snapshot.IsExpired)
        {
            RemainingText = "0:00";
            StatusText = "Expired";
        }
        else if (snapshot.StopReason == BuffStopReason.Dispelled)
        {
            RemainingText = "Stopped";
            StatusText = "Dispelled";
        }
        else if (snapshot.StopReason == BuffStopReason.Death)
        {
            RemainingText = "Stopped";
            StatusText = "Cleared on death";
        }
        else
        {
            RemainingText = "Waiting";
            StatusText = "Not detected yet";
        }
        RaisePropertyChanged(nameof(AlertSummary));
    }

    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var parts = text.Trim().Split([':', '.']);
        if (parts.Length == 2 && int.TryParse(parts[0], out var minutes) &&
            int.TryParse(parts[1], out var seconds) && minutes >= 0 && seconds is >= 0 and < 60)
        {
            duration = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }
        if (parts.Length == 3 && int.TryParse(parts[0], out var hours) &&
            int.TryParse(parts[1], out minutes) && int.TryParse(parts[2], out seconds) &&
            hours >= 0 && minutes is >= 0 and < 60 && seconds is >= 0 and < 60)
        {
            duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }
        return false;
    }

    private static string FormatDuration(TimeSpan value)
    {
        var totalSeconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
        var hours = totalSeconds / 3_600;
        var minutes = (totalSeconds % 3_600) / 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }
}
