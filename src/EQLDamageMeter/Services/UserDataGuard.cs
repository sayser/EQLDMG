using System.IO;

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
                var backups = ProtectedFileNames
                    .Select(fileName => Path.Combine(BackupDirectory, fileName))
                    .Where(File.Exists)
                    .ToArray();

                // Only restore when a pre-update backup actually exists. An empty backup
                // folder must not keep triggering migration side effects forever.
                if (backups.Length > 0)
                {
                    // Backups were taken from the user's live files immediately before the updater
                    // replaced the app folder. Always prefer those backups over anything the zip
                    // may have extracted (including empty/default JSON).
                    foreach (var backup in backups)
                    {
                        var destination = Path.Combine(AppDirectory, Path.GetFileName(backup));
                        File.Copy(backup, destination, overwrite: true);
                    }

                    foreach (var backup in backups)
                        File.Delete(backup);
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(BackupDirectory).Any())
                        Directory.Delete(BackupDirectory);
                }
                catch (IOException)
                {
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
    /// because they used AppContext.BaseDirectory. Copy those files next to the exe only
    /// when the portable folder is missing them.
    /// Do not merge into an existing spelltracker.json — that re-added deleted rules
    /// (e.g. Sha's Legacy) from stale extract copies on every launch.
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
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                continue;

            foreach (var directory in extractDirs)
            {
                var source = Path.Combine(directory.FullName, fileName);
                if (!File.Exists(source) || new FileInfo(source).Length == 0) continue;
                File.Copy(source, destination, overwrite: true);
                break;
            }
        }
    }
}
