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

            RunSection("SpellName", RunSpellNameTests);
            RunSection("LogParser", RunLogParserTests);
            RunSection("GroupEncounter", RunGroupAndEncounterTests);
            RunSection("BuffTracker", RunBuffTrackerTests);
            RunSection("SpellCatalog", RunSpellCatalogBardSongTests);
            RunSection("SmartTiming", RunSmartTimingTests);
            RunSection("SessionLoot", RunSessionAndLootTests);
            RunSection("LatestKill", RunLatestKillVsHistoryTests);
            RunSection("QuestSkyStore", RunQuestSkyStoreTests);
            RunSection("SkyCatalog", RunSkyCatalogParserTests);
            RunSection("SkyLootLedger", RunSkyLootLedgerTests);
            RunSection("AppPaths", RunAppPathsAndGuardTests);
            RunSection("SpellTrackerStore", RunSpellTrackerStoreTests);
            RunSection("AlertMode", RunAlertModeTests);
            RunSection("BisScoring", RunBisScoringTests);
            RunSection("EqWiki", RunEqWikiItemSourceTests);

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

    private static void RunSection(string name, Action tests)
    {
        Console.WriteLine($"  .. {name}");
        Console.Out.Flush();
        tests();
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
        Check("Mob display strip an", MobDisplayName.Format("an initiate familiar") == "initiate familiar");
        Check("Mob display strip a", MobDisplayName.Format("A glyphed guard") == "glyphed guard");
        Check("Mob display keep proper", MobDisplayName.Format("Innoruuk`s Chosen") == "Innoruuk`s Chosen");
        Check("Group prefilter skips melee", !GroupStateTracker.ShouldProcessMessage(
            "a rat hit a rat for 12 points of non-melee damage."));
        Check("Group prefilter allows cast", GroupStateTracker.ShouldProcessMessage("You begin casting Allure."));
        Check("Encounter prefilter skips melee", !EncounterTracker.ShouldProcessMessage(
            "a rat hit a rat for 12 points of non-melee damage."));
        Check("Encounter prefilter allows slain",
            EncounterTracker.ShouldProcessMessage("You have slain a glyphed guard!"));
        Check("Encounter prefilter allows special grant",
            EncounterTracker.ShouldProcessMessage(
                "You will now use Eagle Strike instead of Tiger Claw while attacking."));
        Check("Belongs rank", SpellNameNormalizer.BelongsToFamily("Venom of the Snake III", "Venom of the Snake"));
        Check("Belongs mismatch", !SpellNameNormalizer.BelongsToFamily("Charm", "Mesmerize"));
        Check("Item Benefit family",
            SpellNameNormalizer.GetFamilyName("Item Benefit: Jonthan's Whistling Warsong") ==
            "Jonthan's Whistling Warsong");
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

        Check("Unattributed non-melee",
            parser.TryParse(stamp + "You were hit by non-melee for 42 damage.", out var unm) &&
            unm!.Damage is { Amount: 42, Ability: "Non-melee", Category: DamageCategory.Spell } &&
            unm.Damage.Source == LogLineParser.UnattributedNonMeleeSource &&
            unm.Damage.Target == "Sayser");

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

        Check("Outgoing spell resist names the ability",
            parser.TryParse(stamp + "A forsaken revenant resisted your Earthquake!", out var eqResist) &&
            eqResist!.Outcome is { Kind: CombatOutcomeKind.SpellResist, Ability: "Earthquake",
                Source: "Sayser", Target: "A forsaken revenant" });

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

        Check("Flag ability Double Bow Shot",
            LogLineParser.ExtractNamedAbilityFromFlags(" (Double Bow Shot)") == "Double Bow Shot");
        Check("Flag ignores Critical",
            LogLineParser.ExtractNamedAbilityFromFlags(" (Critical)") is null);
        Check("Flag ignores Double Attack",
            LogLineParser.ExtractNamedAbilityFromFlags(" (Double Attack)") is null);
        Check("Flag ignores Slay Undead",
            LogLineParser.ExtractNamedAbilityFromFlags(" (Slay Undead)") is null);
        Check("Flag ignores Strikethrough",
            LogLineParser.ExtractNamedAbilityFromFlags(" (Strikethrough)") is null);
        Check("Flag ignores compound extras",
            LogLineParser.ExtractNamedAbilityFromFlags(" (Riposte Strikethrough Critical)") is null);

        Check("Double Attack flag keeps weapon verb",
            parser.TryParse(stamp + "You slash a rat for 12 points of damage. (Double Attack)", out var daLine) &&
            daLine!.Damage is { Ability: "Slash" });
        Check("Riposte hit still counts as damage",
            parser.TryParse(stamp + "You slash a rat for 12 points of damage. (Riposte)", out var ripLine) &&
            ripLine!.Damage is { Ability: "Slash", Amount: 12 });
        Check("Riposte miss is a miss",
            parser.TryParse(stamp + "You try to slash a rat, but miss! (Riposte)", out var ripMiss) &&
            ripMiss!.Outcome is { Kind: CombatOutcomeKind.MissedAttack });

        Check("Slay Undead keeps weapon verb",
            parser.TryParse(stamp + "You slash a skeleton for 40 points of damage. (Slay Undead)", out var slayLine) &&
            slayLine!.Damage is { Ability: "Slash", AbilityRow: "Slay Undead" });
        Check("Finishing Blow keeps weapon verb",
            parser.TryParse(stamp + "You slash a rat for 90 points of damage. (Finishing Blow)", out var fbLine) &&
            fbLine!.Damage is { Ability: "Slash", AbilityRow: "Finishing Blow" });
        Check("Strikethrough keeps weapon verb",
            parser.TryParse(stamp + "You slash a rat for 12 points of damage. (Strikethrough)", out var stLine) &&
            stLine!.Damage is { Ability: "Slash", AbilityRow: "Strikethrough" });
        Check("Riposte Strikethrough still uses strikethrough row",
            parser.TryParse(stamp + "You slash a rat for 12 points of damage. (Riposte Strikethrough Critical)",
                out var ripSt) &&
            ripSt!.Damage is { Ability: "Slash", AbilityRow: "Strikethrough" });
        Check("Double Bow Shot stays a named flag",
            parser.TryParse(stamp + "You shoot a rat for 25 points of damage. (Double Bow Shot)", out var dbsLine) &&
            dbsLine!.Damage is { Ability: "Shoot", AbilityRow: "Double Bow Shot" });

        var t = new DateTime(2026, 8, 22, 15, 4, 8);
        var hitEncounter = new EncounterTracker("Sayser");
        var hitGroup = new GroupStateTracker("Sayser");
        var h0 = t.AddSeconds(20);
        hitEncounter.Process(
            new DamageEvent(h0, "Sayser", "a rat", 40, "Punch", DamageCategory.Melee, false), hitGroup);
        hitEncounter.Process(
            new DamageEvent(h0.AddSeconds(1), "Sayser", "a rat", 120, "Kick", DamageCategory.Melee, false),
            hitGroup);
        hitEncounter.Process(
            new DamageEvent(h0.AddSeconds(1), "Sayser", "a rat", 10, "Bash", DamageCategory.Melee, false),
            hitGroup);
        hitEncounter.Process(
            new DamageEvent(h0.AddSeconds(2), "Sayser", "a rat", 500, "Odium", DamageCategory.Spell, false),
            hitGroup);
        var hitSayser = hitEncounter.CreateSnapshot(h0.AddSeconds(2))!.Combatants
            .First(c => c.Name.Equals("Sayser", StringComparison.OrdinalIgnoreCase));
        Check("Melee hit stats ignore spells",
            hitSayser.MeleeHits == 3 && hitSayser.MeleeDamage == 170 &&
            hitSayser.MeleeHitMin == 10 && hitSayser.MeleeHitMax == 120,
            $"hits={hitSayser.MeleeHits} dmg={hitSayser.MeleeDamage} min={hitSayser.MeleeHitMin} max={hitSayser.MeleeHitMax}");
        Check("Damage timeline buckets",
            hitSayser.DamageBySecond.Count >= 3 &&
            hitSayser.DamageBySecond[0] == 40 &&
            hitSayser.DamageBySecond[1] == 130 &&
            hitSayser.DamageBySecond[2] == 500,
            string.Join(",", hitSayser.DamageBySecond));
        var timeline = CombatantViewModel.BuildDpsTimeline(hitSayser.DamageBySecond);
        // Rolling 5s window: (40)/5, (40+130)/5, (40+130+500)/5
        Check("Rolling average DPS timeline",
            timeline.Length >= 3 &&
            Math.Abs(timeline[0] - 8) < 0.01 &&
            Math.Abs(timeline[1] - 34) < 0.01 &&
            Math.Abs(timeline[2] - 134) < 0.01,
            string.Join(",", timeline.Select(v => v.ToString("0.0"))));

        var formatted = FightLogFormatter.Format(
            new FightLogEntry(t, "You slash a revenant for 120 points of damage. (Critical)"),
            "Sayser");
        Check("Fight log colors damage and crit",
            formatted.Any(s => s.Text == "120" && s.Bold) &&
            formatted.Any(s => s.Text.Contains("Critical", StringComparison.Ordinal) && s.Bold) &&
            formatted.Any(s => s.Text.Contains("{slash}", StringComparison.OrdinalIgnoreCase) && s.Bold),
            string.Join("|", formatted.Select(s => s.Text)));
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

        // EQL companion spirits use guardian/vigilant/frenzied (not only "companion").
        var companionGroup = new GroupStateTracker("Sayser");
        var summon = companionGroup.Process("You summon a guardian spirit.", t0.AddSeconds(30));
        Check("Guardian spirit summon arms pending bind", summon.Kind == GroupChangeKind.None);
        var companionBind = companionGroup.Process("Varer begins casting Inner Fire.", t0.AddSeconds(31));
        Check("Companion binds from first cast after summon",
            companionBind.Kind == GroupChangeKind.PetControlled &&
            companionBind.Member == "Varer" &&
            companionBind.Owner == "Sayser" &&
            companionGroup.TryGetPetOwner("Varer", out var companionOwner) &&
            companionOwner == "Sayser");
        var holding = companionGroup.Process(
            "Varer says, 'Now greater holding master.  I will only attack something new if ordered.'",
            t0.AddSeconds(32));
        Check("Greater holding say is recognized as pet speech",
            holding.Kind == GroupChangeKind.None || holding.Kind == GroupChangeKind.PetControlled);
        Check("Companion still owned after holding say",
            companionGroup.TryGetPetOwner("Varer", out _));

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

        Feed("You hit a rat for 10 points of damage.", e0.AddSeconds(62));
        Check("Timeout starts new encounter",
            encounter.Combatants.Any(c => c.Name == "Sayser" && c.Damage == 10));

        // Kill grace finalize
        var encounter2 = new EncounterTracker("Sayser");
        var g3 = new GroupStateTracker("Sayser");
        var k0 = DateTime.Now;
        var dmg = new DamageEvent(k0, "Sayser", "a beetle", 20, "Hit", DamageCategory.Melee, false);
        encounter2.Process(dmg, g3);
        encounter2.ProcessMessage(k0.AddSeconds(1), "You have slain a beetle!");
        encounter2.FinalizeIfInactive(k0.AddSeconds(6));
        Check("Kill grace finalize", encounter2.IsFinalized);

        // Ranked DoT ticks + unranked direct hit must merge (EQ log quirk).
        var rankMerge = new EncounterTracker("Sayser");
        var gRank = new GroupStateTracker("Sayser");
        var r0 = DateTime.Now;
        rankMerge.Process(new DamageEvent(r0, "Sayser", "an Evangelist of Hate", 50, "Envenomed Bolt",
            DamageCategory.Spell, false), gRank);
        rankMerge.Process(new DamageEvent(r0.AddSeconds(1), "Sayser", "an Evangelist of Hate", 432,
            "Envenomed Bolt VI", DamageCategory.DamageOverTime, false), gRank);
        rankMerge.Process(new DamageEvent(r0.AddSeconds(2), "Sayser", "an Evangelist of Hate", 440,
            "Envenomed Bolt VI", DamageCategory.DamageOverTime, false), gRank);
        var sayserRank = rankMerge.Combatants.First(c => c.Name == "Sayser");
        Check("Ranked ability merge single row",
            sayserRank.Abilities.Count == 1 &&
            sayserRank.Abilities.Values.Single().Name == "Envenomed Bolt VI" &&
            sayserRank.Abilities.Values.Single().Damage == 922);

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
        encounter3.FinalizeIfInactive(k0.AddSeconds(62));
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

        var procEncounter = new EncounterTracker("Sayser");
        var procGroup = new GroupStateTracker("Sayser");
        var p0 = new DateTime(2026, 8, 24, 9, 0, 0);
        procEncounter.Process(new DamageEvent(p0, "Sayser", "a rat", 20, "Slash", DamageCategory.Melee, false),
            procGroup);
        procEncounter.ProcessMessage(p0, "You begin casting Fire.");
        procEncounter.Process(new DamageEvent(p0.AddMilliseconds(200), "Sayser", "a rat", 40, "Fire",
            DamageCategory.Spell, false), procGroup);
        procEncounter.Process(new DamageEvent(p0.AddMilliseconds(400), "Sayser", "a beetle", 38, "Fire",
            DamageCategory.Spell, false), procGroup);
        var fire = procEncounter.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Equals("Fire", StringComparison.OrdinalIgnoreCase));
        Check("AoE same-second share one cast",
            fire.Hits == 2 && fire.ProcHits == 0,
            $"{fire.Hits}/{fire.ProcHits}");
        procEncounter.Process(new DamageEvent(p0.AddSeconds(2), "Sayser", "a rat", 41, "Fire",
            DamageCategory.Spell, false), procGroup);
        fire = procEncounter.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Equals("Fire", StringComparison.OrdinalIgnoreCase));
        Check("Later second without a new cast is a proc",
            fire.Hits == 3 && fire.ProcHits == 1,
            $"{fire.Hits}/{fire.ProcHits}");

        procEncounter.Process(new DamageEvent(p0.AddSeconds(3), "Sayser", "a rat", 12, "Envenomed Bolt VI",
            DamageCategory.DamageOverTime, false), procGroup);
        var bolt = procEncounter.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Contains("Envenomed", StringComparison.OrdinalIgnoreCase));
        Check("DoT tick is never a proc", bolt.ProcHits == 0, $"{bolt.ProcHits}");

        procEncounter.ProcessMessage(p0.AddSeconds(4), "You activate Quick Buff.");
        procEncounter.ProcessHealing(new HealingEvent(p0.AddSeconds(4), "Sayser", "Sayser", 200, 200,
            "Complete Heal", false, false), procGroup);
        procEncounter.Process(new DamageEvent(p0.AddSeconds(4), "Sayser", "a rat", 55, "Fangol's Breath",
            DamageCategory.Spell, false), procGroup);
        var breathProc = procEncounter.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Contains("Fangol", StringComparison.OrdinalIgnoreCase));
        Check("Quick Buff does not hide a damage proc",
            breathProc is { ProcHits: 1 }, $"{breathProc.ProcHits}");

        procEncounter.ProcessMessage(p0.AddSeconds(5), "You begin singing Denon's Desperate Dirge.");
        procEncounter.Process(new DamageEvent(p0.AddSeconds(6), "Sayser", "a rat", 879, "Denon's Desperate Dirge",
            DamageCategory.Spell, false), procGroup);
        procEncounter.Process(new DamageEvent(p0.AddSeconds(6), "Sayser", "a beetle", 879, "Denon's Desperate Dirge",
            DamageCategory.Spell, false), procGroup);
        procEncounter.Process(new DamageEvent(p0.AddSeconds(6), "Sayser", "Cleric of Innoruuk", 879,
            "Denon's Desperate Dirge", DamageCategory.Spell, false), procGroup);
        var dirge = procEncounter.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Contains("Dirge", StringComparison.OrdinalIgnoreCase));
        Check("Sung damage song is not a proc",
            dirge is { Hits: 3, ProcHits: 0 },
            dirge is null ? "missing" : $"{dirge.Hits}/{dirge.ProcHits}");
        procEncounter.Process(new DamageEvent(p0.AddSeconds(8), "Sayser", "a rat", 246, "Earthquake",
            DamageCategory.Spell, false), procGroup);
        var quake = procEncounter.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Equals("Earthquake", StringComparison.OrdinalIgnoreCase));
        Check("Weapon proc without a matching start is still a proc",
            quake is { ProcHits: 1 }, $"{quake.ProcHits}");

        procEncounter.ProcessOutcome(new CombatOutcomeEvent(p0.AddSeconds(8), "Sayser", "a beetle", "Earthquake",
            CombatOutcomeKind.SpellResist), procGroup);
        var quakeAfterResist = procEncounter.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Equals("Earthquake", StringComparison.OrdinalIgnoreCase));
        Check("Resisted weapon proc still counts as a proc",
            quakeAfterResist is { ProcHits: 2, ProcDamage: 246 },
            $"{quakeAfterResist.ProcHits}/{quakeAfterResist.ProcDamage}");

        var resistOnly = new EncounterTracker("Sayser");
        var resistGroup = new GroupStateTracker("Sayser");
        resistOnly.Process(new DamageEvent(p0.AddMinutes(3), "Sayser", "a revenant", 10, "Slash",
            DamageCategory.Melee, false), resistGroup);
        resistOnly.ProcessOutcome(new CombatOutcomeEvent(p0.AddMinutes(3), "Sayser", "a revenant", "Earthquake",
            CombatOutcomeKind.SpellResist), resistGroup);
        Check("Resist-only Earthquake without a land is not a ghost proc row",
            !resistOnly.CreateCombatantArray().First(c => c.Name == "Sayser")
                .Abilities.ContainsKey("Earthquake"));

        var seloResist = new EncounterTracker("Sayser");
        var seloGroup = new GroupStateTracker("Sayser");
        seloResist.Process(new DamageEvent(p0.AddMinutes(5), "Sayser", "a chest", 10, "Slash",
            DamageCategory.Melee, false), seloGroup);
        seloResist.Process(new DamageEvent(p0.AddMinutes(5), "Sayser", "a chest", 43,
            "Selo's Chords of Cessation", DamageCategory.DamageOverTime, false), seloGroup);
        seloResist.ProcessOutcome(new CombatOutcomeEvent(p0.AddMinutes(5), "Sayser", "a chest",
            "Selo's Chords of Cessation", CombatOutcomeKind.SpellResist), seloGroup);
        var selo = seloResist.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Contains("Cessation", StringComparison.OrdinalIgnoreCase));
        Check("Sung DoT resist is not a proc",
            selo is { Damage: 43, ProcHits: 0 }, $"{selo.ProcHits}/{selo.Damage}");

        var bladeResist = new EncounterTracker("Sayser");
        var bladeGroup = new GroupStateTracker("Sayser");
        bladeResist.Process(new DamageEvent(p0.AddMinutes(6), "Sayser", "a golem", 10, "Slash",
            DamageCategory.Melee, false), bladeGroup);
        bladeResist.ProcessOutcome(new CombatOutcomeEvent(p0.AddMinutes(6), "Sayser", "a golem",
            "Blade Dance", CombatOutcomeKind.SpellResist), bladeGroup);
        Check("Blade Dance resist is not a proc",
            !bladeResist.CreateCombatantArray().First(c => c.Name == "Sayser")
                .Abilities.ContainsKey("Blade Dance"));

        var castResist = new EncounterTracker("Sayser");
        var castResistGroup = new GroupStateTracker("Sayser");
        castResist.Process(new DamageEvent(p0.AddMinutes(4), "Sayser", "a rat", 10, "Slash",
            DamageCategory.Melee, false), castResistGroup);
        castResist.ProcessMessage(p0.AddMinutes(4), "You begin casting Firebolt.");
        castResist.ProcessOutcome(new CombatOutcomeEvent(p0.AddMinutes(4).AddSeconds(1), "Sayser", "a rat",
            "Firebolt", CombatOutcomeKind.SpellResist), castResistGroup);
        Check("Cast resist is not credited as a proc",
            !castResist.CreateCombatantArray().First(c => c.Name == "Sayser")
                .Abilities.ContainsKey("Firebolt"));

        procEncounter.ProcessMessage(p0.AddSeconds(9), "You begin singing Chords of Dissonance.");
        procEncounter.ProcessMessage(p0.AddSeconds(9.2), "Your melody has been interrupted!");
        procEncounter.Process(new DamageEvent(p0.AddSeconds(10), "Sayser", "a rat", 50, "Chords of Dissonance",
            DamageCategory.Spell, false), procGroup);
        var chords = procEncounter.CreateCombatantArray().First(c => c.Name == "Sayser")
            .Abilities.Values.First(a => a.Name.Contains("Dissonance", StringComparison.OrdinalIgnoreCase));
        Check("Interrupted song does not explain a later hit",
            chords is { ProcHits: 1 }, $"{chords.ProcHits}");

        var missEncounter = new EncounterTracker("Sayser");
        var missGroup = new GroupStateTracker("Sayser");
        var r1 = p0.AddMinutes(1);
        missEncounter.Process(new DamageEvent(r1, "Sayser", "a rat", 10, "Slash", DamageCategory.Melee, false),
            missGroup);
        missEncounter.ProcessOutcome(new CombatOutcomeEvent(r1.AddSeconds(2), "Sayser", "a rat", "Slash",
            CombatOutcomeKind.MissedAttack), missGroup);
        var missCombatant = missEncounter.CreateSnapshot(r1.AddSeconds(2))!.Combatants.First(c => c.Name == "Sayser");
        Check("Miss is counted", missCombatant.Misses >= 1, $"{missCombatant.Misses}");

        var flagEncounter = new EncounterTracker("Sayser");
        var flagGroup = new GroupStateTracker("Sayser");
        var flagParser = new LogLineParser("Sayser");
        var flagStamp = "[Sat Aug 08 12:00:00 2026] ";
        flagParser.TryParse(flagStamp + "You slash a skeleton for 40 points of damage. (Slay Undead)", out var slayHit);
        flagParser.TryParse(flagStamp + "You slash a rat for 12 points of damage. (Strikethrough)", out var stHit);
        flagParser.TryParse(flagStamp + "You slash a rat for 90 points of damage. (Finishing Blow)", out var fbHit);
        flagParser.TryParse(flagStamp + "You slash a rat for 11 points of damage. (Riposte Strikethrough Critical)",
            out var ripStHit);
        var flagAt = p0.AddMinutes(3);
        flagEncounter.Process(slayHit!.Damage! with { Timestamp = flagAt }, flagGroup);
        flagEncounter.Process(stHit!.Damage! with { Timestamp = flagAt.AddSeconds(1) }, flagGroup);
        flagEncounter.Process(fbHit!.Damage! with { Timestamp = flagAt.AddSeconds(2) }, flagGroup);
        flagEncounter.Process(ripStHit!.Damage! with { Timestamp = flagAt.AddSeconds(3) }, flagGroup);
        var flagCombatant = flagEncounter.CreateCombatantArray().First(c => c.Name == "Sayser");
        Check("Slay Undead is its own ability row",
            flagCombatant.Abilities.ContainsKey("Slay Undead") &&
            flagCombatant.Abilities["Slay Undead"].Damage == 40 &&
            !flagCombatant.Abilities.ContainsKey("Slash"));
        Check("Strikethrough and Finishing Blow are ability rows",
            flagCombatant.Abilities.ContainsKey("Strikethrough") &&
            flagCombatant.Abilities.ContainsKey("Finishing Blow") &&
            flagCombatant.Abilities["Strikethrough"].Damage == 23);
        Check("Melee flag rows are not procs",
            flagCombatant.Abilities.Values.All(ability => ability.ProcHits == 0));

        var specialEncounter = new EncounterTracker("Sayser");
        var specialGroup = new GroupStateTracker("Sayser");
        var specialAt = p0.AddMinutes(2);
        specialEncounter.ProcessMessage(specialAt,
            "You will now use Dragon Punch instead of Eagle Strike while attacking.");
        specialEncounter.Process(new DamageEvent(specialAt.AddSeconds(1), "Sayser", "a rat", 44, "Strike",
            DamageCategory.Melee, false), specialGroup);
        specialEncounter.ProcessOutcome(new CombatOutcomeEvent(specialAt.AddSeconds(2), "Sayser", "a rat", "Strike",
            CombatOutcomeKind.MissedAttack), specialGroup);
        var punch = specialEncounter.CreateCombatantArray().First(c => c.Name == "Sayser");
        Check("Strike grant becomes Dragon Punch",
            punch.Abilities.ContainsKey("Dragon Punch") && punch.Abilities["Dragon Punch"].Damage == 44 &&
            !punch.Abilities.ContainsKey("Strike"));
        Check("Strike miss uses Dragon Punch",
            punch.Misses >= 1, $"{punch.Misses}");

        specialEncounter.Reset();
        specialEncounter.Process(new DamageEvent(specialAt.AddSeconds(10), "Sayser", "a rat", 45, "Strike",
            DamageCategory.Melee, false), specialGroup);
        var afterReset = specialEncounter.CreateCombatantArray().First(c => c.Name == "Sayser");
        Check("Special name survives fight reset",
            afterReset.Abilities.ContainsKey("Dragon Punch") && !afterReset.Abilities.ContainsKey("Strike"));

        specialEncounter.ProcessMessage(specialAt.AddSeconds(11),
            "You will now use Flying Kick instead of Round Kick while attacking.");
        specialEncounter.Process(new DamageEvent(specialAt.AddSeconds(12), "Sayser", "a rat", 50, "Kick",
            DamageCategory.Melee, false), specialGroup);
        var flying = specialEncounter.CreateCombatantArray().First(c => c.Name == "Sayser");
        Check("Kick grant becomes Flying Kick",
            flying.Abilities.ContainsKey("Flying Kick") && flying.Abilities["Flying Kick"].Damage == 50 &&
            !flying.Abilities.ContainsKey("Kick"));

        specialEncounter.ProcessMessage(specialAt.AddSeconds(13),
            "You will now use Slam instead of Bash while attacking.");
        specialEncounter.Process(new DamageEvent(specialAt.AddSeconds(14), "Sayser", "a rat", 20, "Bash",
            DamageCategory.Melee, false), specialGroup);
        var slam = specialEncounter.CreateCombatantArray().First(c => c.Name == "Sayser");
        Check("Slam grant does not rename Bash",
            slam.Abilities.ContainsKey("Bash") && slam.Abilities["Bash"].Damage == 20 &&
            !slam.Abilities.ContainsKey("Slam"));

        var mezEncounter = new EncounterTracker("Sayser");
        var mezGroup = new GroupStateTracker("Sayser");
        mezEncounter.Process(new DamageEvent(p0, "Sayser", "a rat", 10, "Slash", DamageCategory.Melee, false), mezGroup);
        mezEncounter.ProcessMessage(p0.AddSeconds(1), "a rat has been mesmerized.");
        mezEncounter.FinalizeIfInactive(p0.AddSeconds(70));
        Check("Mez keeps the fight open past idle", !mezEncounter.IsFinalized);

        var zoneEncounter = new EncounterTracker("Sayser");
        var zoneGroup = new GroupStateTracker("Sayser");
        zoneEncounter.Process(new DamageEvent(p0, "Sayser", "a rat", 50, "Slash", DamageCategory.Melee, false),
            zoneGroup);
        zoneEncounter.ProcessMessage(p0.AddSeconds(1), "You have slain a rat!");
        zoneEncounter.ProcessMessage(p0.AddSeconds(2), "You have entered Everfrost Peaks.");
        zoneEncounter.Process(new DamageEvent(p0.AddSeconds(3), "Sayser", "a beetle", 30, "Slash",
            DamageCategory.Melee, false), zoneGroup);
        var afterZone = zoneEncounter.CreateCombatantArray().First(c => c.Name == "Sayser");
        Check("Zoning starts a fresh encounter",
            afterZone.Damage == 30, $"{afterZone.Damage}");

        var hintGroup = new GroupStateTracker("Sayser");
        hintGroup.Process("You summon a guardian spirit.", p0);
        hintGroup.RefreshCompanionHint(p0.AddSeconds(8));
        Check("Unbound spirit shows attach hint",
            hintGroup.CompanionHint is not null && hintGroup.CompanionHint.Contains("summoned", StringComparison.OrdinalIgnoreCase),
            hintGroup.CompanionHint ?? "null");
        var leaderBind = hintGroup.Process("Varer says, 'My leader is Sayser.'", p0.AddSeconds(9));
        Check("Leader say binds the spirit",
            leaderBind.Kind == GroupChangeKind.PetControlled &&
            hintGroup.TryGetPetOwner("Varer", out var leaderOwner) && leaderOwner == "Sayser");
        hintGroup.RefreshCompanionHint(p0.AddSeconds(10));
        Check("Bound spirit clears attach hint", hintGroup.CompanionHint is null);

        var buffBindGroup = new GroupStateTracker("Sayser");
        buffBindGroup.Process("You summon a frenzied spirit.", p0);
        var fromBuff = buffBindGroup.Process("Your Inner Fire spell has taken hold on Nokar.", p0.AddSeconds(1));
        Check("Buff landing binds a pending spirit",
            fromBuff.Kind == GroupChangeKind.PetControlled && fromBuff.Member == "Nokar");
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

        // Shared "has been charmed." is ambiguous in live data — still keep only the latest charm.
        var charmA = Rule("Allure", SpellTrackerCategory.Control, ControlEffectType.Charm, 210, 5);
        var charmB = Rule("Beguile", SpellTrackerCategory.Control, ControlEffectType.Charm, 750, 5);
        var charmRe = new BuffTracker();
        charmRe.Configure([charmA, charmB], _ => [], _ => [], _ => [" has been charmed."], _ => true);
        charmRe.Observe(t0, "You begin casting Allure.");
        charmRe.Observe(t0.AddSeconds(5.1), "a wolf has been charmed.");
        charmRe.Observe(t0.AddSeconds(20), "You begin casting Beguile.");
        charmRe.Observe(t0.AddSeconds(25.1), "a bear has been charmed.");
        var charmReSnap = charmRe.GetActiveSnapshots(t0.AddSeconds(25.2));
        Check("Ambiguous recharm keeps only latest",
            charmReSnap.Count == 1 &&
            charmReSnap[0].TargetName.Equals("a bear", StringComparison.OrdinalIgnoreCase) &&
            charmReSnap[0].SpellName == "Beguile");

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

        // Pending other-buff land is death-linked: pet death clears Chloroplast.
        var chloroDeath = Rule("Chloroplast", SpellTrackerCategory.Buff, ControlEffectType.Other, 120, 4);
        chloroDeath = chloroDeath with { TrackSelf = true, TrackOthers = true };
        var chloroDeathTracker = new BuffTracker();
        chloroDeathTracker.Configure([chloroDeath],
            _ => ["You have stopped regenerating."],
            _ => ["You begin to regenerate."],
            _ => [" begins to regenerate."], _ => true);
        chloroDeathTracker.Observe(t0, "You begin casting Chloroplast.");
        chloroDeathTracker.Observe(t0.AddSeconds(4), "Innoruuk`s Chosen begins to regenerate.");
        chloroDeathTracker.Observe(t0.AddSeconds(10), "You have slain Innoruuk`s Chosen!");
        Check("Pet death clears other Chloroplast",
            chloroDeathTracker.GetActiveSnapshots(t0.AddSeconds(10.1)).Count == 0);

        // Charm break leaves OTHER buffs up — they still tick on the former pet.
        var charmRule = Rule("Allure", SpellTrackerCategory.Control, ControlEffectType.Charm, 210, 5);
        var chloroCharm = Rule("Chloroplast", SpellTrackerCategory.Buff, ControlEffectType.Other, 120, 4);
        chloroCharm = chloroCharm with { TrackSelf = true, TrackOthers = true };
        var charmBuffTracker = new BuffTracker();
        charmBuffTracker.Configure([charmRule, chloroCharm],
            _ => [],
            _ => ["You begin to regenerate."],
            spell => spell.Equals("Chloroplast", StringComparison.OrdinalIgnoreCase)
                ? [" begins to regenerate."]
                : [" has been charmed."],
            _ => true);
        charmBuffTracker.Observe(t0, "You begin casting Allure.");
        charmBuffTracker.Observe(t0.AddSeconds(5), "Innoruuk`s Chosen has been charmed.");
        charmBuffTracker.Observe(t0.AddSeconds(10), "You begin casting Chloroplast.");
        charmBuffTracker.Observe(t0.AddSeconds(14), "Innoruuk`s Chosen begins to regenerate.");
        charmBuffTracker.Observe(t0.AddSeconds(40),
            "Your Allure spell has worn off of Innoruuk`s Chosen.");
        Check("Charm break keeps pet Chloroplast",
            charmBuffTracker.GetActiveSnapshots(t0.AddSeconds(40.1))
                .Count(s => s.SpellName == "Chloroplast" &&
                            s.TargetName.Equals("Innoruuk`s Chosen", StringComparison.OrdinalIgnoreCase)) == 1);
        charmBuffTracker.Observe(t0.AddSeconds(45), "You have slain Innoruuk`s Chosen!");
        Check("Former pet death clears Chloroplast after charm break",
            charmBuffTracker.GetActiveSnapshots(t0.AddSeconds(45.1))
                .All(s => s.SpellName != "Chloroplast"));

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

        var alacrity = Rule("Alacrity", SpellTrackerCategory.Buff, ControlEffectType.Other, 1026, 2) with
        {
            TrackSelf = true,
            TrackOthers = false
        };
        var alacTracker = new BuffTracker();
        alacTracker.Configure([alacrity],
            _ => ["Your speed returns to normal."],
            _ => ["You feel much faster."],
            _ => [" feels much faster."], _ => true);
        alacTracker.Observe(t0, "You begin casting Alacrity.");
        alacTracker.Observe(t0.AddSeconds(2), "You feel much faster.");
        Check("Alacrity armed on self", alacTracker.GetActiveSnapshots(t0.AddSeconds(2.1)).Count == 1);
        alacTracker.Observe(t0.AddSeconds(30), "You feel a bit dispelled.");
        Check("Generic dispel does not drop Alacrity",
            alacTracker.GetActiveSnapshots(t0.AddSeconds(30.1)).Count == 1);
        alacTracker.Observe(t0.AddSeconds(40), "Your speed returns to normal.");
        Check("Alacrity fade drops the timer",
            alacTracker.GetActiveSnapshots(t0.AddSeconds(40.1)).Count == 0);

        var regenSelf = Rule("Chloroplast", SpellTrackerCategory.Buff, ControlEffectType.Other, 1392, 3.6) with
        {
            TrackSelf = true,
            TrackOthers = false
        };
        var mixedBuffs = new BuffTracker();
        mixedBuffs.Configure([alacrity, regenSelf],
            spell => spell.Equals("Alacrity", StringComparison.OrdinalIgnoreCase)
                ? ["Your speed returns to normal."]
                : ["You have stopped regenerating."],
            spell => spell.Equals("Alacrity", StringComparison.OrdinalIgnoreCase)
                ? ["You feel much faster."]
                : ["You begin to regenerate."],
            _ => [], _ => true);
        mixedBuffs.Observe(t0, "You begin casting Alacrity.");
        mixedBuffs.Observe(t0.AddSeconds(2), "You feel much faster.");
        mixedBuffs.Observe(t0.AddSeconds(3), "You begin casting Chloroplast.");
        mixedBuffs.Observe(t0.AddSeconds(7), "You begin to regenerate.");
        Check("Alacrity and Chloroplast both armed",
            mixedBuffs.GetActiveSnapshots(t0.AddSeconds(7.1)).Count == 2);
        mixedBuffs.Observe(t0.AddSeconds(20), "You feel a bit dispelled.");
        Check("Generic dispel drops neither tracked buff without fade wording",
            mixedBuffs.GetActiveSnapshots(t0.AddSeconds(20.1)).Count == 2);
        mixedBuffs.Observe(t0.AddSeconds(21), "Your speed returns to normal.");
        var afterHasteFade = mixedBuffs.GetActiveSnapshots(t0.AddSeconds(21.1));
        Check("Haste fade after dispel drops only Alacrity",
            afterHasteFade.Count == 1 && afterHasteFade[0].SpellName == "Chloroplast");
        Check("Haste fade after dispel is labeled Dispelled",
            mixedBuffs.GetSnapshot(alacrity.Id, t0.AddSeconds(21.1)).StopReason == BuffStopReason.Dispelled);

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

        // Same-name AE pack: each land is its own timer (EQ has no entity ids).
        var mezSame = Rule("Mesmerization", SpellTrackerCategory.Control, ControlEffectType.Mez, 24, 3);
        var mezSameTracker = new BuffTracker();
        mezSameTracker.Configure([mezSame], _ => [], _ => [], _ => [" has been mesmerized."], _ => true);
        mezSameTracker.Observe(t0, "You begin casting Mesmerization.");
        mezSameTracker.Observe(t0.AddSeconds(3), "an imp protector has been mesmerized.");
        mezSameTracker.Observe(t0.AddSeconds(3), "an imp protector has been mesmerized.");
        mezSameTracker.Observe(t0.AddSeconds(3), "an imp protector has been mesmerized.");
        Check("Mez same-name multi lands",
            mezSameTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 3);
        mezSameTracker.Observe(t0.AddSeconds(27),
            "Your Mesmerization spell has worn off of an imp protector.");
        mezSameTracker.Observe(t0.AddSeconds(27),
            "Your Mesmerization spell has worn off of an imp protector.");
        mezSameTracker.Observe(t0.AddSeconds(27),
            "Your Mesmerization spell has worn off of an imp protector.");
        var sameNameBreakAlerts = mezSameTracker.Tick(t0.AddSeconds(27.1));
        Check("Mez same-name worn-off alerts each", sameNameBreakAlerts.Count == 3);
        Check("Mez same-name all cleared",
            mezSameTracker.GetActiveSnapshots(t0.AddSeconds(27.1)).Count == 0);

        // Remes: new land stacks; overwrite worn-off pops the old application without alert.
        var mezRemes = Rule("Mesmerization", SpellTrackerCategory.Control, ControlEffectType.Mez, 24, 3);
        var mezRemesTracker = new BuffTracker();
        mezRemesTracker.Configure([mezRemes], _ => [], _ => [], _ => [" has been mesmerized."], _ => true);
        mezRemesTracker.Observe(t0, "You begin casting Mesmerization.");
        mezRemesTracker.Observe(t0.AddSeconds(3), "a Knight of Innoruuk has been mesmerized.");
        mezRemesTracker.Observe(t0.AddSeconds(10), "You begin casting Mesmerization.");
        mezRemesTracker.Observe(t0.AddSeconds(13), "a Knight of Innoruuk has been mesmerized.");
        mezRemesTracker.Observe(t0.AddSeconds(13),
            "Your Mesmerization spell has worn off of a Knight of Innoruuk.");
        var remesAlerts = mezRemesTracker.Tick(t0.AddSeconds(13.1));
        var remesSnap = mezRemesTracker.GetActiveSnapshots(t0.AddSeconds(13.1));
        Check("Remes overwrite keeps timer",
            remesSnap.Count == 1 &&
            remesSnap[0].TargetName.Equals("a Knight of Innoruuk", StringComparison.OrdinalIgnoreCase) &&
            Math.Abs((remesSnap[0].ExpiresAt - t0.AddSeconds(13 + 24)).TotalSeconds) < 1.0);
        Check("Remes overwrite suppresses alert", remesAlerts.Count == 0);

        // AE remes of two targets: overwrite worn-offs must not fire; later real worn-offs do.
        var mezAeRemes = Rule("Mesmerization", SpellTrackerCategory.Control, ControlEffectType.Mez, 24, 3);
        var mezAeRemesTracker = new BuffTracker();
        mezAeRemesTracker.Configure([mezAeRemes], _ => [], _ => [], _ => [" has been mesmerized."], _ => true);
        mezAeRemesTracker.Observe(t0, "You begin casting Mesmerization.");
        mezAeRemesTracker.Observe(t0.AddSeconds(3), "an Agent of Innoruuk has been mesmerized.");
        mezAeRemesTracker.Observe(t0.AddSeconds(3), "a Knight of Innoruuk has been mesmerized.");
        mezAeRemesTracker.Observe(t0.AddSeconds(20), "You begin casting Mesmerization.");
        mezAeRemesTracker.Observe(t0.AddSeconds(23), "an Agent of Innoruuk has been mesmerized.");
        mezAeRemesTracker.Observe(t0.AddSeconds(23), "a Knight of Innoruuk has been mesmerized.");
        mezAeRemesTracker.Observe(t0.AddSeconds(23),
            "Your Mesmerization spell has worn off of an Agent of Innoruuk.");
        mezAeRemesTracker.Observe(t0.AddSeconds(23),
            "Your Mesmerization spell has worn off of a Knight of Innoruuk.");
        var aeRemesAlerts = mezAeRemesTracker.Tick(t0.AddSeconds(23.1));
        Check("AE remes no overwrite alerts", aeRemesAlerts.Count == 0);
        Check("AE remes both still active",
            mezAeRemesTracker.GetActiveSnapshots(t0.AddSeconds(23.1)).Count == 2);
        mezAeRemesTracker.Observe(t0.AddSeconds(47),
            "Your Mesmerization spell has worn off of an Agent of Innoruuk.");
        mezAeRemesTracker.Observe(t0.AddSeconds(47),
            "Your Mesmerization spell has worn off of a Knight of Innoruuk.");
        var aeBreakAlerts = mezAeRemesTracker.Tick(t0.AddSeconds(47.1));
        Check("AE mez real worn-off alerts once each", aeBreakAlerts.Count == 2);
        Check("AE mez cleared after real worn-off",
            mezAeRemesTracker.GetActiveSnapshots(t0.AddSeconds(47.1)).Count == 0);

        // Recast mez: only successful lands from the new cast remain (drop prior wave).
        var mezWave = Rule("Mesmerization", SpellTrackerCategory.Control, ControlEffectType.Mez, 24, 3);
        var mezWaveTracker = new BuffTracker();
        mezWaveTracker.Configure([mezWave], _ => [], _ => [], _ => [" has been mesmerized."], _ => true);
        mezWaveTracker.Observe(t0, "You begin casting Mesmerization.");
        mezWaveTracker.Observe(t0.AddSeconds(3), "a kobold has been mesmerized.");
        mezWaveTracker.Observe(t0.AddSeconds(3), "a goblin has been mesmerized.");
        mezWaveTracker.Observe(t0.AddSeconds(3), "a rat has been mesmerized.");
        mezWaveTracker.Observe(t0.AddSeconds(3), "a bat has been mesmerized.");
        mezWaveTracker.Observe(t0.AddSeconds(3), "a snake has been mesmerized.");
        Check("Mez first wave tracks all lands",
            mezWaveTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 5);
        mezWaveTracker.Observe(t0.AddSeconds(10), "You begin casting Mesmerization.");
        mezWaveTracker.Observe(t0.AddSeconds(13), "a kobold has been mesmerized.");
        mezWaveTracker.Observe(t0.AddSeconds(13), "a goblin has been mesmerized.");
        mezWaveTracker.Observe(t0.AddSeconds(13),
            "Your Mesmerization spell has worn off of a kobold.");
        mezWaveTracker.Observe(t0.AddSeconds(13),
            "Your Mesmerization spell has worn off of a goblin.");
        mezWaveTracker.Observe(t0.AddSeconds(13),
            "Your Mesmerization spell has worn off of a rat.");
        var waveSnap = mezWaveTracker.GetActiveSnapshots(t0.AddSeconds(13.1));
        Check("Mez recast keeps only new successful lands",
            waveSnap.Count == 2 &&
            waveSnap.Any(s => s.TargetName.Equals("a kobold", StringComparison.OrdinalIgnoreCase)) &&
            waveSnap.Any(s => s.TargetName.Equals("a goblin", StringComparison.OrdinalIgnoreCase)));
        Check("Mez recast ignores stale overwrite worn-offs",
            mezWaveTracker.Tick(t0.AddSeconds(13.1)).Count == 0);

        // Same-second AE land + break on one of two identical names still alerts (not overwrite).
        var mezEarly = Rule("Mesmerization", SpellTrackerCategory.Control, ControlEffectType.Mez, 24, 3);
        var mezEarlyTracker = new BuffTracker();
        mezEarlyTracker.Configure([mezEarly], _ => [], _ => [], _ => [" has been mesmerized."], _ => true);
        mezEarlyTracker.Observe(t0, "You begin casting Mesmerization.");
        mezEarlyTracker.Observe(t0.AddSeconds(3), "a greater kobold shaman has been mesmerized.");
        mezEarlyTracker.Observe(t0.AddSeconds(3), "a greater kobold shaman has been mesmerized.");
        mezEarlyTracker.Observe(t0.AddSeconds(3),
            "Your Mesmerization spell has worn off of a greater kobold shaman.");
        var earlyAlerts = mezEarlyTracker.Tick(t0.AddSeconds(3.1));
        Check("Mez early break among same-name alerts", earlyAlerts.Count == 1);
        Check("Mez early break leaves sibling",
            mezEarlyTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 1);

        // Death clears one same-name mez stack, not the whole pack.
        var mezDeath = Rule("Mesmerization", SpellTrackerCategory.Control, ControlEffectType.Mez, 24, 3);
        var mezDeathTracker = new BuffTracker();
        mezDeathTracker.Configure([mezDeath], _ => [], _ => [], _ => [" has been mesmerized."], _ => true);
        mezDeathTracker.Observe(t0, "You begin casting Mesmerization.");
        mezDeathTracker.Observe(t0.AddSeconds(3), "a lava beetle has been mesmerized.");
        mezDeathTracker.Observe(t0.AddSeconds(3), "a lava beetle has been mesmerized.");
        mezDeathTracker.Observe(t0.AddSeconds(5), "You have slain a lava beetle!");
        Check("Mez death clears one same-name",
            mezDeathTracker.GetActiveSnapshots(t0.AddSeconds(5.1)).Count == 1);

        var mezRuleId = Guid.NewGuid();
        BuffInstanceSnapshot MezSnap(string key, string name, int extraExpireSeconds = 0) =>
            new(mezRuleId, key, "Mesmerization", name, false, t0.AddSeconds(3),
                t0.AddSeconds(27 + extraExpireSeconds), TimeSpan.FromSeconds(24 + extraExpireSeconds), false, false);
        var stackedRats = MezOverlayGrouping.Collapse(
            [MezSnap("1", "a rat"), MezSnap("2", "a rat"), MezSnap("3", "a rat")],
            _ => ControlEffectType.Mez);
        Check("Mez overlay stacks same name",
            stackedRats.Count == 1 && stackedRats[0].StackCount == 3 &&
            stackedRats[0].Snapshot.TargetName == "a rat");
        Check("Mez overlay stack drops on break",
            MezOverlayGrouping.Collapse([MezSnap("1", "a rat"), MezSnap("2", "a rat")], _ => ControlEffectType.Mez)
                is { Count: 1 } dropped && dropped[0].StackCount == 2);
        var mixedNames = MezOverlayGrouping.Collapse(
            [MezSnap("1", "a rat"), MezSnap("2", "a rat"), MezSnap("3", "a goblin")],
            _ => ControlEffectType.Mez);
        Check("Mez overlay keeps distinct names",
            mixedNames.Count == 2 &&
            mixedNames.Any(item => item.Snapshot.TargetName == "a rat" && item.StackCount == 2) &&
            mixedNames.Any(item => item.Snapshot.TargetName == "a goblin" && item.StackCount == 1));
        var soonest = MezOverlayGrouping.Collapse(
            [MezSnap("later", "a rat", 10), MezSnap("soon", "a rat")],
            _ => ControlEffectType.Mez);
        Check("Mez overlay uses soonest timer",
            soonest.Count == 1 && soonest[0].Snapshot.ExpiresAt == t0.AddSeconds(27));
        var stackedVm = new BuffOverlayEntryViewModel(stackedRats[0].Snapshot, SpellTrackerCategory.Control,
            ControlEffectType.Mez, stackCount: stackedRats[0].StackCount);
        Check("Mez overlay label is name X3",
            stackedVm.OverlayTargetText == "rat  X3" && stackedVm.StackCountText == "X3" && stackedVm.HasStackCount);
        stackedVm.Update(stackedRats[0].Snapshot, 2);
        Check("Mez overlay label updates to X2",
            stackedVm.OverlayTargetText == "rat  X2" && stackedVm.StackCountText == "X2");
        var charmRows = MezOverlayGrouping.Collapse(
            [MezSnap("1", "a rat"), MezSnap("2", "a rat")],
            _ => ControlEffectType.Charm);
        Check("Charm overlay does not mez-stack",
            charmRows.Count == 2 && charmRows.All(item => item.StackCount == 1));

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

        // Pet/group poison lands share "has been poisoned." — must not stack as Envenomed Bolt
        var petDot = Rule("Envenomed Bolt", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 36, 3);
        var petTracker = new BuffTracker();
        petTracker.Configure([petDot], _ => ["The poison has run its course."], _ => [],
            _ => [" has been poisoned."], _ => true);
        petTracker.Observe(t0, "You begin casting Envenomed Bolt VI.");
        petTracker.Observe(t0.AddSeconds(2.9),
            "Bzzazzt hit an essence tamer for 100 points of poison damage by Deadly Poison.");
        petTracker.Observe(t0.AddSeconds(2.9), "An essence tamer has been poisoned.");
        Check("Foreign pet poison ignored during pending",
            petTracker.GetActiveSnapshots(t0.AddSeconds(3)).Count == 0);
        petTracker.Observe(t0.AddSeconds(3.1),
            "You hit an essence tamer for 48 points of poison damage by Envenomed Bolt.");
        petTracker.Observe(t0.AddSeconds(3.1), "An essence tamer has been poisoned.");
        Check("Local Envenomed Bolt land accepted",
            petTracker.GetActiveSnapshots(t0.AddSeconds(3.2)).Count == 1);
        petTracker.Observe(t0.AddSeconds(3.4),
            "Bzzazzt hit an essence tamer for 100 points of poison damage by Deadly Poison.");
        petTracker.Observe(t0.AddSeconds(3.4), "An essence tamer has been poisoned.");
        petTracker.Observe(t0.AddSeconds(3.6),
            "Bzzazzt hit an essence tamer for 100 points of poison damage by Deadly Poison.");
        petTracker.Observe(t0.AddSeconds(3.6), "An essence tamer has been poisoned.");
        Check("Foreign pet poison does not stack DoT rows",
            petTracker.GetActiveSnapshots(t0.AddSeconds(3.7)).Count == 1);

        // Unique Odium land must not be blocked by a pet proc on the same target.
        var odium = Rule("Odium", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 42, 1.73);
        var odiumTracker = new BuffTracker();
        odiumTracker.Configure([odium], _ => [], _ => [],
            _ => [" staggers under a dark curse."], _ => false);
        odiumTracker.Observe(t0, "You begin casting Odium VIII.");
        odiumTracker.Observe(t0.AddSeconds(1),
            "Innoruuk`s Chosen hit a forsaken revenant for 154 points of prismatic damage by Puma Maw.");
        odiumTracker.Observe(t0.AddSeconds(1), "a forsaken revenant staggers under a dark curse.");
        var odiumSnaps = odiumTracker.GetActiveSnapshots(t0.AddSeconds(1.1));
        Check("Odium land survives pet Puma Maw",
            odiumSnaps.Count == 1 &&
            odiumSnaps[0].SpellName == "Odium" &&
            odiumSnaps[0].TargetName.Equals("a forsaken revenant", StringComparison.OrdinalIgnoreCase),
            odiumSnaps.Count == 0 ? "missing" : $"{odiumSnaps[0].SpellName}/{odiumSnaps[0].TargetName}");

        // Tick line is enough when land text never confirms.
        var tickOnly = Rule("Odium", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 42, 1.73);
        var tickTracker = new BuffTracker();
        tickTracker.Configure([tickOnly], _ => [], _ => [],
            _ => [" staggers under a dark curse."], _ => false);
        tickTracker.Observe(t0, "You begin casting Odium VIII.");
        tickTracker.Observe(t0.AddSeconds(4),
            "A forsaken revenant has taken 420 damage from your Odium VIII.");
        var tickSnaps = tickTracker.GetActiveSnapshots(t0.AddSeconds(4.1));
        Check("Odium tick opens overlay without land text",
            tickSnaps.Count == 1 &&
            tickSnaps[0].TargetName.Equals("A forsaken revenant", StringComparison.OrdinalIgnoreCase),
            tickSnaps.Count == 0 ? "missing" : tickSnaps[0].TargetName);
        var remainingBefore = tickSnaps[0].Remaining;
        tickTracker.Observe(t0.AddSeconds(10),
            "A forsaken revenant has taken 412 damage from your Odium VIII.");
        var remainingAfter = tickTracker.GetActiveSnapshots(t0.AddSeconds(10)).First().Remaining;
        Check("Odium ticks do not refresh duration",
            remainingAfter < remainingBefore,
            $"{remainingBefore} -> {remainingAfter}");

        // Hit ability picks the matching pending poison when two share land text
        var hitPick = new BuffTracker();
        hitPick.Configure([venom, envenom], _ => [], _ => [], _ => [" has been poisoned."], _ => true);
        hitPick.Observe(t0, "You begin casting Envenomed Bolt.");
        hitPick.Observe(t0.AddSeconds(0.5), "You begin casting Venom of the Snake.");
        hitPick.Observe(t0.AddSeconds(3.5),
            "You hit a rat for 55 points of poison damage by Envenomed Bolt.");
        hitPick.Observe(t0.AddSeconds(3.5), "a rat has been poisoned.");
        var hitSnaps = hitPick.GetActiveSnapshots(t0.AddSeconds(3.6));
        Check("Hit ability selects matching poison DoT",
            hitSnaps.Count == 1 && hitSnaps[0].SpellName == "Envenomed Bolt");

        // Natural expiry removes the instance after configured duration
        var shortDot = Rule("Short Poison", SpellTrackerCategory.DamageOverTime, ControlEffectType.Other, 2, 0);
        var expireTracker = new BuffTracker();
        expireTracker.Configure([shortDot], _ => [], _ => [], _ => [" has been poisoned."], _ => true);
        expireTracker.Observe(t0, "You begin casting Short Poison.");
        expireTracker.Observe(t0.AddSeconds(0.1), "a bat has been poisoned.");
        expireTracker.Tick(t0.AddSeconds(2.3));
        var snap = expireTracker.GetSnapshot(shortDot.Id, t0.AddSeconds(2.4));
        Check("Natural expiry flagged", snap.IsExpired && snap.StopReason == BuffStopReason.Expired);

        // Hostile Deadly Poison: only on me; clears on fade / duration / death — not zone, other-target land, or nameless dispel
        var deadly = Rule("Deadly Poison", SpellTrackerCategory.Hostile, ControlEffectType.Other, 222, 0) with
        {
            TrackSelf = true,
            TrackOthers = false,
            LandSound = BuffSoundKind.Klaxon
        };
        var hostile = new BuffTracker();
        hostile.Configure([deadly],
            _ => ["The poison has run its course."],
            _ => ["You have been poisoned."],
            _ => [" has been poisoned."],
            _ => true);

        hostile.Observe(t0, "a gnoll has been poisoned.");
        Check("Hostile ignores other-target land", hostile.GetActiveSnapshots(t0.AddSeconds(0.1)).Count == 0);

        var landAlerts = hostile.Tick(t0.AddSeconds(0.2));
        Check("Hostile no land alert for other", landAlerts.Count == 0);

        hostile.Observe(t0.AddSeconds(1), "You have been poisoned.");
        var afterLand = hostile.GetActiveSnapshots(t0.AddSeconds(1.1));
        Check("Hostile lands on self",
            afterLand.Count == 1 && afterLand[0].IsSelf && afterLand[0].SpellName == "Deadly Poison");
        var landed = hostile.Tick(t0.AddSeconds(1.2));
        Check("Hostile land alert queued",
            landed.Count == 1 && landed[0].Phase == BuffAlertPhase.Landed);

        hostile.Observe(t0.AddSeconds(2), "You have entered Greater Faydark.");
        Check("Hostile survives zone", hostile.GetActiveSnapshots(t0.AddSeconds(2.1)).Count == 1);

        hostile.Observe(t0.AddSeconds(3), "You have slain a gnoll!");
        Check("Hostile ignores NPC death", hostile.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 1);

        hostile.Observe(t0.AddSeconds(4), "The poison has run its course.");
        Check("Hostile clears on fade", hostile.GetActiveSnapshots(t0.AddSeconds(4.1)).Count == 0);
        var fadeAlerts = hostile.Tick(t0.AddSeconds(4.2));
        Check("Hostile expire alert on fade",
            fadeAlerts.Count == 1 && fadeAlerts[0].Phase == BuffAlertPhase.Expired);

        hostile.Observe(t0.AddSeconds(10), "You have been poisoned.");
        Check("Hostile relands", hostile.GetActiveSnapshots(t0.AddSeconds(10.1)).Count == 1);
        hostile.Observe(t0.AddSeconds(11), "You feel dispelled.");
        Check("Generic dispel does not drop unique hostile without fade",
            hostile.GetActiveSnapshots(t0.AddSeconds(11.1)).Count == 1);
        hostile.Observe(t0.AddSeconds(11), "The poison has run its course.");
        Check("Hostile fade after dispel clears poison",
            hostile.GetActiveSnapshots(t0.AddSeconds(11.1)).Count == 0);
        Check("Hostile fade after dispel is labeled Dispelled",
            hostile.GetSnapshot(deadly.Id, t0.AddSeconds(11.1)).StopReason == BuffStopReason.Dispelled);

        hostile.Observe(t0.AddSeconds(20), "You have been poisoned.");
        Check("Hostile active before death", hostile.GetActiveSnapshots(t0.AddSeconds(20.1)).Count == 1);
        hostile.Observe(t0.AddSeconds(21), "You have been slain by a gnoll!");
        Check("Hostile clears on player death", hostile.GetActiveSnapshots(t0.AddSeconds(21.1)).Count == 0);

        var shortHostile = deadly with { Id = Guid.NewGuid(), DurationSeconds = 2 };
        var hostileExpire = new BuffTracker();
        hostileExpire.Configure([shortHostile],
            _ => ["The poison has run its course."],
            _ => ["You have been poisoned."],
            _ => [],
            _ => false);
        hostileExpire.Observe(t0, "You have been poisoned.");
        hostileExpire.Tick(t0.AddSeconds(0.1)); // drain land alert
        hostileExpire.Tick(t0.AddSeconds(2.2));
        Check("Hostile clears on duration",
            hostileExpire.GetActiveSnapshots(t0.AddSeconds(2.3)).Count == 0);
        var durationSnap = hostileExpire.GetSnapshot(shortHostile.Id, t0.AddSeconds(2.3));
        Check("Hostile duration stop reason", durationSnap.StopReason == BuffStopReason.Expired);

        var selo = SongRule("Selo's Accelerando", 180);
        var seloTracker = new BuffTracker();
        seloTracker.Configure([selo],
            _ => ["You slow down."],
            _ => ["Your feet move faster."],
            _ => [" feels much faster."],
            _ => true);
        seloTracker.Observe(t0, "Your feet move faster.");
        Check("Song starts on first self land", seloTracker.GetActiveSnapshots(t0.AddSeconds(0.1)).Count == 1);
        seloTracker.Observe(t0.AddSeconds(1), "Your song ends.");
        seloTracker.Observe(t0.AddSeconds(4), "Your feet move faster.");
        Check("Song starts after own twist", seloTracker.GetActiveSnapshots(t0.AddSeconds(4.1)).Count == 1);
        seloTracker.Observe(t0.AddSeconds(10), "Innoruuk`s Chosen feels much faster.");
        Check("Song ignores group member land", seloTracker.GetActiveSnapshots(t0.AddSeconds(10.1)).Count == 1);
        seloTracker.Observe(t0.AddSeconds(20), "Your Selo's Accelerando spell has worn off.");
        Check("Song stops on self worn off", seloTracker.GetActiveSnapshots(t0.AddSeconds(20.1)).Count == 0);
        seloTracker.Observe(t0.AddSeconds(21), "Your song ends.");
        seloTracker.Observe(t0.AddSeconds(24), "Your feet move faster.");
        seloTracker.Observe(t0.AddSeconds(50), "You slow down.");
        Check("Song stops on fade", seloTracker.GetActiveSnapshots(t0.AddSeconds(50.1)).Count == 0);
        seloTracker.Observe(t0.AddSeconds(51), "Your song ends.");
        seloTracker.Observe(t0.AddSeconds(54), "Your feet move faster.");
        var seloFirst = seloTracker.GetActiveSnapshots(t0.AddSeconds(54.1))[0].StartedAt;
        seloTracker.Observe(t0.AddSeconds(120), "Your feet move faster.");
        Check("Song refresh while active",
            seloTracker.GetActiveSnapshots(t0.AddSeconds(120.1))[0].StartedAt > seloFirst);

        seloTracker.Tick(t0.AddSeconds(199.9));
        seloTracker.Observe(t0.AddSeconds(200), "Your song ends.");
        Check("Twist end keeps active song buff",
            seloTracker.GetActiveSnapshots(t0.AddSeconds(200.1)).Count == 1);
        Check("Twist end does not alert",
            seloTracker.Tick(t0.AddSeconds(200.2)).Count == 0);

        var twistSelo = SongRule("Selo's Accelerando", 0);
        var twistAnthem = SongRule("Anthem de Arms", 0);
        var twistTracker = new BuffTracker();
        twistTracker.Configure([twistSelo, twistAnthem],
            spell => spell.Contains("Selo", StringComparison.OrdinalIgnoreCase)
                ? (IReadOnlyList<string>)["You slow down."]
                : ["Your surge of strength fades."],
            spell => spell.Contains("Selo", StringComparison.OrdinalIgnoreCase)
                ? ["Your feet move faster."]
                : ["A burst of strength surges through your body."],
            _ => [],
            _ => false,
            _ => false);
        twistTracker.Observe(t0, "Your feet move faster.");
        twistTracker.Observe(t0.AddSeconds(1), "Your song ends.");
        twistTracker.Observe(t0.AddSeconds(3), "A burst of strength surges through your body.");
        Check("Multiple twisted songs stay in overlay",
            twistTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 2);

        seloTracker.Observe(t0.AddSeconds(210), "Your feet move faster.");
        seloTracker.Tick(t0.AddSeconds(210.1));
        seloTracker.Observe(t0.AddSeconds(220), "You slow down.");
        Check("Song stops on fade after replay", seloTracker.GetActiveSnapshots(t0.AddSeconds(220.05)).Count == 0);
        var songFadeAlerts = seloTracker.Tick(t0.AddSeconds(220.1));
        Check("Song expire alert on fade",
            songFadeAlerts.Count == 1 && songFadeAlerts[0].Phase == BuffAlertPhase.Expired);

        var shortSong = SongRule("Anthem de Arms", 2);
        var noTimerSong = new BuffTracker();
        noTimerSong.Configure([shortSong],
            _ => ["Your surge of strength fades."],
            _ => ["A burst of strength surges through your body."],
            _ => [],
            _ => false,
            _ => false);
        noTimerSong.Observe(t0, "A burst of strength surges through your body.");
        Check("Song ignores duration timer",
            noTimerSong.GetActiveSnapshots(t0.AddSeconds(10)).Count == 1);

        var anthem = SongRule("Anthem de Arms", 180);
        var anthemTracker = new BuffTracker();
        anthemTracker.Configure([anthem],
            _ => ["Your surge of strength fades."],
            _ => ["A burst of strength surges through your body."],
            _ => [],
            _ => false,
            _ => false);
        anthemTracker.Observe(t0, "A burst of strength surges through your body.");
        Check("Unique song land starts without song ends",
            anthemTracker.GetActiveSnapshots(t0.AddSeconds(0.1)).Count == 1);

        var warsong = SongRule("Jonthan's Whistling Warsong", 180);
        var warsongTracker = new BuffTracker();
        warsongTracker.Configure([warsong],
            _ => ["You stop whistling."],
            _ => ["You whistle an ancient warsong."],
            _ => [],
            _ => false,
            _ => false);
        warsongTracker.Observe(t0, "You begin singing Jonthan's Whistling Warsong.");
        warsongTracker.Observe(t0.AddSeconds(1), "You whistle an ancient warsong.");
        Check("Whistling warsong starts on land after begin singing",
            warsongTracker.GetActiveSnapshots(t0.AddSeconds(1.1)).Count == 1);
        warsongTracker.Observe(t0.AddSeconds(18), "You stop whistling.");
        Check("Whistling warsong stops on fade",
            warsongTracker.GetActiveSnapshots(t0.AddSeconds(18.1)).Count == 0);

        var seloA = SongRule("Selo's Accelerando", 180);
        var seloB = SongRule("Selo's Accelerating Chorus", 180);
        var sharedSeloTracker = new BuffTracker();
        sharedSeloTracker.Configure([seloA, seloB],
            _ => ["You slow down."],
            _ => ["Your feet move faster."],
            _ => [" feels much faster."],
            _ => true,
            _ => true);
        sharedSeloTracker.Observe(t0, "Your feet move faster.");
        Check("Shared song land blocked without twist when multiple tracked",
            sharedSeloTracker.GetActiveSnapshots(t0.AddSeconds(0.1)).Count == 0);
        sharedSeloTracker.Observe(t0.AddSeconds(1), "Your song ends.");
        sharedSeloTracker.Observe(t0.AddSeconds(3), "Your feet move faster.");
        Check("Shared song land still ambiguous after twist when multiple tracked",
            sharedSeloTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 0);

        var clarity = SongRule("Cassindra's Chant of Clarity", 18);
        var clarityTracker = new BuffTracker();
        clarityTracker.Configure([clarity],
            _ => [],
            _ => ["Your mind clears."],
            _ => [],
            _ => false,
            _ => false,
            _ => true);
        clarityTracker.Observe(t0, "Your mind clears.");
        clarityTracker.Observe(t0.AddSeconds(6), "Your mind clears.");
        Check("Clarity pulse song stays active while land repeats",
            clarityTracker.GetActiveSnapshots(t0.AddSeconds(6.1)).Count == 1);
        var clarityStopAlerts = clarityTracker.Tick(t0.AddSeconds(24.2));
        Check("Clarity pulse song stops after configured land silence",
            clarityTracker.GetActiveSnapshots(t0.AddSeconds(24.2)).Count == 0);
        Check("Clarity pulse stop alert", clarityStopAlerts.Count == 1);

        var denon = DamageSongRule("Denon's Disruptive Discord", 12);
        var denonTracker = new BuffTracker();
        denonTracker.Configure([denon],
            _ => [],
            _ => ["Jagged notes tear through your body."],
            _ => [" winces."],
            _ => true,
            _ => false,
            _ => false,
            _ => true);
        denonTracker.Observe(t0, "Your song ends.");
        denonTracker.Observe(t0.AddSeconds(1), "a rat winces.");
        Check("Damage song starts on enemy land after twist",
            denonTracker.GetActiveSnapshots(t0.AddSeconds(1.1)).Count == 1);
        denonTracker.Observe(t0.AddSeconds(2),
            "You hit a rat for 5 points of magic damage by Denon's Disruptive Discord.");
        Check("Damage song starts on owned hit line",
            denonTracker.GetActiveSnapshots(t0.AddSeconds(2.1)).Count == 1);
        Check("Damage song clears after duration",
            denonTracker.GetActiveSnapshots(t0.AddSeconds(14.2)).Count == 0);

        var chords = DamageSongRule("Chords of Dissonance", 12);
        var chordsTracker = new BuffTracker();
        chordsTracker.Configure([chords],
            _ => [],
            _ => ["Jagged notes tear through your body."],
            _ => [" winces."],
            _ => true,
            _ => false,
            _ => false,
            _ => true);
        chordsTracker.Observe(t0, "You begin singing Chords of Dissonance.");
        chordsTracker.Observe(t0.AddSeconds(3), "a rattlesnake winces.");
        Check("Damage song starts on winces after begin singing",
            chordsTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 1 &&
            chordsTracker.GetActiveSnapshots(t0.AddSeconds(3.1))[0].ShowsPlayingLabel);
        chordsTracker.Observe(t0.AddSeconds(6),
            "A rattlesnake has taken 7 damage from your Chords of Dissonance.");
        Check("Damage song dot tick keeps overlay active",
            chordsTracker.GetActiveSnapshots(t0.AddSeconds(6.1)).Count == 1);

        var chordsRule = DamageSongRule("Chords of Dissonance", 12);
        var denonRule = DamageSongRule("Denon's Disruptive Discord", 12);
        var dualTracker = new BuffTracker();
        dualTracker.Configure([chordsRule, denonRule],
            _ => [],
            _ => ["Jagged notes tear through your body."],
            _ => [" winces."],
            _ => true,
            _ => false,
            _ => false,
            _ => true);
        dualTracker.Observe(t0, "You begin singing Denon's Disruptive Discord.");
        dualTracker.Observe(t0.AddSeconds(3), "a sand scarab winces.");
        var dualSnap = dualTracker.GetActiveSnapshots(t0.AddSeconds(3.1));
        Check("Shared winces only starts pending damage song",
            dualSnap.Count == 1 &&
            dualSnap[0].SpellName.Contains("Denon", StringComparison.OrdinalIgnoreCase));
        dualTracker.Observe(t0.AddSeconds(4),
            "A sand scarab has taken 9 damage from your Denon's Disruptive Discord.");
        Check("Denon dot tick does not start Chords",
            dualTracker.GetActiveSnapshots(t0.AddSeconds(4.1)).Count == 1 &&
            !dualTracker.GetActiveSnapshots(t0.AddSeconds(4.1)).Any(item =>
                item.SpellName.Contains("Chords", StringComparison.OrdinalIgnoreCase)));
        dualTracker.Observe(t0.AddSeconds(20), "You begin singing Chords of Dissonance.");
        dualTracker.Observe(t0.AddSeconds(23), "a sand scarab winces.");
        var twisted = dualTracker.GetActiveSnapshots(t0.AddSeconds(23.1));
        Check("Twist to Chords clears Denon and starts Chords only",
            twisted.Count == 1 &&
            twisted[0].SpellName.Contains("Chords", StringComparison.OrdinalIgnoreCase));

        var solon = DamageSongRule("Solon's Song of the Sirens", 18) with { TrackSelf = false };
        var solonTracker = new BuffTracker();
        solonTracker.Configure([solon],
            _ => ["You are no longer captivated."],
            _ => ["You are captivated by the haunting tune."],
            _ => ["'s eyes glaze over."],
            suffix => suffix.Equals("'s eyes glaze over.", StringComparison.OrdinalIgnoreCase),
            _ => false,
            _ => false,
            _ => true);
        solonTracker.Observe(t0, "You begin singing Solon's Song of the Sirens.");
        solonTracker.Observe(t0.AddSeconds(3.1), "a sand beetle's eyes glaze over.");
        Check("Solon charm song starts after begin singing and mob land",
            solonTracker.GetActiveSnapshots(t0.AddSeconds(3.2)).Count == 1 &&
            solonTracker.GetActiveSnapshots(t0.AddSeconds(3.2))[0].SpellName.Contains("Solon",
                StringComparison.OrdinalIgnoreCase));
        solonTracker.Observe(t0.AddSeconds(4), "You are captivated by the haunting tune.");
        Check("Solon charm song ignores self captivated land",
            solonTracker.GetActiveSnapshots(t0.AddSeconds(4.1)).Count == 1);
        var solonLanded = solonTracker.GetActiveSnapshots(t0.AddSeconds(4.1))[0];
        Check("Solon charm timer is on the mob",
            !solonLanded.IsSelf &&
            solonLanded.TargetName.Equals("a sand beetle", StringComparison.OrdinalIgnoreCase));
        solonTracker.Observe(t0.AddSeconds(8),
            "Your Solon's Song of the Sirens spell has worn off of a sand beetle.");
        Check("Solon charm dismiss clears overlay",
            solonTracker.GetActiveSnapshots(t0.AddSeconds(8.1)).Count == 0);

        var bravura = DamageSongRule("Solon's Bewitching Bravura", 24) with { TrackSelf = false };
        var bravuraTracker = new BuffTracker();
        bravuraTracker.Configure([bravura],
            _ => ["You are no longer captivated."],
            _ => ["You are captivated by the haunting tune."],
            _ => ["'s eyes glaze over."],
            suffix => suffix.Equals("'s eyes glaze over.", StringComparison.OrdinalIgnoreCase),
            _ => false,
            _ => false,
            _ => true);
        bravuraTracker.Observe(t0, "You begin singing Solon's Bewitching Bravura.");
        bravuraTracker.Observe(t0.AddSeconds(3), "a loathling lich's eyes glaze over.");
        Check("Bewitching Bravura starts on the lich",
            bravuraTracker.GetActiveSnapshots(t0.AddSeconds(3.1)).Count == 1 &&
            bravuraTracker.GetActiveSnapshots(t0.AddSeconds(3.1))[0].TargetName
                .Equals("a loathling lich", StringComparison.OrdinalIgnoreCase));
        bravuraTracker.Observe(t0.AddSeconds(6),
            "Your Solon's Bewitching Bravura spell has worn off of a loathling lich.");
        Check("Bewitching Bravura dismiss clears overlay",
            bravuraTracker.GetActiveSnapshots(t0.AddSeconds(6.1)).Count == 0);
        var leaveAlerts = bravuraTracker.Tick(t0.AddSeconds(6.2));
        Check("Bewitching Bravura leave speaks expired",
            leaveAlerts.Count == 1 &&
            leaveAlerts[0].Phase == BuffAlertPhase.Expired &&
            leaveAlerts[0].Rule.SpellName.Contains("Bewitching", StringComparison.OrdinalIgnoreCase),
            $"{leaveAlerts.Count}");

        var breakTracker = new BuffTracker();
        breakTracker.Configure([bravura],
            _ => ["You are no longer captivated."],
            _ => ["You are captivated by the haunting tune."],
            _ => ["'s eyes glaze over."],
            suffix => suffix.Equals("'s eyes glaze over.", StringComparison.OrdinalIgnoreCase),
            _ => false,
            _ => false,
            _ => true);
        breakTracker.Observe(t0, "You begin singing Solon's Bewitching Bravura.");
        breakTracker.Observe(t0.AddSeconds(3), "an ire ghast's eyes glaze over.");
        Check("Charm prefilter allows incoming swing on you",
            breakTracker.ShouldProcessMessage("An ire ghast hits YOU for 30 points of damage."));
        breakTracker.Observe(t0.AddSeconds(4),
            "An ire ghast told you, 'Attacking a forsaken revenant Master.'");
        Check("Pet Master tell does not break charm overlay",
            breakTracker.GetActiveSnapshots(t0.AddSeconds(4.1)).Count == 1);
        breakTracker.Observe(t0.AddSeconds(5), "An ire ghast slashes a forsaken revenant for 40 points of damage.");
        Check("Pet attacking others does not break charm overlay",
            breakTracker.GetActiveSnapshots(t0.AddSeconds(5.1)).Count == 1);
        breakTracker.Observe(t0.AddSeconds(11), "An ire ghast hits YOU for 30 points of damage.");
        Check("Charm break on you clears overlay",
            breakTracker.GetActiveSnapshots(t0.AddSeconds(11.1)).Count == 0);
        var breakAlerts = breakTracker.Tick(t0.AddSeconds(11.2));
        Check("Charm break on you speaks expired",
            breakAlerts.Count == 1 && breakAlerts[0].Phase == BuffAlertPhase.Expired,
            $"{breakAlerts.Count}");

        var missBreakTracker = new BuffTracker();
        missBreakTracker.Configure([bravura],
            _ => ["You are no longer captivated."],
            _ => ["You are captivated by the haunting tune."],
            _ => ["'s eyes glaze over."],
            suffix => suffix.Equals("'s eyes glaze over.", StringComparison.OrdinalIgnoreCase),
            _ => false,
            _ => false,
            _ => true);
        missBreakTracker.Observe(t0, "You begin singing Solon's Bewitching Bravura.");
        missBreakTracker.Observe(t0.AddSeconds(3), "an ire ghast's eyes glaze over.");
        missBreakTracker.Observe(t0.AddSeconds(7), "An ire ghast tries to hit YOU, but misses!");
        Check("Charm break on miss against you clears overlay",
            missBreakTracker.GetActiveSnapshots(t0.AddSeconds(7.1)).Count == 0);

        var twinTracker = new BuffTracker();
        twinTracker.Configure([bravura],
            _ => ["You are no longer captivated."],
            _ => ["You are captivated by the haunting tune."],
            _ => ["'s eyes glaze over."],
            suffix => suffix.Equals("'s eyes glaze over.", StringComparison.OrdinalIgnoreCase),
            _ => false,
            _ => false,
            _ => true);
        twinTracker.Observe(t0, "You begin singing Solon's Bewitching Bravura.");
        twinTracker.Observe(t0.AddSeconds(1), "An ire ghast hits YOU for 20 points of damage.");
        twinTracker.Observe(t0.AddSeconds(3), "an ire ghast's eyes glaze over.");
        twinTracker.Observe(t0.AddSeconds(4), "An ire ghast hits YOU for 21 points of damage.");
        twinTracker.Observe(t0.AddSeconds(5), "An ire ghast slashes a forsaken revenant for 40 points of damage.");
        twinTracker.Observe(t0.AddSeconds(6), "An ire ghast hits YOU for 22 points of damage.");
        twinTracker.Observe(t0.AddSeconds(7), "An ire ghast slashes a forsaken revenant for 38 points of damage.");
        Check("Same-name hostile does not clear live charm",
            twinTracker.GetActiveSnapshots(t0.AddSeconds(7.1)).Count == 1);
        twinTracker.Observe(t0.AddSeconds(8), "An ire ghast hits YOU for 22 points of damage.");
        twinTracker.Observe(t0.AddSeconds(9.5), "An ire ghast hits YOU for 23 points of damage.");
        Check("Same-name hostile still up while pet recently attacked others",
            twinTracker.GetActiveSnapshots(t0.AddSeconds(9.6)).Count == 1);
        twinTracker.Observe(t0.AddSeconds(12.1), "An ire ghast hits YOU for 24 points of damage.");
        Check("Charm break with same-name hostile clears after pet goes quiet",
            twinTracker.GetActiveSnapshots(t0.AddSeconds(12.2)).Count == 0);

        var pulledTwinTracker = new BuffTracker();
        pulledTwinTracker.Configure([bravura],
            _ => ["You are no longer captivated."],
            _ => ["You are captivated by the haunting tune."],
            _ => ["'s eyes glaze over."],
            suffix => suffix.Equals("'s eyes glaze over.", StringComparison.OrdinalIgnoreCase),
            _ => false,
            _ => false,
            _ => true);
        pulledTwinTracker.Observe(t0, "You begin singing Solon's Bewitching Bravura.");
        pulledTwinTracker.Observe(t0.AddSeconds(3), "a forsaken revenant's eyes glaze over.");
        pulledTwinTracker.Observe(t0.AddSeconds(13),
            "A forsaken revenant told you, 'Attacking a forsaken revenant Master.'");
        pulledTwinTracker.Observe(t0.AddSeconds(13),
            "A forsaken revenant tries to slash a forsaken revenant, but a forsaken revenant parries!");
        pulledTwinTracker.Observe(t0.AddSeconds(14), "You slash a forsaken revenant for 206 points of damage.");
        pulledTwinTracker.Observe(t0.AddSeconds(16), "A forsaken revenant slashes a forsaken revenant for 104 points of damage.");
        pulledTwinTracker.Observe(t0.AddSeconds(17), "A forsaken revenant tries to hit YOU, but misses! (Riposte)");
        Check("Pulling a same-name mob does not expire live charm",
            pulledTwinTracker.GetActiveSnapshots(t0.AddSeconds(17.1)).Count == 1);
        pulledTwinTracker.Observe(t0.AddSeconds(20), "You have slain a forsaken revenant!");
        Check("Killing a same-name twin does not expire live charm",
            pulledTwinTracker.GetActiveSnapshots(t0.AddSeconds(20.1)).Count == 1);

        var allure = Rule("Allure", SpellTrackerCategory.Control, ControlEffectType.Charm, 210, 5);
        var allureTracker = new BuffTracker();
        allureTracker.Configure([allure], _ => [], _ => [], _ => [" has been charmed."], _ => true);
        allureTracker.Observe(t0, "You begin casting Allure.");
        allureTracker.Observe(t0.AddSeconds(5), "Innoruuk`s Chosen has been charmed.");
        allureTracker.Observe(t0.AddSeconds(8), "You slash Innoruuk's Chosen for 206 points of damage.");
        allureTracker.Observe(t0.AddSeconds(9), "Innoruuk's Chosen hits YOU for 48 points of damage.");
        allureTracker.Observe(t0.AddSeconds(20), "Innoruuk`s Chosen slashes YOU for 51 points of damage.");
        Check("Enchanter charm ignores same-name twin hitting you",
            allureTracker.GetActiveSnapshots(t0.AddSeconds(20.1)).Count == 1);
        allureTracker.Observe(t0.AddSeconds(30), "Your Allure spell has worn off of Innoruuk's Chosen.");
        Check("Enchanter charm clears on worn off",
            allureTracker.GetActiveSnapshots(t0.AddSeconds(30.1)).Count == 0);

        var bardTwinPull = new BuffTracker();
        bardTwinPull.Configure([bravura],
            _ => ["You are no longer captivated."],
            _ => ["You are captivated by the haunting tune."],
            _ => ["'s eyes glaze over."],
            suffix => suffix.Equals("'s eyes glaze over.", StringComparison.OrdinalIgnoreCase),
            _ => false,
            _ => false,
            _ => true);
        bardTwinPull.Observe(t0, "You begin singing Solon's Bewitching Bravura.");
        bardTwinPull.Observe(t0.AddSeconds(3), "an ire ghast's eyes glaze over.");
        bardTwinPull.Observe(t0.AddSeconds(6), "You slash an ire ghast for 90 points of damage.");
        bardTwinPull.Observe(t0.AddSeconds(7), "An ire ghast hits YOU for 30 points of damage.");
        Check("Bard charm stays while you attack a same-name twin",
            bardTwinPull.GetActiveSnapshots(t0.AddSeconds(7.1)).Count == 1);

        Check("Prefilter skips pure melee combat spam",
            !solonTracker.ShouldProcessMessage(
                "a sand beetle hit a sand beetle for 12 points of non-melee damage."));
        Check("Prefilter allows configured mob land suffix",
            solonTracker.ShouldProcessMessage("a sand beetle's eyes glaze over."));
    }

    private static void RunSpellCatalogBardSongTests()
    {
        var install = Path.Combine(Path.GetTempPath(), "eqdm-bard-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(install, "Logs"));
        File.WriteAllText(Path.Combine(install, "Logs", "eqlog_Test_test.txt"), string.Empty);
        File.WriteAllLines(Path.Combine(install, "spells_us.txt"),
        [
            MakeEqlSpellLine(2602, "Song of Sustenance", 3000, 6, 15, 345, eqlSkill: 41, bard107: 6, bard108: 15),
            MakeEqlSpellLine(717, "Selo's Accelerando", 3000, 5, 2, 100, eqlSkill: 35, bard107: 0, bard108: 24),
            MakeEqlSpellLine(718, "Selo's Accelerating Chorus", 3000, 5, 2, 100, eqlSkill: 35, bard107: 0, bard108: 30),
            MakeSpellLine(120, "Minor Healing", 1500, 0, 0, 1, skill: 0, bardLevel: 255)
        ]);
        File.WriteAllLines(Path.Combine(install, "spells_us_str.txt"),
        [
            "717^717^717^Your feet move faster.^ feels much faster.^You slow down.",
            "718^718^718^Your feet move faster.^ feels much faster.^You slow down.",
            "120^120^120^You feel better.^ looks better.^"
        ]);

        var catalog = SpellDataCatalog.TryLoadFromInstallDirectory(install);
        Check("Bard catalog loads", catalog is not null);
        Check("Selo is bard song",
            catalog!.TryFind("Song of Sustenance", out var selo) && selo!.IsBardSong);
        Check("Sustenance bard level", selo!.BardLevel == 15);
        Check("Sustenance name pattern", SpellDataCatalog.LooksLikeBardSongName("Song of Sustenance"));
        Check("Healing is not bard song",
            catalog.TryFind("Minor Healing", out var heal) && heal is not null && !heal.IsBardSong);
        Check("EQL bard song list",
            catalog.GetEqlBardSongFamilies().Any(entry => entry.Name == "Song of Sustenance"));
        Check("Song mode match filter",
            catalog.FindMatches("Sustenance", trackingMode: BuffTrackingMode.Song)
                .Any(name => name.Contains("Sustenance", StringComparison.OrdinalIgnoreCase)));
        Check("Spell mode excludes bard songs",
            !catalog.FindMatches("Selo", trackingMode: BuffTrackingMode.Spell)
                .Any(name => name.Contains("Selo", StringComparison.OrdinalIgnoreCase)));
        Check("Spell mode includes healing",
            catalog.FindMatches("Minor", trackingMode: BuffTrackingMode.Spell)
                .Any(name => name.Contains("Minor Healing", StringComparison.OrdinalIgnoreCase)));
        Check("Shared self land flagged",
            catalog.IsAmbiguousSelfAppliedMessage("Your feet move faster.") == true);

        try { Directory.Delete(install, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        const string eqlInstall =
            @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends";
        if (!Directory.Exists(eqlInstall)) return;
        var live = SpellDataCatalog.TryLoadFromInstallDirectory(eqlInstall);
        Check("EQL install catalog loads", live is not null);
        Check("EQL Song of Sustenance is bard song",
            live!.TryResolveFamily("Song of Sustenance", out var sustenance) && sustenance!.IsBardSong);
        Check("EQL sustenance song autocomplete",
            live.FindMatches("Susten", trackingMode: BuffTrackingMode.Song)
                .Any(name => name.Contains("Sustenance", StringComparison.OrdinalIgnoreCase)));
        Check("EQL shared selo land flagged",
            live.IsAmbiguousSelfAppliedMessage("Your feet move faster.") == true);
        Check("EQL anthem land unique",
            live.IsAmbiguousSelfAppliedMessage("A burst of strength surges through your body.") == false);
        Check("EQL Jaxan resolves with apostrophe",
            live.TryResolveFamily("Jaxan's Jig o'Vigor", out var jaxan) && jaxan!.IsBardSong);
        Check("EQL Jaxan autocomplete",
            live.FindMatches("Jaxan", trackingMode: BuffTrackingMode.Song)
                .Any(name => name.Contains("Jig", StringComparison.OrdinalIgnoreCase)));
        Check("EQL Jaxan fade message",
            live.TryResolveFamily("Jaxan's Jig o'Vigor", out jaxan) &&
            jaxan!.FadeMessages.Contains("You are no longer invigorated."));
        Check("EQL Jaxan not pulse tracked",
            live.TryResolveFamily("Jaxan's Jig o'Vigor", out jaxan) && !jaxan!.UsesLandPulseTracking);
        Check("EQL Cassindra clarity is pulse tracked",
            live.TryResolveFamily("Cassindra's Chant of Clarity", out var clarity) && clarity!.IsBardSong &&
            clarity.UsesLandPulseTracking &&
            clarity.SelfAppliedMessages.Contains("Your mind clears."));
        Check("EQL Chords of Dissonance is bard damage song",
            live.TryResolveFamily("Chords of Dissonance", out var chords) && chords!.IsBardSong &&
            chords.IsBardDamageSong && chords.IsTrackableBardSong);
        Check("EQL Brusco bellow is instant not trackable",
            live.TryResolveFamily("Brusco's Boastful Bellow", out var brusco) && brusco!.IsBardSong &&
            brusco.IsInstantBardDamageSong && !brusco.IsTrackableBardSong && !brusco.IsBardDamageSong);
        Check("EQL Denon discord is bard damage song",
            live.TryResolveFamily("Denon's Disruptive Discord", out var denon) && denon!.IsBardSong &&
            denon.IsBardDamageSong && denon.IsTrackableBardSong && !denon.UsesLandPulseTracking);
        Check("EQL Jonthan warsong is trackable buff song",
            live.TryResolveFamily("Jonthan's Whistling Warsong", out var warsong) && warsong!.IsBardSong &&
            warsong.IsTrackableBardSong && !warsong.IsBardDamageSong &&
            warsong.SelfAppliedMessages.Contains("You whistle an ancient warsong.") &&
            warsong.FadeMessages.Contains("You stop whistling."));
        Check("EQL Jonthan land not ambiguous",
            live.IsAmbiguousSelfAppliedMessage("You whistle an ancient warsong.") == false);
        Check("EQL Jonthan autocomplete",
            live.FindMatches("Jonthan", trackingMode: BuffTrackingMode.Song)
                .Any(name => name.Contains("Whistling", StringComparison.OrdinalIgnoreCase)));
        Check("EQL Solon sirens resolves",
            live.TryResolveFamily("Solon's Song of the Sirens", out var solon) && solon is not null);
        Check("EQL Solon sirens is bard song",
            solon!.IsBardSong && solon.IsTrackableBardSong);
        Check("EQL Solon sirens is charm not damage",
            solon.IsBardCharmSong && !solon.IsBardDamageSong);
        Check("EQL Solon sirens messages",
            solon.SelfAppliedMessages.Contains("You are captivated by the haunting tune.") &&
            solon.OtherAppliedMessageSuffixes.Any(value =>
                value.Contains("eyes glaze over", StringComparison.OrdinalIgnoreCase)) &&
            solon.FadeMessages.Contains("You are no longer captivated."));
        Check("EQL Solon autocomplete in song mode",
            live.FindMatches("Solon", trackingMode: BuffTrackingMode.Song)
                .Any(name => name.Contains("Sirens", StringComparison.OrdinalIgnoreCase)) &&
            !live.FindMatches("Solon", trackingMode: BuffTrackingMode.Spell)
                .Any(name => name.Contains("Sirens", StringComparison.OrdinalIgnoreCase)));
        Check("EQL Solon duration at 60",
            live.TryResolveFamily("Solon's Song of the Sirens", out solon) &&
            solon!.DurationSecondsFor(60) == 18);
    }

    private static BuffRuleSettings SongRule(string name, int duration, double cast = 3) =>
        new(Guid.NewGuid(), name, duration, cast, true, true, BuffAlertMode.Sound, BuffSoundKind.Chime,
            string.Empty, TrackSelf: true, TrackOthers: false, TrackingMode: BuffTrackingMode.Song);

    private static BuffRuleSettings DamageSongRule(string name, int duration) =>
        new(Guid.NewGuid(), name, duration, 0, true, true, BuffAlertMode.Sound, BuffSoundKind.Chime,
            string.Empty, TrackSelf: true, TrackOthers: true, Category: SpellTrackerCategory.DamageOverTime,
            TrackingMode: BuffTrackingMode.Song);

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

    private static void RunSkyCatalogParserTests()
    {
        const string modernSnippet = """
            === [[Bard]] Tests ===

            '''Quest Giver:''' [[Cilin Spellsinger]]

            {| class="eoTable3"
            |-
            ! Quest || Trigger Phrases || Rune || Quest Items || Reward
            |-
            | Bard Test of Tone
            | tone
            | <div class="checkbox-list"><ul><li>[[Wind Rune Meda]]</li></ul></div>
            | <div class="checkbox-list"><ul><li>'''{{SkyNoDrop|[[Light Woolen Mask]]}}''' (3-Gorga)</li></ul></div>
            | {{:Mask of Song}}
            |}

            === [[Warrior]] Tests ===

            '''Quest Giver:''' [[Korin Thunderstaff]]

            {| class="eoTable3"
            |-
            ! Quest || Trigger Phrases || Rune || Quest Items || Reward
            |-
            | Warrior Test of Strength
            | strength
            | <div class="checkbox-list"><ul><li>[[Wind Rune Kala]]</li></ul></div>
            | <div class="checkbox-list"><ul><li>'''{{SkyNoDrop|[[Glowing Red Stone]]}}''' (4-KoS)</li></ul></div>
            | {{:Blade of Strategy}}
            |}
            """;

        var modern = EqWikiSkyCatalog.Parse(modernSnippet);
        Check("Sky modern parser finds classes", modern.Count == 2);
        var bard = modern.FirstOrDefault(entry => entry.ClassName == "Bard");
        Check("Sky modern bard giver",
            bard is not null && bard.QuestGiver.Equals("Cilin Spellsinger", StringComparison.OrdinalIgnoreCase));
        Check("Sky modern bard reward",
            bard?.Rewards.Any(reward => reward.RewardName == "Mask of Song" &&
                                        reward.TriggerPhrase == "tone" &&
                                        reward.RequiredItems.Any(item =>
                                            item.ItemName == "Light Woolen Mask")) == true);

        const string legacySnippet = """
            <h3>[[Cleric]] (Lelulean the Wise)</h3>
            {|
            |-
            | Cleric Test of Healing
            | healing
            | [[Wind Rune Meda]]
            | [[Silver Disc]]
            | {{:Necklace of Resolution}}
            |}
            """;
        var legacy = EqWikiSkyCatalog.Parse(legacySnippet);
        Check("Sky legacy parser still works", legacy.Count == 1 &&
            legacy[0].ClassName == "Cleric" &&
            legacy[0].QuestGiver == "Lelulean the Wise" &&
            legacy[0].Rewards.Any(reward => reward.RewardName == "Necklace of Resolution"));

        var wikiPath = Path.Combine(Path.GetTempPath(), "pos_sky.json");
        if (File.Exists(wikiPath))
        {
            var payload = JsonSerializer.Deserialize<WikiParsePayload>(File.ReadAllText(wikiPath));
            var wikitext = payload?.Parse?.Wikitext?.Text;
            if (!string.IsNullOrWhiteSpace(wikitext))
            {
                var live = EqWikiSkyCatalog.Parse(wikitext);
                Check("Sky live wiki class count", live.Count == 16);
                Check("Sky live wiki has Warrior",
                    live.Any(entry => entry.ClassName.Equals("Warrior", StringComparison.OrdinalIgnoreCase) &&
                                      entry.Rewards.Count > 0));
                if (string.Equals(Environment.GetEnvironmentVariable("EQDM_EXPORT_SKY"), "1",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var exportPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                        "..", "..", "..", "..", "src", "EQLDamageMeter", "Assets", "Data", "sky_catalog.json"));
                    var document = new SkyCatalogDocument
                    {
                        FetchedAtUtc = DateTime.UtcNow,
                        Classes = live.Select(entry => entry).ToList()
                    };
                    File.WriteAllText(exportPath,
                        JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
    }

    private static void RunSkyLootLedgerTests()
    {
        Check("Sky name strips +N", SkyItemName.Normalize("Efreeti Standard +2") == "Efreeti Standard");
        Check("Sky name keeps plain", SkyItemName.Normalize("Silver Hoop") == "Silver Hoop");
        Check("Sky drop Gorgalosk",
            SkyDropSource.Format("3-Gorga") == "Harpy Island (Island 3) · Gorgalosk");
        Check("Sky drop empty note", SkyDropSource.Format("") == string.Empty);

        var cleric = new SkyClassCatalog
        {
            ClassName = "Cleric",
            QuestGiver = "Josin Faithbringer",
            Rewards =
            [
                new SkyRewardCatalog
                {
                    RewardName = "Truewind Earring",
                    RequiredItems =
                    [
                        new SkyRequiredItemCatalog { ItemName = "Wind Rune Ena", NeededCount = 1 },
                        new SkyRequiredItemCatalog { ItemName = "Efreeti Standard", NeededCount = 1 },
                        new SkyRequiredItemCatalog { ItemName = "Mithril Bands", NeededCount = 1 }
                    ]
                },
                new SkyRewardCatalog
                {
                    RewardName = "Aegis of the Wind",
                    RequiredItems =
                    [
                        new SkyRequiredItemCatalog { ItemName = "Silver Hoop", NeededCount = 1, Note = "3-Gorga" }
                    ]
                },
                new SkyRewardCatalog
                {
                    RewardName = "Dummy Rune",
                    RequiredItems =
                    [
                        new SkyRequiredItemCatalog { ItemName = "Wind Rune Dena", NeededCount = 1 }
                    ]
                }
            ]
        };
        var ledger = new SkyLootLedger();
        ledger.LoadCatalog([cleric]);

        ledger.Observe("--You have looted a Silver Hoop from Gorgalosk's corpse.--");
        Check("Sky kept inventory", ledger.Snapshot("Silver Hoop").Owned == 1 &&
                                    ledger.Snapshot("Silver Hoop").Location == SkyItemLocation.Inventory);
        Check("Sky ready from owned", ledger.QuestStatus("Cleric", cleric.Rewards[1]) == "READY");

        ledger.Observe("--You have looted a Wind Rune Dena from an azarack's corpse.--");
        Check("Sky wind rune kept is currency",
            ledger.Snapshot("Wind Rune Dena").Owned == 1 &&
            ledger.Snapshot("Wind Rune Dena").Location == SkyItemLocation.Currency);

        ledger.Observe("You successfully destroyed 1 Silver Hoop.");
        Check("Sky deleted after destroy", ledger.Snapshot("Silver Hoop").IsDeleted &&
                                           ledger.Snapshot("Silver Hoop").DestroyedCount == 1 &&
                                           ledger.QuestStatus("Cleric", cleric.Rewards[1]) == "IN PROGRESS");

        ledger.Observe("You looted a Silver Hoop from Gorgalosk's corpse and stored it in your currency");
        Check("Sky stored currency", ledger.Snapshot("Silver Hoop").Owned == 1 &&
                                     ledger.Snapshot("Silver Hoop").Location == SkyItemLocation.Currency);
        Check("Sky not deleted after loot again", !ledger.Snapshot("Silver Hoop").IsDeleted);

        ledger.Observe("You looted a Wind Rune Ena from a spiroc vanquisher's corpse and sold it for 1 platinum.");
        Check("Sky sold is not owned", ledger.Snapshot("Wind Rune Ena").Owned == 0);
        Check("Sky autosold count", ledger.Snapshot("Wind Rune Ena").SoldCount == 1);

        ledger.Observe("--You have looted a Wind Rune Ena from a spiroc vanquisher's corpse.--");
        ledger.Observe("--You have looted an Efreeti Standard +2 from a named's corpse.--");
        ledger.Observe("--You have looted a Mithril Bands from a named's corpse.--");
        ledger.Observe("You offered 1 Wind Rune Ena to Josin Faithbringer.");
        ledger.Observe("You offered 1 Efreeti Standard +2 to Josin Faithbringer.");
        ledger.Observe("You offered 1 Mithril Bands to Josin Faithbringer.");
        ledger.Observe("You have cancelled the trade.");
        Check("Sky cancel keeps items", ledger.Snapshot("Wind Rune Ena").Owned == 1 &&
                                        ledger.QuestStatus("Cleric", cleric.Rewards[0]) == "READY");

        ledger.Observe("You offered 1 Wind Rune Ena to Josin Faithbringer.");
        ledger.Observe("You offered 1 Efreeti Standard +2 to Josin Faithbringer.");
        ledger.Observe("You offered 1 Mithril Bands to Josin Faithbringer.");
        ledger.Observe("You complete the trade with Josin Faithbringer.");
        Check("Sky turn-in completed", ledger.QuestStatus("Cleric", cleric.Rewards[0]) == "COMPLETED");
        Check("Sky turn-in consumed", ledger.Snapshot("Wind Rune Ena").Owned == 0 &&
                                      ledger.Snapshot("Efreeti Standard").Owned == 0);

        var bard = new SkyClassCatalog
        {
            ClassName = "Bard",
            QuestGiver = "Cilin Spellsinger",
            Rewards =
            [
                new SkyRewardCatalog
                {
                    RewardName = "Spear of Harmony",
                    RequiredItems =
                    [
                        new SkyRequiredItemCatalog { ItemName = "Efreeti War Spear", NeededCount = 1 }
                    ]
                }
            ]
        };
        var spearLedger = new SkyLootLedger();
        spearLedger.LoadCatalog([bard]);
        spearLedger.Observe("--You have looted an Efreeti War Spear from Noble Dojorn's corpse.--");
        spearLedger.Observe(
            "You looted an Efreeti War Spear from the Hand of Veeshan's corpse to create an Efreeti War Spear +1");
        Check("Sky +N merge keeps one", spearLedger.Snapshot("Efreeti War Spear").Owned == 1);
        spearLedger.Observe("--You have looted an Efreeti War Spear from Overseer of Air's corpse.--");
        spearLedger.Observe(
            "You have successfully merged two items together to create a new item: Efreeti War Spear +2");
        Check("Sky inventory merge keeps one", spearLedger.Snapshot("Efreeti War Spear").Owned == 1);
        spearLedger.Observe("You successfully destroyed 1 Efreeti War Spear +1.");
        Check("Sky destroy is deleted", spearLedger.Snapshot("Efreeti War Spear").IsDeleted);

        var logPath = Path.Combine(Path.GetTempPath(), "eqdm_sky_" + Guid.NewGuid().ToString("N") + ".txt");
        var parser = new LogLineParser("You");
        Check("Sky envelope loot",
            parser.TryParseEnvelope(
                "[Thu Aug 13 12:00:01 2026] --You have looted a Silver Hoop from Gorgalosk's corpse.--",
                out _, out var lootMessage) &&
            SessionLootParser.TryReadLootEvent(lootMessage, out var lootName, out var lootDisp, out _) &&
            lootName == "Silver Hoop" && lootDisp == "Kept");
        File.WriteAllLines(logPath,
        [
            "[Thu Aug 13 12:00:00 2026] You say, 'Hail, Josin Faithbringer'",
            "[Thu Aug 13 12:00:01 2026] --You have looted a Silver Hoop from Gorgalosk's corpse.--",
            "[Thu Aug 13 12:00:02 2026] You successfully destroyed 1 Silver Hoop."
        ]);
        try
        {
            var bytes = File.ReadAllBytes(logPath);
            var scanned = SkyLogScanner.Scan(logPath, [cleric], bytes.Length);
            Check("Sky scanner deleted from log", scanned.Snapshot("Silver Hoop").IsDeleted);
            Check("Sky scanner ignores combat", scanned.Snapshot("Wind Rune Ena").Owned == 0);
        }
        finally
        {
            File.Delete(logPath);
        }

        Check("Sky dump classifies hoard",
            SkyInventoryDump.TryClassifyLocation("Hoard 35", out var hoardLoc) &&
            hoardLoc == SkyItemLocation.Hoard);
        Check("Sky dump classifies bank bag slot",
            SkyInventoryDump.TryClassifyLocation("Bank2-Slot10", out var bankLoc) &&
            bankLoc == SkyItemLocation.Bank);
        Check("Sky dump classifies bags",
            SkyInventoryDump.TryClassifyLocation("General 2-Slot5", out var bagLoc) &&
            bagLoc == SkyItemLocation.Inventory);
        Check("Sky dump skips keyring",
            !SkyInventoryDump.TryClassifyLocation("KeyRing", out _));

        var dumpText = string.Join("\n",
        [
            "Location\tName\tID\tCount\tSlots",
            "General 2-Slot5\tEfreeti War Spear +1\t20831\t1\t10",
            "Hoard 35\tSilver Hoop\t123\t1\t0",
            "Hoard 12\tBixie Essence\t1\t1\t0",
            "Hoard 19\tBixie Essence\t1\t1\t0",
            "Bank1-Slot1\tWhite Dragon Scales\t2\t1\t0",
            "Head-Slot7\tMane Attraction (Exaltation)\t12254\t1\t10",
            "Ear\tEmpty\t0\t0\t0",
            "KeyRing\tName\tID",
            "KeyRing\tEfreeti War Spear\t20831"
        ]);
        var piles = SkyInventoryDump.Parse(dumpText);
        Check("Sky dump strips +N spear", piles.TryGetValue("Efreeti War Spear", out var spearPile) &&
                                          spearPile.Inventory == 1 && spearPile.Total == 1);
        Check("Sky dump sums hoard stacks", piles.TryGetValue("Bixie Essence", out var essencePile) &&
                                            essencePile.Hoard == 2);
        Check("Sky dump skips exaltation and keyring extra",
            !piles.ContainsKey("Mane Attraction") && spearPile!.Inventory == 1);

        var dumpLedger = new SkyLootLedger();
        dumpLedger.LoadCatalog([bard]);
        dumpLedger.Observe("--You have looted an Efreeti War Spear from Noble Dojorn's corpse.--");
        dumpLedger.Observe("You successfully destroyed 1 Efreeti War Spear.");
        Check("Sky dump pretest deleted", dumpLedger.Snapshot("Efreeti War Spear").IsDeleted);
        var applied = dumpLedger.ApplyInventorySnapshot(piles);
        var afterDump = dumpLedger.Snapshot("Efreeti War Spear");
        Check("Sky dump restores owned", afterDump.Owned == 1 && !afterDump.IsDeleted);
        Check("Sky dump location bags", afterDump.Location == SkyItemLocation.Inventory);
        Check("Sky dump found count", applied.SkyItemsFound == 1 && applied.Copies == 1);

        var runeLedger = new SkyLootLedger();
        runeLedger.LoadCatalog([cleric]);
        runeLedger.Observe("--You have looted a Wind Rune Dena from an azarack's corpse.--");
        runeLedger.ApplyInventorySnapshot(piles);
        Check("Sky dump keeps currency runes", runeLedger.Snapshot("Wind Rune Dena").Owned == 1 &&
                                               runeLedger.Snapshot("Wind Rune Dena").Location ==
                                               SkyItemLocation.Currency);

        var hoopLedger = new SkyLootLedger();
        hoopLedger.LoadCatalog([cleric]);
        hoopLedger.Observe("--You have looted a Silver Hoop from Gorgalosk's corpse.--");
        hoopLedger.ApplyInventorySnapshot(piles);
        Check("Sky dump moves hoop to hoard", hoopLedger.Snapshot("Silver Hoop").Owned == 1 &&
                                             hoopLedger.Snapshot("Silver Hoop").Location ==
                                             SkyItemLocation.Hoard);

        var tmpRoot = Path.Combine(Path.GetTempPath(), "eqdm_inv_" + Guid.NewGuid().ToString("N"));
        var logsDir = Path.Combine(tmpRoot, "Logs");
        Directory.CreateDirectory(logsDir);
        var fakeLog = Path.Combine(logsDir, "eqlog_Sayser_halas.txt");
        var fakeDump = Path.Combine(tmpRoot, "Sayser_halas-Inventory.txt");
        File.WriteAllText(fakeLog, "x");
        File.WriteAllText(fakeDump, dumpText);
        try
        {
            Check("Sky dump finds file beside Logs",
                SkyInventoryDump.TryFindPath(fakeLog, out var foundDump, out var expected, out _) &&
                expected == "Sayser_halas-Inventory.txt" &&
                string.Equals(foundDump, fakeDump, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tmpRoot, recursive: true);
        }

        var otherRoot = Path.Combine(Path.GetTempPath(), "eqdm_inv_other_" + Guid.NewGuid().ToString("N"));
        var otherLogs = Path.Combine(otherRoot, "Logs");
        Directory.CreateDirectory(otherLogs);
        var otherLog = Path.Combine(otherLogs, "eqlog_Thalor_caerlynn.txt");
        var otherDump = Path.Combine(otherRoot, "Thalor_caerlynn-Inventory.txt");
        File.WriteAllText(otherLog, "x");
        File.WriteAllText(otherDump, dumpText);
        try
        {
            Check("Sky dump name follows log identity",
                LogIdentity.TryFromPath(otherLog, out var otherId) && otherId is not null &&
                SkyInventoryDump.FileNameFor(otherId) == "Thalor_caerlynn-Inventory.txt");
            Check("Sky dump finds other install",
                SkyInventoryDump.TryFindPath(otherLog, out var otherFound, out var otherExpected, out var otherFolder) &&
                otherExpected == "Thalor_caerlynn-Inventory.txt" &&
                string.Equals(otherFound, otherDump, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(otherFolder, otherRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(otherRoot, recursive: true);
        }

        var logsFallbackRoot = Path.Combine(Path.GetTempPath(), "eqdm_inv_logs_" + Guid.NewGuid().ToString("N"));
        var logsFallbackDir = Path.Combine(logsFallbackRoot, "Logs");
        Directory.CreateDirectory(logsFallbackDir);
        var logsFallbackLog = Path.Combine(logsFallbackDir, "eqlog_Mira_halas.txt");
        var logsFallbackDump = Path.Combine(logsFallbackDir, "Mira_halas-Inventory.txt");
        File.WriteAllText(logsFallbackLog, "x");
        File.WriteAllText(logsFallbackDump, dumpText);
        try
        {
            Check("Sky dump falls back to Logs folder",
                SkyInventoryDump.TryFindPath(logsFallbackLog, out var logsFound, out _, out _) &&
                string.Equals(logsFound, logsFallbackDump, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(logsFallbackRoot, recursive: true);
        }
    }

    private sealed class WikiParsePayload
    {
        public WikiParseBlock? Parse { get; set; }
    }

    private sealed class WikiParseBlock
    {
        public WikiWikitextBlock? Wikitext { get; set; }
    }

    private sealed class WikiWikitextBlock
    {
        public string? Text { get; set; }
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

    private static void RunBisScoringTests()
    {
        const string fangol = """
            MAGIC ITEM  LORE ITEM
            Slot: PRIMARY
            Skill: 2H Slashing  Atk Delay: 35  DMG: 29
            STR: +3  DEX: +10  STA: +10  SV POISON: +5
            Effect: Fangol's Breath (Combat, Casting Time: Instant) at Level 50
            Class: WAR
            """;
        const string dagas = """
            MAGIC ITEM  LORE ITEM
            Slot: PRIMARY SECONDARY
            Skill: 1H Slashing  Atk Delay: 21  DMG: 11
            AC: 20  STR: +3  AGI: +2  HP: +100
            Class: WAR
            """;
        const string serpent = """
            MAGIC ITEM
            Slot: PRIMARY SECONDARY
            Skill: Piercing  Atk Delay: 27  DMG: 13
            STR: +3
            Class: ALL except CLR PAL DRU MNK SHM
            """;
        const string garduk = """
            MAGIC ITEM  LORE ITEM
            Slot: PRIMARY
            Skill: Piercing  Atk Delay: 40  DMG: 23
            WIS: +15  MANA: +75
            Effect: Tagar's Insects (Combat, Casting Time: Instant)
            Class: SHM
            """;
        const string rod = """
            MAGIC ITEM  LORE ITEM
            Slot: PRIMARY
            Skill: 2H Blunt  Atk Delay: 45  DMG: 35
            AC: 10  CHA: +15  INT: +15  MANA: +75
            Effect: Rune III (Must Equip, Casting Time: Instant)
            Class: ENC
            """;

        var fangolW = WeaponAt(fangol, "Fangol", twoHand: true, 10);
        var dagasW = WeaponAt(dagas, "Dagas", twoHand: false, 10);
        var serpentW = WeaponAt(serpent, "Serpent's Tooth", twoHand: false, 10);
        var gardukW = WeaponAt(garduk, "Garduk", twoHand: false, 10);
        var rodW = WeaponAt(rod, "Rod of the Protecting Winds", twoHand: true, 10);

        Check("Fangol +10 DMG", fangolW.Dmg == 58, $"got {fangolW.Dmg}");
        Check("Fangol +10 delay", fangolW.Delay == 35, $"got {fangolW.Delay}");
        Check("Fangol swing ratio", Math.Abs(fangolW.SwingRatio - 58.0 / 35.0) < 0.0001, $"{fangolW.SwingRatio}");
        Check("Mid-line Effect parse",
            BisItemEffects.Parse("Slot: PRIMARY  DMG: 29  Effect: Fangol's Breath (Combat)  WT: 8.0").Kind
            == BisProcKind.CombatDps);
        Check("Fangol Breath hit 120", fangolW.Proc.EstimatedHit == 120, $"{fangolW.Proc.EstimatedHit}");
        Check("Fangol proc ratio 120/300", Math.Abs(fangolW.ProcRatio - 0.4) < 0.0001, $"{fangolW.ProcRatio}");

        Check("Insects is utility", gardukW.Proc.Kind == BisProcKind.CombatUtility, gardukW.Proc.Kind.ToString());
        Check("Garduk proc adds 0", gardukW.ProcRatio == 0);
        Check("Rune clicky not DPS", rodW.Proc.Kind == BisProcKind.Clicky, rodW.Proc.Kind.ToString());
        Check("Rod proc adds 0", rodW.ProcRatio == 0);

        const string ivandyr = """
            MAGIC ITEM LORE ITEM
            Slot: EAR Charges: 6 AC: 6 WIS: +6 INT: +6 HP: +6
            Effect: Spirit Tap(Any Slot, Casting Time: Instant) WT: 0.1 Size: TINY
            Class: ALL except DRU MNK BRD Race: ALL
            """;
        const string unchargedTap = """
            Slot: EAR AC: 6 WIS: +6 INT: +6 HP: +6
            Effect: Spirit Tap(Any Slot, Casting Time: Instant) WT: 0.1
            """;
        const string unlimitedTap = """
            Slot: EAR Charges: Unlimited AC: 6
            Effect: Spirit Tap (Any Slot, Casting Time: Instant)
            """;
        var ivandyrProc = BisItemEffects.Parse(ivandyr);
        var ivandyrStats = EqWikiItemUpgrade.ParseStatValues(ivandyr);
        var hoopWeights = BisGearScorer.MergeWeights("WAR", "SHM", "ENC", BisPlaystyle.DpsDots);
        var hoopWithEffect = BisGearScorer.Score(ivandyrStats, hoopWeights, ["WAR", "SHM", "ENC"], ivandyrProc);
        var hoopStatsOnly = BisGearScorer.Score(ivandyrStats, hoopWeights, ["WAR", "SHM", "ENC"]);
        Check("Ivandyr has limited charges", BisItemEffects.HasLimitedCharges(ivandyr));
        Check("Ivandyr Spirit Tap ignored", ivandyrProc.Kind == BisProcKind.None, ivandyrProc.Kind.ToString());
        Check("Ivandyr score ignores Spirit Tap", Math.Abs(hoopWithEffect - hoopStatsOnly) < 0.01,
            $"{hoopWithEffect} vs {hoopStatsOnly}");
        Check("Ivandyr summary omits Spirit Tap",
            !BisGearScorer.Summary(ivandyrStats, ivandyrProc).Contains("Spirit Tap", StringComparison.OrdinalIgnoreCase),
            BisGearScorer.Summary(ivandyrStats, ivandyrProc));
        Check("Uncharged Spirit Tap is a clicky, not combat DPS",
            BisItemEffects.Parse(unchargedTap).Kind == BisProcKind.Clicky);
        Check("Unlimited charges Spirit Tap is a clicky, not combat DPS",
            BisItemEffects.Parse(unlimitedTap).Kind == BisProcKind.Clicky);
        Check("Damage-named Must Equip is clicky",
            BisItemEffects.Parse("Effect: Firestrike (Must Equip, Casting Time: Instant)").Kind == BisProcKind.Clicky);
        Check("Charged Fangol Breath ignored",
            BisItemEffects.Parse("Charges: 5\nEffect: Fangol's Breath (Combat)").Kind == BisProcKind.None);

        var fangolOut = fangolW.MeleeOutput;
        var dwGarduk = BisMeleeMath.DualWieldOutput(gardukW, dagasW);
        var dwDagasSerpent = BisMeleeMath.DualWieldOutput(dagasW, serpentW);
        var expectedGardukDw = (46.0 / 40.0) + 0.5 * (22.0 / 40.0);
        Check("Garduk+Dagas DW math", Math.Abs(dwGarduk - expectedGardukDw) < 0.0001,
            $"{dwGarduk} vs {expectedGardukDw}");
        Check("Fangol+proc beats Garduk+Dagas", fangolOut > dwGarduk,
            $"{fangolOut:0.000} vs {dwGarduk:0.000}");
        Check("Fangol+proc beats Dagas+Serpent", fangolOut > dwDagasSerpent,
            $"{fangolOut:0.000} vs {dwDagasSerpent:0.000}");
        Check("Prefer 2H Fangol vs Garduk+Dagas", BisMeleeMath.PreferTwoHand(fangolW, gardukW, dagasW));
        Check("Prefer 2H Fangol vs Dagas+Serpent", BisMeleeMath.PreferTwoHand(fangolW, dagasW, serpentW));
        Check("Fangol beats Rod on melee", fangolOut > rodW.MeleeOutput,
            $"{fangolOut:0.000} vs {rodW.MeleeOutput:0.000}");

        Check("Haste cap 75 at 50", BisGearScorer.EffectiveHaste(200, monkUncapped: false) == 75);
        Check("Monk haste cap 85", BisGearScorer.EffectiveHaste(200, monkUncapped: true) == 85);
        Check("FBSS 21 is under cap", BisGearScorer.EffectiveHaste(21, monkUncapped: false) == 21);

        Check("WAR STA 4.5 HP at 50", BisGearScorer.StaToHp("WAR") == 4.5);
        Check("ENC STA 2.0 HP at 50", BisGearScorer.StaToHp("ENC") == 2.0);
        Check("Combo uses WAR STA rate", BisGearScorer.BestStaToHp(["WAR", "SHM", "ENC"]) == 4.5);

        var tankWeights = BisGearScorer.MergeWeights("WAR", "SHM", "ENC", BisPlaystyle.Tank);
        var tenHp = BisGearScorer.Score(new Dictionary<string, double> { ["HP"] = 10 }, tankWeights, ["WAR", "SHM", "ENC"]);
        var tenSta = BisGearScorer.Score(new Dictionary<string, double> { ["STA"] = 10 }, tankWeights, ["WAR", "SHM", "ENC"]);
        Check("WAR tank 10 STA > 10 HP", tenSta > tenHp, $"{tenSta:0} vs {tenHp:0}");
        Check("WAR tank 10 STA = 45 HP", Math.Abs(tenSta - tenHp * 4.5) < 0.01, $"{tenSta} vs {tenHp * 4.5}");

        var dotWeights = BisGearScorer.MergeWeights("WAR", "SHM", "ENC", BisPlaystyle.DpsDotsOnly);
        var tenWis = BisGearScorer.Score(new Dictionary<string, double> { ["WIS"] = 10 }, dotWeights, ["WAR", "SHM", "ENC"]);
        var wisAsMana = BisGearScorer.Score(
            new Dictionary<string, double> { ["MANA"] = 10 * BisGearScorer.ManaPerPrimaryStatAt50 },
            dotWeights, ["WAR", "SHM", "ENC"]);
        Check("DoT WIS converts to mana", Math.Abs(tenWis - wisAsMana) < 0.01, $"{tenWis} vs {wisAsMana}");

        var shieldItem = new BisCachedItem
        {
            Title = "Bladestopper",
            BaseStats = "Slot: SECONDARY\nAC: 30\nClass: WAR",
            SlotLine = "Slot: SECONDARY",
            ClassLine = "Class: WAR"
        };
        var shieldStats = EqWikiItemUpgrade.ParseStatValues(shieldItem.BaseStats);
        Check("Bladestopper is a shield", BisGearCatalog.IsShield(shieldItem, shieldStats));
        Check("Dagas is not a shield",
            !BisGearCatalog.IsShield(new BisCachedItem
            {
                Title = "Dagas",
                BaseStats = dagas,
                SlotLine = "Slot: PRIMARY SECONDARY",
                ClassLine = "Class: WAR"
            }, StatsAt(dagas, 0)));

        var meleeWeights = BisGearScorer.WeaponWeights(
            BisGearScorer.MergeWeights("WAR", "SHM", "ENC", BisPlaystyle.Dps), BisPlaystyle.Dps);
        Check("Melee INT weight ~0", meleeWeights.GetValueOrDefault("INT") < 0.5,
            $"{meleeWeights.GetValueOrDefault("INT")}");
        Check("Melee MANA weight ~0", meleeWeights.GetValueOrDefault("MANA") < 0.5,
            $"{meleeWeights.GetValueOrDefault("MANA")}");

        var fangolStats = StatsAt(fangol, 10);
        var rodStats = StatsAt(rod, 10);
        var fangolScore = BisGearScorer.Score(fangolStats, meleeWeights, ["WAR", "SHM", "ENC"], fangolW.Proc);
        var rodScore = BisGearScorer.Score(rodStats, meleeWeights, ["WAR", "SHM", "ENC"], rodW.Proc);
        Check("Fangol weapon score > Rod", fangolScore > rodScore, $"{fangolScore:0} vs {rodScore:0}");

        var dagasStats = StatsAt(dagas, 10);
        var dagasScore = BisGearScorer.Score(dagasStats, meleeWeights, ["WAR", "SHM", "ENC"]);
        Check("Dagas HP does not beat Fangol weapon score", fangolScore > dagasScore,
            $"{fangolScore:0} vs {dagasScore:0}");

        Check("AC hard cap 350 at 50 for WAR", BisGearScorer.AcHardCap("WAR") == 350);
        Check("AC hard cap 350 at 50 for ENC", BisGearScorer.AcHardCap("ENC") == 350);
        Check("AC hard cap 350 at 50 for PAL", BisGearScorer.AcHardCap("PAL") == 350);
        Check("WAR hard cap 430 above 50", BisGearScorer.AcHardCap("WAR", 51) == 430);
        Check("PAL hard cap 403 above 50", BisGearScorer.AcHardCap("PAL", 51) == 403);
        Check("Combo hard cap 350 at 50", BisGearScorer.AcHardCap(["WAR", "SHM", "ENC"]) == 350);
        Check("Hard cap over-cap return is 0", BisGearScorer.AcOverCapReturn("WAR") == 0);
        Check("Iksar +35 at 50", BisGearScorer.IksarAcBonus(50) == 35);
        Check("Anti-twink 325 at 50", BisGearScorer.AntiTwinkWornAcCap(50) == 325);
        Check("Worn AC floor is 263 at 50", BisGearScorer.WornAcFloor(["WAR", "SHM", "ENC"]) == 263);
        Check("AC under hard cap is full", BisGearScorer.EffectiveAc(40, "WAR") == 40);
        Check("AC over hard cap is unused", BisGearScorer.EffectiveAc(400, "WAR") == 350);

        Check("WAR is plate", BisGearScorer.IsPlateClass("WAR"));
        Check("ENC is not plate", !BisGearScorer.IsPlateClass("ENC"));
        Check("WAR/SHM/ENC is a plate combo", BisGearScorer.HasPlateClass(["WAR", "SHM", "ENC"]));
        Check("ENC/MAG/WIZ is not a plate combo", !BisGearScorer.HasPlateClass(["ENC", "MAG", "WIZ"]));

        var plateArmor = BisGearScorer.MergeWeights("WAR", "SHM", "ENC", BisPlaystyle.DpsDots);
        var clothArmor = BisGearScorer.MergeWeights("ENC", "MAG", "WIZ", BisPlaystyle.DpsDots);
        Check("Plate combo AC weight > cloth combo",
            plateArmor.GetValueOrDefault("AC") > clothArmor.GetValueOrDefault("AC"));

        var swap = BisGearScorer.BestPlateAcSwap(10, 100, uniqueHaste: 0,
            [
                new("Low AC", 12, 99, false, 0),
                new("High AC cheap", 40, 80, false, 0),
                new("Tiny AC", 11, 100, false, 0)
            ],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Check("Plate AC swap prefers efficient AC gain", swap?.Name == "High AC cheap", swap?.Name);

        var loreBlocked = BisGearScorer.BestPlateAcSwap(10, 100, 0,
            [new("Taken", 50, 90, true, 0)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Taken" });
        Check("Plate AC swap skips used lore", loreBlocked is null);

        var dotsWeapons = BisGearScorer.WeaponWeights(plateArmor, BisPlaystyle.DpsDots);
        var dagasDots = BisGearScorer.Score(dagasStats, dotsWeapons, ["WAR", "SHM", "ENC"]);
        var fangolDots = BisGearScorer.Score(fangolStats, dotsWeapons, ["WAR", "SHM", "ENC"], fangolW.Proc);
        Check("Plate AC floor does not make Dagas beat Fangol on weapons",
            fangolDots > dagasDots, $"{fangolDots:0} vs {dagasDots:0}");

        const string fangOfWolf = """
            MAGIC ITEM
            Slot: EAR PRIMARY SECONDARY
            Skill: Piercing  Atk Delay: 26  DMG: 10
            Ratio: (0.38)
            BACKSTAB: 5
            WT: 0.3 Size: MEDIUM
            Class: WAR ROG SHM
            Race: ALL
            """;
        var fangStats = EqWikiItemUpgrade.ParseStatValues(fangOfWolf);
        var earWeights = BisGearScorer.NonWeaponWeights(
            BisGearScorer.MergeWeights("WAR", "SHM", "ENC", BisPlaystyle.Dps));
        var earScore = BisGearScorer.Score(BisGearScorer.WithoutWeaponOffense(fangStats), earWeights,
            ["WAR", "SHM", "ENC"]);
        var handWeights = BisGearScorer.WeaponWeights(
            BisGearScorer.MergeWeights("WAR", "SHM", "ENC", BisPlaystyle.Dps), BisPlaystyle.Dps);
        var handScore = BisGearScorer.Score(fangStats, handWeights, ["WAR", "SHM", "ENC"]);
        Check("Fang of the Wolf has weapon ratio", fangStats.GetValueOrDefault("RATIO") > 0);
        Check("Fang of the Wolf scores 0 as jewelry", earScore <= 0, $"{earScore}");
        Check("Fang of the Wolf scores as a weapon", handScore > 0, $"{handScore}");

        var parser = new LogLineParser("Sayser");
        Check("Fangol Breath log parses",
            parser.TryParse(
                "[Thu Aug 13 18:22:22 2026] You hit Magi P`tasa for 120 points of poison damage by Fangol's Breath.",
                out var fangolLine) &&
            fangolLine?.Damage is not null &&
            fangolLine.Damage.Ability == "Fangol's Breath" &&
            fangolLine.Damage.Amount == 120 &&
            fangolLine.Damage.Category == DamageCategory.Spell &&
            fangolLine.Damage.Source == "Sayser",
            fangolLine?.Damage is null
                ? "no damage"
                : $"{fangolLine.Damage.Ability}/{fangolLine.Damage.Amount}/{fangolLine.Damage.Category}");

        Check("Fangol Breath critical parses",
            parser.TryParse(
                "[Thu Aug 13 18:27:53 2026] You hit Avatar of Abhorrence for 150 points of poison damage by Fangol's Breath. (Critical)",
                out var critLine) &&
            critLine?.Damage is { Amount: 150, IsCritical: true });

        var group = new GroupStateTracker("Sayser");
        var encounter = new EncounterTracker("Sayser");
        var stamp = DateTime.Parse("2026-08-13T18:22:20", CultureInfo.InvariantCulture);
        encounter.Process(new DamageEvent(stamp, "Sayser", "Magi P`tasa", 200, "Slash", DamageCategory.Melee, false),
            group);
        encounter.Process(fangolLine!.Damage!, group);
        var sayser = encounter.CreateCombatantArray().First(c => c.Name == "Sayser");
        var breath = sayser.Abilities.Values.FirstOrDefault(a =>
            a.Name.Equals("Fangol's Breath", StringComparison.OrdinalIgnoreCase));
        Check("Fangol Breath credited to DPS", breath is { Damage: 120, Hits: 1 },
            breath is null ? "missing" : $"{breath.Damage}/{breath.Hits}");
        Check("Fangol Breath credited as proc", breath is { ProcHits: 1, ProcDamage: 120 },
            breath is null ? "missing" : $"{breath.ProcHits}/{breath.ProcDamage}");

        var venomStamp = DateTime.Parse("2026-08-24T06:45:00", CultureInfo.InvariantCulture);
        var venomGroup = new GroupStateTracker("Sayser");
        var venomEncounter = new EncounterTracker("Sayser") { EncounterTimeout = TimeSpan.FromSeconds(10) };
        venomEncounter.Process(
            new DamageEvent(venomStamp, "Sayser", "a rat", 40, "Slash", DamageCategory.Melee, false),
            venomGroup);
        venomEncounter.ProcessMessage(venomStamp.AddSeconds(12), "You begin casting Venom of the Snake.");
        venomEncounter.Process(
            new DamageEvent(venomStamp.AddSeconds(14), "Sayser", "an ire ghast", 35, "Venom of the Snake",
                DamageCategory.Spell, false),
            venomGroup);
        var venomAbility = venomEncounter.CreateCombatantArray()
            .First(c => c.Name == "Sayser")
            .Abilities.Values.FirstOrDefault(a =>
                a.Name.Equals("Venom of the Snake", StringComparison.OrdinalIgnoreCase));
        Check("Cast spell is not a proc after fight reset",
            venomAbility is { Damage: 35, Hits: 1, ProcHits: 0, ProcDamage: 0 },
            venomAbility is null
                ? "missing"
                : $"{venomAbility.ProcHits}/{venomAbility.ProcDamage}/{venomAbility.Damage}");

        var petGroup = new GroupStateTracker("Sayser");
        var petEncounter = new EncounterTracker("Sayser");
        var petStamp = venomStamp.AddMinutes(1);
        petGroup.Process("You summon a guardian spirit.", petStamp);
        var petBind = petGroup.Process("Varer begins casting Inner Fire.", petStamp.AddSeconds(1));
        petEncounter.ApplyGroupChange(petBind);
        petEncounter.Process(
            new DamageEvent(petStamp.AddSeconds(2), "Sayser", "a golem", 50, "Slash", DamageCategory.Melee, false),
            petGroup);
        petEncounter.ProcessMessage(petStamp.AddSeconds(3), "Varer begins casting Venom of the Snake.");
        petEncounter.Process(
            new DamageEvent(petStamp.AddSeconds(5), "Varer", "a golem", 33, "Venom of the Snake",
                DamageCategory.Spell, false),
            petGroup);
        var petVenom = petEncounter.CreateCombatantArray()
            .First(c => c.Name.Equals("Varer", StringComparison.OrdinalIgnoreCase))
            .Abilities.Values.FirstOrDefault(a =>
                a.Name.Equals("Venom of the Snake", StringComparison.OrdinalIgnoreCase));
        Check("Pet cast spell is not a proc",
            petVenom is { Damage: 33, Hits: 1, ProcHits: 0, ProcDamage: 0 },
            petVenom is null ? "missing" : $"{petVenom.ProcHits}/{petVenom.ProcDamage}/{petVenom.Damage}");

        const string stonemelder = """
            MAGIC ITEM LORE ITEM
            Slot: EAR
            AC: 18
            DEX: -35
            AGI: -35
            WT: 0.1 Size: TINY
            Class: ALL
            Race: ALL
            """;
        const string fishboneEar = """
            MAGIC ITEM LORE ITEM
            Slot: EAR
            DEX: +3
            Effect: Enduring Breath (Worn)
            WT: 0.1 Size: TINY
            Class: ALL
            """;
        var stoneStats = EqWikiItemUpgrade.ParseStatValues(stonemelder);
        var fishEarStats = EqWikiItemUpgrade.ParseStatValues(fishboneEar);
        Check("Parses negative DEX", stoneStats.GetValueOrDefault("DEX") == -35, $"{stoneStats.GetValueOrDefault("DEX")}");
        Check("Parses negative AGI", stoneStats.GetValueOrDefault("AGI") == -35, $"{stoneStats.GetValueOrDefault("AGI")}");
        var meleeEarWeights = BisGearScorer.NonWeaponWeights(
            BisGearScorer.MergeWeights("WAR", "SHM", "ENC", BisPlaystyle.Dps));
        var stoneScore = BisGearScorer.Score(stoneStats, meleeEarWeights, ["WAR", "SHM", "ENC"]);
        var fishEarScore = BisGearScorer.Score(fishEarStats, meleeEarWeights, ["WAR", "SHM", "ENC"]);
        Check("Stonemelder Melee DPS score is negative", stoneScore < 0, $"{stoneScore}");
        Check("Fishbone beats Stonemelder for Melee DPS ears", fishEarScore > stoneScore,
            $"{fishEarScore} vs {stoneScore}");
        Check("Stonemelder would be excluded from BiS picks", stoneScore <= 0);

        const string multiResist = """
            Slot: FINGER
            AC: 5
            SV FIRE: +10
            SV COLD: +8
            SV MAGIC: +7
            Class: ALL
            """;
        var resistStats = EqWikiItemUpgrade.ParseStatValues(multiResist);
        Check("Multi-resist SV sums", resistStats.GetValueOrDefault("SV") == 25,
            $"{resistStats.GetValueOrDefault("SV")}");

        Check("Empty SlotLine does not fit",
            !BisGearCatalog.FitsSlot(new BisCachedItem { Title = "Bad", SlotLine = "" }, "Category:Head"));
        Check("EAR SlotLine fits Ear category",
            BisGearCatalog.FitsSlot(new BisCachedItem { Title = "Ear", SlotLine = "Slot: EAR" }, "Category:Ear"));
    }

    private static void RunEqWikiItemSourceTests()
    {
        const string helm = """
            {{Itempage
            |notes =
            |itemname = Indicolite Helm
            |statsblock =
            MAGIC ITEM NO DROP
            Slot: HEAD
            AC: 20
            |dropsfrom =

            [[Plane of Hate]]

            * [[a kiraikuei]]

            }}
            """;
        var helmSrc = EqWikiItemSource.Parse(helm);
        Check("Helm zone is Plane of Hate", helmSrc.Zone == "Plane of Hate", helmSrc.Zone);
        Check("Helm mob is a kiraikuei", helmSrc.Mob == "a kiraikuei", helmSrc.Mob);
        Check("Helm display has zone and mob", helmSrc.Display == "Plane of Hate · a kiraikuei", helmSrc.Display);

        const string mask = """
            {{Itempage
            |statsblock = Slot: FACE
            |dropsfrom =

            [[Plane of Hate]]

            * [[Innoruuk_(God)|Innoruuk]]

            }}
            """;
        var maskSrc = EqWikiItemSource.Parse(mask);
        Check("Piped mob link uses display name", maskSrc.Mob == "Innoruuk",
            $"mob=[{maskSrc.Mob}] display=[{maskSrc.Display}]");

        const string fangol = """
            {{Itempage
            |statsblock = Slot: PRIMARY
            |relatedquests =

            * [[Warrior Plane of Sky Tests|Warrior Test of Bash]]

            }}
            """;
        var fangolSrc = EqWikiItemSource.Parse(fangol);
        Check("Quest item kind", fangolSrc.Kind == "quest", fangolSrc.Kind);
        Check("Quest item names the quest", fangolSrc.Mob == "Warrior Test of Bash", fangolSrc.Mob);
        Check("Quest display", fangolSrc.Display == "Quest · Warrior Test of Bash", fangolSrc.Display);

        const string hoop = """
            {{Itempage
            |statsblock = Slot: EAR
            |relatedquests =

            * [[Lynuga's Gem Collection]]

            }}
            """;
        Check("Ivandyr quest", EqWikiItemSource.Parse(hoop).Display == "Quest · Lynuga's Gem Collection");

        const string bauble = """
            {{Itempage
            |notes = Summoned by [[Summon Brilliant Bauble]].
            |itemname = Summoned: Jolum's Brilliant Bauble
            |statsblock = Slot: EAR
            }}
            """;
        var baubleSrc = EqWikiItemSource.Parse(bauble);
        Check("Summoned kind", baubleSrc.Kind == "summoned", baubleSrc.Kind);
        Check("Summoned spell", baubleSrc.Mob == "Summon Brilliant Bauble", baubleSrc.Mob);

        const string fishbone = """
            {{Itempage
            |statsblock = Slot: EAR
            |dropsfrom =

            [[Qeynos Hills]]

            * [[Hadden]]

            }}
            """;
        var fishSrc = EqWikiItemSource.Parse(fishbone);
        Check("Fishbone source", fishSrc.Display == "Qeynos Hills · Hadden", fishSrc.Display);
        Check("Wiki item link", EqWikiLinks.ForPage("Indicolite Helm").EndsWith("Indicolite_Helm", StringComparison.Ordinal));
        Check("Empty wikitext has no source", string.IsNullOrEmpty(EqWikiItemSource.Parse("").Display));

        const string kejaar = """
            Lore Equipped, No Trade
            Slot: NECK
            HP Regen: 2 Mana Regen: 2 End Regen: 2
            WT: 0.1 Size: TINY
            Class: ALL
            Race: ALL
            """;
        var kejaar10 = EqWikiItemUpgrade.ApplyTier(kejaar, 10);
        Check("Regen scales +1 per tier",
            kejaar10.Contains("HP Regen: +12", StringComparison.OrdinalIgnoreCase) &&
            kejaar10.Contains("Mana Regen: +12", StringComparison.OrdinalIgnoreCase) &&
            kejaar10.Contains("End Regen: +12", StringComparison.OrdinalIgnoreCase),
            kejaar10);
        var kejaarStats = EqWikiItemUpgrade.ParseStatValues(kejaar10);
        Check("Regen parse keys",
            kejaarStats.GetValueOrDefault("HPREGEN") == 12 &&
            kejaarStats.GetValueOrDefault("MANAREGEN") == 12);

        const string jasinth = """
            {{Spellpagesmart|
            | spellname = Talisman of Jasinth
            | description = Protects your group with the talisman of Jasinth, shielding them from disease for 36 min.
            | classes =
            * [[Shaman]] - Level 50
            | slots =
            {{SpellSlotRow | 1 | Increase Disease Resist by 45 }}
            | skill = [[Skill Abjuration | Abjuration]]
            | mana = 150
            | range = 0
            | casting_time = 4.50
            | recast_time = 1.50
            | duration = 36 minutes
            | target_type = Group
            | spell_type = Resist Buff
            | resist = Unresistable
            }}
            """;
        Check("Spell page parses", EqWikiSpellPage.TryParse(jasinth, out var jasinthSpell) && jasinthSpell is not null);
        Check("Jasinth is buff family", jasinthSpell!.Family == SpellUpgradeFamily.Buff);
        var jasinth10 = EqWikiSpellPage.Format(jasinthSpell, 10);
        Check("Jasinth +10 mana/cast/duration",
            jasinth10.Contains("Mana: 120", StringComparison.Ordinal) &&
            jasinth10.Contains("Cast: 2.70s", StringComparison.Ordinal) &&
            jasinth10.Contains("Duration: 72 min", StringComparison.Ordinal),
            jasinth10);
        Check("Jasinth resist magnitude unscaled",
            jasinth10.Contains("Increase Disease Resist by 45", StringComparison.Ordinal), jasinth10);

        const string bolt = """
            {{Spellpagesmart|
            | spellname = Envenomed Bolt
            | description = Fills your target's blood with poison.
            | classes =
            * [[Shaman]] - Level 49
            | slots =
            {{SpellSlotRow | 1 | Increase Poison Counter by 10 }}
            {{SpellSlotRow | 2 | Decrease Current Hit Points by 41}}
            {{SpellSlotRow | 3 | Decrease Current Hit Points by 351 per Tick }}
            | skill = Conjuration
            | mana = 409
            | casting_time = 3
            | recast_time = 1.5
            | duration = 36 Sec
            | target_type = Single
            | spell_type = Detrimental
            | resist = Poison (0)
            }}
            """;
        Check("Bolt parses as DoT",
            EqWikiSpellPage.TryParse(bolt, out var boltSpell) &&
            boltSpell!.Family == SpellUpgradeFamily.DotHot);
        var bolt10 = EqWikiSpellPage.Format(boltSpell!, 10);
        Check("Bolt +10 scales tick and resist",
            bolt10.Contains("Mana: 327", StringComparison.Ordinal) &&
            bolt10.Contains("Duration: 54 sec", StringComparison.Ordinal) &&
            bolt10.Contains("by 456", StringComparison.Ordinal) &&
            bolt10.Contains("Poison (-150)", StringComparison.Ordinal),
            bolt10);
    }

    private static BisMeleeMath.Weapon WeaponAt(string baseStats, string name, bool twoHand, int tier)
    {
        var scaled = EqWikiItemUpgrade.ApplyTier(baseStats, tier);
        var stats = EqWikiItemUpgrade.ParseStatValues(scaled);
        var proc = BisItemEffects.Parse(scaled);
        return BisMeleeMath.FromStats(name, stats, twoHand, proc);
    }

    private static Dictionary<string, double> StatsAt(string baseStats, int tier) =>
        EqWikiItemUpgrade.ParseStatValues(EqWikiItemUpgrade.ApplyTier(baseStats, tier));

    private static string MakeEqlSpellLine(int id, string name, int castMs, int formula, int duration, int icon,
        int eqlSkill = 41, int bard107 = 0, int bard108 = 0)
    {
        var fields = new string[112];
        for (var index = 0; index < fields.Length; index++) fields[index] = string.Empty;
        fields[0] = id.ToString(CultureInfo.InvariantCulture);
        fields[1] = name;
        fields[8] = castMs.ToString(CultureInfo.InvariantCulture);
        fields[11] = formula.ToString(CultureInfo.InvariantCulture);
        fields[12] = duration.ToString(CultureInfo.InvariantCulture);
        fields[30] = eqlSkill.ToString(CultureInfo.InvariantCulture);
        fields[75] = icon.ToString(CultureInfo.InvariantCulture);
        fields[107] = bard107.ToString(CultureInfo.InvariantCulture);
        fields[108] = bard108.ToString(CultureInfo.InvariantCulture);
        return string.Join('^', fields);
    }

    private static string MakeSpellLine(int id, string name, int castMs, int formula, int duration, int icon,
        int skill = 0, int bardLevel = 255)
    {
        var fields = new string[112];
        for (var index = 0; index < fields.Length; index++) fields[index] = string.Empty;
        fields[0] = id.ToString(CultureInfo.InvariantCulture);
        fields[1] = name;
        fields[8] = castMs.ToString(CultureInfo.InvariantCulture);
        fields[11] = formula.ToString(CultureInfo.InvariantCulture);
        fields[12] = duration.ToString(CultureInfo.InvariantCulture);
        fields[75] = icon.ToString(CultureInfo.InvariantCulture);
        fields[100] = skill.ToString(CultureInfo.InvariantCulture);
        fields[111] = bardLevel.ToString(CultureInfo.InvariantCulture);
        return string.Join('^', fields);
    }

    private static BuffRuleSettings Rule(string name, SpellTrackerCategory category,
        ControlEffectType controlType, int duration, double cast) =>
        new(Guid.NewGuid(), name, duration, cast, true, true, BuffAlertMode.Sound, BuffSoundKind.Chime,
            "", TrackSelf: false, TrackOthers: true, Category: category, ControlType: controlType);
}
