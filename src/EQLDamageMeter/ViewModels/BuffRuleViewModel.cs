using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

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
    private BuffSoundKind _landSound;
    private BuffAlertMode _landAlertMode;
    private string _voiceText;
    private string _landVoiceText;
    private string _remainingText = "Waiting";
    private string _statusText = "Not detected yet";
    private bool _isActive;
    private bool _isExpiringSoon;
    private bool _isOverdue;
    private string _spellValidationText = string.Empty;
    private ImageSource? _icon;
    private SpellTimingSource _castSource;
    private SpellTimingSource _durationSource;
    private bool _suppressManualMark;

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
        _landSound = settings.LandSound ?? settings.Sound;
        _landAlertMode = BuffAlertModeOptions.Normalize(settings.LandAlertMode ?? BuffAlertMode.Sound);
        _voiceText = settings.VoiceText;
        _landVoiceText = settings.LandVoiceText ?? string.Empty;
        // Legacy "Learned" values are treated as manual edits going forward.
        _castSource = settings.CastSource == SpellTimingSource.Learned
            ? SpellTimingSource.Manual
            : settings.CastSource;
        _durationSource = settings.DurationSource == SpellTimingSource.Learned
            ? SpellTimingSource.Manual
            : settings.DurationSource;
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
    public string DurationText
    {
        get => _durationText;
        set
        {
            if (!SetProperty(ref _durationText, value)) return;
            if (!_suppressManualMark) DurationSource = SpellTimingSource.Manual;
            RaisePropertyChanged(nameof(DurationSourceHint));
        }
    }
    public string CastTimeText
    {
        get => _castTimeText;
        set
        {
            if (!SetProperty(ref _castTimeText, value)) return;
            if (!_suppressManualMark) CastSource = SpellTimingSource.Manual;
            RaisePropertyChanged(nameof(CastSourceHint));
        }
    }
    public SpellTimingSource CastSource
    {
        get => _castSource;
        private set
        {
            if (!SetProperty(ref _castSource, value)) return;
            RaisePropertyChanged(nameof(CastSourceHint));
        }
    }
    public SpellTimingSource DurationSource
    {
        get => _durationSource;
        private set
        {
            if (!SetProperty(ref _durationSource, value)) return;
            RaisePropertyChanged(nameof(DurationSourceHint));
        }
    }
    public string CastSourceHint => SourceHint(CastSource);
    public string DurationSourceHint => SourceHint(DurationSource);
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
    public bool ShowInOverlay
    {
        get => _showInOverlay;
        set => SetProperty(ref _showInOverlay, value);
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
            RaisePropertyChanged(nameof(ExpireSoundPickerVisibility));
            RaisePropertyChanged(nameof(ExpireVoiceTextVisibility));
        }
    }
    public BuffAlertMode LandAlertMode
    {
        get => _landAlertMode;
        set
        {
            var normalized = BuffAlertModeOptions.Normalize(value);
            if (!SetProperty(ref _landAlertMode, normalized)) return;
            RaisePropertyChanged(nameof(AlertSummary));
            RaisePropertyChanged(nameof(LandSoundPickerVisibility));
            RaisePropertyChanged(nameof(LandVoiceTextVisibility));
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
    public BuffSoundKind LandSound
    {
        get => _landSound;
        set
        {
            if (SetProperty(ref _landSound, value)) RaisePropertyChanged(nameof(AlertSummary));
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
    public string LandVoiceText
    {
        get => _landVoiceText;
        set
        {
            if (!SetProperty(ref _landVoiceText, value)) return;
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
    public Visibility SoundPickerVisibility =>
        AlertMode == BuffAlertMode.Sound ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VoiceTextVisibility =>
        AlertMode == BuffAlertMode.TextToSpeech ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LandSoundPickerVisibility =>
        LandAlertMode == BuffAlertMode.Sound ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LandVoiceTextVisibility =>
        LandAlertMode == BuffAlertMode.TextToSpeech ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpireSoundPickerVisibility => SoundPickerVisibility;
    public Visibility ExpireVoiceTextVisibility => VoiceTextVisibility;

    public string AlertSummary
    {
        get
        {
            if (Category != SpellTrackerCategory.Hostile)
            {
                return AlertMode == BuffAlertMode.TextToSpeech
                    ? (string.IsNullOrWhiteSpace(VoiceText) ? "Voice" : "Voice (custom)")
                    : Sound.ToString();
            }

            var land = LandAlertMode == BuffAlertMode.TextToSpeech ? "TTS" : LandSound.ToString();
            var expire = AlertMode == BuffAlertMode.TextToSpeech ? "TTS" : Sound.ToString();
            return $"{land} → {expire}";
        }
    }
    public string TargetSummary => Category switch
    {
        SpellTrackerCategory.DamageOverTime => "Enemy targets",
        SpellTrackerCategory.Control => $"{ControlType} · Enemy targets",
        SpellTrackerCategory.Hostile => "On me",
        _ => (TrackSelf, TrackOthers) switch
        {
            (true, true) => "Self + Others",
            (true, false) => "Self",
            (false, true) => "Others",
            _ => "No targets"
        }
    };

    public void SetSpellValidation(string? error) => SpellValidationText = error ?? string.Empty;

    public void ApplyCatalogTimings(SpellDataEntry spell, bool force = false,
        int casterLevel = SpellDataCatalog.DefaultCasterLevel)
    {
        if (force)
        {
            CastSource = SpellTimingSource.Catalog;
            DurationSource = SpellTimingSource.Catalog;
        }

        var fillCast = force || CastSource == SpellTimingSource.Catalog;
        var fillDuration = force || DurationSource == SpellTimingSource.Catalog;
        var durationSeconds = spell.DurationSecondsFor(casterLevel);

        _suppressManualMark = true;
        try
        {
            if (fillCast && spell.CastTimeSeconds >= 0)
            {
                CastTimeText = spell.CastTimeSeconds.ToString("0.0#", CultureInfo.InvariantCulture);
                CastSource = SpellTimingSource.Catalog;
            }
            if (fillDuration && durationSeconds > 0)
            {
                DurationText = FormatDuration(TimeSpan.FromSeconds(durationSeconds));
                DurationSource = SpellTimingSource.Catalog;
            }
        }
        finally
        {
            _suppressManualMark = false;
        }
    }

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
        var landVoice = string.IsNullOrWhiteSpace(LandVoiceText) ? null : LandVoiceText.Trim();
        settings = new BuffRuleSettings(Id, SpellName.Trim(), checked((int)duration.TotalSeconds), castTime,
            IsEnabled, ShowInOverlay, BuffAlertModeOptions.Normalize(AlertMode), Sound, voice, TrackSelf,
            TrackOthers, Category, ControlType, CastSource, DurationSource, 0, 0, 0, 0,
            Category == SpellTrackerCategory.Hostile ? LandSound : null,
            Category == SpellTrackerCategory.Hostile ? BuffAlertModeOptions.Normalize(LandAlertMode) : null,
            Category == SpellTrackerCategory.Hostile ? landVoice : null);
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
        else if (snapshot.StopReason == BuffStopReason.Zone)
        {
            RemainingText = "Stopped";
            StatusText = "Cleared on zone";
        }
        else
        {
            RemainingText = "Waiting";
            StatusText = "Not detected yet";
        }
    }

    private static string SourceHint(SpellTimingSource source) =>
        source == SpellTimingSource.Manual ? "Manual" : "From spell data";

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
