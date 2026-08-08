using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class QuestTrackerViewModel : ObservableObject
{
    private readonly EqWikiQuestCatalog _catalog = new();
    private readonly BuffAlertService _alerts = new();
    private readonly Dictionary<string, DateTime> _recentAlerts = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = string.Empty;
    private string _statusText = "Load quest list from the wiki to begin";
    private string _catalogSummary = "Catalog not loaded";
    private bool _isBusy;
    private QuestDetailsViewModel? _selectedQuest;

    public ObservableCollection<QuestItemRowViewModel> SuggestedItems { get; } = [];
    public ObservableCollection<TrackedQuestItemViewModel> TrackedItems { get; } = [];
    public ObservableCollection<string> ChecklistLines { get; } = [];
    public IReadOnlyList<BuffSoundKind> SoundChoices { get; } = Enum.GetValues<BuffSoundKind>();
    public IReadOnlyList<BuffAlertMode> AlertModeChoices { get; } = BuffAlertModeOptions.ExclusiveChoices;

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

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

    public QuestDetailsViewModel? SelectedQuest
    {
        get => _selectedQuest;
        private set
        {
            if (!SetProperty(ref _selectedQuest, value)) return;
            RaisePropertyChanged(nameof(HasSelectedQuest));
            RaisePropertyChanged(nameof(QuestDetailsVisibility));
            RaisePropertyChanged(nameof(EmptyDetailsVisibility));
        }
    }

    public bool HasSelectedQuest => SelectedQuest is not null;
    public Visibility QuestDetailsVisibility => HasSelectedQuest ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyDetailsVisibility => HasSelectedQuest ? Visibility.Collapsed : Visibility.Visible;

    public void Initialize()
    {
        _catalog.LoadCached();
        UpdateCatalogSummary();
        var document = QuestTrackerStore.TryLoad();
        TrackedItems.Clear();
        foreach (var item in document.TrackedItems
                     .OrderBy(entry => entry.ItemName, StringComparer.OrdinalIgnoreCase))
        {
            TrackedItems.Add(TrackedQuestItemViewModel.From(item, PersistAsync));
        }

        if (_catalog.IsLoaded)
        {
            StatusText = $"Ready · {_catalog.Titles.Count} quests cached";
            return;
        }

        StatusText = "Downloading quest list from eqlwiki.com…";
        _ = RefreshCatalogAsync();
    }

    public IReadOnlyList<string> FindMatches(string query) => _catalog.FindMatches(query);

    public async Task RefreshCatalogAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Downloading quest list from eqlwiki.com…";
        try
        {
            var (ok, error) = await _catalog.RefreshAsync();
            UpdateCatalogSummary();
            StatusText = ok
                ? $"Loaded {_catalog.Titles.Count} quests from the wiki"
                : error ?? "Quest list refresh failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectSuggestionAsync(string title)
    {
        SearchText = title;
        await LoadQuestAsync(title);
    }

    public async Task LoadSelectedSearchAsync()
    {
        if (_catalog.TryResolveTitle(SearchText, out var title))
        {
            await LoadQuestAsync(title);
            return;
        }

        var matches = _catalog.FindMatches(SearchText, 1);
        if (matches.Count == 1)
        {
            await LoadQuestAsync(matches[0]);
            return;
        }

        StatusText = _catalog.IsLoaded
            ? "Pick a quest from the suggestions list."
            : "Refresh the quest list first.";
    }

    public void TrackSuggestedItem(QuestItemRowViewModel item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Name)) return;
        if (TrackedItems.Any(existing =>
                existing.ItemName.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"Already tracking {item.Name}";
            return;
        }

        var row = new TrackedQuestItemViewModel(
            item.Name,
            SelectedQuest?.Title ?? string.Empty,
            enabled: true,
            BuffAlertMode.Sound,
            BuffSoundKind.Chime,
            voiceText: string.Empty,
            PersistAsync);
        TrackedItems.Insert(0, row);
        item.IsTracked = true;
        StatusText = $"Tracking {item.Name}";
        _ = PersistAsync();
    }

    public void UntrackItem(TrackedQuestItemViewModel item)
    {
        TrackedItems.Remove(item);
        var suggested = SuggestedItems.FirstOrDefault(entry =>
            entry.Name.Equals(item.ItemName, StringComparison.OrdinalIgnoreCase));
        if (suggested is not null) suggested.IsTracked = false;
        StatusText = $"Stopped tracking {item.ItemName}";
        _ = PersistAsync();
    }

    public void OpenSelectedQuestWiki()
    {
        if (SelectedQuest is null || string.IsNullOrWhiteSpace(SelectedQuest.WikiUrl)) return;
        Process.Start(new ProcessStartInfo(SelectedQuest.WikiUrl) { UseShellExecute = true });
    }

    public void ObserveLootMessage(string message)
    {
        if (!message.Contains("looted", StringComparison.OrdinalIgnoreCase)) return;
        if (!SessionLootParser.TryReadLootedItemName(message, out var itemName)) return;
        var match = TrackedItems.FirstOrDefault(item =>
            item.Enabled && item.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;

        var now = DateTime.UtcNow;
        if (_recentAlerts.TryGetValue(match.ItemName, out var last) &&
            now - last < TimeSpan.FromSeconds(2))
            return;
        _recentAlerts[match.ItemName] = now;

        match.LastDropText = $"Looted {now.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}";
        _alerts.PlayLootAlert(match.ItemName, match.Sound, match.AlertMode, match.VoiceText);
        StatusText = $"Tracked drop: {match.ItemName}";
    }

    private async Task LoadQuestAsync(string title)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = $"Loading {title}…";
        try
        {
            var (details, error) = await EqWikiQuestParser.FetchAsync(title);
            if (details is null)
            {
                StatusText = error ?? "Quest details could not be loaded.";
                return;
            }

            SelectedQuest = QuestDetailsViewModel.From(details);
            ChecklistLines.Clear();
            foreach (var line in details.ChecklistLines)
                ChecklistLines.Add(line);

            SuggestedItems.Clear();
            foreach (var itemName in details.SuggestedItems)
            {
                SuggestedItems.Add(new QuestItemRowViewModel(itemName)
                {
                    IsTracked = TrackedItems.Any(tracked =>
                        tracked.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                });
            }

            StatusText = SuggestedItems.Count > 0
                ? $"{details.Title} · {SuggestedItems.Count} suggested items"
                : $"{details.Title} · no clear item list found (open wiki for full walkthrough)";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateCatalogSummary()
    {
        if (!_catalog.IsLoaded)
        {
            CatalogSummary = "Catalog not loaded";
            return;
        }

        var when = _catalog.FetchedAtUtc?.ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture) ?? "unknown";
        CatalogSummary = $"{_catalog.Titles.Count} quests · cached {when}";
    }

    private Task PersistAsync()
    {
        var document = new QuestTrackerDocument
        {
            TrackedItems = TrackedItems.Select(item => item.ToModel()).ToList()
        };
        return QuestTrackerStore.TrySaveAsync(document);
    }
}

public sealed class QuestDetailsViewModel
{
    public string Title { get; init; } = string.Empty;
    public string WikiUrl { get; init; } = string.Empty;
    public string StartZone { get; init; } = "—";
    public string QuestGiver { get; init; } = "—";
    public string RecommendedLevel { get; init; } = "—";
    public string Classes { get; init; } = "—";
    public string RelatedZones { get; init; } = "—";
    public string RelatedNpcs { get; init; } = "—";

    public static QuestDetailsViewModel From(QuestDetails details) => new()
    {
        Title = details.Title,
        WikiUrl = details.WikiUrl,
        StartZone = details.StartZone,
        QuestGiver = details.QuestGiver,
        RecommendedLevel = details.RecommendedLevel,
        Classes = details.Classes,
        RelatedZones = details.RelatedZones,
        RelatedNpcs = details.RelatedNpcs
    };
}

public sealed class QuestItemRowViewModel : ObservableObject
{
    private bool _isTracked;

    public QuestItemRowViewModel(string name) => Name = name;

    public string Name { get; }

    public bool IsTracked
    {
        get => _isTracked;
        set
        {
            if (!SetProperty(ref _isTracked, value)) return;
            RaisePropertyChanged(nameof(TrackButtonText));
        }
    }

    public string TrackButtonText => IsTracked ? "Tracking" : "Track";
}

public sealed class TrackedQuestItemViewModel : ObservableObject
{
    private readonly Func<Task> _persist;
    private bool _enabled;
    private BuffAlertMode _alertMode;
    private BuffSoundKind _sound;
    private string _voiceText;
    private string _lastDropText = "Waiting for drop";

    public TrackedQuestItemViewModel(string itemName, string questTitle, bool enabled, BuffAlertMode alertMode,
        BuffSoundKind sound, string voiceText, Func<Task> persist)
    {
        ItemName = itemName;
        QuestTitle = string.IsNullOrWhiteSpace(questTitle) ? "Custom" : questTitle;
        _enabled = enabled;
        _alertMode = BuffAlertModeOptions.Normalize(alertMode);
        _sound = sound;
        _voiceText = voiceText ?? string.Empty;
        _persist = persist;
    }

    public string ItemName { get; }
    public string QuestTitle { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
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

    public Visibility SoundPickerVisibility =>
        AlertMode == BuffAlertMode.Sound ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VoiceTextVisibility =>
        AlertMode == BuffAlertMode.TextToSpeech ? Visibility.Visible : Visibility.Collapsed;

    public string LastDropText
    {
        get => _lastDropText;
        set => SetProperty(ref _lastDropText, value);
    }

    public static TrackedQuestItemViewModel From(TrackedQuestItem model, Func<Task> persist) =>
        new(model.ItemName, model.QuestTitle, model.Enabled, model.AlertMode, model.Sound,
            model.VoiceText, persist);

    public TrackedQuestItem ToModel() => new()
    {
        ItemName = ItemName,
        QuestTitle = QuestTitle,
        Enabled = Enabled,
        AlertMode = BuffAlertModeOptions.Normalize(AlertMode),
        Sound = Sound,
        VoiceText = VoiceText?.Trim() ?? string.Empty
    };
}
