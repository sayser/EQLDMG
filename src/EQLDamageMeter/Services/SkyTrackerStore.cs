using System.IO;
using System.Text.Json;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public static class SkyTrackerStore
{
    private static readonly string StorePath = AppPaths.Combine("skytracker.json");
    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SkyTrackerDocument TryLoad()
    {
        try
        {
            if (!File.Exists(StorePath)) return new SkyTrackerDocument();
            var document = JsonSerializer.Deserialize<SkyTrackerDocument>(File.ReadAllText(StorePath), JsonOptions);
            document ??= new SkyTrackerDocument();
            document.Goals ??= [];
            foreach (var goal in document.Goals)
            {
                goal.Id = string.IsNullOrWhiteSpace(goal.Id) ? Guid.NewGuid().ToString("N") : goal.Id;
                goal.ClassName = goal.ClassName?.Trim() ?? string.Empty;
                goal.RewardName = goal.RewardName?.Trim() ?? string.Empty;
                goal.QuestName = goal.QuestName?.Trim() ?? string.Empty;
                goal.TriggerPhrase = goal.TriggerPhrase?.Trim() ?? string.Empty;
                goal.QuestGiver = goal.QuestGiver?.Trim() ?? string.Empty;
                goal.RewardStats = goal.RewardStats?.Trim() ?? string.Empty;
                goal.Parts ??= [];
                foreach (var part in goal.Parts)
                {
                    part.ItemName = part.ItemName?.Trim() ?? string.Empty;
                    part.Note = part.Note?.Trim() ?? string.Empty;
                    part.VoiceText = part.VoiceText?.Trim() ?? string.Empty;
                    part.LastDropText = part.LastDropText?.Trim() ?? string.Empty;
                    part.AlertMode = BuffAlertModeOptions.Normalize(part.AlertMode);
                    if (part.NeededCount < 1) part.NeededCount = 1;
                    if (part.FoundCount < 0) part.FoundCount = 0;
                }

                goal.Parts = goal.Parts
                    .Where(part => part.ItemName.Length > 0)
                    .GroupBy(part => part.ItemName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            }

            document.Goals = document.Goals
                .Where(goal => goal.RewardName.Length > 0 && goal.Parts.Count > 0)
                .ToList();
            return document;
        }
        catch (IOException)
        {
            return new SkyTrackerDocument();
        }
        catch (UnauthorizedAccessException)
        {
            return new SkyTrackerDocument();
        }
        catch (JsonException)
        {
            return new SkyTrackerDocument();
        }
    }

    public static async Task<bool> TrySaveAsync(SkyTrackerDocument document,
        CancellationToken cancellationToken = default)
    {
        await StoreGate.WaitAsync(cancellationToken);
        try
        {
            var temporaryPath = StorePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken);
            File.Move(temporaryPath, StorePath, true);
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
        finally
        {
            StoreGate.Release();
        }
    }
}
