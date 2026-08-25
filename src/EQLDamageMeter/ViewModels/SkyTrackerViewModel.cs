using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class SkyTrackerViewModel : ObservableObject
{
    private readonly EqWikiSkyCatalog _catalog = new();
    private readonly BuffAlertService _alerts = new();
    private readonly Dictionary<string, DateTime> _recentAlerts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _ledgerGate = new();
    private readonly SkyLootLedger _ledger = new();
    private readonly List<string> _pendingLiveMessages = [];
    private bool _scanningLog;
    private string? _lastScanPath;
    private long _lastScanMaxPosition;
    private CancellationTokenSource? _scanCts;
    private readonly HashSet<string> _lootWatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string ClassName, string RewardName)> _legacyQuestWatches = [];
    private string _statusText = "Load Plane of Sky rewards from the wiki to begin";
    private string _catalogSummary = "Catalog not loaded";
    private string _previewStats = string.Empty;
    private string _previewQuestSummary = string.Empty;
    private bool _isBusy;
    private bool _syncingClass;
    private bool _syncingQuest;
    private string? _selectedClass;
    private SkyRewardCatalog? _selectedReward;
    private SkyTrackedGoalViewModel? _selectedGoal;
    private SkyClassRowViewModel? _selectedClassRow;
    private SkyQuestRowViewModel? _selectedQuestRow;
    private int _upgradeTier;
    private string _displayStats = string.Empty;
    private string _upgradeSummary = EqWikiItemUpgrade.BonusSummary(0);

    public ObservableCollection<string> ClassNames { get; } = [];
    public ObservableCollection<SkyClassRowViewModel> ClassRows { get; } = [];
    public ObservableCollection<SkyQuestRowViewModel> QuestRows { get; } = [];
    public ObservableCollection<SkyRewardCatalog> AvailableRewards { get; } = [];
    public ObservableCollection<SkyPreviewPartViewModel> PreviewParts { get; } = [];
    public ObservableCollection<SkyTrackedGoalViewModel> Goals { get; } = [];
    public ObservableCollection<SkyRequirementRowViewModel> SelectedParts { get; } = [];
    public IReadOnlyList<BuffSoundKind> SoundChoices { get; } = Enum.GetValues<BuffSoundKind>();
    public IReadOnlyList<BuffAlertMode> AlertModeChoices { get; } = BuffAlertModeOptions.ExclusiveChoices;
    public IReadOnlyList<SkyLocationChoice> LocationChoices { get; } = SkyLocationChoice.All;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CatalogSummary
    {
        get => _catalogSummary;
        private set => SetProperty(ref _catalogSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? SelectedClass
    {
        get => _selectedClass;
        set
        {
            if (!SetProperty(ref _selectedClass, value)) return;
            SyncSelectedClassRow();
            RefreshQuestRows();
            RaisePropertyChanged(nameof(SelectedClassTitle));
            RaisePropertyChanged(nameof(SelectedQuestGiver));
        }
    }

    public SkyClassRowViewModel? SelectedClassRow
    {
        get => _selectedClassRow;
        set
        {
            if (!SetProperty(ref _selectedClassRow, value)) return;
            if (_syncingClass) return;
            SelectedClass = value?.ClassName;
        }
    }

    public SkyQuestRowViewModel? SelectedQuestRow
    {
        get => _selectedQuestRow;
        set
        {
            if (!SetProperty(ref _selectedQuestRow, value)) return;
            if (_syncingQuest) return;
            SelectedReward = value?.Reward;
        }
    }

    public string SelectedClassTitle =>
        string.IsNullOrWhiteSpace(SelectedClass) ? "QUESTS" : $"{SelectedClass.ToUpperInvariant()} QUESTS";

    public string SelectedQuestGiver
    {
        get
        {
            var giver = SelectedClassRow?.QuestGiver ?? _catalog.FindClass(SelectedClass)?.QuestGiver;
            return string.IsNullOrWhiteSpace(giver) ? string.Empty : giver;
        }
    }

    public int UpgradeTier
    {
        get => _upgradeTier;
        set
        {
            var clamped = Math.Clamp(value, 0, 10);
            if (!SetProperty(ref _upgradeTier, clamped)) return;
            RefreshDisplayStats();
        }
    }

    public string UpgradeTierLabel => EqWikiItemUpgrade.TierLabel(UpgradeTier);

    public string DisplayStats
    {
        get => _displayStats;
        private set => SetProperty(ref _displayStats, value);
    }

    public string UpgradeSummary
    {
        get => _upgradeSummary;
        private set => SetProperty(ref _upgradeSummary, value);
    }

    public SkyRewardCatalog? SelectedReward
    {
        get => _selectedReward;
        set
        {
            if (!SetProperty(ref _selectedReward, value)) return;
            SyncSelectedQuestRow();
            _ = LoadPreviewAsync(value);
        }
    }

    public string PreviewStats
    {
        get => _previewStats;
        private set => SetProperty(ref _previewStats, value);
    }

    public string PreviewQuestSummary
    {
        get => _previewQuestSummary;
        private set => SetProperty(ref _previewQuestSummary, value);
    }

    public SkyTrackedGoalViewModel? SelectedGoal
    {
        get => _selectedGoal;
        set
        {
            if (!SetProperty(ref _selectedGoal, value)) return;
            RaisePropertyChanged(nameof(HasSelectedGoal));
            RaisePropertyChanged(nameof(GoalDetailsVisibility));
            RaisePropertyChanged(nameof(EmptyGoalDetailsVisibility));
        }
    }

    public bool HasSelectedGoal => SelectedGoal is not null;
    public Visibility GoalDetailsVisibility => HasSelectedGoal ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyGoalDetailsVisibility => HasSelectedGoal ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PreviewVisibility => SelectedReward is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyPreviewVisibility => SelectedReward is null ? Visibility.Visible : Visibility.Collapsed;

    public void Initialize()
    {
        _catalog.LoadCached();
        ApplyCatalogToUi();
        var document = SkyTrackerStore.TryLoad();
        Goals.Clear();
        foreach (var goal in document.Goals
                     .OrderBy(entry => entry.ClassName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(entry => entry.RewardName, StringComparer.OrdinalIgnoreCase))
        {
            Goals.Add(SkyTrackedGoalViewModel.From(goal, PersistAsync));
        }

        _lootWatches.Clear();
        _legacyQuestWatches.Clear();
        foreach (var watch in document.LootWatches)
        {
            if (string.IsNullOrWhiteSpace(watch.ItemName))
            {
                _legacyQuestWatches.Add((watch.ClassName, watch.RewardName));
                continue;
            }

            _lootWatches.Add(WatchKey(watch.ClassName, watch.RewardName, watch.ItemName));
        }

        ExpandLegacyQuestWatches();

        lock (_ledgerGate)
            _ledger.LoadCatalog(_catalog.Classes);

        if (_catalog.IsLoaded)
        {
            StatusText = $"Ready · {CountRewards()} Sky rewards cached";
            return;
        }

        StatusText = "Downloading Plane of Sky rewards from eqlwiki.com…";
        _ = RefreshCatalogAsync();
    }

    public async Task RefreshCatalogAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Downloading Plane of Sky rewards from eqlwiki.com…";
        try
        {
            var (ok, error) = await _catalog.RefreshAsync();
            lock (_ledgerGate)
                _ledger.LoadCatalog(_catalog.Classes);
            ApplyCatalogToUi();
            ApplyLedgerToUi();
            ExpandLegacyQuestWatches();
            StatusText = ok
                ? $"Loaded {CountRewards()} Sky rewards from the wiki"
                : error ?? "Sky catalog refresh failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddSelectedPartsAsync()
    {
        if (SelectedReward is null || string.IsNullOrWhiteSpace(SelectedClass))
        {
            StatusText = "Pick a class and reward first.";
            return;
        }

        var selectedParts = PreviewParts.Where(part => part.IsSelected).ToList();
        if (selectedParts.Count == 0)
        {
            StatusText = "Select at least one required item to track.";
            return;
        }

        var cls = _catalog.FindClass(SelectedClass);
        var existing = Goals.FirstOrDefault(goal =>
            goal.ClassName.Equals(SelectedClass, StringComparison.OrdinalIgnoreCase) &&
            goal.RewardName.Equals(SelectedReward.RewardName, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            foreach (var part in selectedParts)
            {
                if (existing.Parts.Any(entry =>
                        entry.ItemName.Equals(part.ItemName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                existing.Parts.Add(new SkyTrackedPartViewModel(
                    part.ItemName,
                    part.Note,
                    part.NeededCount,
                    foundCount: 0,
                    SkyItemLocation.Unknown,
                    alertEnabled: true,
                    BuffAlertMode.Sound,
                    BuffSoundKind.Chime,
                    voiceText: string.Empty,
                    lastDropText: string.Empty,
                    PersistAsync,
                    existing.RefreshProgress));
            }

            existing.RefreshProgress();
            SelectedGoal = existing;
            StatusText = $"Updated {existing.RewardName} with {selectedParts.Count} item(s)";
            await PersistAsync();
            return;
        }

        var stats = PreviewStats;
        if (string.IsNullOrWhiteSpace(stats))
        {
            var (fetched, _) = await EqWikiItemStats.FetchStatsAsync(SelectedReward.RewardName);
            stats = fetched;
        }

        var goal = new SkyTrackedGoalViewModel(
            Guid.NewGuid().ToString("N"),
            SelectedClass!,
            SelectedReward.RewardName,
            SelectedReward.QuestName,
            SelectedReward.TriggerPhrase,
            cls?.QuestGiver ?? string.Empty,
            stats,
            PersistAsync);

        foreach (var part in selectedParts)
        {
            goal.Parts.Add(new SkyTrackedPartViewModel(
                part.ItemName,
                part.Note,
                part.NeededCount,
                foundCount: 0,
                SkyItemLocation.Unknown,
                alertEnabled: true,
                BuffAlertMode.Sound,
                BuffSoundKind.Chime,
                voiceText: string.Empty,
                lastDropText: string.Empty,
                PersistAsync,
                goal.RefreshProgress));
        }

        goal.RefreshProgress();
        Goals.Insert(0, goal);
        SelectedGoal = goal;
        StatusText = $"Tracking {goal.RewardName} ({goal.Parts.Count} items)";
        await PersistAsync();
    }

    public void RemoveGoal(SkyTrackedGoalViewModel goal)
    {
        if (goal is null) return;
        Goals.Remove(goal);
        if (ReferenceEquals(SelectedGoal, goal))
            SelectedGoal = Goals.FirstOrDefault();
        StatusText = $"Removed {goal.RewardName}";
        _ = PersistAsync();
    }

    public void RemovePart(SkyTrackedPartViewModel part)
    {
        if (SelectedGoal is null) return;
        SelectedGoal.Parts.Remove(part);
        SelectedGoal.RefreshProgress();
        if (SelectedGoal.Parts.Count == 0)
        {
            var goal = SelectedGoal;
            Goals.Remove(goal);
            SelectedGoal = Goals.FirstOrDefault();
            StatusText = $"Removed {goal.RewardName}";
        }
        else
        {
            StatusText = $"Stopped tracking {part.ItemName}";
        }

        _ = PersistAsync();
    }

    public void OpenSelectedRewardWiki()
    {
        var name = SelectedGoal?.RewardName ?? SelectedReward?.RewardName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var url = EqWikiLinks.ForPage(name);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void OpenSkyWiki()
    {
        Process.Start(new ProcessStartInfo(EqWikiLinks.BaseUrl + "Plane_of_Sky") { UseShellExecute = true });
    }

    public void PrepareLogScan()
    {
        lock (_ledgerGate)
        {
            _scanningLog = true;
            _pendingLiveMessages.Clear();
        }
    }

    public async Task ScanCharacterLogAsync(string path, long maxPosition, CancellationToken cancellationToken)
    {
        _lastScanPath = path;
        _lastScanMaxPosition = maxPosition;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _scanCts.Token;
        PrepareLogScan();
        CatalogSummary = "Scanning log for Sky items…";

        SkyLootLedger scanned;
        try
        {
            var classes = _catalog.Classes;
            scanned = await Task.Run(() => SkyLogScanner.Scan(path, classes, maxPosition, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException)
        {
            if (token.IsCancellationRequested) return;
            StatusText = "Sky log scan could not read the character log.";
            lock (_ledgerGate)
            {
                _scanningLog = false;
                _pendingLiveMessages.Clear();
            }

            return;
        }
        catch (UnauthorizedAccessException)
        {
            if (token.IsCancellationRequested) return;
            StatusText = "Sky log scan was denied access to the character log.";
            lock (_ledgerGate)
            {
                _scanningLog = false;
                _pendingLiveMessages.Clear();
            }

            return;
        }

        if (token.IsCancellationRequested) return;

        List<string> pendingLive;
        lock (_ledgerGate)
        {
            _ledger.CopyFrom(scanned);
            pendingLive = [.. _pendingLiveMessages];
            _pendingLiveMessages.Clear();
            _scanningLog = false;
            foreach (var pending in pendingLive)
                _ledger.Observe(pending);
        }

        await RunOnUiAsync(() =>
        {
            ApplyLedgerToUi();
            UpdateCatalogSummary();
        });
        foreach (var pending in pendingLive)
            TryPlayLootAlert(pending);
    }

    public void ObserveLootMessage(string message)
    {
        if (!SkyLogEvents.IsCandidate(message)) return;

        var changed = false;
        lock (_ledgerGate)
        {
            if (_scanningLog)
            {
                _pendingLiveMessages.Add(message);
                return;
            }

            changed = _ledger.Observe(message);
        }

        if (changed)
            ApplyLedgerToUi();

        TryPlayLootAlert(message);
    }

    private void TryPlayLootAlert(string message)
    {
        if (!SessionLootParser.TryReadLootEvent(message, out var itemName, out var disposition, out _))
            return;
        if (disposition is not ("Kept" or "Stored")) return;
        if (_lootWatches.Count == 0) return;

        var now = DateTime.UtcNow;
        var spokenItem = SkyItemName.Normalize(itemName);
        foreach (var cls in _catalog.Classes)
        {
            foreach (var reward in cls.Rewards)
            {
                if (!reward.RequiredItems.Any(item =>
                        SkyItemName.EqualsNormalized(item.ItemName, itemName)))
                    continue;
                if (!_lootWatches.Contains(WatchKey(cls.ClassName, reward.RewardName, spokenItem)))
                    continue;

                var key = WatchKey(cls.ClassName, reward.RewardName, spokenItem);
                if (_recentAlerts.TryGetValue(key, out var previous) &&
                    now - previous < TimeSpan.FromSeconds(2))
                    continue;
                _recentAlerts[key] = now;
                SpeakDropAlert(cls.ClassName, reward.RewardName, spokenItem);
            }
        }
    }

    private void SpeakDropAlert(string className, string rewardName, string itemName)
    {
        var spokenItem = SkyItemName.Normalize(itemName);
        _alerts.PlayLootAlert(spokenItem, BuffSoundKind.Chime, BuffAlertMode.TextToSpeech,
            DropAlertPhrase(className, rewardName, spokenItem));
    }

    private static string DropAlertPhrase(string className, string rewardName, string itemName) =>
        $"{className}, {rewardName}, {itemName}";

    private async Task LoadPreviewAsync(SkyRewardCatalog? reward)
    {
        PreviewParts.Clear();
        PreviewStats = string.Empty;
        PreviewQuestSummary = string.Empty;
        RefreshDisplayStats();
        RaisePropertyChanged(nameof(PreviewVisibility));
        RaisePropertyChanged(nameof(EmptyPreviewVisibility));
        if (reward is null)
        {
            RebuildSelectedParts();
            return;
        }

        var cls = _catalog.FindClass(SelectedClass);
        PreviewQuestSummary =
            $"{SkyClassPresentation.ShortQuestTitle(reward.QuestName, SelectedClass ?? string.Empty)}" +
            (string.IsNullOrWhiteSpace(reward.TriggerPhrase)
                ? string.Empty
                : $" · say {reward.TriggerPhrase}") +
            (string.IsNullOrWhiteSpace(cls?.QuestGiver)
                ? string.Empty
                : $" · {cls!.QuestGiver}");

        foreach (var item in reward.RequiredItems)
        {
            PreviewParts.Add(new SkyPreviewPartViewModel(item.ItemName, item.Note, item.NeededCount)
            {
                IsSelected = true
            });
        }

        RaisePropertyChanged(nameof(PreviewVisibility));
        RaisePropertyChanged(nameof(EmptyPreviewVisibility));
        RebuildSelectedParts();
        RefreshQuestStatuses();

        var (stats, error) = await EqWikiItemStats.FetchStatsAsync(reward.RewardName);
        if (!ReferenceEquals(SelectedReward, reward)) return;
        PreviewStats = string.IsNullOrWhiteSpace(stats)
            ? error ?? "Stats unavailable"
            : stats;
        RefreshDisplayStats();
        RebuildSelectedParts();
        RefreshQuestStatuses();
    }

    private void RefreshQuestRows()
    {
        var previousReward = SelectedReward?.RewardName;
        AvailableRewards.Clear();
        QuestRows.Clear();
        if (!_syncingQuest)
            SelectedReward = null;

        foreach (var reward in _catalog.GetRewardsForClass(SelectedClass))
        {
            AvailableRewards.Add(reward);
            QuestRows.Add(new SkyQuestRowViewModel
            {
                Reward = reward,
                Title = SkyClassPresentation.ShortQuestTitle(reward.QuestName, SelectedClass ?? string.Empty)
            });
        }

        RefreshQuestStatuses();

        SkyRewardCatalog? next = null;
        if (!string.IsNullOrWhiteSpace(previousReward))
            next = AvailableRewards.FirstOrDefault(reward =>
                reward.RewardName.Equals(previousReward, StringComparison.OrdinalIgnoreCase));
        next ??= AvailableRewards.FirstOrDefault();
        if (next is not null)
            SelectedReward = next;
    }

    private void SyncSelectedClassRow()
    {
        if (_syncingClass) return;
        _syncingClass = true;
        SelectedClassRow = ClassRows.FirstOrDefault(row =>
            row.ClassName.Equals(SelectedClass, StringComparison.OrdinalIgnoreCase));
        _syncingClass = false;
    }

    private void SyncSelectedQuestRow()
    {
        if (_syncingQuest) return;
        _syncingQuest = true;
        SelectedQuestRow = QuestRows.FirstOrDefault(row =>
            SelectedReward is not null &&
            row.Reward.RewardName.Equals(SelectedReward.RewardName, StringComparison.OrdinalIgnoreCase));
        _syncingQuest = false;
    }

    private void RefreshQuestStatuses()
    {
        lock (_ledgerGate)
        {
            foreach (var row in QuestRows)
                row.StatusLabel = _ledger.QuestStatus(SelectedClass, row.Reward);
        }
    }

    private void RebuildSelectedParts()
    {
        SelectedParts.Clear();
        if (SelectedReward is null) return;
        lock (_ledgerGate)
        {
            foreach (var item in SelectedReward.RequiredItems)
            {
                var balance = _ledger.Snapshot(item.ItemName);
                var tracked = SelectedClass is not null &&
                              _lootWatches.Contains(WatchKey(SelectedClass, SelectedReward.RewardName, item.ItemName));
                SelectedParts.Add(SkyRequirementRowViewModel.From(item, balance, tracked, SetItemWatch));
            }
        }
    }

    private void ApplyLedgerToUi()
    {
        RebuildSelectedParts();
        RefreshQuestStatuses();
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    private void RefreshDisplayStats()
    {
        RaisePropertyChanged(nameof(UpgradeTierLabel));
        UpgradeSummary = EqWikiItemUpgrade.BonusSummary(UpgradeTier);
        if (string.IsNullOrWhiteSpace(PreviewStats) ||
            PreviewStats.StartsWith("Stats unavailable", StringComparison.OrdinalIgnoreCase))
        {
            DisplayStats = PreviewStats;
            return;
        }

        DisplayStats = EqWikiItemUpgrade.ApplyTier(PreviewStats, UpgradeTier);
    }

    public void OpenItemWiki(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return;
        var url = EqWikiLinks.ForPage(itemName);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void ApplyCatalogToUi()
    {
        var previousClass = SelectedClass;
        var previousReward = SelectedReward?.RewardName;
        ClassNames.Clear();
        ClassRows.Clear();
        foreach (var entry in _catalog.Classes)
        {
            ClassNames.Add(entry.ClassName);
            ClassRows.Add(new SkyClassRowViewModel
            {
                ClassName = entry.ClassName,
                QuestGiver = entry.QuestGiver,
                Glyph = SkyClassPresentation.Glyph(entry.ClassName)
            });
        }

        UpdateCatalogSummary();
        if (!string.IsNullOrWhiteSpace(previousClass) &&
            ClassNames.Any(name => name.Equals(previousClass, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedClass = ClassNames.First(name =>
                name.Equals(previousClass, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(previousReward))
            {
                SelectedReward = AvailableRewards.FirstOrDefault(reward =>
                    reward.RewardName.Equals(previousReward, StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (ClassNames.Count > 0 && SelectedClass is null)
        {
            SelectedClass = ClassNames.FirstOrDefault(name =>
                name.Equals("Warrior", StringComparison.OrdinalIgnoreCase)) ?? ClassNames[0];
        }
        else
        {
            SyncSelectedClassRow();
        }
    }

    private void UpdateCatalogSummary()
    {
        if (!_catalog.IsLoaded)
        {
            CatalogSummary = "Catalog not loaded";
            return;
        }

        var when = _catalog.FetchedAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "cache";
        CatalogSummary = $"{_catalog.Classes.Count} classes · {CountRewards()} rewards · {when}";
    }

    private int CountRewards() => _catalog.Classes.Sum(entry => entry.Rewards.Count);

    private async Task PersistAsync()
    {
        var document = new SkyTrackerDocument
        {
            Goals = Goals.Select(goal => goal.ToModel()).ToList(),
            LootWatches = _lootWatches
                .Select(key =>
                {
                    var split = key.Split('|', 3);
                    return split.Length == 3
                        ? new SkyLootWatch
                        {
                            ClassName = split[0],
                            RewardName = split[1],
                            ItemName = split[2]
                        }
                        : null;
                })
                .OfType<SkyLootWatch>()
                .Concat(_legacyQuestWatches.Select(watch => new SkyLootWatch
                {
                    ClassName = watch.ClassName,
                    RewardName = watch.RewardName
                }))
                .ToList()
        };
        await SkyTrackerStore.TrySaveAsync(document);
    }

    private void ExpandLegacyQuestWatches()
    {
        if (_legacyQuestWatches.Count == 0 || !_catalog.IsLoaded) return;
        var remaining = new List<(string ClassName, string RewardName)>();
        var changed = false;
        foreach (var (className, rewardName) in _legacyQuestWatches)
        {
            var reward = _catalog.FindClass(className)?.Rewards.FirstOrDefault(entry =>
                entry.RewardName.Equals(rewardName, StringComparison.OrdinalIgnoreCase));
            if (reward is null)
            {
                remaining.Add((className, rewardName));
                continue;
            }

            foreach (var item in reward.RequiredItems)
            {
                if (_lootWatches.Add(WatchKey(className, rewardName, item.ItemName)))
                    changed = true;
            }
        }

        _legacyQuestWatches.Clear();
        _legacyQuestWatches.AddRange(remaining);
        if (changed)
            _ = PersistAsync();
    }

    public void SetItemWatch(string itemName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(SelectedClass) || SelectedReward is null ||
            string.IsNullOrWhiteSpace(itemName))
            return;
        var key = WatchKey(SelectedClass, SelectedReward.RewardName, itemName);
        var changed = enabled ? _lootWatches.Add(key) : _lootWatches.Remove(key);
        if (changed)
            _ = PersistAsync();
    }

    private static string WatchKey(string className, string rewardName, string itemName) =>
        $"{className.Trim()}|{rewardName.Trim()}|{SkyItemName.Normalize(itemName)}";
}

public sealed class SkyRequirementRowViewModel : ObservableObject
{
    private readonly Action<string, bool>? _onTrackChanged;
    private bool _isTracked;

    public SkyRequirementRowViewModel(string itemName, string displayName, string progressText, bool hasEnough,
        string dropSourceText, string locationLabel, bool isDeleted, bool showLocation, string deletedText,
        string autosoldText, bool isTracked, Action<string, bool>? onTrackChanged)
    {
        ItemName = itemName;
        DisplayName = displayName;
        ProgressText = progressText;
        HasEnough = hasEnough;
        DropSourceText = dropSourceText;
        HasDropSource = dropSourceText.Length > 0;
        LocationLabel = locationLabel;
        IsDeleted = isDeleted;
        ShowLocation = showLocation;
        DeletedText = deletedText;
        ShowDeletedCount = deletedText.Length > 0;
        AutosoldText = autosoldText;
        ShowAutosold = autosoldText.Length > 0;
        _isTracked = isTracked;
        _onTrackChanged = onTrackChanged;
    }

    public string ItemName { get; }
    public string DisplayName { get; }
    public string ProgressText { get; }
    public bool HasEnough { get; }
    public string DropSourceText { get; }
    public bool HasDropSource { get; }
    public string LocationLabel { get; }
    public bool IsDeleted { get; }
    public bool ShowLocation { get; }
    public string DeletedText { get; }
    public bool ShowDeletedCount { get; }
    public string AutosoldText { get; }
    public bool ShowAutosold { get; }

    public bool IsTracked
    {
        get => _isTracked;
        set
        {
            if (!SetProperty(ref _isTracked, value)) return;
            _onTrackChanged?.Invoke(ItemName, value);
        }
    }

    public static SkyRequirementRowViewModel From(SkyRequiredItemCatalog item, SkyItemBalance balance,
        bool isTracked, Action<string, bool>? onTrackChanged)
    {
        var drop = SkyDropSource.Format(item.Note);
        var needed = item.NeededCount < 1 ? 1 : item.NeededCount;
        var locationLabel = SkyLocationChoice.LabelFor(balance.Location);
        return new SkyRequirementRowViewModel(
            item.ItemName,
            item.ItemName,
            $"{balance.Owned}/{needed}",
            balance.Owned >= needed,
            drop,
            locationLabel,
            balance.IsDeleted,
            balance.Owned > 0 && locationLabel is not "Not set",
            balance.DestroyedCount > 0 ? $"deleted ×{balance.DestroyedCount}" : string.Empty,
            balance.SoldCount > 0 ? $"autosold ×{balance.SoldCount}" : string.Empty,
            isTracked,
            onTrackChanged);
    }
}


public sealed class SkyPreviewPartViewModel : ObservableObject
{
    private bool _isSelected = true;

    public SkyPreviewPartViewModel(string itemName, string note, int neededCount)
    {
        ItemName = itemName;
        Note = note;
        NeededCount = neededCount < 1 ? 1 : neededCount;
    }

    public string ItemName { get; }
    public string Note { get; }
    public int NeededCount { get; }
    /// <summary>Wiki-style drop source, e.g. " (7-Soth)". Empty for random/any-mob drops.</summary>
    public string DropSourceText =>
        string.IsNullOrWhiteSpace(Note) ? string.Empty : $" ({Note.Trim()})";
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Note) ? ItemName : $"{ItemName} ({Note.Trim()})";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class SkyTrackedGoalViewModel : ObservableObject
{
    private readonly Func<Task> _persist;
    private string _progressText = string.Empty;

    public SkyTrackedGoalViewModel(string id, string className, string rewardName, string questName,
        string triggerPhrase, string questGiver, string rewardStats, Func<Task> persist)
    {
        Id = id;
        ClassName = className;
        RewardName = rewardName;
        QuestName = questName;
        TriggerPhrase = triggerPhrase;
        QuestGiver = questGiver;
        RewardStats = rewardStats;
        _persist = persist;
        Parts = new ObservableCollection<SkyTrackedPartViewModel>();
        Parts.CollectionChanged += (_, _) => RefreshProgress();
    }

    public string Id { get; }
    public string ClassName { get; }
    public string RewardName { get; }
    public string QuestName { get; }
    public string TriggerPhrase { get; }
    public string QuestGiver { get; }
    public string RewardStats { get; }
    public ObservableCollection<SkyTrackedPartViewModel> Parts { get; }
    public string Subtitle =>
        string.IsNullOrWhiteSpace(QuestName) ? ClassName : $"{ClassName} · {QuestName}";

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public void RefreshProgress()
    {
        var needed = Parts.Sum(part => part.NeededCount);
        var found = Parts.Sum(part => Math.Min(part.FoundCount, part.NeededCount));
        ProgressText = needed == 0 ? "0/0" : $"{found}/{needed}";
    }

    public SkyTrackedGoal ToModel() => new()
    {
        Id = Id,
        ClassName = ClassName,
        RewardName = RewardName,
        QuestName = QuestName,
        TriggerPhrase = TriggerPhrase,
        QuestGiver = QuestGiver,
        RewardStats = RewardStats,
        Parts = Parts.Select(part => part.ToModel()).ToList()
    };

    public static SkyTrackedGoalViewModel From(SkyTrackedGoal model, Func<Task> persist)
    {
        var vm = new SkyTrackedGoalViewModel(
            model.Id,
            model.ClassName,
            model.RewardName,
            model.QuestName,
            model.TriggerPhrase,
            model.QuestGiver,
            model.RewardStats,
            persist);
        foreach (var part in model.Parts)
            vm.Parts.Add(SkyTrackedPartViewModel.From(part, persist, vm.RefreshProgress));
        vm.RefreshProgress();
        return vm;
    }
}

public sealed record SkyLocationChoice(SkyItemLocation Value, string Label)
{
    public static IReadOnlyList<SkyLocationChoice> All { get; } =
    [
        new(SkyItemLocation.Unknown, "Not set"),
        new(SkyItemLocation.Inventory, "Equipment"),
        new(SkyItemLocation.Bank, "Bank"),
        new(SkyItemLocation.Currency, "Currencies"),
        new(SkyItemLocation.Other, "Other")
    ];

    public static string LabelFor(SkyItemLocation location) =>
        All.FirstOrDefault(choice => choice.Value == location)?.Label ?? location.ToString();

    public override string ToString() => Label;
}

public sealed class SkyTrackedPartViewModel : ObservableObject
{
    private readonly Func<Task> _persist;
    private readonly Action? _onProgressChanged;
    private int _foundCount;
    private SkyItemLocation _location;
    private bool _alertEnabled;
    private BuffAlertMode _alertMode;
    private BuffSoundKind _sound;
    private string _voiceText;
    private string _lastDropText;

    public SkyTrackedPartViewModel(string itemName, string note, int neededCount, int foundCount,
        SkyItemLocation location, bool alertEnabled, BuffAlertMode alertMode, BuffSoundKind sound,
        string voiceText, string lastDropText, Func<Task> persist, Action? onProgressChanged = null)
    {
        ItemName = itemName;
        Note = note;
        NeededCount = neededCount < 1 ? 1 : neededCount;
        _foundCount = Math.Max(0, foundCount);
        _location = location;
        _alertEnabled = alertEnabled;
        _alertMode = BuffAlertModeOptions.Normalize(alertMode);
        _sound = sound;
        _voiceText = voiceText ?? string.Empty;
        _lastDropText = lastDropText;
        _persist = persist;
        _onProgressChanged = onProgressChanged;
    }

    public string ItemName { get; }
    public string Note { get; }
    public int NeededCount { get; }
    public string DisplayName => ItemName;
    public string ProgressText => $"{FoundCount}/{NeededCount}";
    public bool HasEnough => FoundCount >= NeededCount;
    public string LocationLabel => SkyLocationChoice.LabelFor(Location);
    public string StatusSummary =>
        string.IsNullOrWhiteSpace(LastDropText)
            ? $"{ProgressText} · Where: {LocationLabel}"
            : $"{ProgressText} · Where: {LocationLabel} · {LastDropText}";

    public int FoundCount
    {
        get => _foundCount;
        set
        {
            var clamped = Math.Max(0, value);
            if (!SetProperty(ref _foundCount, clamped)) return;
            RaisePropertyChanged(nameof(ProgressText));
            RaisePropertyChanged(nameof(StatusSummary));
            RaisePropertyChanged(nameof(HasEnough));
            _onProgressChanged?.Invoke();
            _ = _persist();
        }
    }

    public SkyItemLocation Location
    {
        get => _location;
        set
        {
            if (!SetProperty(ref _location, value)) return;
            if (value == SkyItemLocation.Bank && _foundCount < NeededCount)
            {
                _foundCount = NeededCount;
                RaisePropertyChanged(nameof(FoundCount));
                RaisePropertyChanged(nameof(ProgressText));
                RaisePropertyChanged(nameof(HasEnough));
                _onProgressChanged?.Invoke();
            }

            RaisePropertyChanged(nameof(LocationLabel));
            RaisePropertyChanged(nameof(StatusSummary));
            _ = _persist();
        }
    }

    /// <summary>
    /// Updates loot progress without persisting so callers can batch one save.
    /// </summary>
    public void ApplyLootProgress(bool incrementFound, SkyItemLocation location, string lastDropText)
    {
        if (incrementFound)
        {
            _foundCount++;
            RaisePropertyChanged(nameof(FoundCount));
            RaisePropertyChanged(nameof(ProgressText));
            RaisePropertyChanged(nameof(HasEnough));
        }

        if (_location != location)
        {
            _location = location;
            RaisePropertyChanged(nameof(Location));
            RaisePropertyChanged(nameof(LocationLabel));
        }

        LastDropText = lastDropText;
        RaisePropertyChanged(nameof(StatusSummary));
    }

    public bool AlertEnabled
    {
        get => _alertEnabled;
        set
        {
            if (!SetProperty(ref _alertEnabled, value)) return;
            RaisePropertyChanged(nameof(AlertDetailsVisibility));
            _ = _persist();
        }
    }

    public BuffAlertMode AlertMode
    {
        get => _alertMode;
        set
        {
            var normalized = BuffAlertModeOptions.Normalize(value);
            if (!SetProperty(ref _alertMode, normalized)) return;
            RaisePropertyChanged(nameof(SoundPickerVisibility));
            RaisePropertyChanged(nameof(VoiceTextVisibility));
            _ = _persist();
        }
    }

    public BuffSoundKind Sound
    {
        get => _sound;
        set
        {
            if (!SetProperty(ref _sound, value)) return;
            _ = _persist();
        }
    }

    public string VoiceText
    {
        get => _voiceText;
        set
        {
            if (!SetProperty(ref _voiceText, value)) return;
            _ = _persist();
        }
    }

    public Visibility AlertDetailsVisibility =>
        AlertEnabled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SoundPickerVisibility =>
        AlertEnabled && AlertMode == BuffAlertMode.Sound ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VoiceTextVisibility =>
        AlertEnabled && AlertMode == BuffAlertMode.TextToSpeech ? Visibility.Visible : Visibility.Collapsed;

    public string LastDropText
    {
        get => _lastDropText;
        set
        {
            if (!SetProperty(ref _lastDropText, value)) return;
            RaisePropertyChanged(nameof(StatusSummary));
        }
    }

    public SkyTrackedPart ToModel() => new()
    {
        ItemName = ItemName,
        Note = Note,
        NeededCount = NeededCount,
        FoundCount = FoundCount,
        Location = Location,
        AlertEnabled = AlertEnabled,
        AlertMode = BuffAlertModeOptions.Normalize(AlertMode),
        Sound = Sound,
        VoiceText = VoiceText?.Trim() ?? string.Empty,
        LastDropText = LastDropText
    };

    public static SkyTrackedPartViewModel From(SkyTrackedPart model, Func<Task> persist,
        Action? onProgressChanged = null) =>
        new(model.ItemName, model.Note, model.NeededCount, model.FoundCount, model.Location,
            model.AlertEnabled, model.AlertMode, model.Sound, model.VoiceText, model.LastDropText,
            persist, onProgressChanged);
}
