using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class EqWikiQuestCatalog
{
    private static readonly string CachePath = AppPaths.Combine("quest_catalog.json");
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Index / list pages that are in Category:Quests but are not trackable quests.
    /// </summary>
    private static readonly HashSet<string> ExcludedTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Class Race Quest List",
        "Class Epic Quest List",
        "Popular Quests by Level",
        "Popular Quests by Reward",
        "Deity Specific Quests",
        "All Positive Faction Quests",
        "Armor Size",
        "Quests"
    };

    /// <summary>
    /// Wiki era categories marked Out of Era by Template:PageEra (eqlwiki).
    /// </summary>
    private static readonly string[] OutOfEraCategories =
    [
        "Category:Epic Quests Era",
        "Category:Kunark Era",
        "Category:Velious Era",
        "Category:Luclin Era",
        "Category:Chardok Era",
        "Category:Chardok Revamp Era",
        "Category:FearHateRevamp Era",
        "Category:Hole VP Era",
        "Category:UNKNOWN ERA, please correct!"
    ];

    private IReadOnlyList<string> _titles = [];

    public IReadOnlyList<string> Titles => _titles;
    public DateTime? FetchedAtUtc { get; private set; }
    public bool IsLoaded => _titles.Count > 0;

    public void LoadCached()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var document = JsonSerializer.Deserialize<QuestCatalogDocument>(File.ReadAllText(CachePath), JsonOptions);
            if (document?.Titles is not { Count: > 0 }) return;
            _titles = NormalizeTitles(document.Titles);
            FetchedAtUtc = document.FetchedAtUtc;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    public async Task<(bool Ok, string? Error)> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var titles = await FetchCategoryTitlesAsync("Category:Quests", cancellationToken);
            if (titles.Count == 0)
                return (false, "No quests were returned from the wiki.");

            var outOfEra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in OutOfEraCategories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var title in await FetchCategoryTitlesAsync(category, cancellationToken))
                    outOfEra.Add(title);
            }

            _titles = NormalizeTitles(titles.Where(title => !outOfEra.Contains(title)));
            FetchedAtUtc = DateTime.UtcNow;

            var document = new QuestCatalogDocument
            {
                FetchedAtUtc = FetchedAtUtc.Value,
                Titles = _titles.ToList()
            };
            var temporaryPath = CachePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken);
            File.Move(temporaryPath, CachePath, overwrite: true);
            return (true, null);
        }
        catch (HttpRequestException)
        {
            return (false, "Could not reach eqlwiki.com. Check your network connection.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "The wiki request timed out.");
        }
        catch (IOException)
        {
            return (false, "The quest catalog cache could not be written.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access to the quest catalog cache was denied.");
        }
        catch (JsonException)
        {
            return (false, "The wiki response could not be read.");
        }
    }

    public IReadOnlyList<string> FindMatches(string query, int limit = 250)
    {
        if (_titles.Count == 0 || string.IsNullOrWhiteSpace(query)) return [];
        var needle = query.Trim();
        // Prefer titles that start with the query, then alphabetical contains matches.
        var startsWith = new List<string>();
        var contains = new List<string>();
        foreach (var title in _titles)
        {
            if (!title.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            if (title.StartsWith(needle, StringComparison.OrdinalIgnoreCase)) startsWith.Add(title);
            else contains.Add(title);
        }

        return startsWith.Concat(contains).Take(limit).ToArray();
    }

    public bool TryResolveTitle(string query, out string title)
    {
        title = string.Empty;
        if (string.IsNullOrWhiteSpace(query) || _titles.Count == 0) return false;
        var exact = _titles.FirstOrDefault(item =>
            item.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            title = exact;
            return true;
        }

        var matches = FindMatches(query, 1);
        if (matches.Count != 1) return false;
        title = matches[0];
        return true;
    }

    public static bool IsExcludedTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var trimmed = title.Trim();
        if (ExcludedTitles.Contains(trimmed)) return true;
        // Class epics (and any similarly named epic quest pages).
        if (trimmed.EndsWith("Epic Quest", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static IReadOnlyList<string> NormalizeTitles(IEnumerable<string> titles) =>
        titles
            .Where(title => !IsExcludedTitle(title))
            .Select(title => title.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(title => title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
                    titles.Add(member.Title.Trim());
                }
            }

            continueToken = payload?.Continue?.CmContinue;
        } while (!string.IsNullOrWhiteSpace(continueToken));

        return titles;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EQDM/1.4.5 (EverQuest Legends Damage Meter; +https://github.com/sayser/EQLDMG)");
        return client;
    }

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
