using System.IO;

namespace EQLDamageMeter.Services;

/// <summary>
/// Resolves the portable app folder where user JSON lives (next to EQLDamageMeter.exe).
/// Single-file publish extracts assemblies under %TEMP%\.net\, so
/// <see cref="AppContext.BaseDirectory"/> must not be used for user data or updates.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Runtime JSON persisted beside the executable.
    /// </summary>
    public static readonly string[] UserJsonFileNames =
    [
        "settings.json",
        "spelltracker.json",
        "session_info.json",
        "questtracker.json",
        "quest_catalog.json",
        "skytracker.json",
        "sky_catalog.json"
    ];

    public static string AppDirectory { get; } = ResolveAppDirectory();

    public static string Combine(string fileName) => Path.Combine(AppDirectory, fileName);

    private static string ResolveAppDirectory()
    {
        var baseDirectory = NormalizeDirectory(AppContext.BaseDirectory);

        // Single-file host: assemblies extract under %TEMP%\.net\<app>\<hash>\.
        // User JSON and zip updates must target the real exe folder instead.
        if (IsSingleFileExtractDirectory(baseDirectory))
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    var processDirectory = Path.GetDirectoryName(Path.GetFullPath(processPath));
                    if (!string.IsNullOrWhiteSpace(processDirectory) &&
                        Directory.Exists(processDirectory))
                        return processDirectory;
                }
            }
            catch (IOException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        return baseDirectory;
    }

    private static bool IsSingleFileExtractDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        var extractRoot = NormalizeDirectory(Path.Combine(Path.GetTempPath(), ".net"));
        return directory.StartsWith(extractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || directory.Equals(extractRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
