using System.IO;
using System.Text.Json;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public static class QuestTrackerStore
{
    private static readonly string StorePath = AppPaths.Combine("questtracker.json");
    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static QuestTrackerDocument TryLoad()
    {
        try
        {
            if (!File.Exists(StorePath)) return new QuestTrackerDocument();
            var document = JsonSerializer.Deserialize<QuestTrackerDocument>(File.ReadAllText(StorePath), JsonOptions);
            document ??= new QuestTrackerDocument();
            document.TrackedItems ??= [];
            foreach (var item in document.TrackedItems)
            {
                item.ItemName = item.ItemName?.Trim() ?? string.Empty;
                item.QuestTitle = item.QuestTitle?.Trim() ?? string.Empty;
                item.VoiceText = item.VoiceText?.Trim() ?? string.Empty;
                item.AlertMode = BuffAlertModeOptions.Normalize(item.AlertMode);
            }

            document.TrackedItems = document.TrackedItems
                .Where(item => item.ItemName.Length > 0)
                .GroupBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            return document;
        }
        catch (IOException)
        {
            return new QuestTrackerDocument();
        }
        catch (UnauthorizedAccessException)
        {
            return new QuestTrackerDocument();
        }
        catch (JsonException)
        {
            return new QuestTrackerDocument();
        }
    }

    public static async Task<bool> TrySaveAsync(QuestTrackerDocument document,
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
