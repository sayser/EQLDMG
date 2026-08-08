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
    IReadOnlyList<string> FadeMessages,
    double CastTimeSeconds = 0,
    int DurationSeconds = 0,
    int DurationFormula = 0,
    int DurationCap = 0)
{
    /// <summary>Duration in seconds for a caster of the given level (EQ tick formulas).</summary>
    public int DurationSecondsFor(int casterLevel) =>
        DurationFormula == 0 && DurationCap == 0
            ? DurationSeconds
            : SpellDataCatalog.DurationFieldsToSeconds(DurationFormula, DurationCap, casterLevel);
}

public sealed class SpellDataCatalog
{
    private const int IconFieldIndex = 75;
    private const int CastTimeFieldIndex = 8;
    private const int DurationFormulaFieldIndex = 11;
    private const int DurationValueFieldIndex = 12;
    /// <summary>Level used when seeding catalog durations (caps match high-level play).</summary>
    public const int DefaultCasterLevel = 60;
    private const int SecondsPerTick = 6;

    private readonly Dictionary<string, SpellDataEntry> _byName;
    private readonly HashSet<string> _ambiguousOtherSuffixes;
    private string[]? _familyNames;
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
            entry = AggregateFamily(family, baseEntry.IconId, members, trimmed);
        else if (TryFind(trimmed, out var exact) && exact is not null)
            entry = AggregateFamily(family, exact.IconId, members, trimmed);
        else
            entry = AggregateFamily(family, members[0].IconId, members, trimmed);

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

