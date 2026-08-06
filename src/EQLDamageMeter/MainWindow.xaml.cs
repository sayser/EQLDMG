using System.Windows;
using EQLDamageMeter.ViewModels;
using Microsoft.Win32;

namespace EQLDamageMeter;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly MainViewModel _viewModel = new();
    private OverlayWindow? _overlay;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closed += async (_, _) =>
        {
            await DisposeAsync();
        };
    }

    private async void ChooseLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the EverQuest Legends Logs folder",
            InitialDirectory = _viewModel.LogFolderText,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            var error = await _viewModel.LoadFolderAsync(dialog.FolderName);
            if (error is not null)
            {
                MessageBox.Show(this, error, "Logs folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void ResetEncounter_Click(object sender, RoutedEventArgs e) => _viewModel.ResetEncounter();

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Close();
            return;
        }

        _overlay = new OverlayWindow { DataContext = _viewModel, Owner = this };
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
    }

    private void ShowOffense_Click(object sender, RoutedEventArgs e) => _viewModel.ShowOffense();

    private void ShowDefense_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDefense();

    private void ShowHealing_Click(object sender, RoutedEventArgs e) => _viewModel.ShowHealing();

    public async ValueTask DisposeAsync()
    {
        _overlay?.Close();
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
