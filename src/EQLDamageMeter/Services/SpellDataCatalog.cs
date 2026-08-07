using System.Globalization;
using System.IO;
using System.Windows.Media;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed record SpellDataEntry(
    string Name,
    int IconId,
    IReadOnlyList<string> SelfAppliedMessages,
    IReadOnlyList<string> OtherAppliedMessageSuffixes,
    IReadOnlyList<string> FadeMessages);

public sealed class SpellDataCatalog
{
    private const int IconFieldIndex = 75;
    private readonly Dictionary<string, SpellDataEntry> _byName;
    private readonly HashSet<string> _ambiguousOtherSuffixes;
    private SpellIconAtlas? _icons;

    private SpellDataCatalog(string sourceDirectory, Dictionary<string, SpellDataEntry> byName,
        HashSet<string> ambiguousOtherSuffixes, SpellIconAtlas? icons)
    {
        SourceDirectory = sourceDirectory;
        _byName = byName;
        _ambiguousOtherSuffixes = ambiguousOtherSuffixes;
        _icons = icons;
    }

    public string SourceDirectory { get; }
    public int Count => _byName.Count;

    /// <summary>
    /// True when an "other applied" suffix is shared by multiple spell families
    /// (for example " yawns." is used by Togor's Insects, Drowsy, Tagar's Insects, ...).
    /// Those messages cannot uniquely prove which of your tracked spells landed.
    /// </summary>
    public bool IsAmbiguousOtherAppliedSuffix(string suffix) =>
        !string.IsNullOrWhiteSpace(suffix) && _ambiguousOtherSuffixes.Contains(suffix.Trim());

    public bool TryFind(string spellName, out SpellDataEntry? entry) =>
        _byName.TryGetValue(spellName.Trim(), out entry);

    /// <summary>
    /// Resolves a typed spell name to its unranked family entry. "Inner Fire",
    /// "Inner Fire IX", and missing exact ranks all map to the family so users
    /// can track upgrades without typing the Roman numeral.
    /// </summary>
    public bool TryResolveFamily(string spellName, out SpellDataEntry? entry)
    {
        entry = null;
        var trimmed = spellName.Trim();
        if (trimmed.Length == 0) return false;

        var family = SpellNameNormalizer.GetFamilyName(trimmed);
        var members = FindFamilyMembers(family);
        if (members.Count == 0) return false;

        if (TryFind(family, out var baseEntry) && baseEntry is not null)
            entry = AggregateFamily(family, baseEntry.IconId, members);
        else if (TryFind(trimmed, out var exact) && exact is not null)
            entry = AggregateFamily(family, exact.IconId, members);
        else
            entry = AggregateFamily(family, members[0].IconId, members);

        return entry is not null;
    }

