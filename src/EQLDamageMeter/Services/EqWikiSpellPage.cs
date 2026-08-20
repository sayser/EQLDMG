using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public enum SpellUpgradeFamily
{
    Buff,
    Debuff,
    DirectDamage,
    Heal,
    DotHot,
    CrowdControl,
    Pet,
    Summon,
    CombatInnate,
    Generic
}

/// <summary>
/// Parses eqlwiki Spellpage / Spellpagesmart templates and previews +0…+10 spell upgrades
/// using the per-tier bonuses documented on Spell Upgrade System.
/// </summary>
public static partial class EqWikiSpellPage
{
    public sealed record SpellInfo(
        string Name,
        string Description,
        string Classes,
        IReadOnlyList<string> Slots,
        string Skill,
        int? Mana,
        string Range,
        double? CastingTime,
        double? RecastTime,
        string DurationRaw,
        string TargetType,
        string SpellType,
        string Resist,
        SpellUpgradeFamily Family);

    public static bool TryParse(string wikitext, out SpellInfo? spell)
    {
        spell = null;
        if (string.IsNullOrWhiteSpace(wikitext)) return false;
        if (!wikitext.Contains("{{Spellpage", StringComparison.OrdinalIgnoreCase) &&
            !wikitext.Contains("{{Spellpagesmart", StringComparison.OrdinalIgnoreCase) &&
            !wikitext.Contains("| spellname", StringComparison.OrdinalIgnoreCase))
            return false;

        var fields = ParseFields(wikitext);
        if (!fields.TryGetValue("spellname", out var name) || string.IsNullOrWhiteSpace(name))
            return false;

        var slots = ParseSlots(fields.GetValueOrDefault("slots") ?? string.Empty);
        var spellType = CleanWiki(fields.GetValueOrDefault("spell_type") ?? string.Empty);
        var durationRaw = CleanWiki(fields.GetValueOrDefault("duration") ?? string.Empty);
        var family = Classify(name, spellType, durationRaw, slots,
            fields.GetValueOrDefault("description") ?? string.Empty);

        spell = new SpellInfo(
            CleanWiki(name),
            CleanDescription(fields.GetValueOrDefault("description") ?? string.Empty),
            FormatClasses(fields.GetValueOrDefault("classes") ?? string.Empty),
            slots,
            CleanWiki(fields.GetValueOrDefault("skill") ?? string.Empty),
            TryInt(fields.GetValueOrDefault("mana")),
            CleanWiki(fields.GetValueOrDefault("range") ?? string.Empty),
            TryDouble(fields.GetValueOrDefault("casting_time")),
            TryDouble(fields.GetValueOrDefault("recast_time")),
            durationRaw,
            CleanWiki(fields.GetValueOrDefault("target_type") ?? string.Empty),
            spellType,
            CleanWiki(fields.GetValueOrDefault("resist") ?? string.Empty),
            family);
        return true;
    }

