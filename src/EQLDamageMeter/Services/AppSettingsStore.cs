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
        public Dictionary<string, OverlayBounds> OverlayBounds { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> OverlayCompact { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
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

    public static bool TryLoadOverlayBounds(string key, out OverlayBounds? bounds)
    {
        bounds = null;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var settings = TryLoad();
        if (settings?.OverlayBounds is null) return false;
        if (!settings.OverlayBounds.TryGetValue(key, out var stored) || stored is null) return false;
        if (stored.Width < 80 || stored.Height < 40) return false;
        if (double.IsNaN(stored.Left) || double.IsNaN(stored.Top) ||
            double.IsNaN(stored.Width) || double.IsNaN(stored.Height)) return false;
        bounds = stored;
        return true;
    }

    public static Task<bool> TrySaveOverlayBoundsAsync(string key, OverlayBounds bounds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || bounds is null) return Task.FromResult(false);
        return UpdateAsync(settings =>
        {
            settings.OverlayBounds ??= new Dictionary<string, OverlayBounds>(StringComparer.OrdinalIgnoreCase);
            settings.OverlayBounds[key] = bounds;
        }, cancellationToken);
    }

    public static bool TryLoadOverlayCompact(string key) =>
        !string.IsNullOrWhiteSpace(key) &&
        TryLoad()?.OverlayCompact is { } map &&
        map.TryGetValue(key, out var compact) &&
        compact;

    public static Task<bool> TrySaveOverlayCompactAsync(string key, bool compact,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        return UpdateAsync(settings =>
        {
            settings.OverlayCompact ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            settings.OverlayCompact[key] = compact;
        }, cancellationToken);
    }

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
