using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class SkyTrackerViewModel : ObservableObject
{
    private readonly EqWikiSkyCatalog _catalog = new();
    private readonly BuffAlertService _alerts = new();
    private readonly Dictionary<string, DateTime> _recentAlerts = new(StringComparer.OrdinalIgnoreCase);
    private string _statusText = "Load Plane of Sky rewards from the wiki to begin";
    private string _catalogSummary = "Catalog not loaded";
    private string _previewStats = string.Empty;
    private string _previewQuestSummary = string.Empty;
    private bool _isBusy;
    private string? _selectedClass;
    private SkyRewardCatalog? _selectedReward;
    private SkyTrackedGoalViewModel? _selectedGoal;

    public ObservableCollection<string> ClassNames { get; } = [];
    public ObservableCollection<SkyRewardCatalog> AvailableRewards { get; } = [];
    public ObservableCollection<SkyPreviewPartViewModel> PreviewParts { get; } = [];
    public ObservableCollection<SkyTrackedGoalViewModel> Goals { get; } = [];
    public IReadOnlyList<BuffSoundKind> SoundChoices { get; } = Enum.GetValues<BuffSoundKind>();
    public IReadOnlyList<BuffAlertMode> AlertModeChoices { get; } = BuffAlertModeOptions.ExclusiveChoices;
    public IReadOnlyList<SkyItemLocation> LocationChoices { get; } = Enum.GetValues<SkyItemLocation>();

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
            RefreshAvailableRewards();
        }
    }

    public SkyRewardCatalog? SelectedReward
    {
        get => _selectedReward;
        set
        {
            if (!SetProperty(ref _selectedReward, value)) return;
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
            ApplyCatalogToUi();
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
                    part.AlertMode,
                    part.Sound,
                    part.VoiceText,
                    lastDropText: string.Empty,
                    PersistAsync));
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
                part.AlertMode,
                part.Sound,
                part.VoiceText,
                lastDropText: string.Empty,
                PersistAsync));
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

    public void MarkPartInBank(SkyTrackedPartViewModel part)
    {
        if (part is null) return;
        part.Location = SkyItemLocation.Bank;
        if (part.FoundCount < part.NeededCount)
            part.FoundCount = part.NeededCount;
        part.LastDropText = "Marked in bank";
        SelectedGoal?.RefreshProgress();
        StatusText = $"{part.ItemName} marked in bank";
        _ = PersistAsync();
    }

    public void OpenSelectedRewardWiki()
    {
        var name = SelectedGoal?.RewardName ?? SelectedReward?.RewardName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var url = EqWikiLinks.BaseUrl + name.Replace(' ', '_');
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void OpenSkyWiki()
    {
        Process.Start(new ProcessStartInfo(EqWikiLinks.BaseUrl + "Plane_of_Sky") { UseShellExecute = true });
    }

    public void ObserveLootMessage(string message)
    {
        if (!LooksLikeLootMessage(message)) return;
        if (!SessionLootParser.TryReadLootedItemName(message, out var itemName)) return;
        var disposition = ResolveDisposition(message);
        var now = DateTime.UtcNow;
        var matched = false;

        foreach (var goal in Goals)
        {
            foreach (var part in goal.Parts)
            {
                if (!part.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase)) continue;
                matched = true;
                var location = disposition switch
                {
                    "Stored" => SkyItemLocation.Currency,
                    "Kept" => part.Location == SkyItemLocation.Bank
                        ? SkyItemLocation.Bank
                        : SkyItemLocation.Inventory,
                    _ => part.Location == SkyItemLocation.Unknown
                        ? SkyItemLocation.Inventory
                        : part.Location
                };
                var dropText =
                    $"{disposition} {now.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}";
                part.ApplyLootProgress(incrementFound: part.FoundCount < part.NeededCount, location, dropText);
                goal.RefreshProgress();

                if (!part.AlertEnabled) continue;
                var key = $"{goal.Id}:{part.ItemName}";
                if (_recentAlerts.TryGetValue(key, out var previous) &&
                    now - previous < TimeSpan.FromSeconds(2))
                    continue;
                _recentAlerts[key] = now;
                _alerts.PlayLootAlert($"{part.ItemName} ({goal.RewardName})", part.Sound, part.AlertMode,
                    part.VoiceText);
            }
        }

        if (!matched) return;
        StatusText = $"Sky loot: {itemName}";
        _ = PersistAsync();
    }

    private static bool LooksLikeLootMessage(string message) =>
        message.Contains("looted", StringComparison.OrdinalIgnoreCase);

    private async Task LoadPreviewAsync(SkyRewardCatalog? reward)
    {
        PreviewParts.Clear();
        PreviewStats = string.Empty;
        PreviewQuestSummary = string.Empty;
        RaisePropertyChanged(nameof(PreviewVisibility));
        RaisePropertyChanged(nameof(EmptyPreviewVisibility));
        if (reward is null) return;

        var cls = _catalog.FindClass(SelectedClass);
        PreviewQuestSummary =
            $"{reward.QuestName}" +
            (string.IsNullOrWhiteSpace(reward.TriggerPhrase)
                ? string.Empty
                : $" · say \"{reward.TriggerPhrase}\"") +
            (string.IsNullOrWhiteSpace(cls?.QuestGiver)
                ? string.Empty
                : $" · {cls!.QuestGiver}");

        foreach (var item in reward.RequiredItems)
        {
            PreviewParts.Add(new SkyPreviewPartViewModel(item.ItemName, item.Note, item.NeededCount)
            {
                IsSelected = true,
                AlertMode = BuffAlertMode.Sound,
                Sound = BuffSoundKind.Chime,
                VoiceText = string.Empty
            });
        }

        RaisePropertyChanged(nameof(PreviewVisibility));
        RaisePropertyChanged(nameof(EmptyPreviewVisibility));

        StatusText = $"Loading stats for {reward.RewardName}…";
        var (stats, error) = await EqWikiItemStats.FetchStatsAsync(reward.RewardName);
        if (!ReferenceEquals(SelectedReward, reward)) return;
        PreviewStats = string.IsNullOrWhiteSpace(stats)
            ? error ?? "Stats unavailable"
            : stats;
        StatusText = string.IsNullOrWhiteSpace(error)
            ? $"{reward.RewardName} · {PreviewParts.Count} required items"
            : $"{reward.RewardName} · {error}";
    }

    private void RefreshAvailableRewards()
    {
        AvailableRewards.Clear();
        SelectedReward = null;
        foreach (var reward in _catalog.GetRewardsForClass(SelectedClass))
            AvailableRewards.Add(reward);
    }

    private void ApplyCatalogToUi()
    {
        var previousClass = SelectedClass;
        var previousReward = SelectedReward?.RewardName;
        ClassNames.Clear();
        foreach (var name in _catalog.GetClassNames())
            ClassNames.Add(name);

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
            Goals = Goals.Select(goal => goal.ToModel()).ToList()
        };
        await SkyTrackerStore.TrySaveAsync(document);
    }

    private static string ResolveDisposition(string message)
    {
        if (message.Contains(" and stored it ", StringComparison.OrdinalIgnoreCase)) return "Stored";
        if (message.Contains(" and sold it ", StringComparison.OrdinalIgnoreCase)) return "Sold";
        if (message.Contains(" and merged it ", StringComparison.OrdinalIgnoreCase)) return "Merged";
        return "Kept";
    }
}

