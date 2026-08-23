using System.Collections.ObjectModel;
using System.Windows;
using EQLDamageMeter.Controls;
using EQLDamageMeter.Services;

namespace EQLDamageMeter;

public partial class FightLogWindow : Window
{
    public FightLogWindow(
        string title,
        IReadOnlyList<FightLogEntry> entries,
        string? localPlayerName,
        IReadOnlyList<string>? knownActors = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubtitleText.Text = entries.Count == 0
            ? "No log lines captured for this fight."
            : $"{entries.Count:N0} lines";

        var lines = new ObservableCollection<FightLogLineViewModel>();
        var row = 0;
        foreach (var entry in entries)
        {
            lines.Add(new FightLogLineViewModel
            {
                Segments = FightLogFormatter.Format(entry, localPlayerName, knownActors),
                IsAltRow = row++ % 2 == 1
            });
        }
        LogList.ItemsSource = lines;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class FightLogLineViewModel
{
    public required IReadOnlyList<FightLogSegment> Segments { get; init; }
    public bool IsAltRow { get; init; }
}
