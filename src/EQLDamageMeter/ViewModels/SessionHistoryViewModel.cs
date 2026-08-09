using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class SessionHistoryViewModel : ObservableObject
{
    private SessionEntryViewModel? _selectedSession;
    private SessionMobLootRowViewModel? _selectedMob;
    private bool _suppressSelectionSideEffects;

    public ObservableCollection<SessionEntryViewModel> Sessions { get; } = [];
    public event Action? SelectionChanged;

    public SessionEntryViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value)) return;
            if (!_suppressSelectionSideEffects)
                ClearSelectedMob();
            if (_suppressSelectionSideEffects) return;
            SelectionChanged?.Invoke();
        }
    }

    public SessionMobLootRowViewModel? SelectedMob
    {
        get => _selectedMob;
        set
        {
            var previous = _selectedMob;
            if (ReferenceEquals(previous, value)) return;
            var sameMobName = previous is not null && value is not null &&
                              previous.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase);
            if (!SetProperty(ref _selectedMob, value)) return;
            // Live session upserts recreate mob rows; only cancel/reload wiki when the mob changes.
            if (!sameMobName)
                previous?.CancelWikiLoot();
            if (previous is not null) previous.IsSelected = false;
            if (_selectedMob is not null) _selectedMob.IsSelected = true;
            RaisePropertyChanged(nameof(ShowMobDetails));
            RaisePropertyChanged(nameof(ShowSessionDetails));
            RaisePropertyChanged(nameof(MobDetailsVisibility));
            RaisePropertyChanged(nameof(SessionDetailsVisibility));
            if (!sameMobName)
                _selectedMob?.EnsureWikiLootLoaded();
            else if (previous is not null && _selectedMob is not null)
                _selectedMob.AdoptWikiLootFrom(previous);
        }
    }

    public bool ShowMobDetails => SelectedMob is not null;
    public bool ShowSessionDetails => SelectedMob is null;
    public Visibility MobDetailsVisibility => ShowMobDetails ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SessionDetailsVisibility => ShowSessionDetails ? Visibility.Visible : Visibility.Collapsed;

    public void SelectMob(SessionEntryViewModel session, SessionMobLootRowViewModel mob)
    {
        if (!ReferenceEquals(SelectedSession, session))
            SelectedSession = session;
        SelectedMob = mob;
    }

    public void ClearSelectedMob() => SelectedMob = null;

    public void LoadHistory(IEnumerable<SessionRecord> records, SessionRecord? current)
    {
        var selectedId = SelectedSession?.Id;
        var selectedMobName = SelectedMob?.Name;
        var expandedIds = Sessions.Where(item => item.IsExpanded).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        _suppressSelectionSideEffects = true;
        try
        {
            Sessions.Clear();
            if (current is not null)
            {
                var live = SessionEntryViewModel.FromRecord(current, isLive: true);
                live.IsExpanded = expandedIds.Contains(live.Id);
                Sessions.Add(live);
            }

            foreach (var record in records
                         .Where(item => current is null ||
                                        !string.Equals(item.Id, current.Id, StringComparison.Ordinal))
                         .OrderByDescending(item => item.StartedAt))
            {
                var entry = SessionEntryViewModel.FromRecord(record, isLive: false);
                entry.IsExpanded = expandedIds.Contains(entry.Id);
                Sessions.Add(entry);
            }

            SelectedSession = Sessions.FirstOrDefault(item => item.Id == selectedId)
                              ?? Sessions.FirstOrDefault(item => item.IsLive)
                              ?? Sessions.FirstOrDefault();
            RestoreSelectedMob(selectedMobName);
        }
        finally
        {
            _suppressSelectionSideEffects = false;
            SelectionChanged?.Invoke();
        }
    }

    public void UpsertCurrent(SessionRecord current)
    {
        var selectedMobName = SelectedMob?.Name;
        var existing = Sessions.FirstOrDefault(item => item.IsLive);
        if (existing is null)
        {
            existing = SessionEntryViewModel.FromRecord(current, isLive: true);
            Sessions.Insert(0, existing);
            SelectedSession ??= existing;
            RestoreSelectedMob(selectedMobName);
            SelectionChanged?.Invoke();
            return;
        }

        existing.Apply(current, isLive: true);
        if (SelectedSession is null || SelectedSession.IsLive)
            SelectedSession = existing;
        RestoreSelectedMob(selectedMobName);
        SelectionChanged?.Invoke();
    }

    private void RestoreSelectedMob(string? mobName)
    {
        if (SelectedSession is null || string.IsNullOrWhiteSpace(mobName))
        {
            SelectedMob = null;
            return;
        }

        var match = SelectedSession.Mobs.FirstOrDefault(item =>
            item.Name.Equals(mobName, StringComparison.OrdinalIgnoreCase));
        if (ReferenceEquals(SelectedMob, match)) return;
        SelectedMob = match;
    }
}