    public IReadOnlyList<SpellDataEntry> FindFamilyMembers(string spellName)
    {
        var family = SpellNameNormalizer.GetFamilyName(spellName);
        if (family.Length == 0) return [];

        return _byName.Values
            .Where(entry => SpellNameNormalizer.GetFamilyName(entry.Name)
                .Equals(family, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ImageSource? GetIcon(string? spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return null;
        if (TryFind(spellName, out var entry) && entry is not null)
            return _icons?.GetIcon(entry.IconId);
        if (TryResolveFamily(spellName, out entry) && entry is not null)
            return _icons?.GetIcon(entry.IconId);
        return null;
    }

    public ImageSource GetAbilityIcon(string? abilityName) =>
        GetIcon(abilityName) ?? SpellIconAtlas.GenericIcon;

    public ImageSource? GetIcon(SpellDataEntry entry) => _icons?.GetIcon(entry.IconId);

    public void SetIconStyle(SpellIconStyle style) =>
        _icons = SpellIconAtlas.TryCreate(SourceDirectory, style);

    public IReadOnlyList<string> FindSuggestions(string spellName, int limit = 3)
    {
        var search = spellName.Trim();
        if (search.Length == 0) return [];
        var familySearch = SpellNameNormalizer.GetFamilyName(search);

        return _byName.Values
            .Where(entry => entry.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) ||
                            SpellNameNormalizer.GetFamilyName(entry.Name)
                                .StartsWith(familySearch, StringComparison.OrdinalIgnoreCase))
            .Concat(_byName.Values.Where(entry =>
                !entry.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) &&
                entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => SpellNameNormalizer.GetFamilyName(entry.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .ToArray();
    }

    private static SpellDataEntry AggregateFamily(string familyName, int preferredIconId,
        IReadOnlyList<SpellDataEntry> members)
    {
        var iconId = preferredIconId > 0
            ? preferredIconId
            : members.Select(member => member.IconId).FirstOrDefault(id => id > 0);
        var self = members.SelectMany(member => member.SelfAppliedMessages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var other = members.SelectMany(member => member.OtherAppliedMessageSuffixes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fades = members.SelectMany(member => member.FadeMessages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SpellDataEntry(familyName, iconId, self, other, fades);
    }

    public static SpellDataCatalog? TryLoadForLog(string logPath,
        SpellIconStyle iconStyle = SpellIconStyle.Modern)
    {
        try
        {
            var logDirectory = Path.GetDirectoryName(Path.GetFullPath(logPath));
            var installDirectory = logDirectory is null ? null : Directory.GetParent(logDirectory)?.FullName;
            if (installDirectory is null) return null;

            var spellsPath = Path.Combine(installDirectory, "spells_us.txt");
            var stringsPath = Path.Combine(installDirectory, "spells_us_str.txt");
            if (!File.Exists(spellsPath) || !File.Exists(stringsPath)) return null;

            var namesById = ReadSpellRecords(spellsPath);
            var messagesById = ReadSpellMessages(stringsPath);
            var names = new Dictionary<string, (string CanonicalName, int IconId, HashSet<string> SelfApplied,
                HashSet<string> OtherApplied, HashSet<string> Fades)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var (id, record) in namesById)
            {
                if (!names.TryGetValue(record.Name, out var aggregate))
                    aggregate = (record.Name, record.IconId,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                else if (aggregate.IconId <= 0 && record.IconId > 0)
                    aggregate = aggregate with { IconId = record.IconId };

                if (messagesById.TryGetValue(id, out var messages))
                {
                    if (!string.IsNullOrWhiteSpace(messages.SelfApplied))
                        aggregate.SelfApplied.Add(messages.SelfApplied.Trim());
                    if (!string.IsNullOrWhiteSpace(messages.OtherApplied))
                        aggregate.OtherApplied.Add(messages.OtherApplied.Trim());
                    if (!string.IsNullOrWhiteSpace(messages.Fade)) aggregate.Fades.Add(messages.Fade.Trim());
                }
                names[record.Name] = aggregate;
            }

            var entries = names.ToDictionary(
                pair => pair.Key,
                pair => new SpellDataEntry(pair.Value.CanonicalName, pair.Value.IconId,
                    pair.Value.SelfApplied.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.OtherApplied.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.Fades.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()),
                StringComparer.OrdinalIgnoreCase);
            return new SpellDataCatalog(installDirectory, entries, BuildAmbiguousOtherSuffixes(entries.Values),
                SpellIconAtlas.TryCreate(installDirectory, iconStyle));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HashSet<string> BuildAmbiguousOtherSuffixes(IEnumerable<SpellDataEntry> entries)
    {
        var familiesBySuffix = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var family = SpellNameNormalizer.GetFamilyName(entry.Name);
            foreach (var suffix in entry.OtherAppliedMessageSuffixes)
            {
                if (string.IsNullOrWhiteSpace(suffix)) continue;
                var key = suffix.Trim();
                if (!familiesBySuffix.TryGetValue(key, out var families))
                {
                    families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    familiesBySuffix[key] = families;
                }
                families.Add(family);
            }
        }

        return familiesBySuffix
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<int, (string Name, int IconId)> ReadSpellRecords(string path)
    {
        var names = new Dictionary<int, (string Name, int IconId)>();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('^');
            if (fields.Length <= IconFieldIndex) continue;
            if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            var name = fields[1].Trim();
            if (name.Length == 0) continue;
            _ = int.TryParse(fields[IconFieldIndex], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var iconId);
            names[id] = (name, iconId);
        }
        return names;
    }

    private static Dictionary<int, (string SelfApplied, string OtherApplied, string Fade)> ReadSpellMessages(string path)
    {
        var messages = new Dictionary<int, (string SelfApplied, string OtherApplied, string Fade)>();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split(['^'], 7, StringSplitOptions.None);
            if (fields.Length < 6 || !int.TryParse(fields[0], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var id)) continue;
            messages[id] = (fields[3].Trim(), fields[4].Trim(), fields[5].Trim());
        }
        return messages;
    }
}
