using System.Windows;
using EQLDamageMeter.Services;

namespace EQLDamageMeter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppUpdateService.InitializeUserDataProtection();
        BuffAlertService.VolumePercent = AppSettingsStore.TryLoadAlertVolumePercent();
        SpeechPlayback.Settings = AppSettingsStore.TryLoadVoice();
        base.OnStartup(e);
    }
}
