using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class BisGearViewModel : ObservableObject
{
    private string _class1 = "WAR";
    private string _class2 = "SHM";
    private string _class3 = "ENC";
    private string _playstyleLabel = "Balanced DPS";
    private int _upgradeTier;
    private string _statusText = "Pick three classes, then Find BiS. First run downloads wiki item lists (cached after that).";
    private bool _isBusy;
    private BisSlotRowViewModel? _selectedSlot;
    private CancellationTokenSource? _runCts;

    public BisGearViewModel()
    {
        foreach (var slot in BisGearCatalog.Slots)
            Slots.Add(new BisSlotRowViewModel(slot.Key, slot.Label));
        BisGearCatalog.LoadCached();
        SelectedSlot = Slots.FirstOrDefault();
    }

    public ObservableCollection<BisSlotRowViewModel> Slots { get; } = [];
    public ObservableCollection<BisPickViewModel> Alternatives { get; } = [];

    public IReadOnlyList<BisGearScorer.ClassOption> ClassOptions => BisGearScorer.Classes;
    public IReadOnlyList<string> PlaystyleLabels => BisGearScorer.PlaystyleLabels;
    public IReadOnlyList<int> TierChoices { get; } = Enumerable.Range(0, 11).ToArray();

    public string Class1
    {
        get => _class1;
        set => SetProperty(ref _class1, value);
    }

    public string Class2
    {
        get => _class2;
        set => SetProperty(ref _class2, value);
    }

    public string Class3
    {
        get => _class3;
        set => SetProperty(ref _class3, value);
    }

    public string PlaystyleLabel
    {
        get => _playstyleLabel;
        set => SetProperty(ref _playstyleLabel, value);
    }

    public int UpgradeTier
    {
        get => _upgradeTier;
        set => SetProperty(ref _upgradeTier, Math.Clamp(value, 0, 10));
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaisePropertyChanged(nameof(CanFindBis));
        }
    }

    public bool CanFindBis => !IsBusy;

    public BisSlotRowViewModel? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (!SetProperty(ref _selectedSlot, value)) return;
            Alternatives.Clear();
            if (value is null) return;
            foreach (var pick in value.Picks)
                Alternatives.Add(pick);
        }
    }

    public async Task FindBisAsync()
    {
        if (IsBusy)
            return;

        if (new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Class1, Class2, Class3 }.Count < 3)
        {
            StatusText = "Choose three different classes.";
            return;
        }

        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        IsBusy = true;
        try
        {
            var classes = new[] { Class1, Class2, Class3 };
            var playstyle = ResolvePlaystyle();
            var weights = BisGearScorer.MergeWeights(Class1, Class2, Class3, playstyle);

            var classCats = BisGearScorer.Classes
                .Where(c => classes.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
                .Select(c => c.EquipmentCategory)
                .ToArray();
            var neededCats = classCats
                .Concat(BisGearCatalog.Slots.Select(s => s.WikiCategory).Distinct())
                .Append(BisGearCatalog.QuestItemsCategory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var category in neededCats)
            {
                token.ThrowIfCancellationRequested();
                StatusText = $"Loading wiki list {category.Replace("Category:", "")}…";
                var (ok, error) = await BisGearCatalog.EnsureCategoryAsync(category, token);
                if (!ok)
                {
                    StatusText = error ?? "Wiki catalog failed.";
                    return;
                }
            }

            var wearable = BisGearCatalog.Union(classCats);
            var questItems = new HashSet<string>(BisGearCatalog.TitlesIn(BisGearCatalog.QuestItemsCategory),
                StringComparer.OrdinalIgnoreCase);
            var neededTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in BisGearCatalog.Slots)
            {
                foreach (var title in BisGearCatalog.TitlesIn(slot.WikiCategory))
                {
                    if (wearable.Contains(title) || questItems.Contains(title))
                        neededTitles.Add(title);
                }
            }

            var progress = new Progress<string>(text => StatusText = text);
            try
            {
                await BisGearCatalog.EnsureItemsAsync(neededTitles, progress, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                StatusText = "The wiki request timed out.";
                return;
            }

            var weaponWeights = BisGearScorer.WeaponWeights(weights, playstyle);
            var armorWeights = BisGearScorer.NonWeaponWeights(weights);
            var scoredBySlot = new Dictionary<string, List<BisPickViewModel>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Slots)
            {
                token.ThrowIfCancellationRequested();
                var spec = BisGearCatalog.Slots.First(s => s.Key == row.SlotKey);
                var slotWeights = BisGearScorer.IsWeaponSlot(row.SlotKey) ? weaponWeights : armorWeights;
                scoredBySlot[row.SlotKey] = ScoreSlot(spec, slotWeights, classes, questItems);
            }

            token.ThrowIfCancellationRequested();

            var usedLore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var meleeWeapons = playstyle is BisPlaystyle.Dps or BisPlaystyle.DpsDots or BisPlaystyle.Balanced;
            var tanking = playstyle == BisPlaystyle.Tank;
            foreach (var row in Slots)
            {
                if (row.SlotKey is "Primary" or "Secondary") continue;
                var acFirst = tanking && !BisGearScorer.IsWeaponSlot(row.SlotKey);
                AssignTopPicks(row, scoredBySlot[row.SlotKey], usedLore, meleeFirst: false, acFirst: acFirst);
            }

            var weaponNote = tanking
                ? AssignTankWeaponSlots(scoredBySlot, usedLore)
                : AssignWeaponSlots(scoredBySlot, usedLore, meleeWeapons);

            DeduplicateWornHaste(scoredBySlot, usedLore, meleeWeapons, tanking);
            var acNote = MeetPlateAcFloor(scoredBySlot, usedLore, classes, tanking);

            var sourceTitles = Slots.SelectMany(s => s.Picks.Select(p => p.Name)).ToArray();
            var sourcesNote = "";
            try
            {
                await BisGearCatalog.EnsureSourcesAsync(sourceTitles, progress, token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                sourcesNote = "Drop sources timed out. ";
            }
            catch (OperationCanceledException)
            {
                ApplySourcesToSlots();
                SelectedSlot = Slots.FirstOrDefault();
                StatusText =
                    $"BiS for {Class1}/{Class2}/{Class3} at +{UpgradeTier} ({PlaystyleLabel}). " +
                    weaponNote + acNote + "Drop sources skipped (cancelled).";
                return;
            }

            ApplySourcesToSlots();

            SelectedSlot = Slots.FirstOrDefault();
            if (SelectedSlot is not null)
            {
                Alternatives.Clear();
                foreach (var pick in SelectedSlot.Picks)
                    Alternatives.Add(pick);
            }

            StatusText =
                $"BiS for {Class1}/{Class2}/{Class3} at +{UpgradeTier} ({PlaystyleLabel}). " +
                weaponNote +
                acNote +
                sourcesNote +
                (BisGearScorer.HasMonk(classes)
                    ? "Monk haste cap 85% (Unbound Alacrity). "
                    : "Haste scored up to 75% at 50; only the highest worn haste item counts. ") +
                "STA converts to HP (WAR 4.5/pt). WIS/INT convert to mana for DoTs. Quest items are tagged.";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            StatusText = "The wiki request timed out.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "BiS search cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private List<BisPickViewModel> ScoreSlot(
        (string Key, string Label, string WikiCategory) spec,
        IReadOnlyDictionary<string, double> slotWeights,
        IReadOnlyList<string> classes,
        HashSet<string> questItems)
    {
        var scored = new List<BisPickViewModel>();
        foreach (var title in BisGearCatalog.TitlesIn(spec.WikiCategory))
        {
            var item = BisGearCatalog.TryGet(title);
            if (item is null || item.OutOfEra || string.IsNullOrWhiteSpace(item.BaseStats))
                continue;
            if (!BisGearCatalog.IsWearable(item, classes))
                continue;
            if (!BisGearCatalog.FitsSlot(item, spec.WikiCategory))
                continue;

            var scaled = EqWikiItemUpgrade.ApplyTier(item.BaseStats, UpgradeTier);
            var stats = EqWikiItemUpgrade.ParseStatValues(scaled);
            var weaponSlot = BisGearScorer.IsWeaponSlot(spec.Key);
            var proc = BisItemEffects.Parse(scaled);
            // Combat weapon procs / DMG / ratio only matter in hand (or range) slots.
            if (!weaponSlot && BisItemEffects.IsDpsProc(proc))
                proc = new BisProcInfo(proc.Name, BisProcKind.None, 0, proc.Trigger);
            var scoreStats = weaponSlot ? stats : BisGearScorer.WithoutWeaponOffense(stats);
            var score = BisGearScorer.Score(scoreStats, slotWeights, classes, weaponSlot ? proc : null);
            if (score <= 0) continue;
            var fromQuest = item.IsQuest || questItems.Contains(item.Title);
            var weapon = BisMeleeMath.FromStats(item.Title, stats, BisGearCatalog.IsTwoHanded(item), proc);
            var isShield = BisGearCatalog.IsShield(item, stats);
            scored.Add(new BisPickViewModel(item.Title, score, BisGearScorer.Summary(scoreStats, proc),
                fromQuest, scaled, EqWikiLinks.ForPage(item.Title), weapon, stats.GetValueOrDefault("AC"),
                stats.GetValueOrDefault("HASTE"), isShield, item.DropZone, item.DropMob, item.SourceText));
        }

        return scored;
    }

    private static void AssignTopPicks(BisSlotRowViewModel row, IEnumerable<BisPickViewModel> scored,
        HashSet<string> usedLore, bool meleeFirst = false, bool acFirst = false)
    {
        var top = scored
            .Where(p => !p.IsLoreLike || !usedLore.Contains(p.Name))
            .OrderByDescending(p => acFirst ? p.Ac : meleeFirst ? p.MeleeOutput : p.Score)
            .ThenByDescending(p => p.Score)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        row.SetPicks(top);
        if (top.Length > 0 && top[0].IsLoreLike)
            usedLore.Add(top[0].Name);
    }

    private string AssignWeaponSlots(Dictionary<string, List<BisPickViewModel>> scoredBySlot,
        HashSet<string> usedLore, bool meleeWeapons)
    {
        var primaryRow = Slots.First(s => s.SlotKey == "Primary");
        var secondaryRow = Slots.First(s => s.SlotKey == "Secondary");
        var primary = Available(scoredBySlot["Primary"], usedLore);
        var secondary = Available(scoredBySlot["Secondary"], usedLore);

        var bestTwoHand = BestWeapon(primary.Where(p => p.IsTwoHand), meleeWeapons);
        var bestOneHand = BestWeapon(primary.Where(p => !p.IsTwoHand), meleeWeapons);
        var bestOffhand = BestWeapon(
            secondary
                .Where(p => !p.IsTwoHand)
                .Where(p => bestOneHand is null ||
                            !p.IsLoreLike ||
                            !p.Name.Equals(bestOneHand.Name, StringComparison.OrdinalIgnoreCase)),
            meleeWeapons);

        var twoHandDps = bestTwoHand?.MeleeOutput ?? 0;
        var dualDps = BisMeleeMath.DualWieldOutput(bestOneHand?.Weapon, bestOffhand?.Weapon);
        var twoHandScore = bestTwoHand?.Score ?? 0;
        var dualScore = (bestOneHand?.Score ?? 0) + (bestOffhand?.Score ?? 0);
        var useTwoHand = bestTwoHand is not null &&
                         (meleeWeapons
                             ? BisMeleeMath.PreferTwoHand(bestTwoHand.Weapon, bestOneHand?.Weapon, bestOffhand?.Weapon)
                             : twoHandScore >= dualScore);

        if (useTwoHand)
        {
            AssignTopPicks(primaryRow, primary.Where(p => p.IsTwoHand), usedLore, meleeFirst: meleeWeapons);
            secondaryRow.SetOccupiedByTwoHand();
            return meleeWeapons
                ? $"2H {bestTwoHand!.Name} (melee {twoHandDps:0.000} incl. proc) beat dual wield {dualDps:0.000}. "
                : $"2H {bestTwoHand!.Name} beat 1H+offhand ({twoHandScore:0} vs {dualScore:0}). ";
        }

        AssignTopPicks(primaryRow, primary.Where(p => !p.IsTwoHand), usedLore, meleeFirst: meleeWeapons);
        AssignTopPicks(secondaryRow, secondary.Where(p => !p.IsTwoHand), usedLore, meleeFirst: meleeWeapons);
        if (bestTwoHand is not null)
        {
            return meleeWeapons
                ? $"Dual wield beat 2H {bestTwoHand.Name} (melee {dualDps:0.000} vs {twoHandDps:0.000} incl. procs). "
                : $"Dual wield beat 2H {bestTwoHand.Name} ({dualScore:0} vs {twoHandScore:0}). ";
        }

        return "";
    }

    private string AssignTankWeaponSlots(Dictionary<string, List<BisPickViewModel>> scoredBySlot,
        HashSet<string> usedLore)
    {
        var primaryRow = Slots.First(s => s.SlotKey == "Primary");
        var secondaryRow = Slots.First(s => s.SlotKey == "Secondary");
        var primary = Available(scoredBySlot["Primary"], usedLore).Where(p => !p.IsTwoHand);
        var secondary = Available(scoredBySlot["Secondary"], usedLore).Where(p => !p.IsTwoHand);

        AssignTopPicks(primaryRow, primary, usedLore, meleeFirst: false, acFirst: true);
        var offhand = secondary
            .OrderByDescending(p => p.IsShield ? 1 : 0)
            .ThenByDescending(p => p.Ac)
            .ThenByDescending(p => p.Score)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        secondaryRow.SetPicks(offhand);
        if (offhand.Length > 0 && offhand[0].IsLoreLike)
            usedLore.Add(offhand[0].Name);

        var shield = offhand.FirstOrDefault();
        return shield is { IsShield: true }
            ? $"1H + shield {shield.Name} ({shield.Ac:0} AC; shield AC raises the mitigation cap). "
            : "1H + offhand (no shield found; tanking prefers a shield). ";
    }

    private void DeduplicateWornHaste(Dictionary<string, List<BisPickViewModel>> scoredBySlot,
        HashSet<string> usedLore, bool meleeWeapons, bool tanking)
    {
        // Only the highest worn haste counts — including weapons and range.
        var worn = Slots
            .Where(s => s.Best is { Haste: > 0 })
            .OrderByDescending(s => s.Best!.Haste)
            .ThenBy(s => BisGearScorer.IsWeaponSlot(s.SlotKey) ? 1 : 0)
            .ToList();
        if (worn.Count <= 1) return;

        foreach (var extra in worn.Skip(1))
        {
            if (extra.Best is { IsLoreLike: true } old)
                usedLore.Remove(old.Name);
            var withoutHaste = scoredBySlot[extra.SlotKey]
                .Where(p => p.Haste <= 0 || p.Haste < worn[0].Best!.Haste);
            AssignTopPicks(extra, withoutHaste, usedLore, meleeFirst: meleeWeapons &&
                BisGearScorer.IsWeaponSlot(extra.SlotKey), acFirst: tanking);
        }
    }

    private static BisPickViewModel? BestWeapon(IEnumerable<BisPickViewModel> picks, bool melee) =>
        melee
            ? picks.OrderByDescending(p => p.MeleeOutput).ThenByDescending(p => p.Score).FirstOrDefault()
            : picks.MaxBy(p => p.Score);

    private static IEnumerable<BisPickViewModel> Available(IEnumerable<BisPickViewModel> picks,
        HashSet<string> usedLore) =>
        picks.Where(p => !p.IsLoreLike || !usedLore.Contains(p.Name));

    private BisPlaystyle ResolvePlaystyle()
    {
        if (PlaystyleLabel.StartsWith("Tank", StringComparison.OrdinalIgnoreCase))
            return BisPlaystyle.Tank;
        if (PlaystyleLabel.StartsWith("Caster", StringComparison.OrdinalIgnoreCase))
            return BisPlaystyle.Caster;
        if (PlaystyleLabel.StartsWith("Melee", StringComparison.OrdinalIgnoreCase))
            return BisPlaystyle.Dps;
        if (PlaystyleLabel.StartsWith("DoT", StringComparison.OrdinalIgnoreCase))
            return BisPlaystyle.DpsDotsOnly;
        if (PlaystyleLabel.StartsWith("Balanced", StringComparison.OrdinalIgnoreCase))
            return BisPlaystyle.Balanced;
        return BisPlaystyle.Balanced;
    }

    private void ApplySourcesToSlots()
    {
        foreach (var row in Slots)
        {
            foreach (var pick in row.Picks)
            {
                var cached = BisGearCatalog.TryGet(pick.Name);
                if (cached is null)
                    continue;
                pick.ApplySource(cached.DropZone, cached.DropMob, cached.SourceText);
            }

            row.NotifyDisplayChanged();
        }
    }

    private string MeetPlateAcFloor(
        Dictionary<string, List<BisPickViewModel>> scoredBySlot,
        HashSet<string> usedLore,
        IReadOnlyList<string> classes,
        bool tanking)
    {
        if (!BisGearScorer.HasPlateClass(classes))
            return "";

        var floor = BisGearScorer.WornAcFloor(classes);
        var combatCap = BisGearScorer.AcHardCap(classes);
        var setAc = Slots.Sum(s => s.Best?.Ac ?? 0);
        if (setAc >= floor)
            return $"Plate worn AC {setAc:0} meets the {floor} floor (~{combatCap} combat AC). ";

        var hasteSlot = Slots
            .Where(s => s.Best is { Haste: > 0 })
            .OrderByDescending(s => s.Best!.Haste)
            .FirstOrDefault();
        var uniqueHaste = hasteSlot?.Best?.Haste ?? 0;

        var swapped = 0;
        for (var step = 0; step < 24 && setAc < floor; step++)
        {
            BisSlotRowViewModel? bestRow = null;
            BisGearScorer.PlateAcOption? bestOpt = null;
            var bestGain = 0.0;
            var bestEff = double.MinValue;

            foreach (var row in Slots)
            {
                if (BisGearScorer.IsWeaponSlot(row.SlotKey) ||
                    row.SlotKey.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (row.Best is null)
                    continue;
                if (hasteSlot is not null && ReferenceEquals(row, hasteSlot))
                    continue;

                var current = row.Best;
                var usedExceptCurrent = new HashSet<string>(usedLore, StringComparer.OrdinalIgnoreCase);
                if (current.IsLoreLike)
                    usedExceptCurrent.Remove(current.Name);

                var option = BisGearScorer.BestPlateAcSwap(
                    current.Ac, current.Score, uniqueHaste,
                    scoredBySlot[row.SlotKey]
                        .Where(p => !p.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(p => new BisGearScorer.PlateAcOption(p.Name, p.Ac, p.Score, p.IsLoreLike, p.Haste)),
                    usedExceptCurrent);
                if (option is null)
                    continue;

                var gain = option.Value.Ac - current.Ac;
                var loss = Math.Max(0, current.Score - option.Value.Score);
                var efficiency = gain / (loss + 1);
                if (efficiency > bestEff || (Math.Abs(efficiency - bestEff) < 1e-9 && gain > bestGain))
                {
                    bestEff = efficiency;
                    bestGain = gain;
                    bestRow = row;
                    bestOpt = option;
                }
            }

            if (bestRow is null || bestOpt is null)
                break;

            if (bestRow.Best is { IsLoreLike: true } old)
                usedLore.Remove(old.Name);
            var next = scoredBySlot[bestRow.SlotKey]
                .First(p => p.Name.Equals(bestOpt.Value.Name, StringComparison.OrdinalIgnoreCase));
            var rest = scoredBySlot[bestRow.SlotKey]
                .Where(p => !p.Name.Equals(next.Name, StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.IsLoreLike || !usedLore.Contains(p.Name))
                .OrderByDescending(p => tanking ? p.Ac : p.Score)
                .ThenByDescending(p => p.Score)
                .Take(4);
            bestRow.SetPicks(new[] { next }.Concat(rest).ToArray());
            if (next.IsLoreLike)
                usedLore.Add(next.Name);
            setAc += bestGain;
            swapped++;
        }

        return setAc >= floor
            ? $"Plate worn AC {setAc:0} raised to the {floor} floor (~{combatCap} combat AC; {swapped} slot{(swapped == 1 ? "" : "s")}). "
            : $"Plate worn AC {setAc:0} (floor {floor} toward {combatCap} combat AC; could not reach with available pieces). ";
    }

    public void OpenSelectedWiki()
    {
        var url = SelectedSlot?.Best?.WikiUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}

public sealed class BisSlotRowViewModel : ObservableObject
{
    private string _bestName = "—";
    private string _bestScore = "";

    public BisSlotRowViewModel(string slotKey, string label)
    {
        SlotKey = slotKey;
        Label = label;
    }

    public string SlotKey { get; }
    public string Label { get; }
    public IReadOnlyList<BisPickViewModel> Picks { get; private set; } = [];
    public BisPickViewModel? Best => Picks.FirstOrDefault();

    public string BestName
    {
        get => _bestName;
        private set => SetProperty(ref _bestName, value);
    }

    public string BestScore
    {
        get => _bestScore;
        private set => SetProperty(ref _bestScore, value);
    }

    public Visibility QuestVisibility =>
        Best is { FromQuest: true } ? Visibility.Visible : Visibility.Collapsed;

    public void SetPicks(IReadOnlyList<BisPickViewModel> picks)
    {
        Picks = picks;
        BestName = picks.FirstOrDefault()?.Name ?? "—";
        BestScore = picks.Count == 0 ? "" : picks[0].Score.ToString("0");
        NotifyDisplayChanged();
    }

    public void SetOccupiedByTwoHand()
    {
        Picks = [];
        BestName = "— (2H)";
        BestScore = "";
        NotifyDisplayChanged();
    }

    public void NotifyDisplayChanged()
    {
        RaisePropertyChanged(nameof(Best));
        RaisePropertyChanged(nameof(Picks));
        RaisePropertyChanged(nameof(QuestVisibility));
        RaisePropertyChanged(nameof(BestSource));
        RaisePropertyChanged(nameof(SourceVisibility));
    }

    public string BestSource => Best?.SourceText ?? "";

    public Visibility SourceVisibility =>
        string.IsNullOrWhiteSpace(BestSource) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class BisPickViewModel
{
    public BisPickViewModel(string name, double score, string summary, bool fromQuest, string stats, string wikiUrl,
        BisMeleeMath.Weapon weapon, double ac = 0, double haste = 0, bool isShield = false,
        string dropZone = "", string dropMob = "", string sourceText = "")
    {
        Name = name;
        Score = score;
        Summary = summary;
        FromQuest = fromQuest;
        Stats = stats;
        WikiUrl = wikiUrl;
        SourceLabel = fromQuest ? "Quest" : "Drop / other";
        IsLoreLike = stats.Contains("LORE", StringComparison.OrdinalIgnoreCase);
        Weapon = weapon;
        IsTwoHand = weapon.IsTwoHand;
        Ratio = weapon.Ratio;
        Dmg = weapon.Dmg;
        Delay = weapon.Delay;
        MeleeOutput = weapon.MeleeOutput;
        ProcName = weapon.Proc.Name;
        Ac = ac;
        Haste = haste;
        IsShield = isShield;
        DropZone = dropZone;
        DropMob = dropMob;
        SourceText = sourceText;
    }

    public void ApplySource(string dropZone, string dropMob, string sourceText)
    {
        DropZone = dropZone;
        DropMob = dropMob;
        SourceText = sourceText;
    }

    public BisMeleeMath.Weapon Weapon { get; }
    public string Name { get; }
    public double Score { get; }
    public string Summary { get; }
    public bool FromQuest { get; }
    public string Stats { get; }
    public string WikiUrl { get; }
    public string SourceLabel { get; }
    public bool IsLoreLike { get; }
    public bool IsTwoHand { get; }
    public double Ratio { get; }
    public double Dmg { get; }
    public double Delay { get; }
    public double MeleeOutput { get; }
    public string ProcName { get; }
    public double Ac { get; }
    public double Haste { get; }
    public bool IsShield { get; }
    public string DropZone { get; private set; }
    public string DropMob { get; private set; }
    public string SourceText { get; private set; }
    public Visibility QuestVisibility => FromQuest ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SourceLineVisibility =>
        string.IsNullOrWhiteSpace(SourceText) ? Visibility.Collapsed : Visibility.Visible;

    public void OpenWiki()
    {
        if (string.IsNullOrWhiteSpace(WikiUrl))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(WikiUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
