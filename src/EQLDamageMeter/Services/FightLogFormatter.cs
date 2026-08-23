using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using EQLDamageMeter.Controls;

namespace EQLDamageMeter.Services;

public readonly record struct FightLogEntry(DateTime Timestamp, string Message);

/// <summary>Splits fight log messages into color-coded, styled segments for display.</summary>
public static class FightLogFormatter
{
    private static readonly Brush TimestampBrush = Freeze(0x5A, 0x78, 0x8E);
    private static readonly Brush DefaultBrush = Freeze(0xB0, 0xC4, 0xD4);
    private static readonly Brush PlayerBrush = Freeze(0xF2, 0xF8, 0xFF);
    private static readonly Brush EnemyBrush = Freeze(0xFF, 0x9A, 0x7A);
    private static readonly Brush DamageBrush = Freeze(0xFF, 0xD0, 0x6A);
    private static readonly Brush CritBrush = Freeze(0xFF, 0x6C, 0x91);
    private static readonly Brush HealBrush = Freeze(0x4E, 0xE8, 0x8A);
    private static readonly Brush MissBrush = Freeze(0xFF, 0xA0, 0x5C);
    /// <summary>One color for all spells (shown as {Spell Name}).</summary>
    private static readonly Brush SpellBrush = Freeze(0xC9, 0xA0, 0xFF);
    /// <summary>One color for all melee abilities (shown as {slash}).</summary>
    private static readonly Brush AbilityBrush = Freeze(0x3E, 0xE8, 0xD8);
    private static readonly Brush FlagBrush = Freeze(0xE0, 0xD4, 0xFF);
    private static readonly Brush SystemBrush = Freeze(0x7A, 0x96, 0xAA);

    private static readonly Regex DamageAmount = new(
        @"\b(?<n>\d{1,7})(?= points? of damage| damage\b| points? of \S+ damage)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HealAmount = new(
        @"\b(?<n>\d{1,7})(?= points? of healing| hit points?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Flag = new(
        @"\((?<flag>[^)]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex SpellFromYour = new(
        @"\bfrom your (?<spell>[A-Za-z][A-Za-z0-9' `.-]{1,48}?)(?=\.|!|\s*\(|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpellDamageBy = new(
        @"\b(?:points? of (?:\S+ )?damage|was hit|is hit|are hit|hits?|hit)\s+by (?<spell>[A-Za-z][A-Za-z0-9' `.-]{1,48}?)(?=\.|!|\s*\(|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpellBegins = new(
        @"\bbegins? (?:to )?(?:cast(?:ing)?|sing(?:ing)?)\s+(?<spell>[A-Za-z][A-Za-z0-9' `.-]{1,48}?)(?=\.|!|\s*\(|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpellWears = new(
        @"\b(?:Your|your) (?<spell>[A-Za-z][A-Za-z0-9' `.-]{1,40}?) (?:spell has worn off|has worn off)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MeleeVerb = new(
        @"\b(?<verb>slash(?:es)?|bash(?:es)?|kick(?:s)?|cleave(?:s)?|punch(?:es)?|strike(?:s)?|hit|hits|pierce(?:s)?|crush(?:es)?|backstab(?:s)?|shoot(?:s)?|frenzy|frenzies|claw(?:s)?|bite(?:s)?|maul(?:s)?|smash(?:es)?|slice(?:s)?|sting(?:s)?|reave(?:s)?|smite(?:s)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MissWord = new(
        @"\b(?<m>miss(?:es)?|dodges?|parries?|blocks?|ripostes?|resisted|fizzles?|absorbed)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EnemyArticle = new(
        @"\b(?<enemy>(?:an?|the)\s+[A-Za-z][A-Za-z0-9'-]*(?:\s+[A-Za-z][A-Za-z0-9'-]*){0,4})(?=\s+(?:tries|is|was|hits|hit|slashes|slash|bashes|bash|kicks|kick|misses|miss|has|have|begins|casts|cast|takes|died|slain|for)\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SystemLine = new(
        @"^(?:Your target is too far away|You can't see your target|You must face your target|You are too far away|That spell will not take hold)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> CritFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Critical", "Critical Flurry", "Finishing Blow", "Crippling Blow", "Crushing Blow",
        "Slashing Blow", "Stunning Blow", "Riposte Critical", "Riposte Finishing Blow"
    };

