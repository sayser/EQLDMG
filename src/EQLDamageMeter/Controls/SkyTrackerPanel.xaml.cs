using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EQLDamageMeter.ViewModels;

namespace EQLDamageMeter.Controls;

public partial class SkyTrackerPanel : UserControl
{
    public SkyTrackerPanel() => InitializeComponent();

    private SkyTrackerViewModel? Tracker => DataContext as SkyTrackerViewModel;

    private async void ScanInventoryDump_Click(object sender, RoutedEventArgs e) =>
        await (Tracker?.ImportInventoryDumpAsync() ?? Task.CompletedTask);

    private void ToggleMissingTracking_Click(object sender, RoutedEventArgs e) =>
        Tracker?.ToggleMissingItemTracking();

    private void OpenPlanePage_Click(object sender, RoutedEventArgs e) => Tracker?.OpenSkyWiki();

    private void OpenRewardWiki_Click(object sender, MouseButtonEventArgs e)
    {
        Tracker?.OpenSelectedRewardWiki();
        e.Handled = true;
    }

    private void OpenItemWiki_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkyRequirementRowViewModel part })
            Tracker?.OpenItemWiki(part.ItemName);
        e.Handled = true;
    }
}