public sealed class SessionEntryViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _characterText = string.Empty;
    private string _levelXpText = "0.0%";
    private string _levelsText = "0";
    private string _aaPointsText = "0";
    private string _motesText = "0";
    private string _moneyText = "0c";
    private string _deathsText = "0";
    private string _durationText = "—";
    private bool _isLive;
    private bool _isExpanded;
    private IReadOnlyList<MoteCountRow> _moteRows = [];
    private SessionLootData _loot = new();

    public string Id { get; private set; } = string.Empty;
    public SessionLootData Loot => _loot;
    public ObservableCollection<SessionMobLootRowViewModel> Mobs { get; } = [];

    public bool IsLive
    {
        get => _isLive;
        private set => SetProperty(ref _isLive, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            RaisePropertyChanged(nameof(ExpandGlyph));
            RaisePropertyChanged(nameof(LootExpandVisibility));
        }
    }

    public string ExpandGlyph => IsExpanded ? "▼" : "▶";
    public Visibility LootExpandVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string CharacterText
    {
        get => _characterText;
        private set => SetProperty(ref _characterText, value);
    }

    public string LevelXpText
    {
        get => _levelXpText;
        private set => SetProperty(ref _levelXpText, value);
    }

    public string LevelsText
    {
        get => _levelsText;
        private set => SetProperty(ref _levelsText, value);
    }

    public string AaPointsText
    {
        get => _aaPointsText;
        private set => SetProperty(ref _aaPointsText, value);
    }

    public string MotesText
    {
        get => _motesText;
        private set => SetProperty(ref _motesText, value);
    }

    public string MoneyText
    {
        get => _moneyText;
        private set => SetProperty(ref _moneyText, value);
    }

    public string DeathsText
    {
        get => _deathsText;
        private set => SetProperty(ref _deathsText, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public IReadOnlyList<MoteCountRow> MoteRows
    {
        get => _moteRows;
        private set => SetProperty(ref _moteRows, value);
    }

    public static SessionEntryViewModel FromRecord(SessionRecord record, bool isLive)
    {
        var entry = new SessionEntryViewModel();
        entry.Apply(record, isLive);
        return entry;
    }

    public void Apply(SessionRecord record, bool isLive)
    {
        Id = record.Id;
        IsLive = isLive;
        Title = isLive ? "Current session" : FormatTitle(record.StartedAt);
        Subtitle = FormatSubtitle(record, isLive);
        CharacterText = string.IsNullOrWhiteSpace(record.Character)
            ? "—"
            : $"{record.Character} · {record.Server}";
        LevelXpText = $"{record.LevelXpPercent.ToString("0.0", CultureInfo.CurrentCulture)}%";
        LevelsText = record.StartLevel is { } start && record.EndLevel is { } end
            ? $"+{record.LevelsGained}  ({start} → {end})"
            : $"+{record.LevelsGained}";
        AaPointsText = $"+{record.AaPointsGained}";
        MotesText = record.MotesLooted.ToString(CultureInfo.CurrentCulture);
        DeathsText = record.Deaths.ToString(CultureInfo.CurrentCulture);
        DurationText = FormatDuration(record, isLive);
        MoteRows = record.MotesByName
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new MoteCountRow(pair.Key, pair.Value))
            .ToArray();
        _loot = SessionLootParser.Clone(record.Loot);
        MoneyText = SessionLootParser.FormatCopper(CalculateMoneyEarned(_loot));

        // Reuse existing mob rows so an open wiki loot table is not cancelled/reloaded
        // on every live session upsert.
        var previous = Mobs.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        Mobs.Clear();
        foreach (var mob in _loot.Mobs
                     .OrderByDescending(item => item.Items.Sum(x => x.Count))
                     .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (previous.TryGetValue(mob.Name, out var existing))
            {
                existing.UpdateFrom(mob);
                Mobs.Add(existing);
            }
            else
            {
                Mobs.Add(SessionMobLootRowViewModel.From(mob));
            }
        }
    }

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    private static long CalculateMoneyEarned(SessionLootData loot)
    {
        var sold = loot.Mobs.SelectMany(mob => mob.Items)
            .Where(item => item.Disposition.Equals("Sold", StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.ValueCopper);
        return loot.CoinCopper + sold;
    }

    private static string FormatTitle(DateTime startedAt) =>
        startedAt.ToLocalTime().ToString("ddd MMM d · h:mm tt", CultureInfo.CurrentCulture);

    private static string FormatSubtitle(SessionRecord record, bool isLive)
    {
        var start = record.StartedAt.ToLocalTime();
        if (isLive || record.EndedAt is null)
            return $"Started {start.ToString("t", CultureInfo.CurrentCulture)} · {FormatDuration(record, isLive)}";

        var end = record.EndedAt.Value.ToLocalTime();
        return $"{start.ToString("t", CultureInfo.CurrentCulture)} – {end.ToString("t", CultureInfo.CurrentCulture)} · {FormatDuration(record, false)}";
    }

    private static string FormatDuration(SessionRecord record, bool isLive)
    {
        var end = isLive || record.EndedAt is null ? DateTime.Now : record.EndedAt.Value;
        var span = end - record.StartedAt;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1)
            return $"{span.Minutes}m {span.Seconds}s";
        return $"{Math.Max(1, (int)span.TotalSeconds)}s";
    }
}

