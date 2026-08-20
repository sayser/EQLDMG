using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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
    int DurationCap = 0,
    int SkillId = 0,
    int BardLevel = 255,
    bool HasCatalogFadeMessage = false)
{
    public bool IsBardSong =>
        BardLevel > 0 && BardLevel <= SpellDataCatalog.MaxEqlBardSongLevel &&
        (SpellDataCatalog.IsBardSongSkill(SkillId) ||
         SpellDataCatalog.IsEqlBardSongSkill(SkillId) && SpellDataCatalog.LooksLikeBardSongName(Name));

    /// <summary>
    /// Classic bard direct-damage songs (AE) with a timed duration — tracked by mob land
    /// or "damage by Your Song" log lines.
    /// </summary>
    public bool IsBardDamageSong =>
        IsBardSong && OtherAppliedMessageSuffixes.Count > 0 &&
        !SpellDataCatalog.LooksLikeBardBuffSongName(Name) &&
        DurationSecondsFor(SpellDataCatalog.DefaultCasterLevel) > 0;

    /// <summary>Instant AE songs (e.g. Brusco's Boastful Bellow) — not supported in the tracker.</summary>
    public bool IsInstantBardDamageSong =>
        IsBardSong && OtherAppliedMessageSuffixes.Count > 0 &&
        !SpellDataCatalog.LooksLikeBardBuffSongName(Name) &&
        DurationSecondsFor(SpellDataCatalog.DefaultCasterLevel) <= 0;

    public bool IsTrackableBardSong => IsBardSong && !IsInstantBardDamageSong;

    /// <summary>
    /// Bard songs with no fade line in spells_us_str.txt — active while the land text
    /// repeats in the log, stopped after it goes silent.
    /// </summary>
    public bool UsesLandPulseTracking => IsBardSong && !HasCatalogFadeMessage && !IsBardDamageSong;

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
    private const int SkillFieldIndex = 100;
    /// <summary>classes[7] in full EQEmu spells_us.txt — bard minimum level.</summary>
    private const int BardLevelFieldIndex = 111;
    /// <summary>EQL/LIVE truncated spells_us.txt stores casting skill here when field 100 is unused.</summary>
    private const int EqlSkillFieldIndex = 30;
    private const int EqlBardLevelFieldA = 107;
    private const int EqlBardLevelFieldB = 108;
    public const int MaxEqlBardSongLevel = 50;
    private static readonly HashSet<int> EqlBardSongSkillIds =
    [
        2, 3, 4, 5, 6, 8, 33, 41, 42, 45
    ];
    private static readonly Regex BardSongNamePattern = new(
        @"^(?:Song|Hymn|Chant|Anthem|Melody|Lyric|Lullaby|Aria|Psalm|Selo|Carol|Ballad|Harmony|Cantata|Serenade|" +
        @"Sonata|Prelude|Requiem|Nocturne|Operetta|Warsong|Chorus|Crescendo|Virtuoso|Echo|McVaxius|Brusco|Largo|" +
        @"Guardian|Elemental|Purifying|Agilmente|Shauri|Lyssa|Cassindra|Kelin|Alenia|Tarew|Denon|Niv'?s|" +
        @"Breath of|Wind of|Call of|Whispers of|Crispin|Briar|Vilia|Aldor|Garnet|Kazumi|Silisia|Tuyen|Zuriki|" +
        @"Jonth|Innoruuk|Veeshan|Trakanon|Combine|Vishrant|Armee|Arms|Travel|Sionachie|Kaficus|Accelerando|" +
        @"Sonorous|Lucid|Lament|Aquatic|Disenchanting|Clouding|Binding|Warmth|Cooling|Vitality|Purity|Mystic|" +
        @"Bellow|Boastful|Rhythms|Discord|Solidarity|Locating|Lugubrious|Melodic|Regen|Charming|Mesmer|Speed|" +
        @"Haste|Selo|Jaxan|Jig|Dirge|Dance of|Composition|Ervaj|Shield of Songs|Nillipus|Spry Sonata|Warble|" +
        @"Concordia|Katta|Symphony|Staccato|March of the Wee|Chords|Dissonance)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    /// <summary>Level used when seeding catalog durations (caps match high-level play).</summary>
    public const int DefaultCasterLevel = 60;
    private const int SecondsPerTick = 6;

    private readonly Dictionary<string, SpellDataEntry> _byName;
    private readonly HashSet<string> _ambiguousOtherSuffixes;
    private readonly Dictionary<string, HashSet<string>> _selfAppliedMessageFamilies;
    private readonly HashSet<string> _eqlBardSongFamilies;
    private readonly HashSet<string> _trackableBardSongFamilies;
    private string[]? _familyNames;
    private SpellIconAtlas? _icons;

    private SpellDataCatalog(string sourceDirectory, Dictionary<string, SpellDataEntry> byName,
        HashSet<string> ambiguousOtherSuffixes, Dictionary<string, HashSet<string>> selfAppliedMessageFamilies,
        HashSet<string> eqlBardSongFamilies, HashSet<string> trackableBardSongFamilies, SpellIconAtlas? icons)
    {
        SourceDirectory = sourceDirectory;
        _byName = byName;
        _ambiguousOtherSuffixes = ambiguousOtherSuffixes;
        _selfAppliedMessageFamilies = selfAppliedMessageFamilies;
        _eqlBardSongFamilies = eqlBardSongFamilies;
        _trackableBardSongFamilies = trackableBardSongFamilies;
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

    /// <summary>
    /// True when the same self land line is shared by multiple spell families (common among Selo songs).
    /// </summary>
    public bool IsAmbiguousSelfAppliedMessage(string message) =>
        !string.IsNullOrWhiteSpace(message) &&
        _selfAppliedMessageFamilies.TryGetValue(message.Trim(), out var families) &&
        families.Count > 1;

    public bool IsEqlBardSongFamily(string spellOrFamilyName)
    {
        var family = SpellNameNormalizer.GetFamilyName(spellOrFamilyName);
        return family.Length > 0 && _eqlBardSongFamilies.Contains(family);
    }

    public bool IsTrackableBardSong(string spellOrFamilyName) =>
        TryResolveFamily(spellOrFamilyName, out var entry) && entry!.IsTrackableBardSong;

    public bool UsesLandPulseTracking(string spellOrFamilyName) =>
        TryResolveFamily(spellOrFamilyName, out var entry) && entry!.UsesLandPulseTracking;

    public bool IsBardDamageSong(string spellOrFamilyName) =>
        TryResolveFamily(spellOrFamilyName, out var entry) && entry!.IsBardDamageSong;

    public bool MatchesTrackingMode(string spellOrFamilyName, BuffTrackingMode trackingMode)
    {
        if (!TryResolveFamily(spellOrFamilyName, out var entry) || entry is null) return false;
        return trackingMode == BuffTrackingMode.Song
            ? entry.IsTrackableBardSong
            : !entry.IsBardSong;
    }

    public static bool IsBardSongSkill(int skillId) =>
        skillId is >= 35 and <= 39;

    public static bool IsEqlBardSongSkill(int skillId) =>
        EqlBardSongSkillIds.Contains(skillId);

    public static bool LooksLikeBardSongName(string spellName) =>
        !string.IsNullOrWhiteSpace(spellName) &&
        BardSongNamePattern.IsMatch(spellName.Trim());

    /// <summary>Buff/heal twist songs — excluded from damage-song heuristics.</summary>
    public static bool LooksLikeBardBuffSongName(string spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return false;
        var name = spellName.Trim();
        return name.Contains("Chant of Clarity", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Chant of Flame", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Replenishment", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Accelerando", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Anthem de Arms", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Song of Sustenance", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Jig o", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Selo", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Warsong of the Vah Shir", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Whistling Warsong", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Jonthan", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Composition of Ervaj", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildEqlBardSongFamilies(Dictionary<string, SpellDataEntry> entries)
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values)
        {
            if (!entry.IsBardSong) continue;
            families.Add(SpellNameNormalizer.GetFamilyName(entry.Name));
        }
        return families;
    }

    private static HashSet<string> BuildTrackableBardSongFamilies(Dictionary<string, SpellDataEntry> entries)
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values)
        {
            if (!entry.IsTrackableBardSong) continue;
            families.Add(SpellNameNormalizer.GetFamilyName(entry.Name));
        }
        return families;
    }

    internal static int ReadEqlBardLevel(IReadOnlyList<string> fields)
    {
        static int At(IReadOnlyList<string> row, int index) =>
            int.TryParse(row.ElementAtOrDefault(index), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var value)
                ? value
                : 0;

        var a = At(fields, EqlBardLevelFieldA);
        var b = At(fields, EqlBardLevelFieldB);
        if (a is >= 1 and <= MaxEqlBardSongLevel && b is >= 1 and <= MaxEqlBardSongLevel)
            return Math.Max(a, b);
        if (a is >= 1 and <= MaxEqlBardSongLevel) return a;
        if (b is >= 1 and <= MaxEqlBardSongLevel) return b;
        var c = At(fields, BardLevelFieldIndex);
        if (c is >= 1 and <= MaxEqlBardSongLevel) return c;
        var skillAt30 = At(fields, EqlSkillFieldIndex);
        var levelAt31 = At(fields, 31);
        if (IsEqlBardSongSkill(skillAt30) && levelAt31 is >= 1 and <= MaxEqlBardSongLevel)
            return levelAt31;
        return 0;
    }

    private static (int SkillId, int BardLevel) ReadSkillAndBardLevel(string[] fields)
    {
        _ = int.TryParse(fields.ElementAtOrDefault(SkillFieldIndex), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var skillAt100);
        _ = int.TryParse(fields.ElementAtOrDefault(EqlSkillFieldIndex), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var skillAt30);

        int bardLevel;
        if (IsBardSongSkill(skillAt100))
            _ = int.TryParse(fields.ElementAtOrDefault(BardLevelFieldIndex), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out bardLevel);
        else
            bardLevel = ReadEqlBardLevel(fields);

        var skillId = IsBardSongSkill(skillAt100) ? skillAt100 : skillAt30;
        return (skillId, bardLevel);
    }

    /// <summary>Unranked bard song families learnable at or below <paramref name="maxBardLevel"/>.</summary>
    public IReadOnlyList<SpellDataEntry> GetEqlBardSongFamilies(int maxBardLevel = MaxEqlBardSongLevel)
    {
        var results = new List<SpellDataEntry>();
        foreach (var family in _eqlBardSongFamilies.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryResolveFamily(family, out var entry) || entry is null) continue;
            if (!entry.IsTrackableBardSong) continue;
            if (entry.BardLevel > maxBardLevel) continue;
            results.Add(entry);
        }
        return results;
    }

    public bool TryFind(string spellName, out SpellDataEntry? entry) =>
        _byName.TryGetValue(SpellNameNormalizer.NormalizeEqName(spellName), out entry);

    /// <summary>
    /// Resolves a typed spell name to its unranked family entry. "Inner Fire",
    /// "Inner Fire IX", and missing exact ranks all map to the family so users
    /// can track upgrades without typing the Roman numeral.
    /// </summary>
    public bool TryResolveFamily(string spellName, out SpellDataEntry? entry)
    {
        entry = null;
        var trimmed = SpellNameNormalizer.NormalizeEqName(spellName);
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
    public IReadOnlyList<string> FindMatches(string spellName, int limit = 40,
        BuffTrackingMode? trackingMode = null)
    {
        var search = SpellNameNormalizer.NormalizeEqName(spellName);
        if (search.Length == 0 || limit <= 0) return [];

        IEnumerable<string> families = trackingMode switch
        {
            BuffTrackingMode.Song => _trackableBardSongFamilies,
            BuffTrackingMode.Spell => GetFamilyNames().Where(name => !_eqlBardSongFamilies.Contains(name)),
            _ => GetFamilyNames()
        };

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
        var skillId = members.Select(member => member.SkillId).FirstOrDefault(id => id > 0);
        var bardLevel = members.Where(member => member.BardLevel is > 0 and <= 255)
            .Select(member => member.BardLevel)
            .DefaultIfEmpty(255)
            .Min();
        return new SpellDataEntry(familyName, iconId, self, other, fades,
            timing.CastTimeSeconds, timing.DurationSecondsFor(DefaultCasterLevel),
            timing.DurationFormula, timing.DurationCap, skillId, bardLevel,
            members.Any(member => member.HasCatalogFadeMessage));
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
                int DurationSeconds, int DurationFormula, int DurationCap, int SkillId, int BardLevel,
                bool HasCatalogFade, HashSet<string> SelfApplied,
                HashSet<string> OtherApplied, HashSet<string> Fades)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (id, record) in namesById)
            {
                if (!names.TryGetValue(record.Name, out var aggregate))
                    aggregate = (record.Name, record.IconId, record.CastTimeSeconds, record.DurationSeconds,
                        record.DurationFormula, record.DurationCap, record.SkillId, record.BardLevel, false,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                else
                {
                    if (aggregate.IconId <= 0 && record.IconId > 0)
                        aggregate = aggregate with { IconId = record.IconId };
                    if (record.SkillId > 0 && aggregate.SkillId <= 0)
                        aggregate = aggregate with { SkillId = record.SkillId };
                    if (record.BardLevel is > 0 and <= MaxEqlBardSongLevel &&
                        (aggregate.BardLevel is <= 0 or > MaxEqlBardSongLevel ||
                         record.BardLevel < aggregate.BardLevel))
                        aggregate = aggregate with { BardLevel = record.BardLevel };
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
                    if (!string.IsNullOrWhiteSpace(messages.Fade))
                    {
                        aggregate.Fades.Add(messages.Fade.Trim());
                        aggregate = aggregate with { HasCatalogFade = true };
                    }
                }
                names[record.Name] = aggregate;
            }

            var entries = EnrichMissingBardSongFades(names.ToDictionary(
                pair => pair.Key,
                pair => new SpellDataEntry(pair.Value.CanonicalName, pair.Value.IconId,
                    pair.Value.SelfApplied.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.OtherApplied.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.Fades.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    pair.Value.CastTimeSeconds, pair.Value.DurationSeconds,
                    pair.Value.DurationFormula, pair.Value.DurationCap,
                    pair.Value.SkillId, pair.Value.BardLevel, pair.Value.HasCatalogFade),
                StringComparer.OrdinalIgnoreCase));
            return new SpellDataCatalog(installDirectory, entries, BuildAmbiguousOtherSuffixes(entries.Values),
                BuildSelfAppliedMessageFamilies(entries.Values),
                BuildEqlBardSongFamilies(entries),
                BuildTrackableBardSongFamilies(entries),
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

    private static Dictionary<string, HashSet<string>> BuildSelfAppliedMessageFamilies(
        IEnumerable<SpellDataEntry> entries)
    {
        var familiesByMessage = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var family = SpellNameNormalizer.GetFamilyName(entry.Name);
            foreach (var message in entry.SelfAppliedMessages)
            {
                if (string.IsNullOrWhiteSpace(message)) continue;
                var key = message.Trim();
                if (!familiesByMessage.TryGetValue(key, out var families))
                {
                    families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    familiesByMessage[key] = families;
                }
                families.Add(family);
            }
        }

        return familiesByMessage;
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

    /// <summary>
    /// EQL spells_us_str.txt leaves fade empty for many classic bard songs. Infer stop lines from
    /// related songs (same name prefix), thematic keywords, and shared land text.
    /// </summary>
    internal static Dictionary<string, SpellDataEntry> EnrichMissingBardSongFades(
        Dictionary<string, SpellDataEntry> entries)
    {
        var fadesBySelfLand = BuildFadesBySelfLand(entries.Values);
        var enriched = new Dictionary<string, SpellDataEntry>(entries, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in entries)
        {
            if (!entry.IsBardSong || entry.FadeMessages.Count > 0) continue;

            var inferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            inferred.UnionWith(InferFadesFromNamePrefix(entry.Name, entries.Values));
            inferred.UnionWith(InferThematicFades(entry.Name));

            if (inferred.Count == 0)
            {
                foreach (var self in entry.SelfAppliedMessages)
                {
                    if (string.IsNullOrWhiteSpace(self)) continue;
                    if (fadesBySelfLand.TryGetValue(self.Trim(), out var shared))
                        inferred.UnionWith(shared);
                }
            }

            if (inferred.Count == 0) continue;
            enriched[key] = entry with
            {
                FadeMessages = inferred.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        return enriched;
    }

    private static Dictionary<string, HashSet<string>> BuildFadesBySelfLand(IEnumerable<SpellDataEntry> entries)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            foreach (var self in entry.SelfAppliedMessages)
            {
                if (string.IsNullOrWhiteSpace(self)) continue;
                foreach (var fade in entry.FadeMessages)
                {
                    if (string.IsNullOrWhiteSpace(fade)) continue;
                    if (!map.TryGetValue(self.Trim(), out var fades))
                    {
                        fades = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        map[self.Trim()] = fades;
                    }
                    fades.Add(fade.Trim());
                }
            }
        }

        return map;
    }

    private static IEnumerable<string> InferFadesFromNamePrefix(string spellName,
        IEnumerable<SpellDataEntry> entries)
    {
        var prefix = ExtractSongNamePrefix(spellName);
        if (prefix is null) yield break;

        foreach (var entry in entries)
        {
            if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var fade in entry.FadeMessages)
            {
                if (!string.IsNullOrWhiteSpace(fade))
                    yield return fade.Trim();
            }
        }
    }

    private static string? ExtractSongNamePrefix(string spellName)
    {
        var normalized = SpellNameNormalizer.NormalizeEqName(spellName);
        var marker = normalized.IndexOf("'s ", StringComparison.OrdinalIgnoreCase);
        if (marker > 0)
            return normalized[..(marker + 3)];
        return null;
    }

    private static IEnumerable<string> InferThematicFades(string spellName)
    {
        var name = SpellNameNormalizer.NormalizeEqName(spellName);
        if (name.Contains("Clarity", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Your clarity of mind fades.";
            yield return "The clarity of mind fades.";
        }

        if (name.Contains("Replenish", StringComparison.OrdinalIgnoreCase))
        {
            yield return "The chorus of replenishment fades.";
            yield return "The cantata of replenishment fades.";
            yield return "The chorus of life fades.";
            yield return "The replenishment of life fades.";
        }

        if (name.Contains("Vigor", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Jig", StringComparison.OrdinalIgnoreCase))
            yield return "You are no longer invigorated.";

        if (name.Contains("Warsong", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Whistling", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Jonthan", StringComparison.OrdinalIgnoreCase))
        {
            yield return "You stop whistling.";
        }

        if (name.Contains("Inspiration", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ervaj", StringComparison.OrdinalIgnoreCase))
            yield return "The inspiration fades.";

        if (name.Contains("Warsong", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Vah Shir", StringComparison.OrdinalIgnoreCase))
            yield return "The acceleration fades.";
    }

    private static Dictionary<int, (string Name, int IconId, double CastTimeSeconds, int DurationSeconds,
            int DurationFormula, int DurationCap, int SkillId, int BardLevel)>
        ReadSpellRecords(string path)
    {
        var names = new Dictionary<int, (string Name, int IconId, double CastTimeSeconds, int DurationSeconds,
            int DurationFormula, int DurationCap, int SkillId, int BardLevel)>();
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('^');
            if (fields.Length <= IconFieldIndex) continue;
            if (!int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) continue;
            var name = SpellNameNormalizer.NormalizeEqName(fields[1]);
            if (name.Length == 0) continue;
            _ = int.TryParse(fields[IconFieldIndex], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var iconId);
            _ = int.TryParse(fields[CastTimeFieldIndex], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var castMs);
            _ = int.TryParse(fields[DurationFormulaFieldIndex], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var durationFormula);
            _ = int.TryParse(fields[DurationValueFieldIndex], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var durationValue);
            var (skillId, bardLevel) = ReadSkillAndBardLevel(fields);
            names[id] = (name, iconId, CastTimeMsToSeconds(castMs),
                DurationFieldsToSeconds(durationFormula, durationValue), durationFormula, durationValue,
                skillId, bardLevel);
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
