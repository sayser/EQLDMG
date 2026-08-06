using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class AbilityAggregate(string name)
{
    public string Name { get; } = name;
    public long Damage { get; set; }
    public Dictionary<string, AbilityAggregate> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TargetAggregate(string name)
{
    public string Name { get; } = name;
    public long Damage { get; set; }
    public Dictionary<string, AbilityAggregate> Abilities { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CombatantAggregate(string name)
{
    public string Name { get; } = name;
    public string? OwnerName { get; set; }
    public long Damage { get; set; }
    public int Hits { get; set; }
    public int MeleeHits { get; set; }
    public int SpellHits { get; set; }
    public int MeleeCriticalHits { get; set; }
    public int SpellCriticalHits { get; set; }
    public int Misses { get; set; }
    public int SpellFizzles { get; set; }
    public int SpellResists { get; set; }
    public long DamageTaken { get; set; }
    public int IncomingHits { get; set; }
    public int IncomingMeleeHits { get; set; }
    public int IncomingMisses { get; set; }
    public int Dodges { get; set; }
    public int Parries { get; set; }
    public int Blocks { get; set; }
    public int Ripostes { get; set; }
    public int Absorbed { get; set; }
    public int SpellAbsorbs { get; set; }
    public int IncomingSpellResists { get; set; }
    public long Healing { get; set; }
    public long PotentialHealing { get; set; }
    public int DirectHeals { get; set; }
    public int HealOverTimeTicks { get; set; }
    public int CriticalHeals { get; set; }
    public Dictionary<string, AbilityAggregate> Abilities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AbilityAggregate> IncomingAbilities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AbilityAggregate> HealingAbilities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TargetAggregate> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record EncounterSnapshot(DateTime StartedAt, DateTime EndedAt,
    IReadOnlyList<CombatantAggregate> Combatants, IReadOnlyList<string> Targets);

public sealed class EncounterTracker(string localPlayerName)
{
    private sealed record RollingDamageEvent(DateTime Timestamp, string Source, string? OwnerName, int Amount);

    private readonly Dictionary<string, CombatantAggregate> _combatants = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CombatantAggregate> _retiredCombatants = [];
    private readonly HashSet<string> _hostileTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeHostileTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hostileSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DamageEvent> _pending = [];
    private readonly Queue<RollingDamageEvent> _rollingEvents = [];
    private readonly List<CombatOutcomeEvent> _pendingOutcomes = [];

    public TimeSpan EncounterTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan KillCompletionGrace { get; set; } = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RollingRetention = TimeSpan.FromSeconds(30);
    public DateTime? StartedAt { get; private set; }
    public DateTime? LastDamageAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public DateTime? CompletionCandidateAt { get; private set; }
    public bool IsFinalized { get; private set; }
    public IReadOnlyCollection<CombatantAggregate> Combatants =>
        _combatants.Values.Concat(_retiredCombatants).ToArray();

    public CombatantAggregate[] CreateCombatantArray() =>
        _combatants.Values.Concat(_retiredCombatants).ToArray();

    public void Process(DamageEvent damage, GroupStateTracker group)
    {
        var isLocal = damage.Source.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase);
        var sourceIsKnownMember = group.IsConfirmedMemberOrPet(damage.Source);
        var targetIsKnownMember = damage.Target.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                                  group.IsConfirmedMemberOrPet(damage.Target);
        var isRelevantCombat = isLocal || sourceIsKnownMember || targetIsKnownMember;
        var previousEncounterHasEnded = LastDamageAt.HasValue &&
                                        damage.Timestamp - LastDamageAt.Value > EncounterTimeout;

        FinalizeCompletedEncounterAt(damage.Timestamp);

        if (IsFinalized)
        {
            if (!isRelevantCombat) return;
            Reset();
        }

        // Keep the completed encounter visible through unrelated nearby combat. A new
        // encounter begins only when the local player or a known group contributor deals damage.
        if (previousEncounterHasEnded)
        {
            if (!isRelevantCombat)
            {
                return;
            }

            Reset();
        }

        // A non-player source damaging the local player is always incoming damage.
        // This remains true when another living NPC shares a controlled pet's name.
        if (!isLocal && (damage.Target.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                         targetIsKnownMember && !sourceIsKnownMember))
        {
            if (!damage.Source.Equals(LogLineParser.UnattributedNonMeleeSource,
                    StringComparison.OrdinalIgnoreCase) &&
                !damage.Source.Equals(LogLineParser.UnattributedDamageOverTimeSource,
                    StringComparison.OrdinalIgnoreCase))
            {
                _hostileSources.Add(damage.Source);
                _hostileTargets.Add(damage.Source);
                _activeHostileTargets.Add(damage.Source);
            }
            AddIncomingDamage(damage, group);
            return;
        }

        if (isLocal || sourceIsKnownMember)
        {
            _hostileTargets.Add(damage.Target);
            AddDamage(damage, group);
            ReplayPendingFor(damage.Target, group);
            return;
        }

        if (!group.IsGrouped)
        {
            return;
        }

        if (group.IsConfirmedMemberOrPet(damage.Target))
        {
            _hostileSources.Add(damage.Source);
            return;
        }

        _pending.Add(damage);
        _pending.RemoveAll(item => damage.Timestamp - item.Timestamp > EncounterTimeout);
    }

    public void ProcessOutcome(CombatOutcomeEvent outcome, GroupStateTracker group)
    {
        FinalizeCompletedEncounterAt(outcome.Timestamp);
        var sourceIsEligible = outcome.Source.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                               group.IsConfirmedMemberOrPet(outcome.Source);
        var targetsLocalPlayer = outcome.Target?.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) == true &&
                                 !outcome.Source.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase);
        if (targetsLocalPlayer)
        {
            if (IsFinalized || (LastDamageAt.HasValue && outcome.Timestamp - LastDamageAt.Value > EncounterTimeout))
            {
                Reset();
            }

            _hostileSources.Add(outcome.Source);
            _hostileTargets.Add(outcome.Source);
            _activeHostileTargets.Add(outcome.Source);
            TouchEncounter(outcome.Timestamp);
            AddDefensiveOutcome(localPlayerName, outcome, group);
            return;
        }

        var startsNewOffensiveEncounter = sourceIsEligible && outcome.Target is not null &&
                                          (!StartedAt.HasValue || IsFinalized ||
                                           (LastDamageAt.HasValue && outcome.Timestamp - LastDamageAt.Value > EncounterTimeout));
        if (startsNewOffensiveEncounter && outcome.Target is not null)
        {
            Reset();
            _hostileTargets.Add(outcome.Target);
            _activeHostileTargets.Add(outcome.Target);
            TouchEncounter(outcome.Timestamp);
            AddOutcome(outcome, group);
            return;
        }

        var defenderIsEligible = outcome.Target is not null &&
                                  (outcome.Target.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                                   group.IsConfirmedMemberOrPet(outcome.Target));
        if (defenderIsEligible && !sourceIsEligible && outcome.Target is not null)
        {
            if (IsFinalized || (LastDamageAt.HasValue && outcome.Timestamp - LastDamageAt.Value > EncounterTimeout))
            {
                Reset();
            }

            _hostileSources.Add(outcome.Source);
            _hostileTargets.Add(outcome.Source);
            _activeHostileTargets.Add(outcome.Source);
            TouchEncounter(outcome.Timestamp);
            AddDefensiveOutcome(outcome.Target, outcome, group);
            return;
        }

        var targetIsEligible = outcome.Target is null || _hostileTargets.Contains(outcome.Target);
        if (StartedAt.HasValue && !IsFinalized && sourceIsEligible && targetIsEligible)
        {
            if (outcome.Target is not null)
            {
                _activeHostileTargets.Add(outcome.Target);
                TouchEncounter(outcome.Timestamp);
            }
            AddOutcome(outcome, group);
            return;
        }

        _pendingOutcomes.Add(outcome);
        _pendingOutcomes.RemoveAll(item => outcome.Timestamp - item.Timestamp > EncounterTimeout);
    }

    public void ProcessHealing(HealingEvent healing, GroupStateTracker group)
    {
        if (!StartedAt.HasValue || IsFinalized) return;
        if (CompletionCandidateAt.HasValue && healing.Timestamp > CompletionCandidateAt.Value) return;
        if (LastDamageAt.HasValue && healing.Timestamp - LastDamageAt.Value > EncounterTimeout) return;
        var sourceIsEligible = healing.Source.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                               group.IsConfirmedMemberOrPet(healing.Source);
        if (!sourceIsEligible) return;

        group.TryGetPetOwner(healing.Source, out var owner);
        var combatant = GetOrCreateCombatant(healing.Source, owner);

        combatant.Healing += healing.Amount;
        combatant.PotentialHealing += healing.PotentialAmount;
        if (healing.IsOverTime) combatant.HealOverTimeTicks++;
        else combatant.DirectHeals++;
        if (healing.IsCritical) combatant.CriticalHeals++;
        if (!combatant.HealingAbilities.TryGetValue(healing.Ability, out var ability))
        {
            ability = new AbilityAggregate(healing.Ability);
            combatant.HealingAbilities[healing.Ability] = ability;
        }
        ability.Damage += healing.Amount;
    }

    public void ApplyGroupChange(GroupChange change)
    {
        if (change.Kind == GroupChangeKind.PetControlled && change.Member is not null)
        {
            _hostileTargets.Remove(change.Member);
            _activeHostileTargets.Remove(change.Member);
            _hostileSources.Remove(change.Member);
            // A new control period must not inherit damage or outcomes queued while
            // this same NPC name was hostile after an earlier Charm break.
            _pending.RemoveAll(item => item.Source.Equals(change.Member, StringComparison.OrdinalIgnoreCase));
            _pendingOutcomes.RemoveAll(item =>
                item.Source.Equals(change.Member, StringComparison.OrdinalIgnoreCase));
            if (_combatants.TryGetValue(change.Member, out var pet))
            {
                if (!string.IsNullOrWhiteSpace(pet.OwnerName) &&
                    !pet.OwnerName.Equals(change.Owner, StringComparison.OrdinalIgnoreCase))
                {
                    _combatants.Remove(change.Member);
                    _retiredCombatants.Add(pet);
                }
                else
                {
                    pet.OwnerName = change.Owner;
                }
            }
            if (!_combatants.ContainsKey(change.Member))
            {
                ReactivateRetiredCombatant(change.Member, change.Owner);
            }
        }
        else if (change.Kind == GroupChangeKind.MemberLeft && change.Member is not null)
        {
            RetireCombatant(change.Member);
            foreach (var pet in _combatants.Where(item =>
                         item.Value.OwnerName?.Equals(change.Member, StringComparison.OrdinalIgnoreCase) == true ||
                         GroupStateTracker.IsOwnedPet(item.Value.Name, change.Member)).Select(item => item.Key).ToArray())
            {
                RetireCombatant(pet);
            }
        }
        else if (change.Kind == GroupChangeKind.MemberJoined && change.Member is not null)
        {
            ReactivateRetiredCombatant(change.Member, null);
        }
        else if (change.Kind == GroupChangeKind.LocalPlayerLeft)
        {
            var local = _combatants.TryGetValue(localPlayerName, out var value) ? value : null;
            _combatants.Clear();
            _retiredCombatants.Clear();
            if (local is not null)
            {
                _combatants[localPlayerName] = local;
            }
            _pending.Clear();
        }
    }

    public void ProcessMessage(DateTime timestamp, string message)
    {
        string? defeatedTarget = null;
        const string localPrefix = "You have slain ";
        if (message.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase) && message.EndsWith('!'))
        {
            defeatedTarget = message[localPrefix.Length..^1];
        }
        else
        {
            const string marker = " has been slain by ";
            var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex > 0 && message.EndsWith('!'))
            {
                defeatedTarget = message[..markerIndex];
            }
        }

        if (defeatedTarget is null && message.EndsWith(" died.", StringComparison.OrdinalIgnoreCase))
        {
            defeatedTarget = message[..^" died.".Length];
        }

        if (defeatedTarget is null || !_hostileTargets.Contains(defeatedTarget)) return;
        _activeHostileTargets.Remove(defeatedTarget);
        // Enemy names are not unique in the log. Two living enemies can share the
        // same name, so a kill message cannot safely prove that combat has ended.
        // Give another same-named target a short window to produce combat before
        // treating the name-only death message as encounter completion.
        TouchEncounter(timestamp);
        if (_activeHostileTargets.Count == 0)
        {
            CompletionCandidateAt = timestamp;
        }
    }

    public void FinalizeIfInactive(DateTime now)
    {
        if (IsFinalized) return;
        if (CompletionCandidateAt.HasValue && now - CompletionCandidateAt.Value >= KillCompletionGrace)
        {
            FinalizeEncounter(CompletionCandidateAt.Value);
        }
        else if (LastDamageAt.HasValue && now - LastDamageAt.Value >= EncounterTimeout)
        {
            FinalizeEncounter(LastDamageAt.Value);
        }
    }

    internal void FinalizeAt(DateTime timestamp) => FinalizeEncounter(timestamp);

    private void FinalizeCompletedEncounterAt(DateTime timestamp)
    {
        if (!IsFinalized && CompletionCandidateAt.HasValue &&
            timestamp - CompletionCandidateAt.Value >= KillCompletionGrace)
        {
            FinalizeEncounter(CompletionCandidateAt.Value);
        }
    }

    public double GetElapsedSeconds(DateTime now)
    {
        if (!StartedAt.HasValue) return 0;
        var endpoint = IsFinalized
            ? EndedAt ?? LastDamageAt ?? StartedAt.Value
            : CompletionCandidateAt ?? now;
        return Math.Max(1, (endpoint - StartedAt.Value).TotalSeconds);
    }

    public long GetRollingDamage(string source, DateTime now, TimeSpan window)
    {
        if (IsFinalized || CompletionCandidateAt.HasValue) return 0;
        var cutoff = now - window;
        PruneRollingEvents(now);
        return _rollingEvents.Where(item =>
                item.Source.Equals(source, StringComparison.OrdinalIgnoreCase) && item.Timestamp > cutoff)
            .Sum(item => (long)item.Amount);
    }

    public long GetRollingDamage(IEnumerable<string> sources, DateTime now, TimeSpan window)
    {
        if (IsFinalized || CompletionCandidateAt.HasValue) return 0;
        var sourceSet = sources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = now - window;
        PruneRollingEvents(now);
        return _rollingEvents.Where(item => sourceSet.Contains(item.Source) && item.Timestamp > cutoff)
            .Sum(item => (long)item.Amount);
    }

    public long GetRollingDamageForOwner(string owner, bool includePets, DateTime now, TimeSpan window)
    {
        if (IsFinalized || CompletionCandidateAt.HasValue) return 0;
        var cutoff = now - window;
        PruneRollingEvents(now);
        return _rollingEvents.Where(item => item.Timestamp > cutoff &&
                                            (item.Source.Equals(owner, StringComparison.OrdinalIgnoreCase) ||
                                             includePets && item.OwnerName?.Equals(owner,
                                                 StringComparison.OrdinalIgnoreCase) == true))
            .Sum(item => (long)item.Amount);
    }

    public EncounterSnapshot? CreateSnapshot(DateTime now)
    {
        if (!StartedAt.HasValue) return null;
        var end = IsFinalized ? EndedAt ?? LastDamageAt ?? StartedAt.Value : CompletionCandidateAt ?? now;
        return new EncounterSnapshot(StartedAt.Value, end,
            _combatants.Values.Concat(_retiredCombatants).Select(CloneCombatant).ToArray(),
            _hostileTargets.OrderBy(name => name).ToArray());
    }

    public void Reset()
    {
        _combatants.Clear();
        _retiredCombatants.Clear();
        _hostileTargets.Clear();
        _activeHostileTargets.Clear();
        _hostileSources.Clear();
        _pending.Clear();
        _rollingEvents.Clear();
        _pendingOutcomes.Clear();
        StartedAt = null;
        LastDamageAt = null;
        EndedAt = null;
        CompletionCandidateAt = null;
        IsFinalized = false;
    }

    private void ReplayPendingFor(string target, GroupStateTracker group)
    {
        foreach (var pending in _pending.Where(item =>
                     item.Target.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                     !_hostileSources.Contains(item.Source) &&
                     group.IsConfirmedMemberOrPet(item.Source) &&
                     group.WasConfirmedMemberOrPetAt(item.Source, item.Timestamp)).ToArray())
        {
            AddDamage(pending, group);
            _pending.Remove(pending);
        }
    }

    private void AddDamage(DamageEvent damage, GroupStateTracker group)
    {
        _hostileTargets.Add(damage.Target);
        _activeHostileTargets.Add(damage.Target);
        TouchEncounter(damage.Timestamp);
        group.TryGetPetOwner(damage.Source, out var owner);
        var combatant = GetOrCreateCombatant(damage.Source, owner);

        _rollingEvents.Enqueue(new RollingDamageEvent(damage.Timestamp, damage.Source, combatant.OwnerName,
            damage.Amount));
        PruneRollingEvents(damage.Timestamp);

        combatant.Damage += damage.Amount;
        combatant.Hits++;
        if (damage.Category == DamageCategory.Melee)
        {
            combatant.MeleeHits++;
            if (damage.IsCritical) combatant.MeleeCriticalHits++;
        }
        else if (damage.Category is DamageCategory.Spell or DamageCategory.DamageOverTime)
        {
            combatant.SpellHits++;
            if (damage.IsCritical) combatant.SpellCriticalHits++;
        }

        if (!combatant.Abilities.TryGetValue(damage.Ability, out var ability))
        {
            ability = new AbilityAggregate(damage.Ability);
            combatant.Abilities[damage.Ability] = ability;
        }

        ability.Damage += damage.Amount;

        if (!combatant.Targets.TryGetValue(damage.Target, out var target))
        {
            target = new TargetAggregate(damage.Target);
            combatant.Targets[damage.Target] = target;
        }
        target.Damage += damage.Amount;
        if (!target.Abilities.TryGetValue(damage.Ability, out var targetAbility))
        {
            targetAbility = new AbilityAggregate(damage.Ability);
            target.Abilities[damage.Ability] = targetAbility;
        }
        targetAbility.Damage += damage.Amount;

        ReplayPendingOutcomes(damage, group);
    }

    private void ReplayPendingOutcomes(DamageEvent confirmingDamage, GroupStateTracker group)
    {
        foreach (var outcome in _pendingOutcomes.Where(item =>
                     confirmingDamage.Timestamp - item.Timestamp <= EncounterTimeout &&
                     item.Timestamp <= confirmingDamage.Timestamp &&
                     item.Source.Equals(confirmingDamage.Source, StringComparison.OrdinalIgnoreCase) &&
                     (item.Target is null || item.Target.Equals(confirmingDamage.Target, StringComparison.OrdinalIgnoreCase)))
                 .ToArray())
        {
            AddOutcome(outcome, group);
            _pendingOutcomes.Remove(outcome);
        }
    }

    private void AddOutcome(CombatOutcomeEvent outcome, GroupStateTracker group)
    {
        group.TryGetPetOwner(outcome.Source, out var owner);
        var combatant = GetOrCreateCombatant(outcome.Source, owner);

        switch (outcome.Kind)
        {
            case CombatOutcomeKind.MissedAttack:
            case CombatOutcomeKind.DefensiveDodge:
            case CombatOutcomeKind.DefensiveParry:
            case CombatOutcomeKind.DefensiveBlock:
            case CombatOutcomeKind.DefensiveRiposte:
            case CombatOutcomeKind.DefensiveAbsorb:
            case CombatOutcomeKind.DefensiveSpellAbsorb:
                combatant.Misses++;
                break;
            case CombatOutcomeKind.SpellFizzle:
                combatant.SpellFizzles++;
                break;
            case CombatOutcomeKind.SpellResist:
                combatant.SpellResists++;
                break;
        }
    }

    private void AddIncomingDamage(DamageEvent damage, GroupStateTracker group)
    {
        TouchEncounter(damage.Timestamp);
        group.TryGetPetOwner(damage.Target, out var owner);
        var defender = GetOrCreateCombatant(damage.Target, owner);

        defender.DamageTaken += damage.Amount;
        defender.IncomingHits++;
        if (damage.Category == DamageCategory.Melee) defender.IncomingMeleeHits++;
        if (!defender.IncomingAbilities.TryGetValue(damage.Ability, out var ability))
        {
            ability = new AbilityAggregate(damage.Ability);
            defender.IncomingAbilities[damage.Ability] = ability;
        }

        ability.Damage += damage.Amount;
    }

    private void AddDefensiveOutcome(string defenderName, CombatOutcomeEvent outcome, GroupStateTracker group)
    {
        group.TryGetPetOwner(defenderName, out var owner);
        var defender = GetOrCreateCombatant(defenderName, owner);

        switch (outcome.Kind)
        {
            case CombatOutcomeKind.DefensiveDodge:
                defender.Dodges++;
                break;
            case CombatOutcomeKind.DefensiveParry:
                defender.Parries++;
                break;
            case CombatOutcomeKind.DefensiveBlock:
                defender.Blocks++;
                break;
            case CombatOutcomeKind.DefensiveRiposte:
                defender.Ripostes++;
                break;
            case CombatOutcomeKind.DefensiveAbsorb:
                defender.Absorbed++;
                break;
            case CombatOutcomeKind.DefensiveSpellAbsorb:
                defender.Absorbed++;
                defender.SpellAbsorbs++;
                break;
            case CombatOutcomeKind.DefensiveSpellResist:
                defender.IncomingSpellResists++;
                break;
            case CombatOutcomeKind.MissedAttack:
                defender.IncomingMisses++;
                break;
        }
    }

    private void TouchEncounter(DateTime timestamp)
    {
        CompletionCandidateAt = null;
        StartedAt = !StartedAt.HasValue || timestamp < StartedAt ? timestamp : StartedAt;
        LastDamageAt = !LastDamageAt.HasValue || timestamp > LastDamageAt ? timestamp : LastDamageAt;
    }

    private CombatantAggregate GetOrCreateCombatant(string name, string? owner)
    {
        if (_combatants.TryGetValue(name, out var combatant))
        {
            if (!string.IsNullOrWhiteSpace(owner)) combatant.OwnerName = owner;
            return combatant;
        }

        combatant = ReactivateRetiredCombatant(name, owner) ?? new CombatantAggregate(name) { OwnerName = owner };
        _combatants[name] = combatant;
        return combatant;
    }

    private CombatantAggregate? ReactivateRetiredCombatant(string name, string? owner)
    {
        var combatant = _retiredCombatants.LastOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.OwnerName, owner, StringComparison.OrdinalIgnoreCase));
        if (combatant is null) return null;
        _retiredCombatants.Remove(combatant);
        _combatants[name] = combatant;
        return combatant;
    }

    private void RetireCombatant(string name)
    {
        if (!_combatants.Remove(name, out var combatant)) return;
        _retiredCombatants.Add(combatant);
    }

    private void PruneRollingEvents(DateTime now)
    {
        var cutoff = now - RollingRetention;
        while (_rollingEvents.TryPeek(out var oldest) && oldest.Timestamp <= cutoff)
        {
            _rollingEvents.Dequeue();
        }
    }

    private void FinalizeEncounter(DateTime timestamp)
    {
        if (!StartedAt.HasValue) return;
        IsFinalized = true;
        EndedAt = timestamp < StartedAt.Value ? LastDamageAt : timestamp;
    }

    private static CombatantAggregate CloneCombatant(CombatantAggregate source)
    {
        var clone = new CombatantAggregate(source.Name)
        {
            OwnerName = source.OwnerName,
            Damage = source.Damage,
            Hits = source.Hits,
            MeleeHits = source.MeleeHits,
            SpellHits = source.SpellHits,
            MeleeCriticalHits = source.MeleeCriticalHits,
            SpellCriticalHits = source.SpellCriticalHits,
            Misses = source.Misses,
            SpellFizzles = source.SpellFizzles,
            SpellResists = source.SpellResists,
            DamageTaken = source.DamageTaken,
            IncomingHits = source.IncomingHits,
            IncomingMeleeHits = source.IncomingMeleeHits,
            IncomingMisses = source.IncomingMisses,
            Dodges = source.Dodges,
            Parries = source.Parries,
            Blocks = source.Blocks,
            Ripostes = source.Ripostes,
            Absorbed = source.Absorbed,
            SpellAbsorbs = source.SpellAbsorbs,
            IncomingSpellResists = source.IncomingSpellResists,
            Healing = source.Healing,
            PotentialHealing = source.PotentialHealing,
            DirectHeals = source.DirectHeals,
            HealOverTimeTicks = source.HealOverTimeTicks,
            CriticalHeals = source.CriticalHeals
        };
        foreach (var ability in source.Abilities.Values) clone.Abilities[ability.Name] = CloneAbility(ability);
        foreach (var ability in source.IncomingAbilities.Values) clone.IncomingAbilities[ability.Name] = CloneAbility(ability);
        foreach (var ability in source.HealingAbilities.Values) clone.HealingAbilities[ability.Name] = CloneAbility(ability);
        foreach (var target in source.Targets.Values)
        {
            var targetClone = new TargetAggregate(target.Name) { Damage = target.Damage };
            foreach (var ability in target.Abilities.Values) targetClone.Abilities[ability.Name] = CloneAbility(ability);
            clone.Targets[target.Name] = targetClone;
        }
        return clone;
    }

    private static AbilityAggregate CloneAbility(AbilityAggregate source) => new(source.Name)
    {
        Damage = source.Damage
    };
}
