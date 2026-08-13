using System.Globalization;
using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

/// <summary>
/// Applies EverQuest Legends item upgrade tiers to wiki base stats.
/// Calibrated against in-game tooltips and eqlegendstools.com scaling
/// (wiki's "+5% weapon damage" note does not match live DMG):
/// - Most stats: if |base| &lt; 10 → +1 per tier; else floor(base × (1 + 0.1×tier))
/// - Weapon DMG: always floor(base × (1 + 0.1×tier))
/// - Haste: base + tier (percentage points)
/// - Weight: each tier multiplies current weight by 0.9, floored to 0.1, min 0.1
/// - Delay / Size / Skill / Range: unchanged
/// </summary>
public static partial class EqWikiItemUpgrade
{
    public static string ApplyTier(string baseStats, int tier)
    {
        if (string.IsNullOrWhiteSpace(baseStats))
            return string.Empty;

        tier = Math.Clamp(tier, 0, 10);
        var text = baseStats;
        if (tier > 0)
        {
            var lines = baseStats.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n');
            for (var i = 0; i < lines.Length; i++)
                lines[i] = ScaleLine(lines[i], tier);
            text = string.Join(Environment.NewLine, lines);
        }

        return WithWeaponRatio(text);
    }

    /// <summary>
    /// Adds or refreshes Ratio = DMG / Delay (3 decimals), matching in-game weapon tooltips.
    /// </summary>
    public static string WithWeaponRatio(string stats)
    {
        if (string.IsNullOrWhiteSpace(stats))
            return string.Empty;

        var normalized = stats.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized
            .Split('\n')
            .Where(line => !RatioLineRegex().IsMatch(line.Trim()))
            .ToList();

        int? dmg = null;
        int? delay = null;
        var dmgLineIndex = -1;
        var delayLineIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            foreach (Match match in StatTokenRegex().Matches(line))
            {
                var key = match.Groups["key"].Value.Trim();
                if (!int.TryParse(match.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var number))
                    continue;

                if (IsWeaponDamage(key))
                {
                    dmg = number;
                    dmgLineIndex = i;
                }
                else if (IsDelay(key))
                {
                    delay = number;
                    delayLineIndex = i;
                }
            }
        }

        if (dmg is null or <= 0 || delay is null or <= 0)
            return string.Join(Environment.NewLine, lines);

        var ratio = dmg.Value / (double)delay.Value;
        var ratioLine = $"Ratio: {ratio.ToString("0.000", CultureInfo.InvariantCulture)}";
        var insertAt = Math.Max(dmgLineIndex, delayLineIndex) + 1;
        lines.Insert(insertAt, ratioLine);
        return string.Join(Environment.NewLine, lines);
    }

    private static string ScaleLine(string line, int tier) =>
        StatTokenRegex().Replace(line, match =>
        {
            var key = match.Groups["key"].Value.Trim();
            var sign = match.Groups["sign"].Value;
            var numberText = match.Groups["num"].Value;
            var suffix = match.Groups["suffix"].Value;
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return match.Value;

            if (IsNonScaling(key))
                return match.Value;

            if (IsWeight(key))
            {
                var reduced = ReduceWeight(value, tier);
                return $"{key}: {reduced.ToString("0.0", CultureInfo.InvariantCulture)}{suffix}";
            }

            if (IsHaste(key))
            {
                var haste = (int)value + tier;
                return string.IsNullOrEmpty(sign)
                    ? $"{key}: {haste.ToString(CultureInfo.InvariantCulture)}{suffix}"
                    : $"{key}: {sign}{haste.ToString(CultureInfo.InvariantCulture)}{suffix}";
            }

            var scaled = ScaleStat(value, tier, isWeaponDamage: IsWeaponDamage(key));
            var asInt = (int)scaled;
            return string.IsNullOrEmpty(sign)
                ? $"{key}: {asInt.ToString(CultureInfo.InvariantCulture)}{suffix}"
                : $"{key}: {sign}{asInt.ToString(CultureInfo.InvariantCulture)}{suffix}";
        });

    /// <summary>Matches eqlegendstools scaledWeaponTooltipStatValue / DMG scaling.</summary>
    internal static double ScaleStat(double value, int tier, bool isWeaponDamage)
    {
        tier = Math.Clamp(tier, 0, 10);
        if (tier == 0) return value;

        if (value < 0)
            return Math.Min(0, value + tier);

        if (isWeaponDamage || Math.Abs(value) >= 10)
            return Math.Floor(value * (1.0 + tier * 0.10));

        // Small stats (|base| &lt; 10): +1 per tier (covers wiki "min +1 at tier start").
        return value + tier;
    }

    /// <summary>Compound −10% per tier, floored to one decimal each step (matches Sunderfury +4 = 4.8).</summary>
    internal static double ReduceWeight(double baseWeight, int tier)
    {
        tier = Math.Clamp(tier, 0, 10);
        var current = baseWeight;
        for (var i = 0; i < tier; i++)
        {
            current = Math.Floor(current * 0.9 * 10.0) / 10.0;
            if (current < 0.1) current = 0.1;
        }

        return current;
    }

    private static bool IsNonScaling(string key) =>
        IsDelay(key) ||
        key.Equals("Size", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Skill", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("Level", StringComparison.OrdinalIgnoreCase);

    private static bool IsDelay(string key) =>
        key.Equals("Delay", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Atk Delay", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeight(string key) =>
        key.Equals("WT", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Weight", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeaponDamage(string key) =>
        key.Equals("DMG", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Damage", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Base Dmg", StringComparison.OrdinalIgnoreCase);

    private static bool IsHaste(string key) =>
        key.Equals("Haste", StringComparison.OrdinalIgnoreCase);

    public static string TierLabel(int tier) => $"+{Math.Clamp(tier, 0, 10)}";

    public static string BonusSummary(int tier)
    {
        tier = Math.Clamp(tier, 0, 10);
        if (tier == 0) return "Base wiki stats (no upgrade).";
        var pct = tier * 10;
        return $"+{tier}: ~+{pct}% on larger stats/DMG, +{tier} on small stats & haste; weight −10%/tier (compound). Matched to in-game scaling.";
    }

    [GeneratedRegex(
        @"(?<key>AC|HP|MANA|ENDUR|END|ATK|Attack|STR|STA|AGI|DEX|WIS|INT|CHA|WT|Weight|DMG|Damage|Base\s+Dmg|Haste|Atk\s+Delay|Delay|SVP|SVM|SVC|SVF|SVD|SV\s+[A-Za-z]+)\s*:\s*(?<sign>\+?)(?<num>\d+(?:\.\d+)?)(?<suffix>%?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex StatTokenRegex();

    [GeneratedRegex(@"^\s*Ratio\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex RatioLineRegex();
}
