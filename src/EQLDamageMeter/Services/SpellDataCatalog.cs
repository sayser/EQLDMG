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
    private SpellIconAtlas? _icons;

    private SpellDataCatalog(string sourceDirectory, Dictionary<string, SpellDataEntry> byName,
        SpellIconAtlas? icons)
    {
        SourceDirectory = sourceDirectory;
        _byName = byName;
        _icons = icons;
    }

    public string SourceDirectory { get; }
    public int Count => _byName.Count;

    public bool TryFind(string spellName, out SpellDataEntry? entry) =>
        _byName.TryGetValue(spellName.Trim(), out entry);

    public ImageSource? GetIcon(string? spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName) || !TryFind(spellName, out var entry) || entry is null)
            return null;
        return _icons?.GetIcon(entry.IconId);
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

        return _byName.Values
            .Where(entry => entry.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .Concat(_byName.Values.Where(entry =>
                !entry.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) &&
                entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .ToArray();
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
            return new SpellDataCatalog(installDirectory, entries,
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
