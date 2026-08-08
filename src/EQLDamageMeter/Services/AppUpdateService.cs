using System.IO;
using System.Reflection;
using System.Windows;
using AutoUpdaterDotNET;

namespace EQLDamageMeter.Services;

/// <summary>
/// Checks GitHub-hosted update.xml and applies zip updates into the portable
/// app folder. Runtime files such as settings.json and spelltracker.json are
/// not shipped in the release zip, so user preferences and spell rules survive
/// updates.
/// </summary>
public static class AppUpdateService
{
    public const string UpdateFeedUrl =
        "https://raw.githubusercontent.com/sayser/EQLDMG/main/update.xml";

    private static bool _configured;

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 2, 1, 0);

    public static string CurrentVersionText
    {
        get
        {
            var version = CurrentVersion;
            return version.Build >= 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : $"{version.Major}.{version.Minor}";
        }
    }

    public static void Configure(Window? owner = null)
    {
        if (!_configured)
        {
            _configured = true;
            AutoUpdater.InstalledVersion = CurrentVersion;
            AutoUpdater.AppCastURL = UpdateFeedUrl;
            AutoUpdater.RunUpdateAsAdmin = false;
            AutoUpdater.ShowSkipButton = true;
            AutoUpdater.ShowRemindLaterButton = true;
            AutoUpdater.InstallationPath = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            AutoUpdater.DownloadPath = Path.Combine(Path.GetTempPath(), "EQDMUpdates");
            AutoUpdater.ExecutablePath = "EQLDamageMeter.exe";
            AutoUpdater.HttpUserAgent = $"EQDM/{CurrentVersionText}";
            AutoUpdater.ApplicationExitEvent += () =>
            {
                foreach (Window window in Application.Current.Windows)
                    window.Close();
                Application.Current.Shutdown();
            };
        }

        if (owner is not null) AutoUpdater.SetOwner(owner);
    }

    public static void CheckForUpdates(Window? owner = null, bool reportNoUpdate = false)
    {
        Configure(owner);

        if (reportNoUpdate)
        {
            void Handler(UpdateInfoEventArgs args)
            {
                AutoUpdater.CheckForUpdateEvent -= Handler;
                if (args.Error is not null)
                {
                    MessageBox.Show(owner,
                        "Could not check for updates. Check your internet connection and try again.",
                        "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!args.IsUpdateAvailable)
                {
                    MessageBox.Show(owner,
                        $"EQDM {CurrentVersionText} is up to date.",
                        "No Update Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                AutoUpdater.ShowUpdateForm(args);
            }

            AutoUpdater.CheckForUpdateEvent += Handler;
        }

        // Bust GitHub raw CDN / local HTTP caches so the latest update.xml is fetched.
        var feedUrl = $"{UpdateFeedUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        AutoUpdater.Start(feedUrl);
    }
}