public sealed class SessionMobLootRowViewModel : ObservableObject
{
    private bool _isSelected;
    private string _wikiUrl = string.Empty;
    private string _wikiLootStatus = string.Empty;
    private bool _wikiLootLoaded;
    private bool _wikiLootInFlight;
    private CancellationTokenSource? _wikiLootCts;

    private string _summary = string.Empty;
    private string _historyCoinText = "0c";
    private string _historyItemCountText = "0";
    private string _historySoldText = "0";
    private string _historyKeptStoredText = "0";
    private string _historyMergedText = "0";
    private string _corpsesText = "0";
    private SessionMobKillViewModel? _latestKill;
    private IReadOnlyList<SessionMobKillViewModel> _killHistory = [];
    private IReadOnlyList<SessionLootItemRowViewModel> _historyItems = [];

    public string Name { get; private init; } = string.Empty;
    public string WikiUrl
    {
        get => _wikiUrl;
        private set => SetProperty(ref _wikiUrl, value);
    }
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }
    public string HistoryCoinText
    {
        get => _historyCoinText;
        private set => SetProperty(ref _historyCoinText, value);
    }
    public string HistoryItemCountText
    {
        get => _historyItemCountText;
        private set => SetProperty(ref _historyItemCountText, value);
    }
    public string HistorySoldText
    {
        get => _historySoldText;
        private set => SetProperty(ref _historySoldText, value);
    }
    public string HistoryKeptStoredText
    {
        get => _historyKeptStoredText;
        private set => SetProperty(ref _historyKeptStoredText, value);
    }
    public string HistoryMergedText
    {
        get => _historyMergedText;
        private set => SetProperty(ref _historyMergedText, value);
    }
    public string CorpsesText
    {
        get => _corpsesText;
        private set => SetProperty(ref _corpsesText, value);
    }
    public SessionMobKillViewModel? LatestKill
    {
        get => _latestKill;
        private set => SetProperty(ref _latestKill, value);
    }
    public IReadOnlyList<SessionMobKillViewModel> KillHistory
    {
        get => _killHistory;
        private set => SetProperty(ref _killHistory, value);
    }
    public IReadOnlyList<SessionLootItemRowViewModel> HistoryItems
    {
        get => _historyItems;
        private set => SetProperty(ref _historyItems, value);
    }
    public ObservableCollection<WikiLootItemViewModel> WikiLootItems { get; } = [];

    public string WikiLootStatus
    {
        get => _wikiLootStatus;
        private set
        {
            if (!SetProperty(ref _wikiLootStatus, value)) return;
            RaisePropertyChanged(nameof(WikiLootStatusVisibility));
            RaisePropertyChanged(nameof(WikiLootItemsVisibility));
        }
    }

    public Visibility WikiLootStatusVisibility =>
        string.IsNullOrWhiteSpace(WikiLootStatus) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WikiLootItemsVisibility =>
        WikiLootItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void EnsureWikiLootLoaded()
    {
        if (_wikiLootLoaded || _wikiLootInFlight) return;
        _ = LoadWikiLootAsync();
    }

    public void CancelWikiLoot()
    {
        _wikiLootCts?.Cancel();
        _wikiLootCts = null;
        _wikiLootInFlight = false;
    }

    private async Task LoadWikiLootAsync()
    {
        var cts = new CancellationTokenSource();
        _wikiLootCts?.Cancel();
        _wikiLootCts = cts;
        _wikiLootInFlight = true;
        await SetWikiLootStatusAsync("Loading loot table from eqlwiki…", cts.Token);
        try
        {
            var (table, error) = await EqWikiMobLoot.FetchAsync(Name, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (table is null)
            {
                _wikiLootInFlight = false;
                await SetWikiLootStatusAsync(error ?? "No loot table found on the wiki.", cts.Token);
                return;
            }

            var dropCount = 0;
            await DispatchAsync(() =>
            {
                if (cts.IsCancellationRequested) return;
                WikiUrl = table.WikiUrl;
                WikiLootItems.Clear();
                foreach (var drop in table.Drops)
                    WikiLootItems.Add(new WikiLootItemViewModel(drop.ItemName, drop.DropChance));
                dropCount = table.Drops.Count;
                _wikiLootLoaded = true;
                _wikiLootInFlight = false;
                WikiLootStatus = dropCount == 0
                    ? $"Wiki page found ({table.ResolvedTitle}), but no known loot was listed."
                    : $"{dropCount} possible drops · {table.ResolvedTitle}";
                RaisePropertyChanged(nameof(WikiLootItemsVisibility));
            }, cts.Token);

            if (cts.IsCancellationRequested || dropCount == 0) return;
            await LoadItemStatsAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Selection changed.
        }
        catch
        {
            _wikiLootInFlight = false;
            await SetWikiLootStatusAsync("Could not load this mob's loot table.", cts.Token);
        }
    }

    private async Task LoadItemStatsAsync(CancellationToken cancellationToken)
    {
        var items = WikiLootItems.Where(item => string.IsNullOrWhiteSpace(item.Stats)).ToArray();
        if (items.Length == 0) return;
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var (stats, error) = await EqWikiItemStats.FetchStatsAsync(item.Name, cancellationToken);
                await DispatchAsync(() => item.ApplyStats(stats, error), cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task SetWikiLootStatusAsync(string status, CancellationToken cancellationToken) =>
        await DispatchAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
                WikiLootStatus = status;
        }, cancellationToken);

    private static Task DispatchAsync(Action action, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background, cancellationToken).Task;
    }

    public static SessionMobLootRowViewModel From(SessionMobLoot mob)
    {
        var row = new SessionMobLootRowViewModel { Name = mob.Name };
        row.WikiUrl = EqWikiLinks.ForMob(mob.Name);
        row.UpdateFrom(mob);
        return row;
    }

    public void UpdateFrom(SessionMobLoot mob)
    {
        var historyItems = ToItemRows(mob.Items);
        var historyItemCount = historyItems.Sum(item => item.Count);
        var kills = mob.Kills
            .OrderBy(kill => kill.Timestamp)
            .Select(SessionMobKillViewModel.From)
            .ToArray();
        if (kills.Length == 0 && (mob.Items.Count > 0 || mob.CoinCopper > 0))
        {
            // Older session_info.json without per-kill data: show aggregate as a single kill.
            kills =
            [
                new SessionMobKillViewModel
                {
                    TimeText = "Combined kills",
                    Summary =
                        $"{SessionLootParser.FormatCopper(mob.CoinCopper)} · {historyItemCount} items",
                    CoinText = SessionLootParser.FormatCopper(mob.CoinCopper),
                    ItemCountText = historyItemCount.ToString(CultureInfo.CurrentCulture),
                    SoldText = CountDisposition(historyItems, "Sold").ToString(CultureInfo.CurrentCulture),
                    KeptStoredText = (CountDisposition(historyItems, "Kept") + CountDisposition(historyItems, "Stored"))
                        .ToString(CultureInfo.CurrentCulture),
                    MergedText = CountDisposition(historyItems, "Merged").ToString(CultureInfo.CurrentCulture),
                    Items = historyItems
                }
            ];
        }

        Summary =
            $"{mob.CorpsesLooted} corpses · {SessionLootParser.FormatCopper(mob.CoinCopper)} · {historyItemCount} items";
        HistoryCoinText = SessionLootParser.FormatCopper(mob.CoinCopper);
        HistoryItemCountText = historyItemCount.ToString(CultureInfo.CurrentCulture);
        HistorySoldText = CountDisposition(historyItems, "Sold").ToString(CultureInfo.CurrentCulture);
        HistoryKeptStoredText = (CountDisposition(historyItems, "Kept") + CountDisposition(historyItems, "Stored"))
            .ToString(CultureInfo.CurrentCulture);
        HistoryMergedText = CountDisposition(historyItems, "Merged").ToString(CultureInfo.CurrentCulture);
        CorpsesText = mob.CorpsesLooted.ToString(CultureInfo.CurrentCulture);
        LatestKill = kills.Length > 0 ? kills[^1] : null;
        KillHistory = kills.Length > 0 ? kills.Reverse().ToArray() : [];
        HistoryItems = historyItems;
    }

    /// <summary>
    /// Moves wiki loot UI state onto a replacement row for the same mob (e.g. full history reload).
    /// Live session upserts reuse the same instance and never need this.
    /// </summary>
    public void AdoptWikiLootFrom(SessionMobLootRowViewModel previous)
    {
        if (ReferenceEquals(this, previous)) return;
        previous.CancelWikiLoot();
        WikiUrl = previous.WikiUrl;
        WikiLootStatus = previous.WikiLootStatus;
        WikiLootItems.Clear();
        foreach (var item in previous.WikiLootItems)
            WikiLootItems.Add(item);
        _wikiLootLoaded = previous._wikiLootLoaded || WikiLootItems.Count > 0;
        _wikiLootInFlight = false;
        RaisePropertyChanged(nameof(WikiLootItemsVisibility));
        if (!_wikiLootLoaded)
        {
            EnsureWikiLootLoaded();
            return;
        }

        if (WikiLootItems.Any(item => string.IsNullOrWhiteSpace(item.Stats)))
            _ = ResumeItemStatsAsync();
    }

    private async Task ResumeItemStatsAsync()
    {
        var cts = new CancellationTokenSource();
        _wikiLootCts?.Cancel();
        _wikiLootCts = cts;
        _wikiLootInFlight = true;
        try
        {
            await LoadItemStatsAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                _wikiLootInFlight = false;
        }
    }

    public void OpenWiki()
    {
        if (string.IsNullOrWhiteSpace(WikiUrl)) return;
        Process.Start(new ProcessStartInfo(WikiUrl) { UseShellExecute = true });
    }

    private static IReadOnlyList<SessionLootItemRowViewModel> ToItemRows(IEnumerable<SessionLootItem> items) =>
        items.OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(SessionLootItemRowViewModel.From)
            .ToArray();

    private static int CountDisposition(IEnumerable<SessionLootItemRowViewModel> items, string disposition) =>
        items.Where(item => item.Disposition.Equals(disposition, StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Count);
}

