using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using EQLDamageMeter.Services;
using EQLDamageMeter.ViewModels;
using Microsoft.Win32;

namespace EQLDamageMeter;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly MainViewModel _viewModel = new();
    private OverlayWindow? _overlay;
    private BuffOverlayWindow? _buffOverlay;
    private SpellEffectOverlayWindow? _dotOverlay;
    private SpellEffectOverlayWindow? _controlOverlay;
    private bool _startupUpdateChecked;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
        Closed += async (_, _) =>
        {
            await DisposeAsync();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        if (_startupUpdateChecked) return;
        _startupUpdateChecked = true;
        AppUpdateService.CheckForUpdates(this);
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        AppUpdateService.CheckForUpdates(this, reportNoUpdate: true);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
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

    private void ToggleBuffOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_buffOverlay is { IsVisible: true })
        {
            _buffOverlay.Close();
            return;
        }

        _buffOverlay = new BuffOverlayWindow { DataContext = _viewModel, Owner = this };
        _buffOverlay.Closed += (_, _) => _buffOverlay = null;
        _buffOverlay.Show();
    }

    private void DotOverlay_Requested(object sender, RoutedEventArgs e) =>
        ToggleSpellOverlay(ref _dotOverlay, _viewModel.DotSpellTracker);

    private void ControlOverlay_Requested(object sender, RoutedEventArgs e) =>
        ToggleSpellOverlay(ref _controlOverlay, _viewModel.ControlSpellTracker);

    private void ToggleSpellOverlay(ref SpellEffectOverlayWindow? overlay, object dataContext)
    {
        if (overlay is { IsVisible: true })
        {
            overlay.Close();
            return;
        }
        var window = new SpellEffectOverlayWindow { DataContext = dataContext, Owner = this };
        overlay = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dotOverlay, window)) _dotOverlay = null;
            if (ReferenceEquals(_controlOverlay, window)) _controlOverlay = null;
        };
        window.Show();
    }

    private void AddBuffRule_Click(object sender, RoutedEventArgs e) => _viewModel.AddBuffRule();

    private async void DeleteBuffRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuffRuleViewModel rule }) return;
        var result = MessageBox.Show(this, $"Delete the tracking rule for {rule.SpellName}?",
            "Delete buff", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        ShowBuffError(await _viewModel.DeleteBuffRuleAsync(rule));
    }

    private async void BuffRuleToggle_Click(object sender, RoutedEventArgs e) =>
        ShowBuffError(await _viewModel.SaveBuffRulesAsync());

    private async void SaveBuffRules_Click(object sender, RoutedEventArgs e) =>
        ShowBuffError(await _viewModel.SaveBuffRulesAsync());

    private void TestBuffAlert_Click(object sender, RoutedEventArgs e) =>
        ShowBuffError(_viewModel.TestSelectedBuffAlert());

    private void BuffSpellName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Controls.SpellNameSearchBox { Tag: BuffRuleViewModel rule })
            _viewModel.ValidateBuffSpell(rule);
    }

    private void ShowAllBuffs_Checked(object sender, RoutedEventArgs e) => _viewModel.SetBuffFilter("All");
    private void ShowEnabledBuffs_Checked(object sender, RoutedEventArgs e) => _viewModel.SetBuffFilter("Enabled");
    private void ShowDisabledBuffs_Checked(object sender, RoutedEventArgs e) => _viewModel.SetBuffFilter("Disabled");

    private void ShowBuffError(string? error)
    {
        if (error is not null)
            MessageBox.Show(this, error, "Spell Tracker", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowOffense_Click(object sender, RoutedEventArgs e) => _viewModel.ShowOffense();

    private void ShowDefense_Click(object sender, RoutedEventArgs e) => _viewModel.ShowDefense();

    private void ShowHealing_Click(object sender, RoutedEventArgs e) => _viewModel.ShowHealing();

    public async ValueTask DisposeAsync()
    {
        _overlay?.Close();
        _buffOverlay?.Close();
        _dotOverlay?.Close();
        _controlOverlay?.Close();
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