public sealed class SkyPreviewPartViewModel : ObservableObject
{
    private bool _isSelected = true;
    private BuffAlertMode _alertMode = BuffAlertMode.Sound;
    private BuffSoundKind _sound = BuffSoundKind.Chime;
    private string _voiceText = string.Empty;

    public SkyPreviewPartViewModel(string itemName, string note, int neededCount)
    {
        ItemName = itemName;
        Note = note;
        NeededCount = neededCount < 1 ? 1 : neededCount;
    }

    public string ItemName { get; }
    public string Note { get; }
    public int NeededCount { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Note) ? ItemName : $"{ItemName} ({Note})";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
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
        }
    }

    public BuffSoundKind Sound
    {
        get => _sound;
        set => SetProperty(ref _sound, value);
    }

    public string VoiceText
    {
        get => _voiceText;
        set => SetProperty(ref _voiceText, value);
    }

    public Visibility SoundPickerVisibility =>
        AlertMode == BuffAlertMode.Sound ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VoiceTextVisibility =>
        AlertMode == BuffAlertMode.TextToSpeech ? Visibility.Visible : Visibility.Collapsed;
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
            vm.Parts.Add(SkyTrackedPartViewModel.From(part, persist));
        vm.RefreshProgress();
        return vm;
    }
}

public sealed class SkyTrackedPartViewModel : ObservableObject
{
    private readonly Func<Task> _persist;
    private int _foundCount;
    private SkyItemLocation _location;
    private bool _alertEnabled;
    private BuffAlertMode _alertMode;
    private BuffSoundKind _sound;
    private string _voiceText;
    private string _lastDropText;

    public SkyTrackedPartViewModel(string itemName, string note, int neededCount, int foundCount,
        SkyItemLocation location, bool alertEnabled, BuffAlertMode alertMode, BuffSoundKind sound,
        string voiceText, string lastDropText, Func<Task> persist)
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
    }

    public string ItemName { get; }
    public string Note { get; }
    public int NeededCount { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Note) ? ItemName : $"{ItemName} ({Note})";
    public string ProgressText => $"{FoundCount}/{NeededCount}";

    public int FoundCount
    {
        get => _foundCount;
        set
        {
            var clamped = Math.Max(0, value);
            if (!SetProperty(ref _foundCount, clamped)) return;
            RaisePropertyChanged(nameof(ProgressText));
            _ = _persist();
        }
    }

    public SkyItemLocation Location
    {
        get => _location;
        set
        {
            if (!SetProperty(ref _location, value)) return;
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
        }

        if (_location != location)
        {
            _location = location;
            RaisePropertyChanged(nameof(Location));
        }

        LastDropText = lastDropText;
    }

    public bool AlertEnabled
    {
        get => _alertEnabled;
        set
        {
            if (!SetProperty(ref _alertEnabled, value)) return;
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

    public static SkyTrackedPartViewModel From(SkyTrackedPart model, Func<Task> persist) =>
        new(model.ItemName, model.Note, model.NeededCount, model.FoundCount, model.Location,
            model.AlertEnabled, model.AlertMode, model.Sound, model.VoiceText, model.LastDropText,
            persist);
}
