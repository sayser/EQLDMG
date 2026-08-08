using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public static class AppSettingsStore
{
    private sealed class AppSettings
    {
        public string? LogFolder { get; set; }
        public SpellIconStyle SpellIconStyle { get; set; } = SpellIconStyle.Modern;
    }

    private static readonly string SettingsPath = AppPaths.Combine("settings.json");
    private static readonly SemaphoreSlim SettingsGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string? TryLoadLogFolder()
    {
        var settings = TryLoad();
        return string.IsNullOrWhiteSpace(settings?.LogFolder) ? null : settings.LogFolder;
    }

    public static SpellIconStyle TryLoadSpellIconStyle() =>
        TryLoad()?.SpellIconStyle ?? SpellIconStyle.Modern;

    public static Task<bool> TrySaveLogFolderAsync(string folder,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(settings => settings.LogFolder = Path.GetFullPath(folder), cancellationToken);

    public static Task<bool> TrySaveSpellIconStyleAsync(SpellIconStyle style,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(settings => settings.SpellIconStyle = style, cancellationToken);

    /// <summary>
    /// Rewrites settings.json without legacy Buff/DoT/Control rule lists after those
    /// rules have been moved to spelltracker.json.
    /// </summary>
    internal static bool TryStripLegacySpellRules()
    {
        SettingsGate.Wait();
        try
        {
            var settings = TryLoad() ?? new AppSettings();
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, SettingsPath, true);
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
            SettingsGate.Release();
        }
    }

    private static AppSettings? TryLoad()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                   ?? new AppSettings();
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

    private static async Task<bool> UpdateAsync(Action<AppSettings> update,
        CancellationToken cancellationToken)
    {
        await SettingsGate.WaitAsync(cancellationToken);
        try
        {
            var settings = TryLoad() ?? new AppSettings();
            update(settings);
            var temporaryPath = SettingsPath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 4 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, SettingsPath, true);
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
            SettingsGate.Release();
        }
    }
}
