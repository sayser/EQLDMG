using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public sealed record LogIdentity(string Character, string Server)
{
    private static readonly Regex FilePattern = new(
        "^eqlog_(?<character>[^_]+)_(?<server>.+)\\.txt$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryFromPath(string path, out LogIdentity? identity)
    {
        var match = FilePattern.Match(System.IO.Path.GetFileName(path));
        if (!match.Success)
        {
            identity = null;
            return false;
        }

        identity = new LogIdentity(match.Groups["character"].Value, match.Groups["server"].Value);
        return true;
    }
}
