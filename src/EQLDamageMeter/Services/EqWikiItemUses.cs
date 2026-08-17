using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

/// <summary>
/// Resolves which quests / recipes use an item via eqlwiki item pages + backlinks.
/// </summary>
public static partial class EqWikiItemUses
{
    private static readonly HttpClient Http = CreateClient();

    public sealed record ItemUseInfo(IReadOnlyList<string> Quests, IReadOnlyList<string> Recipes, string Summary);

    public static async Task<(ItemUseInfo Info, string? Error)> FetchUsesAsync(string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return (new ItemUseInfo([], [], string.Empty), "Choose an item first.");

        try
        {
            var page = itemName.Trim();
            var wikitext = await FetchWikitextAsync(page, cancellationToken);
            var quests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recipes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(wikitext))
            {
                foreach (Match match in QuestFieldRegex().Matches(wikitext))
                {
                    var value = CleanWikiValue(match.Groups[1].Value);
                    if (value.Length > 0) quests.Add(value);
                }

                foreach (Match match in WikiLinkRegex().Matches(wikitext))
                {
                    var link = match.Groups[1].Value.Trim();
                    if (link.Contains("quest", StringComparison.OrdinalIgnoreCase) ||
                        link.EndsWith(" Quest", StringComparison.OrdinalIgnoreCase))
                        quests.Add(link);
                }

                foreach (Match match in RecipeRegex().Matches(wikitext))
                {
                    var value = CleanWikiValue(match.Groups[1].Value);
                    if (value.Length > 0) recipes.Add(value);
                }
            }

            foreach (var title in await FetchBacklinksAsync(page, cancellationToken))
            {
                if (title.Contains("quest", StringComparison.OrdinalIgnoreCase) ||
                    title.EndsWith(" Quest", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Epic", StringComparison.OrdinalIgnoreCase))
                    quests.Add(title);
                else if (title.Contains("recipe", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Baking", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Smithing", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Tailoring", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Jewelcraft", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Fletching", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Alchemy", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Pottery", StringComparison.OrdinalIgnoreCase) ||
                         title.Contains("Brewing", StringComparison.OrdinalIgnoreCase))
                    recipes.Add(title);
            }

            var questList = quests.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            var recipeList = recipes.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            var parts = new List<string>();
            if (questList.Length > 0)
                parts.Add("Quests: " + string.Join(", ", questList));
            if (recipeList.Length > 0)
                parts.Add("Recipes: " + string.Join(", ", recipeList));
            var summary = parts.Count == 0 ? "No known quest/recipe uses on wiki." : string.Join(Environment.NewLine, parts);
            return (new ItemUseInfo(questList, recipeList, summary), null);
        }
        catch (HttpRequestException)
        {
            return (new ItemUseInfo([], [], string.Empty), "Could not reach eqlwiki.com.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new ItemUseInfo([], [], string.Empty), "The wiki request timed out.");
        }
        catch (JsonException)
        {
            return (new ItemUseInfo([], [], string.Empty), "The wiki response could not be read.");
        }
    }

    private static async Task<string?> FetchWikitextAsync(string page, CancellationToken cancellationToken)
    {
        var url =
            "https://eqlwiki.com/api.php?action=parse&prop=wikitext&format=json&redirects=1&page=" +
            Uri.EscapeDataString(page);
        await using var stream = await Http.GetStreamAsync(url, cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<ParseResponse>(stream,
            cancellationToken: cancellationToken);
        return payload?.Parse?.Wikitext?.Text;
    }

    private static async Task<IReadOnlyList<string>> FetchBacklinksAsync(string page,
        CancellationToken cancellationToken)
    {
        var url =
            "https://eqlwiki.com/api.php?action=query&list=backlinks&blnamespace=0&bllimit=40&format=json&bltitle=" +
            Uri.EscapeDataString(page);
        await using var stream = await Http.GetStreamAsync(url, cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<BacklinksResponse>(stream,
            cancellationToken: cancellationToken);
        return payload?.Query?.Backlinks?
            .Select(item => item.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .ToArray() ?? [];
    }

    private static string CleanWikiValue(string raw)
    {
        var value = WikiLinkRegex().Replace(raw, m => m.Groups[1].Value);
        value = HtmlTagRegex().Replace(value, string.Empty);
        value = value.Replace("'''", string.Empty).Replace("''", string.Empty).Trim();
        return value;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EQDM/1.4.8 (EverQuest Legends Damage Meter; +https://github.com/sayser/EQLDMG)");
        return client;
    }

    [GeneratedRegex(@"\|\s*(?:quest|quests|relatedquest|usedin|used_in|questname)\s*=\s*(.+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuestFieldRegex();

    [GeneratedRegex(@"\|\s*(?:recipe|recipes|crafted|tradeskill)\s*=\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex RecipeRegex();

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:\|[^\]]+)?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    private sealed class ParseResponse
    {
        [JsonPropertyName("parse")] public ParseBlock? Parse { get; set; }
    }

    private sealed class ParseBlock
    {
        [JsonPropertyName("wikitext")] public WikitextBlock? Wikitext { get; set; }
    }

    private sealed class WikitextBlock
    {
        [JsonPropertyName("*")] public string? Text { get; set; }
    }

    private sealed class BacklinksResponse
    {
        [JsonPropertyName("query")] public BacklinksQuery? Query { get; set; }
    }

    private sealed class BacklinksQuery
    {
        [JsonPropertyName("backlinks")] public List<BacklinkEntry>? Backlinks { get; set; }
    }

    private sealed class BacklinkEntry
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
    }
}
