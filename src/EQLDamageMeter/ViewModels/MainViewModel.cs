using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private enum BreakdownMode { Offense, Defense, Healing }
    private sealed record QueuedParsedLine(int Generation, ParsedLogLine Parsed);
    public const string DefaultLogFolder = @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends\Logs";
    private const int HistoryLimit = 25;
    private const int ParsedLineBatchSize = 1_000;
    private const int MaxQueuedParsedLines = 20_000;
    private static readonly Brush[] ChartBrushes = CreateChartBrushes();

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly HashSet<DateTime> _archivedStarts = [];
    private readonly ConcurrentQueue<QueuedParsedLine> _parsedLines = new();
    private readonly SemaphoreSlim _parsedQueueSlots = new(MaxQueuedParsedLines, MaxQueuedParsedLines);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly BuffTracker _buffTracker = new();
    private readonly BuffAlertService _buffAlertService = new();
    private readonly SemaphoreSlim _buffTimingGate = new(1, 1);
    private readonly SessionTracker _sessionTracker = new();
    private readonly List<SessionRecord> _sessionHistory = [];
    private DateTime _lastSessionPersistUtc = DateTime.MinValue;
    private bool _sessionDirty;
    private DateTime? _lastLogTimestamp;
    private DateTime? _lastLogWallClock;
    private SpellDataCatalog? _spellDataCatalog;
    private LogFileMonitor? _monitor;
    private LogLineParser? _parser;
    private GroupStateTracker? _group;
    private EncounterTracker? _encounter;
    private CombatantViewModel? _selectedCombatant;
    private EncounterHistoryViewModel? _selectedHistory;
    private string _statusText = "Looking for combat logs…";
    private string _characterName = "No character";
    private string _serverName = "—";
    private string _modeText = "SOLO";
    private string _encounterTime = "0:00";
    private long _maxDamage = 1;
    private string? _activeLogPath;
    private BreakdownMode _breakdownMode;
    private long _dataVersion;
    private long _renderedDataVersion = -1;
    private long _lastRenderedSecond = -1;
    private long _cachedLiveCardDamage;
    private double _cachedLiveCardDps;
    private EncounterHistoryViewModel? _renderedHistory;
    private bool _combinePetDamage;
    private bool _isPetDamageExpanded;
    private BuffRuleViewModel? _selectedBuffRule;
    private string _buffSearchText = string.Empty;
    private string _buffFilterMode = "All";
    private SpellIconStyle _spellIconStyle = SpellIconStyle.Modern;
    private bool _isCompactBuffOverlay;
    private bool _lockDpsOverlay;
    private bool _lockBuffOverlay;
    private int _characterLevel = SpellDataCatalog.DefaultCasterLevel;
    private int _parseGeneration;
    private int _drainScheduled;
    private bool _disposed;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
            (_, _) => OnRefreshTimer(), _dispatcher);
        _spellIconStyle = AppSettingsStore.TryLoadSpellIconStyle();
        _isCompactBuffOverlay = AppSettingsStore.TryLoadOverlayCompact(OverlayWindowPlacement.BuffKey);
        _lockDpsOverlay = AppSettingsStore.TryLoadOverlayLocked(OverlayWindowPlacement.DpsKey);
        _lockBuffOverlay = AppSettingsStore.TryLoadOverlayLocked(OverlayWindowPlacement.BuffKey);

        foreach (var settings in SpellTrackerStore.TryLoadBuffRules())
            BuffRules.Add(new BuffRuleViewModel(settings));
        DotSpellTracker = new SpellRuleSetViewModel(SpellTrackerCategory.DamageOverTime,
            SpellTrackerStore.TryLoadDotRules(), () => _spellDataCatalog,
            SpellTrackerStore.TrySaveDotRulesAsync, _buffAlertService, () => _characterLevel);
        ControlSpellTracker = new SpellRuleSetViewModel(SpellTrackerCategory.Control,
            SpellTrackerStore.TryLoadControlRules(), () => _spellDataCatalog,
            SpellTrackerStore.TrySaveControlRulesAsync, _buffAlertService, () => _characterLevel);
        HostileSpellTracker = new SpellRuleSetViewModel(SpellTrackerCategory.Hostile,
            SpellTrackerStore.TryLoadHostileRules(), () => _spellDataCatalog,
            SpellTrackerStore.TrySaveHostileRulesAsync, _buffAlertService, () => _characterLevel);
        _buffTracker.PreserveBuffTargetOnDeath = (target, timestamp) =>
            ControlSpellTracker.HasActiveCharmTarget(target, timestamp);
        BuffRulesView = CollectionViewSource.GetDefaultView(BuffRules);
        BuffRulesView.Filter = FilterBuffRule;
        ApplyBuffConfiguration();
        _sessionHistory.AddRange(SessionInfoStore.TryLoadSessions()
            .Where(item => item.EndedAt.HasValue)
            .Where(item => !SessionLogBackfill.IsBackfillId(item.Id))
            .OrderByDescending(item => item.StartedAt));
        SessionHistory.LoadHistory(_sessionHistory, current: null);
        QuestTracker.Initialize();
        SkyTracker.Initialize();
    }

    public ObservableCollection<CombatantViewModel> Combatants { get; } = [];
    public ObservableCollection<EncounterHistoryViewModel> EncounterHistory { get; } = [];
    public ObservableCollection<BuffRuleViewModel> BuffRules { get; } = [];
    public ObservableCollection<BuffOverlayEntryViewModel> OverlayBuffEntries { get; } = [];
    public SessionHistoryViewModel SessionHistory { get; } = new();
    public QuestTrackerViewModel QuestTracker { get; } = new();
    public SkyTrackerViewModel SkyTracker { get; } = new();
    public ItemsViewModel Items { get; } = new();
    public BisGearViewModel Bis { get; } = new();
    public ICollectionView BuffRulesView { get; }
    public IReadOnlyList<BuffAlertMode> BuffAlertModes { get; } = BuffAlertModeOptions.ExclusiveChoices;
    public IReadOnlyList<BuffSoundKind> BuffSoundChoices { get; } = Enum.GetValues<BuffSoundKind>();
    public SpellRuleSetViewModel DotSpellTracker { get; }
    public SpellRuleSetViewModel ControlSpellTracker { get; }
    public SpellRuleSetViewModel HostileSpellTracker { get; }
    public SpellDataCatalog? SpellCatalog => _spellDataCatalog;

    public bool UseModernSpellIcons
    {
        get => _spellIconStyle == SpellIconStyle.Modern;
        set
        {
            var style = value ? SpellIconStyle.Modern : SpellIconStyle.Classic;
            if (_spellIconStyle == style) return;
            _spellIconStyle = style;
            RaisePropertyChanged();
            ApplySpellIconStyle();
            _ = AppSettingsStore.TrySaveSpellIconStyleAsync(style);
        }
    }

    public bool IsCompactBuffOverlay
    {
        get => _isCompactBuffOverlay;
        set
        {
            if (!SetProperty(ref _isCompactBuffOverlay, value)) return;
            _ = AppSettingsStore.TrySaveOverlayCompactAsync(OverlayWindowPlacement.BuffKey, value);
        }
    }

    public bool LockDpsOverlay
    {
        get => _lockDpsOverlay;
        set
        {
            if (!SetProperty(ref _lockDpsOverlay, value)) return;
            OverlayLockChanged?.Invoke(OverlayWindowPlacement.DpsKey, value);
        }
    }

    public bool LockBuffOverlay
    {
        get => _lockBuffOverlay;
        set
        {
            if (!SetProperty(ref _lockBuffOverlay, value)) return;
            OverlayLockChanged?.Invoke(OverlayWindowPlacement.BuffKey, value);
        }
    }

    /// <summary>Raised when a main-app Lock checkbox changes (key, locked).</summary>
    public event Action<string, bool>? OverlayLockChanged;

    public string AppVersionText => $"v {AppUpdateService.CurrentVersionText}";

    public string UpdateButtonToolTip =>
        $"Check for EQDM updates (current version {AppUpdateService.CurrentVersionText})";

    public BuffRuleViewModel? SelectedBuffRule
    {
        get => _selectedBuffRule;
        set => SetProperty(ref _selectedBuffRule, value);
    }

    public string BuffSearchText
    {
        get => _buffSearchText;
        set
        {
            if (!SetProperty(ref _buffSearchText, value)) return;
            BuffRulesView.Refresh();
        }
    }

    public string BuffFilterMode
    {
        get => _buffFilterMode;
        private set
        {
            if (!SetProperty(ref _buffFilterMode, value)) return;
            BuffRulesView.Refresh();
        }
    }

    public CombatantViewModel? SelectedCombatant
    {
        get => _selectedCombatant;
        set
        {
            if (!SetProperty(ref _selectedCombatant, value)) return;
            RaiseBreakdownProperties();
        }
    }

    public EncounterHistoryViewModel? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            EnsureLiveHistoryCard();
            value ??= EncounterHistory[0];
            if (!SetProperty(ref _selectedHistory, value)) return;
            RefreshDisplay(force: true);
        }
    }

    public IReadOnlyList<AbilityViewModel> SelectedAbilities => _breakdownMode switch
    {
        BreakdownMode.Defense => SelectedCombatant?.IncomingAbilities ?? [],
        BreakdownMode.Healing => SelectedCombatant?.HealingAbilities ?? [],
        _ => SelectedCombatant?.Abilities ?? []
    };
    public IReadOnlyList<AbilityViewModel> SelectedProcs => SelectedCombatant?.Procs ?? [];
    public IReadOnlyList<AbilityViewModel> SelectedIncomingAbilities => SelectedCombatant?.IncomingAbilities ?? [];
    public IReadOnlyList<AbilityViewModel> SelectedMitigations => SelectedCombatant?.Mitigations ?? [];
    public string SelectedName => SelectedCombatant?.Name ?? string.Empty;
    public Visibility OffenseVisibility => _breakdownMode == BreakdownMode.Offense ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DefenseVisibility => _breakdownMode == BreakdownMode.Defense ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HealingVisibility => _breakdownMode == BreakdownMode.Healing ? Visibility.Visible : Visibility.Collapsed;
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (!SetProperty(ref _statusText, value)) return;
            RaisePropertyChanged(nameof(IsLogOnline));
            RaisePropertyChanged(nameof(MonitorStatusText));
        }
    }
    public bool IsLogOnline => StatusText.StartsWith("LIVE", StringComparison.OrdinalIgnoreCase);
    public string MonitorStatusText => IsLogOnline ? "LIVE · Monitoring log" : "OFFLINE";
    public string CharacterName
    {
        get => _characterName;
        private set
        {
            if (SetProperty(ref _characterName, value)) RaisePropertyChanged(nameof(CharacterNameUpper));
        }
    }
    public string CharacterNameUpper => CharacterName.ToUpperInvariant();
    public string ServerName { get => _serverName; private set => SetProperty(ref _serverName, value); }
    public string ModeText { get => _modeText; private set => SetProperty(ref _modeText, value); }
    public string EncounterTime { get => _encounterTime; private set => SetProperty(ref _encounterTime, value); }
    public long MaxDamage { get => _maxDamage; private set => SetProperty(ref _maxDamage, value); }
    public bool CombinePetDamage
    {
        get => _combinePetDamage;
        set
        {
            if (!SetProperty(ref _combinePetDamage, value)) return;
            _dataVersion++;
            _renderedDataVersion = -1;
            RefreshDisplay(force: true);
        }
    }
    public bool IsPetDamageExpanded
    {
        get => _isPetDamageExpanded;
        set => SetProperty(ref _isPetDamageExpanded, value);
    }
    public string LogFolderText => _activeLogPath is null ? DefaultLogFolder : Path.GetDirectoryName(_activeLogPath) ?? DefaultLogFolder;

    public async Task InitializeAsync(string? folder = null)
    {
        if (folder is null)
        {
            var savedFolder = AppSettingsStore.TryLoadLogFolder();
            folder = savedFolder is not null && TryFindLatestLog(savedFolder, out _, out _)
                ? savedFolder
                : DefaultLogFolder;
        }

        if (!TryFindLatestLog(folder, out var latest, out var error))
        {
            StatusText = error;
            return;
        }
        await LoadLogAsync(latest!.FullName);
    }

    public async Task<string?> LoadFolderAsync(string folder)
    {
        if (!TryFindLatestLog(folder, out var latest, out var error)) return error;
        if (!await LoadLogAsync(latest!.FullName)) return "The selected log could not be opened.";
        return await AppSettingsStore.TrySaveLogFolderAsync(folder)
            ? null
            : "Monitoring started, but settings.json could not be saved beside the app. Check that the app folder is writable.";
    }

    public void ResetEncounter()
    {
        _encounter?.Reset();
        _dataVersion++;
        SelectLiveHistory();
        RefreshDisplay(force: true);
    }

    public void ShowOffense() => SetBreakdownMode(BreakdownMode.Offense);
    public void ShowDefense() => SetBreakdownMode(BreakdownMode.Defense);
    public void ShowHealing() => SetBreakdownMode(BreakdownMode.Healing);

    public void SetBuffFilter(string mode) => BuffFilterMode = mode;

    public BuffRuleViewModel AddBuffRule()
    {
        var rule = new BuffRuleViewModel(new BuffRuleSettings(Guid.NewGuid(), string.Empty, 9 * 60 + 6, 3.4,
            true, false, BuffAlertMode.Sound, BuffSoundKind.Chime, string.Empty));
        BuffRules.Add(rule);
        SelectedBuffRule = rule;
        BuffFilterMode = "All";
        BuffRulesView.Refresh();
        return rule;
    }

    public async Task<string?> DeleteBuffRuleAsync(BuffRuleViewModel rule)
    {
        await _buffTimingGate.WaitAsync();
        try
        {
            BuffRules.Remove(rule);
            if (ReferenceEquals(SelectedBuffRule, rule)) SelectedBuffRule = null;
            // Persist the in-memory list as-is. Full SaveBuffRulesAsync re-resolves every
            // sibling against the spell catalog and can abort the delete (rule stays on disk).
            ApplyBuffConfiguration();
            BuffRulesView.Refresh();
            return await PersistBuffRulesFromMemoryAsync()
                ? null
                : "Buff rules could not be saved to spelltracker.json. Check that the app folder is writable.";
        }
        finally
        {
            _buffTimingGate.Release();
        }
    }

    public async Task<string?> SaveBuffRulesAsync()
    {
        await _buffTimingGate.WaitAsync();
        try
        {
            var settings = new List<BuffRuleSettings>(BuffRules.Count);
            foreach (var rule in BuffRules)
            {
                if (!rule.TryCreateSettings(out var configured, out var error))
                {
                    SelectedBuffRule = rule;
                    var displayName = string.IsNullOrWhiteSpace(rule.SpellName) ? "New buff" : rule.SpellName;
                    return $"{displayName}: {error}";
                }
                if (!TryResolveSpell(rule.SpellName, out var spell, out error))
                {
                    SelectedBuffRule = rule;
                    rule.SetSpellValidation(error);
                    return error;
                }
                var previousFamily = SpellNameNormalizer.GetFamilyName(rule.SpellName);
                rule.SpellName = spell!.Name;
                rule.SetSpellValidation(null);
                rule.SetIcon(_spellDataCatalog?.GetIcon(spell));
                rule.ApplyCatalogTimings(spell,
                    force: !previousFamily.Equals(spell.Name, StringComparison.OrdinalIgnoreCase),
                    casterLevel: _characterLevel);
                if (!rule.TryCreateSettings(out configured, out error))
                {
                    SelectedBuffRule = rule;
                    var displayName = string.IsNullOrWhiteSpace(rule.SpellName) ? "New buff" : rule.SpellName;
                    return $"{displayName}: {error}";
                }
                settings.Add(configured! with { SpellName = spell.Name });
            }

            var duplicate = settings.GroupBy(rule => SpellNameNormalizer.GetFamilyName(rule.SpellName),
                StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null) return $"Only one tracking rule can use the spell name {duplicate.Key}.";

            _buffTracker.Configure(settings, ResolveFadeMessages, ResolveSelfAppliedMessages,
                ResolveOtherAppliedMessages,
                suffix => _spellDataCatalog?.IsAmbiguousOtherAppliedSuffix(suffix) == true);
            RefreshBuffRuleIcons();
            RefreshOverlayEntries(DateTime.Now);
            BuffRulesView.Refresh();
            return await SpellTrackerStore.TrySaveBuffRulesAsync(settings)
                ? null
                : "Buff rules could not be saved to spelltracker.json. Check that the app folder is writable.";
        }
        finally
        {
            _buffTimingGate.Release();
        }
    }

    public string? TestSelectedBuffAlert()
    {
        if (SelectedBuffRule is null) return "Select a buff first.";
        if (!SelectedBuffRule.TryCreateSettings(out var settings, out var error)) return error;
        _buffAlertService.Test(settings!);
        return null;
    }

    public string? ValidateBuffSpell(BuffRuleViewModel? rule)
    {
        if (rule is null) return null;
        if (string.IsNullOrWhiteSpace(rule.SpellName))
        {
            rule.SetSpellValidation("Enter a spell name.");
            return "Enter a spell name.";
        }
        if (!TryResolveSpell(rule.SpellName, out var spell, out var error))
        {
            rule.SetSpellValidation(error);
            return error;
        }
        var previousFamily = SpellNameNormalizer.GetFamilyName(rule.SpellName);
        rule.SpellName = spell!.Name;
        rule.SetSpellValidation(null);
        rule.SetIcon(_spellDataCatalog?.GetIcon(spell));
        rule.ApplyCatalogTimings(spell,
            force: string.IsNullOrWhiteSpace(previousFamily) ||
                   !previousFamily.Equals(spell.Name, StringComparison.OrdinalIgnoreCase),
            casterLevel: _characterLevel);
        return null;
    }

    public string? ResetSelectedBuffTimingsToCatalog()
    {
        if (SelectedBuffRule is null) return "Select a buff first.";
        if (!TryResolveSpell(SelectedBuffRule.SpellName, out var spell, out var error))
        {
            SelectedBuffRule.SetSpellValidation(error);
            return error;
        }
        SelectedBuffRule.SpellName = spell!.Name;
        SelectedBuffRule.SetSpellValidation(null);
        SelectedBuffRule.SetIcon(_spellDataCatalog?.GetIcon(spell));
        SelectedBuffRule.ApplyCatalogTimings(spell, force: true, casterLevel: _characterLevel);
        return null;
    }

    private async Task ReseedCatalogTimingsFromCharacterLevelAsync()
    {
        var catalog = _spellDataCatalog;
        if (catalog is null || _characterLevel <= 0) return;

        var buffChanged = false;
        await _buffTimingGate.WaitAsync();
        try
        {
            foreach (var rule in BuffRules)
            {
                if (rule.CastSource != SpellTimingSource.Catalog &&
                    rule.DurationSource != SpellTimingSource.Catalog) continue;
                if (!catalog.TryResolveFamily(rule.SpellName, out var spell) || spell is null) continue;
                var beforeCast = rule.CastTimeText;
                var beforeDuration = rule.DurationText;
                rule.ApplyCatalogTimings(spell, force: false, casterLevel: _characterLevel);
                if (!string.Equals(beforeCast, rule.CastTimeText, StringComparison.Ordinal) ||
                    !string.Equals(beforeDuration, rule.DurationText, StringComparison.Ordinal))
                    buffChanged = true;
            }
            if (buffChanged) ApplyBuffConfiguration(pruneMissing: false);
        }
        finally
        {
            _buffTimingGate.Release();
        }

        var dotChanged = await DotSpellTracker.ReseedCatalogTimingsAsync(_characterLevel);
        var controlChanged = await ControlSpellTracker.ReseedCatalogTimingsAsync(_characterLevel);
        var hostileChanged = await HostileSpellTracker.ReseedCatalogTimingsAsync(_characterLevel);
        if (buffChanged)
        {
            await _buffTimingGate.WaitAsync();
            try { _ = await PersistBuffRulesFromMemoryAsync(); }
            finally { _buffTimingGate.Release(); }
        }
        if (dotChanged) _ = await DotSpellTracker.SaveAsync();
        if (controlChanged) _ = await ControlSpellTracker.SaveAsync();
        if (hostileChanged) _ = await HostileSpellTracker.SaveAsync();
    }

    /// <summary>
    /// Writes the current BuffRules collection to disk without catalog re-validation.
    /// Deleted rules are never resurrected from the previous file.
    /// </summary>
    private async Task<bool> PersistBuffRulesFromMemoryAsync()
    {
        var previous = SpellTrackerStore.TryLoadBuffRules().ToDictionary(item => item.Id);
        var settings = new List<BuffRuleSettings>(BuffRules.Count);
        foreach (var item in BuffRules)
        {
            if (item.TryCreateSettings(out var configured, out _))
                settings.Add(configured!);
            else if (previous.TryGetValue(item.Id, out var prior))
                settings.Add(prior);
        }

        return await SpellTrackerStore.TrySaveBuffRulesAsync(settings);
    }

    private void NoteCharacterLevelFromMessage(string message)
    {
        if (!SpellDataCatalog.TryParseLevelUp(message, out var level) || level == _characterLevel) return;
        _characterLevel = level;
        _ = ReseedCatalogTimingsFromCharacterLevelAsync();
    }

    private void SetBreakdownMode(BreakdownMode mode)
    {
        if (_breakdownMode == mode) return;
        _breakdownMode = mode;
        RaiseBreakdownProperties();
        RaisePropertyChanged(nameof(OffenseVisibility));
        RaisePropertyChanged(nameof(DefenseVisibility));
        RaisePropertyChanged(nameof(HealingVisibility));
    }

    private async Task<bool> LoadLogAsync(string path)
    {
        try
        {
            await _loadGate.WaitAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (_disposed) return false;
            return await LoadLogCoreAsync(path, _lifetimeCancellation.Token);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<bool> LoadLogCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (!LogIdentity.TryFromPath(path, out var identity) || identity is null)
        {
            StatusText = "The selected filename is not a character log";
            return false;
        }

        await StopMonitorAsync();
        Interlocked.Increment(ref _parseGeneration);
        ClearParsedLines();
        _activeLogPath = path;
        CharacterName = identity.Character;
        ServerName = identity.Server;
        EnsurePlaySession(identity.Character, identity.Server);
        var parser = new LogLineParser(identity.Character);
        var group = new GroupStateTracker(identity.Character);
        _parser = parser;
        _group = group;
        _encounter = new EncounterTracker(identity.Character);
        _buffTracker.ClearRuntime();
        DotSpellTracker.ClearRuntime();
        ControlSpellTracker.ClearRuntime();
        HostileSpellTracker.ClearRuntime();
        EncounterHistory.Clear();
        _archivedStarts.Clear();
        _dataVersion++;
        _renderedDataVersion = -1;
        _lastRenderedSecond = -1;
        _renderedHistory = null;
        EnsureLiveHistoryCard();
        SelectLiveHistory();
        StatusText = "Preparing live monitoring…";
        RaisePropertyChanged(nameof(LogFolderText));

        LogMonitorStart liveStart;
        try
        {
            var spellCatalogTask = Task.Run(() => SpellDataCatalog.TryLoadForLog(path, _spellIconStyle),
                cancellationToken);
            var levelTask = Task.Run(() => SpellDataCatalog.TryReadLatestCharacterLevel(path), cancellationToken);
            liveStart = await LogFileMonitor.CaptureLiveStartAsync(path, cancellationToken);
            var groupRestoreTask = Task.Run(() => GroupContextRestorer.RestoreAsync(path, liveStart.ResumePosition,
                parser, group, cancellationToken), cancellationToken);
            await Task.WhenAll(spellCatalogTask, groupRestoreTask, levelTask);
            _spellDataCatalog = await spellCatalogTask;
            if (await levelTask is { } level) _characterLevel = level;
            RaisePropertyChanged(nameof(SpellCatalog));
            DotSpellTracker.NotifyCatalogChanged();
            ControlSpellTracker.NotifyCatalogChanged();
            HostileSpellTracker.NotifyCatalogChanged();
            await ReseedCatalogTimingsFromCharacterLevelAsync();
            ApplyBuffConfiguration();
            DotSpellTracker.RefreshConfiguration();
            ControlSpellTracker.RefreshConfiguration();
            HostileSpellTracker.RefreshConfiguration();
        }
        catch (IOException)
        {
            StatusText = "OFFLINE";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            StatusText = "OFFLINE";
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        RefreshDisplay(force: true);
        StatusText = "LIVE • Monitoring log";
        var monitorParser = _parser;
        var monitorGeneration = _parseGeneration;
        _monitor = new LogFileMonitor(path, liveStart.ResumePosition,
            (line, cancellation) => ParseAndQueueAsync(line, monitorParser, monitorGeneration, cancellation),
            isHealthy => ReportMonitorHealthAsync(monitorGeneration, isHealthy),
            liveStart.DiscardInitialPartialLine);
        _monitor.Start();
        return true;
    }

    public async Task<string?> PopulateSessionFromLastHoursAsync(double hours = 3)
    {
        if (_activeLogPath is null || !LogIdentity.TryFromPath(_activeLogPath, out var identity) || identity is null)
            return "No character log is loaded.";

        try
        {
            var lookback = TimeSpan.FromHours(Math.Clamp(hours, 0.25, 24));
            var built = await Task.Run(() =>
                SessionLogBackfill.TryBuild(_activeLogPath, identity.Character, identity.Server, lookback));
            if (built is null)
                return "No session data found in that log window.";

            ApplyBackfillSession(built);
            return null;
        }
        catch (IOException)
        {
            return "The character log could not be read.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Access to the character log was denied.";
        }
    }

    private void ApplyBackfillSession(SessionRecord? built)
    {
        if (built is null) return;
        _sessionHistory.RemoveAll(item => string.Equals(item.Id, built.Id, StringComparison.Ordinal));
        _sessionHistory.Insert(0, built);
        _sessionDirty = true;

        // Reload the full session list (live + history). UpsertCurrent alone leaves a
        // newly inserted backfill invisible until the next LoadHistory.
        var live = _sessionTracker.CreateSnapshot();
        SessionHistory.LoadHistory(_sessionHistory, live);
        var backfillEntry = SessionHistory.Sessions.FirstOrDefault(item =>
            string.Equals(item.Id, built.Id, StringComparison.Ordinal));
        if (backfillEntry is not null)
        {
            backfillEntry.IsExpanded = true;
            SessionHistory.SelectedSession = backfillEntry;
        }

        PersistSessionHistoryIfNeeded(force: true);
    }

    private async Task ParseAndQueueAsync(string line, LogLineParser parser, int generation,
        CancellationToken cancellationToken)
    {
        if (generation != Volatile.Read(ref _parseGeneration) ||
            !parser.TryParse(line, out var parsed) || parsed is null) return;

        await _parsedQueueSlots.WaitAsync(cancellationToken);
        if (generation != Volatile.Read(ref _parseGeneration))
        {
            _parsedQueueSlots.Release();
            return;
        }

        _parsedLines.Enqueue(new QueuedParsedLine(generation, parsed));
        ScheduleParsedLineDrain();
    }

    private void ScheduleParsedLineDrain()
    {
        if (Interlocked.Exchange(ref _drainScheduled, 1) != 0) return;
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            Volatile.Write(ref _drainScheduled, 0);
            return;
        }
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DrainParsedLines));
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _drainScheduled, 0);
        }
    }

    private void DrainParsedLines()
    {
        try
        {
            var generation = Volatile.Read(ref _parseGeneration);
            var remaining = ParsedLineBatchSize;
            while (remaining-- > 0 && _parsedLines.TryDequeue(out var queued))
            {
                _parsedQueueSlots.Release();
                if (queued.Generation == generation) ProcessParsedLine(queued.Parsed);
            }
        }
        finally
        {
            Volatile.Write(ref _drainScheduled, 0);
            if (!_parsedLines.IsEmpty) ScheduleParsedLineDrain();
        }
    }

    private void ClearParsedLines()
    {
        while (_parsedLines.TryDequeue(out _)) _parsedQueueSlots.Release();
    }

    private Task ReportMonitorHealthAsync(int generation, bool isHealthy)
    {
        if (_disposed || generation != Volatile.Read(ref _parseGeneration) ||
            _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        try
        {
            return _dispatcher.InvokeAsync(() =>
                StatusText = isHealthy ? "LIVE • Monitoring log" : "OFFLINE").Task;
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask;
        }
    }

    private static bool TryFindLatestLog(string folder, out FileInfo? latest, out string error)
    {
        latest = null;
        if (!Directory.Exists(folder))
        {
            error = "The selected Logs folder does not exist.";
            return false;
        }

        try
        {
            latest = new DirectoryInfo(folder).EnumerateFiles("eqlog_*.txt")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            error = latest is null
                ? "The selected folder does not contain any eqlog_*.txt character logs."
                : string.Empty;
            return latest is not null;
        }
        catch (IOException)
        {
            error = "The selected Logs folder could not be read.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Access to the selected Logs folder was denied.";
            return false;
        }
    }

    private void ProcessParsedLine(ParsedLogLine parsed)
    {
        if (_group is null || _encounter is null) return;

        _lastLogTimestamp = parsed.Timestamp;
        _lastLogWallClock = DateTime.Now;

        if (_sessionTracker.Observe(parsed.Timestamp, parsed.Message))
        {
            _sessionDirty = true;
            RefreshSessionHistoryUi();
            PersistSessionHistoryIfNeeded(force: false);
        }

        QuestTracker.ObserveLootMessage(parsed.Message);
        SkyTracker.ObserveLootMessage(parsed.Message);
        NoteCharacterLevelFromMessage(parsed.Message);

        _buffTracker.Observe(parsed.Timestamp, parsed.Message);
        DotSpellTracker.Observe(parsed.Timestamp, parsed.Message);
        ControlSpellTracker.Observe(parsed.Timestamp, parsed.Message);
        HostileSpellTracker.Observe(parsed.Timestamp, parsed.Message);

        var priorStart = _encounter.StartedAt;
        var priorCompletionCandidate = _encounter.CompletionCandidateAt;
        var priorFinalized = _encounter.IsFinalized;
        var priorMode = _group.IsGrouped ? "GROUP" : "SOLO";
        var eventTimestamp = parsed.Damage?.Timestamp ?? parsed.Outcome?.Timestamp;
        var mayStartNewEncounter = priorStart.HasValue && eventTimestamp.HasValue &&
                                   (priorFinalized || (_encounter.CompletionCandidateAt.HasValue &&
                                    eventTimestamp.Value - _encounter.CompletionCandidateAt.Value >=
                                    _encounter.KillCompletionGrace) || (_encounter.LastDamageAt.HasValue &&
                                    eventTimestamp.Value - _encounter.LastDamageAt.Value > _encounter.EncounterTimeout));
        // The refresh timer usually archives a finished encounter before the next log
        // line arrives, and Archive ignores a start that is already recorded, so
        // cloning the aggregate again would be pure waste.
        var priorSnapshot = mayStartNewEncounter && !_archivedStarts.Contains(priorStart!.Value)
            ? _encounter.CreateSnapshot(_encounter.CompletionCandidateAt ?? _encounter.LastDamageAt ?? parsed.Timestamp)
            : null;

        var change = _group.Process(parsed.Message, parsed.Timestamp);
        if (parsed.Healing is not null) _group.ObserveHealing(parsed.Healing);
        if (parsed.Damage is not null) _group.ObserveDamage(parsed.Damage);
        if (parsed.Outcome is not null) _group.ObserveOutcome(parsed.Outcome);

        if (priorStart.HasValue && change.Kind is GroupChangeKind.EnteredGroup or GroupChangeKind.LocalPlayerLeft)
        {
            var boundaryTime = _encounter.CompletionCandidateAt ?? _encounter.LastDamageAt ?? parsed.Timestamp;
            if (_encounter.CreateSnapshot(boundaryTime)
                is { } boundarySnapshot)
            {
                Archive(boundarySnapshot, priorMode);
            }
            // Preserve the completed fight on screen, while making the first combat
            // event in the new mode reset into a clean encounter.
            _encounter.FinalizeAt(boundaryTime);
        }

        _encounter.ApplyGroupChange(change);
        _encounter.ProcessMessage(parsed.Timestamp, parsed.Message);
        if (parsed.Damage is not null) _encounter.Process(parsed.Damage, _group);
        if (parsed.Healing is not null) _encounter.ProcessHealing(parsed.Healing, _group);
        if (parsed.Outcome is not null) _encounter.ProcessOutcome(parsed.Outcome, _group);

        if (priorStart.HasValue && _encounter.StartedAt != priorStart && priorSnapshot is not null)
            Archive(priorSnapshot, priorMode);
        if (_encounter.StartedAt.HasValue && _encounter.StartedAt != priorStart &&
            SelectedHistory is { IsLive: false })
            SelectLiveHistory();

        if (parsed.Damage is not null || parsed.Healing is not null || parsed.Outcome is not null ||
            change.Kind != GroupChangeKind.None || priorStart != _encounter.StartedAt ||
            priorCompletionCandidate != _encounter.CompletionCandidateAt || priorFinalized != _encounter.IsFinalized)
        {
            _dataVersion++;
        }
    }

    private void RefreshDisplay(bool force = false)
    {
        if (_encounter is null || _group is null) return;
        var now = DateTime.Now;
        // Map wall-clock idle onto the last log timestamp so backlog drain cannot
        // finalize a fight early, while AFK (no new lines) still times out.
        var finalizeAt = _lastLogTimestamp is { } logTs && _lastLogWallClock is { } wall
            ? logTs + (now - wall)
            : now;
        var wasFinalized = _encounter.IsFinalized;
        _encounter.FinalizeIfInactive(finalizeAt);
        if (!wasFinalized && _encounter.IsFinalized) _dataVersion++;
        if (_encounter.IsFinalized && _encounter.StartedAt is { } startedAt && !_archivedStarts.Contains(startedAt) &&
            _encounter.CreateSnapshot(finalizeAt) is { } finished && Archive(finished, _group.IsGrouped ? "GROUP" : "SOLO"))
        {
            _dataVersion++;
        }

        EnsureLiveHistoryCard();
        var history = SelectedHistory;
        var viewingArchive = history is { IsLive: false, Snapshot: not null };
        var snapshot = viewingArchive ? history!.Snapshot : null;
        var seconds = snapshot is null ? _encounter.GetElapsedSeconds(finalizeAt) : history!.Seconds;
        var renderedSecond = (long)Math.Floor(seconds);
        if (!force && viewingArchive && ReferenceEquals(_renderedHistory, history) &&
            _renderedDataVersion == _dataVersion)
        {
            UpdateLiveHistoryCard(_cachedLiveCardDamage, _cachedLiveCardDps);
            return;
        }
        if (!force && _renderedDataVersion == _dataVersion && _lastRenderedSecond == renderedSecond &&
            ReferenceEquals(_renderedHistory, history))
        {
            UpdateLiveHistoryCard(_cachedLiveCardDamage, _cachedLiveCardDps);
            return;
        }

        var rawAggregates = snapshot is null
            ? _encounter.CreateCombatantArray()
            : snapshot.Combatants.ToArray();
        var aggregates = (CombinePetDamage ? CombinePetAggregates(rawAggregates) : rawAggregates)
            .OrderByDescending(item => item.Damage / Math.Max(1, seconds))
            .ThenByDescending(item => item.Damage)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasEncounter = snapshot is not null || _encounter.StartedAt.HasValue;
        var isWarmingUp = snapshot is null && hasEncounter && !_encounter.IsFinalized && seconds < 3;

        ModeText = viewingArchive ? history!.Mode : (_group.IsGrouped ? "GROUP" : "SOLO");
        EncounterTime = TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);
        var localSources = aggregates.Where(item =>
            item.Name.Equals(CharacterName, StringComparison.OrdinalIgnoreCase) ||
            (CombinePetDamage && item.OwnerName?.Equals(CharacterName, StringComparison.OrdinalIgnoreCase) == true))
            .ToArray();
        var localPlayer = localSources.Length == 0
            ? null
            : new CombatantAggregate(CharacterName) { Damage = localSources.Sum(item => item.Damage) };

        PopulateCombatants(aggregates, seconds, isWarmingUp);
        MaxDamage = Math.Max(1, Combatants.FirstOrDefault()?.Damage ?? 1);
        _cachedLiveCardDamage = localPlayer?.Damage ?? 0;
        _cachedLiveCardDps = localPlayer is null || !hasEncounter || isWarmingUp
            ? 0
            : localPlayer.Damage / Math.Max(1, seconds);
        UpdateLiveHistoryCard(_cachedLiveCardDamage, _cachedLiveCardDps);
        _renderedDataVersion = _dataVersion;
        _lastRenderedSecond = renderedSecond;
        _renderedHistory = history;
    }

    private void OnRefreshTimer()
    {
        RefreshDisplay();
        var now = DateTime.Now;
        foreach (var alert in _buffTracker.Tick(now)) _buffAlertService.Play(alert);
        foreach (var rule in BuffRules) rule.ApplyRuntime(_buffTracker.GetSnapshot(rule.Id, now));
        RefreshOverlayEntries(now);
        DotSpellTracker.Tick(now);
        ControlSpellTracker.Tick(now);
        HostileSpellTracker.Tick(now);
        // Duration ticks every second even when no new XP/loot lines arrive; dirty only
        // gates disk persist.
        if (_sessionTracker.Current is not null || _sessionDirty)
            RefreshSessionHistoryUi();
        PersistSessionHistoryIfNeeded(force: false);
    }

    private bool FilterBuffRule(object item)
    {
        if (item is not BuffRuleViewModel rule) return false;
        if (!string.IsNullOrWhiteSpace(BuffSearchText) &&
            !rule.SpellName.Contains(BuffSearchText.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        return BuffFilterMode switch
        {
            "Enabled" => rule.IsEnabled,
            "Disabled" => !rule.IsEnabled,
            _ => true
        };
    }

    private void ApplyBuffConfiguration(bool pruneMissing = true)
    {
        var settings = BuffRules.Select(rule => rule.TryCreateSettings(out var configured, out _)
            ? configured
            : null).OfType<BuffRuleSettings>().ToArray();
        _buffTracker.Configure(settings, ResolveFadeMessages, ResolveSelfAppliedMessages,
            ResolveOtherAppliedMessages,
            suffix => _spellDataCatalog?.IsAmbiguousOtherAppliedSuffix(suffix) == true,
            pruneMissing: pruneMissing);
        RefreshBuffRuleIcons();
        RefreshOverlayEntries(DateTime.Now);
    }

    private void RefreshBuffRuleIcons()
    {
        foreach (var rule in BuffRules)
            rule.SetIcon(string.IsNullOrWhiteSpace(rule.SpellName) ? null : _spellDataCatalog?.GetIcon(rule.SpellName));
    }

    private void ApplySpellIconStyle()
    {
        _spellDataCatalog?.SetIconStyle(_spellIconStyle);
        RefreshBuffRuleIcons();
        OverlayBuffEntries.Clear();
        RefreshOverlayEntries(DateTime.Now);
        DotSpellTracker.RefreshIcons();
        ControlSpellTracker.RefreshIcons();
        HostileSpellTracker.RefreshIcons();
        _dataVersion++;
        _renderedDataVersion = -1;
        RefreshDisplay(force: true);
    }

    private IReadOnlyList<string> ResolveFadeMessages(string spellName) =>
        _spellDataCatalog is not null && _spellDataCatalog.TryResolveFamily(spellName, out var spell)
            ? spell!.FadeMessages
            : [];

    private IReadOnlyList<string> ResolveSelfAppliedMessages(string spellName) =>
        _spellDataCatalog is not null && _spellDataCatalog.TryResolveFamily(spellName, out var spell)
            ? spell!.SelfAppliedMessages
            : [];

    private IReadOnlyList<string> ResolveOtherAppliedMessages(string spellName) =>
        _spellDataCatalog is not null && _spellDataCatalog.TryResolveFamily(spellName, out var spell)
            ? spell!.OtherAppliedMessageSuffixes
            : [];

    private bool TryResolveSpell(string spellName, out SpellDataEntry? spell, out string error)
    {
        spell = null;
        if (_spellDataCatalog is null)
        {
            error = "EverQuest Legends spell data is not available. Select the game's Logs folder and try again.";
            return false;
        }
        if (_spellDataCatalog.TryResolveFamily(spellName, out spell))
        {
            error = string.Empty;
            return true;
        }

        error = "Spell not found";
        return false;
    }

    private void RefreshOverlayEntries(DateTime now)
    {
        var visibleIds = BuffRules.Where(rule => rule.IsEnabled && rule.ShowInOverlay)
            .Select(rule => rule.Id).ToHashSet();
        var snapshots = _buffTracker.GetActiveSnapshots(now)
            .Where(snapshot => visibleIds.Contains(snapshot.RuleId)).ToArray();
        var desiredKeys = snapshots.Select(BuffOverlayEntryViewModel.CreateKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stale in OverlayBuffEntries.Where(entry => !desiredKeys.Contains(entry.InstanceKey)).ToArray())
            OverlayBuffEntries.Remove(stale);

        for (var index = 0; index < snapshots.Length; index++)
        {
            var snapshot = snapshots[index];
            var key = BuffOverlayEntryViewModel.CreateKey(snapshot);
            var entry = OverlayBuffEntries.FirstOrDefault(item =>
                item.InstanceKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                var ruleIcon = BuffRules.FirstOrDefault(rule => rule.Id == snapshot.RuleId)?.Icon;
                entry = new BuffOverlayEntryViewModel(snapshot,
                    icon: ruleIcon ?? _spellDataCatalog?.GetIcon(snapshot.SpellName));
                OverlayBuffEntries.Insert(Math.Min(index, OverlayBuffEntries.Count), entry);
            }
            else
            {
                entry.Update(snapshot);
                var currentIndex = OverlayBuffEntries.IndexOf(entry);
                if (currentIndex != index) OverlayBuffEntries.Move(currentIndex, index);
            }
        }
    }

    private static CombatantAggregate[] CombinePetAggregates(CombatantAggregate[] aggregates)
    {
        var hasPets = false;
        for (var i = 0; i < aggregates.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(aggregates[i].OwnerName))
            {
                hasPets = true;
                break;
            }
        }
        if (!hasPets) return aggregates;

        var owners = aggregates.Where(item => string.IsNullOrWhiteSpace(item.OwnerName))
            .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var petsByOwner = aggregates.Where(item => !string.IsNullOrWhiteSpace(item.OwnerName))
            .GroupBy(item => item.OwnerName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var ownerName in petsByOwner.Keys) owners.TryAdd(ownerName, new CombatantAggregate(ownerName));

        var combinedOwners = new CombatantAggregate[owners.Count];
        var index = 0;
        foreach (var owner in owners.Values)
        {
            var pets = petsByOwner.GetValueOrDefault(owner.Name) ?? [];
            long petDamage = 0, petHits = 0, petMeleeHits = 0, petSpellHits = 0;
            long petMeleeCrit = 0, petSpellCrit = 0, petMisses = 0, petFizzles = 0, petResists = 0;
            long petTaken = 0, petInHits = 0, petInMelee = 0, petInMisses = 0;
            long petDodges = 0, petParries = 0, petBlocks = 0, petRipostes = 0;
            long petAbsorbed = 0, petSpellAbsorbs = 0, petInSpellResists = 0;
            long petStunsLanded = 0, petStunsTaken = 0, petHealing = 0, petPotential = 0;
            long petDirectHeals = 0, petHots = 0, petCritHeals = 0;
            foreach (var pet in pets)
            {
                petDamage += pet.Damage;
                petHits += pet.Hits;
                petMeleeHits += pet.MeleeHits;
                petSpellHits += pet.SpellHits;
                petMeleeCrit += pet.MeleeCriticalHits;
                petSpellCrit += pet.SpellCriticalHits;
                petMisses += pet.Misses;
                petFizzles += pet.SpellFizzles;
                petResists += pet.SpellResists;
                petTaken += pet.DamageTaken;
                petInHits += pet.IncomingHits;
                petInMelee += pet.IncomingMeleeHits;
                petInMisses += pet.IncomingMisses;
                petDodges += pet.Dodges;
                petParries += pet.Parries;
                petBlocks += pet.Blocks;
                petRipostes += pet.Ripostes;
                petAbsorbed += pet.Absorbed;
                petSpellAbsorbs += pet.SpellAbsorbs;
                petInSpellResists += pet.IncomingSpellResists;
                petStunsLanded += pet.StunsLanded;
                petStunsTaken += pet.StunsTaken;
                petHealing += pet.Healing;
                petPotential += pet.PotentialHealing;
                petDirectHeals += pet.DirectHeals;
                petHots += pet.HealOverTimeTicks;
                petCritHeals += pet.CriticalHeals;
            }

            var combined = new CombatantAggregate(owner.Name)
            {
                Damage = owner.Damage + petDamage,
                Hits = owner.Hits + (int)petHits,
                MeleeHits = owner.MeleeHits + (int)petMeleeHits,
                SpellHits = owner.SpellHits + (int)petSpellHits,
                MeleeCriticalHits = owner.MeleeCriticalHits + (int)petMeleeCrit,
                SpellCriticalHits = owner.SpellCriticalHits + (int)petSpellCrit,
                Misses = owner.Misses + (int)petMisses,
                SpellFizzles = owner.SpellFizzles + (int)petFizzles,
                SpellResists = owner.SpellResists + (int)petResists,
                DamageTaken = owner.DamageTaken + petTaken,
                IncomingHits = owner.IncomingHits + (int)petInHits,
                IncomingMeleeHits = owner.IncomingMeleeHits + (int)petInMelee,
                IncomingMisses = owner.IncomingMisses + (int)petInMisses,
                Dodges = owner.Dodges + (int)petDodges,
                Parries = owner.Parries + (int)petParries,
                Blocks = owner.Blocks + (int)petBlocks,
                Ripostes = owner.Ripostes + (int)petRipostes,
                Absorbed = owner.Absorbed + (int)petAbsorbed,
                SpellAbsorbs = owner.SpellAbsorbs + (int)petSpellAbsorbs,
                IncomingSpellResists = owner.IncomingSpellResists + (int)petInSpellResists,
                StunsLanded = owner.StunsLanded + (int)petStunsLanded,
                StunsTaken = owner.StunsTaken + (int)petStunsTaken,
                Healing = owner.Healing + petHealing,
                PotentialHealing = owner.PotentialHealing + petPotential,
                DirectHeals = owner.DirectHeals + (int)petDirectHeals,
                HealOverTimeTicks = owner.HealOverTimeTicks + (int)petHots,
                CriticalHeals = owner.CriticalHeals + (int)petCritHeals
            };

            MergeAbilities(combined.Abilities, owner.Abilities.Values);
            MergeAbilities(combined.IncomingAbilities,
                pets.Prepend(owner).SelectMany(combatant => combatant.IncomingAbilities.Values));
            MergeAbilities(combined.HealingAbilities,
                pets.Prepend(owner).SelectMany(combatant => combatant.HealingAbilities.Values));
            MergeTargets(combined.Targets, pets.Prepend(owner).SelectMany(combatant => combatant.Targets.Values));

            if (petDamage > 0)
            {
                var petSummary = new AbilityAggregate("PET DMG") { Damage = petDamage };
                foreach (var abilityGroup in pets.SelectMany(pet => pet.Abilities.Values)
                             .GroupBy(ability => SpellNameNormalizer.GetFamilyName(ability.Name),
                                 StringComparer.OrdinalIgnoreCase))
                {
                    var display = abilityGroup.OrderByDescending(item => item.Name.Length).First().Name;
                    petSummary.Children[abilityGroup.Key] = new AbilityAggregate(display)
                    {
                        Damage = abilityGroup.Sum(item => item.Damage),
                        Hits = abilityGroup.Sum(item => item.Hits),
                        ProcHits = abilityGroup.Sum(item => item.ProcHits),
                        ProcDamage = abilityGroup.Sum(item => item.ProcDamage)
                    };
                }
                combined.Abilities[petSummary.Name] = petSummary;
            }
            combinedOwners[index++] = combined;
        }
        return combinedOwners;
    }

    private static void MergeAbilities(Dictionary<string, AbilityAggregate> destination,
        IEnumerable<AbilityAggregate> abilities)
    {
        foreach (var ability in abilities)
        {
            var key = SpellNameNormalizer.GetFamilyName(ability.Name);
            if (!destination.TryGetValue(key, out var aggregate))
            {
                aggregate = new AbilityAggregate(ability.Name);
                destination[key] = aggregate;
            }
            else
            {
                aggregate.PreferDisplayName(ability.Name);
            }
            aggregate.Damage += ability.Damage;
            aggregate.Hits += ability.Hits;
            aggregate.ProcHits += ability.ProcHits;
            aggregate.ProcDamage += ability.ProcDamage;
        }
    }

    private static void MergeTargets(Dictionary<string, TargetAggregate> destination,
        IEnumerable<TargetAggregate> targets)
    {
        foreach (var target in targets)
        {
            if (!destination.TryGetValue(target.Name, out var aggregate))
            {
                aggregate = new TargetAggregate(target.Name);
                destination[target.Name] = aggregate;
            }
            aggregate.Damage += target.Damage;
            MergeAbilities(aggregate.Abilities, target.Abilities.Values);
        }
    }

    private void PopulateCombatants(CombatantAggregate[] aggregates, double seconds, bool isWarmingUp)
    {
        var selectedName = CombinePetDamage && !string.IsNullOrWhiteSpace(SelectedCombatant?.OwnerName)
            ? SelectedCombatant.OwnerName
            : SelectedCombatant?.Name;

        var canUpdateInPlace = Combatants.Count == aggregates.Length;
        if (canUpdateInPlace)
        {
            for (var i = 0; i < aggregates.Length; i++)
            {
                if (!Combatants[i].Name.Equals(aggregates[i].Name, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Combatants[i].OwnerName, aggregates[i].OwnerName, StringComparison.OrdinalIgnoreCase))
                {
                    canUpdateInPlace = false;
                    break;
                }
            }
        }

        if (canUpdateInPlace)
        {
            for (var index = 0; index < aggregates.Length; index++)
                ApplyCombatantRow(Combatants[index], aggregates[index], seconds, isWarmingUp, index + 1);

            if (SelectedCombatant is null || !Combatants.Contains(SelectedCombatant))
            {
                SelectedCombatant = Combatants.FirstOrDefault(item =>
                                        item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                                    ?? Combatants.FirstOrDefault();
            }
            else
            {
                RaiseBreakdownProperties();
            }
            return;
        }

        Combatants.Clear();
        for (var index = 0; index < aggregates.Length; index++)
        {
            var row = new CombatantViewModel();
            ApplyCombatantRow(row, aggregates[index], seconds, isWarmingUp, index + 1);
            Combatants.Add(row);
        }

        SelectedCombatant = Combatants.FirstOrDefault(item => item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                            ?? Combatants.FirstOrDefault();
    }

    private void ApplyCombatantRow(CombatantViewModel row, CombatantAggregate aggregate, double seconds,
        bool isWarmingUp, int rank)
    {
        var abilities = CreateAbilities(aggregate.Abilities.Values, seconds);
        var incomingAbilities = CreateAbilities(aggregate.IncomingAbilities.Values, seconds);
        var healingAbilities = CreateAbilities(aggregate.HealingAbilities.Values, seconds);
        var procs = CreateProcAbilities(aggregate.Abilities.Values, seconds);
        var mitigationValues = new (string Name, int Count)[]
        {
            ("Dodge", aggregate.Dodges), ("Parry", aggregate.Parries), ("Block", aggregate.Blocks),
            ("Riposte", aggregate.Ripostes), ("Absorbed", aggregate.Absorbed),
            ("Spell Resist", aggregate.IncomingSpellResists)
        };
        var recorded = mitigationValues.Where(item => item.Count > 0).ToArray();
        var mitigationTotal = Math.Max(1, recorded.Sum(item => item.Count));
        var mitigations = recorded.Select((item, colorIndex) =>
            new AbilityViewModel
            {
                Name = item.Name, Damage = item.Count,
                Share = item.Count * 100d / mitigationTotal,
                Color = ChartBrushes[colorIndex % ChartBrushes.Length]
            }).ToArray();

        row.ApplyAggregate(aggregate, seconds, isWarmingUp, rank, abilities, incomingAbilities,
            healingAbilities, mitigations, procs);
    }

    private AbilityViewModel[] CreateAbilities(IEnumerable<AbilityAggregate> source, double seconds)
    {
        var abilities = source.OrderByDescending(item => item.Damage).ToArray();
        var total = Math.Max(1, abilities.Sum(item => item.Damage));
        return abilities.Select((ability, index) => new AbilityViewModel
        {
            Name = ability.Name, Damage = ability.Damage, Hits = ability.Hits,
            Dps = ability.Damage / Math.Max(1, seconds),
            Share = ability.Damage * 100d / total,
            Color = ChartBrushes[index % ChartBrushes.Length],
            Icon = _spellDataCatalog?.GetAbilityIcon(ability.Name) ?? SpellIconAtlas.GenericIcon,
            IsPetSummary = ability.Children.Count > 0,
            Children = ability.Children.Count == 0
                ? []
                : CreateAbilities(ability.Children.Values, seconds)
        }).ToArray();
    }

    private AbilityViewModel[] CreateProcAbilities(IEnumerable<AbilityAggregate> source, double seconds)
    {
        static IEnumerable<AbilityAggregate> Flatten(AbilityAggregate ability)
        {
            if (ability.ProcHits > 0) yield return ability;
            foreach (var child in ability.Children.Values)
            foreach (var nested in Flatten(child))
                yield return nested;
        }

        var procs = source.SelectMany(Flatten)
            .GroupBy(item => SpellNameNormalizer.GetFamilyName(item.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var display = group.OrderByDescending(item => item.Name.Length).First().Name;
                return new AbilityAggregate(display)
                {
                    Damage = group.Sum(item => item.ProcDamage),
                    Hits = group.Sum(item => item.Hits),
                    ProcHits = group.Sum(item => item.ProcHits),
                    ProcDamage = group.Sum(item => item.ProcDamage)
                };
            })
            .OrderByDescending(item => item.ProcHits)
            .ThenByDescending(item => item.ProcDamage)
            .ToArray();
        if (procs.Length == 0) return [];
        var totalDamage = Math.Max(1, procs.Sum(item => item.ProcDamage));
        var elapsed = Math.Max(1, seconds);
        var minutes = Math.Max(elapsed / 60d, 1d / 60d);
        return procs.Select((ability, index) => new AbilityViewModel
        {
            Name = ability.Name,
            Damage = ability.ProcDamage,
            Hits = ability.ProcHits,
            Dps = ability.ProcDamage / elapsed,
            Ppm = ability.ProcHits / minutes,
            Share = ability.ProcDamage * 100d / totalDamage,
            Color = ChartBrushes[index % ChartBrushes.Length],
            Icon = _spellDataCatalog?.GetAbilityIcon(ability.Name) ?? SpellIconAtlas.GenericIcon
        }).ToArray();
    }

    private static Brush[] CreateChartBrushes()
    {
        Color[] colors =
        [
            Color.FromRgb(124, 92, 252), Color.FromRgb(41, 211, 194), Color.FromRgb(64, 156, 255),
            Color.FromRgb(255, 183, 77), Color.FromRgb(255, 99, 132), Color.FromRgb(155, 112, 255),
            Color.FromRgb(63, 205, 118), Color.FromRgb(255, 126, 80), Color.FromRgb(88, 204, 255)
        ];
        return colors.Select(color =>
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return (Brush)brush;
        }).ToArray();
    }

    private bool Archive(EncounterSnapshot snapshot, string mode)
    {
        if (snapshot.Combatants.Count == 0 || !_archivedStarts.Add(snapshot.StartedAt)) return false;
        EnsureLiveHistoryCard();
        EncounterHistory.Insert(1, EncounterHistoryViewModel.CreateArchived(snapshot, mode, CharacterName));
        while (EncounterHistory.Count(item => !item.IsLive) > HistoryLimit)
        {
            var oldest = EncounterHistory[^1];
            if (oldest.IsLive) break;
            _archivedStarts.Remove(oldest.StartedAt);
            EncounterHistory.RemoveAt(EncounterHistory.Count - 1);
        }
        return true;
    }

    private void EnsureLiveHistoryCard()
    {
        if (EncounterHistory.FirstOrDefault()?.IsLive == true) return;
        var existing = EncounterHistory.FirstOrDefault(item => item.IsLive);
        if (existing is not null)
        {
            EncounterHistory.Remove(existing);
            EncounterHistory.Insert(0, existing);
            return;
        }

        EncounterHistory.Insert(0, EncounterHistoryViewModel.CreateLive(CharacterName));
    }

    private void SelectLiveHistory()
    {
        EnsureLiveHistoryCard();
        var live = EncounterHistory[0];
        if (!ReferenceEquals(_selectedHistory, live))
            SelectedHistory = live;
    }

    private void UpdateLiveHistoryCard(long damage, double dps)
    {
        EnsureLiveHistoryCard();
        var live = EncounterHistory[0];
        if (!live.IsLive || _encounter is null || _group is null) return;

        var mode = _group.IsGrouped ? "GROUP" : "SOLO";
        live.UpdateLive(mode, damage, dps, _encounter.StartedAt);
    }

    private void RaiseBreakdownProperties()
    {
        RaisePropertyChanged(nameof(SelectedAbilities));
        RaisePropertyChanged(nameof(SelectedProcs));
        RaisePropertyChanged(nameof(SelectedIncomingAbilities));
        RaisePropertyChanged(nameof(SelectedMitigations));
        RaisePropertyChanged(nameof(SelectedName));
    }

    private async Task StopMonitorAsync()
    {
        if (_monitor is null) return;
        await _monitor.DisposeAsync();
        _monitor = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        Interlocked.Increment(ref _parseGeneration);
        ClearParsedLines();
        EndPlaySession();
        await PersistSessionHistoryAsync(force: true);
        _lifetimeCancellation.Cancel();
        await _loadGate.WaitAsync();
        try
        {
            await StopMonitorAsync();
        }
        finally
        {
            _loadGate.Release();
            _lifetimeCancellation.Dispose();
            _loadGate.Dispose();
        }
    }

    private void EnsurePlaySession(string character, string server)
    {
        if (_sessionTracker.Current is null)
        {
            _sessionTracker.StartSession(character, server, DateTime.Now);
            _sessionDirty = true;
            RefreshSessionHistoryUi();
            PersistSessionHistoryIfNeeded(force: true);
            return;
        }

        _sessionTracker.UpdateIdentity(character, server);
        RefreshSessionHistoryUi();
    }

    private void EndPlaySession()
    {
        var finished = _sessionTracker.EndSession(DateTime.Now);
        if (finished is null) return;
        _sessionHistory.RemoveAll(item =>
            string.Equals(item.Id, finished.Id, StringComparison.Ordinal));
        _sessionHistory.Insert(0, finished);
        _sessionDirty = true;
        SessionHistory.LoadHistory(_sessionHistory, current: null);
    }

    private void RefreshSessionHistoryUi()
    {
        var current = _sessionTracker.CreateSnapshot();
        if (current is null)
        {
            SessionHistory.LoadHistory(_sessionHistory, current: null);
            return;
        }

        SessionHistory.UpsertCurrent(current);
    }

    private void PersistSessionHistoryIfNeeded(bool force)
    {
        if (!_sessionDirty && !force) return;
        var elapsed = DateTime.UtcNow - _lastSessionPersistUtc;
        if (!force && elapsed < TimeSpan.FromSeconds(5)) return;
        _ = PersistSessionHistoryAsync(force);
    }

    private async Task PersistSessionHistoryAsync(bool force)
    {
        if (!_sessionDirty && !force) return;
        var records = BuildPersistableSessions();
        try
        {
            var saved = await SessionInfoStore.TrySaveSessionsAsync(records, CancellationToken.None);
            if (!saved)
            {
                // Keep dirty so a later flush can retry instead of silently dropping data.
                return;
            }

            _sessionDirty = false;
            _lastSessionPersistUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            // Final flush uses CancellationToken.None; ignore unexpected cancels.
        }
    }

    private List<SessionRecord> BuildPersistableSessions()
    {
        var records = _sessionHistory
            .Where(item => !SessionLogBackfill.IsBackfillId(item.Id))
            .Select(SessionTracker.Clone)
            .ToList();
        if (_sessionTracker.CreateSnapshot() is { } current)
        {
            records.RemoveAll(item => string.Equals(item.Id, current.Id, StringComparison.Ordinal));
            records.Insert(0, current);
        }

        return records;
    }
}
