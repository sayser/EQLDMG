namespace EQLDamageMeter.Services;

public enum BisPlaystyle
{
    Balanced,
    Tank,
    Caster,
    Dps,
    DpsDots,
    DpsDotsOnly
}

public static class BisGearScorer
{
    public sealed record ClassOption(string Id, string Name, string EquipmentCategory);

    public static readonly IReadOnlyList<ClassOption> Classes =
    [
        new("WAR", "Warrior", "Category:Warrior Equipment"),
        new("PAL", "Paladin", "Category:Paladin Equipment"),
        new("SHD", "Shadow Knight", "Category:Shadow Knight Equipment"),
        new("MNK", "Monk", "Category:Monk Equipment"),
        new("ROG", "Rogue", "Category:Rogue Equipment"),
        new("BER", "Berserker", "Category:Berserker Equipment"),
        new("RNG", "Ranger", "Category:Ranger Equipment"),
        new("BST", "Beastlord", "Category:Beastlord Equipment"),
        new("BRD", "Bard", "Category:Bard Equipment"),
        new("CLR", "Cleric", "Category:Cleric Equipment"),
        new("DRU", "Druid", "Category:Druid Equipment"),
        new("SHM", "Shaman", "Category:Shaman Equipment"),
        new("ENC", "Enchanter", "Category:Enchanter Equipment"),
        new("MAG", "Magician", "Category:Magician Equipment"),
        new("NEC", "Necromancer", "Category:Necromancer Equipment"),
        new("WIZ", "Wizard", "Category:Wizard Equipment")
    ];

    public static readonly IReadOnlyList<string> PlaystyleLabels =
        ["Balanced DPS", "Melee DPS", "DoT DPS", "Tank / survive", "Caster / CC"];

