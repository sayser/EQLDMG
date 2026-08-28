using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using EQLDamageMeter.Services;

namespace EQLDamageMeter;

public partial class SkyNeededItemsWindow : Window
{
    public SkyNeededItemsWindow(IReadOnlyList<SkyNeededIslandGroup> groups)
    {
        InitializeComponent();
        DataContext = groups;
        var itemCount = groups.Sum(island => island.ItemCount);
        SubtitleText.Text = itemCount == 0
            ? "Nothing left to farm. Completed quests and items you already have are hidden."
            : $"{itemCount} item{(itemCount == 1 ? "" : "s")} still needed, grouped by island and boss.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenItemWiki_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkyNeededItem item })
        {
            var url = EqWikiLinks.ForPage(item.ItemName);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        e.Handled = true;
    }
}
