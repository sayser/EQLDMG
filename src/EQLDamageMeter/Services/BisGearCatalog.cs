using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public sealed class BisCachedItem
{
    public string Title { get; set; } = string.Empty;
    public string BaseStats { get; set; } = string.Empty;
    public string ClassLine { get; set; } = string.Empty;
    public string SlotLine { get; set; } = string.Empty;
    public bool IsQuest { get; set; }
    public bool IsLore { get; set; }
    public bool OutOfEra { get; set; }
    public string DropZone { get; set; } = string.Empty;
    public string DropMob { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public bool SourceResolved { get; set; }
}

public static partial class BisGearCatalog
{
    public const string QuestItemsCategory = "Category:Quest Items";

    public static readonly (string Key, string Label, string WikiCategory)[] Slots =
    [
        ("Ear1", "Ear 1", "Category:Ear"),
        ("Ear2", "Ear 2", "Category:Ear"),
        ("Head", "Head", "Category:Head"),
        ("Face", "Face", "Category:Face"),
        ("Neck", "Neck", "Category:Neck"),
        ("Shoulders", "Shoulders", "Category:Shoulders"),
        ("Arms", "Arms", "Category:Arms"),
        ("Back", "Back", "Category:Back"),
        ("Wrist1", "Wrist 1", "Category:Wrist"),
        ("Wrist2", "Wrist 2", "Category:Wrist"),
        ("Range", "Range", "Category:Range"),
        ("Hands", "Hands", "Category:Hands"),
        ("Primary", "Primary", "Category:Primary"),
        ("Secondary", "Secondary", "Category:Secondary"),
        ("Finger1", "Finger 1", "Category:Fingers"),
        ("Finger2", "Finger 2", "Category:Fingers"),
        ("Chest", "Chest", "Category:Chest"),
        ("Legs", "Legs", "Category:Legs"),
        ("Feet", "Feet", "Category:Feet"),
        ("Waist", "Waist", "Category:Waist"),
        ("Ammo", "Ammo", "Category:Ammo")
    ];

    private static readonly string CatalogPath = AppPaths.Combine("bis_catalog.json");
    private static readonly string ItemsPath = AppPaths.Combine("bis_items.json");
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly SemaphoreSlim Gate = new(3);
    private static readonly SemaphoreSlim CatalogGate = new(1);

    private static Dictionary<string, List<string>> _categories = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, BisCachedItem> _items = new(StringComparer.OrdinalIgnoreCase);

    public static int CachedItemCount => _items.Count;

    public static void LoadCached()
    {
        TryLoad(CatalogPath, ref _categories);
        try
        {
            if (!File.Exists(ItemsPath)) return;
            var list = JsonSerializer.Deserialize<List<BisCachedItem>>(File.ReadAllText(ItemsPath), JsonOptions);
            if (list is null) return;
            _items = list
                .Where(item => !string.IsNullOrWhiteSpace(item.Title))
                .GroupBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // cache is optional
        }
    }

    public static async Task<(bool Ok, string? Error)> EnsureCategoryAsync(string category,
        CancellationToken cancellationToken)
    {
        await CatalogGate.WaitAsync(cancellationToken);
        try
        {
            if (_categories.TryGetValue(category, out var existing) && existing.Count > 0)
                return (true, null);
        }
        finally
        {
            CatalogGate.Release();
        }

        try
        {
            var titles = await FetchCategoryTitlesAsync(category, cancellationToken);
            if (titles.Count == 0 &&
                category.Equals("Category:Shadow Knight Equipment", StringComparison.OrdinalIgnoreCase))
                titles = await FetchCategoryTitlesAsync("Category:Shadowknight Equipment", cancellationToken);

            await CatalogGate.WaitAsync(cancellationToken);
            try
            {
                _categories[category] = titles;
                await SaveCatalogUnlockedAsync(cancellationToken);
            }
            finally
            {
                CatalogGate.Release();
            }

            return (true, null);
        }
        catch (HttpRequestException)
        {
            return (false, "Could not reach eqlwiki.com.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "The wiki request timed out.");
        }
        catch (IOException)
        {
            return (false, "The BiS catalog cache could not be written.");
        }
    }

    public static IReadOnlyList<string> TitlesIn(string category) =>
        _categories.TryGetValue(category, out var titles) ? titles : [];

    public static HashSet<string> Union(params string[] categories)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            foreach (var title in TitlesIn(category))
                set.Add(title);
        }

        return set;
    }

    public static async Task EnsureItemsAsync(IReadOnlyCollection<string> titles,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var missing = titles
            .Where(title => !string.IsNullOrWhiteSpace(title) && NeedsItemFetch(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length == 0) return;

        const int batchSize = 40;
        var fetched = 0;
        for (var offset = 0; offset < missing.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = missing.Skip(offset).Take(batchSize).ToArray();
            progress?.Report($"Loading item stats {fetched + batch.Length}/{missing.Length}…");
            await FetchItemBatchAsync(batch, cancellationToken);
            fetched += batch.Length;
            if (fetched % 80 == 0 || fetched == missing.Length)
                await SaveItemsAsync(cancellationToken);
        }

        await SaveItemsAsync(cancellationToken);
    }

    private static bool NeedsItemFetch(string title) =>
        TryGet(title) is not { } item || string.IsNullOrWhiteSpace(item.BaseStats);

    public static async Task EnsureSourcesAsync(IEnumerable<string> titles,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var stale = titles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(title => TryGet(title) is not { SourceResolved: true })
            .ToArray();
        if (stale.Length == 0)
            return;

        const int batchSize = 40;
        var fetched = 0;
        for (var offset = 0; offset < stale.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = stale.Skip(offset).Take(batchSize).ToArray();
            progress?.Report($"Loading drop sources {fetched + batch.Length}/{stale.Length}…");
            await FetchItemBatchAsync(batch, cancellationToken);
            fetched += batch.Length;
        }

        await SaveItemsAsync(cancellationToken);
    }

    public static BisCachedItem? TryGet(string title) =>
        _items.TryGetValue(title, out var item) ? item : null;

    public static bool IsWearable(BisCachedItem item, IReadOnlyList<string> classIds)
    {
        var line = item.ClassLine;
        if (string.IsNullOrWhiteSpace(line)) return false;
        if (line.Contains("ALL", StringComparison.OrdinalIgnoreCase))
        {
            var except = ParseExcept(line);
            return classIds.Any(id => !except.Contains(id));
        }

        return classIds.Any(id => ContainsClass(line, id));
    }

    public static bool FitsSlot(BisCachedItem item, string wikiCategory)
    {
        var slotName = wikiCategory.Replace("Category:", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(item.SlotLine))
            return false;
        var line = item.SlotLine;
        if (slotName.Equals("Fingers", StringComparison.OrdinalIgnoreCase))
            return line.Contains("FINGER", StringComparison.OrdinalIgnoreCase);
        if (slotName.Equals("Shoulders", StringComparison.OrdinalIgnoreCase))
            return line.Contains("SHOULDER", StringComparison.OrdinalIgnoreCase);
        return line.Contains(slotName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTwoHanded(BisCachedItem item)
    {
        var skill = FindLine(item.BaseStats, "Skill:");
        if (skill.Contains("2H", StringComparison.OrdinalIgnoreCase))
            return true;
        var slot = item.SlotLine ?? "";
        return slot.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase) &&
               !slot.Contains("SECONDARY", StringComparison.OrdinalIgnoreCase) &&
               (item.BaseStats?.Contains("2H", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Offhand armor used for bash/mitigation, not a 1H weapon. Shield AC raises the AC cap.
    /// </summary>
    public static bool IsShield(BisCachedItem item, IReadOnlyDictionary<string, double> stats)
    {
        var dmg = stats.GetValueOrDefault("DMG");
        var delay = stats.GetValueOrDefault("DELAY");
        if (dmg > 0 && delay > 0) return false;
        var name = item.Title ?? "";
        if (name.Contains("Shield", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Bladestopper", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Aegis", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Buckler", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Kite", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Tower", StringComparison.OrdinalIgnoreCase))
            return true;
        var skill = FindLine(item.BaseStats, "Skill:");
        if (skill.Contains("Bash", StringComparison.OrdinalIgnoreCase))
            return true;
        var slot = item.SlotLine ?? "";
        return slot.Contains("SECONDARY", StringComparison.OrdinalIgnoreCase) &&
               !slot.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase) &&
               stats.GetValueOrDefault("AC") > 0;
    }

    private static async Task FetchItemBatchAsync(IReadOnlyList<string> titles, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var url =
                "https://eqlwiki.com/api.php?action=query&prop=revisions|categories" +
                "&rvprop=content&rvslots=main&cllimit=80&format=json&redirects=1&titles=" +
                string.Join('|', titles.Select(Uri.EscapeDataString));
            await using var stream = await Http.GetStreamAsync(url, cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages))
                return;

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (query.TryGetProperty("redirects", out var redirects))
            {
                foreach (var redirect in redirects.EnumerateArray())
                {
                    var from = redirect.TryGetProperty("from", out var fromEl) ? fromEl.GetString() : null;
                    var to = redirect.TryGetProperty("to", out var toEl) ? toEl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
                        aliases[from] = to;
                }
            }

            await CatalogGate.WaitAsync(cancellationToken);
            try
            {
                foreach (var page in pages.EnumerateObject())
                {
                    var node = page.Value;
                    var title = node.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(title) || node.TryGetProperty("missing", out _))
                        continue;

                    var wikitext = ReadWikitext(node);
                    // Redirect stubs / empty revisions must not poison the cache.
                    if (string.IsNullOrWhiteSpace(wikitext))
                        continue;

                    var isQuest = false;
                    if (node.TryGetProperty("categories", out var cats))
                    {
                        foreach (var cat in cats.EnumerateArray())
                        {
                            var catTitle = cat.TryGetProperty("title", out var ct) ? ct.GetString() : null;
                            if (catTitle is not null &&
                                catTitle.Equals(QuestItemsCategory, StringComparison.OrdinalIgnoreCase))
                                isQuest = true;
                        }
                    }

                    var stats = EqWikiItemStats.ExtractStatsBlock(wikitext);
                    var source = EqWikiItemSource.Parse(wikitext);
                    var outOfEra = EqWikiQuestParser.IsOutOfEraWikitext(wikitext);
                    var cached = new BisCachedItem
                    {
                        Title = title,
                        BaseStats = stats,
                        ClassLine = FindLine(stats, "Class:"),
                        SlotLine = FindLine(stats, "Slot:"),
                        IsQuest = isQuest ||
                                  (wikitext.Contains("related quests", StringComparison.OrdinalIgnoreCase) &&
                                   !wikitext.Contains("This item has no related quests",
                                       StringComparison.OrdinalIgnoreCase)),
                        IsLore = stats.Contains("LORE", StringComparison.OrdinalIgnoreCase),
                        OutOfEra = outOfEra,
                        DropZone = source.Zone,
                        DropMob = source.Mob,
                        SourceText = source.Display,
                        SourceResolved = true
                    };
                    _items[title] = cached;
                    foreach (var (from, to) in aliases)
                    {
                        if (to.Equals(title, StringComparison.OrdinalIgnoreCase))
                            _items[from] = cached;
                    }
                }
            }
            finally
            {
                CatalogGate.Release();
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string ReadWikitext(JsonElement page)
    {
        if (!page.TryGetProperty("revisions", out var revisions) || revisions.GetArrayLength() == 0)
            return string.Empty;
        var rev = revisions[0];
        if (rev.TryGetProperty("slots", out var slots) &&
            slots.TryGetProperty("main", out var main) &&
            main.TryGetProperty("*", out var slotted))
            return slotted.GetString() ?? string.Empty;
        if (rev.TryGetProperty("*", out var star))
            return star.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string FindLine(string stats, string prefix)
    {
        foreach (var line in stats.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return line;
        }

        return string.Empty;
    }

    private static HashSet<string> ParseExcept(string classLine)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idx = classLine.IndexOf("except", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return set;
        foreach (var token in ClassTokenRegex().Matches(classLine[idx..]).Select(m => m.Value))
            set.Add(CanonicalClass(token));
        return set;
    }

    private static bool ContainsClass(string classLine, string classId)
    {
        foreach (var match in ClassTokenRegex().Matches(classLine).Select(m => m.Value))
        {
            if (CanonicalClass(match).Equals(classId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string CanonicalClass(string token)
    {
        var compact = token.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return compact switch
        {
            "WARRIOR" or "WAR" => "WAR",
            "PALADIN" or "PAL" => "PAL",
            "SHADOWKNIGHT" or "SHADOW" or "SK" or "SHD" => "SHD",
            "MONK" or "MNK" => "MNK",
            "ROGUE" or "ROG" => "ROG",
            "BERSERKER" or "BER" => "BER",
            "RANGER" or "RNG" => "RNG",
            "BEASTLORD" or "BST" => "BST",
            "BARD" or "BRD" => "BRD",
            "CLERIC" or "CLR" => "CLR",
            "DRUID" or "DRU" => "DRU",
            "SHAMAN" or "SHM" => "SHM",
            "ENCHANTER" or "ENC" => "ENC",
            "MAGICIAN" or "MAG" => "MAG",
            "NECROMANCER" or "NEC" => "NEC",
            "WIZARD" or "WIZ" => "WIZ",
            _ => compact
        };
    }

    private static async Task<List<string>> FetchCategoryTitlesAsync(string categoryTitle,
        CancellationToken cancellationToken)
    {
        var titles = new List<string>();
        string? continueToken = null;
        do
        {
            var url =
                "https://eqlwiki.com/api.php?action=query&list=categorymembers" +
                "&cmtitle=" + Uri.EscapeDataString(categoryTitle) +
                "&cmtype=page&cmlimit=500&format=json" +
                (continueToken is null
                    ? string.Empty
                    : "&cmcontinue=" + Uri.EscapeDataString(continueToken));
            await using var stream = await Http.GetStreamAsync(url, cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<CategoryMembersResponse>(stream,
                cancellationToken: cancellationToken);
            if (payload?.Query?.CategoryMembers is { Count: > 0 } members)
            {
                foreach (var member in members)
                {
                    if (string.IsNullOrWhiteSpace(member.Title)) continue;
                    if (member.Title.StartsWith("Category:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (member.Title.StartsWith("File:", StringComparison.OrdinalIgnoreCase)) continue;
                    titles.Add(member.Title.Trim());
                }
            }

            continueToken = payload?.Continue?.CmContinue;
        } while (!string.IsNullOrWhiteSpace(continueToken));

        return titles;
    }

    private static async Task SaveCatalogUnlockedAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = CatalogPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(_categories, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, CatalogPath, overwrite: true);
    }

    private static async Task SaveItemsAsync(CancellationToken cancellationToken)
    {
        await CatalogGate.WaitAsync(cancellationToken);
        try
        {
            var temporaryPath = ItemsPath + ".tmp";
            var snapshot = _items.Values
                .GroupBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            await File.WriteAllTextAsync(temporaryPath,
                JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
            File.Move(temporaryPath, ItemsPath, overwrite: true);
        }
        finally
        {
            CatalogGate.Release();
        }
    }

    private static void TryLoad(string path, ref Dictionary<string, List<string>> target)
    {
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path),
                JsonOptions);
            if (loaded is { Count: > 0 })
                target = new Dictionary<string, List<string>>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EQDM/1.4.9 (EverQuest Legends Damage Meter; +https://github.com/sayser/EQLDMG)");
        return client;
    }

    [GeneratedRegex(@"\b(WAR|PAL|SHD|SK|MNK|ROG|BER|RNG|BST|BRD|CLR|DRU|SHM|ENC|MAG|NEC|WIZ|Warrior|Paladin|Shadow\s*Knight|Monk|Rogue|Berserker|Ranger|Beastlord|Bard|Cleric|Druid|Shaman|Enchanter|Magician|Necromancer|Wizard)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClassTokenRegex();

    private sealed class CategoryMembersResponse
    {
        [JsonPropertyName("continue")]
        public ContinueToken? Continue { get; set; }

        [JsonPropertyName("query")]
        public QueryBlock? Query { get; set; }
    }

    private sealed class ContinueToken
    {
        [JsonPropertyName("cmcontinue")]
        public string? CmContinue { get; set; }
    }

    private sealed class QueryBlock
    {
        [JsonPropertyName("categorymembers")]
        public List<CategoryMember>? CategoryMembers { get; set; }
    }

    private sealed class CategoryMember
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}
