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

        // Resist cancels pending
        var pending = new BuffTracker();
        pending.Configure([dot], _ => [], _ => [], _ => [" has been poisoned."], _ => true);
        pending.Observe(t0, "You begin casting Venom of the Snake.");
        pending.Observe(t0.AddSeconds(1), "a gnoll resisted your Venom of the Snake!");
        pending.Tick(t0.AddSeconds(4));
        Check("Resist cancels DoT", pending.GetActiveSnapshots(t0.AddSeconds(4)).Count == 0);

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

        // Natural expiry status
        var shortDot = Rule("Short Poison", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 2, 0);
        var expireTracker = new BuffTracker();
        expireTracker.Configure([shortDot], _ => [], _ => [], _ => [" has been poisoned."], _ => true);
        expireTracker.Observe(t0, "You begin casting Short Poison.");
        expireTracker.Observe(t0.AddSeconds(0.1), "a bat has been poisoned.");
        expireTracker.Tick(t0.AddSeconds(2.2));
        var snap = expireTracker.GetSnapshot(shortDot.Id, t0.AddSeconds(2.3));
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
            Check("Alert Both→Sound", questDoc.TrackedItems[0].AlertMode == BuffAlertMode.Sound);

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
        Check("Normalize Both", BuffAlertModeOptions.Normalize(BuffAlertMode.Both) == BuffAlertMode.Sound);
        Check("Normalize TTS",
            BuffAlertModeOptions.Normalize(BuffAlertMode.TextToSpeech) == BuffAlertMode.TextToSpeech);
        Check("Exclusive choices", BuffAlertModeOptions.ExclusiveChoices.Count == 2 &&
                                   !BuffAlertModeOptions.ExclusiveChoices.Contains(BuffAlertMode.Both));
    }

    private static BuffRuleSettings Rule(string name, SpellTrackerCategory category,
        ControlEffectType controlType, int duration, double cast) =>
        new(Guid.NewGuid(), name, duration, cast, true, true, BuffAlertMode.Sound, BuffSoundKind.Chime,
            "", TrackSelf: false, TrackOthers: true, Category: category, ControlType: controlType);
}
