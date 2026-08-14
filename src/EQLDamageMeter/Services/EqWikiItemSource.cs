using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public sealed record EqWikiItemSourceInfo(string Zone, string Mob, string Kind, string Display);

public static partial class EqWikiItemSource
{
    public static EqWikiItemSourceInfo Parse(string? wikitext)
    {
        if (string.IsNullOrWhiteSpace(wikitext))
            return Empty();

        var drops = ParsePlaceList(ExtractField(wikitext, "dropsfrom"));
        if (drops.Zones.Count > 0 || drops.Mobs.Count > 0)
            return Make("drop", Join(drops.Zones), Join(drops.Mobs));

        var sold = ParsePlaceList(ExtractField(wikitext, "soldby"));
        if (sold.Zones.Count > 0 || sold.Mobs.Count > 0)
            return Make("vendor", Join(sold.Zones), Join(sold.Mobs, vendor: true));

        var quests = ParseLinks(ExtractField(wikitext, "relatedquests"));
        if (quests.Count > 0)
            return Make("quest", "Quest", quests[0]);

        var notes = ExtractField(wikitext, "notes");
        var summoned = SummonedByRegex().Match(notes);
        if (summoned.Success)
            return Make("summoned", "Summoned", CleanLink(summoned.Groups[1].Value));

        var crafted = ParseLinks(ExtractField(wikitext, "playercrafted"));
        if (crafted.Count > 0)
            return Make("crafted", "Crafted", crafted[0]);

        return Empty();
    }

    public static EqWikiItemSourceInfo Empty() => new("", "", "", "");

    private static EqWikiItemSourceInfo Make(string kind, string zone, string mob)
    {
        var display = kind switch
        {
            "quest" => string.IsNullOrWhiteSpace(mob) ? "Quest" : $"Quest · {mob}",
            "summoned" => string.IsNullOrWhiteSpace(mob) ? "Summoned" : $"Summoned · {mob}",
            "crafted" => string.IsNullOrWhiteSpace(mob) ? "Crafted" : $"Crafted · {mob}",
            "vendor" when !string.IsNullOrWhiteSpace(zone) && !string.IsNullOrWhiteSpace(mob) => $"{zone} · {mob}",
            _ when !string.IsNullOrWhiteSpace(zone) && !string.IsNullOrWhiteSpace(mob) => $"{zone} · {mob}",
            _ when !string.IsNullOrWhiteSpace(zone) => zone,
            _ => mob
        };
        return new(zone, mob, kind, display);
    }

    /// <summary>
    /// Reads an Itempage template field. Stops only at a new <c>|fieldname =</c> line or <c>}}</c>,
    /// so wiki pipes inside <c>[[Page|Display]]</c> links are not treated as field separators.
    /// </summary>
    internal static string ExtractField(string wikitext, string field)
    {
        // Find "|field =" allowing leading spaces on the line.
        var match = FieldNeedleRegex(field).Match(wikitext);
        if (!match.Success)
            return "";

        var from = match.Index + match.Length;
        var rest = wikitext[from..];
        var end = NextFieldOrEndRegex().Match(rest);
        var raw = end.Success ? rest[..end.Index] : rest;
        return raw.Trim();
    }

    private static Regex FieldNeedleRegex(string field) =>
        new(@"^[ \t]*\|\s*" + Regex.Escape(field) + @"\s*=\s*",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static (List<string> Zones, List<string> Mobs) ParsePlaceList(string raw)
    {
        var zones = new List<string>();
        var mobs = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return (zones, mobs);

        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.StartsWith("This item", StringComparison.OrdinalIgnoreCase))
                continue;
            var bullet = trimmed.StartsWith('*');
            var text = bullet ? trimmed.TrimStart('*').Trim() : trimmed;
            text = FlattenLinks(text);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (bullet)
                mobs.Add(text);
            else
                zones.Add(text);
        }

        return (zones, mobs);
    }

    private static List<string> ParseLinks(string raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return list;
        foreach (Match match in WikiLinkRegex().Matches(raw))
        {
            var text = DisplayFromWikiMatch(match);
            if (text.Length > 0)
                list.Add(text);
        }

        if (list.Count > 0)
            return list;

        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var text = FlattenLinks(line.Trim().TrimStart('*').Trim());
            if (text.Length > 0 && !text.StartsWith("This item", StringComparison.OrdinalIgnoreCase))
                list.Add(text);
        }

        return list;
    }

    private static string FlattenLinks(string text)
    {
        text = WikiLinkRegex().Replace(text, match => DisplayFromWikiMatch(match));
        text = text.Replace("'''", "").Replace("''", "").Trim();
        return text;
    }

    internal static string CleanLink(string raw)
    {
        var match = WikiLinkRegex().Match(raw.Trim());
        return match.Success ? DisplayFromWikiMatch(match) : raw.Trim().Replace('_', ' ').Trim();
    }

    private static string DisplayFromWikiMatch(Match match)
    {
        var text = match.Groups["display"].Success && match.Groups["display"].Value.Length > 0
            ? match.Groups["display"].Value
            : match.Groups["page"].Value;
        return text.Replace('_', ' ').Trim();
    }

    private static string Join(IReadOnlyList<string> parts, bool vendor = false)
    {
        if (parts.Count == 0)
            return "";
        var shown = parts.Take(2).ToArray();
        var text = string.Join(", ", shown);
        if (vendor && shown.Length == 1 && !shown[0].StartsWith("Vendor", StringComparison.OrdinalIgnoreCase))
            text = shown[0];
        if (parts.Count > 2)
            text += $" +{parts.Count - 2}";
        return text;
    }

    // Next Itempage field at line start, or template end.
    [GeneratedRegex(@"\n[ \t]*\|[a-zA-Z_][a-zA-Z0-9_]*\s*=|\n[ \t]*\}\}",
        RegexOptions.CultureInvariant)]
    private static partial Regex NextFieldOrEndRegex();

    [GeneratedRegex(@"\[\[(?<page>[^\]|#]+)(?:\|(?<display>[^\]]+))?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"Summoned by\s*\[\[([^\]]+)\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex SummonedByRegex();
}
