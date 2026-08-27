using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed partial class EqWikiSkyCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private IReadOnlyList<SkyClassCatalog> _classes = [];

    public IReadOnlyList<SkyClassCatalog> Classes => _classes;
    public DateTime? FetchedAtUtc { get; private set; }
    public bool IsLoaded => _classes.Count > 0;

    public void LoadCached() => TryLoadEmbedded();

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
}