public sealed class SessionMobKillViewModel
{
    public string TimeText { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string CoinText { get; init; } = "0c";
    public string ItemCountText { get; init; } = "0";
    public string SoldText { get; init; } = "0";
    public string KeptStoredText { get; init; } = "0";
    public string MergedText { get; init; } = "0";
    public IReadOnlyList<SessionLootItemRowViewModel> Items { get; init; } = [];

    public static SessionMobKillViewModel From(SessionMobKill kill)
    {
        var items = kill.Items
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(SessionLootItemRowViewModel.From)
            .ToArray();
        var itemCount = items.Sum(item => item.Count);
        var sold = items.Where(item => item.Disposition.Equals("Sold", StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Count);
        var keptStored = items.Where(item =>
                item.Disposition.Equals("Kept", StringComparison.OrdinalIgnoreCase) ||
                item.Disposition.Equals("Stored", StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Count);
        var merged = items.Where(item => item.Disposition.Equals("Merged", StringComparison.OrdinalIgnoreCase))
            .Sum(item => item.Count);
        return new SessionMobKillViewModel
        {
            TimeText = kill.Timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
            Summary = $"{SessionLootParser.FormatCopper(kill.CoinCopper)} · {itemCount} items",
            CoinText = SessionLootParser.FormatCopper(kill.CoinCopper),
            ItemCountText = itemCount.ToString(CultureInfo.CurrentCulture),
            SoldText = sold.ToString(CultureInfo.CurrentCulture),
            KeptStoredText = keptStored.ToString(CultureInfo.CurrentCulture),
            MergedText = merged.ToString(CultureInfo.CurrentCulture),
            Items = items
        };
    }
}

public sealed class SessionLootItemRowViewModel
{
    public string Name { get; private init; } = string.Empty;
    public string Disposition { get; private init; } = string.Empty;
    public string ValueText { get; private init; } = string.Empty;
    public int Count { get; private init; }
    public string CountText => Count.ToString(CultureInfo.CurrentCulture);

    public static SessionLootItemRowViewModel From(SessionLootItem item) => new()
    {
        Name = item.Name,
        Disposition = item.Disposition,
        Count = item.Count,
        ValueText = item.Disposition switch
        {
            "Sold" => SessionLootParser.FormatCopper(item.ValueCopper),
            "Stored" => item.Note ?? "currency",
            "Merged" => item.Note is null ? "merged" : $"→ {item.Note}",
            _ => "—"
        }
    };
}

public sealed class WikiLootItemViewModel : ObservableObject
{
    private string _stats = string.Empty;
    private string _statsStatus = "Loading item info…";

    public WikiLootItemViewModel(string name, string dropChance)
    {
        Name = name;
        DropChanceText = string.IsNullOrWhiteSpace(dropChance) ? "—" : dropChance;
    }

    public string Name { get; }
    public string DropChanceText { get; }

    public string Stats
    {
        get => _stats;
        private set
        {
            if (!SetProperty(ref _stats, value)) return;
            RaisePropertyChanged(nameof(StatsVisibility));
        }
    }

    public string StatsStatus
    {
        get => _statsStatus;
        private set
        {
            if (!SetProperty(ref _statsStatus, value)) return;
            RaisePropertyChanged(nameof(StatsStatusVisibility));
        }
    }

    public Visibility StatsVisibility =>
        string.IsNullOrWhiteSpace(Stats) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StatsStatusVisibility =>
        string.IsNullOrWhiteSpace(StatsStatus) ? Visibility.Collapsed : Visibility.Visible;

    public void ApplyStats(string stats, string? error)
    {
        if (!string.IsNullOrWhiteSpace(stats))
        {
            Stats = stats;
            StatsStatus = string.Empty;
            return;
        }

        Stats = string.Empty;
        StatsStatus = string.IsNullOrWhiteSpace(error) ? "No item stats on wiki." : error;
    }
}

public sealed record MoteCountRow(string Name, int Count);
