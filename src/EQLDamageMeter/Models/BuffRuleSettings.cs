namespace EQLDamageMeter.Models;

public enum BuffAlertMode
{
    Sound,
    TextToSpeech,
    Both
}

public enum BuffSoundKind
{
    Bell,
    Chime,
    Drum
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
