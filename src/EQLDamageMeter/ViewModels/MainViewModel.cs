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
    private const int HistoryLimit = 5;
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
    private string _characterDamage = "0";
    private string _encounterDps = "—";
    private string _currentDps = "—";
    private long _maxDamage = 1;
    private string? _activeLogPath;
    private BreakdownMode _breakdownMode;
    private long _dataVersion;
    private long _renderedDataVersion = -1;
    private long _lastRenderedSecond = -1;
    private EncounterHistoryViewModel? _renderedHistory;
    private bool _combinePetDamage;
    private bool _isPetDamageExpanded;
    private BuffRuleViewModel? _selectedBuffRule;
    private string _buffSearchText = string.Empty;
    private string _buffFilterMode = "All";
    private SpellIconStyle _spellIconStyle = SpellIconStyle.Modern;
    private int _parseGeneration;
    private int _drainScheduled;
    private bool _disposed;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
            (_, _) => OnRefreshTimer(), _dispatcher);
        _spellIconStyle = AppSettingsStore.TryLoadSpellIconStyle();

        foreach (var settings in AppSettingsStore.TryLoadBuffRules())
            BuffRules.Add(new BuffRuleViewModel(settings));
        DotSpellTracker = new SpellRuleSetViewModel(SpellTrackerCategory.DamageOverTime,
            AppSettingsStore.TryLoadDotRules(), () => _spellDataCatalog,
            AppSettingsStore.TrySaveDotRulesAsync, _buffAlertService);
        ControlSpellTracker = new SpellRuleSetViewModel(SpellTrackerCategory.Control,
            AppSettingsStore.TryLoadControlRules(), () => _spellDataCatalog,
            AppSettingsStore.TrySaveControlRulesAsync, _buffAlertService);
        _buffTracker.PreserveBuffTargetOnDeath = (target, timestamp) =>
            ControlSpellTracker.HasActiveCharmTarget(target, timestamp);
        BuffRulesView = CollectionViewSource.GetDefaultView(BuffRules);
        BuffRulesView.Filter = FilterBuffRule;
        ApplyBuffConfiguration();
    }

    public ObservableCollection<CombatantViewModel> Combatants { get; } = [];
    public ObservableCollection<EncounterHistoryViewModel> EncounterHistory { get; } = [];
    public ObservableCollection<BuffRuleViewModel> BuffRules { get; } = [];
    public ObservableCollection<BuffOverlayEntryViewModel> OverlayBuffEntries { get; } = [];
    public ICollectionView BuffRulesView { get; }
    public IReadOnlyList<BuffAlertMode> BuffAlertModes { get; } = Enum.GetValues<BuffAlertMode>();
    public IReadOnlyList<BuffSoundKind> BuffSoundChoices { get; } = Enum.GetValues<BuffSoundKind>();
    public SpellRuleSetViewModel DotSpellTracker { get; }
    public SpellRuleSetViewModel ControlSpellTracker { get; }

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
    public string CharacterDamage { get => _characterDamage; private set => SetProperty(ref _characterDamage, value); }
    public string EncounterDps { get => _encounterDps; private set => SetProperty(ref _encounterDps, value); }
    public string CurrentDps { get => _currentDps; private set => SetProperty(ref _currentDps, value); }
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
        SelectedHistory = null;
        RefreshDisplay(force: true);
    }

    public void ShowOffense() => SetBreakdownMode(BreakdownMode.Offense);
    public void ShowDefense() => SetBreakdownMode(BreakdownMode.Defense);
    public void ShowHealing() => SetBreakdownMode(BreakdownMode.Healing);

    public void SetBuffFilter(string mode) => BuffFilterMode = mode;

    public BuffRuleViewModel AddBuffRule()
    {
        var rule = new BuffRuleViewModel(new BuffRuleSettings(Guid.NewGuid(), string.Empty, 9 * 60 + 6, 3.4,
            true, false, BuffAlertMode.Both, BuffSoundKind.Chime, string.Empty));
        BuffRules.Add(rule);
        SelectedBuffRule = rule;
        BuffFilterMode = "All";
        BuffRulesView.Refresh();
        return rule;
    }

    public async Task<string?> DeleteBuffRuleAsync(BuffRuleViewModel rule)
    {
        BuffRules.Remove(rule);
        if (ReferenceEquals(SelectedBuffRule, rule)) SelectedBuffRule = null;
        return await SaveBuffRulesAsync();
    }

    public async Task<string?> SaveBuffRulesAsync()
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
            rule.SpellName = spell!.Name;
            rule.SetSpellValidation(null);
            rule.SetIcon(_spellDataCatalog?.GetIcon(spell));
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
        return await AppSettingsStore.TrySaveBuffRulesAsync(settings)
            ? null
            : "Buff rules could not be saved beside the app. Check that the app folder is writable.";
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
        rule.SpellName = spell!.Name;
        rule.SetSpellValidation(null);
        rule.SetIcon(_spellDataCatalog?.GetIcon(spell));
        return null;
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
        var parser = new LogLineParser(identity.Character);
        var group = new GroupStateTracker(identity.Character);
        _parser = parser;
        _group = group;
        _encounter = new EncounterTracker(identity.Character);
        _buffTracker.ClearRuntime();
        DotSpellTracker.ClearRuntime();
        ControlSpellTracker.ClearRuntime();
        EncounterHistory.Clear();
        _archivedStarts.Clear();
        _dataVersion++;
        _renderedDataVersion = -1;
        _lastRenderedSecond = -1;
        _renderedHistory = null;
        SelectedHistory = null;
        StatusText = "Preparing live monitoring…";
        RaisePropertyChanged(nameof(LogFolderText));

        LogMonitorStart liveStart;
        try
        {
            var spellCatalogTask = Task.Run(() => SpellDataCatalog.TryLoadForLog(path, _spellIconStyle),
                cancellationToken);
            liveStart = await LogFileMonitor.CaptureLiveStartAsync(path, cancellationToken);
            var groupRestoreTask = Task.Run(() => GroupContextRestorer.RestoreAsync(path, liveStart.ResumePosition,
                parser, group, cancellationToken), cancellationToken);
            await Task.WhenAll(spellCatalogTask, groupRestoreTask);
            _spellDataCatalog = await spellCatalogTask;
            ApplyBuffConfiguration();
            DotSpellTracker.RefreshConfiguration();
            ControlSpellTracker.RefreshConfiguration();
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

        _buffTracker.Observe(parsed.Timestamp, parsed.Message);
        DotSpellTracker.Observe(parsed.Timestamp, parsed.Message);
        ControlSpellTracker.Observe(parsed.Timestamp, parsed.Message);

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
        if (_encounter.StartedAt.HasValue && _encounter.StartedAt != priorStart && SelectedHistory is not null)
            SelectedHistory = null;

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
        var wasFinalized = _encounter.IsFinalized;
        _encounter.FinalizeIfInactive(now);
        if (!wasFinalized && _encounter.IsFinalized) _dataVersion++;
        if (_encounter.IsFinalized && _encounter.StartedAt is { } startedAt && !_archivedStarts.Contains(startedAt) &&
            _encounter.CreateSnapshot(now) is { } finished && Archive(finished, _group.IsGrouped ? "GROUP" : "SOLO"))
        {
            _dataVersion++;
        }

        var history = SelectedHistory;
        if (!force && history is not null && ReferenceEquals(_renderedHistory, history)) return;
        var snapshot = history?.Snapshot;
        var seconds = snapshot is null ? _encounter.GetElapsedSeconds(now) : history!.Seconds;
        var renderedSecond = (long)Math.Floor(seconds);
        if (!force && _renderedDataVersion == _dataVersion && _lastRenderedSecond == renderedSecond &&
            ReferenceEquals(_renderedHistory, history))
        {
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

        ModeText = history?.Mode ?? (_group.IsGrouped ? "GROUP" : "SOLO");
        EncounterTime = TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);
        var localSources = aggregates.Where(item =>
            item.Name.Equals(CharacterName, StringComparison.OrdinalIgnoreCase) ||
            (CombinePetDamage && item.OwnerName?.Equals(CharacterName, StringComparison.OrdinalIgnoreCase) == true))
            .ToArray();
        var localPlayer = localSources.Length == 0
            ? null
            : new CombatantAggregate(CharacterName) { Damage = localSources.Sum(item => item.Damage) };
        CharacterDamage = (localPlayer?.Damage ?? 0).ToString("N0", CultureInfo.CurrentCulture);
        EncounterDps = localPlayer is null || !hasEncounter ? "—" : isWarmingUp ? "Calculating…" :
            (localPlayer.Damage / Math.Max(1, seconds)).ToString("N1", CultureInfo.CurrentCulture);

        if (history is not null) CurrentDps = "—";
        else
        {
            var rollingWindow = TimeSpan.FromSeconds(10);
            var rollingSeconds = Math.Min(rollingWindow.TotalSeconds, seconds);
            var rollingDamage = localPlayer is null ? 0 : _encounter.GetRollingDamageForOwner(
                CharacterName, CombinePetDamage, now, rollingWindow);
            CurrentDps = localPlayer is null || !hasEncounter ? "—" : isWarmingUp ? "Calculating…" :
                (rollingDamage / Math.Max(1, rollingSeconds)).ToString("N1", CultureInfo.CurrentCulture);
        }

        PopulateCombatants(aggregates, seconds, isWarmingUp);
        MaxDamage = Math.Max(1, Combatants.FirstOrDefault()?.Damage ?? 1);
        _renderedDataVersion = _dataVersion;
        _lastRenderedSecond = renderedSecond;
        _renderedHistory = history;
    }

    private void OnRefreshTimer()
    {
        RefreshDisplay();
        var now = DateTime.Now;
        foreach (var alert in _buffTracker.Tick(now)) _buffAlertService.Play(alert.Rule);
        foreach (var rule in BuffRules) rule.ApplyRuntime(_buffTracker.GetSnapshot(rule.Id, now));
        RefreshOverlayEntries(now);
        DotSpellTracker.Tick(now);
        ControlSpellTracker.Tick(now);
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

    private void ApplyBuffConfiguration()
    {
        var settings = BuffRules.Select(rule => rule.TryCreateSettings(out var configured, out _)
            ? configured
            : null).OfType<BuffRuleSettings>().ToArray();
        _buffTracker.Configure(settings, ResolveFadeMessages, ResolveSelfAppliedMessages,
            ResolveOtherAppliedMessages,
            suffix => _spellDataCatalog?.IsAmbiguousOtherAppliedSuffix(suffix) == true);
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

        var suggestions = _spellDataCatalog.FindSuggestions(spellName);
        var suggestionText = suggestions.Count == 0
            ? string.Empty
            : $" Did you mean {string.Join(", ", suggestions)}?";
        error = $"Spell '{spellName.Trim()}' was not found in the installed EverQuest Legends spell data.{suggestionText}";
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
        var owners = aggregates.Where(item => string.IsNullOrWhiteSpace(item.OwnerName))
            .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        var petsByOwner = aggregates.Where(item => !string.IsNullOrWhiteSpace(item.OwnerName))
            .GroupBy(item => item.OwnerName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var ownerName in petsByOwner.Keys) owners.TryAdd(ownerName, new CombatantAggregate(ownerName));

        return owners.Values.Select(owner =>
        {
            var pets = petsByOwner.GetValueOrDefault(owner.Name) ?? [];
            var combined = new CombatantAggregate(owner.Name)
            {
                Damage = owner.Damage + pets.Sum(pet => pet.Damage),
                Hits = owner.Hits + pets.Sum(pet => pet.Hits),
                MeleeHits = owner.MeleeHits + pets.Sum(pet => pet.MeleeHits),
                SpellHits = owner.SpellHits + pets.Sum(pet => pet.SpellHits),
                MeleeCriticalHits = owner.MeleeCriticalHits + pets.Sum(pet => pet.MeleeCriticalHits),
                SpellCriticalHits = owner.SpellCriticalHits + pets.Sum(pet => pet.SpellCriticalHits),
                Misses = owner.Misses + pets.Sum(pet => pet.Misses),
                SpellFizzles = owner.SpellFizzles + pets.Sum(pet => pet.SpellFizzles),
                SpellResists = owner.SpellResists + pets.Sum(pet => pet.SpellResists),
                DamageTaken = owner.DamageTaken + pets.Sum(pet => pet.DamageTaken),
                IncomingHits = owner.IncomingHits + pets.Sum(pet => pet.IncomingHits),
                IncomingMeleeHits = owner.IncomingMeleeHits + pets.Sum(pet => pet.IncomingMeleeHits),
                IncomingMisses = owner.IncomingMisses + pets.Sum(pet => pet.IncomingMisses),
                Dodges = owner.Dodges + pets.Sum(pet => pet.Dodges),
                Parries = owner.Parries + pets.Sum(pet => pet.Parries),
                Blocks = owner.Blocks + pets.Sum(pet => pet.Blocks),
                Ripostes = owner.Ripostes + pets.Sum(pet => pet.Ripostes),
                Absorbed = owner.Absorbed + pets.Sum(pet => pet.Absorbed),
                SpellAbsorbs = owner.SpellAbsorbs + pets.Sum(pet => pet.SpellAbsorbs),
                IncomingSpellResists = owner.IncomingSpellResists + pets.Sum(pet => pet.IncomingSpellResists),
                StunsLanded = owner.StunsLanded + pets.Sum(pet => pet.StunsLanded),
                StunsTaken = owner.StunsTaken + pets.Sum(pet => pet.StunsTaken),
                Healing = owner.Healing + pets.Sum(pet => pet.Healing),
                PotentialHealing = owner.PotentialHealing + pets.Sum(pet => pet.PotentialHealing),
                DirectHeals = owner.DirectHeals + pets.Sum(pet => pet.DirectHeals),
                HealOverTimeTicks = owner.HealOverTimeTicks + pets.Sum(pet => pet.HealOverTimeTicks),
                CriticalHeals = owner.CriticalHeals + pets.Sum(pet => pet.CriticalHeals)
            };

            MergeAbilities(combined.Abilities, owner.Abilities.Values);
            MergeAbilities(combined.IncomingAbilities,
                pets.Prepend(owner).SelectMany(combatant => combatant.IncomingAbilities.Values));
            MergeAbilities(combined.HealingAbilities,
                pets.Prepend(owner).SelectMany(combatant => combatant.HealingAbilities.Values));
            MergeTargets(combined.Targets, pets.Prepend(owner).SelectMany(combatant => combatant.Targets.Values));

            var petDamage = pets.Sum(pet => pet.Damage);
            if (petDamage > 0)
            {
                var petSummary = new AbilityAggregate("PET DMG")
                {
                    Damage = petDamage
                };
                foreach (var abilityGroup in pets.SelectMany(pet => pet.Abilities.Values)
                             .GroupBy(ability => ability.Name, StringComparer.OrdinalIgnoreCase))
                {
                    petSummary.Children[abilityGroup.Key] = new AbilityAggregate(abilityGroup.Key)
                    {
                        Damage = abilityGroup.Sum(item => item.Damage)
                    };
                }
                combined.Abilities[petSummary.Name] = petSummary;
            }
            return combined;
        }).ToArray();
    }

    private static void MergeAbilities(Dictionary<string, AbilityAggregate> destination,
        IEnumerable<AbilityAggregate> abilities)
    {
        foreach (var ability in abilities)
        {
            if (!destination.TryGetValue(ability.Name, out var aggregate))
            {
                aggregate = new AbilityAggregate(ability.Name);
                destination[ability.Name] = aggregate;
            }
            aggregate.Damage += ability.Damage;
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
        Combatants.Clear();
        for (var index = 0; index < aggregates.Length; index++)
        {
            var aggregate = aggregates[index];
            var abilities = CreateAbilities(aggregate.Abilities.Values, seconds);
            var incomingAbilities = CreateAbilities(aggregate.IncomingAbilities.Values, seconds);
            var healingAbilities = CreateAbilities(aggregate.HealingAbilities.Values, seconds);
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

            Combatants.Add(new CombatantViewModel
            {
                Name = aggregate.Name, OwnerName = aggregate.OwnerName,
                Damage = aggregate.Damage,
                DpsText = isWarmingUp ? "—" : (aggregate.Damage / Math.Max(1, seconds))
                    .ToString("N1", CultureInfo.CurrentCulture),
                Hits = aggregate.Hits, Misses = aggregate.Misses,
                MeleeHits = aggregate.MeleeHits, SpellHits = aggregate.SpellHits,
                MeleeCriticalHits = aggregate.MeleeCriticalHits,
                SpellCriticalHits = aggregate.SpellCriticalHits,
                SpellFizzles = aggregate.SpellFizzles, SpellResists = aggregate.SpellResists,
                DamageTaken = aggregate.DamageTaken,
                IncomingMeleeHits = aggregate.IncomingMeleeHits, IncomingMisses = aggregate.IncomingMisses,
                Dodges = aggregate.Dodges, Parries = aggregate.Parries, Blocks = aggregate.Blocks,
                Ripostes = aggregate.Ripostes, Absorbed = aggregate.Absorbed,
                SpellAbsorbs = aggregate.SpellAbsorbs,
                IncomingSpellResists = aggregate.IncomingSpellResists, Rank = index + 1,
                StunsLanded = aggregate.StunsLanded, StunsTaken = aggregate.StunsTaken,
                Healing = aggregate.Healing,
                DirectHeals = aggregate.DirectHeals, HealOverTimeTicks = aggregate.HealOverTimeTicks,
                CriticalHeals = aggregate.CriticalHeals,
                HpsText = (aggregate.Healing / Math.Max(1, seconds))
                    .ToString("N1", CultureInfo.CurrentCulture),
                Abilities = abilities, IncomingAbilities = incomingAbilities, HealingAbilities = healingAbilities,
                Mitigations = mitigations
            });
        }

        SelectedCombatant = Combatants.FirstOrDefault(item => item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                            ?? Combatants.FirstOrDefault();
    }

    private AbilityViewModel[] CreateAbilities(IEnumerable<AbilityAggregate> source, double seconds)
    {
        var abilities = source.OrderByDescending(item => item.Damage).ToArray();
        var total = Math.Max(1, abilities.Sum(item => item.Damage));
        return abilities.Select((ability, index) => new AbilityViewModel
        {
            Name = ability.Name, Damage = ability.Damage, Dps = ability.Damage / Math.Max(1, seconds),
            Share = ability.Damage * 100d / total,
            Color = ChartBrushes[index % ChartBrushes.Length],
            Icon = _spellDataCatalog?.GetAbilityIcon(ability.Name) ?? SpellIconAtlas.GenericIcon,
            IsPetSummary = ability.Children.Count > 0,
            Children = ability.Children.Count == 0
                ? []
                : CreateAbilities(ability.Children.Values, seconds)
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
        EncounterHistory.Insert(0, new EncounterHistoryViewModel
        {
            Snapshot = snapshot, Mode = mode, CharacterName = CharacterName
        });
        while (EncounterHistory.Count > HistoryLimit)
        {
            _archivedStarts.Remove(EncounterHistory[^1].StartedAt);
            EncounterHistory.RemoveAt(EncounterHistory.Count - 1);
        }
        return true;
    }

    private void RaiseBreakdownProperties()
    {
        RaisePropertyChanged(nameof(SelectedAbilities));
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
        _lifetimeCancellation.Cancel();
        Interlocked.Increment(ref _parseGeneration);
        ClearParsedLines();
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
}
