using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
    private SpellEffectOverlayWindow? _hostileOverlay;
    private MouseHighlightOverlayWindow? _mouseHighlight;
    private MouseHighlightSettings _mouseHighlightSettings = AppSettingsStore.TryLoadMouseHighlight();
    private bool _startupUpdateChecked;
    private bool _disposed;
    private readonly DispatcherTimer _manualTimerTick;
    private readonly Stopwatch _manualStopwatch = new();
    private bool _manualTimerRunning;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _manualTimerTick = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _manualTimerTick.Tick += (_, _) => RefreshManualTimerDisplay();
        _viewModel.OverlayLockChanged += ApplyOverlayLock;
        _viewModel.DotSpellTracker.OverlayLockChanged += ApplyOverlayLock;
        _viewModel.ControlSpellTracker.OverlayLockChanged += ApplyOverlayLock;
        _viewModel.HostileSpellTracker.OverlayLockChanged += ApplyOverlayLock;
        Loaded += MainWindow_Loaded;
        Closed += async (_, _) =>
        {
            await DisposeAsync();
        };
    }

    private void ApplyOverlayLock(string key, bool locked)
    {
        Window? window = key switch
        {
            OverlayWindowPlacement.DpsKey => _overlay,
            OverlayWindowPlacement.BuffKey => _buffOverlay,
            OverlayWindowPlacement.DotKey => _dotOverlay,
            OverlayWindowPlacement.ControlKey => _controlOverlay,
            OverlayWindowPlacement.HostileKey => _hostileOverlay,
            _ => null
        };
        if (window is not null)
            OverlayClickThrough.SetLocked(window, locked);
        else
            _ = AppSettingsStore.TrySaveOverlayLockedAsync(key, locked);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyMouseHighlight();
        await _viewModel.InitializeAsync();
        if (_startupUpdateChecked) return;
        _startupUpdateChecked = true;
        AppUpdateService.CheckForUpdates(this);
    }

    private async void MouseHighlightOptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new MouseHighlightSettingsWindow(_mouseHighlightSettings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _mouseHighlightSettings = dialog.Result;
        await AppSettingsStore.TrySaveMouseHighlightAsync(_mouseHighlightSettings);
        ApplyMouseHighlight();
    }

    private void ApplyMouseHighlight()
    {
        // No Owner — owned windows hide when the main window minimizes.
        if (_mouseHighlightSettings.Enabled)
            _mouseHighlight ??= new MouseHighlightOverlayWindow();
        _mouseHighlight?.ApplyOptions(_mouseHighlightSettings);
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        AppUpdateService.CheckForUpdates(this, reportNoUpdate: true);

    private void ManualTimerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_manualTimerRunning)
        {
            _manualStopwatch.Stop();
            _manualTimerTick.Stop();
            _manualTimerRunning = false;
            if (ManualTimerButton is not null)
                ManualTimerButton.Content = "START TIMER";
            RefreshManualTimerDisplay();
            return;
        }

        _manualStopwatch.Reset();
        _manualStopwatch.Start();
        _manualTimerRunning = true;
        if (ManualTimerButton is not null)
            ManualTimerButton.Content = "STOP TIMER";
        RefreshManualTimerDisplay();
        _manualTimerTick.Start();
    }

    private void RefreshManualTimerDisplay()
    {
        if (ManualTimerText is null) return;
        var elapsed = _manualStopwatch.Elapsed;
        ManualTimerText.Text = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    private void SessionExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionEntryViewModel session })
            session.ToggleExpanded();
        e.Handled = true;
    }

    private void SessionMobSelect_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionMobLootRowViewModel mob }) return;
        var session = FindParentDataContext<SessionEntryViewModel>(sender as DependencyObject);
        if (session is null) return;
        _viewModel.SessionHistory.SelectMob(session, mob);
        e.Handled = true;
    }

    private void SessionMobClear_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SessionHistory.ClearSelectedMob();
        e.Handled = true;
    }

    private void OpenSessionMobWiki_Click(object sender, RoutedEventArgs e)
    {
        var mob = _viewModel.SessionHistory.SelectedMob
                  ?? (sender as FrameworkElement)?.DataContext as SessionMobLootRowViewModel;
        mob?.OpenWiki();
        e.Handled = true;
    }

    private static T? FindParentDataContext<T>(DependencyObject? current) where T : class
    {
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: T match })
                return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

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

    private async void PopulateSessionFromLog_Click(object sender, RoutedEventArgs e)
    {
        var error = await _viewModel.PopulateSessionFromLastHoursAsync(3);
        if (error is not null)
        {
            MessageBox.Show(this, error, "Session Tracker", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(this, "Session Tracker was populated from the last 3 hours of your character log.",
            "Session Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void QuestRefreshCatalog_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.QuestTracker.RefreshCatalogAsync();

    private async void QuestOpenSearch_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.QuestTracker.LoadSelectedSearchAsync();

    private async void QuestSuggestionChosen(object sender, RoutedEventArgs e)
    {
        var title = _viewModel.QuestTracker.SearchText;
        if (!string.IsNullOrWhiteSpace(title))
            await _viewModel.QuestTracker.SelectSuggestionAsync(title);
    }

    private void QuestTrackItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuestItemRowViewModel item })
            _viewModel.QuestTracker.TrackSuggestedItem(item);
    }

    private void QuestUntrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackedQuestItemViewModel item })
            _viewModel.QuestTracker.UntrackItem(item);
    }

    private void QuestOpenWiki_Click(object sender, RoutedEventArgs e) =>
        _viewModel.QuestTracker.OpenSelectedQuestWiki();

    private async void SkyRefreshCatalog_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.SkyTracker.RefreshCatalogAsync();

    private async void SkyAddSelected_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.SkyTracker.AddSelectedPartsAsync();

    private void SkyOpenWiki_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SkyTracker.OpenSelectedRewardWiki();

    private void SkyOpenPlanePage_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SkyTracker.OpenSkyWiki();

    private void SkyRemoveGoal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkyTrackedGoalViewModel goal })
            _viewModel.SkyTracker.RemoveGoal(goal);
    }

    private void SkyRemovePart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkyTrackedPartViewModel part })
            _viewModel.SkyTracker.RemovePart(part);
    }

    private void SkyFoundMinus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkyTrackedPartViewModel part } && part.FoundCount > 0)
        {
            part.FoundCount--;
            _viewModel.SkyTracker.SelectedGoal?.RefreshProgress();
        }
    }

    private void SkyFoundPlus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SkyTrackedPartViewModel part })
        {
            part.FoundCount++;
            _viewModel.SkyTracker.SelectedGoal?.RefreshProgress();
        }
    }

    private void ResetEncounter_Click(object sender, RoutedEventArgs e) => _viewModel.ResetEncounter();

    private void ToggleAllOverlays_Click(object sender, RoutedEventArgs e)
    {
        if (AnyOverlayOpen())
            CloseAllOverlays();
        else
            OpenAllOverlays();
        RefreshOverlaysToggleButton();
    }

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Close();
            return;
        }

        ShowDpsOverlay();
    }

    private void ToggleBuffOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_buffOverlay is { IsVisible: true })
        {
            _buffOverlay.Close();
            return;
        }

        ShowBuffOverlay();
    }

    private void DotOverlay_Requested(object sender, RoutedEventArgs e) =>
        ToggleSpellOverlay(ref _dotOverlay, _viewModel.DotSpellTracker, OverlayWindowPlacement.DotKey);

    private void ControlOverlay_Requested(object sender, RoutedEventArgs e) =>
        ToggleSpellOverlay(ref _controlOverlay, _viewModel.ControlSpellTracker,
            OverlayWindowPlacement.ControlKey);

    private void HostileOverlay_Requested(object sender, RoutedEventArgs e) =>
        ToggleSpellOverlay(ref _hostileOverlay, _viewModel.HostileSpellTracker,
            OverlayWindowPlacement.HostileKey);

    private void OpenAllOverlays()
    {
        ShowDpsOverlay();
        ShowBuffOverlay();
        ShowSpellOverlay(ref _dotOverlay, _viewModel.DotSpellTracker, OverlayWindowPlacement.DotKey);
        ShowSpellOverlay(ref _controlOverlay, _viewModel.ControlSpellTracker,
            OverlayWindowPlacement.ControlKey);
        ShowSpellOverlay(ref _hostileOverlay, _viewModel.HostileSpellTracker,
            OverlayWindowPlacement.HostileKey);
    }

    private void CloseAllOverlays()
    {
        _overlay?.Close();
        _buffOverlay?.Close();
        _dotOverlay?.Close();
        _controlOverlay?.Close();
        _hostileOverlay?.Close();
    }

    private bool AnyOverlayOpen() =>
        _overlay is { IsVisible: true } ||
        _buffOverlay is { IsVisible: true } ||
        _dotOverlay is { IsVisible: true } ||
        _controlOverlay is { IsVisible: true } ||
        _hostileOverlay is { IsVisible: true };

    private void RefreshOverlaysToggleButton()
    {
        if (OverlaysToggleButton is null) return;
        var open = AnyOverlayOpen();
        OverlaysToggleButton.Content = open ? "Close all overlays" : "Open all overlays";
        OverlaysToggleButton.ToolTip = open
            ? "Close DPS, Buff, DoT, Control, and Hostile overlays"
            : "Open DPS, Buff, DoT, Control, and Hostile overlays";
    }

    private void ShowDpsOverlay()
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Activate();
            return;
        }

        _overlay = new OverlayWindow { DataContext = _viewModel };
        OverlayWindowPlacement.Attach(_overlay, OverlayWindowPlacement.DpsKey);
        _overlay.Closed += (_, _) =>
        {
            _overlay = null;
            RefreshOverlaysToggleButton();
        };
        _overlay.Show();
        RefreshOverlaysToggleButton();
    }

    private void ShowBuffOverlay()
    {
        if (_buffOverlay is { IsVisible: true })
        {
            _buffOverlay.Activate();
            return;
        }

        _buffOverlay = new BuffOverlayWindow { DataContext = _viewModel };
        OverlayWindowPlacement.Attach(_buffOverlay, OverlayWindowPlacement.BuffKey);
        if (_viewModel.IsCompactBuffOverlay &&
            !AppSettingsStore.TryLoadOverlayBounds(OverlayWindowPlacement.BuffKey, out _))
        {
            _buffOverlay.Width = 240;
            _buffOverlay.Height = 140;
        }
        _buffOverlay.Closed += (_, _) =>
        {
            _buffOverlay = null;
            RefreshOverlaysToggleButton();
        };
        _buffOverlay.Show();
        RefreshOverlaysToggleButton();
    }

    private void ToggleSpellOverlay(ref SpellEffectOverlayWindow? overlay, object dataContext,
        string placementKey)
    {
        if (overlay is { IsVisible: true })
        {
            overlay.Close();
            return;
        }

        ShowSpellOverlay(ref overlay, dataContext, placementKey);
    }

    private void ShowSpellOverlay(ref SpellEffectOverlayWindow? overlay, object dataContext,
        string placementKey)
    {
        if (overlay is { IsVisible: true })
        {
            overlay.Activate();
            return;
        }

        var window = new SpellEffectOverlayWindow
        {
            DataContext = dataContext,
            Tag = placementKey
        };
        OverlayWindowPlacement.Attach(window, placementKey);
        // Compact defaults only when this overlay has no saved bounds yet (e.g. first Hostile open).
        if (dataContext is SpellRuleSetViewModel { IsCompactOverlay: true } &&
            !AppSettingsStore.TryLoadOverlayBounds(placementKey, out _))
        {
            window.Width = 250;
            window.Height = 150;
        }
        overlay = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dotOverlay, window)) _dotOverlay = null;
            if (ReferenceEquals(_controlOverlay, window)) _controlOverlay = null;
            if (ReferenceEquals(_hostileOverlay, window)) _hostileOverlay = null;
            RefreshOverlaysToggleButton();
        };
        window.Show();
        RefreshOverlaysToggleButton();
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

    private void ResetBuffTimings_Click(object sender, RoutedEventArgs e) =>
        ShowBuffError(_viewModel.ResetSelectedBuffTimingsToCatalog());

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
        if (_disposed) return;
        _disposed = true;
        _manualTimerTick.Stop();
        _manualStopwatch.Stop();
        _overlay?.Close();
        _buffOverlay?.Close();
        _dotOverlay?.Close();
        _controlOverlay?.Close();
        _hostileOverlay?.Close();
        _mouseHighlight?.Close();
        _mouseHighlight = null;
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
