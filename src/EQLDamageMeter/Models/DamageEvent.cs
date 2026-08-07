namespace EQLDamageMeter.Models;

public enum DamageCategory
{
    Melee,
    Spell,
    DamageOverTime,
    Reactive
}

public sealed record DamageEvent(
    DateTime Timestamp,
    string Source,
    string Target,
    int Amount,
    string Ability,
    DamageCategory Category,
    bool IsCritical);

public sealed record HealingEvent(
    DateTime Timestamp,
    string Source,
    string Target,
    int Amount,
    int PotentialAmount,
    string Ability,
    bool IsOverTime,
    bool IsCritical);

public enum CombatOutcomeKind
{
    MissedAttack,
    SpellFizzle,
    SpellResist,
    DefensiveDodge,
    DefensiveParry,
    DefensiveBlock,
    DefensiveRiposte,
    DefensiveAbsorb,
    DefensiveSpellAbsorb,
    DefensiveSpellResist,
    StunApplied,
    StunDiminished
}

public sealed record CombatOutcomeEvent(
    DateTime Timestamp,
    string Source,
    string? Target,
    string Ability,
    CombatOutcomeKind Kind);

public sealed record ParsedLogLine(
    DateTime Timestamp,
    string Message,
    DamageEvent? Damage,
    HealingEvent? Healing,
    CombatOutcomeEvent? Outcome);
