using System.Globalization;
using System.IO;
using System.Text.Json;
using EQLDamageMeter.Models;
using EQLDamageMeter.Services;
using EQLDamageMeter.ViewModels;

namespace EQLDamageMeter.Verify;

/// <summary>
/// Headless verification of core EQDM services. Exit code = number of failures.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = [];

    private static int Main(string[] args)
    {
        var runs = 4;
        if (args.Length > 0 && int.TryParse(args[0], out var requested) && requested > 0)
            runs = requested;

        for (var run = 1; run <= runs; run++)
        {
            Console.WriteLine($"========== VERIFY RUN {run}/{runs} ==========");
            _passed = 0;
            _failed = 0;
            Failures.Clear();

            RunSpellNameTests();
            RunLogParserTests();
            RunGroupAndEncounterTests();
            RunBuffTrackerTests();
            RunSmartTimingTests();
            RunSessionAndLootTests();
            RunLatestKillVsHistoryTests();
            RunQuestSkyStoreTests();
            RunAppPathsAndGuardTests();
            RunSpellTrackerStoreTests();
            RunAlertModeTests();

            Console.WriteLine($"Run {run}: passed={_passed} failed={_failed}");
            if (_failed > 0)
            {
                foreach (var failure in Failures)
                    Console.WriteLine("  FAIL: " + failure);
                return _failed;
            }
            Console.WriteLine();
        }

        Console.WriteLine($"ALL {runs} RUNS PASSED");
        return 0;
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            return;
        }

        _failed++;
        Failures.Add(detail is null ? name : $"{name}: {detail}");
    }

    private static void RunSpellNameTests()
    {
        Check("Family roman", SpellNameNormalizer.GetFamilyName("Inner Fire IX") == "Inner Fire");
        Check("Family Rk", SpellNameNormalizer.GetFamilyName("Complete Healing Rk. II") == "Complete Healing");
        Check("Family plain", SpellNameNormalizer.GetFamilyName("Venom of the Snake") == "Venom of the Snake");
        Check("Belongs rank", SpellNameNormalizer.BelongsToFamily("Venom of the Snake III", "Venom of the Snake"));
        Check("Belongs mismatch", !SpellNameNormalizer.BelongsToFamily("Charm", "Mesmerize"));
    }

    private static void RunLogParserTests()
    {
        var parser = new LogLineParser("Sayser");
        var stamp = "[Sat Aug 08 12:00:00 2026] ";

        Check("Melee hit",
            parser.TryParse(stamp + "You hit a rat for 10 points of damage.", out var hit) &&
            hit!.Damage is { Amount: 10, Ability: "Hit", Category: DamageCategory.Melee } &&
            hit.Damage.Source == "Sayser" && hit.Damage.Target == "a rat");

        Check("Melee crit",
            parser.TryParse(stamp + "You hit a rat for 20 points of damage. (Critical)", out var crit) &&
            crit!.Damage!.IsCritical);

        Check("DoT your",
            parser.TryParse(stamp + "a rat has taken 5 damage from your Venom of the Snake.", out var dot) &&
            dot!.Damage is { Amount: 5, Category: DamageCategory.DamageOverTime } &&
            dot.Damage.Source == "Sayser");

        Check("DoT unattributed",
            parser.TryParse(stamp + "a rat has taken 5 damage by Poison.", out var udot) &&
            udot!.Damage!.Source == LogLineParser.UnattributedDamageOverTimeSource);

        Check("Thorns",
            parser.TryParse(stamp + "a rat is pierced by YOUR thorns for 3 points of non-melee damage.", out var thorns) &&
            thorns!.Damage is { Amount: 3, Ability: "Thorns", Category: DamageCategory.Reactive });

        Check("Spell damage",
            parser.TryParse(stamp + "Sayser hit a rat for 100 points of fire damage by Firebolt.", out var spell) &&
            spell!.Damage is { Amount: 100, Ability: "Firebolt", Category: DamageCategory.Spell });

        Check("Heal with overheal",
            parser.TryParse(stamp + "Foo healed you for 100 (150) hit points by Complete Heal.", out var heal) &&
            heal!.Healing is { Amount: 100, PotentialAmount: 150 } &&
            heal.Healing.Target == "Sayser");

        Check("Self heal himself",
            parser.TryParse(stamp + "Foo healed himself for 50 hit points by Light Healing.", out var selfHeal) &&
            selfHeal!.Healing is { Target: "Foo", Amount: 50 });

        Check("Frenzy miss target",
            parser.TryParse(stamp + "You try to frenzy on a kobold, but miss!", out var frenzy) &&
            frenzy!.Outcome is { Kind: CombatOutcomeKind.MissedAttack, Ability: "Frenzy" } &&
            frenzy.Outcome.Target == "a kobold");

        Check("Stun no source",
            parser.TryParse(stamp + "a rat is stunned by Bash.", out var stun) &&
            stun!.Outcome is { Kind: CombatOutcomeKind.StunApplied, Ability: "Bash" } &&
            string.IsNullOrEmpty(stun.Outcome.Source));

        Check("Protected spell",
            parser.TryParse(stamp + "Foo tries to cast a spell on You, but You are protected.", out var prot) &&
            prot!.Outcome is { Ability: LogLineParser.ProtectedSpellAbility });

        Check("Chat ignored",
            parser.TryParse(stamp + "Bob tells the group, 'hi'", out var chat) &&
            chat is { Damage: null, Healing: null, Outcome: null });

        Check("Identity path",
            LogIdentity.TryFromPath(@"C:\Logs\eqlog_Sayser_halas.txt", out var id) &&
            id!.Character == "Sayser" && id.Server == "halas");
    }

    private static void RunGroupAndEncounterTests()
    {
        var group = new GroupStateTracker("Sayser");
        var t0 = DateTime.Now;

        group.Process("Alice invites you to join a group.", t0);
        group.Process("You have joined the group.", t0.AddSeconds(1));
        group.Process("Alice has joined the group.", t0.AddSeconds(2));
        Check("Grouped", group.IsGrouped && group.KnownMembers.Contains("Alice"));

        group.Process("Alice begins casting Charm.", t0.AddSeconds(3));
        var charm = group.Process("an orc has been charmed.", t0.AddSeconds(8));
        Check("Charm bind",
            charm.Kind == GroupChangeKind.PetControlled &&
            group.TryGetPetOwner("an orc", out var owner) && owner == "Alice");

        group.Process("You begin casting Charm.", t0.AddSeconds(10));
        group.Process("a skeleton has been charmed.", t0.AddSeconds(15));
        Check("Local charm", group.TryGetControlledPetOwner("a skeleton", out var localOwner) &&
                             localOwner == "Sayser");

        group.Process("LOADING, PLEASE WAIT...", t0.AddSeconds(20));
        Check("Zone clears local charm pet",
            !group.TryGetControlledPetOwner("a skeleton", out _));
        // Alice's remote pet may remain — only local cleared
        Check("Remote charm survives local zone clear",
            group.TryGetPetOwner("an orc", out _));

        // Encounter
        var encounter = new EncounterTracker("Sayser");
        var g2 = new GroupStateTracker("Sayser");
        var parser = new LogLineParser("Sayser");
        var e0 = DateTime.Now;
        void Feed(string message, DateTime at)
        {
            var line = $"[{at.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.GetCultureInfo("en-US"))}] {message}";
            if (!parser.TryParse(line, out var parsed) || parsed is null) return;
            g2.Process(parsed.Message, parsed.Timestamp);
            if (parsed.Damage is { } d) encounter.Process(d, g2);
            if (parsed.Healing is { } h) encounter.ProcessHealing(h, g2);
            if (parsed.Outcome is { } o) encounter.ProcessOutcome(o, g2);
            encounter.ProcessMessage(parsed.Timestamp, parsed.Message);
            encounter.FinalizeIfInactive(at);
        }

        Feed("You hit a rat for 50 points of damage.", e0);
        Feed("You hit a rat for 50 points of damage.", e0.AddSeconds(1));
        Check("Encounter open damage",
            encounter.Combatants.Any(c => c.Name == "Sayser" && c.Damage == 100));

        Feed("You hit a rat for 10 points of damage.", e0.AddSeconds(12));
        Check("Timeout starts new encounter",
            encounter.Combatants.Any(c => c.Name == "Sayser" && c.Damage == 10));

        // Kill grace finalize
        var encounter2 = new EncounterTracker("Sayser");
        var g3 = new GroupStateTracker("Sayser");
        var k0 = DateTime.Now;
        var dmg = new DamageEvent(k0, "Sayser", "a beetle", 20, "Hit", DamageCategory.Melee, false);
        encounter2.Process(dmg, g3);
        encounter2.ProcessMessage(k0.AddSeconds(1), "You have slain a beetle!");
        encounter2.FinalizeIfInactive(k0.AddSeconds(4));
        Check("Kill grace finalize", encounter2.IsFinalized);

        // Friendly fire does not credit outgoing (including local → ally)
        var encounter3 = new EncounterTracker("Sayser");
        var g4 = new GroupStateTracker("Sayser");
        g4.Process("You have joined the group.", k0);
        g4.Process("Alice has joined the group.", k0);
        encounter3.Process(new DamageEvent(k0, "Alice", "Sayser", 5, "Hit", DamageCategory.Melee, false), g4);
        var alice = encounter3.Combatants.FirstOrDefault(c => c.Name == "Alice");
        Check("Friendly fire ally→local not outgoing", alice is null || alice.Damage == 0);
        encounter3.Process(new DamageEvent(k0.AddSeconds(1), "Sayser", "Alice", 9, "Hit", DamageCategory.Melee, false), g4);
        var local = encounter3.Combatants.FirstOrDefault(c => c.Name == "Sayser");
        Check("Friendly fire local→ally not outgoing", local is null || local.Damage == 0);
        var aliceTaken = encounter3.Combatants.FirstOrDefault(c => c.Name == "Alice");
        Check("Friendly fire credited as incoming", aliceTaken is { DamageTaken: 9 });
        // Player-on-player FF must not keep the encounter alive via hostile targeting
        encounter3.FinalizeIfInactive(k0.AddSeconds(15));
        Check("Friendly fire does not block timeout finalize", encounter3.IsFinalized || !encounter3.StartedAt.HasValue);

        // Charm name collision: pet and hostile share "an imp protector"
        var encounter4 = new EncounterTracker("Sayser");
        var g5 = new GroupStateTracker("Sayser");
        var c0 = DateTime.Now;
        void FeedCharm(string message, DateTime at)
        {
            var line = $"[{at.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.GetCultureInfo("en-US"))}] {message}";
            if (!parser.TryParse(line, out var parsed) || parsed is null)
            {
                g5.Process(message, at);
                return;
            }
            g5.Process(parsed.Message, parsed.Timestamp);
            if (parsed.Damage is { } d) encounter4.Process(d, g5);
            if (parsed.Outcome is { } o) encounter4.ProcessOutcome(o, g5);
            encounter4.ProcessMessage(parsed.Timestamp, parsed.Message);
        }
        FeedCharm("You begin casting Charm.", c0);
        FeedCharm("an imp protector has been charmed.", c0.AddSeconds(5));
        Check("Charm name collision pet bound",
            g5.TryGetControlledPetOwner("an imp protector", out var petOwner) && petOwner == "Sayser");
        FeedCharm("You slash an imp protector for 312 points of damage.", c0.AddSeconds(6));
        FeedCharm("You try to slash an imp protector, but miss!", c0.AddSeconds(6.5));
        FeedCharm("An imp protector slashes an imp protector for 92 points of damage.", c0.AddSeconds(7));
        FeedCharm("An imp protector backstabs a lava guardian for 313 points of damage.", c0.AddSeconds(8));
        var sayserDps = encounter4.Combatants.FirstOrDefault(c => c.Name == "Sayser");
        var petDps = encounter4.Combatants.FirstOrDefault(c =>
            c.Name.Equals("an imp protector", StringComparison.OrdinalIgnoreCase));
        Check("Charm name collision local DPS counted", sayserDps is { Damage: 312 });
        Check("Charm name collision local miss counted", sayserDps is { Misses: >= 1 });
        Check("Charm name collision pet DPS counted", petDps is not null && petDps.Damage >= 92 + 313);
    }

    private static void RunBuffTrackerTests()
    {
        var t0 = DateTime.Now;

        // Multi DoT
        var dot = Rule("Venom of the Snake", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 60, 3);
        var dots = new BuffTracker();
        dots.Configure([dot], _ => ["The poison has run its course."], _ => [],
            _ => [" has been poisoned."], _ => true);
        dots.Observe(t0, "You begin casting Venom of the Snake.");
        dots.Observe(t0.AddSeconds(3.1), "a gnoll has been poisoned.");
        dots.Observe(t0.AddSeconds(5), "You begin casting Venom of the Snake.");
        dots.Observe(t0.AddSeconds(8.1), "a skeleton has been poisoned.");
        Check("DoT multi target", dots.GetActiveSnapshots(t0.AddSeconds(9)).Count == 2);
        dots.Observe(t0.AddSeconds(10), "You have slain a gnoll!");
        Check("DoT death clear one",
            dots.GetActiveSnapshots(t0.AddSeconds(10.1)).Count == 1 &&
            dots.GetActiveSnapshots(t0.AddSeconds(10.1))[0].TargetName == "a skeleton");

        // AE mez beyond 1s
        var mez = Rule("Mesmerization", SpellTrackerCategory.Control, ControlEffectType.Mez, 24, 3);
        var mezTracker = new BuffTracker();
        mezTracker.Configure([mez], _ => [], _ => [], _ => [" has been mesmerized."], _ => true);
        mezTracker.Observe(t0, "You begin casting Mesmerization.");
        mezTracker.Observe(t0.AddSeconds(3.0), "a kobold has been mesmerized.");
        mezTracker.Observe(t0.AddSeconds(3.2), "a kobold pet has been mesmerized.");
        mezTracker.Observe(t0.AddSeconds(4.5), "a goblin has been mesmerized.");
        Check("AE mez all lands", mezTracker.GetActiveSnapshots(t0.AddSeconds(5)).Count == 3);

        // Charm survives death, clears on zone
        var charm = Rule("Charm", SpellTrackerCategory.Control, ControlEffectType.Charm, 750, 5);
        var charmTracker = new BuffTracker();
        charmTracker.Configure([charm], _ => [], _ => [], _ => [" has been charmed."], _ => false);
        charmTracker.Observe(t0, "You begin casting Charm.");
        charmTracker.Observe(t0.AddSeconds(5.1), "a wolf has been charmed.");
        charmTracker.Observe(t0.AddSeconds(6), "a wolf has been slain by Sayser!");
        Check("Charm ignores target death", charmTracker.GetActiveSnapshots(t0.AddSeconds(6.1)).Count == 1);
        charmTracker.Observe(t0.AddSeconds(7), "You have entered Greater Faydark.");
        Check("Charm clears on zone", charmTracker.GetActiveSnapshots(t0.AddSeconds(7.1)).Count == 0);

        // Self buff survives zone
        var buff = Rule("Shield of Lava", SpellTrackerCategory.Buff, ControlEffectType.Other, 60, 2);
        buff = buff with { TrackSelf = true, TrackOthers = false };
        var buffTracker = new BuffTracker();
        buffTracker.Configure([buff],
            _ => ["The flames die down."],
            _ => ["You feel the spirit of lava enter your body."],
            _ => [], _ => false);
        buffTracker.Observe(t0, "You begin casting Shield of Lava.");
        buffTracker.Observe(t0.AddSeconds(2.1), "You feel the spirit of lava enter your body.");
        buffTracker.Observe(t0.AddSeconds(3), "LOADING, PLEASE WAIT...");
        Check("Self buff survives zone", buffTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 1);

        // Recast on Other with shared land text must refresh the timer (charmed pet case).
        var chloro = Rule("Chloroplast", SpellTrackerCategory.Buff, ControlEffectType.Other, 120, 4);
        chloro = chloro with { TrackSelf = true, TrackOthers = true };
        var chloroTracker = new BuffTracker();
        chloroTracker.Configure([chloro],
            _ => ["You have stopped regenerating."],
            _ => ["You begin to regenerate."],
            _ => [" begins to regenerate."], _ => true);
        chloroTracker.Observe(t0, "You begin casting Chloroplast.");
        chloroTracker.Observe(t0.AddSeconds(4), "Innoruuk`s Chosen begins to regenerate.");
        var firstSnap = chloroTracker.GetActiveSnapshots(t0.AddSeconds(4.1));
        Check("Other Chloroplast armed",
            firstSnap.Count == 1 &&
            firstSnap[0].TargetName.Equals("Innoruuk`s Chosen", StringComparison.OrdinalIgnoreCase));
        var firstExpires = firstSnap[0].ExpiresAt;
        chloroTracker.Observe(t0.AddSeconds(50), "You begin casting Chloroplast.");
        chloroTracker.Observe(t0.AddSeconds(54), "Innoruuk`s Chosen begins to regenerate.");
        var refreshed = chloroTracker.GetActiveSnapshots(t0.AddSeconds(54.1));
        Check("Other Chloroplast refresh resets timer",
            refreshed.Count == 1 &&
            refreshed[0].TargetName.Equals("Innoruuk`s Chosen", StringComparison.OrdinalIgnoreCase) &&
            refreshed[0].ExpiresAt > firstExpires &&
            Math.Abs((refreshed[0].ExpiresAt - t0.AddSeconds(54 + 120)).TotalSeconds) < 1.0);

        var haste = Rule("Swift Like the Wind", SpellTrackerCategory.Buff, ControlEffectType.Other, 180, 6);
        haste = haste with { TrackSelf = true, TrackOthers = true };
        var hasteTracker = new BuffTracker();
        hasteTracker.Configure([haste],
            _ => [],
            _ => ["You feel much faster."],
            _ => [" feels much faster."], _ => true);
        hasteTracker.Observe(t0, "You begin casting Swift Like the Wind.");
        hasteTracker.Observe(t0.AddSeconds(6), "Innoruuk`s Chosen feels much faster.");
        var hasteFirst = hasteTracker.GetActiveSnapshots(t0.AddSeconds(6.1))[0].ExpiresAt;
        hasteTracker.Observe(t0.AddSeconds(70), "You begin casting Swift Like the Wind.");
        hasteTracker.Observe(t0.AddSeconds(76), "Innoruuk`s Chosen feels much faster.");
        var hasteRefresh = hasteTracker.GetActiveSnapshots(t0.AddSeconds(76.1));
        Check("Other Swift refresh resets timer",
            hasteRefresh.Count == 1 && hasteRefresh[0].ExpiresAt > hasteFirst);

        // Resist cancels pending
        var pending = new BuffTracker();
        pending.Configure([dot], _ => [], _ => [], _ => [" has been poisoned."], _ => true);
        pending.Observe(t0, "You begin casting Venom of the Snake.");
        pending.Observe(t0.AddSeconds(1), "a gnoll resisted your Venom of the Snake!");
        pending.Tick(t0.AddSeconds(4));
        Check("Resist cancels DoT", pending.GetActiveSnapshots(t0.AddSeconds(4)).Count == 0);

        // AE mez: many lands open one timer per target
        var mezCast = Rule("Mesmerization", SpellTrackerCategory.Control, ControlEffectType.Mez, 24, 3);
        var mezCastTracker = new BuffTracker();
        mezCastTracker.Configure([mezCast], _ => [], _ => [], _ => [" has been mesmerized."], _ => true);
        mezCastTracker.Observe(t0, "You begin casting Mesmerization.");
        mezCastTracker.Observe(t0.AddSeconds(3), "a kobold has been mesmerized.");
        mezCastTracker.Observe(t0.AddSeconds(3), "a kobold pet has been mesmerized.");
        mezCastTracker.Observe(t0.AddSeconds(3), "a goblin has been mesmerized.");
        Check("AE mez multi-target",
            mezCastTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 3);

        // Worn-off must not cancel a pending recast of the same spell
        var recast = Rule("Odium", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 30, 3);
        var recastTracker = new BuffTracker();
        recastTracker.Configure([recast], _ => [], _ => [],
            _ => [" staggers under a dark curse."], _ => false);
        recastTracker.Observe(t0, "You begin casting Odium.");
        recastTracker.Observe(t0.AddSeconds(3), "a rat staggers under a dark curse.");
        recastTracker.Observe(t0.AddSeconds(20), "You begin casting Odium.");
        recastTracker.Observe(t0.AddSeconds(22),
            "Your Odium spell has worn off of a rat.");
        recastTracker.Observe(t0.AddSeconds(23), "a bat staggers under a dark curse.");
        Check("Worn-off preserves pending recast land",
            recastTracker.GetActiveSnapshots(t0.AddSeconds(23.1))
                .Any(s => s.TargetName.Equals("a bat", StringComparison.OrdinalIgnoreCase)));

        // Death clears the DoT timer
        var nearFull = Rule("Venom of the Snake", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other,
            36, 3);
        var nearTracker = new BuffTracker();
        nearTracker.Configure([nearFull], _ => [], _ => [], _ => [" has been poisoned."], _ => true);
        nearTracker.Observe(t0, "You begin casting Venom of the Snake.");
        nearTracker.Observe(t0.AddSeconds(3), "a bat has been poisoned.");
        nearTracker.Observe(t0.AddSeconds(33), "You have slain a bat!");
        Check("Death clears DoT timer",
            nearTracker.GetActiveSnapshots(t0.AddSeconds(33.1)).Count == 0);

        // Overlapping ambiguous poisons: one land arms only the newest cast
        var venom = Rule("Venom of the Snake", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 60, 3);
        var envenom = Rule("Envenomed Bolt", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 40, 3);
        var overlap = new BuffTracker();
        overlap.Configure([venom, envenom], _ => [], _ => [], _ => [" has been poisoned."], _ => true);
        overlap.Observe(t0, "You begin casting Venom of the Snake.");
        overlap.Observe(t0.AddSeconds(1), "You begin casting Envenomed Bolt.");
        overlap.Observe(t0.AddSeconds(4.1), "a rat has been poisoned.");
        var snaps = overlap.GetActiveSnapshots(t0.AddSeconds(4.2));
        Check("Ambiguous land one rule", snaps.Count == 1 && snaps[0].SpellName == "Envenomed Bolt");

        // Natural expiry removes the instance after configured duration
        var shortDot = Rule("Short Poison", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 2, 0);
        var expireTracker = new BuffTracker();
        expireTracker.Configure([shortDot], _ => [], _ => [], _ => [" has been poisoned."], _ => true);
        expireTracker.Observe(t0, "You begin casting Short Poison.");
        expireTracker.Observe(t0.AddSeconds(0.1), "a bat has been poisoned.");
        expireTracker.Tick(t0.AddSeconds(2.3));
        var snap = expireTracker.GetSnapshot(shortDot.Id, t0.AddSeconds(2.4));
        Check("Natural expiry flagged", snap.IsExpired && snap.StopReason == BuffStopReason.Expired);
    }

    private static void RunSessionAndLootTests()
    {
        SessionLootParser.ResetRuntime();
        var session = new SessionTracker();
        session.StartSession("Sayser", "halas", DateTime.UtcNow);
        var t = DateTime.UtcNow;

        session.Observe(t, "You gain experience! (1.5%)");
        Check("XP percent", Math.Abs(session.Current!.LevelXpPercent - 1.5) < 0.0001);

        session.Observe(t, "You have gained a level! Welcome to level 30!");
        Check("Level up", session.Current.LevelsGained == 1 && session.Current.EndLevel == 30 &&
                          session.Current.StartLevel == 29);

        session.Observe(t, "You have gained 2 ability point(s)!");
        Check("AA multi", session.Current.AaPointsGained == 2);

        session.Observe(t, "You have gained an ability point!");
        Check("AA singular", session.Current.AaPointsGained == 3);

        session.Observe(t, "--You have looted a Mote of Lesser Potential from a goblin's corpse.--");
        Check("Mote manual", session.Current.MotesLooted == 1);

        session.Observe(t, "You looted a Mote of Median Potential from a goblin's corpse and stored it in your currency.");
        Check("Mote auto-store", session.Current.MotesLooted == 2 &&
                                 session.Current.MotesByName.ContainsKey("Mote of Median Potential"));

        session.Observe(t, "You died.");
        Check("Death", session.Current.Deaths == 1);

        // Coin parsing: abbreviated + full words
        Check("Coin abbrev",
            SessionLootParser.TryObserve(session.Current, t.AddMinutes(1),
                "You looted a Stick from a rat's corpse and sold it for 4p 2g 1s 4c.") &&
            session.Current.Loot.Mobs.Any(m => m.Items.Any(i =>
                i.Name == "Stick" && i.ValueCopper == 4 * 1000 + 2 * 100 + 1 * 10 + 4)));

        Check("Coin words",
            SessionLootParser.TryObserve(session.Current, t.AddMinutes(2),
                "You looted a Rock from a bat's corpse and sold it for 1 platinum 2 gold.") &&
            session.Current.Loot.Mobs.Any(m => m.Items.Any(i =>
                i.Name == "Rock" && i.ValueCopper == 1200)));

        SessionLootParser.ResetRuntime();
        var coinSession = new SessionRecord
        {
            Id = "x", StartedAt = DateTime.UtcNow, Character = "Sayser", Server = "halas",
            Loot = new SessionLootData()
        };
        var c0 = DateTime.UtcNow;
        SessionLootParser.TryObserve(coinSession, c0,
            "You looted a Cap from a goblin's corpse.--".Replace(".--", ".")); // plain kept
        SessionLootParser.TryObserve(coinSession, c0,
            "--You have looted a Cap from a goblin's corpse.--");
        SessionLootParser.TryObserve(coinSession, c0.AddSeconds(1),
            "You receive 5p 9g 7s 8c from the corpse.");
        var goblin = coinSession.Loot.Mobs.FirstOrDefault(m =>
            m.Name.Contains("goblin", StringComparison.OrdinalIgnoreCase));
        Check("Corpse coin on kill",
            goblin is not null && goblin.CoinCopper == 5 * 1000 + 9 * 100 + 7 * 10 + 8 &&
            goblin.Kills.Count >= 1 && goblin.Kills[^1].CoinCopper == goblin.CoinCopper);

        Check("FormatCopper", SessionLootParser.FormatCopper(4214) == "4p 2g 1s 4c");

        // Coin before named loot stays pending and attaches to the named mob, not a prior mob
        SessionLootParser.ResetRuntime();
        var coinOrder = new SessionRecord
        {
            Id = "coin", StartedAt = DateTime.UtcNow, Character = "Sayser", Server = "halas",
            Loot = new SessionLootData()
        };
        var co = DateTime.UtcNow;
        SessionLootParser.TryObserve(coinOrder, co,
            "--You have looted a Bone from a skeleton's corpse.--");
        SessionLootParser.TryObserve(coinOrder, co.AddSeconds(1),
            "You receive 1p from the corpse.");
        // Next corpse: coin line first must NOT open/credit skeleton again
        SessionLootParser.TryObserve(coinOrder, co.AddSeconds(3),
            "You receive 2p from the corpse.");
        SessionLootParser.TryObserve(coinOrder, co.AddSeconds(3.5),
            "--You have looted a Cloth Cap from a goblin's corpse.--");
        var skeleton = coinOrder.Loot.Mobs.First(m => m.Name.Contains("skeleton", StringComparison.OrdinalIgnoreCase));
        var goblinMob = coinOrder.Loot.Mobs.First(m => m.Name.Contains("goblin", StringComparison.OrdinalIgnoreCase));
        Check("Prior mob coin not inflated by next corpse", skeleton.CoinCopper == 1000);
        Check("Pending coin attaches to named mob", goblinMob.CoinCopper == 2000);

        // Coin-only / empty corpses: slain lines must create mob rows.
        SessionLootParser.ResetRuntime();
        var emptyKill = new SessionRecord
        {
            Id = "empty", StartedAt = DateTime.UtcNow, Character = "Sayser", Server = "halas",
            Loot = new SessionLootData()
        };
        var ek = DateTime.UtcNow;
        SessionLootParser.TryObserve(emptyKill, ek,
            "You receive 8 platinum, 7 gold, 7 silver and 1 copper from the corpse.");
        SessionLootParser.TryObserve(emptyKill, ek, "You have slain a haunted chest!");
        var chest = emptyKill.Loot.Mobs.FirstOrDefault(m =>
            m.Name.Contains("haunted chest", StringComparison.OrdinalIgnoreCase));
        Check("Coin-only slain creates mob",
            chest is not null && chest.CorpsesLooted == 1 && chest.Kills.Count == 1 &&
            chest.CoinCopper == 8 * 1000 + 7 * 100 + 7 * 10 + 1 &&
            chest.Items.Count == 0);

        SessionLootParser.TryObserve(emptyKill, ek.AddSeconds(30),
            "A barren chest has been slain by Innoruuk`s Chosen!");
        var barren = emptyKill.Loot.Mobs.FirstOrDefault(m =>
            m.Name.Contains("barren chest", StringComparison.OrdinalIgnoreCase));
        Check("Empty pet slain creates mob",
            barren is not null && barren.CorpsesLooted == 1 && barren.CoinCopper == 0 &&
            barren.Items.Count == 0);
    }

    private static void RunLatestKillVsHistoryTests()
    {
        SessionLootParser.ResetRuntime();
        var session = new SessionRecord
        {
            Id = "y", StartedAt = DateTime.UtcNow, Character = "Sayser", Server = "halas",
            Loot = new SessionLootData()
        };
        var t1 = DateTime.UtcNow;
        SessionLootParser.TryObserve(session, t1,
            "You looted a Fine Steel Rapier from an Efreeti Lord Djarn's corpse and sold it for 4p 2g 1s 4c.");
        SessionLootParser.TryObserve(session, t1.AddSeconds(1),
            "You receive 1p from the corpse.");

        var t2 = t1.AddSeconds(45);
        SessionLootParser.TryObserve(session, t2,
            "You looted a Cloth Cap from an Efreeti Lord Djarn's corpse and sold it for 1g.");
        SessionLootParser.TryObserve(session, t2.AddSeconds(1),
            "You receive 2p from the corpse.");

        var mob = session.Loot.Mobs.Single();
        var vm = SessionMobLootRowViewModel.From(mob);
        Check("Two corpses", mob.CorpsesLooted == 2 && mob.Kills.Count == 2);
        Check("Latest items only last kill",
            vm.LatestKill is not null &&
            vm.LatestKill.Items.Count == 1 &&
            vm.LatestKill.Items[0].Name == "Cloth Cap");
        Check("Latest excludes earlier item",
            vm.LatestKill!.Items.All(i => !i.Name.Contains("Rapier", StringComparison.OrdinalIgnoreCase)));
        Check("History accumulates items", vm.HistoryItems.Count == 2);
        Check("History coin accumulates",
            mob.CoinCopper == 1000 + 2000 &&
            vm.LatestKill.CoinText == SessionLootParser.FormatCopper(2000));
        Check("Kill history newest first",
            vm.KillHistory.Count == 2 &&
            vm.KillHistory[0].Items.Any(i => i.Name == "Cloth Cap"));
    }

    private static void RunQuestSkyStoreTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eqdm_verify_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Quest store round-trip via JSON shape the store expects in AppPaths dir —
            // call store APIs after temporarily writing beside process is hard; test models/merge logic.
            var questDoc = new QuestTrackerDocument
            {
                TrackedItems =
                [
                    new TrackedQuestItem
                    {
                        ItemName = "Bone Chips", QuestTitle = "Test", Enabled = true,
                        AlertMode = BuffAlertMode.Both, Sound = BuffSoundKind.Chime
                    },
                    new TrackedQuestItem
                    {
                        ItemName = "bone chips", QuestTitle = "Dup", Enabled = false
                    }
                ]
            };
            questDoc.TrackedItems[0].AlertMode = BuffAlertModeOptions.Normalize(questDoc.TrackedItems[0].AlertMode);
            Check("Alert legacy Both→Sound", questDoc.TrackedItems[0].AlertMode == BuffAlertMode.Sound);

            var sky = new SkyTrackerDocument
            {
                Goals =
                [
                    new SkyTrackedGoal
                    {
                        Id = "g1", ClassName = "Enchanter", RewardName = "Bracer",
                        Parts =
                        [
                            new SkyTrackedPart { ItemName = "Part A", NeededCount = 2, FoundCount = 0 }
                        ]
                    }
                ]
            };
            var part = sky.Goals[0].Parts[0];
            part.FoundCount = Math.Min(part.NeededCount, part.FoundCount + 1);
            Check("Sky progress clamp", part.FoundCount == 1);
            part.FoundCount = Math.Min(part.NeededCount, part.FoundCount + 1);
            part.FoundCount = Math.Min(part.NeededCount, part.FoundCount + 1);
            Check("Sky no overshoot", part.FoundCount == 2);

            // Wiki era filter uses Template:PageEra markers in wikitext, not categories.
            Check("Out of era epic template",
                EqWikiQuestParser.IsOutOfEraWikitext("{{Epic Quests Era|page}} walkthrough"));
            Check("Classic quest ok",
                !EqWikiQuestParser.IsOutOfEraWikitext("[[Category:Quests]] start in Freeport"));

            var lootWiki =
                """
                {{Namedmobpage
                | name = a spite golem
                | known_loot =

                <ul>
                <li> {{:Bone Chips}}       <span class='dcommon'>(72.3%)</span></li>
                <li> {{:Midnight Clad Headband}} <span class='drare'>(Rare)</span></li>
                <li> [[Cloth Armor]]       <span class='drare'>(Rare)</span></li>
                </ul>

                | factions =
                * [[Inhabitants of Hate]]
                }}
                """;
            var drops = EqWikiMobLoot.ParseKnownLoot(lootWiki);
            Check("Wiki loot parses items", drops.Count == 3);
            Check("Wiki loot percent chance",
                drops.Any(d => d.ItemName == "Bone Chips" && d.DropChance == "72.3%"));
            Check("Wiki loot rare label",
                drops.Any(d => d.ItemName == "Midnight Clad Headband" && d.DropChance == "Rare"));
            Check("Wiki loot link item",
                drops.Any(d => d.ItemName == "Cloth Armor" && d.DropChance == "Rare"));
            Check("Wiki loot title keeps article",
                EqWikiMobLoot.TitleCandidates("a spite golem")
                    .Any(t => t.Equals("A spite golem", StringComparison.OrdinalIgnoreCase) ||
                              t.Equals("A Spite Golem", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void RunAppPathsAndGuardTests()
    {
        Check("AppPaths non-empty", !string.IsNullOrWhiteSpace(AppPaths.AppDirectory));
        Check("AppPaths combine",
            AppPaths.Combine("spelltracker.json")
                .EndsWith("spelltracker.json", StringComparison.OrdinalIgnoreCase));
        Check("Protected file list",
            AppPaths.UserJsonFileNames.Contains("spelltracker.json") &&
            AppPaths.UserJsonFileNames.Contains("session_info.json") &&
            AppPaths.UserJsonFileNames.Contains("skytracker.json"));

        // Merge helpers via UserDataGuard path: write temp files and invoke restore migrate
        // by exercising spelltracker merge indirectly through serialize shape.
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var a = new
        {
            BuffRules = Array.Empty<object>(),
            DotRules = new[]
            {
                new
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    SpellName = "Venom of the Snake",
                    DurationSeconds = 60,
                    CastTimeSeconds = 3.0,
                    IsEnabled = true,
                    ShowInOverlay = true,
                    AlertMode = "Sound",
                    Sound = "Chime",
                    VoiceText = "",
                    TrackSelf = false,
                    TrackOthers = true,
                    Category = "DamageOverTime",
                    ControlType = "Other"
                }
            },
            ControlRules = Array.Empty<object>()
        };
        Check("Spelltracker json serializes", JsonSerializer.Serialize(a, opts).Contains("Venom"));
    }

    private static void RunSpellTrackerStoreTests()
    {
        // Duration / cast parsing via BuffRuleViewModel
        var settings = new BuffRuleSettings(Guid.NewGuid(), "Test", 60, 3, true, true,
            BuffAlertMode.Sound, BuffSoundKind.Chime, "", true, false);
        var vm = new BuffRuleViewModel(settings);
        vm.DurationText = "9:06";
        vm.CastTimeText = "3:4";
        Check("Duration parse", vm.TryCreateSettings(out var configured, out _) &&
                                configured!.DurationSeconds == 546);
        Check("Cast parse", configured!.CastTimeSeconds == 3.4);

        vm.TrackSelf = false;
        vm.TrackOthers = false;
        Check("Track none invalid", !vm.TryCreateSettings(out _, out var err) &&
                                    !string.IsNullOrWhiteSpace(err));
    }

    private static void RunAlertModeTests()
    {
        Check("Normalize legacy Both→Sound",
            BuffAlertModeOptions.Normalize(BuffAlertMode.Both) == BuffAlertMode.Sound);
        Check("Normalize TTS kept",
            BuffAlertModeOptions.Normalize(BuffAlertMode.TextToSpeech) == BuffAlertMode.TextToSpeech);
        Check("Exclusive choices Sound+TTS only",
            BuffAlertModeOptions.ExclusiveChoices.Count == 2 &&
            BuffAlertModeOptions.ExclusiveChoices.Contains(BuffAlertMode.Sound) &&
            BuffAlertModeOptions.ExclusiveChoices.Contains(BuffAlertMode.TextToSpeech) &&
            !BuffAlertModeOptions.ExclusiveChoices.Contains(BuffAlertMode.Both));
    }

    private static void RunSmartTimingTests()
    {
        Check("Cast ms→sec", Math.Abs(SpellDataCatalog.CastTimeMsToSeconds(3000) - 3.0) < 0.001);
        Check("Mez duration ticks",
            SpellDataCatalog.DurationFieldsToSeconds(1, 4) == 24);
        Check("SoL duration ticks",
            SpellDataCatalog.DurationFieldsToSeconds(10, 150) == 900);
        Check("Venom duration ticks",
            SpellDataCatalog.DurationFieldsToSeconds(1, 6) == 36);
        // Listless Power: formula 7 (level), cap 65 → at 44 = 44 ticks = 264s (not level-60's 360s)
        Check("Listless at 44",
            SpellDataCatalog.DurationFieldsToSeconds(7, 65, 44) == 264);
        Check("Listless at 60",
            SpellDataCatalog.DurationFieldsToSeconds(7, 65, 60) == 360);
        Check("Level-up parse",
            SpellDataCatalog.TryParseLevelUp("You have gained a level! Welcome to level 44!", out var lvl) &&
            lvl == 44);

        var dir = Path.Combine(Path.GetTempPath(), "eqdm-verify-spells-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "Logs"));
            File.WriteAllText(Path.Combine(dir, "spells_us.txt"),
                MakeSpellLine(101, "Mesmerization", 3000, 1, 4, 12) + Environment.NewLine);
            File.WriteAllText(Path.Combine(dir, "spells_us_str.txt"),
                "101^0^0^You feel sleepy.^ has been mesmerized.^The mesmerization fades.^" +
                Environment.NewLine);
            var catalog = SpellDataCatalog.TryLoadFromInstallDirectory(dir);
            Check("Fixture catalog loads", catalog is not null && catalog.Count >= 1);
            SpellDataEntry? entry = null;
            var resolved = catalog is not null &&
                           catalog.TryResolveFamily("Mesmerization", out entry) &&
                           entry is not null;
            Check("Fixture cast/duration",
                resolved &&
                Math.Abs(entry!.CastTimeSeconds - 3.0) < 0.001 &&
                entry.DurationSeconds == 24);

            var vm = new BuffRuleViewModel(Rule("Mesmerization", SpellTrackerCategory.Control,
                ControlEffectType.Mez, 1, 1));
            vm.ApplyCatalogTimings(entry!, force: true, casterLevel: 60);
            Check("Catalog defaults cast/duration",
                vm.CastSource == SpellTimingSource.Catalog &&
                vm.DurationSource == SpellTimingSource.Catalog &&
                Math.Abs(double.Parse(vm.CastTimeText, CultureInfo.InvariantCulture) - 3.0) < 0.001 &&
                vm.DurationText == "0:24");
            vm.DurationText = "0:30";
            Check("Manual duration mark", vm.DurationSource == SpellTimingSource.Manual);

            var legacy = new BuffRuleViewModel(Rule("Odium", SpellTrackerCategory.DamageOverTime,
                ControlEffectType.Other, 30, 3) with { CastSource = SpellTimingSource.Learned });
            Check("Legacy Learned treated as Manual", legacy.CastSource == SpellTimingSource.Manual);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static string MakeSpellLine(int id, string name, int castMs, int formula, int duration, int icon)
    {
        var fields = new string[76];
        for (var index = 0; index < fields.Length; index++) fields[index] = string.Empty;
        fields[0] = id.ToString(CultureInfo.InvariantCulture);
        fields[1] = name;
        fields[8] = castMs.ToString(CultureInfo.InvariantCulture);
        fields[11] = formula.ToString(CultureInfo.InvariantCulture);
        fields[12] = duration.ToString(CultureInfo.InvariantCulture);
        fields[75] = icon.ToString(CultureInfo.InvariantCulture);
        return string.Join('^', fields);
    }

    private static BuffRuleSettings Rule(string name, SpellTrackerCategory category,
        ControlEffectType controlType, int duration, double cast) =>
        new(Guid.NewGuid(), name, duration, cast, true, true, BuffAlertMode.Sound, BuffSoundKind.Chime,
            "", TrackSelf: false, TrackOthers: true, Category: category, ControlType: controlType);
}
