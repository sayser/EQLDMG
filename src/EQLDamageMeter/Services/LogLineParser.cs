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

    private static readonly Regex MissedAttack = new(
        @"^(?<source>.+?) (?:try|tries) to (?<ability>\S+) (?<target>.+?), but (?<result>.+?)!(?<flags>(?: \([^)]+\))*)$",
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

    private static readonly Regex ProtectedFromSpell = new(
        @"^(?<source>.+?) (?:try|tries) to cast a spell on (?<target>.+?), but (?<targetAgain>.+?) (?:is|are) protected\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string LocalPlayerName { get; } = localPlayerName;

    public bool TryParse(string line, out ParsedLogLine? parsed)
    {
        parsed = null;
        if (!TryParseEnvelope(line, out var timestamp, out var message)) return false;
        var damage = ParseDamage(timestamp, message);
        var healing = ParseHealing(timestamp, message);
        var outcome = ParseOutcome(timestamp, message);
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
        if ((match = ProtectedFromSpell.Match(message)).Success)
        {
            return new CombatOutcomeEvent(timestamp, NormalizeSource(match.Groups["source"].Value),
                NormalizeTarget(match.Groups["target"].Value), "Protected Spell",
                CombatOutcomeKind.DefensiveSpellAbsorb);
        }

        if ((match = MissedAttack.Match(message)).Success)
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
        Match match;
        if ((match = NamedSpell.Match(message)).Success)
        {
            return Create(timestamp, match, match.Groups["source"].Value, DamageCategory.Spell);
        }

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

        if ((match = UnattributedNonMelee.Match(message)).Success)
        {
            return Create(timestamp, match, UnattributedNonMeleeSource, DamageCategory.Spell, "Non-melee");
        }

        if ((match = Direct.Match(message)).Success)
        {
            return Create(timestamp, match, match.Groups["source"].Value, DamageCategory.Melee,
                NormalizeMeleeAbility(match.Groups["ability"].Value));
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

    private static bool IsCritical(Match match) =>
        match.Groups["flags"].Value.Contains("Critical", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMeleeAbility(string value) => value.ToLowerInvariant() switch
    {
        "slashes" or "slash" => "Slash",
        "hits" or "hit" => "Hit",
        "smites" or "smite" => "Smite",
        "claws" or "claw" => "Claw",
        "stings" or "sting" => "Sting",
        "slices" or "slice" => "Slice",
        "smashes" or "smash" => "Smash",
        "cleaves" or "cleave" => "Cleave",
        "bashes" or "bash" => "Bash",
        "kicks" or "kick" => "Kick",
        "punches" or "punch" => "Punch",
        "strikes" or "strike" => "Strike",
        "reaves" or "reave" => "Reave",
        "crushes" or "crush" => "Crush",
        "pierces" or "pierce" => "Pierce",
        "bites" or "bite" => "Bite",
        "mauls" or "maul" => "Maul",
        "backstabs" or "backstab" => "Backstab",
        "shoots" or "shoot" => "Shoot",
        "frenzies on" or "frenzy on" => "Frenzy",
        _ => value
    };
}
