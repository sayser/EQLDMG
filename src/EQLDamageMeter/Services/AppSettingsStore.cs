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
        public List<BuffRuleSettings> BuffRules { get; set; } = [];
        public List<BuffRuleSettings> DotRules { get; set; } = [];
        public List<BuffRuleSettings> ControlRules { get; set; } = [];
    }

    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
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

    public static IReadOnlyList<BuffRuleSettings> TryLoadBuffRules() =>
        TryLoad()?.BuffRules ?? [];

    public static IReadOnlyList<BuffRuleSettings> TryLoadDotRules() =>
        TryLoad()?.DotRules ?? [];

    public static IReadOnlyList<BuffRuleSettings> TryLoadControlRules() =>
        TryLoad()?.ControlRules ?? [];

    public static Task<bool> TrySaveLogFolderAsync(string folder,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(settings => settings.LogFolder = Path.GetFullPath(folder), cancellationToken);

    public static Task<bool> TrySaveSpellIconStyleAsync(SpellIconStyle style,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(settings => settings.SpellIconStyle = style, cancellationToken);

    public static Task<bool> TrySaveBuffRulesAsync(IEnumerable<BuffRuleSettings> rules,
        CancellationToken cancellationToken = default)
    {
        var snapshot = rules.ToList();
        return UpdateAsync(settings => settings.BuffRules = snapshot, cancellationToken);
    }

    public static Task<bool> TrySaveDotRulesAsync(IEnumerable<BuffRuleSettings> rules,
        CancellationToken cancellationToken = default)
    {
        var snapshot = rules.ToList();
        return UpdateAsync(settings => settings.DotRules = snapshot, cancellationToken);
    }

    public static Task<bool> TrySaveControlRulesAsync(IEnumerable<BuffRuleSettings> rules,
        CancellationToken cancellationToken = default)
    {
        var snapshot = rules.ToList();
        return UpdateAsync(settings => settings.ControlRules = snapshot, cancellationToken);
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