    public static string Format(SpellInfo spell, int tier)
    {
        tier = Math.Clamp(tier, 0, 10);
        var rates = RatesFor(spell.Family);
        var lines = new List<string>
        {
            $"{spell.Name}  (Spell)",
            FamilyLabel(spell.Family)
        };
        if (spell.Classes.Length > 0)
            lines.Add($"Classes: {spell.Classes}");
        if (spell.Description.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add(spell.Description);
        }

        lines.Add(string.Empty);
        if (spell.Skill.Length > 0)
            lines.Add($"Skill: {spell.Skill}");
        if (spell.Mana is { } mana)
            lines.Add($"Mana: {ScaleReduce(mana, rates.ManaPct, tier, floor: 1)}");
        if (spell.Range.Length > 0)
            lines.Add($"Range: {spell.Range}");
        if (spell.CastingTime is { } cast)
            lines.Add($"Cast: {FormatSeconds(ScaleReduce(cast, rates.CastPct, tier, floor: 0.1))}s");
        if (spell.RecastTime is { } recast)
            lines.Add($"Recast: {FormatSeconds(ScaleReduce(recast, rates.RecastPct, tier, floor: 0.1))}s");
        lines.Add($"Duration: {ScaleDuration(spell.DurationRaw, rates.DurationPct, tier)}");
        if (spell.TargetType.Length > 0)
            lines.Add($"Target: {spell.TargetType}");
        if (spell.SpellType.Length > 0)
            lines.Add($"Type: {spell.SpellType}");
        if (spell.Resist.Length > 0)
            lines.Add($"Resist: {ScaleResist(spell.Resist, rates.ResistPerTier, tier)}");

        if (spell.Slots.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Effects:");
            foreach (var slot in spell.Slots)
                lines.Add(ScaleSlot(slot, rates, tier, spell.Family));
        }

        if (tier > 0)
        {
            if (spell.Family == SpellUpgradeFamily.Pet)
                lines.Add($"Pet level: +{tier} (capped at your level − 1)");
            if (spell.Family == SpellUpgradeFamily.Summon)
                lines.Add($"Summoned item: +{tier}");
            if (spell.Family == SpellUpgradeFamily.CombatInnate)
                lines.Add($"Proc spell rank: +{tier / 2}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string BonusSummary(int tier, SpellUpgradeFamily family)
    {
        tier = Math.Clamp(tier, 0, 10);
        if (tier == 0) return "Base wiki spell (no upgrade).";
        var rates = RatesFor(family);
        var parts = new List<string> { $"+{tier}" };
        if (rates.CastPct > 0)
            parts.Add($"cast −{Pct(rates.CastPct * tier)}");
        if (rates.ManaPct > 0)
            parts.Add($"mana −{Pct(rates.ManaPct * tier)}");
        if (rates.DurationPct > 0)
            parts.Add($"duration +{Pct(rates.DurationPct * tier)}");
        if (rates.DamagePct > 0)
            parts.Add($"dmg/heal +{Pct(rates.DamagePct * tier)}");
        if (rates.ResistPerTier > 0)
            parts.Add($"resist {(-rates.ResistPerTier * tier).ToString(CultureInfo.InvariantCulture)}");
        if (family == SpellUpgradeFamily.Pet)
            parts.Add($"+{tier} pet level");
        if (family == SpellUpgradeFamily.Summon)
            parts.Add($"summoned item +{tier}");
        return string.Join("; ", parts) + ". From Spell Upgrade System (preview).";
    }

    internal static SpellUpgradeFamily Classify(string name, string spellType, string duration,
        IReadOnlyList<string> slots, string description)
    {
        var blob = string.Join(' ', name, spellType, duration, description, string.Join(' ', slots));
        if (LooksLikeCrowdControl(blob))
            return SpellUpgradeFamily.CrowdControl;
        if (blob.Contains("warder", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(blob, @"\bsummon(?:s|ed)? (?:a )?(?:pet|skeleton|elemental|familiar|animation)\b",
                RegexOptions.IgnoreCase))
            return SpellUpgradeFamily.Pet;
        if (Regex.IsMatch(blob, @"summon(?:s|ed)? .+\b(item|dagger|arrow|drink|food|modulating)\b",
                RegexOptions.IgnoreCase))
            return SpellUpgradeFamily.Summon;
        if (blob.Contains("Add Proc", StringComparison.OrdinalIgnoreCase) ||
            blob.Contains("Combat Innate", StringComparison.OrdinalIgnoreCase))
            return SpellUpgradeFamily.CombatInnate;

        var instant = duration.Contains("instant", StringComparison.OrdinalIgnoreCase);
        var perTick = blob.Contains("per tick", StringComparison.OrdinalIgnoreCase);
        var heals = LooksLikeHeal(blob);
        var damages = LooksLikeDamage(blob);

        if (perTick && (heals || damages))
            return SpellUpgradeFamily.DotHot;
        if (instant && heals)
            return SpellUpgradeFamily.Heal;
        if (instant && damages)
            return SpellUpgradeFamily.DirectDamage;
        if (spellType.Contains("buff", StringComparison.OrdinalIgnoreCase) ||
            spellType.Contains("beneficial", StringComparison.OrdinalIgnoreCase))
            return SpellUpgradeFamily.Buff;
        if (spellType.Contains("detrimental", StringComparison.OrdinalIgnoreCase) ||
            spellType.Contains("debuff", StringComparison.OrdinalIgnoreCase))
            return SpellUpgradeFamily.Debuff;
        return damages ? SpellUpgradeFamily.DirectDamage : SpellUpgradeFamily.Buff;
    }

    private static bool LooksLikeCrowdControl(string blob) =>
        Regex.IsMatch(blob,
            @"\b(mesmeriz|mez\b|charm|stun|root|fear|pacify|lull|calm|blind|silence)\b",
            RegexOptions.IgnoreCase);

    private static bool LooksLikeHeal(string blob) =>
        blob.Contains("heal", StringComparison.OrdinalIgnoreCase) ||
        blob.Contains("restores", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(blob, @"increase (?:current )?hit points", RegexOptions.IgnoreCase);

    private static bool LooksLikeDamage(string blob) =>
        blob.Contains("damage", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(blob, @"decrease (?:current )?hit points", RegexOptions.IgnoreCase);

    private readonly record struct UpgradeRates(
        double CastPct, double ManaPct, double RecastPct, double DurationPct, double DamagePct, int ResistPerTier);

    private static UpgradeRates RatesFor(SpellUpgradeFamily family) => family switch
    {
        SpellUpgradeFamily.Buff => new(0.04, 0.02, 0, 0.10, 0, 0),
        SpellUpgradeFamily.Debuff => new(0.04, 0.04, 0, 0.10, 0, 15),
        SpellUpgradeFamily.DirectDamage => new(0.02, 0.02, 0.02, 0, 0.06, 15),
        SpellUpgradeFamily.Heal => new(0.04, 0.02, 0, 0, 0.03, 0),
        SpellUpgradeFamily.DotHot => new(0.04, 0.02, 0, 0.05, 0.03, 15),
        SpellUpgradeFamily.CrowdControl => new(0.02, 0.02, 0, 0.10, 0, 15),
        SpellUpgradeFamily.CombatInnate => new(0.02, 0.02, 0.02, 0.05, 0, 0),
        SpellUpgradeFamily.Pet => new(0.02, 0.02, 0, 0, 0, 0),
        SpellUpgradeFamily.Summon => new(0.02, 0.02, 0, 0, 0, 0),
        _ => new(0.02, 0.02, 0, 0, 0, 0)
    };

    private static string FamilyLabel(SpellUpgradeFamily family) => family switch
    {
        SpellUpgradeFamily.Buff => "Upgrade group: Buff",
        SpellUpgradeFamily.Debuff => "Upgrade group: Debuff",
        SpellUpgradeFamily.DirectDamage => "Upgrade group: Direct damage",
        SpellUpgradeFamily.Heal => "Upgrade group: Heal",
        SpellUpgradeFamily.DotHot => "Upgrade group: Damage / heal over time",
        SpellUpgradeFamily.CrowdControl => "Upgrade group: Crowd control",
        SpellUpgradeFamily.Pet => "Upgrade group: Pet",
        SpellUpgradeFamily.Summon => "Upgrade group: Summon item",
        SpellUpgradeFamily.CombatInnate => "Upgrade group: Combat innate",
        _ => "Upgrade group: Spell"
    };

    internal static Dictionary<string, string> ParseFields(string wikitext)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var buffer = new StringBuilder();
        foreach (var rawLine in wikitext.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var match = FieldStartRegex().Match(line);
            if (match.Success)
            {
                FlushField(fields, current, buffer);
                current = match.Groups[1].Value.Trim();
                buffer.Clear();
                buffer.Append(match.Groups[2].Value.Trim());
                continue;
            }

            if (current is not null)
            {
                if (line.Trim() == "}}" || line.TrimStart().StartsWith("==", StringComparison.Ordinal))
                {
                    FlushField(fields, current, buffer);
                    current = null;
                    continue;
                }
                if (buffer.Length > 0) buffer.Append('\n');
                buffer.Append(line.Trim());
            }
        }

        FlushField(fields, current, buffer);
        return fields;
    }

    private static void FlushField(Dictionary<string, string> fields, string? key, StringBuilder buffer)
    {
        if (key is null) return;
        fields[key] = buffer.ToString().Trim();
    }

    internal static IReadOnlyList<string> ParseSlots(string slotsField)
    {
        var slots = new List<string>();
        foreach (Match match in SlotRowRegex().Matches(slotsField))
        {
            var n = match.Groups[1].Value.Trim();
            var text = CleanWiki(match.Groups[2].Value);
            if (text.Length == 0) continue;
            slots.Add($"{n}: {text}");
        }
        return slots;
    }

    private static string FormatClasses(string raw)
    {
        var parts = new List<string>();
        foreach (Match match in ClassRowRegex().Matches(raw))
        {
            var cls = CleanWiki(match.Groups[1].Value);
            var level = match.Groups[2].Value.Trim();
            if (cls.Length == 0) continue;
            parts.Add(level.Length == 0 ? cls : $"{cls} {level}");
        }
        return string.Join(", ", parts);
    }

    private static string ScaleSlot(string slot, UpgradeRates rates, int tier, SpellUpgradeFamily family)
    {
        if (tier == 0 || rates.DamagePct <= 0)
            return slot;
        if (family is not (SpellUpgradeFamily.DirectDamage or SpellUpgradeFamily.DotHot
            or SpellUpgradeFamily.Heal))
            return slot;

        return SlotAmountRegex().Replace(slot, match =>
        {
            if (!TryParseNumber(match.Groups[1].Value, out var value))
                return match.Value;
            var scaled = Math.Max(1, Math.Floor(value * (1.0 + rates.DamagePct * tier)));
            return scaled.ToString("0", CultureInfo.InvariantCulture);
        });
    }

    private static string ScaleDuration(string raw, double pct, int tier)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        if (raw.Contains("instant", StringComparison.OrdinalIgnoreCase) || pct <= 0 || tier == 0)
            return raw;
        if (!TryParseDurationSeconds(raw, out var seconds) || seconds <= 0)
            return raw;
        var scaled = Math.Max(6, Math.Round(seconds * (1.0 + pct * tier)));
        return FormatDuration(scaled, raw);
    }

    internal static bool TryParseDurationSeconds(string raw, out double seconds)
    {
        seconds = 0;
        var text = raw.Trim();
        if (text.Length == 0 || text.Contains("instant", StringComparison.OrdinalIgnoreCase))
            return false;

        var hours = 0.0;
        var minutes = 0.0;
        var hourMatch = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*hour", RegexOptions.IgnoreCase);
        if (hourMatch.Success)
            hours = double.Parse(hourMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var minMatch = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*min", RegexOptions.IgnoreCase);
        if (minMatch.Success)
            minutes = double.Parse(minMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        if (hours > 0 || minutes > 0)
        {
            seconds = hours * 3600 + minutes * 60;
            return seconds > 0;
        }

        var secMatch = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*(?:sec|s)\b", RegexOptions.IgnoreCase);
        if (!secMatch.Success) return false;
        seconds = double.Parse(secMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        return seconds > 0;
    }

    private static string FormatDuration(double seconds, string original)
    {
        var total = Math.Max(1, (int)Math.Round(seconds));
        if (Regex.IsMatch(original, @"hour", RegexOptions.IgnoreCase))
        {
            var hours = total / 3600;
            var minutes = total % 3600 / 60;
            if (hours > 0 && minutes > 0)
                return $"{hours} hour(s) {minutes} min";
            if (hours > 0)
                return $"{hours} hour(s)";
        }

        if (Regex.IsMatch(original, @"min", RegexOptions.IgnoreCase))
            return $"{Math.Max(1, (int)Math.Round(total / 60.0))} min";
        return $"{total} sec";
    }

    private static string ScaleResist(string resist, int perTier, int tier)
    {
        if (perTier <= 0 || tier == 0) return resist;
        var match = ResistModRegex().Match(resist);
        if (!match.Success) return resist;
        var current = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var next = current - perTier * tier;
        return ResistModRegex().Replace(resist, $"({next.ToString(CultureInfo.InvariantCulture)})");
    }

    private static double ScaleReduce(double value, double pct, int tier, double floor)
    {
        if (pct <= 0 || tier == 0) return value;
        return Math.Max(floor, Math.Round(value * (1.0 - pct * tier), 2));
    }

    private static int ScaleReduce(int value, double pct, int tier, int floor)
    {
        if (pct <= 0 || tier == 0) return value;
        return Math.Max(floor, (int)Math.Floor(value * (1.0 - pct * tier)));
    }

    private static string FormatSeconds(double value) =>
        value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);

    private static string Pct(double fraction) =>
        $"{Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture)}%";

    private static string CleanDescription(string raw)
    {
        var text = CleanWiki(raw);
        var cutoff = text.IndexOf("Additional efficiency", StringComparison.OrdinalIgnoreCase);
        if (cutoff > 0) text = text[..cutoff];
        cutoff = text.IndexOf("See [[", StringComparison.OrdinalIgnoreCase);
        if (cutoff > 0) text = text[..cutoff];
        return Regex.Replace(text, @"\n{2,}", "\n").Trim();
    }

    private static string CleanWiki(string raw)
    {
        var value = WikiLinkRegex().Replace(raw, m => m.Groups[1].Value);
        value = HtmlTagRegex().Replace(value, string.Empty);
        value = value.Replace("'''", string.Empty).Replace("''", string.Empty).Trim();
        value = Regex.Replace(value, @"[ \t]+", " ");
        return value;
    }

    private static int? TryInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = Regex.Match(text, @"\d+");
        return digits.Success && int.TryParse(digits.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static double? TryDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text.Trim(), @"\d+(?:\.\d+)?");
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(@"^\s*\|\s*([A-Za-z][A-Za-z0-9_]*)\s*=\s*(.*)$")]
    private static partial Regex FieldStartRegex();

    [GeneratedRegex(@"\{\{\s*SpellSlotRow\s*\|\s*(\d+)\s*\|\s*([^}]+)\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex SlotRowRegex();

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:\|[^\]]+)?\]\]\s*-\s*Level\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ClassRowRegex();

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:\|[^\]]+)?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(?<=(?:by|Up To|up to)\s)(\d{1,3}(?:,\d{3})*|\d+)(?=\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SlotAmountRegex();

    [GeneratedRegex(@"\((-?\d+)\)")]
    private static partial Regex ResistModRegex();
}
