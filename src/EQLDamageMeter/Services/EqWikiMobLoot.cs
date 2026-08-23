using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQLDamageMeter.Services;

public sealed record EqWikiMobLootDrop(string ItemName, string DropChance);

public sealed record EqWikiMobLootTable(
    string ResolvedTitle,
    string WikiUrl,
    IReadOnlyList<EqWikiMobLootDrop> Drops);

public static partial class EqWikiMobLoot
{
    private static readonly HttpClient Http = CreateClient();

    public static async Task<(EqWikiMobLootTable? Table, string? Error)> FetchAsync(string mobName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mobName))
            return (null, "Choose a mob first.");

        try
        {
            foreach (var title in TitleCandidates(mobName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (wikitext, resolvedTitle) = await TryFetchWikitextAsync(title, cancellationToken);
                if (string.IsNullOrWhiteSpace(wikitext) || string.IsNullOrWhiteSpace(resolvedTitle))
                    continue;
                if (IsRedirectOnly(wikitext))
                    continue;

                var drops = ParseKnownLoot(wikitext);
                var url = EqWikiLinks.BaseUrl + resolvedTitle.Replace(' ', '_');
                return (new EqWikiMobLootTable(resolvedTitle, url, drops), null);
            }

            return (null, "No wiki page was found for this mob.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return (null, "Could not reach eqlwiki.com. Check your network connection.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The wiki request timed out.");
        }
        catch (JsonException)
        {
            return (null, "The wiki response could not be read.");
        }
    }

    public static IReadOnlyList<EqWikiMobLootDrop> ParseKnownLoot(string wikitext)
    {
        var block = ExtractKnownLootBlock(wikitext);
        if (string.IsNullOrWhiteSpace(block)) return [];

        var drops = new List<EqWikiMobLootDrop>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match item in ItemTransclusionRegex().Matches(block))
        {
            var name = CleanItemName(item.Groups["item"].Value);
            if (name.Length == 0 || !seen.Add(name)) continue;
            drops.Add(new EqWikiMobLootDrop(name, ExtractChanceAfter(block, item.Index + item.Length)));
        }

        foreach (Match item in WikiLinkRegex().Matches(block))
        {
            var name = CleanItemName(item.Groups["item"].Value);
            if (name.Length == 0 || !seen.Add(name)) continue;
            drops.Add(new EqWikiMobLootDrop(name, ExtractChanceAfter(block, item.Index + item.Length)));
        }

        return drops;
    }

    public static string ExtractKnownLootBlock(string wikitext)
    {
        var match = KnownLootRegex().Match(wikitext);
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    public static IEnumerable<string> TitleCandidates(string mobName)
    {
        var trimmed = WhitespaceRegex().Replace(mobName.Trim(), " ");
        if (trimmed.Length == 0) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in ExpandTitleForms(trimmed))
        {
            if (seen.Add(candidate))
                yield return candidate;
        }

        var bare = ArticlePrefixRegex().Replace(trimmed, string.Empty).Trim();
        if (bare.Length == 0)
            yield break;

        if (!bare.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in ExpandTitleForms(bare))
            {
                if (seen.Add(candidate))
                    yield return candidate;
            }

            foreach (var article in new[] { "A ", "An ", "The " })
            {
                foreach (var candidate in ExpandTitleForms(article + bare))
                {
                    if (seen.Add(candidate))
                        yield return candidate;
                }
            }
        }
        else
        {
            foreach (var article in new[] { "A ", "An ", "The " })
            {
                foreach (var candidate in ExpandTitleForms(article + trimmed))
                {
                    if (seen.Add(candidate))
                        yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> ExpandTitleForms(string name)
    {
        var normalized = name.Replace('`', '\'');
        yield return FirstCharUpper(normalized);
        yield return ToTitleCase(normalized);

        if (!normalized.Contains('\'', StringComparison.Ordinal)) yield break;
        var spaced = WhitespaceRegex().Replace(normalized.Replace('\'', ' '), " ").Trim();
        yield return FirstCharUpper(spaced);
        yield return ToTitleCase(spaced);
    }

    private static async Task<(string? Wikitext, string? Title)> TryFetchWikitextAsync(string title,
        CancellationToken cancellationToken)
    {
        var url =
            "https://eqlwiki.com/api.php?action=parse&prop=wikitext&format=json&redirects=1&page=" +
            Uri.EscapeDataString(title);
        await using var stream = await Http.GetStreamAsync(url, cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<ParseResponse>(stream,
            cancellationToken: cancellationToken);
        var wikitext = payload?.Parse?.Wikitext?.Text;
        var resolved = payload?.Parse?.Title;
        if (string.IsNullOrWhiteSpace(wikitext) || string.IsNullOrWhiteSpace(resolved))
            return (null, null);
        return (wikitext, resolved);
    }

    private static string ExtractChanceAfter(string block, int start)
    {
        if (start < 0 || start >= block.Length) return string.Empty;
        var end = Math.Min(block.Length, start + 180);
        var window = block[start..end];
        var cut = window.Length;
        var nextItem = window.IndexOf("{{:", StringComparison.Ordinal);
        var nextLink = window.IndexOf("[[", StringComparison.Ordinal);
        var liEnd = window.IndexOf("</li>", StringComparison.OrdinalIgnoreCase);
        if (nextItem >= 0) cut = Math.Min(cut, nextItem);
        if (nextLink >= 0) cut = Math.Min(cut, nextLink);
        if (liEnd >= 0) cut = Math.Min(cut, liEnd);
        window = window[..cut];

        var chance = ChanceSpanRegex().Match(window);
        if (chance.Success) return CleanChance(chance.Groups["chance"].Value);
        var plain = PlainChanceRegex().Match(window);
        return plain.Success ? CleanChance(plain.Groups["chance"].Value) : string.Empty;
    }

    private static bool IsRedirectOnly(string wikitext) =>
        RedirectRegex().IsMatch(wikitext.TrimStart());

    private static string CleanItemName(string value)
    {
        var name = value.Trim();
        name = CategoryPrefixRegex().Replace(name, string.Empty);
        return WhitespaceRegex().Replace(name, " ").Trim();
    }

    private static string CleanChance(string value) =>
        WhitespaceRegex().Replace(value.Trim().Trim('(', ')').Trim(), " ");

    private static string FirstCharUpper(string value) =>
        value.Length == 0 ? value : char.ToUpper(value[0], CultureInfo.InvariantCulture) + value[1..];

    private static string ToTitleCase(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            parts[i] = char.ToUpper(part[0], CultureInfo.InvariantCulture) +
                       (part.Length > 1 ? part[1..] : string.Empty);
        }
        return string.Join(' ', parts);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EQDM/1.5.1 (EverQuest Legends Damage Meter; +https://github.com/sayser/EQLDMG)");
        return client;
    }

    [GeneratedRegex(@"\|\s*known_loot\s*=\s*(.*?)(?:\n\|\s*[a-zA-Z_]|\n\}\})", RegexOptions.Singleline)]
    private static partial Regex KnownLootRegex();

    [GeneratedRegex(@"\{\{:\s*(?<item>[^}|]+?)\s*(?:\|[^}]*)?\}\}")]
    private static partial Regex ItemTransclusionRegex();

    [GeneratedRegex(@"\[\[(?<item>[^\]|#]+)(?:\|[^\]]+)?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(
        @"<span[^>]*class\s*=\s*['""][^'""]*d(?:rare|common|uncommon)[^'""]*['""][^>]*>\s*\((?<chance>[^)]+)\)\s*</span>",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChanceSpanRegex();

    [GeneratedRegex(@"\((?<chance>\d+(?:\.\d+)?%|Rare|Uncommon|Common|Always|Extremely Rare[^)]*)\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PlainChanceRegex();

    [GeneratedRegex(@"^#REDIRECT\b", RegexOptions.IgnoreCase)]
    private static partial Regex RedirectRegex();

    [GeneratedRegex(@"^(a|an|the)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArticlePrefixRegex();

    [GeneratedRegex(@"^(Category|File|Image|Talk)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex CategoryPrefixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed class ParseResponse
    {
        [JsonPropertyName("parse")]
        public ParseBlock? Parse { get; set; }
    }

    private sealed class ParseBlock
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("wikitext")]
        public WikitextBlock? Wikitext { get; set; }
    }

    private sealed class WikitextBlock
    {
        [JsonPropertyName("*")]
        public string? Text { get; set; }
    }
}
