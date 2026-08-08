using System.IO;
using System.Reflection;
using System.Windows;
using AutoUpdaterDotNET;

namespace EQLDamageMeter.Services;

/// <summary>
/// Checks GitHub-hosted update.xml and applies zip updates into the portable
/// app folder. Runtime JSON listed in <see cref="UserDataGuard.ProtectedFileNames"/>
/// must never be shipped in the release zip. Before the updater replaces app files,
/// user data is flushed, backed up, and always restored on the next launch.
/// </summary>
public static class AppUpdateService
{
    public const string UpdateFeedUrl =
        "https://raw.githubusercontent.com/sayser/EQLDMG/main/update.xml";

    private static bool _configured;

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 2, 7, 0);

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
            AutoUpdater.InstallationPath = AppPaths.AppDirectory;
            AutoUpdater.DownloadPath = Path.Combine(Path.GetTempPath(), "EQDMUpdates");
            // Relative to InstallationPath; ZipExtractor relaunches this after extract.
            AutoUpdater.ExecutablePath = "EQLDamageMeter.exe";
            AutoUpdater.HttpUserAgent = $"EQDM/{CurrentVersionText}";
            AutoUpdater.ApplicationExitEvent += OnApplicationExitForUpdate;
        }

        if (owner is not null) AutoUpdater.SetOwner(owner);
    }

    public static void InitializeUserDataProtection() =>
        UserDataGuard.RestoreAfterUpdateIfNeeded();

    /// <summary>
    /// AutoUpdater.NET's ZipExtractor blocks on "Waiting for application to exit...".
    /// Never sync-over-async Dispose on the UI thread here — that deadlocks WPF and
    /// leaves the updater hung forever. Flush best-effort, backup, then kill the process.
    /// </summary>
    private static void OnApplicationExitForUpdate()
    {
        try
        {
            global::EQLDamageMeter.MainWindow? mainWindow = null;
            if (Application.Current is { } app)
            {
                foreach (Window window in app.Windows)
                {
                    if (window is global::EQLDamageMeter.MainWindow candidate)
                    {
                        mainWindow = candidate;
                        break;
                    }
                }
            }

            if (mainWindow is not null)
            {
                // Run dispose off the UI thread with a hard timeout so a stuck
                // log/monitor flush cannot block the updater.
                var flush = Task.Run(() => mainWindow.DisposeAsync().AsTask());
                flush.Wait(TimeSpan.FromSeconds(4));
            }

            UserDataGuard.BackupBeforeUpdate();
        }
        catch (Exception)
        {
            // Prefer exiting so the zip extractor can replace the exe.
        }

        Environment.Exit(0);
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
