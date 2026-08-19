namespace EQLDamageMeter.Models;

public enum BuffAlertMode
{
    Sound = 0,
    TextToSpeech = 1,
    /// <summary>Obsolete persisted value from older builds; normalized to Sound.</summary>
    Both = 2
}

public static class BuffAlertModeOptions
{
    public static IReadOnlyList<BuffAlertMode> ExclusiveChoices { get; } =
    [
        BuffAlertMode.Sound,
        BuffAlertMode.TextToSpeech
    ];

    /// <summary>UI/runtime only allow Sound or TextToSpeech; legacy Both → Sound.</summary>
    public static BuffAlertMode Normalize(BuffAlertMode mode) =>
        mode == BuffAlertMode.TextToSpeech ? BuffAlertMode.TextToSpeech : BuffAlertMode.Sound;
}

public enum BuffSoundKind
{
    Bell,
    Chime,
    Drum,
    Ping,
    Alert,
    Fanfare,
    Horn,
    Gong,
    Click,
    Blip,
    Pulse,
    Siren,
    Knock,
    Triangle,
    Marimba,
    Glass,
    Coin,
    Thud,
    Whistle,
    Cascade,
    Klaxon,
    Buzzer,
    AirHorn,
    Wail,
    AlarmBeep,
    RedAlert
}

public enum BuffStopReason
{
    None,
    Dispelled,
    Death,
    Zone,
    Expired
}

public enum SpellTrackerCategory
{
    Buff,
    DamageOverTime,
    Control,
    /// <summary>Enemy spells / debuffs applied to the local player.</summary>
    Hostile
}

public enum ControlEffectType
{
    Charm,
    Mez,
    Root,
    Other
}

public enum SpellIconStyle
{
    Modern,
    Classic
}

public enum SpellTimingSource
{
    Catalog,
    /// <summary>Obsolete persisted value from older builds; treated as Manual.</summary>
    Learned,
    Manual
}

/// <summary>
/// Spells arm on cast lines; bard songs arm on self land and clear on fade, worn off, or twist.
/// </summary>
public enum BuffTrackingMode
{
    Spell,
    Song
}

public sealed record BuffRuleSettings(
    Guid Id,
    string SpellName,
    int DurationSeconds,
    double CastTimeSeconds,
    bool IsEnabled,
    bool ShowInOverlay,
    BuffAlertMode AlertMode,
    BuffSoundKind Sound,
    string VoiceText,
    bool TrackSelf = true,
    bool TrackOthers = false,
    SpellTrackerCategory Category = SpellTrackerCategory.Buff,
    ControlEffectType ControlType = ControlEffectType.Other,
    SpellTimingSource CastSource = SpellTimingSource.Catalog,
    SpellTimingSource DurationSource = SpellTimingSource.Catalog,
    int CastSampleCount = 0,
    int DurationSampleCount = 0,
    double CastSampleSum = 0,
    double DurationSampleSum = 0,
    /// <summary>Hostile only: sound when the effect lands on you. Defaults to Sound.</summary>
    BuffSoundKind? LandSound = null,
    /// <summary>Hostile only: Sound or TTS for the land alert. Defaults to Sound.</summary>
    BuffAlertMode? LandAlertMode = null,
    /// <summary>Hostile only: spoken text when land alert uses TTS.</summary>
    string? LandVoiceText = null,
    BuffTrackingMode TrackingMode = BuffTrackingMode.Spell);

public sealed record BuffRuntimeSnapshot(
    Guid RuleId,
    DateTime? StartedAt,
    DateTime? ExpiresAt,
    TimeSpan Remaining,
    bool IsCasting,
    bool IsActive,
    bool IsExpired,
    bool IsExpiringSoon,
    bool IsOverdue,
    BuffStopReason StopReason);

public enum BuffAlertPhase
{
    Expired = 0,
    Landed = 1
}

public sealed record BuffExpirationAlert(BuffRuleSettings Rule, BuffAlertPhase Phase = BuffAlertPhase.Expired);

public sealed record BuffInstanceSnapshot(
    Guid RuleId,
    string InstanceKey,
    string SpellName,
    string TargetName,
    bool IsSelf,
    DateTime StartedAt,
    DateTime ExpiresAt,
    TimeSpan Remaining,
    bool IsExpiringSoon,
    bool IsOverdue,
    bool ShowsPlayingLabel = false);
