using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public static class SpellTrackerStore
{
    private sealed class SpellTrackerSettings
    {
        public List<BuffRuleSettings> BuffRules { get; set; } = [];
        public List<BuffRuleSettings> DotRules { get; set; } = [];
        public List<BuffRuleSettings> ControlRules { get; set; } = [];
    }

    /// <summary>
    /// Older builds stored spell rules inside settings.json. Used only to migrate
    /// once into spelltracker.json.
    /// </summary>
    private sealed class LegacySettingsDocument
    {
        public List<BuffRuleSettings>? BuffRules { get; set; }
        public List<BuffRuleSettings>? DotRules { get; set; }
        public List<BuffRuleSettings>? ControlRules { get; set; }
    }

    private static readonly string TrackerPath = Path.Combine(AppContext.BaseDirectory, "spelltracker.json");
    private static readonly string LegacySettingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly SemaphoreSlim TrackerGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<BuffRuleSettings> TryLoadBuffRules() =>
        TryLoad()?.BuffRules ?? [];

    public static IReadOnlyList<BuffRuleSettings> TryLoadDotRules() =>
        TryLoad()?.DotRules ?? [];

    public static IReadOnlyList<BuffRuleSettings> TryLoadControlRules() =>
        TryLoad()?.ControlRules ?? [];

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

    private static SpellTrackerSettings? TryLoad()
    {
        try
        {
            MigrateFromLegacySettingsIfNeeded();
            if (!File.Exists(TrackerPath)) return new SpellTrackerSettings();
            return JsonSerializer.Deserialize<SpellTrackerSettings>(File.ReadAllText(TrackerPath), JsonOptions)
                   ?? new SpellTrackerSettings();
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

    private static void MigrateFromLegacySettingsIfNeeded()
    {
        if (File.Exists(TrackerPath) || !File.Exists(LegacySettingsPath)) return;

        LegacySettingsDocument? legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<LegacySettingsDocument>(
                File.ReadAllText(LegacySettingsPath), JsonOptions);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (JsonException)
        {
            return;
        }

        if (legacy is null) return;
        var hasRules = (legacy.BuffRules?.Count ?? 0) > 0 ||
                       (legacy.DotRules?.Count ?? 0) > 0 ||
                       (legacy.ControlRules?.Count ?? 0) > 0;
        if (!hasRules) return;

        var migrated = new SpellTrackerSettings
        {
            BuffRules = legacy.BuffRules ?? [],
            DotRules = legacy.DotRules ?? [],
            ControlRules = legacy.ControlRules ?? []
        };

        try
        {
            var temporaryPath = TrackerPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(migrated, JsonOptions));
            File.Move(temporaryPath, TrackerPath, true);
            AppSettingsStore.TryStripLegacySpellRules();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<bool> UpdateAsync(Action<SpellTrackerSettings> update,
        CancellationToken cancellationToken)
    {
        await TrackerGate.WaitAsync(cancellationToken);
        try
        {
            var settings = TryLoad() ?? new SpellTrackerSettings();
            update(settings);
            var temporaryPath = TrackerPath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, 4 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, TrackerPath, true);
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
            TrackerGate.Release();
        }
    }
}
