using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public static partial class EqWikiItemStats
{
    private static readonly HttpClient Http = CreateClient();

    public enum PageKind
    {
        Item,
        Spell
    }

    public sealed record FetchResult(PageKind Kind, string ItemStats, EqWikiSpellPage.SpellInfo? Spell, string? Error);

    public static async Task<(string Stats, string? Error)> FetchStatsAsync(string itemName,
        CancellationToken cancellationToken = default)
    {
        var result = await FetchAsync(itemName, cancellationToken);
        if (result.Kind == PageKind.Spell && result.Spell is not null)
            return (EqWikiSpellPage.Format(result.Spell, 0), result.Error);
        return (result.ItemStats, result.Error);
    }

    public static async Task<FetchResult> FetchAsync(string itemName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return new FetchResult(PageKind.Item, string.Empty, null, "Choose an item first.");

        try
        {
            var url =
                "https://eqlwiki.com/api.php?action=parse&prop=wikitext&format=json&redirects=1&page=" +
                Uri.EscapeDataString(itemName.Trim());
            await using var stream = await Http.GetStreamAsync(url, cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<ParseResponse>(stream,
                cancellationToken: cancellationToken);
            var wikitext = payload?.Parse?.Wikitext?.Text;
            if (string.IsNullOrWhiteSpace(wikitext))
                return new FetchResult(PageKind.Item, string.Empty, null,
                    "That wiki page could not be loaded.");

            if (EqWikiSpellPage.TryParse(wikitext, out var spell) && spell is not null)
                return new FetchResult(PageKind.Spell, string.Empty, spell, null);

            var stats = ExtractStatsBlock(wikitext);
            if (string.IsNullOrWhiteSpace(stats))
                return new FetchResult(PageKind.Item, string.Empty, null,
                    "No item stats or spell data were found on that wiki page.");

            return new FetchResult(PageKind.Item, EqWikiItemUpgrade.WithWeaponRatio(stats), null, null);
        }
        catch (HttpRequestException)
        {
            return new FetchResult(PageKind.Item, string.Empty, null,
                "Could not reach eqlwiki.com. Check your network connection.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new FetchResult(PageKind.Item, string.Empty, null, "The wiki request timed out.");
        }
        catch (JsonException)
        {
            return new FetchResult(PageKind.Item, string.Empty, null, "The wiki response could not be read.");
        }
    }

    public static string ExtractStatsBlock(string wikitext)
    {
        var match = StatsBlockRegex().Match(wikitext);
        if (!match.Success) return string.Empty;
        var raw = match.Groups[1].Value;
        raw = WikiLinkRegex().Replace(raw, m => m.Groups[1].Value);
        raw = raw.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
        raw = HtmlTagRegex().Replace(raw, string.Empty);
        raw = raw.Replace("'''", string.Empty).Replace("''", string.Empty);
        var lines = raw
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EQDM/1.5.0 (EverQuest Legends Damage Meter; +https://github.com/sayser/EQLDMG)");
        return client;
    }

    [GeneratedRegex(@"\|\s*statsblock\s*=\s*(.*?)(?:\n\|[a-zA-Z_]|\n\}\})", RegexOptions.Singleline)]
    private static partial Regex StatsBlockRegex();

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:\|[^\]]+)?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    private sealed class ParseResponse
    {
        [JsonPropertyName("parse")]
        public ParseBlock? Parse { get; set; }
    }

    private sealed class ParseBlock
    {
        [JsonPropertyName("wikitext")]
        public WikitextBlock? Wikitext { get; set; }
    }

    private sealed class WikitextBlock
    {
        [JsonPropertyName("*")]
        public string? Text { get; set; }
    }
}
