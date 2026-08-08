using System.IO;
using System.Text.Json;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public static class SessionInfoStore
{
    private static readonly string StorePath = Path.Combine(AppContext.BaseDirectory, "session_info.json");
    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const int MaxStoredSessions = 100;

    public static IReadOnlyList<SessionRecord> TryLoadSessions()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            var document = JsonSerializer.Deserialize<SessionInfoDocument>(File.ReadAllText(StorePath), JsonOptions);
            return document?.Sessions ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task<bool> TrySaveSessionsAsync(IEnumerable<SessionRecord> sessions,
        CancellationToken cancellationToken = default)
    {
        await StoreGate.WaitAsync(cancellationToken);
        try
        {
            var document = new SessionInfoDocument
            {
                Sessions = sessions
                    .OrderByDescending(item => item.StartedAt)
                    .Take(MaxStoredSessions)
                    .ToList()
            };
            var temporaryPath = StorePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
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
