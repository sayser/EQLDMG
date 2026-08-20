using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class ItemsViewModel : ObservableObject
{
    private string _searchText = string.Empty;
    private string _statusText = "Search eqlwiki.com for an item or spell, then pick an upgrade tier (+0…+10).";
    private string _selectedTitle = string.Empty;
    private string _baseStats = string.Empty;
    private string _displayStats = string.Empty;
    private string _usesEmptyText = string.Empty;
    private string _upgradeSummary = EqWikiItemUpgrade.BonusSummary(0);
    private EqWikiItemStats.PageKind _pageKind = EqWikiItemStats.PageKind.Item;
    private EqWikiSpellPage.SpellInfo? _spell;
    private int _upgradeTier;
    private bool _isBusy;
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<string> SearchResults { get; } = [];
    public ObservableCollection<WikiUseLink> QuestUses { get; } = [];
    public ObservableCollection<WikiUseLink> RecipeUses { get; } = [];

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

    public string SelectedTitle
    {
        get => _selectedTitle;
        private set
        {
            if (!SetProperty(ref _selectedTitle, value)) return;
            RaisePropertyChanged(nameof(HasSelection));
            RaisePropertyChanged(nameof(WikiUrl));
        }
    }

    public string BaseStats
    {
        get => _baseStats;
        private set => SetProperty(ref _baseStats, value);
    }

    public string DisplayStats
    {
        get => _displayStats;
        private set => SetProperty(ref _displayStats, value);
    }

    public string UsesEmptyText
    {
        get => _usesEmptyText;
        private set
        {
            if (!SetProperty(ref _usesEmptyText, value)) return;
            RaisePropertyChanged(nameof(HasUseLinks));
            RaisePropertyChanged(nameof(UsesEmptyVisibility));
            RaisePropertyChanged(nameof(QuestUsesVisibility));
            RaisePropertyChanged(nameof(RecipeUsesVisibility));
        }
    }

    public string UpgradeSummary
    {
        get => _upgradeSummary;
        private set => SetProperty(ref _upgradeSummary, value);
    }

    public int UpgradeTier
    {
        get => _upgradeTier;
        set
        {
            var tier = Math.Clamp(value, 0, 10);
            if (!SetProperty(ref _upgradeTier, tier)) return;
            RefreshUpgradeSummary();
            RaisePropertyChanged(nameof(SelectedTierChoice));
            RefreshDisplayStats();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedTitle);
    public string DetailsHeader => _pageKind == EqWikiItemStats.PageKind.Spell ? "SPELL DETAILS" : "ITEM DETAILS";
    public string StatsHeader => _pageKind == EqWikiItemStats.PageKind.Spell ? "SPELL" : "STATS";
    public bool HasUseLinks => QuestUses.Count > 0 || RecipeUses.Count > 0;
    public Visibility UsesEmptyVisibility => HasUseLinks ? Visibility.Collapsed : Visibility.Visible;
    public Visibility QuestUsesVisibility => QuestUses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecipeUsesVisibility => RecipeUses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string WikiUrl => string.IsNullOrWhiteSpace(SelectedTitle)
        ? EqWikiLinks.BaseUrl
        : EqWikiLinks.ForPage(SelectedTitle);

    public IReadOnlyList<TierChoice> TierChoices { get; } =
        Enumerable.Range(0, 11)
            .Select(t => new TierChoice(t, EqWikiItemUpgrade.TierLabel(t)))
            .ToArray();

    public TierChoice? SelectedTierChoice
    {
        get => TierChoices.FirstOrDefault(t => t.Tier == UpgradeTier);
        set
        {
            if (value is null) return;
            UpgradeTier = value.Tier;
            RaisePropertyChanged();
        }
    }

    public async Task SearchAsync()
    {
        IsBusy = true;
        StatusText = "Searching wiki…";
        SearchResults.Clear();
        try
        {
            var (titles, error) = await EqWikiItemSearch.SearchAsync(SearchText);
            foreach (var title in titles)
                SearchResults.Add(title);
            StatusText = error ?? $"{titles.Count} result(s). Select an item or spell to load stats.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectResultAsync(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        SelectedTitle = title.Trim();
        BaseStats = string.Empty;
        DisplayStats = string.Empty;
        _spell = null;
        _pageKind = EqWikiItemStats.PageKind.Item;
        RaiseKindChanged();
        RefreshUpgradeSummary();
        ClearUses();
        IsBusy = true;
        StatusText = $"Loading {SelectedTitle}…";
        try
        {
            var statsTask = EqWikiItemStats.FetchAsync(SelectedTitle, token);
            var usesTask = EqWikiItemUses.FetchUsesAsync(SelectedTitle, token);
            await Task.WhenAll(statsTask, usesTask);
            token.ThrowIfCancellationRequested();

            var result = await statsTask;
            var (uses, usesError) = await usesTask;
            if (!string.IsNullOrWhiteSpace(result.Error) &&
                string.IsNullOrWhiteSpace(result.ItemStats) && result.Spell is null)
            {
                StatusText = result.Error;
                return;
            }

            _pageKind = result.Kind;
            _spell = result.Spell;
            BaseStats = result.ItemStats;
            RaiseKindChanged();
            RefreshUpgradeSummary();
            RefreshDisplayStats();
            ApplyUses(uses, usesError);
            StatusText = string.IsNullOrWhiteSpace(result.Error)
                ? $"Loaded {SelectedTitle}."
                : result.Error;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer selection
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenWiki()
    {
        if (!HasSelection) return;
        OpenUrl(WikiUrl);
    }

    public void OpenUseLink(WikiUseLink? link)
    {
        if (link is null || string.IsNullOrWhiteSpace(link.Url)) return;
        OpenUrl(link.Url);
    }

    private void ApplyUses(EqWikiItemUses.ItemUseInfo uses, string? usesError)
    {
        ClearUses();
        foreach (var quest in uses.Quests)
            QuestUses.Add(new WikiUseLink(quest, EqWikiLinks.ForPage(quest)));
        foreach (var recipe in uses.Recipes)
            RecipeUses.Add(new WikiUseLink(recipe, EqWikiLinks.ForPage(recipe)));

        UsesEmptyText = HasUseLinks
            ? string.Empty
            : (usesError ?? "No known quest/recipe uses on wiki.");
        RaisePropertyChanged(nameof(HasUseLinks));
        RaisePropertyChanged(nameof(UsesEmptyVisibility));
        RaisePropertyChanged(nameof(QuestUsesVisibility));
        RaisePropertyChanged(nameof(RecipeUsesVisibility));
    }

    private void ClearUses()
    {
        QuestUses.Clear();
        RecipeUses.Clear();
        UsesEmptyText = string.Empty;
        RaisePropertyChanged(nameof(HasUseLinks));
        RaisePropertyChanged(nameof(UsesEmptyVisibility));
        RaisePropertyChanged(nameof(QuestUsesVisibility));
        RaisePropertyChanged(nameof(RecipeUsesVisibility));
    }

    private void RefreshDisplayStats()
    {
        if (_spell is not null)
        {
            DisplayStats = EqWikiSpellPage.Format(_spell, UpgradeTier);
            return;
        }

        DisplayStats = string.IsNullOrWhiteSpace(BaseStats)
            ? string.Empty
            : EqWikiItemUpgrade.ApplyTier(BaseStats, UpgradeTier);
    }

    private void RefreshUpgradeSummary()
    {
        UpgradeSummary = _spell is not null
            ? EqWikiSpellPage.BonusSummary(UpgradeTier, _spell.Family)
            : EqWikiItemUpgrade.BonusSummary(UpgradeTier);
    }

    private void RaiseKindChanged()
    {
        RaisePropertyChanged(nameof(DetailsHeader));
        RaisePropertyChanged(nameof(StatsHeader));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    public sealed record TierChoice(int Tier, string Label);

    public sealed record WikiUseLink(string Name, string Url);
}
