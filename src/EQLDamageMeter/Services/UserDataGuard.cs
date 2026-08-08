using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

/// <summary>
/// Protects portable runtime JSON files that must survive AutoUpdater.NET zip updates.
/// Release zips must never include these files. Before an update applies, the guard
/// copies them to a temp backup; on the next launch it always restores those backups
/// over whatever the zip left behind, then deletes the backups.
/// </summary>
public static class UserDataGuard
{
    public static readonly string[] ProtectedFileNames = AppPaths.UserJsonFileNames;

    private static string AppDirectory => AppPaths.AppDirectory;

    private static string BackupDirectory =>
        Path.Combine(Path.GetTempPath(), "EQDMUpdates", "UserDataBackup");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void BackupBeforeUpdate()
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            foreach (var fileName in ProtectedFileNames)
            {
                var source = Path.Combine(AppDirectory, fileName);
                if (!File.Exists(source)) continue;
                File.Copy(source, Path.Combine(BackupDirectory, fileName), overwrite: true);
            }
        }
        catch (IOException)
        {
            // Best-effort protection; update should still proceed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void RestoreAfterUpdateIfNeeded()
    {
        try
        {
            if (Directory.Exists(BackupDirectory))
            {
                // Backups were taken from the user's live files immediately before the updater
                // replaced the app folder. Always prefer those backups over anything the zip
                // may have extracted (including empty/default JSON).
                foreach (var fileName in ProtectedFileNames)
                {
                    var backup = Path.Combine(BackupDirectory, fileName);
                    if (!File.Exists(backup)) continue;

                    var destination = Path.Combine(AppDirectory, fileName);
                    File.Copy(backup, destination, overwrite: true);
                }

                foreach (var fileName in ProtectedFileNames)
                {
                    var backup = Path.Combine(BackupDirectory, fileName);
                    if (File.Exists(backup)) File.Delete(backup);
                }
            }

            MigrateFromSingleFileExtractIfNeeded();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Older single-file builds wrote user JSON into %TEMP%\.net\EQLDamageMeter\&lt;hash&gt;\
    /// because they used AppContext.BaseDirectory. Pull those files next to the exe
    /// when the portable folder is missing them, and merge spelltracker rules.
    /// </summary>
    private static void MigrateFromSingleFileExtractIfNeeded()
    {
        var extractRoot = Path.Combine(Path.GetTempPath(), ".net", "EQLDamageMeter");
        if (!Directory.Exists(extractRoot)) return;

        var extractDirs = new DirectoryInfo(extractRoot).GetDirectories()
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToArray();
        if (extractDirs.Length == 0) return;

        foreach (var fileName in ProtectedFileNames)
        {
            var destination = Path.Combine(AppDirectory, fileName);
            foreach (var directory in extractDirs)
            {
                var source = Path.Combine(directory.FullName, fileName);
                if (!File.Exists(source) || new FileInfo(source).Length == 0) continue;

                if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                {
                    File.Copy(source, destination, overwrite: true);
                    break;
                }

                if (TryMergeTrackerFile(fileName, destination, source))
                    break;
            }
        }
    }

    private static bool TryMergeTrackerFile(string fileName, string destinationPath, string sourcePath)
    {
        try
        {
            if (fileName.Equals("spelltracker.json", StringComparison.OrdinalIgnoreCase))
                return TryRewrite(destinationPath, () =>
                {
                    var destination = ReadJson<SpellTrackerDocument>(destinationPath);
                    var source = ReadJson<SpellTrackerDocument>(sourcePath);
                    return MergeRules(destination.BuffRules, source.BuffRules) |
                           MergeRules(destination.DotRules, source.DotRules) |
                           MergeRules(destination.ControlRules, source.ControlRules)
                        ? destination
                        : null;
                });

            if (fileName.Equals("questtracker.json", StringComparison.OrdinalIgnoreCase))
                return TryRewrite(destinationPath, () =>
                {
                    var destination = ReadJson<QuestTrackerDocument>(destinationPath);
                    var source = ReadJson<QuestTrackerDocument>(sourcePath);
                    destination.TrackedItems ??= [];
                    source.TrackedItems ??= [];
                    var known = new HashSet<string>(
                        destination.TrackedItems.Select(item => $"{item.ItemName}\u001f{item.QuestTitle}"),
                        StringComparer.OrdinalIgnoreCase);
                    var changed = false;
                    foreach (var item in source.TrackedItems)
                    {
                        if (string.IsNullOrWhiteSpace(item.ItemName)) continue;
                        var key = $"{item.ItemName}\u001f{item.QuestTitle}";
                        if (!known.Add(key)) continue;
                        destination.TrackedItems.Add(item);
                        changed = true;
                    }
                    return changed ? destination : null;
                });

            if (fileName.Equals("skytracker.json", StringComparison.OrdinalIgnoreCase))
                return TryRewrite(destinationPath, () =>
                {
                    var destination = ReadJson<SkyTrackerDocument>(destinationPath);
                    var source = ReadJson<SkyTrackerDocument>(sourcePath);
                    destination.Goals ??= [];
                    source.Goals ??= [];
                    var knownIds = new HashSet<string>(
                        destination.Goals.Select(goal => goal.Id), StringComparer.OrdinalIgnoreCase);
                    var knownKeys = new HashSet<string>(
                        destination.Goals.Select(goal => $"{goal.ClassName}\u001f{goal.RewardName}"),
                        StringComparer.OrdinalIgnoreCase);
                    var changed = false;
                    foreach (var goal in source.Goals)
                    {
                        if (!string.IsNullOrWhiteSpace(goal.Id) && !knownIds.Add(goal.Id)) continue;
                        var key = $"{goal.ClassName}\u001f{goal.RewardName}";
                        if (!knownKeys.Add(key)) continue;
                        destination.Goals.Add(goal);
                        changed = true;
                    }
                    return changed ? destination : null;
                });

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static T ReadJson<T>(string path) where T : new() =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? new T();

    private static bool TryRewrite<T>(string destinationPath, Func<T?> build)
    {
        var document = build();
        if (document is null) return false;
        var temporaryPath = destinationPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, destinationPath, true);
        return true;
    }

    private static bool MergeRules(List<BuffRuleSettings> destination, List<BuffRuleSettings> source)
    {
        if (source.Count == 0) return false;
        var known = new HashSet<Guid>(destination.Select(rule => rule.Id));
        var knownNames = new HashSet<string>(
            destination.Select(rule => rule.SpellName), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var rule in source)
        {
            if (known.Contains(rule.Id) || knownNames.Contains(rule.SpellName)) continue;
            destination.Add(rule);
            known.Add(rule.Id);
            knownNames.Add(rule.SpellName);
            changed = true;
        }
        return changed;
    }

    private sealed class SpellTrackerDocument
    {
        public List<BuffRuleSettings> BuffRules { get; set; } = [];
        public List<BuffRuleSettings> DotRules { get; set; } = [];
        public List<BuffRuleSettings> ControlRules { get; set; } = [];
    }
}
