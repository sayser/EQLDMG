using System.Globalization;
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
    private int _misses;
    private int _spellFizzles;
    private int _spellResists;
    private long _damageTaken;
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

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
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

    public int IncomingMeleeHits
    {
        get => _incomingMeleeHits;
        set
        {
            if (!SetProperty(ref _incomingMeleeHits, value)) return;
            RaisePropertyChanged(nameof(AvoidanceRateText));
        }
    }

    public int IncomingMisses
    {
        get => _incomingMisses;
        set
        {
            if (!SetProperty(ref _incomingMisses, value)) return;
            RaisePropertyChanged(nameof(AvoidanceRateText));
        }
    }

    public int Dodges
    {
        get => _dodges;
        set
        {
            if (!SetProperty(ref _dodges, value)) return;
            RaisePropertyChanged(nameof(Avoided));
            RaisePropertyChanged(nameof(AvoidanceRateText));
        }
    }

    public int Parries
    {
        get => _parries;
        set
        {
            if (!SetProperty(ref _parries, value)) return;
            RaisePropertyChanged(nameof(Avoided));
            RaisePropertyChanged(nameof(AvoidanceRateText));
        }
    }

    public int Blocks
    {
        get => _blocks;
        set
        {
            if (!SetProperty(ref _blocks, value)) return;
            RaisePropertyChanged(nameof(Avoided));
            RaisePropertyChanged(nameof(AvoidanceRateText));
        }
    }

    public int Ripostes
    {
        get => _ripostes;
        set
        {
            if (!SetProperty(ref _ripostes, value)) return;
            RaisePropertyChanged(nameof(Avoided));
            RaisePropertyChanged(nameof(AvoidanceRateText));
        }
    }

    public int Absorbed
    {
        get => _absorbed;
        set
        {
            if (!SetProperty(ref _absorbed, value)) return;
            RaisePropertyChanged(nameof(Avoided));
            RaisePropertyChanged(nameof(AvoidanceRateText));
        }
    }

    public int SpellAbsorbs
    {
        get => _spellAbsorbs;
        set
        {
            if (!SetProperty(ref _spellAbsorbs, value)) return;
            RaisePropertyChanged(nameof(Avoided));
            RaisePropertyChanged(nameof(AvoidanceRateText));
        }
    }

    public int IncomingSpellResists { get => _incomingSpellResists; set => SetProperty(ref _incomingSpellResists, value); }
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

    public string DamageText => Damage.ToString("N0", CultureInfo.CurrentCulture);
    public string DamageTakenText => DamageTaken.ToString("N0", CultureInfo.CurrentCulture);
    public string HealingText => Healing.ToString("N0", CultureInfo.CurrentCulture);
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

    public void ApplyAggregate(CombatantAggregate aggregate, double seconds, bool isWarmingUp, int rank,
        AbilityViewModel[] abilities, AbilityViewModel[] incomingAbilities, AbilityViewModel[] healingAbilities,
        AbilityViewModel[] mitigations)
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
        SpellFizzles = aggregate.SpellFizzles;
        SpellResists = aggregate.SpellResists;
        DamageTaken = aggregate.DamageTaken;
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
        Abilities = abilities;
        IncomingAbilities = incomingAbilities;
        HealingAbilities = healingAbilities;
        Mitigations = mitigations;
    }

    private static string FormatRate(int criticals, int total) =>
        total == 0 ? "0.0%" : $"{criticals * 100d / total:0.0}%";
}