    public static IReadOnlyList<FightLogSegment> Format(
        FightLogEntry entry,
        string? localPlayerName = null,
        IReadOnlyList<string>? knownActors = null)
    {
        var segments = new List<FightLogSegment>();
        segments.Add(new FightLogSegment
        {
            Text = entry.Timestamp.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "  ",
            Foreground = TimestampBrush,
            Style = FightLogSegmentStyle.Timestamp
        });

        var message = entry.Message;
        if (string.IsNullOrEmpty(message))
        {
            segments.Add(new FightLogSegment { Text = message, Foreground = DefaultBrush });
            return segments;
        }

        if (SystemLine.IsMatch(message))
        {
            segments.Add(new FightLogSegment { Text = message, Foreground = SystemBrush });
            return segments;
        }

        var marks = new List<Mark>();
        AddMatches(marks, DamageAmount.Matches(message), m => (m.Groups["n"], DamageBrush, true, FightLogSegmentStyle.Normal, false));
        AddMatches(marks, HealAmount.Matches(message), m => (m.Groups["n"], HealBrush, true, FightLogSegmentStyle.Normal, false));
        AddMatches(marks, MeleeVerb.Matches(message), m => (m.Groups["verb"], AbilityBrush, true, FightLogSegmentStyle.Ability, true));
        AddMatches(marks, MissWord.Matches(message), m => (m.Groups["m"], MissBrush, true, FightLogSegmentStyle.Normal, false));
        AddMatches(marks, SpellFromYour.Matches(message), m => (m.Groups["spell"], SpellBrush, true, FightLogSegmentStyle.Spell, true));
        AddMatches(marks, SpellDamageBy.Matches(message), m => (m.Groups["spell"], SpellBrush, true, FightLogSegmentStyle.Spell, true));
        AddMatches(marks, SpellBegins.Matches(message), m => (m.Groups["spell"], SpellBrush, true, FightLogSegmentStyle.Spell, true));
        AddMatches(marks, SpellWears.Matches(message), m => (m.Groups["spell"], SpellBrush, true, FightLogSegmentStyle.Spell, true));
        AddMatches(marks, EnemyArticle.Matches(message), m => (m.Groups["enemy"], EnemyBrush, true, FightLogSegmentStyle.Actor, false));

        foreach (Match match in Flag.Matches(message))
        {
            var flag = match.Groups["flag"].Value;
            var brush = CritFlags.Contains(flag) || flag.Contains("Critical", StringComparison.OrdinalIgnoreCase)
                ? CritBrush
                : FlagBrush;
            marks.Add(new Mark(match.Index, match.Index + match.Length, brush, true, FightLogSegmentStyle.Normal, false));
        }

        if (!string.IsNullOrWhiteSpace(localPlayerName))
        {
            var localBrush = CombatantColorPalette.ForName(localPlayerName);
            MarkLiteral(marks, message, localPlayerName!, localBrush, true, FightLogSegmentStyle.Actor);
        }

        MarkLiteral(marks, message, "You", PlayerBrush, true, FightLogSegmentStyle.Actor);
        MarkLiteral(marks, message, "YOU", PlayerBrush, true, FightLogSegmentStyle.Actor);

        if (knownActors is not null)
        {
            foreach (var actor in knownActors
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(name => name.Length))
            {
                if (!string.IsNullOrWhiteSpace(localPlayerName) &&
                    actor.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
                    continue;
                MarkLiteral(marks, message, actor, CombatantColorPalette.ForName(actor), true,
                    FightLogSegmentStyle.Actor);
            }
        }

        marks.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var merged = MergeMarks(marks, message.Length);

        var cursor = 0;
        foreach (var mark in merged)
        {
            if (mark.Start > cursor)
            {
                segments.Add(new FightLogSegment
                {
                    Text = message[cursor..mark.Start],
                    Foreground = DefaultBrush
                });
            }

            var text = message[mark.Start..mark.End];
            if (mark.WrapBraces)
                text = "{" + text + "}";

            segments.Add(new FightLogSegment
            {
                Text = text,
                Foreground = mark.Brush,
                Bold = mark.Bold,
                Style = mark.Style
            });
            cursor = mark.End;
        }

        if (cursor < message.Length)
        {
            segments.Add(new FightLogSegment
            {
                Text = message[cursor..],
                Foreground = DefaultBrush
            });
        }

        return segments;
    }

    private readonly record struct Mark(
        int Start, int End, Brush Brush, bool Bold, FightLogSegmentStyle Style, bool WrapBraces);

    private static void AddMatches(List<Mark> marks, MatchCollection matches,
        Func<Match, (Group Group, Brush Brush, bool Bold, FightLogSegmentStyle Style, bool WrapBraces)> pick)
    {
        foreach (Match match in matches)
        {
            var (group, brush, bold, style, wrap) = pick(match);
            if (!group.Success || group.Length == 0) continue;
            marks.Add(new Mark(group.Index, group.Index + group.Length, brush, bold, style, wrap));
        }
    }

    private static void MarkLiteral(List<Mark> marks, string message, string literal, Brush brush, bool bold,
        FightLogSegmentStyle style)
    {
        var start = 0;
        while (start < message.Length)
        {
            var index = message.IndexOf(literal, start, StringComparison.Ordinal);
            if (index < 0) break;
            var beforeOk = index == 0 || !char.IsLetterOrDigit(message[index - 1]);
            var after = index + literal.Length;
            var afterOk = after >= message.Length || !char.IsLetterOrDigit(message[after]);
            if (beforeOk && afterOk)
                marks.Add(new Mark(index, after, brush, bold, style, false));
            start = index + literal.Length;
        }
    }

    private static List<Mark> MergeMarks(List<Mark> marks, int length)
    {
        var merged = new List<Mark>();
        foreach (var mark in marks)
        {
            if (mark.Start < 0 || mark.End > length || mark.Start >= mark.End) continue;
            if (merged.Count > 0 && mark.Start < merged[^1].End) continue;
            merged.Add(mark);
        }
        return merged;
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
