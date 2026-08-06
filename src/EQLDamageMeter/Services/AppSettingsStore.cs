using System.IO;
using System.Text.Json;

namespace EQLDamageMeter.Services;

public static class AppSettingsStore
{
    private sealed record AppSettings(string LogFolder);

    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static string? TryLoadLogFolder()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            return string.IsNullOrWhiteSpace(settings?.LogFolder) ? null : settings.LogFolder;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<bool> TrySaveLogFolderAsync(string folder,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4 * 1024, FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(stream, new AppSettings(Path.GetFullPath(folder)),
                cancellationToken: cancellationToken);
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
    }
}
