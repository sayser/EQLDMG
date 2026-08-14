using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public enum BisProcKind
{
    None,
    CombatDps,
    CombatUtility,
    Clicky
}

public sealed record BisProcInfo(
    string Name,
    BisProcKind Kind,
    double EstimatedHit,
    string Trigger);

public static partial class BisItemEffects
{
    /// <summary>
    /// Direct-damage combat proc hit sizes from eqlwiki spell pages (Classic).
    /// Unknown DPS-looking combat procs use <see cref="DefaultDpsProcHit"/>.
    /// </summary>
    private static readonly Dictionary<string, double> KnownDpsHits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fangol's Breath"] = 120
    };

    public const double DefaultDpsProcHit = 90;

    public static BisProcInfo Parse(string? stats)
    {
        if (string.IsNullOrWhiteSpace(stats))
            return None();

        // Finite charges (Ivandyr's Hoop: 6) mean the clicky is gone after use.
        // Unlimited / missing Charges still count if the effect deals damage.
        if (HasLimitedCharges(stats))
            return None();

        string? name = null;
        var trigger = "";
        var idx = stats.IndexOf("Effect:", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var rest = stats[(idx + "Effect:".Length)..].TrimStart();
            var end = rest.IndexOf('\n');
            if (end >= 0)
                rest = rest[..end].Trim();
            rest = rest.Replace('\r', ' ').Trim();
            var open = rest.IndexOf('(');
            if (open >= 0)
            {
                name = rest[..open].Trim();
                var close = rest.IndexOf(')', open + 1);
                trigger = close > open ? rest[(open + 1)..close] : rest[(open + 1)..];
            }
            else
            {
                var wt = rest.IndexOf(" WT:", StringComparison.OrdinalIgnoreCase);
                name = (wt >= 0 ? rest[..wt] : rest).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return None();

        var combat = trigger.Contains("Combat", StringComparison.OrdinalIgnoreCase);
        var clicky = trigger.Contains("Must Equip", StringComparison.OrdinalIgnoreCase) ||
                     (trigger.Contains("Casting Time", StringComparison.OrdinalIgnoreCase) && !combat) ||
                     trigger.Contains("Any Slot", StringComparison.OrdinalIgnoreCase);
        var damage = LooksLikeDamageProc(name) || KnownDpsHits.ContainsKey(name);

        // Only swing/combat triggers contribute PPM DPS. Damage-named clickies stay Clicky.
        if (combat && damage && !IsUtilityProc(name))
        {
            var hit = KnownDpsHits.GetValueOrDefault(name, DefaultDpsProcHit);
            return new BisProcInfo(name, BisProcKind.CombatDps, hit, trigger);
        }

        if (clicky && !combat)
            return new BisProcInfo(name, BisProcKind.Clicky, 0, trigger);

        if (!combat)
            return new BisProcInfo(name, BisProcKind.None, 0, trigger);

        return new BisProcInfo(name, BisProcKind.CombatUtility, 0, trigger);
    }

    /// <summary>
    /// True when the wiki stats list a finite charge count. "Unlimited" / "Infinite" are not limited.
    /// </summary>
    public static bool HasLimitedCharges(string? stats)
    {
        if (string.IsNullOrWhiteSpace(stats))
            return false;
        var match = LimitedChargesRegex().Match(stats);
        if (!match.Success)
            return false;
        var raw = match.Groups["n"].Value;
        return !raw.Equals("Unlimited", StringComparison.OrdinalIgnoreCase) &&
               !raw.Equals("Infinite", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDpsProc(BisProcInfo proc) => proc.Kind == BisProcKind.CombatDps;

    private static BisProcInfo None() => new("", BisProcKind.None, 0, "");

    private static bool IsUtilityProc(string name)
    {
        foreach (var token in UtilityTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool LooksLikeDamageProc(string name)
    {
        foreach (var token in DamageTokens)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static readonly string[] UtilityTokens =
    [
        "Rune", "Heal", "Regenerat", "Insects", "Drowsy", "Slow", "Snare", "Root",
        "Stun", "Mez", "Charm", "Fear", "Blind", "Lull", "Calm", "Pacify", "Harmony",
        "Haste", "Celerity", "Spirit of Wolf", "Invis", "Illusion", "Gate", "Portal",
        "Invigor", "Shield", "Barrier", "Ward", "Grow", "Shrink", "See Invisible",
        "Deadeye", "Inner Fire", "Skin like", "Endure", "Aegis"
    ];

    private static readonly string[] DamageTokens =
    [
        "Breath", "Strike", "Fire", "Flame", "Frost", "Ice", "Shock", "Bolt", "Blast",
        "Poison", "Venom", "Disease", "Drain", "Lifetap", "Magma", "Lightning", "Thunder",
        "Burst", "Burn", "Smash", "Harm", "Spike", "Thorn", "Lava", "Scorch", "Chill",
        "Toxin", "Plague", "Nuke", "Missile", "Wrath", "Fury", "Doom", "Cleave", "Rend",
        "Gore", "Impale", "Sting", "Bite", "Flare", "Spark", "Ember", "Avalanche",
        "Spirit Tap", "Life Tap", "Mana Tap", "Manatap", "Touch of"
    ];

    [GeneratedRegex(@"(?<![A-Za-z])Charges?:\s*(?<n>Unlimited|Infinite|\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LimitedChargesRegex();
}
