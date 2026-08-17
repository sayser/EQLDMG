using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLDamageMeter.Services;

public static class EqWikiItemSearch
{
    private static readonly HttpClient Http = CreateClient();

    public static async Task<(IReadOnlyList<string> Titles, string? Error)> SearchAsync(string query,
        int limit = 25, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ([], "Enter an item name to search.");

        try
        {
            // list=search is case-insensitive; opensearch on eqlwiki fails for ALL CAPS queries.
            var url =
                "https://eqlwiki.com/api.php?action=query&list=search&srnamespace=0&format=json&srlimit=" +
                Math.Clamp(limit, 1, 50) +
                "&srsearch=" + Uri.EscapeDataString(query.Trim());
            await using var stream = await Http.GetStreamAsync(url, cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<SearchResponse>(stream,
                cancellationToken: cancellationToken);

            var titles = payload?.Query?.Search?
                .Select(item => item.Title?.Trim())
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Select(title => title!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];

            return (titles, titles.Length == 0 ? "No matching items found on the wiki." : null);
        }
        catch (HttpRequestException)
        {
            return ([], "Could not reach eqlwiki.com. Check your network connection.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ([], "The wiki request timed out.");
        }
        catch (JsonException)
        {
            return ([], "The wiki search response could not be read.");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EQDM/1.4.6 (EverQuest Legends Damage Meter; +https://github.com/sayser/EQLDMG)");
        return client;
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("query")]
        public QueryBlock? Query { get; set; }
    }

    private sealed class QueryBlock
    {
        [JsonPropertyName("search")]
        public List<SearchHit>? Search { get; set; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }
}
