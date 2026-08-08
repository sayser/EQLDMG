using System.Windows;
using EQLDamageMeter.Services;

namespace EQLDamageMeter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppUpdateService.InitializeUserDataProtection();
        base.OnStartup(e);
    }
}
