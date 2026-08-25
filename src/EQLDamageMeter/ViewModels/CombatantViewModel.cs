using System.Globalization;
using System.Windows.Media;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class CombatantViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string? _ownerName;
    private long _damage;
    private int _hits;
    private int _meleeHits;
    private int _spellHits;
    private int _meleeCriticalHits;
    private int _spellCriticalHits;
    private long _meleeDamage;
    private int _meleeHitMin;
    private int _meleeHitMax;
    private int _misses;
    private int _spellFizzles;
    private int _spellResists;
    private long _damageTaken;
    private int _incomingHits;
    private int _incomingMeleeHits;
    private int _incomingMisses;
    private int _dodges;
    private int _parries;
    private int _blocks;
    private int _ripostes;
    private int _absorbed;
    private int _spellAbsorbs;
    private int _incomingSpellResists;
    private int _stunsLanded;
    private int _stunsTaken;
    private long _healing;
    private int _directHeals;
    private int _healOverTimeTicks;
    private int _criticalHeals;
    private int _rank;
    private string _dpsText = "—";
    private string _hpsText = "0.0";
    private AbilityViewModel[] _abilities = [];
    private AbilityViewModel[] _incomingAbilities = [];
    private AbilityViewModel[] _mitigations = [];
    private AbilityViewModel[] _healingAbilities = [];
    private AbilityViewModel[] _procs = [];
    private bool _isGraphExpanded;
    private double[] _dpsTimeline = [];
    private double[] _petDpsTimeline = [];

    public string Name
    {
        get => _name;
        set
        {
            if (!SetProperty(ref _name, value)) return;
            RaisePropertyChanged(nameof(BarBrush));
        }
    }

    public string? OwnerName
    {
        get => _ownerName;
        set => SetProperty(ref _ownerName, value);
    }

    public long Damage
    {
        get => _damage;
        set
        {
            if (!SetProperty(ref _damage, value)) return;
            RaisePropertyChanged(nameof(DamageText));
        }
    }

    public int Hits { get => _hits; set => SetProperty(ref _hits, value); }
    public int MeleeHits
    {
        get => _meleeHits;
        set
        {
            if (!SetProperty(ref _meleeHits, value)) return;
            RaisePropertyChanged(nameof(CriticalRateText));
            RaisePropertyChanged(nameof(MeleeAverageHitText));
            RaisePropertyChanged(nameof(MeleeLowestHitText));
            RaisePropertyChanged(nameof(MeleeHighestHitText));
        }
    }

    public int SpellHits
    {
        get => _spellHits;
        set
        {
            if (!SetProperty(ref _spellHits, value)) return;
            RaisePropertyChanged(nameof(SpellCriticalRateText));
        }
    }

    public int MeleeCriticalHits
    {
        get => _meleeCriticalHits;
        set
        {
            if (!SetProperty(ref _meleeCriticalHits, value)) return;
            RaisePropertyChanged(nameof(CriticalRateText));
        }
    }

    public int SpellCriticalHits
    {
        get => _spellCriticalHits;
        set
        {
            if (!SetProperty(ref _spellCriticalHits, value)) return;
            RaisePropertyChanged(nameof(SpellCriticalRateText));
        }
    }

    public long MeleeDamage
    {
        get => _meleeDamage;
        set
        {
            if (!SetProperty(ref _meleeDamage, value)) return;
            RaisePropertyChanged(nameof(MeleeAverageHitText));
        }
    }

    public int MeleeHitMin
    {
        get => _meleeHitMin;
        set
        {
            if (!SetProperty(ref _meleeHitMin, value)) return;
            RaisePropertyChanged(nameof(MeleeLowestHitText));
        }
    }

    public int MeleeHitMax
    {
        get => _meleeHitMax;
        set
        {
            if (!SetProperty(ref _meleeHitMax, value)) return;
            RaisePropertyChanged(nameof(MeleeHighestHitText));
        }
    }

    public int Misses { get => _misses; set => SetProperty(ref _misses, value); }
    public int SpellFizzles { get => _spellFizzles; set => SetProperty(ref _spellFizzles, value); }
    public int SpellResists { get => _spellResists; set => SetProperty(ref _spellResists, value); }

    public long DamageTaken
    {
        get => _damageTaken;
        set
        {
            if (!SetProperty(ref _damageTaken, value)) return;
            RaisePropertyChanged(nameof(DamageTakenText));
        }
    }

    public int IncomingHits
    {
        get => _incomingHits;
        set
        {
            if (!SetProperty(ref _incomingHits, value)) return;
            RaisePropertyChanged(nameof(MitigationRateText));
        }
    }

    public int IncomingMeleeHits { get => _incomingMeleeHits; set => SetProperty(ref _incomingMeleeHits, value); }
    public int IncomingMisses { get => _incomingMisses; set => SetProperty(ref _incomingMisses, value); }

    public int Dodges
    {
        get => _dodges;
        set
        {
            if (!SetProperty(ref _dodges, value)) return;
            RaiseMitigationChanged();
        }
    }

    public int Parries
    {
        get => _parries;
        set
        {
            if (!SetProperty(ref _parries, value)) return;
            RaiseMitigationChanged();
        }
    }

    public int Blocks
    {
        get => _blocks;
        set
        {
            if (!SetProperty(ref _blocks, value)) return;
            RaiseMitigationChanged();
        }
    }

    public int Ripostes
    {
        get => _ripostes;
        set
        {
            if (!SetProperty(ref _ripostes, value)) return;
            RaiseMitigationChanged();
        }
    }

    public int Absorbed
    {
        get => _absorbed;
        set
        {
            if (!SetProperty(ref _absorbed, value)) return;
            RaiseMitigationChanged();
        }
    }

    public int SpellAbsorbs { get => _spellAbsorbs; set => SetProperty(ref _spellAbsorbs, value); }

    public int IncomingSpellResists
    {
        get => _incomingSpellResists;
        set
        {
            if (!SetProperty(ref _incomingSpellResists, value)) return;
            RaiseMitigationChanged();
        }
    }
    public int StunsLanded { get => _stunsLanded; set => SetProperty(ref _stunsLanded, value); }
    public int StunsTaken { get => _stunsTaken; set => SetProperty(ref _stunsTaken, value); }

    public long Healing
    {
        get => _healing;
        set
        {
            if (!SetProperty(ref _healing, value)) return;
            RaisePropertyChanged(nameof(HealingText));
        }
    }

    public int DirectHeals
    {
        get => _directHeals;
        set
        {
            if (!SetProperty(ref _directHeals, value)) return;
            RaisePropertyChanged(nameof(HealingCriticalRateText));
        }
    }

    public int HealOverTimeTicks
    {
        get => _healOverTimeTicks;
        set
        {
            if (!SetProperty(ref _healOverTimeTicks, value)) return;
            RaisePropertyChanged(nameof(HealingCriticalRateText));
        }
    }

    public int CriticalHeals
    {
        get => _criticalHeals;
        set
        {
            if (!SetProperty(ref _criticalHeals, value)) return;
            RaisePropertyChanged(nameof(HealingCriticalRateText));
        }
    }

    public int Rank { get => _rank; set => SetProperty(ref _rank, value); }
    public string DpsText { get => _dpsText; set => SetProperty(ref _dpsText, value); }
    public string HpsText { get => _hpsText; set => SetProperty(ref _hpsText, value); }
    public Brush BarBrush => CombatantColorPalette.ForName(Name);

    public AbilityViewModel[] Abilities
    {
        get => _abilities;
        set => SetProperty(ref _abilities, value);
    }

    public AbilityViewModel[] IncomingAbilities
    {
        get => _incomingAbilities;
        set => SetProperty(ref _incomingAbilities, value);
    }

    public AbilityViewModel[] Mitigations
    {
        get => _mitigations;
        set => SetProperty(ref _mitigations, value);
    }

    public AbilityViewModel[] HealingAbilities
    {
        get => _healingAbilities;
        set => SetProperty(ref _healingAbilities, value);
    }

    public AbilityViewModel[] Procs
    {
        get => _procs;
        set => SetProperty(ref _procs, value);
    }

    public bool IsGraphExpanded
    {
        get => _isGraphExpanded;
        set
        {
            if (!SetProperty(ref _isGraphExpanded, value)) return;
            RaisePropertyChanged(nameof(GraphExpandGlyph));
            RaisePropertyChanged(nameof(GraphVisibility));
        }
    }

    public string GraphExpandGlyph => IsGraphExpanded ? "▾" : "▸";
    public System.Windows.Visibility GraphVisibility =>
        IsGraphExpanded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public double[] DpsTimeline
    {
        get => _dpsTimeline;
        set => SetProperty(ref _dpsTimeline, value);
    }

    public double[] PetDpsTimeline
    {
        get => _petDpsTimeline;
        set
        {
            if (!SetProperty(ref _petDpsTimeline, value)) return;
            RaisePropertyChanged(nameof(HasPetDpsTimeline));
            RaisePropertyChanged(nameof(PetGraphLegendVisibility));
        }
    }

    public bool HasPetDpsTimeline => PetDpsTimeline.Length > 0 && PetDpsTimeline.Any(v => v > 0);
    public System.Windows.Visibility PetGraphLegendVisibility =>
        HasPetDpsTimeline ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public void ToggleGraphExpanded() => IsGraphExpanded = !IsGraphExpanded;

    public string DamageText => Damage.ToString("N0", CultureInfo.CurrentCulture);
    public string DamageTakenText => DamageTaken.ToString("N0", CultureInfo.CurrentCulture);
    public string HealingText => Healing.ToString("N0", CultureInfo.CurrentCulture);
    public string CriticalRateText => FormatRate(MeleeCriticalHits, MeleeHits);
    public string SpellCriticalRateText => FormatRate(SpellCriticalHits, SpellHits);
    public string HealingCriticalRateText => FormatRate(CriticalHeals, DirectHeals + HealOverTimeTicks);
    public string MeleeAverageHitText =>
        MeleeHits == 0 ? "—" : (MeleeDamage / (double)MeleeHits).ToString("N0", CultureInfo.CurrentCulture);
    public string MeleeLowestHitText =>
        MeleeHits == 0 ? "—" : MeleeHitMin.ToString("N0", CultureInfo.CurrentCulture);
    public string MeleeHighestHitText =>
        MeleeHits == 0 ? "—" : MeleeHitMax.ToString("N0", CultureInfo.CurrentCulture);
    public int MitigationCount =>
        Dodges + Parries + Blocks + Ripostes + Absorbed + IncomingSpellResists;
    public string MitigationRateText => FormatRate(MitigationCount, IncomingHits + MitigationCount);

    public void ApplyAggregate(CombatantAggregate aggregate, double seconds, bool isWarmingUp, int rank,
        AbilityViewModel[] abilities, AbilityViewModel[] incomingAbilities, AbilityViewModel[] healingAbilities,
        AbilityViewModel[] mitigations, AbilityViewModel[] procs)
    {
        Name = aggregate.Name;
        OwnerName = aggregate.OwnerName;
        Damage = aggregate.Damage;
        DpsText = isWarmingUp ? "—" : (aggregate.Damage / Math.Max(1, seconds))
            .ToString("N1", CultureInfo.CurrentCulture);
        Hits = aggregate.Hits;
        Misses = aggregate.Misses;
        MeleeHits = aggregate.MeleeHits;
        SpellHits = aggregate.SpellHits;
        MeleeCriticalHits = aggregate.MeleeCriticalHits;
        SpellCriticalHits = aggregate.SpellCriticalHits;
        MeleeDamage = aggregate.MeleeDamage;
        MeleeHitMin = aggregate.MeleeHitMin;
        MeleeHitMax = aggregate.MeleeHitMax;
        SpellFizzles = aggregate.SpellFizzles;
        SpellResists = aggregate.SpellResists;
        DamageTaken = aggregate.DamageTaken;
        IncomingHits = aggregate.IncomingHits;
        IncomingMeleeHits = aggregate.IncomingMeleeHits;
        IncomingMisses = aggregate.IncomingMisses;
        Dodges = aggregate.Dodges;
        Parries = aggregate.Parries;
        Blocks = aggregate.Blocks;
        Ripostes = aggregate.Ripostes;
        Absorbed = aggregate.Absorbed;
        SpellAbsorbs = aggregate.SpellAbsorbs;
        IncomingSpellResists = aggregate.IncomingSpellResists;
        Rank = rank;
        StunsLanded = aggregate.StunsLanded;
        StunsTaken = aggregate.StunsTaken;
        Healing = aggregate.Healing;
        DirectHeals = aggregate.DirectHeals;
        HealOverTimeTicks = aggregate.HealOverTimeTicks;
        CriticalHeals = aggregate.CriticalHeals;
        HpsText = (aggregate.Healing / Math.Max(1, seconds)).ToString("N1", CultureInfo.CurrentCulture);
        DpsTimeline = BuildDpsTimeline(
            aggregate.OwnerDamageBySecond.Count > 0 ? aggregate.OwnerDamageBySecond : aggregate.DamageBySecond);
        PetDpsTimeline = BuildDpsTimeline(aggregate.PetDamageBySecond);
        Abilities = abilities;
        IncomingAbilities = incomingAbilities;
        HealingAbilities = healingAbilities;
        Mitigations = mitigations;
        Procs = procs;
    }

    public void ApplySummary(CombatantAggregate aggregate, double seconds, bool isWarmingUp, int rank)
    {
        Name = aggregate.Name;
        OwnerName = aggregate.OwnerName;
        Damage = aggregate.Damage;
        DpsText = isWarmingUp ? "—" : (aggregate.Damage / Math.Max(1, seconds))
            .ToString("N1", CultureInfo.CurrentCulture);
        Hits = aggregate.Hits;
        Misses = aggregate.Misses;
        MeleeHits = aggregate.MeleeHits;
        SpellHits = aggregate.SpellHits;
        MeleeCriticalHits = aggregate.MeleeCriticalHits;
        SpellCriticalHits = aggregate.SpellCriticalHits;
        MeleeDamage = aggregate.MeleeDamage;
        MeleeHitMin = aggregate.MeleeHitMin;
        MeleeHitMax = aggregate.MeleeHitMax;
        SpellFizzles = aggregate.SpellFizzles;
        SpellResists = aggregate.SpellResists;
        DamageTaken = aggregate.DamageTaken;
        IncomingHits = aggregate.IncomingHits;
        IncomingMeleeHits = aggregate.IncomingMeleeHits;
        IncomingMisses = aggregate.IncomingMisses;
        Dodges = aggregate.Dodges;
        Parries = aggregate.Parries;
        Blocks = aggregate.Blocks;
        Ripostes = aggregate.Ripostes;
        Absorbed = aggregate.Absorbed;
        SpellAbsorbs = aggregate.SpellAbsorbs;
        IncomingSpellResists = aggregate.IncomingSpellResists;
        Rank = rank;
        StunsLanded = aggregate.StunsLanded;
        StunsTaken = aggregate.StunsTaken;
        Healing = aggregate.Healing;
        DirectHeals = aggregate.DirectHeals;
        HealOverTimeTicks = aggregate.HealOverTimeTicks;
        CriticalHeals = aggregate.CriticalHeals;
        HpsText = (aggregate.Healing / Math.Max(1, seconds)).ToString("N1", CultureInfo.CurrentCulture);
        DpsTimeline = BuildDpsTimeline(
            aggregate.OwnerDamageBySecond.Count > 0 ? aggregate.OwnerDamageBySecond : aggregate.DamageBySecond);
        PetDpsTimeline = BuildDpsTimeline(aggregate.PetDamageBySecond);
    }

    /// <summary>
    /// Rolling average DPS over a short window (damage in the last N seconds / N).
    /// Avoids the first-second spike from overall-DPS (total / elapsed) when an opener lands.
    /// </summary>
    public const int DpsTimelineWindowSeconds = 5;

    public static double[] BuildDpsTimeline(IReadOnlyList<long> damageBySecond)
    {
        if (damageBySecond.Count == 0) return [];
        var window = DpsTimelineWindowSeconds;
        var points = new double[damageBySecond.Count];
        long windowSum = 0;
        for (var i = 0; i < damageBySecond.Count; i++)
        {
            windowSum += damageBySecond[i];
            if (i >= window)
                windowSum -= damageBySecond[i - window];
            points[i] = windowSum / (double)window;
        }
        return points;
    }

    private void RaiseMitigationChanged()
    {
        RaisePropertyChanged(nameof(MitigationCount));
        RaisePropertyChanged(nameof(MitigationRateText));
    }

    private static string FormatRate(int count, int total) =>
        total == 0 ? "0.0%" : $"{count * 100d / total:0.0}%";
}

