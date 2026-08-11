using System.Globalization;
using System.Text.RegularExpressions;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class LogLineParser(string localPlayerName)
{
    public const string UnattributedNonMeleeSource = "Unattributed non-melee";
    public const string UnattributedDamageOverTimeSource = "Unattributed damage-over-time";
    private static readonly CultureInfo LogCulture = CultureInfo.GetCultureInfo("en-US");
    private const string Flags = @"(?<flags>(?: \([^)]+\))*)$";

    private static readonly Regex Envelope = new(
        @"^\[(?<stamp>[^]]+)\] (?<message>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex NamedSpell = new(
        @"^(?<source>.+?) hit (?<target>.+?) for (?<amount>\d+) points? of (?<type>\S+) damage by (?<ability>.+?)\." + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DamageOverTimeByActor = new(
        @"^(?<target>.+?) (?:has|have) taken (?<amount>\d+) damage from (?<ability>.+?) by (?<source>.+?)\." + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UnattributedDamageOverTime = new(
        @"^(?<target>.+?) (?:has|have) taken (?<amount>\d+) damage by (?<ability>.+?)\." + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DamageOverTimeByYou = new(
        @"^(?<target>.+?) has taken (?<amount>\d+) damage from your (?<ability>.+?)\." + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Thorns = new(
        @"^(?<target>.+?) (?:is|are) pierced by (?:(?<self>YOUR)|(?<source>.+?)(?:'|`|\u2019)s) thorns for (?<amount>\d+) points? of non-melee damage[.!]" + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Flames = new(
        @"^(?<target>.+?) (?:is|are) burned by (?:(?<self>YOUR)|(?<source>.+?)(?:'|`|\u2019)s) flames for (?<amount>\d+) points? of non-melee damage[.!]" + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Frost = new(
        @"^(?<target>.+?) (?:is|are) tormented by (?:(?<self>YOUR)|(?<source>.+?)(?:'|`|\u2019)s) frost for (?<amount>\d+) points? of non-melee damage[.!]" + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UnattributedNonMelee = new(
        @"^(?<target>You) (?:was|were) hit by non-melee for (?<amount>\d+) damage\." + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Direct = new(
        @"^(?<source>.+?) (?<ability>frenzies on|frenzy on|hits|hit|smites|smite|claws|claw|stings|sting|slices|slice|smashes|smash|slashes|slash|cleaves|cleave|bashes|bash|kicks|kick|punches|punch|strikes|strike|reaves|reave|crushes|crush|pierces|pierce|bites|bite|mauls|maul|backstabs|backstab|shoots|shoot) (?<target>.+?) for (?<amount>\d+) points? of damage\." + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Healing = new(
        @"^(?<source>.+?) healed (?<target>.+?)(?: (?<hot>over time))? for (?<amount>\d+)(?: \((?<potential>\d+)\))? hit points by (?<ability>.+?)\." + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UnnamedHealing = new(
        @"^(?<source>.+?) healed (?<target>.+?)(?: (?<hot>over time))? for (?<amount>\d+)(?: \((?<potential>\d+)\))? hit points\." + Flags,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "frenzy" is the one attack whose verb carries a preposition ("tries to frenzy
    // on a kobold"), so it must be matched before the single-word verb alternative
    // or the preposition is captured as part of the defender's name.
    private static readonly Regex MissedAttack = new(
        @"^(?<source>.+?) (?:try|tries) to (?<ability>frenzies on|frenzy on|\S+) (?<target>.+?), but (?<result>.+?)!(?<flags>(?: \([^)]+\))*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LocalFizzle = new(
        @"^Your (?<ability>.+?) spell fizzles!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OtherFizzle = new(
        @"^(?<source>.+?)(?:'|`|\u2019)s (?<ability>.+?) spell fizzles!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LocalResist = new(
        @"^(?<target>.+?) resisted your (?<ability>.+?)!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OtherResist = new(
        @"^(?<target>.+?) resisted (?<source>.+?)(?:'|`|\u2019)s (?<ability>.+?)!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IncomingSpellResist = new(
        @"^(?<target>You|.+?) resist(?:s)? (?<source>.+?)(?:'|`|\u2019)s (?<ability>.+?)!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AbsorbedReactiveDamage = new(
        @"^(?<target>.+?)(?:'|`|\u2019)s magical skin absorbs the damage of (?:(?<self>YOUR)|(?<source>.+?)(?:'|`|\u2019)s) (?<ability>thorns|flames|frost)\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Stun lines name the effect but never the caster, so the source is left empty
    // for the encounter tracker to infer from the cast that preceded it.
    private static readonly Regex Stunned = new(
        @"^(?<target>.+?) (?:is|are) stunned(?: by (?<ability>.+?))?[.!]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StunDiminished = new(
        @"^Your target has been stunned too recently for your stun to have full effect\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProtectedFromSpell = new(
        @"^(?<source>.+?) (?:try|tries) to cast a spell on (?<target>.+?), but (?<targetAgain>.+?) (?:is|are) protected\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Ability name given to a "tries to cast a spell on ..., but ... is protected"
    /// outcome. The game emits that line for blocked beneficial spells as well as
    /// hostile ones, so consumers must qualify it before treating it as mitigation.
    /// </summary>
    public const string ProtectedSpellAbility = "Protected Spell";

    public string LocalPlayerName { get; } = localPlayerName;

    public bool TryParse(string line, out ParsedLogLine? parsed)
    {
        parsed = null;
        if (!TryParseEnvelope(line, out var timestamp, out var message)) return false;
        // A single message is never more than one kind of event, so each classifier
        // is skipped once an earlier one has claimed the line.
        var damage = ParseDamage(timestamp, message);
        var healing = damage is null ? ParseHealing(timestamp, message) : null;
        var outcome = damage is null && healing is null ? ParseOutcome(timestamp, message) : null;
        parsed = new ParsedLogLine(timestamp, message, damage, healing, outcome);
        return true;
    }

    public bool TryParseEnvelope(string line, out DateTime timestamp, out string message)
    {
        timestamp = default;
        message = string.Empty;
        var envelope = Envelope.Match(line);
        if (!envelope.Success ||
            !DateTime.TryParseExact(envelope.Groups["stamp"].Value, "ddd MMM dd HH:mm:ss yyyy", LogCulture,
                DateTimeStyles.None, out timestamp)) return false;

        message = envelope.Groups["message"].Value;
        return true;
    }

    private HealingEvent? ParseHealing(DateTime timestamp, string message)
    {
        if (!message.Contains(" healed ", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Healing.Match(message);
        var ability = match.Success ? match.Groups["ability"].Value : "Unspecified Healing";
        if (!match.Success)
        {
            match = UnnamedHealing.Match(message);
            if (!match.Success) return null;
        }
        var source = NormalizeSource(match.Groups["source"].Value);
        var rawTarget = match.Groups["target"].Value;
        var target = rawTarget.Equals("himself", StringComparison.OrdinalIgnoreCase) ||
                     rawTarget.Equals("herself", StringComparison.OrdinalIgnoreCase) ||
                      rawTarget.Equals("themselves", StringComparison.OrdinalIgnoreCase) ||
                      rawTarget.Equals("itself", StringComparison.OrdinalIgnoreCase) ||
                      rawTarget.Equals("yourself", StringComparison.OrdinalIgnoreCase)
            ? source
            : NormalizeTarget(rawTarget);
        var amount = int.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
        var potential = match.Groups["potential"].Success
            ? int.Parse(match.Groups["potential"].Value, CultureInfo.InvariantCulture)
            : amount;
        return new HealingEvent(timestamp, source, target, amount, potential,
            ability, match.Groups["hot"].Success, IsCritical(match));
    }

    private CombatOutcomeEvent? ParseOutcome(DateTime timestamp, string message)
    {
        Match match;
        var hasFailureClause = message.Contains(", but ", StringComparison.OrdinalIgnoreCase);
        var hasStun = message.Contains("stun", StringComparison.OrdinalIgnoreCase);
        if (!hasFailureClause && !hasStun &&
            !message.Contains("fizzles!", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("resist", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("absorbs ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (hasStun)
        {
            if (StunDiminished.IsMatch(message))
            {
                return new CombatOutcomeEvent(timestamp, LocalPlayerName, null, "Stun",
                    CombatOutcomeKind.StunDiminished);
            }

            if ((match = Stunned.Match(message)).Success)
            {
                var stunAbility = match.Groups["ability"].Success ? match.Groups["ability"].Value : "Stun";
                return new CombatOutcomeEvent(timestamp, string.Empty,
                    NormalizeTarget(match.Groups["target"].Value), stunAbility, CombatOutcomeKind.StunApplied);
            }
        }

        if (hasFailureClause && (match = ProtectedFromSpell.Match(message)).Success)
        {
            return new CombatOutcomeEvent(timestamp, NormalizeSource(match.Groups["source"].Value),
                NormalizeTarget(match.Groups["target"].Value), ProtectedSpellAbility,
                CombatOutcomeKind.DefensiveSpellAbsorb);
        }

        if (hasFailureClause && (match = MissedAttack.Match(message)).Success)
        {
            var source = NormalizeSource(match.Groups["source"].Value);
            var target = NormalizeTarget(match.Groups["target"].Value);
            var result = match.Groups["result"].Value;
            var kind = result.Contains("dodge", StringComparison.OrdinalIgnoreCase) ? CombatOutcomeKind.DefensiveDodge
                : result.Contains("parr", StringComparison.OrdinalIgnoreCase) ? CombatOutcomeKind.DefensiveParry
                : result.Contains("block", StringComparison.OrdinalIgnoreCase) ? CombatOutcomeKind.DefensiveBlock
                : result.Contains("riposte", StringComparison.OrdinalIgnoreCase) ? CombatOutcomeKind.DefensiveRiposte
                : result.Contains("absorbs", StringComparison.OrdinalIgnoreCase) ? CombatOutcomeKind.DefensiveAbsorb
                : CombatOutcomeKind.MissedAttack;
            return new CombatOutcomeEvent(timestamp, source, target,
                NormalizeMeleeAbility(match.Groups["ability"].Value), kind);
        }

        if ((match = LocalFizzle.Match(message)).Success)
        {
            return new CombatOutcomeEvent(timestamp, LocalPlayerName, null, match.Groups["ability"].Value,
                CombatOutcomeKind.SpellFizzle);
        }

        if ((match = OtherFizzle.Match(message)).Success)
        {
            return new CombatOutcomeEvent(timestamp, NormalizeSource(match.Groups["source"].Value), null,
                match.Groups["ability"].Value, CombatOutcomeKind.SpellFizzle);
        }

        if ((match = LocalResist.Match(message)).Success)
        {
            return new CombatOutcomeEvent(timestamp, LocalPlayerName, match.Groups["target"].Value,
                match.Groups["ability"].Value, CombatOutcomeKind.SpellResist);
        }

        if ((match = OtherResist.Match(message)).Success)
        {
            return new CombatOutcomeEvent(timestamp, NormalizeSource(match.Groups["source"].Value),
                match.Groups["target"].Value, match.Groups["ability"].Value, CombatOutcomeKind.SpellResist);
        }


        if ((match = IncomingSpellResist.Match(message)).Success)
        {
            return new CombatOutcomeEvent(timestamp, NormalizeSource(match.Groups["source"].Value),
                NormalizeTarget(match.Groups["target"].Value), match.Groups["ability"].Value,
                CombatOutcomeKind.DefensiveSpellResist);
        }

        if ((match = AbsorbedReactiveDamage.Match(message)).Success)
        {
            var source = match.Groups["self"].Success
                ? LocalPlayerName
                : NormalizeSource(match.Groups["source"].Value);
            return new CombatOutcomeEvent(timestamp, source, NormalizeTarget(match.Groups["target"].Value),
                match.Groups["ability"].Value, match.Groups["self"].Success
                    ? CombatOutcomeKind.SpellResist
                    : CombatOutcomeKind.DefensiveSpellAbsorb);
        }

        return null;
    }

    private DamageEvent? ParseDamage(DateTime timestamp, string message)
    {
        // Every damage form quantifies an amount, and the two families are told apart
        // by "... has taken N ..." versus "... for N ...". Splitting on those literals
        // keeps chat and system chatter away from nine backtracking patterns, and the
        // patterns within each family run most-frequent-first.
        if (!ContainsDigit(message)) return null;

        Match match;
        if (message.Contains(" taken ", StringComparison.OrdinalIgnoreCase))
        {
            if ((match = DamageOverTimeByYou.Match(message)).Success)
            {
                return Create(timestamp, match, LocalPlayerName, DamageCategory.DamageOverTime);
            }

            if ((match = DamageOverTimeByActor.Match(message)).Success)
            {
                return Create(timestamp, match, match.Groups["source"].Value, DamageCategory.DamageOverTime);
            }

            if ((match = UnattributedDamageOverTime.Match(message)).Success)
            {
                return Create(timestamp, match, UnattributedDamageOverTimeSource, DamageCategory.DamageOverTime);
            }
        }

        // Unattributed incoming: "You were hit by non-melee for N damage." (no "points of").
        if (message.Contains("hit by non-melee", StringComparison.OrdinalIgnoreCase) &&
            (match = UnattributedNonMelee.Match(message)).Success)
        {
            return Create(timestamp, match, UnattributedNonMeleeSource, DamageCategory.Spell, "Non-melee");
        }

        // Melee/spell/reactive hits all use "for N points of … damage". Gate before the
        // heavy Direct/NamedSpell regexes so chat/auction lines with " for " are skipped.
        if (!message.Contains(" for ", StringComparison.OrdinalIgnoreCase) ||
            !message.Contains("points of", StringComparison.OrdinalIgnoreCase) ||
            !message.Contains("damage", StringComparison.OrdinalIgnoreCase))
            return null;

        if ((match = Direct.Match(message)).Success)
        {
            return Create(timestamp, match, match.Groups["source"].Value, DamageCategory.Melee,
                NormalizeMeleeAbility(match.Groups["ability"].Value));
        }

        if ((match = NamedSpell.Match(message)).Success)
        {
            return Create(timestamp, match, match.Groups["source"].Value, DamageCategory.Spell);
        }

        if ((match = Thorns.Match(message)).Success)
        {
            var source = match.Groups["self"].Success ? LocalPlayerName : match.Groups["source"].Value;
            return Create(timestamp, match, source, DamageCategory.Reactive, "Thorns");
        }

        if ((match = Flames.Match(message)).Success)
        {
            var source = match.Groups["self"].Success ? LocalPlayerName : match.Groups["source"].Value;
            return Create(timestamp, match, source, DamageCategory.Reactive, "Flames");
        }

        if ((match = Frost.Match(message)).Success)
        {
            var source = match.Groups["self"].Success ? LocalPlayerName : match.Groups["source"].Value;
            return Create(timestamp, match, source, DamageCategory.Reactive, "Frost");
        }

        return null;
    }

    private DamageEvent Create(DateTime timestamp, Match match, string source, DamageCategory category,
        string? forcedAbility = null)
    {
        source = NormalizeSource(source);
        return new DamageEvent(
            timestamp,
            source,
            NormalizeTarget(match.Groups["target"].Value),
            int.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture),
            forcedAbility ?? match.Groups["ability"].Value,
            category,
            IsCritical(match));
    }

    private string NormalizeSource(string source) =>
        source.Equals("You", StringComparison.OrdinalIgnoreCase) ? LocalPlayerName : source;

    private string NormalizeTarget(string target) =>
        target.Equals("You", StringComparison.OrdinalIgnoreCase) ? LocalPlayerName : target;

    private static bool ContainsDigit(string value)
    {
        foreach (var character in value)
        {
            if (character is >= '0' and <= '9') return true;
        }

        return false;
    }

    private static bool IsCritical(Match match) =>
        match.Groups["flags"].Value.Contains("Critical", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMeleeAbility(string value)
    {
        if (value.Equals("slashes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("slash", StringComparison.OrdinalIgnoreCase)) return "Slash";
        if (value.Equals("hits", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("hit", StringComparison.OrdinalIgnoreCase)) return "Hit";
        if (value.Equals("smites", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("smite", StringComparison.OrdinalIgnoreCase)) return "Smite";
        if (value.Equals("claws", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("claw", StringComparison.OrdinalIgnoreCase)) return "Claw";
        if (value.Equals("stings", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("sting", StringComparison.OrdinalIgnoreCase)) return "Sting";
        if (value.Equals("slices", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("slice", StringComparison.OrdinalIgnoreCase)) return "Slice";
        if (value.Equals("smashes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("smash", StringComparison.OrdinalIgnoreCase)) return "Smash";
        if (value.Equals("cleaves", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("cleave", StringComparison.OrdinalIgnoreCase)) return "Cleave";
        if (value.Equals("bashes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("bash", StringComparison.OrdinalIgnoreCase)) return "Bash";
        if (value.Equals("kicks", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("kick", StringComparison.OrdinalIgnoreCase)) return "Kick";
        if (value.Equals("punches", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("punch", StringComparison.OrdinalIgnoreCase)) return "Punch";
        if (value.Equals("strikes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("strike", StringComparison.OrdinalIgnoreCase)) return "Strike";
        if (value.Equals("reaves", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("reave", StringComparison.OrdinalIgnoreCase)) return "Reave";
        if (value.Equals("crushes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("crush", StringComparison.OrdinalIgnoreCase)) return "Crush";
        if (value.Equals("pierces", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("pierce", StringComparison.OrdinalIgnoreCase)) return "Pierce";
        if (value.Equals("bites", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("bite", StringComparison.OrdinalIgnoreCase)) return "Bite";
        if (value.Equals("mauls", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("maul", StringComparison.OrdinalIgnoreCase)) return "Maul";
        if (value.Equals("backstabs", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("backstab", StringComparison.OrdinalIgnoreCase)) return "Backstab";
        if (value.Equals("shoots", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("shoot", StringComparison.OrdinalIgnoreCase)) return "Shoot";
        if (value.Equals("frenzies on", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("frenzy on", StringComparison.OrdinalIgnoreCase)) return "Frenzy";
        return value;
    }
}
