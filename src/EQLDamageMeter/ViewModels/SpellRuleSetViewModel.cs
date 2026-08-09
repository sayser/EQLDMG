using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class SpellRuleSetViewModel : ObservableObject
{
    private readonly SpellTrackerCategory _category;
    private readonly Func<SpellDataCatalog?> _catalog;
    private readonly Func<int> _casterLevel;
    private readonly Func<IEnumerable<BuffRuleSettings>, CancellationToken, Task<bool>> _save;
    private readonly BuffAlertService _alerts;
    private readonly BuffTracker _tracker = new();
    private readonly SemaphoreSlim _timingGate = new(1, 1);
    private BuffRuleViewModel? _selectedRule;
    private string _searchText = string.Empty;
    private string _filterMode = "All";

    public SpellRuleSetViewModel(SpellTrackerCategory category,
        IEnumerable<BuffRuleSettings> savedRules,
        Func<SpellDataCatalog?> catalog,
        Func<IEnumerable<BuffRuleSettings>, CancellationToken, Task<bool>> save,
        BuffAlertService alerts,
        Func<int>? casterLevel = null)
    {
        _category = category;
        _catalog = catalog;
        _casterLevel = casterLevel ?? (() => SpellDataCatalog.DefaultCasterLevel);
        _save = save;
        _alerts = alerts;
        foreach (var settings in savedRules)
            Rules.Add(new BuffRuleViewModel(settings with
            {
                Category = category,
                TrackSelf = false,
                TrackOthers = true
            }));
        RulesView = CollectionViewSource.GetDefaultView(Rules);
        RulesView.Filter = FilterRule;
        RefreshConfiguration();
    }

    public SpellTrackerCategory Category => _category;
    public string SingularName => _category == SpellTrackerCategory.Control ? "control spell" : "DoT";
    public string HeaderTitle => _category == SpellTrackerCategory.Control ? "CONTROL TRACKER" : "DoT TRACKER";
    public string DetailsTitle => _category == SpellTrackerCategory.Control ? "CONTROL DETAILS" : "DoT DETAILS";
    public string AddLabel => _category == SpellTrackerCategory.Control ? "+ Add Control" : "+ Add DoT";
    public string OverlayLabel => _category == SpellTrackerCategory.Control ? "Control Overlay" : "DoT Overlay";
    public string EmptyDetailsHint => _category == SpellTrackerCategory.Control
        ? "Select a control spell or click + Add Control to view details."
        : "Select a DoT or click + Add DoT to view details.";
    public bool IsControl => _category == SpellTrackerCategory.Control;
    public SpellDataCatalog? SpellCatalog => _catalog();
    public ObservableCollection<BuffRuleViewModel> Rules { get; } = [];
    public ObservableCollection<BuffOverlayEntryViewModel> OverlayEntries { get; } = [];
    public ICollectionView RulesView { get; }
    public IReadOnlyList<BuffAlertMode> AlertModes { get; } = BuffAlertModeOptions.ExclusiveChoices;
    public IReadOnlyList<BuffSoundKind> SoundChoices { get; } = Enum.GetValues<BuffSoundKind>();
    public IReadOnlyList<ControlEffectType> ControlTypes { get; } = Enum.GetValues<ControlEffectType>();

    public BuffRuleViewModel? SelectedRule
    {
        get => _selectedRule;
        set => SetProperty(ref _selectedRule, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) RulesView.Refresh();
        }
    }

    public void SetFilter(string mode)
    {
        _filterMode = mode;
        RulesView.Refresh();
    }

    public BuffRuleViewModel AddRule()
    {
        var duration = _category == SpellTrackerCategory.Control ? 30 : 60;
        var controlType = _category == SpellTrackerCategory.Control ? ControlEffectType.Mez : ControlEffectType.Other;
        var rule = new BuffRuleViewModel(new BuffRuleSettings(Guid.NewGuid(), string.Empty, duration, 3,
            true, true, BuffAlertMode.Sound, BuffSoundKind.Chime, string.Empty, false, true,
            _category, controlType));
        Rules.Add(rule);
        SelectedRule = rule;
        _filterMode = "All";
        RulesView.Refresh();
        return rule;
    }

    public async Task<string?> DeleteRuleAsync(BuffRuleViewModel rule)
    {
        Rules.Remove(rule);
        if (ReferenceEquals(SelectedRule, rule)) SelectedRule = null;
        return await SaveAsync();
    }

    public async Task<string?> SaveAsync(CancellationToken cancellationToken = default)
    {
        var settings = new List<BuffRuleSettings>(Rules.Count);
        foreach (var rule in Rules)
        {
            rule.Category = _category;
            rule.TrackSelf = false;
            rule.TrackOthers = true;
            if (!rule.TryCreateSettings(out var configured, out var error))
            {
                SelectedRule = rule;
                return $"{DisplayName(rule)}: {error}";
            }
            if (!TryResolveSpell(rule.SpellName, out var spell, out error))
            {
                SelectedRule = rule;
                rule.SetSpellValidation(error);
                return error;
            }
            var previousFamily = SpellNameNormalizer.GetFamilyName(rule.SpellName);
            rule.SpellName = spell!.Name;
            rule.SetSpellValidation(null);
            rule.SetIcon(_catalog()?.GetIcon(spell));
            rule.ApplyCatalogTimings(spell,
                force: !previousFamily.Equals(spell.Name, StringComparison.OrdinalIgnoreCase),
                casterLevel: _casterLevel());
            if (!rule.TryCreateSettings(out configured, out error))
            {
                SelectedRule = rule;
                return $"{DisplayName(rule)}: {error}";
            }
            settings.Add(configured! with
            {
                SpellName = spell.Name,
                Category = _category,
                TrackSelf = false,
                TrackOthers = true
            });
        }

        var duplicate = settings.GroupBy(rule => SpellNameNormalizer.GetFamilyName(rule.SpellName),
            StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) return $"Only one {_category} rule can use {duplicate.Key}.";

        Configure(settings);
        RefreshRuleIcons();
        RefreshOverlay(DateTime.Now);
        RulesView.Refresh();
        return await _save(settings, cancellationToken)
            ? null
            : $"{_category} rules could not be saved to spelltracker.json. Check that the app folder is writable.";
    }

    public string? ValidateSpell(BuffRuleViewModel? rule)
    {
        if (rule is null) return null;
        if (!TryResolveSpell(rule.SpellName, out var spell, out var error))
        {
            rule.SetSpellValidation(error);
            return error;
        }
        var previousFamily = SpellNameNormalizer.GetFamilyName(rule.SpellName);
        rule.SpellName = spell!.Name;
        rule.SetSpellValidation(null);
        rule.SetIcon(_catalog()?.GetIcon(spell));
        rule.ApplyCatalogTimings(spell,
            force: string.IsNullOrWhiteSpace(previousFamily) ||
                   !previousFamily.Equals(spell.Name, StringComparison.OrdinalIgnoreCase),
            casterLevel: _casterLevel());
        return null;
    }

    public string? ResetSelectedTimingsToCatalog()
    {
        if (SelectedRule is null) return $"Select a {SingularName} first.";
        if (!TryResolveSpell(SelectedRule.SpellName, out var spell, out var error))
        {
            SelectedRule.SetSpellValidation(error);
            return error;
        }
        SelectedRule.SpellName = spell!.Name;
        SelectedRule.SetSpellValidation(null);
        SelectedRule.SetIcon(_catalog()?.GetIcon(spell));
        SelectedRule.ApplyCatalogTimings(spell, force: true, casterLevel: _casterLevel());
        return null;
    }

    /// <summary>Re-seeds Catalog-sourced cast/duration using the current caster level.</summary>
    public bool ReseedCatalogTimings(int casterLevel)
    {
        _timingGate.Wait();
        try
        {
            return ReseedCatalogTimingsCore(casterLevel);
        }
        finally
        {
            _timingGate.Release();
        }
    }

    public async Task<bool> ReseedCatalogTimingsAsync(int casterLevel)
    {
        await _timingGate.WaitAsync();
        try
        {
            return ReseedCatalogTimingsCore(casterLevel);
        }
        finally
        {
            _timingGate.Release();
        }
    }

    private bool ReseedCatalogTimingsCore(int casterLevel)
    {
        var catalog = _catalog();
        if (catalog is null || casterLevel <= 0) return false;
        var changed = false;
        foreach (var rule in Rules)
        {
            if (rule.CastSource != SpellTimingSource.Catalog &&
                rule.DurationSource != SpellTimingSource.Catalog) continue;
            if (!catalog.TryResolveFamily(rule.SpellName, out var spell) || spell is null) continue;
            var beforeCast = rule.CastTimeText;
            var beforeDuration = rule.DurationText;
            rule.ApplyCatalogTimings(spell, force: false, casterLevel: casterLevel);
            if (!string.Equals(beforeCast, rule.CastTimeText, StringComparison.Ordinal) ||
                !string.Equals(beforeDuration, rule.DurationText, StringComparison.Ordinal))
                changed = true;
        }
        if (changed) RefreshConfiguration();
        return changed;
    }

    public string? TestAlert()
    {
        if (SelectedRule is null) return $"Select a {SingularName} first.";
        if (!SelectedRule.TryCreateSettings(out var settings, out var error)) return error;
        _alerts.Test(settings!);
        return null;
    }

    public void Observe(DateTime timestamp, string message) => _tracker.Observe(timestamp, message);
    public bool HasActiveCharmTarget(string target, DateTime now) => _tracker.HasActiveCharmTarget(target, now);
    public void ClearRuntime()
    {
        _tracker.ClearRuntime();
        OverlayEntries.Clear();
    }

    public void Tick(DateTime now)
    {
        foreach (var alert in _tracker.Tick(now)) _alerts.Play(alert.Rule);
        foreach (var rule in Rules) rule.ApplyRuntime(_tracker.GetSnapshot(rule.Id, now));
        RefreshOverlay(now);
    }

    public void RefreshConfiguration()
    {
        var settings = Rules.Select(rule => rule.TryCreateSettings(out var configured, out _)
            ? configured
            : null).OfType<BuffRuleSettings>().Select(rule => rule with
            {
                Category = _category,
                TrackSelf = false,
                TrackOthers = true
            }).ToArray();
        // Do not prune rules mid-edit: an invalid sibling must keep its last runtime state.
        Configure(settings, pruneMissing: false);
        RefreshRuleIcons();
        RefreshOverlay(DateTime.Now);
    }

    public void RefreshIcons()
    {
        RefreshRuleIcons();
        OverlayEntries.Clear();
        RefreshOverlay(DateTime.Now);
    }

    public void NotifyCatalogChanged() => RaisePropertyChanged(nameof(SpellCatalog));

    private void Configure(IReadOnlyCollection<BuffRuleSettings> settings, bool pruneMissing = true) =>
        _tracker.Configure(settings, ResolveFadeMessages, _ => [], ResolveOtherAppliedMessages,
            suffix => _catalog()?.IsAmbiguousOtherAppliedSuffix(suffix) == true,
            pruneMissing: pruneMissing);

    private void RefreshRuleIcons()
    {
        var catalog = _catalog();
        foreach (var rule in Rules)
            rule.SetIcon(string.IsNullOrWhiteSpace(rule.SpellName) ? null : catalog?.GetIcon(rule.SpellName));
    }

    private IReadOnlyList<string> ResolveFadeMessages(string spellName) =>
        _catalog() is { } catalog && catalog.TryResolveFamily(spellName, out var spell)
            ? spell!.FadeMessages
            : [];

    private IReadOnlyList<string> ResolveOtherAppliedMessages(string spellName) =>
        _catalog() is { } catalog && catalog.TryResolveFamily(spellName, out var spell)
            ? spell!.OtherAppliedMessageSuffixes
            : [];

    private bool TryResolveSpell(string spellName, out SpellDataEntry? spell, out string error)
    {
        spell = null;
        if (string.IsNullOrWhiteSpace(spellName))
        {
            error = "Enter a spell name.";
            return false;
        }
        var catalog = _catalog();
        if (catalog is null)
        {
            error = "EverQuest Legends spell data is not available. Select the game's Logs folder and try again.";
            return false;
        }
        if (catalog.TryResolveFamily(spellName, out spell))
        {
            error = string.Empty;
            return true;
        }
        error = "Spell not found";
        return false;
    }

    private void RefreshOverlay(DateTime now)
    {
        var visible = Rules.Where(rule => rule.IsEnabled && rule.ShowInOverlay)
            .ToDictionary(rule => rule.Id);
        var snapshots = _tracker.GetActiveSnapshots(now).Where(item => visible.ContainsKey(item.RuleId)).ToArray();
        var desired = snapshots.Select(BuffOverlayEntryViewModel.CreateKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in OverlayEntries.Where(item => !desired.Contains(item.InstanceKey)).ToArray())
            OverlayEntries.Remove(stale);
        for (var index = 0; index < snapshots.Length; index++)
        {
            var snapshot = snapshots[index];
            var key = BuffOverlayEntryViewModel.CreateKey(snapshot);
            var entry = OverlayEntries.FirstOrDefault(item => item.InstanceKey.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                var rule = visible[snapshot.RuleId];
                entry = new BuffOverlayEntryViewModel(snapshot, _category, rule.ControlType,
                    rule.Icon ?? _catalog()?.GetIcon(snapshot.SpellName));
                OverlayEntries.Insert(Math.Min(index, OverlayEntries.Count), entry);
            }
            else
            {
                entry.Update(snapshot);
                var current = OverlayEntries.IndexOf(entry);
                if (current != index) OverlayEntries.Move(current, index);
            }
        }
    }

    private bool FilterRule(object item) => item is BuffRuleViewModel rule &&
        (string.IsNullOrWhiteSpace(SearchText) || rule.SpellName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        (_filterMode == "All" || (_filterMode == "Enabled" ? rule.IsEnabled : !rule.IsEnabled));

    private static string DisplayName(BuffRuleViewModel rule) =>
        string.IsNullOrWhiteSpace(rule.SpellName) ? "New spell" : rule.SpellName;
}
