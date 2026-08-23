using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed partial class EqWikiSkyCatalog
{
    private static readonly string CachePath = AppPaths.Combine("sky_catalog.json");
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private IReadOnlyList<SkyClassCatalog> _classes = [];

    public IReadOnlyList<SkyClassCatalog> Classes => _classes;
    public DateTime? FetchedAtUtc { get; private set; }
    public bool IsLoaded => _classes.Count > 0;

    public void LoadCached()
    {
        if (TryLoadFromFile(CachePath)) return;
        TryLoadEmbedded();
    }

    private bool TryLoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var document = JsonSerializer.Deserialize<SkyCatalogDocument>(File.ReadAllText(path), JsonOptions);
            if (document?.Classes is not { Count: > 0 }) return false;
            _classes = Normalize(document.Classes);
            FetchedAtUtc = document.FetchedAtUtc;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void TryLoadEmbedded()
    {
        try
        {
            using var stream = typeof(EqWikiSkyCatalog).Assembly
                .GetManifestResourceStream("EQLDamageMeter.Assets.Data.sky_catalog.json");
            if (stream is null) return;
            using var reader = new StreamReader(stream);
            var document = JsonSerializer.Deserialize<SkyCatalogDocument>(reader.ReadToEnd(), JsonOptions);
            if (document?.Classes is not { Count: > 0 }) return;
            _classes = Normalize(document.Classes);
            FetchedAtUtc = document.FetchedAtUtc;
        }
        catch (IOException)
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
            var url =
                "https://eqlwiki.com/api.php?action=parse&prop=wikitext&format=json&page=" +
                Uri.EscapeDataString("Plane of Sky");
            await using var stream = await Http.GetStreamAsync(url, cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<ParseResponse>(stream,
                cancellationToken: cancellationToken);
            var wikitext = payload?.Parse?.Wikitext?.Text;
            if (string.IsNullOrWhiteSpace(wikitext))
                return (false, "Plane of Sky page could not be loaded from the wiki.");

            var classes = Parse(wikitext);
            if (classes.Count == 0)
                return (false, "No Plane of Sky class rewards were found on the wiki page.");

            _classes = classes;
            FetchedAtUtc = DateTime.UtcNow;
            var document = new SkyCatalogDocument
            {
                FetchedAtUtc = FetchedAtUtc.Value,
                Classes = _classes.ToList()
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
            return (false, "The Sky catalog cache could not be written.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access to the Sky catalog cache was denied.");
        }
        catch (JsonException)
        {
            return (false, "The wiki response could not be read.");
        }
    }

    public IReadOnlyList<string> GetClassNames() =>
        _classes.Select(entry => entry.ClassName).ToArray();

    public IReadOnlyList<SkyRewardCatalog> GetRewardsForClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return [];
        var match = _classes.FirstOrDefault(entry =>
            entry.ClassName.Equals(className.Trim(), StringComparison.OrdinalIgnoreCase));
        return match?.Rewards ?? [];
    }

    public SkyClassCatalog? FindClass(string? className) =>
        string.IsNullOrWhiteSpace(className)
            ? null
            : _classes.FirstOrDefault(entry =>
                entry.ClassName.Equals(className.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<SkyClassCatalog> Parse(string wikitext)
    {
        var classes = ParseModern(wikitext);
        if (classes.Count == 0)
            classes = ParseLegacy(wikitext);
        return Normalize(classes);
    }

    private static List<SkyClassCatalog> ParseModern(string wikitext)
    {
        var classes = new List<SkyClassCatalog>();
        foreach (Match block in ModernClassBlockRegex().Matches(wikitext))
        {
            var className = block.Groups["class"].Value.Trim();
            var table = block.Groups["table"].Value;
            var questGiver = QuestGiverRegex().Match(block.Groups["preamble"].Value).Groups["giver"].Value.Trim();
            var rewards = ParseRewards(table);
            if (rewards.Count == 0) continue;
            classes.Add(new SkyClassCatalog
            {
                ClassName = className,
                QuestGiver = questGiver,
                Rewards = rewards
            });
        }

        return classes;
    }

    private static List<SkyClassCatalog> ParseLegacy(string wikitext)
    {
        var classes = new List<SkyClassCatalog>();
        foreach (Match block in LegacyClassBlockRegex().Matches(wikitext))
        {
            var className = block.Groups["class"].Value.Trim();
            var questGiver = block.Groups["giver"].Value.Trim();
            var table = block.Groups["table"].Value;
            var rewards = ParseRewards(table);
            if (rewards.Count == 0) continue;
            classes.Add(new SkyClassCatalog
            {
                ClassName = className,
                QuestGiver = questGiver,
                Rewards = rewards
            });
        }

        return classes;
    }

    private static List<SkyRewardCatalog> ParseRewards(string table)
    {
        var rewards = new List<SkyRewardCatalog>();
        var rows = RowSplitRegex().Split(table);
        foreach (var row in rows.Skip(1))
        {
            if (!row.Contains("{{:", StringComparison.Ordinal)) continue;
            var rewardMatch = RewardRegex().Match(row);
            if (!rewardMatch.Success) continue;

            var rewardName = rewardMatch.Groups[1].Value.Trim();
            if (rewardName.Length == 0) continue;

            var plainCells = PlainCellRegex().Matches(row)
                .Select(match => match.Groups[1].Value.Trim())
                .Where(cell => cell.Length > 0)
                .Where(cell => !cell.Contains("checkbox-list", StringComparison.OrdinalIgnoreCase))
                .Where(cell => !cell.StartsWith("{{:", StringComparison.Ordinal))
                .Where(cell => cell is not "}" and not "]")
                .ToList();

            var questName = plainCells.ElementAtOrDefault(0) ?? string.Empty;
            var trigger = plainCells.ElementAtOrDefault(1) ?? string.Empty;
            // Legacy tables put giver in column 2; quest name then lacks "Test".
            if (plainCells.Count >= 3 &&
                !questName.Contains("Test", StringComparison.OrdinalIgnoreCase) &&
                plainCells[1].Contains("Test", StringComparison.OrdinalIgnoreCase))
            {
                questName = plainCells[1];
                trigger = plainCells.ElementAtOrDefault(2) ?? string.Empty;
            }

            var required = ExtractRequiredItems(row, rewardName);
            rewards.Add(new SkyRewardCatalog
            {
                RewardName = rewardName,
                QuestName = questName,
                TriggerPhrase = trigger,
                RequiredItems = required
            });
        }

        return rewards;
    }

    private static List<SkyRequiredItemCatalog> ExtractRequiredItems(string row, string rewardName)
    {
        var items = new List<SkyRequiredItemCatalog>();
        foreach (Match match in WikiLinkRegex().Matches(row))
        {
            var name = match.Groups[1].Value.Trim();
            if (name.Length == 0) continue;
            if (name.StartsWith("File:", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("Image:", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("Category:", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals(rewardName, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("Plane of Sky", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("Test of", StringComparison.OrdinalIgnoreCase)) continue;

            var note = string.Empty;
            var after = row[(match.Index + match.Length)..];
            var noteMatch = TrailingNoteRegex().Match(after);
            if (noteMatch.Success)
                note = noteMatch.Groups[1].Value.Trim();

            if (items.Any(item => item.ItemName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;

            items.Add(new SkyRequiredItemCatalog
            {
                ItemName = name,
                Note = note,
                NeededCount = 1
            });
        }

        return items;
    }

    private static IReadOnlyList<SkyClassCatalog> Normalize(IEnumerable<SkyClassCatalog> classes) =>
        classes
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ClassName))
            .Select(entry =>
            {
                entry.ClassName = entry.ClassName.Trim();
                entry.QuestGiver = entry.QuestGiver?.Trim() ?? string.Empty;
                entry.Rewards ??= [];
                entry.Rewards = entry.Rewards
                    .Where(reward => !string.IsNullOrWhiteSpace(reward.RewardName))
                    .GroupBy(reward => reward.RewardName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Select(reward =>
                    {
                        reward.RewardName = reward.RewardName.Trim();
                        reward.QuestName = reward.QuestName?.Trim() ?? string.Empty;
                        reward.TriggerPhrase = reward.TriggerPhrase?.Trim() ?? string.Empty;
                        reward.RequiredItems ??= [];
                        reward.RequiredItems = reward.RequiredItems
                            .Where(item => !string.IsNullOrWhiteSpace(item.ItemName))
                            .GroupBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.First())
                            .Select(item =>
                            {
                                item.ItemName = item.ItemName.Trim();
                                item.Note = item.Note?.Trim() ?? string.Empty;
                                if (item.NeededCount < 1) item.NeededCount = 1;
                                return item;
                            })
                            .ToList();
                        return reward;
                    })
                    .ToList();
                return entry;
            })
            .Where(entry => entry.Rewards.Count > 0)
            .OrderBy(entry => entry.ClassName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EQDM/1.5.1 (EverQuest Legends Damage Meter; +https://github.com/sayser/EQLDMG)");
        return client;
    }

    [GeneratedRegex(
        @"===\s*\[\[(?<class>[^\]|#]+)(?:\|[^\]]+)?\]\]\s+Tests\s*===\s*(?<preamble>.*?)(?<table>\{\|.*?^\|\})",
        RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex ModernClassBlockRegex();

    [GeneratedRegex(
        @"<h3>\s*\[\[(?<class>[^\]]+)\]\]\s*\((?<giver>[^)]+)\)\s*</h3>\s*(?<table>\{\|.*?^\|\})",
        RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex LegacyClassBlockRegex();

    [GeneratedRegex(@"Quest Giver:.*? \[\[(?<giver>[^\]|#]+)(?:\|[^\]]+)?\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex QuestGiverRegex();

    [GeneratedRegex(@"(?m)^\s*\|-\s*$")]
    private static partial Regex RowSplitRegex();

    [GeneratedRegex(@"\{\{:([^}]+)\}\}")]
    private static partial Regex RewardRegex();

    [GeneratedRegex(@"(?m)^\|\s*([^|{<\n][^\n]*)$")]
    private static partial Regex PlainCellRegex();

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:\|[^\]]+)?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"^(?:[^\[\n]{0,80}?)\(([^)]+)\)")]
    private static partial Regex TrailingNoteRegex();

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
