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
        public Dictionary<string, bool> OverlayLocked { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public MouseHighlightSettings MouseHighlight { get; set; } = new();
        public int AlertVolumePercent { get; set; } = 100;
        public VoiceSettings Voice { get; set; } = new();
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

    public static bool TryLoadOverlayLocked(string key) =>
        !string.IsNullOrWhiteSpace(key) &&
        TryLoad()?.OverlayLocked is { } map &&
        map.TryGetValue(key, out var locked) &&
        locked;

    public static Task<bool> TrySaveOverlayLockedAsync(string key, bool locked,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return Task.FromResult(false);
        return UpdateAsync(settings =>
        {
            settings.OverlayLocked ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            settings.OverlayLocked[key] = locked;
        }, cancellationToken);
    }

    public static MouseHighlightSettings TryLoadMouseHighlight()
    {
        var settings = TryLoad()?.MouseHighlight;
        if (settings is null) return new MouseHighlightSettings();
        settings.Diameter = Math.Clamp(settings.Diameter <= 0 ? 48 : settings.Diameter, 16, 200);
        settings.Thickness = Math.Clamp(settings.Thickness <= 0 ? 3 : settings.Thickness, 1, 16);
        settings.Opacity = Math.Clamp(settings.Opacity <= 0 ? 0.85 : settings.Opacity, 0.15, 1.0);
        settings.BlinkHz = Math.Clamp(settings.BlinkHz <= 0 ? 2.0 : settings.BlinkHz, 0.5, 8.0);
        settings.SecondDiameter = Math.Clamp(settings.SecondDiameter <= 0 ? 84 : settings.SecondDiameter, 20, 260);
        if (string.IsNullOrWhiteSpace(settings.ColorHex)) settings.ColorHex = "#FF5522";
        return settings;
    }

    public static Task<bool> TrySaveMouseHighlightAsync(MouseHighlightSettings options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return UpdateAsync(settings => settings.MouseHighlight = new MouseHighlightSettings
        {
            Enabled = options.Enabled,
            ColorHex = options.ColorHex,
            Diameter = Math.Clamp(options.Diameter, 16, 200),
            Thickness = Math.Clamp(options.Thickness, 1, 16),
            Opacity = Math.Clamp(options.Opacity, 0.15, 1.0),
            Blink = options.Blink,
            BlinkHz = Math.Clamp(options.BlinkHz <= 0 ? 2.0 : options.BlinkHz, 0.5, 8.0),
            SecondRing = options.SecondRing,
            SecondDiameter = Math.Clamp(options.SecondDiameter <= 0 ? 84 : options.SecondDiameter, 20, 260)
        }, cancellationToken);
    }

    public static int TryLoadAlertVolumePercent()
    {
        var volume = TryLoad()?.AlertVolumePercent ?? 100;
        return Math.Clamp(volume, 0, 100);
    }

    public static VoiceSettings TryLoadVoice()
    {
        var voice = TryLoad()?.Voice ?? new VoiceSettings();
        voice.WindowsVoiceId ??= string.Empty;
        voice.NaturalVoiceId = string.IsNullOrWhiteSpace(voice.NaturalVoiceId) ? "af_heart" : voice.NaturalVoiceId;
        voice.BossDefeatSoundPath = EventSoundService.ResolveSoundPath(voice.BossDefeatSoundPath);
        return voice;
    }

    public static Task<bool> TrySaveVoiceAsync(VoiceSettings voice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(voice);
        return UpdateAsync(settings => settings.Voice = new VoiceSettings
        {
            Engine = voice.Engine,
            WindowsVoiceId = voice.WindowsVoiceId?.Trim() ?? string.Empty,
            NaturalVoiceId = string.IsNullOrWhiteSpace(voice.NaturalVoiceId) ? "af_heart" : voice.NaturalVoiceId.Trim(),
            BossDefeatSoundEnabled = voice.BossDefeatSoundEnabled,
            BossDefeatSoundPath = EventSoundService.PersistSoundPath(voice.BossDefeatSoundPath)
        }, cancellationToken);
    }

    public static Task<bool> TrySaveAlertVolumePercentAsync(int volumePercent,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(settings => settings.AlertVolumePercent = Math.Clamp(volumePercent, 0, 100),
            cancellationToken);

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
