using System.Globalization;
using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public static partial class EqWikiLinks
{
    public const string BaseUrl = "https://eqlwiki.com/";

    public static string ForMob(string mobName)
    {
        // Prefer the log name with article kept ("a spite golem" → "A_spite_golem");
        // eqlwiki redirects handle capitalization. Stripping the article often 404s.
        var name = WhitespaceRegex().Replace(mobName.Trim().Replace('`', '\''), " ").Trim();
        if (name.Length == 0) return BaseUrl;
        var title = char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..];
        return BaseUrl + title.Replace(' ', '_');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