    public IReadOnlyList<string> GetFamilyNames() =>
        _familyNames ??= _byName.Values
            .Select(entry => SpellNameNormalizer.GetFamilyName(entry.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Case-insensitive contains search over unranked family names for autocomplete.
    /// Starts-with matches are listed first.
    /// </summary>
    public IReadOnlyList<string> FindMatches(string spellName, int limit = 40)
    {
        var search = spellName.Trim();
        if (search.Length == 0 || limit <= 0) return [];

        var families = GetFamilyNames();
        var startsWith = new List<string>();
        var contains = new List<string>();
        foreach (var name in families)
        {
            if (name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
                startsWith.Add(name);
            else if (name.Contains(search, StringComparison.OrdinalIgnoreCase))
                contains.Add(name);
        }

        return startsWith.Concat(contains).Take(limit).ToArray();
    }

    /// <summary>Cast time field is milliseconds in spells_us.txt.</summary>
    public static double CastTimeMsToSeconds(int castTimeMs) =>
        Math.Clamp(castTimeMs / 1000.0, 0, 120);

    /// <summary>
    /// Converts EQ buff duration formula + value to seconds using one tick = 6s.
    /// Matches EQEmu CalcBuffDuration formulas with <paramref name="casterLevel"/> capping.
    /// </summary>
    public static int DurationFieldsToSeconds(int formula, int durationValue,
        int casterLevel = DefaultCasterLevel)
    {
        var ticks = CalcBuffDurationTicks(formula, durationValue, casterLevel);
        if (ticks <= 0) return 0;
        return checked((int)Math.Min(int.MaxValue / SecondsPerTick, (long)ticks * SecondsPerTick));
    }

    internal static int CalcBuffDurationTicks(int formula, int duration, int level)
    {
        if (formula is 50) return 72_000; // 5 days
        if (formula is 51) return 0; // permanent — no finite seed

        int calculated;
        if (formula >= 200)
        {
            if (formula == 3600)
                calculated = duration != 0 ? duration : 3600;
            else
                calculated = duration > formula ? formula : duration;
            return Math.Max(0, calculated);
        }

        calculated = formula switch
        {
            0 => 0,
            1 => level / 2,
            2 => level > 3 ? level / 2 + 5 : 6,
            3 => 30 * level,
            4 => 50,
            5 => 2,
            6 => level / 2 + 2,
            7 => level,
            8 => level + 10,
            9 => 2 * level + 10,
            10 => 3 * level + 10,
            11 => 30 * (level + 3),
            12 => level > 7 ? level / 4 : 1,
            13 => 4 * level + 10,
            14 => 5 * (level + 2),
            15 => 10 * (level + 10),
            _ => 0
        };

        if (calculated <= 0) return 0;
        // buffduration is a cap for formulas 1–15
        if (duration > 0 && duration < calculated) return duration;
        return calculated;
    }

    private static SpellDataEntry AggregateFamily(string familyName, int preferredIconId,
        IReadOnlyList<SpellDataEntry> members, string? preferredMemberName = null)
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

        var timing = ResolveFamilyTiming(members, familyName, preferredMemberName);
        return new SpellDataEntry(familyName, iconId, self, other, fades,
            timing.CastTimeSeconds, timing.DurationSecondsFor(DefaultCasterLevel),
            timing.DurationFormula, timing.DurationCap);
    }

    private static SpellDataEntry ResolveFamilyTiming(IReadOnlyList<SpellDataEntry> members,
        string familyName, string? preferredMemberName)
    {
        if (!string.IsNullOrWhiteSpace(preferredMemberName))
        {
            var preferred = members.FirstOrDefault(member =>
                member.Name.Equals(preferredMemberName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return preferred;
        }

        var baseMember = members.FirstOrDefault(member =>
            member.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
        if (baseMember is not null) return baseMember;

        return members
            .OrderByDescending(member => member.DurationSeconds)
            .ThenByDescending(member => member.CastTimeSeconds)
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .First();
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
            var names = new Dictionary<string, (string CanonicalName, int IconId, double CastTimeSeconds,
                int DurationSeconds, int DurationFormula, int DurationCap, HashSet<string> SelfApplied,
                HashSet<string> OtherApplied, HashSet<string> Fades)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (id, record) in namesById)
            {
                if (!names.TryGetValue(record.Name, out var aggregate))
                    aggregate = (record.Name, record.IconId, record.CastTimeSeconds, record.DurationSeconds,
                        record.DurationFormula, record.DurationCap,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                else
                {
                    if (aggregate.IconId <= 0 && record.IconId > 0)
                        aggregate = aggregate with { IconId = record.IconId };
                    if (record.DurationSeconds > aggregate.DurationSeconds)
                        aggregate = aggregate with
                        {
                            DurationSeconds = record.DurationSeconds,
                            CastTimeSeconds = record.CastTimeSeconds,
                            DurationFormula = record.DurationFormula,
                            DurationCap = record.DurationCap
                        };
                    else if (aggregate.CastTimeSeconds <= 0 && record.CastTimeSeconds > 0)
                        aggregate = aggregate with { CastTimeSeconds = record.CastTimeSeconds };
                }

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
                    pair.Value.Fades.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.CastTimeSeconds, pair.Value.DurationSeconds,
                    pair.Value.DurationFormula, pair.Value.DurationCap),
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

    /// <summary>Loads a catalog from an install directory (for tests / fixtures).</summary>
    public static SpellDataCatalog? TryLoadFromInstallDirectory(string installDirectory,
        SpellIconStyle iconStyle = SpellIconStyle.Modern)
    {
        var logsStub = Path.Combine(installDirectory, "Logs", "eqlog_Test_test.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(logsStub)!);
        if (!File.Exists(logsStub)) File.WriteAllText(logsStub, string.Empty);
        return TryLoadForLog(logsStub, iconStyle);
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

    private static Dictionary<int, (string Name, int IconId, double CastTimeSeconds, int DurationSeconds,
            int DurationFormula, int DurationCap)>
        ReadSpellRecords(string path)
    {
        var names = new Dictionary<int, (string Name, int IconId, double CastTimeSeconds, int DurationSeconds,
            int DurationFormula, int DurationCap)>();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('^');
            if (fields.Length <= IconFieldIndex) continue;
            if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            var name = fields[1].Trim();
            if (name.Length == 0) continue;
            _ = int.TryParse(fields[IconFieldIndex], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var iconId);
            _ = int.TryParse(fields[CastTimeFieldIndex], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var castMs);
            _ = int.TryParse(fields[DurationFormulaFieldIndex], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var durationFormula);
            _ = int.TryParse(fields[DurationValueFieldIndex], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var durationValue);
            names[id] = (name, iconId, CastTimeMsToSeconds(castMs),
                DurationFieldsToSeconds(durationFormula, durationValue), durationFormula, durationValue);
        }
        return names;
    }

    /// <summary>Scans a character log for the latest "Welcome to level N" line.</summary>
    public static int? TryReadLatestCharacterLevel(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return null;
            int? level = null;
            foreach (var line in File.ReadLines(logPath))
            {
                var messageStart = line.IndexOf("] ", StringComparison.Ordinal);
                var message = messageStart >= 0 ? line[(messageStart + 2)..] : line;
                if (TryParseLevelUp(message, out var parsed)) level = parsed;
            }
            return level;
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

    public static bool TryParseLevelUp(string message, out int level)
    {
        level = 0;
        const string prefix = "You have gained a level! Welcome to level ";
        if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var digits = message[prefix.Length..].TrimEnd('!', '.', ' ');
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out level) &&
               level is > 0 and < 300;
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
