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
    public static readonly string[] ProtectedFileNames =
    [
        "settings.json",
        "spelltracker.json",
        "session_info.json",
        "questtracker.json",
        "quest_catalog.json",
        "skytracker.json",
        "sky_catalog.json"
    ];

    private static string AppDirectory =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
            if (!Directory.Exists(BackupDirectory)) return;

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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
