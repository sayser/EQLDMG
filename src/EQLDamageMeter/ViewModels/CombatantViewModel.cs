using System.Globalization;

namespace EQLDamageMeter.ViewModels;

public sealed class CombatantViewModel
{
    public required string Name { get; init; }
    public string? OwnerName { get; init; }
    public long Damage { get; init; }
    public int Hits { get; init; }
    public int MeleeHits { get; init; }
    public int SpellHits { get; init; }
    public int MeleeCriticalHits { get; init; }
    public int SpellCriticalHits { get; init; }
    public int Misses { get; init; }
    public int SpellFizzles { get; init; }
    public int SpellResists { get; init; }
    public long DamageTaken { get; init; }
    public int IncomingMeleeHits { get; init; }
    public int IncomingMisses { get; init; }
    public int Dodges { get; init; }
    public int Parries { get; init; }
    public int Blocks { get; init; }
    public int Ripostes { get; init; }
    public int Absorbed { get; init; }
    public int SpellAbsorbs { get; init; }
    public int IncomingSpellResists { get; init; }
    public long Healing { get; init; }
    public int DirectHeals { get; init; }
    public int HealOverTimeTicks { get; init; }
    public int CriticalHeals { get; init; }
    public int Rank { get; init; }
    public required string DpsText { get; init; }
    public AbilityViewModel[] Abilities { get; init; } = [];
    public AbilityViewModel[] IncomingAbilities { get; init; } = [];
    public AbilityViewModel[] Mitigations { get; init; } = [];
    public AbilityViewModel[] HealingAbilities { get; init; } = [];
    public string DamageText => Damage.ToString("N0", CultureInfo.CurrentCulture);
    public string DamageTakenText => DamageTaken.ToString("N0", CultureInfo.CurrentCulture);
    public string HealingText => Healing.ToString("N0", CultureInfo.CurrentCulture);
    public string HpsText { get; init; } = "0.0";
    public string CriticalRateText => FormatRate(MeleeCriticalHits, MeleeHits);
    public string SpellCriticalRateText => FormatRate(SpellCriticalHits, SpellHits);
    public string HealingCriticalRateText => FormatRate(CriticalHeals, DirectHeals + HealOverTimeTicks);
    public int Avoided => Dodges + Parries + Blocks + Ripostes + Math.Max(0, Absorbed - SpellAbsorbs);
    public string AvoidanceRateText
    {
        get
        {
            var attempts = IncomingMeleeHits + IncomingMisses + Avoided;
            return attempts == 0 ? "0.0%" : $"{Avoided * 100d / attempts:0.0}%";
        }
    }

    private static string FormatRate(int criticals, int total) =>
        total == 0 ? "0.0%" : $"{criticals * 100d / total:0.0}%";
}
