using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;

namespace EQLDamageMeter.Replay;

/// <summary>
/// Offline replay harness. Feeds a real character log through the production
/// parser/group/encounter pipeline in the same order MainViewModel does, then
/// reports encounter results, parser coverage and throughput.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var logPath = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        if (logPath is null || !File.Exists(logPath))
        {
            Console.Error.WriteLine("usage: replay <path-to-eqlog.txt> [--top N] [--min-damage N] [--fights]");
            return 1;
        }

        var top = ReadIntOption(args, "--top", 20);
        var minDamage = ReadIntOption(args, "--min-damage", 20000);
        var listFights = args.Contains("--fights");
        var since = ReadDateOption(args, "--since");

        var player = ResolvePlayerName(logPath!);
        Console.WriteLine($"log    : {logPath}");
        Console.WriteLine($"player : {player}");
        Console.WriteLine($"size   : {new FileInfo(logPath).Length / 1024d / 1024d:N1} MB");
        Console.WriteLine();

        var run = Replay(logPath, player);
        if (since is { } cutoff)
        {
            // Live monitoring: keep the whole log's raw events for cross-checking but
            // report only the fights played since the session started.
            run.Encounters.RemoveAll(item => item.StartedAt < cutoff);
            Console.WriteLine($"since  : {cutoff:yyyy-MM-dd HH:mm:ss} ({run.Encounters.Count} fights)");
            Console.WriteLine();
            minDamage = 0;
        }

        PrintCoverage(run);
        PrintSuspects(run);
        PrintEncounters(run, player, top, minDamage, listFights, since.HasValue);
        PrintAudit(run, player);
        if (!since.HasValue) PrintBuffSimulation(logPath!, player, run);
        PrintPerformance(run);
        return 0;
    }

    private static DateTime? ReadDateOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length &&
               DateTime.TryParse(args[index + 1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }

    private readonly record struct RawDamage(DateTime Timestamp, string Source, string Target, int Amount);

    private readonly record struct PetBinding(DateTime Timestamp, string Pet, string Owner, string Mechanism);

    private readonly record struct RawOutcome(DateTime Timestamp, string Source, string? Target,
        CombatOutcomeKind Kind);

    private sealed class ReplayRun
    {
        public long Lines;
        public long EnvelopeFailures;
        public long DamageEvents;
        public long HealingEvents;
        public long OutcomeEvents;
        public long UnclassifiedCombatLines;
        public long GroupChanges;
        public double ParseMilliseconds;
        public double PipelineMilliseconds;
        public double TotalMilliseconds;
        public long AllocatedBytes;
        public long FriendlyFireEvents;
        public long FriendlyFireDamage;
        public long MalformedOutcomeTargets;
        public long SnapshotClones;
        public long StunApplied;
        public long StunDiminished;
        public long CharmLines;
        public readonly List<EncounterSnapshot> Encounters = [];
        public readonly List<RawDamage> RawDamage = [];
        public readonly List<RawDamage> RawHealing = [];
        public readonly List<RawOutcome> RawOutcomes = [];
        public readonly HashSet<string> DamageSources = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> DamageTargets = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> HealSources = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> HealTargets = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<PetBinding> PetBindings = [];
        public readonly List<string> Violations = [];
        public readonly Dictionary<string, long> SuspectNames = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> UnclassifiedSamples = new(StringComparer.Ordinal);
        public readonly Dictionary<string, long> LocalCasts = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ReplayRun Replay(string logPath, string player)
    {
        var run = new ReplayRun();
        var parser = new LogLineParser(player);
        var group = new GroupStateTracker(player);
        var encounter = new EncounterTracker(player);
        var archivedStarts = new HashSet<DateTime>();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var total = Stopwatch.StartNew();
        var parseWatch = new Stopwatch();
        var pipelineWatch = new Stopwatch();

        using var reader = new StreamReader(
            new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16),
            Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            run.Lines++;

            parseWatch.Start();
            var parsed = parser.TryParse(line, out var value) ? value : null;
            parseWatch.Stop();

            if (parsed is null)
            {
                run.EnvelopeFailures++;
                continue;
            }

            if (parsed.Damage is not null) run.DamageEvents++;
            if (parsed.Healing is not null) run.HealingEvents++;
            if (parsed.Outcome is not null) run.OutcomeEvents++;
            if (parsed.Damage is null && parsed.Healing is null && parsed.Outcome is null &&
                LooksLikeCombat(parsed.Message))
            {
                run.UnclassifiedCombatLines++;
                var template = Templatize(parsed.Message);
                run.UnclassifiedSamples[template] = run.UnclassifiedSamples.GetValueOrDefault(template) + 1;
            }

            TrackSuspectNames(run, parsed);

            if (parsed.Damage is { } rawDamage)
            {
                run.RawDamage.Add(new RawDamage(rawDamage.Timestamp, rawDamage.Source, rawDamage.Target,
                    rawDamage.Amount));
                run.DamageSources.Add(rawDamage.Source);
                run.DamageTargets.Add(rawDamage.Target);
            }

            if (parsed.Healing is { } rawHealing)
            {
                run.RawHealing.Add(new RawDamage(rawHealing.Timestamp, rawHealing.Source, rawHealing.Target,
                    rawHealing.Amount));
                run.HealSources.Add(rawHealing.Source);
                run.HealTargets.Add(rawHealing.Target);
            }

            if (parsed.Outcome is { } rawOutcome)
            {
                run.RawOutcomes.Add(new RawOutcome(rawOutcome.Timestamp, rawOutcome.Source, rawOutcome.Target,
                    rawOutcome.Kind));
            }

            if (parsed.Message.EndsWith(" has been charmed.", StringComparison.OrdinalIgnoreCase)) run.CharmLines++;

            if (parsed.Message.StartsWith("You begin casting ", StringComparison.OrdinalIgnoreCase) &&
                parsed.Message.EndsWith(".", StringComparison.Ordinal))
            {
                var spell = parsed.Message["You begin casting ".Length..^1];
                run.LocalCasts[spell] = run.LocalCasts.GetValueOrDefault(spell) + 1;
            }

            pipelineWatch.Start();
            // Simulate the refresh timer closing out an idle encounter before the
            // next line lands, which is what FinalizeIfInactive does live.
            encounter.FinalizeIfInactive(parsed.Timestamp);
            ArchiveIfComplete(run, encounter, archivedStarts, parsed.Timestamp);

            var priorStart = encounter.StartedAt;
            var eventTimestamp = parsed.Damage?.Timestamp ?? parsed.Outcome?.Timestamp;
            var mayStartNewEncounter = priorStart.HasValue && eventTimestamp.HasValue &&
                (encounter.IsFinalized ||
                 (encounter.CompletionCandidateAt.HasValue &&
                  eventTimestamp.Value - encounter.CompletionCandidateAt.Value >= encounter.KillCompletionGrace) ||
                 (encounter.LastDamageAt.HasValue &&
                  eventTimestamp.Value - encounter.LastDamageAt.Value > encounter.EncounterTimeout));
            EncounterSnapshot? priorSnapshot = null;
            if (mayStartNewEncounter && !archivedStarts.Contains(priorStart!.Value))
            {
                run.SnapshotClones++;
                priorSnapshot = encounter.CreateSnapshot(
                    encounter.CompletionCandidateAt ?? encounter.LastDamageAt ?? parsed.Timestamp);
            }

            if (parsed.Damage is { } inspected &&
                (inspected.Source.Equals(player, StringComparison.OrdinalIgnoreCase) ||
                 group.IsConfirmedMemberOrPet(inspected.Source)) &&
                !inspected.Source.Equals(inspected.Target, StringComparison.OrdinalIgnoreCase) &&
                (inspected.Target.Equals(player, StringComparison.OrdinalIgnoreCase) ||
                 group.IsConfirmedMemberOrPet(inspected.Target)))
            {
                run.FriendlyFireEvents++;
                run.FriendlyFireDamage += inspected.Amount;
            }

            switch (parsed.Outcome?.Kind)
            {
                case CombatOutcomeKind.StunApplied: run.StunApplied++; break;
                case CombatOutcomeKind.StunDiminished: run.StunDiminished++; break;
            }

            if (parsed.Outcome?.Target is { } outcomeTarget &&
                outcomeTarget.StartsWith("on ", StringComparison.OrdinalIgnoreCase))
            {
                run.MalformedOutcomeTargets++;
            }

            var change = group.Process(parsed.Message, parsed.Timestamp);
            if (change.Kind != GroupChangeKind.None) run.GroupChanges++;
            if (change is { Kind: GroupChangeKind.PetControlled, Member: { } boundPet, Owner: { } petOwner })
            {
                var mechanism = GroupStateTracker.IsOwnedPet(boundPet, petOwner) ? "name"
                    : parsed.Message.EndsWith(" has been charmed.", StringComparison.OrdinalIgnoreCase) ? "charm"
                    : "companion";
                run.PetBindings.Add(new PetBinding(parsed.Timestamp, boundPet, petOwner, mechanism));
            }
            if (parsed.Healing is not null) group.ObserveHealing(parsed.Healing);
            if (parsed.Damage is not null) group.ObserveDamage(parsed.Damage);
            if (parsed.Outcome is not null) group.ObserveOutcome(parsed.Outcome);

            encounter.ApplyGroupChange(change);
            encounter.ProcessMessage(parsed.Timestamp, parsed.Message);
            if (parsed.Damage is not null) encounter.Process(parsed.Damage, group);
            if (parsed.Healing is not null) encounter.ProcessHealing(parsed.Healing, group);
            if (parsed.Outcome is not null) encounter.ProcessOutcome(parsed.Outcome, group);

            if (priorStart.HasValue && encounter.StartedAt != priorStart && priorSnapshot is not null)
            {
                Record(run, archivedStarts, priorSnapshot);
            }
            pipelineWatch.Stop();
        }

        pipelineWatch.Start();
        if (encounter.LastDamageAt is { } last)
        {
            encounter.FinalizeIfInactive(last + TimeSpan.FromMinutes(1));
            ArchiveIfComplete(run, encounter, archivedStarts, last + TimeSpan.FromMinutes(1));
        }
        pipelineWatch.Stop();

        total.Stop();
        run.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        run.ParseMilliseconds = parseWatch.Elapsed.TotalMilliseconds;
        run.PipelineMilliseconds = pipelineWatch.Elapsed.TotalMilliseconds;
        run.TotalMilliseconds = total.Elapsed.TotalMilliseconds;
        return run;
    }

    private static void ArchiveIfComplete(ReplayRun run, EncounterTracker encounter,
        HashSet<DateTime> archivedStarts, DateTime now)
    {
        if (!encounter.IsFinalized || encounter.StartedAt is not { } startedAt ||
            archivedStarts.Contains(startedAt)) return;
        if (encounter.CreateSnapshot(now) is { } snapshot) Record(run, archivedStarts, snapshot);
    }

    private static void Record(ReplayRun run, HashSet<DateTime> archivedStarts, EncounterSnapshot snapshot)
    {
        if (!archivedStarts.Add(snapshot.StartedAt)) return;
        run.Encounters.Add(snapshot);
    }

    private static void TrackSuspectNames(ReplayRun run, ParsedLogLine parsed)
    {
        Inspect(parsed.Damage?.Source);
        Inspect(parsed.Damage?.Target);
        Inspect(parsed.Healing?.Source);
        Inspect(parsed.Healing?.Target);
        Inspect(parsed.Outcome?.Source);
        Inspect(parsed.Outcome?.Target);
        return;

        void Inspect(string? name)
        {
            if (string.IsNullOrEmpty(name) || !IsSuspectName(name)) return;
            run.SuspectNames[name] = run.SuspectNames.GetValueOrDefault(name) + 1;
        }
    }

    // A combatant name should be a creature or player. Anything that reads like a
    // sentence fragment means a regex captured the wrong span.
    private static bool IsSuspectName(string name) =>
        name.StartsWith("on ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("at ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(" for ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(" but ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(" tries ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(" hit ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(" healed ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("  ", StringComparison.Ordinal) ||
        name.Length > 48;

    private static readonly string[] CombatKeywords =
    [
        " damage", "points of", "hit points", "heal", "resist", "absorb", "slain", " died",
        "fizzle", "worn off", "dodge", "parry", "block", "riposte", "rune", "critical",
        "stun", "interrupted", "take hold", "immune", "invulnerable"
    ];

    private static readonly string[] NoiseMarkers =
    [
        " tells ", " says ", " told you", " tell your", "You say", "begins casting",
        "You have looted", "You loot", " auctions", " shouts", "Beginning to memorize",
        " sold it for ", "You gain party experience", "You gain experience"
    ];

    private static bool LooksLikeCombat(string message)
    {
        foreach (var noise in NoiseMarkers)
            if (message.Contains(noise, StringComparison.OrdinalIgnoreCase))
                return false;
        foreach (var keyword in CombatKeywords)
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    private static string Templatize(string message) => Digits.Replace(message, "<N>");

    private static void PrintCoverage(ReplayRun run)
    {
        Console.WriteLine("== parser coverage ==");
        Console.WriteLine($"lines                    : {run.Lines:N0}");
        Console.WriteLine($"envelope failures        : {run.EnvelopeFailures:N0}");
        Console.WriteLine($"damage events            : {run.DamageEvents:N0}");
        Console.WriteLine($"healing events           : {run.HealingEvents:N0}");
        Console.WriteLine($"outcome events           : {run.OutcomeEvents:N0}");
        Console.WriteLine($"group changes            : {run.GroupChanges:N0}");
        Console.WriteLine($"combat-ish, unclassified : {run.UnclassifiedCombatLines:N0}");
        Console.WriteLine();
        Console.WriteLine("== correctness probes ==");
        Console.WriteLine($"friendly-fire damage events : {run.FriendlyFireEvents:N0} " +
                          $"({run.FriendlyFireDamage:N0} damage held out of group output)");
        Console.WriteLine($"malformed outcome targets   : {run.MalformedOutcomeTargets:N0}");
        Console.WriteLine($"encounter snapshot clones   : {run.SnapshotClones:N0}");
        Console.WriteLine($"stun events parsed          : {run.StunApplied:N0} applied, " +
                          $"{run.StunDiminished:N0} diminished");
        Console.WriteLine();
        // A template that still quantifies an amount is a parser gap, not chatter.
        var quantified = run.UnclassifiedSamples
            .Where(item => item.Key.Contains("points of", StringComparison.OrdinalIgnoreCase) ||
                           item.Key.Contains(" hit points", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Value).ToArray();
        Console.WriteLine($"unclassified templates still carrying an amount : {quantified.Length}");
        foreach (var pair in quantified.Take(40))
        {
            Console.WriteLine($"  {pair.Value,6:N0}  {Trim(pair.Key, 110)}");
        }
        Console.WriteLine();
        Console.WriteLine("top unclassified combat templates:");
        foreach (var pair in run.UnclassifiedSamples.OrderByDescending(item => item.Value).Take(80))
        {
            Console.WriteLine($"  {pair.Value,6:N0}  {Trim(pair.Key, 110)}");
        }
        Console.WriteLine();
    }

    private static void PrintSuspects(ReplayRun run)
    {
        Console.WriteLine("== suspect entity names (regex captured a sentence fragment) ==");
        if (run.SuspectNames.Count == 0)
        {
            Console.WriteLine("  none");
        }
        else
        {
            foreach (var pair in run.SuspectNames.OrderByDescending(item => item.Value).Take(25))
            {
                Console.WriteLine($"  {pair.Value,6:N0}  \"{Trim(pair.Key, 96)}\"");
            }
        }
        Console.WriteLine();
    }

    private static void PrintEncounters(ReplayRun run, string player, int top, int minDamage, bool listFights,
        bool chronological = false)
    {
        var filtered = run.Encounters
            .Select(snapshot => new
            {
                Snapshot = snapshot,
                Damage = snapshot.Combatants.Sum(item => item.Damage),
                Seconds = Math.Max(1, (snapshot.EndedAt - snapshot.StartedAt).TotalSeconds)
            })
            .Where(item => item.Damage >= minDamage);
        var ranked = (chronological
                ? filtered.OrderBy(item => item.Snapshot.StartedAt)
                : filtered.OrderByDescending(item => item.Damage))
            .ToArray();

        Console.WriteLine($"== encounters ==");
        Console.WriteLine($"total encounters archived : {run.Encounters.Count:N0}");
        var all = run.Encounters.SelectMany(snapshot => snapshot.Combatants).ToArray();
        Console.WriteLine($"stuns attributed          : {all.Sum(item => item.StunsLanded):N0} landed, " +
                          $"{all.Sum(item => item.StunsTaken):N0} taken");
        foreach (var group in all.Where(item => item.StunsLanded + item.StunsTaken > 0)
                     .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(item => item.Sum(combatant => combatant.StunsLanded + combatant.StunsTaken))
                     .Take(8))
        {
            Console.WriteLine($"  {Trim(group.Key, 28),-28} landed {group.Sum(item => item.StunsLanded),4}  " +
                              $"taken {group.Sum(item => item.StunsTaken),4}");
        }

        Console.WriteLine($"encounters >= {minDamage:N0} dmg  : {ranked.Length:N0}");
        Console.WriteLine();

        if (listFights)
        {
            foreach (var item in run.Encounters.OrderBy(snapshot => snapshot.StartedAt))
            {
                var damage = item.Combatants.Sum(combatant => combatant.Damage);
                var seconds = Math.Max(1, (item.EndedAt - item.StartedAt).TotalSeconds);
                Console.WriteLine(
                    $"{item.StartedAt:MM-dd HH:mm:ss}  {seconds,6:N0}s  {damage,10:N0} dmg  " +
                    $"{item.Combatants.Count,3} combatants  vs {Trim(string.Join(", ", item.Targets.Take(3)), 60)}");
            }
            Console.WriteLine();
        }

        foreach (var item in ranked.Take(top))
        {
            Console.WriteLine($"--- {item.Snapshot.StartedAt:ddd MMM dd HH:mm:ss} .. {item.Snapshot.EndedAt:HH:mm:ss} " +
                              $"({item.Seconds:N0}s, {item.Damage:N0} total) ---");
            Console.WriteLine($"    targets: {Trim(string.Join(", ", item.Snapshot.Targets), 110)}");
            foreach (var combatant in item.Snapshot.Combatants
                         .OrderByDescending(combatant => combatant.Damage).Take(12))
            {
                if (combatant.Damage == 0 && combatant.Healing == 0 && combatant.DamageTaken == 0) continue;
                var owner = string.IsNullOrEmpty(combatant.OwnerName) ? "" : $" (pet of {combatant.OwnerName})";
                var isPlayer = combatant.Name.Equals(player, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                Console.WriteLine(
                    $"  {isPlayer}{Trim(combatant.Name + owner, 34),-34} " +
                    $"{combatant.Damage,10:N0} dmg  {combatant.Damage / item.Seconds,8:N1} dps  " +
                    $"{combatant.Hits,5} hits  {combatant.Misses,4} miss  " +
                    $"{combatant.Healing,8:N0} heal  {combatant.DamageTaken,8:N0} taken  " +
                    $"stun {combatant.StunsLanded,3} landed {combatant.StunsTaken,3} taken");
                var abilities = combatant.Abilities.Values.OrderByDescending(ability => ability.Damage).Take(4);
                var summary = string.Join(", ", abilities.Select(ability => $"{ability.Name} {ability.Damage:N0}"));
                if (summary.Length > 0) Console.WriteLine($"      {Trim(summary, 108)}");
            }
            Console.WriteLine();
        }
    }

    private static void PrintAudit(ReplayRun run, string player)
    {
        Console.WriteLine("== attribution audit ==");
        foreach (var snapshot in run.Encounters)
        {
            foreach (var combatant in snapshot.Combatants)
            {
                var label = $"{snapshot.StartedAt:MM-dd HH:mm:ss} {combatant.Name}";

                // Internal conservation: the headline totals must equal the sum of the
                // breakdowns the UI renders beside them.
                Check(combatant.Damage == combatant.Abilities.Values.Sum(item => item.Damage),
                    $"{label}: damage {combatant.Damage} != ability sum " +
                    $"{combatant.Abilities.Values.Sum(item => item.Damage)}");
                Check(combatant.Damage == combatant.Targets.Values.Sum(item => item.Damage),
                    $"{label}: damage {combatant.Damage} != target sum " +
                    $"{combatant.Targets.Values.Sum(item => item.Damage)}");
                Check(combatant.Healing == combatant.HealingAbilities.Values.Sum(item => item.Damage),
                    $"{label}: healing {combatant.Healing} != heal ability sum " +
                    $"{combatant.HealingAbilities.Values.Sum(item => item.Damage)}");
                Check(combatant.DamageTaken == combatant.IncomingAbilities.Values.Sum(item => item.Damage),
                    $"{label}: taken {combatant.DamageTaken} != incoming ability sum " +
                    $"{combatant.IncomingAbilities.Values.Sum(item => item.Damage)}");
                Check(combatant.Hits >= combatant.MeleeHits + combatant.SpellHits,
                    $"{label}: hits {combatant.Hits} < melee {combatant.MeleeHits} + spell {combatant.SpellHits}");
                foreach (var target in combatant.Targets.Values)
                {
                    Check(target.Damage == target.Abilities.Values.Sum(item => item.Damage),
                        $"{label}: target {target.Name} damage {target.Damage} != ability sum");
                }

                // Provenance: a combatant may only hold a statistic for a role it was
                // actually observed performing in the raw event stream.
                Check(combatant.Damage <= 0 || run.DamageSources.Contains(combatant.Name),
                    $"{label}: credited {combatant.Damage} damage but never appeared as a damage source");
                Check(combatant.Healing <= 0 || run.HealSources.Contains(combatant.Name),
                    $"{label}: credited {combatant.Healing} healing but never appeared as a heal source");
                Check(combatant.DamageTaken <= 0 || run.DamageTargets.Contains(combatant.Name),
                    $"{label}: took {combatant.DamageTaken} damage but never appeared as a damage target");
                Check(!string.Equals(combatant.Name, combatant.OwnerName, StringComparison.OrdinalIgnoreCase),
                    $"{label}: combatant is listed as its own pet owner");

                // Subset counters can never exceed the population they are drawn from.
                Check(combatant.MeleeCriticalHits <= combatant.MeleeHits,
                    $"{label}: melee crits {combatant.MeleeCriticalHits} > melee hits {combatant.MeleeHits}");
                Check(combatant.SpellCriticalHits <= combatant.SpellHits,
                    $"{label}: spell crits {combatant.SpellCriticalHits} > spell hits {combatant.SpellHits}");
                Check(combatant.SpellAbsorbs <= combatant.Absorbed,
                    $"{label}: spell absorbs {combatant.SpellAbsorbs} > absorbs {combatant.Absorbed}");
                Check(combatant.IncomingMeleeHits <= combatant.IncomingHits,
                    $"{label}: incoming melee {combatant.IncomingMeleeHits} > incoming hits {combatant.IncomingHits}");
                Check(combatant.CriticalHeals <= combatant.DirectHeals + combatant.HealOverTimeTicks,
                    $"{label}: crit heals {combatant.CriticalHeals} > heal events");
                Check(combatant.Healing <= combatant.PotentialHealing,
                    $"{label}: healing {combatant.Healing} > potential {combatant.PotentialHealing}");
                Check(combatant.Damage >= 0 && combatant.DamageTaken >= 0 && combatant.Healing >= 0,
                    $"{label}: a negative total was recorded");
            }
        }

        // The tracker is one sequential state machine, so archived fights must form a
        // strictly forward, non-overlapping timeline.
        var ordered = run.Encounters.OrderBy(item => item.StartedAt).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var snapshot = ordered[index];
            Check(snapshot.EndedAt >= snapshot.StartedAt,
                $"encounter {snapshot.StartedAt:MM-dd HH:mm:ss} ends before it starts");
            if (index > 0)
            {
                Check(snapshot.StartedAt >= ordered[index - 1].StartedAt,
                    $"encounter {snapshot.StartedAt:MM-dd HH:mm:ss} is out of order");
                Check(snapshot.StartedAt >= ordered[index - 1].EndedAt,
                    $"encounter {snapshot.StartedAt:MM-dd HH:mm:ss} overlaps the previous fight " +
                    $"ending {ordered[index - 1].EndedAt:HH:mm:ss}");
            }
        }

        Console.WriteLine(run.Violations.Count == 0
            ? "  all conservation and provenance checks passed"
            : $"  {run.Violations.Count:N0} VIOLATIONS:");
        foreach (var violation in run.Violations.Take(25)) Console.WriteLine($"    {violation}");
        Console.WriteLine();

        Console.WriteLine("== pet ownership ==");
        Console.WriteLine($"'has been charmed' lines : {run.CharmLines:N0}");
        Console.WriteLine($"pet bindings recorded    : {run.PetBindings.Count:N0} " +
                          $"(charm {run.PetBindings.Count(item => item.Mechanism == "charm")}, " +
                          $"companion {run.PetBindings.Count(item => item.Mechanism == "companion")}, " +
                          $"name {run.PetBindings.Count(item => item.Mechanism == "name")})");
        foreach (var owner in run.PetBindings.GroupBy(item => item.Owner, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(item => item.Count()))
        {
            var pets = owner.GroupBy(item => item.Pet, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item => item.Count())
                .Select(item => $"{item.Key} x{item.Count()} [{item.First().Mechanism}]");
            Console.WriteLine($"  {Trim(owner.Key, 20),-20} <- {Trim(string.Join(", ", pets), 96)}");
        }

        var petLike = run.Encounters.SelectMany(snapshot => snapshot.Combatants)
            .Where(item => item.Damage > 0 && string.IsNullOrEmpty(item.OwnerName) &&
                           run.PetBindings.Any(binding =>
                               binding.Pet.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Sum(combatant => combatant.Damage))
            .ToArray();
        Console.WriteLine($"known pets credited with damage but no owner in snapshot : {petLike.Length}");
        foreach (var pet in petLike.Take(10))
        {
            Console.WriteLine($"  {Trim(pet.Key, 30),-30} {pet.Sum(item => item.Damage),10:N0} dmg " +
                              $"in {pet.Count()} encounter(s)");
        }
        Console.WriteLine();

        Console.WriteLine("== outgoing damage vs raw log (top encounters) ==");
        foreach (var snapshot in run.Encounters
                     .OrderByDescending(item => item.Combatants.Sum(combatant => combatant.Damage)).Take(6))
        {
            var window = run.RawDamage
                .Where(item => item.Timestamp >= snapshot.StartedAt && item.Timestamp <= snapshot.EndedAt)
                .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(item => item.Key, item => item.Sum(raw => (long)raw.Amount),
                    StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"--- {snapshot.StartedAt:MM-dd HH:mm:ss} .. {snapshot.EndedAt:HH:mm:ss} ---");
            foreach (var combatant in snapshot.Combatants.Where(item => item.Damage > 0)
                         .OrderByDescending(item => item.Damage))
            {
                var raw = window.GetValueOrDefault(combatant.Name);
                var delta = combatant.Damage - raw;
                var flag = delta == 0 ? "exact" : delta < 0 ? $"{-delta:N0} excluded" : $"{delta:N0} EXTRA";
                var star = combatant.Name.Equals(player, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
                Console.WriteLine($" {star}{Trim(combatant.Name, 28),-28} meter {combatant.Damage,9:N0}  " +
                                  $"raw {raw,9:N0}  {flag}");
            }
        }
        Console.WriteLine();

        Console.WriteLine("== healing credited to caster vs raw log (top encounters) ==");
        Console.WriteLine("  a heal must land on the caster's row; 'MISCREDIT' means it did not");
        var healMismatches = 0;
        var takenMismatches = 0;
        foreach (var snapshot in run.Encounters
                     .OrderByDescending(item => item.Combatants.Sum(combatant => combatant.Healing)).Take(8))
        {
            // Healing is accepted for one encounter-timeout past the final damage tick,
            // so the comparison window has to extend the same distance.
            var grace = TimeSpan.FromSeconds(10);
            var byCaster = Window(run.RawHealing, snapshot, raw => raw.Source, grace);
            var byTarget = Window(run.RawHealing, snapshot, raw => raw.Target, grace);
            Console.WriteLine($"--- {snapshot.StartedAt:MM-dd HH:mm:ss} .. {snapshot.EndedAt:HH:mm:ss} ---");
            foreach (var combatant in snapshot.Combatants.Where(item => item.Healing > 0)
                         .OrderByDescending(item => item.Healing))
            {
                var asCaster = byCaster.GetValueOrDefault(combatant.Name);
                var asTarget = byTarget.GetValueOrDefault(combatant.Name);
                var verdict = combatant.Healing == asCaster ? "matches cast total"
                    : combatant.Healing == asTarget ? "MISCREDIT: equals heals RECEIVED"
                    : $"{asCaster - combatant.Healing:N0} excluded";
                if (verdict.StartsWith("MISCREDIT", StringComparison.Ordinal)) healMismatches++;
                Console.WriteLine($"  {Trim(combatant.Name, 26),-26} meter {combatant.Healing,8:N0}  " +
                                  $"cast {asCaster,8:N0}  received {asTarget,8:N0}  {verdict}");
            }
        }
        Console.WriteLine();

        Console.WriteLine("== damage taken vs raw log (top encounters) ==");
        foreach (var snapshot in run.Encounters
                     .OrderByDescending(item => item.Combatants.Sum(combatant => combatant.DamageTaken)).Take(6))
        {
            var byTarget = Window(run.RawDamage, snapshot, raw => raw.Target);
            Console.WriteLine($"--- {snapshot.StartedAt:MM-dd HH:mm:ss} .. {snapshot.EndedAt:HH:mm:ss} ---");
            foreach (var combatant in snapshot.Combatants.Where(item => item.DamageTaken > 0)
                         .OrderByDescending(item => item.DamageTaken))
            {
                var raw = byTarget.GetValueOrDefault(combatant.Name);
                var delta = combatant.DamageTaken - raw;
                if (delta > 0) takenMismatches++;
                var avoidable = combatant.IncomingMeleeHits + combatant.IncomingMisses + combatant.Dodges +
                                combatant.Parries + combatant.Blocks + combatant.Ripostes;
                Console.WriteLine($"  {Trim(combatant.Name, 26),-26} meter {combatant.DamageTaken,8:N0}  " +
                                  $"raw {raw,8:N0}  {(delta == 0 ? "exact" : delta < 0 ? $"{-delta:N0} excluded" : $"{delta:N0} EXTRA"),-16}" +
                                  $"swings {avoidable,5:N0}");
            }
        }
        Console.WriteLine();

        Console.WriteLine("== mitigation counts vs raw log (all encounters) ==");
        Console.WriteLine("  a bucket may lag the raw log (events outside the fight) but must never exceed it");
        var overcounts = new List<string>();
        var checkedBuckets = 0;
        foreach (var snapshot in run.Encounters)
        {
            // Outcomes stay attributable for one timeout past the last damage, because
            // until that elapses the fight may still be running.
            var outcomes = run.RawOutcomes
                .Where(item => item.Timestamp >= snapshot.StartedAt &&
                               item.Timestamp <= snapshot.EndedAt + TimeSpan.FromSeconds(10))
                .ToArray();
            foreach (var combatant in snapshot.Combatants)
            {
                var defended = outcomes.Where(item =>
                    item.Target?.Equals(combatant.Name, StringComparison.OrdinalIgnoreCase) == true).ToArray();
                var attacked = outcomes.Where(item =>
                    item.Source.Equals(combatant.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                Compare("dodge", combatant.Dodges, defended.Count(item => item.Kind == CombatOutcomeKind.DefensiveDodge));
                Compare("parry", combatant.Parries, defended.Count(item => item.Kind == CombatOutcomeKind.DefensiveParry));
                Compare("block", combatant.Blocks, defended.Count(item => item.Kind == CombatOutcomeKind.DefensiveBlock));
                Compare("riposte", combatant.Ripostes, defended.Count(item => item.Kind == CombatOutcomeKind.DefensiveRiposte));
                Compare("absorb", combatant.Absorbed, defended.Count(item =>
                    item.Kind is CombatOutcomeKind.DefensiveAbsorb or CombatOutcomeKind.DefensiveSpellAbsorb));
                Compare("spell resist taken", combatant.IncomingSpellResists,
                    defended.Count(item => item.Kind == CombatOutcomeKind.DefensiveSpellResist));
                Compare("incoming miss", combatant.IncomingMisses,
                    defended.Count(item => item.Kind == CombatOutcomeKind.MissedAttack));
                // Any swing the defender turned aside is a miss for the attacker, so the
                // attacker's bucket spans the plain miss and every defensive result.
                Compare("outgoing miss", combatant.Misses, attacked.Count(item =>
                    item.Kind is CombatOutcomeKind.MissedAttack or CombatOutcomeKind.DefensiveDodge
                        or CombatOutcomeKind.DefensiveParry or CombatOutcomeKind.DefensiveBlock
                        or CombatOutcomeKind.DefensiveRiposte or CombatOutcomeKind.DefensiveAbsorb
                        or CombatOutcomeKind.DefensiveSpellAbsorb));
                Compare("fizzle", combatant.SpellFizzles,
                    attacked.Count(item => item.Kind == CombatOutcomeKind.SpellFizzle));

                void Compare(string bucket, int meter, int raw)
                {
                    checkedBuckets++;
                    if (meter > raw)
                    {
                        overcounts.Add($"{snapshot.StartedAt:MM-dd HH:mm:ss} {combatant.Name}: " +
                                       $"{bucket} meter {meter} > raw {raw}");
                    }
                }
            }
        }

        Console.WriteLine($"buckets compared : {checkedBuckets:N0}");
        Console.WriteLine(overcounts.Count == 0
            ? "  no mitigation bucket exceeds the raw log"
            : $"  {overcounts.Count:N0} OVERCOUNTS:");
        foreach (var overcount in overcounts.Take(20)) Console.WriteLine($"    {overcount}");
        Console.WriteLine();
        Console.WriteLine($"heal miscredits : {healMismatches}");
        Console.WriteLine($"damage-taken overcounts : {takenMismatches}");
        Console.WriteLine($"mitigation overcounts : {overcounts.Count}");
        Console.WriteLine();
        return;

        static Dictionary<string, long> Window(List<RawDamage> events, EncounterSnapshot snapshot,
            Func<RawDamage, string> key, TimeSpan grace = default) =>
            events.Where(item => item.Timestamp >= snapshot.StartedAt &&
                                 item.Timestamp <= snapshot.EndedAt + grace)
                .GroupBy(key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(item => item.Key, item => item.Sum(raw => (long)raw.Amount),
                    StringComparer.OrdinalIgnoreCase);

        void Check(bool condition, string message)
        {
            if (!condition) run.Violations.Add(message);
        }
    }

    private sealed class SpellProbe
    {
        public required BuffRuleSettings Rule { get; init; }
        public required SpellDataEntry Entry { get; init; }
        public long Casts;
        public long SelfAppliedSeen;
        public long OtherAppliedSeen;
        public long FadeSeen;
        public long WornOffSeen;
        public long Activations;
        public bool WasActive;
    }

    /// <summary>
    /// Replays the log a second time through the real BuffTracker, using rules built
    /// from the game's own spell catalog for the spells this character actually casts.
    /// Reports whether casts turn into tracked instances and whether the catalog's
    /// application and fade messages ever appear in the log.
    /// </summary>
    private static void PrintBuffSimulation(string logPath, string player, ReplayRun run)
    {
        Console.WriteLine("== buff / DoT / control tracking simulation ==");
        var catalog = SpellDataCatalog.TryLoadForLog(logPath);
        if (catalog is null)
        {
            Console.WriteLine("  spell catalog unavailable beside the log; skipped");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"catalog entries : {catalog.Count:N0}");
        var probes = new List<SpellProbe>();
        foreach (var (spell, casts) in run.LocalCasts.OrderByDescending(item => item.Value).Take(60))
        {
            if (!catalog.TryFind(spell, out var entry) || entry is null) continue;
            probes.Add(new SpellProbe
            {
                Entry = entry,
                Casts = casts,
                Rule = new BuffRuleSettings(Guid.NewGuid(), entry.Name, 600, 3, true, true,
                    BuffAlertMode.Both, BuffSoundKind.Chime, string.Empty,
                    TrackSelf: true, TrackOthers: true)
            });
            if (probes.Count >= 20) break;
        }

        if (probes.Count == 0)
        {
            Console.WriteLine("  no cast spells resolved against the catalog");
            Console.WriteLine();
            return;
        }

        var tracker = new BuffTracker();
        tracker.Configure(probes.Select(probe => probe.Rule).ToArray(),
            name => catalog.TryFind(name, out var found) ? found!.FadeMessages : [],
            name => catalog.TryFind(name, out var found) ? found!.SelfAppliedMessages : [],
            name => catalog.TryFind(name, out var found) ? found!.OtherAppliedMessageSuffixes : []);

        var parser = new LogLineParser(player);
        var lastSampled = DateTime.MinValue;
        var lastTimestamp = DateTime.MinValue;
        using (var reader = new StreamReader(
                   new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16),
                   Encoding.UTF8))
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || !parser.TryParse(line, out var parsed) || parsed is null) continue;
                var message = parsed.Message;
                lastTimestamp = parsed.Timestamp;

                foreach (var probe in probes)
                {
                    if (probe.Entry.SelfAppliedMessages.Contains(message, StringComparer.OrdinalIgnoreCase))
                        probe.SelfAppliedSeen++;
                    if (probe.Entry.OtherAppliedMessageSuffixes.Any(suffix =>
                            message.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                        probe.OtherAppliedSeen++;
                    if (probe.Entry.FadeMessages.Contains(message, StringComparer.OrdinalIgnoreCase))
                        probe.FadeSeen++;
                    if (message.StartsWith($"Your {probe.Entry.Name} spell has worn off",
                            StringComparison.OrdinalIgnoreCase))
                        probe.WornOffSeen++;
                }

                tracker.Observe(parsed.Timestamp, message);
                tracker.Tick(parsed.Timestamp);

                // Log timestamps have one-second resolution, so sampling on each new
                // second catches every activation and expiry transition.
                if (parsed.Timestamp == lastSampled) continue;
                lastSampled = parsed.Timestamp;
                foreach (var probe in probes)
                {
                    var isActive = tracker.GetSnapshot(probe.Rule.Id, parsed.Timestamp).IsActive;
                    if (isActive && !probe.WasActive) probe.Activations++;
                    probe.WasActive = isActive;
                }
            }
        }

        Console.WriteLine($"{"spell",-28}{"casts",8}{"applied",9}{"fades",8}{"wornoff",9}{"tracked",9}  notes");
        var untrackable = 0;
        var silent = 0;
        foreach (var probe in probes.OrderByDescending(item => item.Casts))
        {
            var applied = probe.SelfAppliedSeen + probe.OtherAppliedSeen;
            var hasMessages = probe.Entry.SelfAppliedMessages.Count > 0 ||
                              probe.Entry.OtherAppliedMessageSuffixes.Count > 0;
            var notes = !hasMessages ? "no application message in catalog"
                : applied == 0 ? "CATALOG MESSAGE NEVER SEEN IN LOG"
                : probe.Activations == 0 ? "CONFIRMED BUT NEVER TRACKED"
                : "ok";
            if (!hasMessages) untrackable++;
            if (hasMessages && applied == 0) silent++;
            Console.WriteLine($"{Trim(probe.Entry.Name, 27),-28}{probe.Casts,8:N0}{applied,9:N0}" +
                              $"{probe.FadeSeen,8:N0}{probe.WornOffSeen,9:N0}{probe.Activations,9:N0}  {notes}");
        }

        var stuck = tracker.GetActiveSnapshots(lastTimestamp);
        Console.WriteLine();
        Console.WriteLine($"spells with no application message : {untrackable}");
        Console.WriteLine($"catalog messages never seen in log : {silent}");
        Console.WriteLine($"instances still active at log end  : {stuck.Count}");
        foreach (var instance in stuck.Take(10))
        {
            Console.WriteLine($"    {Trim(instance.SpellName, 24),-24} on {Trim(instance.TargetName, 24),-24}" +
                              $" overdue={instance.IsOverdue}");
        }
        Console.WriteLine();
    }

    private static void PrintPerformance(ReplayRun run)
    {
        Console.WriteLine("== performance ==");
        Console.WriteLine($"total          : {run.TotalMilliseconds:N0} ms");
        Console.WriteLine($"regex parsing  : {run.ParseMilliseconds:N0} ms " +
                          $"({run.ParseMilliseconds / run.TotalMilliseconds:P0} of total, " +
                          $"{run.ParseMilliseconds * 1000 / Math.Max(1, run.Lines):N1} us/line)");
        Console.WriteLine($"aggregation    : {run.PipelineMilliseconds:N0} ms " +
                          $"({run.PipelineMilliseconds / run.TotalMilliseconds:P0} of total)");
        Console.WriteLine($"throughput     : {run.Lines / Math.Max(1, run.TotalMilliseconds) * 1000:N0} lines/sec");
        Console.WriteLine($"allocated      : {run.AllocatedBytes / 1024d / 1024d:N0} MB " +
                          $"({run.AllocatedBytes / Math.Max(1, run.Lines):N0} bytes/line)");
        Console.WriteLine($"gc collections : gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} " +
                          $"gen2={GC.CollectionCount(2)}");
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "\u2026";

    private static int ReadIntOption(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length &&
               int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static string ResolvePlayerName(string logPath)
    {
        var name = Path.GetFileNameWithoutExtension(logPath);
        var parts = name.Split('_');
        return parts.Length >= 2 ? parts[1] : name;
    }
}
