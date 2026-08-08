namespace EQLDamageMeter.Models;

public enum BuffAlertMode
{
    Sound = 0,
    TextToSpeech = 1,
    /// <summary>Legacy persisted value; treated as Sound.</summary>
    Both = 2
}

public static class BuffAlertModeOptions
{
    public static IReadOnlyList<BuffAlertMode> ExclusiveChoices { get; } =
    [
        BuffAlertMode.Sound,
        BuffAlertMode.TextToSpeech
    ];

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
    Cascade
}

public enum BuffStopReason
{
    None,
    Dispelled,
    Death
}

public enum SpellTrackerCategory
{
    Buff,
    DamageOverTime,
    Control
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
    ControlEffectType ControlType = ControlEffectType.Other);

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

public sealed record BuffExpirationAlert(BuffRuleSettings Rule);

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
    bool IsOverdue);