    private static readonly Dictionary<string, Dictionary<string, double>> ClassWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WAR"] = W(ac: 10, hp: 10, sta: 10, str: 8, agi: 7, dex: 5, haste: 7, ratio: 6, wis: 0, intel: 0, cha: 1, mana: 0, dmg: 6),
        ["PAL"] = W(ac: 10, hp: 10, sta: 8, str: 6, agi: 5, dex: 5, haste: 5, ratio: 5, wis: 7, intel: 0, cha: 6, mana: 6, dmg: 5),
        ["SHD"] = W(ac: 10, hp: 10, sta: 8, str: 6, agi: 5, dex: 5, haste: 5, ratio: 5, wis: 0, intel: 7, cha: 3, mana: 6, dmg: 5),
        ["MNK"] = W(ac: 7, hp: 8, sta: 7, str: 7, agi: 9, dex: 8, haste: 8, ratio: 8, wis: 0, intel: 0, cha: 0, mana: 0, dmg: 8),
        ["ROG"] = W(ac: 5, hp: 6, sta: 5, str: 6, agi: 9, dex: 10, haste: 9, ratio: 9, wis: 0, intel: 0, cha: 1, mana: 0, dmg: 8),
        ["BER"] = W(ac: 6, hp: 7, sta: 6, str: 9, agi: 6, dex: 8, haste: 8, ratio: 8, wis: 0, intel: 0, cha: 0, mana: 0, dmg: 8),
        ["RNG"] = W(ac: 6, hp: 7, sta: 6, str: 6, agi: 7, dex: 7, haste: 6, ratio: 7, wis: 7, intel: 0, cha: 2, mana: 6, dmg: 7),
        ["BST"] = W(ac: 6, hp: 7, sta: 7, str: 5, agi: 6, dex: 5, haste: 6, ratio: 5, wis: 8, intel: 0, cha: 4, mana: 7, dmg: 5),
        ["BRD"] = W(ac: 6, hp: 6, sta: 5, str: 4, agi: 5, dex: 9, haste: 6, ratio: 5, wis: 0, intel: 7, cha: 9, mana: 6, dmg: 4),
        ["CLR"] = W(ac: 7, hp: 7, sta: 6, str: 3, agi: 3, dex: 2, haste: 2, ratio: 2, wis: 10, intel: 0, cha: 2, mana: 9, dmg: 1),
        ["DRU"] = W(ac: 5, hp: 6, sta: 5, str: 2, agi: 3, dex: 2, haste: 2, ratio: 2, wis: 10, intel: 0, cha: 2, mana: 9, dmg: 1),
        ["SHM"] = W(ac: 6, hp: 8, sta: 9, str: 3, agi: 3, dex: 3, haste: 4, ratio: 3, wis: 10, intel: 0, cha: 4, mana: 9, dmg: 2),
        ["ENC"] = W(ac: 4, hp: 4, sta: 3, str: 1, agi: 2, dex: 2, haste: 3, ratio: 1, wis: 0, intel: 10, cha: 9, mana: 9, dmg: 1),
        ["MAG"] = W(ac: 4, hp: 4, sta: 4, str: 1, agi: 2, dex: 2, haste: 1, ratio: 1, wis: 0, intel: 10, cha: 1, mana: 9, dmg: 1),
        ["NEC"] = W(ac: 4, hp: 4, sta: 3, str: 1, agi: 2, dex: 6, haste: 1, ratio: 1, wis: 0, intel: 10, cha: 1, mana: 9, dmg: 1),
        ["WIZ"] = W(ac: 3, hp: 3, sta: 4, str: 1, agi: 2, dex: 1, haste: 1, ratio: 1, wis: 0, intel: 10, cha: 1, mana: 9, dmg: 1)
    };

    private static readonly Dictionary<BisPlaystyle, Dictionary<string, double>> StyleMult = new()
    {
        // Hybrid melee + DoTs + enough HP/AC to live. Classic WAR/SHM/ENC default.
        [BisPlaystyle.Balanced] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = 1.05, ["HP"] = 1.15, ["STA"] = 1.1, ["STR"] = 1.2, ["AGI"] = 1.05,
            ["DEX"] = 1.2, ["HASTE"] = 1.25, ["RATIO"] = 1.25, ["DMG"] = 1.15,
            ["WIS"] = 1.15, ["INT"] = 1.15, ["CHA"] = 0.7, ["MANA"] = 1.2
        },
        // Classic tank: AC then HP/STA, AGI to 75, haste for aggro, shield offhand.
        [BisPlaystyle.Tank] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = 1.65, ["HP"] = 1.55, ["STA"] = 1.4, ["STR"] = 0.95, ["AGI"] = 1.25,
            ["DEX"] = 0.85, ["HASTE"] = 1.2, ["RATIO"] = 0.45, ["DMG"] = 0.4,
            ["WIS"] = 0.55, ["INT"] = 0.45, ["CHA"] = 0.4, ["MANA"] = 0.55
        },
        [BisPlaystyle.Caster] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = 0.8, ["HP"] = 0.9, ["STA"] = 0.9, ["STR"] = 0.5, ["AGI"] = 0.6,
            ["DEX"] = 0.6, ["HASTE"] = 0.7, ["RATIO"] = 0.4, ["DMG"] = 0.4,
            ["WIS"] = 1.3, ["INT"] = 1.3, ["CHA"] = 1.35, ["MANA"] = 1.45
        },
        // Melee auto-attack: STR/DEX/haste/ratio. Caster stats from melee classes only.
        [BisPlaystyle.Dps] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = 0.5, ["HP"] = 0.55, ["STA"] = 0.7, ["STR"] = 1.55, ["AGI"] = 1.1,
            ["DEX"] = 1.5, ["HASTE"] = 1.6, ["RATIO"] = 1.85, ["DMG"] = 0.35,
            ["WIS"] = 0.12, ["INT"] = 0.12, ["CHA"] = 0.15, ["MANA"] = 0.12
        },
        // Melee + DoT mana. CHA stays low — mez is Caster / CC.
        [BisPlaystyle.DpsDots] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = 0.9, ["HP"] = 1.05, ["STA"] = 1.0, ["STR"] = 1.3, ["AGI"] = 1.05,
            ["DEX"] = 1.25, ["HASTE"] = 1.35, ["RATIO"] = 1.35, ["DMG"] = 1.2,
            ["WIS"] = 1.3, ["INT"] = 1.3, ["CHA"] = 0.6, ["MANA"] = 1.35
        },
        [BisPlaystyle.DpsDotsOnly] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = 0.7, ["HP"] = 0.9, ["STA"] = 0.85, ["STR"] = 0.5, ["AGI"] = 0.6,
            ["DEX"] = 0.55, ["HASTE"] = 0.55, ["RATIO"] = 0.35, ["DMG"] = 0.35,
            ["WIS"] = 1.45, ["INT"] = 1.45, ["CHA"] = 0.55, ["MANA"] = 1.5
        }
    };

    /// <summary>
    /// EQL worn+spell haste cap at level 50 (eqlwiki Haste Guide). Item haste is a percent
    /// (FBSS +21, CoF +36, Four Winds +41), not Live's 175 display. Only the highest worn
    /// haste item counts. Monk Unbound Alacrity raises the cap by 10.
    /// </summary>
    public const double HasteCap = 75;
    public const double MonkHasteCap = 85;

    /// <summary>
    /// EQL wiki mana-per-WIS/INT at 60 is ~11.3 under the 200 soft cap.
    /// Scaled to level 50: 11.3 × 50/60 ≈ 9.4. DoT damage does not scale with INT/WIS;
    /// the stat is scored as mana (uptime).
    /// </summary>
    public const double ManaPerPrimaryStatAt50 = 9.4;

    /// <summary>
    /// Classic / Velious computed-AC hard cap (client decompile, TAKP, mackal Mid-Velious).
    /// At level 50 this is 350 for every class. Class-specific raises (WAR 430, PAL/SHD/BRD 403,
    /// RNG/ROG/MNK/BST 375) only apply when level is above 50. Extra AC past this is unused.
    /// This is combat AC after item×4/3, defense, AGI, and race bonuses — not inventory worn AC.
    /// Combat Stability raises it by 2/5/10%; Physical Enhancement by another 2%.
    /// </summary>
    public static int AcHardCap(string classId, int level = 50)
    {
        level = Math.Clamp(level, 1, 105);
        if (level <= 50)
            return 350;

        return classId.ToUpperInvariant() switch
        {
            "WAR" => 430,
            "PAL" or "SHD" or "BRD" => 403,
            "RNG" or "ROG" or "MNK" or "BST" => 375,
            _ => 350
        };
    }

    public static int AcHardCap(IReadOnlyList<string> classes, int level = 50)
    {
        var best = 0;
        foreach (var id in classes)
            best = Math.Max(best, AcHardCap(id, level));
        return best;
    }

    /// <summary>Alias used by older call sites — EQL 50 uses the hard cap, not Live soft caps.</summary>
    public static int AcSoftCap(string classId, int level = 50) => AcHardCap(classId, level);

    public static int AcSoftCap(IReadOnlyList<string> classes, int level = 50) => AcHardCap(classes, level);

    /// <summary>Hard cap: extra computed AC past <see cref="AcHardCap"/> does not mitigate.</summary>
    public static double AcOverCapReturn(string classId) => 0;

    public static double AcOverCapReturn(IReadOnlyList<string> classes) => 0;

    /// <summary>
    /// Sony/Dzarn anti-twink hard cap on (item AC × 4/3). EQEmu applies it when level is below 50;
    /// EQL wiki lists it for level 50 and below. Same formula for every class.
    /// </summary>
    public static int AntiTwinkWornAcCap(int level) => 25 + 6 * Math.Max(0, level);

    /// <summary>Iksar innate AC: +level, min 10, max 35. Added after anti-twink; not a worn-AC cap.</summary>
    public static int IksarAcBonus(int level) => Math.Clamp(level, 10, 35);

    /// <summary>Full value to the hard cap, then unused.</summary>
    public static double EffectiveAc(double ac, string classId, int level = 50)
    {
        var cap = AcHardCap(classId, level);
        if (ac <= 0 || cap <= 0) return Math.Max(0, ac);
        return Math.Min(ac, cap);
    }

    private static readonly HashSet<string> MeleeClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "WAR", "PAL", "SHD", "MNK", "ROG", "BER", "RNG", "BST", "BRD"
    };

    private static readonly HashSet<string> CasterStatKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "WIS", "INT", "CHA", "MANA"
    };

    private static readonly HashSet<string> WisManaClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLR", "DRU", "SHM", "PAL", "RNG"
    };

    private static readonly HashSet<string> IntManaClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ENC", "MAG", "NEC", "WIZ", "SHD", "BRD"
    };

    /// <summary>EQL Game Mechanics: STA→HP at level 50. Combo uses the best class.</summary>
    public static double StaToHp(string classId) => classId.ToUpperInvariant() switch
    {
        "WAR" => 4.5,
        "PAL" or "SHD" => 3.8,
        "RNG" => 3.3,
        "BRD" or "MNK" or "ROG" or "BER" => 3.0,
        "CLR" or "DRU" or "SHM" => 2.5,
        _ => 2.0
    };

    public static double BestStaToHp(IReadOnlyList<string>? classes)
    {
        var best = 2.0;
        if (classes is null) return best;
        foreach (var id in classes)
            best = Math.Max(best, StaToHp(id));
        return best;
    }

    public static bool UsesWisMana(IReadOnlyList<string>? classes) =>
        classes?.Any(id => WisManaClasses.Contains(id)) == true;

    public static bool UsesIntMana(IReadOnlyList<string>? classes) =>
        classes?.Any(id => IntManaClasses.Contains(id)) == true;

    public static bool IsMeleeClass(string classId) => MeleeClassIds.Contains(classId);

    /// <summary>Classic plate wearers. Combos that include one keep worn AC at the combat cap.</summary>
    private static readonly HashSet<string> PlateClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "WAR", "PAL", "SHD", "CLR", "BRD"
    };

    public static bool IsPlateClass(string classId) => PlateClassIds.Contains(classId);

    public static bool HasPlateClass(IReadOnlyList<string>? classes) =>
        classes?.Any(IsPlateClass) == true;

    public static bool IsWeaponSlot(string slotKey) =>
        slotKey.Equals("Primary", StringComparison.OrdinalIgnoreCase) ||
        slotKey.Equals("Secondary", StringComparison.OrdinalIgnoreCase) ||
        slotKey.Equals("Range", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, double> MergeWeights(string class1, string class2, string class3,
        BisPlaystyle playstyle)
    {
        var ids = new[] { class1, class2, class3 };
        var meleeIds = ids.Where(IsMeleeClass).ToArray();
        var restrictCaster = meleeIds.Length > 0 &&
                             playstyle is BisPlaystyle.Dps or BisPlaystyle.Tank;
        var keys = new[] { "AC", "HP", "STA", "STR", "AGI", "DEX", "HASTE", "RATIO", "WIS", "INT", "CHA", "MANA", "DMG", "SV" };
        var merged = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var style = StyleMult[playstyle];
        foreach (var key in keys)
        {
            var sources = restrictCaster && CasterStatKeys.Contains(key) ? meleeIds : ids;
            var values = sources.Select(id => Read(id, key)).ToArray();
            var raw = values.Length == 0 ? 0 : values.Max();
            var overlap = values.Count(v => v > 0);
            if (overlap >= 2 && raw >= 5) raw = Math.Min(10, raw + 1);
            var mult = style.GetValueOrDefault(key, 1.0);
            merged[key] = raw * mult;
        }

        if (HasPlateClass(ids) && playstyle != BisPlaystyle.Tank)
            ApplyPlateAcBias(merged);

        return merged;
    }

    /// <summary>
    /// Worn inventory AC that maps to <see cref="AcHardCap"/> via the classic item×4/3 formula,
    /// clamped by the anti-twink worn cap. At 50 this is 263 (ceil(350×3/4)), not 350.
    /// </summary>
    public static int WornAcFloor(IReadOnlyList<string> classes, int level = 50)
    {
        var combatCap = AcHardCap(classes, level);
        var wornForCombat = (int)Math.Ceiling(combatCap * 3.0 / 4.0);
        return Math.Min(wornForCombat, AntiTwinkWornAcCap(level));
    }

    /// <summary>
    /// Mild AC preference for plate so close calls keep armor. The hard rule is
    /// <see cref="WornAcFloor"/> after picks, not ranking every slot by AC.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ApplyPlateAcBias(IReadOnlyDictionary<string, double> merged)
    {
        var copy = merged as Dictionary<string, double> ??
                   new Dictionary<string, double>(merged, StringComparer.OrdinalIgnoreCase);
        if (!ReferenceEquals(copy, merged))
        {
            Scale(copy, "AC", 1.3);
            Scale(copy, "HP", 1.05);
            Scale(copy, "AGI", 1.05);
            return copy;
        }

        Scale(copy, "AC", 1.3);
        Scale(copy, "HP", 1.05);
        Scale(copy, "AGI", 1.05);
        return copy;
    }

    public readonly record struct PlateAcOption(string Name, double Ac, double Score, bool IsLore, double Haste);

    /// <summary>
    /// Among higher-AC replacements, pick the one that gains the most AC per score lost.
    /// Skips lore already used and weaker haste pieces when a unique worn haste is equipped.
    /// </summary>
    public static PlateAcOption? BestPlateAcSwap(
        double currentAc, double currentScore, double uniqueHaste,
        IEnumerable<PlateAcOption> candidates, IReadOnlySet<string> usedLore)
    {
        PlateAcOption? best = null;
        var bestEff = double.MinValue;
        var bestGain = 0.0;
        foreach (var cand in candidates)
        {
            if (cand.IsLore && usedLore.Contains(cand.Name))
                continue;
            var gain = cand.Ac - currentAc;
            if (gain <= 0)
                continue;
            if (uniqueHaste > 0 && cand.Haste > 0 && cand.Haste < uniqueHaste)
                continue;
            var loss = Math.Max(0, currentScore - cand.Score);
            var efficiency = gain / (loss + 1);
            if (efficiency > bestEff || (Math.Abs(efficiency - bestEff) < 1e-9 && gain > bestGain))
            {
                best = cand;
                bestEff = efficiency;
                bestGain = gain;
            }
        }

        return best;
    }

    /// <summary>
    /// Melee DPS weapons: ratio is real auto-attack DPS. HP/AC/mana on a 1H must not beat a 2H.
    /// </summary>
    public static IReadOnlyDictionary<string, double> WeaponWeights(
        IReadOnlyDictionary<string, double> merged, BisPlaystyle playstyle)
    {
        if (playstyle is BisPlaystyle.Caster or BisPlaystyle.DpsDotsOnly)
            return merged;

        var copy = new Dictionary<string, double>(merged, StringComparer.OrdinalIgnoreCase);
        if (playstyle == BisPlaystyle.Tank)
        {
            Scale(copy, "AC", 1.7);
            Scale(copy, "HP", 1.15);
            Scale(copy, "RATIO", 0.35);
            Scale(copy, "DMG", 0.3);
            Scale(copy, "MANA", 0.25);
            Scale(copy, "INT", 0.2);
            Scale(copy, "WIS", 0.25);
            Scale(copy, "CHA", 0.2);
            return copy;
        }

        Scale(copy, "AC", 0.3);
        Scale(copy, "HP", 0.2);
        if (playstyle == BisPlaystyle.Dps)
        {
            Scale(copy, "MANA", 0.2);
            Scale(copy, "INT", 0.2);
            Scale(copy, "WIS", 0.2);
            Scale(copy, "CHA", 0.2);
            Scale(copy, "RATIO", 1.4);
        }

        return copy;
    }

    /// <summary>
    /// Armor/jewelry slots must not score weapon offense. Multi-slot items like
    /// Fang of the Wolf (EAR PRIMARY SECONDARY) only use DMG/ratio in hand slots.
    /// </summary>
    public static IReadOnlyDictionary<string, double> NonWeaponWeights(
        IReadOnlyDictionary<string, double> merged)
    {
        var copy = new Dictionary<string, double>(merged, StringComparer.OrdinalIgnoreCase);
        copy["DMG"] = 0;
        copy["RATIO"] = 0;
        return copy;
    }

    public static Dictionary<string, double> WithoutWeaponOffense(IReadOnlyDictionary<string, double> stats)
    {
        var copy = new Dictionary<string, double>(stats, StringComparer.OrdinalIgnoreCase);
        copy.Remove("DMG");
        copy.Remove("RATIO");
        copy.Remove("DELAY");
        return copy;
    }

    public static double Score(IReadOnlyDictionary<string, double> stats, IReadOnlyDictionary<string, double> weights,
        IReadOnlyList<string>? classes = null, BisProcInfo? proc = null)
    {
        var monk = HasMonk(classes);
        var hasRatio = Stat(stats, "RATIO") > 0;
        var hp = Stat(stats, "HP") + Stat(stats, "STA") * BestStaToHp(classes);
        var mana = Stat(stats, "MANA");
        if (weights.GetValueOrDefault("WIS") > 0 && UsesWisMana(classes))
            mana += Stat(stats, "WIS") * ManaPerPrimaryStatAt50;
        if (weights.GetValueOrDefault("INT") > 0 && UsesIntMana(classes))
            mana += Stat(stats, "INT") * ManaPerPrimaryStatAt50;

        var total = 0.0;
        var hpWeight = weights.GetValueOrDefault("HP", 0);
        if (hp != 0 && hpWeight > 0)
            total += hp * hpWeight;
        var manaWeight = weights.GetValueOrDefault("MANA", 0);
        if (mana != 0 && manaWeight > 0)
            total += mana * manaWeight;

        foreach (var (key, raw) in stats)
        {
            if (hasRatio && key.Equals("DMG", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("DELAY", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("STA", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("HP", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("MANA", StringComparison.OrdinalIgnoreCase))
                continue;
            if (key.Equals("WIS", StringComparison.OrdinalIgnoreCase) &&
                weights.GetValueOrDefault("WIS") > 0 && UsesWisMana(classes))
                continue;
            if (key.Equals("INT", StringComparison.OrdinalIgnoreCase) &&
                weights.GetValueOrDefault("INT") > 0 && UsesIntMana(classes))
                continue;

            var value = raw;
            if (key.Equals("HASTE", StringComparison.OrdinalIgnoreCase))
                value = EffectiveHaste(raw, monk);
            if (key.Equals("RATIO", StringComparison.OrdinalIgnoreCase))
                value *= 100;
            if (value == 0) continue;
            var weight = weights.GetValueOrDefault(key, 0);
            if (weight <= 0) continue;
            // Negative DEX/AGI/etc. must reduce the score — Stonemelder's Band style traps.
            total += value * weight;
        }

        var haste = EffectiveHaste(Stat(stats, "HASTE"), monk);
        if (haste >= 41) total += 140;
        else if (haste >= 36) total += 110;
        else if (haste >= 21) total += 80;

        if (proc is not null && BisItemEffects.IsDpsProc(proc))
        {
            var ratioWeight = weights.GetValueOrDefault("RATIO", 0);
            if (ratioWeight >= 3)
                total += BisMeleeMath.ProcRatioEquivalent(proc.EstimatedHit) * 100 * ratioWeight;
        }

        return total;
    }

    private static double Stat(IReadOnlyDictionary<string, double> stats, string key) =>
        stats.TryGetValue(key, out var value) ? value : 0;

    private static void Scale(Dictionary<string, double> weights, string key, double factor)
    {
        if (weights.TryGetValue(key, out var value))
            weights[key] = value * factor;
    }

    public static bool HasMonk(IReadOnlyList<string>? classes) =>
        classes?.Any(id => id.Equals("MNK", StringComparison.OrdinalIgnoreCase)) == true;

    public static double EffectiveHaste(double haste, bool monkUncapped) =>
        haste <= 0 ? 0 : Math.Min(haste, monkUncapped ? MonkHasteCap : HasteCap);

    public static string Summary(IReadOnlyDictionary<string, double> stats)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "AC", "HP", "MANA", "STR", "STA", "AGI", "DEX", "WIS", "INT", "CHA", "HASTE", "DMG", "RATIO" })
        {
            if (!stats.TryGetValue(key, out var value) || value == 0) continue;
            parts.Add(key == "RATIO" || key == "HASTE"
                ? $"{key} {value.ToString(key == "RATIO" ? "0.000" : "0")}"
                : $"{key} {value:+0;-0}");
        }

        return parts.Count == 0 ? "No scored stats" : string.Join("  ", parts);
    }

    public static string Summary(IReadOnlyDictionary<string, double> stats, BisProcInfo proc)
    {
        var text = Summary(stats);
        if (proc.Kind == BisProcKind.None || string.IsNullOrWhiteSpace(proc.Name))
            return text;
        if (BisItemEffects.IsDpsProc(proc))
            return $"{text}  PROC {proc.Name} ~{proc.EstimatedHit:0}";
        if (proc.Kind == BisProcKind.CombatUtility)
            return $"{text}  {proc.Name} (not DPS)";
        return $"{text}  {proc.Name} (clicky)";
    }

    private static double Read(string classId, string key) =>
        ClassWeights.GetValueOrDefault(classId)?.GetValueOrDefault(key) ?? 0;

    private static Dictionary<string, double> W(double ac, double hp, double sta, double str, double agi, double dex,
        double haste, double ratio, double wis, double intel, double cha, double mana, double dmg) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AC"] = ac, ["HP"] = hp, ["STA"] = sta, ["STR"] = str, ["AGI"] = agi, ["DEX"] = dex,
            ["HASTE"] = haste, ["RATIO"] = ratio, ["WIS"] = wis, ["INT"] = intel, ["CHA"] = cha,
            ["MANA"] = mana, ["DMG"] = dmg, ["SV"] = 1
        };
}
