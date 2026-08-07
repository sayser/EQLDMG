using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

/// <summary>
/// EverQuest Legends upgrades append Roman ranks (Inner Fire IX) or classic Rk.
/// suffixes. Tracking rules are stored under the unranked family name so one rule
/// covers every rank that appears in the log.
/// </summary>
public static partial class SpellNameNormalizer
{
    [GeneratedRegex(
        @"\s+(?:Rk\.\s*)?(?=[IVXLCDM]+\b)M{0,4}(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RankSuffixRegex();

    public static string GetFamilyName(string spellName)
    {
        var name = spellName.Trim();
        if (name.Length == 0) return name;

        while (true)
        {
            var stripped = RankSuffixRegex().Replace(name, string.Empty).TrimEnd();
            if (stripped.Length == 0 || stripped.Equals(name, StringComparison.Ordinal))
                return name;
            name = stripped;
        }
    }

    public static bool BelongsToFamily(string spellName, string familyOrSpellName) =>
        GetFamilyName(spellName).Equals(GetFamilyName(familyOrSpellName), StringComparison.OrdinalIgnoreCase);
}
