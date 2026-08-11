using System.Text.RegularExpressions;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class BuffTracker
{
    private const string SelfTargetKey = "\0SELF";
    private const string UnconfirmedTargetKey = "\0TARGET";
    private const string UnconfirmedTargetName = "Target";
    private static readonly TimeSpan ConfirmationGrace = TimeSpan.FromSeconds(10);
    /// <summary>
    /// EQ often logs "Your X spell has worn off of Y" in the same second as a remes/reroot
    /// land. That worn-off refers to the previous application and must not clear the new timer.
    /// </summary>
    private static readonly TimeSpan ControlOverwriteGrace = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Charm stays on the overlay past its configured duration until worn-off. After this
    /// overdue window we drop the instance so a missed worn-off cannot pin buffs forever.
    /// </summary>
    private static readonly TimeSpan CharmMaxOverdue = TimeSpan.FromMinutes(3);
    /// <summary>Overlay rows pulse when remaining time is at or below this.</summary>
    private static readonly TimeSpan ExpiringSoonWindow = TimeSpan.FromSeconds(10);
    private sealed class ActiveInstance
    {
        public required string TargetName { get; set; }
        public required bool IsSelf { get; init; }
        public required DateTime StartedAt { get; set; }
        public required DateTime ExpiresAt { get; set; }
        public bool Alerted { get; set; }
        /// <summary>
        /// False when the land message is shared by many spells (e.g. "yawns.") or the
        /// timer was started from cast time alone. Those instances must not disappear
        /// just because some nearby mob with that name died.
        /// </summary>
        public bool ClearsOnTargetDeath { get; set; }
    }

    private sealed class RuleRuntime
    {
        public DateTime? PendingCastStartedAt { get; set; }
        public DateTime? PendingStart { get; set; }
        public DateTime? PendingConfirmationEndsAt { get; set; }
        public bool PendingRequiresConfirmation { get; set; }
        public BuffStopReason StopReason { get; set; }
        public Dictionary<string, List<string>> PendingTargetKeys { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> PendingTargetOccurrences { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ActiveInstance> Instances { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Regex LocalCast = new(
        @"^You begin casting (?<spell>.+?)\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalFailure = new(
        @"^Your (?<spell>.+?) spell (?:is interrupted\.|fizzles!|did not take hold(?: on .+?)?\..*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalResist = new(
        @"^.+? resisted your (?<spell>.+?)!$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalWornOff = new(
        @"^Your (?<spell>.+?) spell has worn off of (?<target>.+?)\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalDeath = new(
        @"^(?:You died\.|You have been slain by .+?!)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalDispel = new(
        @"^You feel(?: a bit| very)? dispelled\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TargetDeath = new(
        @"^(?<target>.+?) (?:has been slain by .+?!|(?:has )?died\.)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalTargetDeath = new(
        @"^You have slain (?<target>.+?)!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ZoneLoading = new(
        @"^LOADING, PLEASE WAIT\.\.\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ZoneEntered = new(
        @"^You have entered .+\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Dictionary<Guid, BuffRuleSettings> _rules = [];
    private readonly Dictionary<Guid, RuleRuntime> _states = [];
    private readonly Dictionary<Guid, HashSet<string>> _fadeMessages = [];
    private readonly Dictionary<Guid, HashSet<string>> _selfAppliedMessages = [];
    private readonly Dictionary<Guid, string[]> _uniqueOtherSuffixes = [];
    private readonly Dictionary<Guid, string[]> _ambiguousOtherSuffixes = [];
    private readonly Queue<BuffExpirationAlert> _queuedAlerts = [];
    private DateTime? _lastSpecificFadeAt;

    public Func<string, DateTime, bool>? PreserveBuffTargetOnDeath { get; set; }

    public void Configure(IEnumerable<BuffRuleSettings> rules,
        Func<string, IReadOnlyList<string>>? fadeMessageResolver = null,
        Func<string, IReadOnlyList<string>>? selfAppliedMessageResolver = null,
        Func<string, IReadOnlyList<string>>? otherAppliedMessageResolver = null,
        Func<string, bool>? isAmbiguousOtherSuffix = null,
        bool pruneMissing = true)
    {
        var configured = rules.ToDictionary(rule => rule.Id);
        if (pruneMissing)
        {
            foreach (var removed in _rules.Keys.Except(configured.Keys).ToArray())
            {
                _rules.Remove(removed);
                _states.Remove(removed);
                _fadeMessages.Remove(removed);
                _selfAppliedMessages.Remove(removed);
                _uniqueOtherSuffixes.Remove(removed);
                _ambiguousOtherSuffixes.Remove(removed);
            }
        }

        foreach (var rule in configured.Values)
        {
            _rules[rule.Id] = rule;
            _fadeMessages[rule.Id] = new HashSet<string>(
                fadeMessageResolver?.Invoke(rule.SpellName) ?? [], StringComparer.OrdinalIgnoreCase);
            _selfAppliedMessages[rule.Id] = new HashSet<string>(
                selfAppliedMessageResolver?.Invoke(rule.SpellName) ?? [], StringComparer.OrdinalIgnoreCase);
            var otherSuffixes = (otherAppliedMessageResolver?.Invoke(rule.SpellName) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value.Length)
                .ToArray();
            _uniqueOtherSuffixes[rule.Id] = otherSuffixes
                .Where(suffix => isAmbiguousOtherSuffix?.Invoke(suffix) != true)
                .ToArray();
            _ambiguousOtherSuffixes[rule.Id] = otherSuffixes
                .Where(suffix => isAmbiguousOtherSuffix?.Invoke(suffix) == true)
                .ToArray();
            if (!_states.TryGetValue(rule.Id, out var state))
            {
                state = new RuleRuntime();
                _states[rule.Id] = state;
            }
            if (!rule.IsEnabled || !rule.TrackSelf)
                state.Instances.Remove(SelfTargetKey);
            if (!rule.IsEnabled || !rule.TrackOthers)
                foreach (var key in state.Instances.Where(pair => !pair.Value.IsSelf).Select(pair => pair.Key).ToArray())
                    state.Instances.Remove(key);
            if (!rule.IsEnabled) ClearPendingCast(state);
        }
    }

    public void ClearRuntime()
    {
        foreach (var state in _states.Values) ResetState(state);
        _queuedAlerts.Clear();
        _lastSpecificFadeAt = null;
    }

    public void Observe(DateTime timestamp, string message)
    {
        // Gate / zone / evac ends enemy control and DoTs immediately (charm included).
        if ((message.Contains("LOADING", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("entered", StringComparison.OrdinalIgnoreCase)) &&
            (ZoneLoading.IsMatch(message) || ZoneEntered.IsMatch(message)))
        {
            ClearEnemyEffectsOnZone();
            return;
        }

        if ((message.Contains("died", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("slain", StringComparison.OrdinalIgnoreCase)) &&
            LocalDeath.IsMatch(message))
        {
            StopAll(BuffStopReason.Death);
            return;
        }

        // Land/fade texts are spell-specific and often have no shared keywords, so these
        // confirmation paths stay ungated.
        if (ConfirmSelfApplication(timestamp, message)) return;
        if (ConfirmOtherApplication(timestamp, message)) return;
        if (ExpireByFadeMessage(timestamp, message)) return;

        if (message.Contains("worn off", StringComparison.OrdinalIgnoreCase))
        {
            var wornOff = LocalWornOff.Match(message);
            if (wornOff.Success)
            {
                ExpireOtherTarget(timestamp, wornOff.Groups["spell"].Value, wornOff.Groups["target"].Value);
                return;
            }
        }

        if (message.Contains("died", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("slain", StringComparison.OrdinalIgnoreCase))
        {
            var death = LocalTargetDeath.Match(message);
            if (!death.Success) death = TargetDeath.Match(message);
            if (death.Success)
            {
                RemoveTarget(death.Groups["target"].Value, timestamp);
                return;
            }
        }

        if (message.Contains("dispelled", StringComparison.OrdinalIgnoreCase) &&
            LocalDispel.IsMatch(message))
        {
            if (_lastSpecificFadeAt != timestamp)
            {
                var selfInstances = _rules.Values
                    .Where(rule => rule.IsEnabled && _states[rule.Id].Instances.ContainsKey(SelfTargetKey))
                    .ToArray();
                if (selfInstances.Length == 1) StopSelf(selfInstances[0].Id, BuffStopReason.Dispelled);
            }
            return;
        }

        if (message.Contains("begin casting", StringComparison.OrdinalIgnoreCase))
        {
            var cast = LocalCast.Match(message);
            if (cast.Success)
            {
                BeginMatchingRules(timestamp, cast.Groups["spell"].Value);
                return;
            }
        }

        if (message.Contains("spell", StringComparison.OrdinalIgnoreCase))
        {
            var failure = LocalFailure.Match(message);
            if (failure.Success)
            {
                CancelMatchingRules(failure.Groups["spell"].Value);
                return;
            }
        }

        if (message.Contains("resisted your", StringComparison.OrdinalIgnoreCase))
        {
            var resisted = LocalResist.Match(message);
            if (resisted.Success) CancelMatchingRules(resisted.Groups["spell"].Value);
        }
    }

    public IReadOnlyList<BuffExpirationAlert> Tick(DateTime now)
    {
        List<BuffExpirationAlert>? alerts = null;
        while (_queuedAlerts.TryDequeue(out var queued))
        {
            alerts ??= [];
            alerts.Add(queued);
        }
        foreach (var rule in _rules.Values.Where(rule => rule.IsEnabled))
        {
            var state = _states[rule.Id];
            if (state.PendingStart is { } pendingStart && now >= pendingStart)
            {
                if (!state.PendingRequiresConfirmation)
                {
                    if (rule.TrackSelf)
                        Activate(state, rule, SelfTargetKey, "Self", true, pendingStart,
                            clearsOnTargetDeath: true);
                    else if (rule.TrackOthers)
                        Activate(state, rule, UnconfirmedTargetKey, UnconfirmedTargetName, false,
                            pendingStart, clearsOnTargetDeath: false);
                    else ClearPendingCast(state);
                }
                else if (now > (state.PendingConfirmationEndsAt ?? pendingStart + ConfirmationGrace))
                    ClearPendingCast(state);
            }

            foreach (var pair in state.Instances.ToArray())
            {
                var instance = pair.Value;
                if (now < instance.ExpiresAt) continue;
                var isCharm = rule.Category == SpellTrackerCategory.Control &&
                              rule.ControlType == ControlEffectType.Charm;
                if (!instance.Alerted)
                {
                    instance.Alerted = true;
                    alerts ??= [];
                    alerts.Add(new BuffExpirationAlert(rule));
                }
                // Charm stays overdue until worn-off, but drop it after CharmMaxOverdue so a
                // missed worn-off cannot pin PreserveBuffTargetOnDeath forever.
                if (isCharm && now < instance.ExpiresAt + CharmMaxOverdue) continue;
                state.Instances.Remove(pair.Key);
                if (state.Instances.Count == 0 && state.StopReason == BuffStopReason.None)
                    state.StopReason = BuffStopReason.Expired;
            }
        }
        return alerts ?? [];
    }

    public BuffRuntimeSnapshot GetSnapshot(Guid ruleId, DateTime now)
    {
        if (!_states.TryGetValue(ruleId, out var state))
            return EmptySnapshot(ruleId);

        var rule = _rules.GetValueOrDefault(ruleId);
        var isCharm = rule?.Category == SpellTrackerCategory.Control &&
                      rule.ControlType == ControlEffectType.Charm;
        var active = state.Instances.Values
            .Where(instance => now < instance.ExpiresAt ||
                               isCharm && now < instance.ExpiresAt + CharmMaxOverdue)
            .OrderBy(instance => instance.ExpiresAt).ToArray();
        var isCasting = state.PendingStart is { } pending && now < pending + ConfirmationGrace;
        if (active.Length == 0)
            return new BuffRuntimeSnapshot(ruleId, null, null, TimeSpan.Zero, isCasting, false,
                state.StopReason == BuffStopReason.Expired, false, false, state.StopReason);

        var next = active[0];
        var isOverdue = isCharm && now >= next.ExpiresAt;
        var remaining = isOverdue ? TimeSpan.Zero : next.ExpiresAt - now;
        return new BuffRuntimeSnapshot(ruleId, next.StartedAt, next.ExpiresAt, remaining, isCasting, true, false,
            !isOverdue && remaining <= ExpiringSoonWindow, isOverdue, state.StopReason);
    }

    public IReadOnlyList<BuffInstanceSnapshot> GetActiveSnapshots(DateTime now) =>
        _rules.Values.Where(rule => rule.IsEnabled)
            .SelectMany(rule =>
            {
                var isCharm = rule.Category == SpellTrackerCategory.Control &&
                              rule.ControlType == ControlEffectType.Charm;
                return _states[rule.Id].Instances
                    .Where(pair => now < pair.Value.ExpiresAt ||
                                   isCharm && now < pair.Value.ExpiresAt + CharmMaxOverdue)
                    .Select(pair => new BuffInstanceSnapshot(rule.Id, pair.Key, rule.SpellName,
                        pair.Value.TargetName, pair.Value.IsSelf, pair.Value.StartedAt, pair.Value.ExpiresAt,
                        pair.Value.ExpiresAt > now ? pair.Value.ExpiresAt - now : now - pair.Value.ExpiresAt,
                        pair.Value.ExpiresAt > now && pair.Value.ExpiresAt - now <= ExpiringSoonWindow,
                        pair.Value.ExpiresAt <= now));
            })
            // Self first, then others grouped by target, soonest expiry within each group.
            .OrderByDescending(snapshot => snapshot.IsSelf)
            .ThenBy(snapshot => snapshot.IsSelf ? string.Empty : snapshot.TargetName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.ExpiresAt)
            .ThenBy(snapshot => snapshot.SpellName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool HasActiveCharmTarget(string target, DateTime now) =>
        _rules.Values.Any(rule => rule.IsEnabled && rule.Category == SpellTrackerCategory.Control &&
            rule.ControlType == ControlEffectType.Charm && _states[rule.Id].Instances.Values.Any(instance =>
                instance.TargetName.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase) &&
                now < instance.ExpiresAt + CharmMaxOverdue));

    private void BeginMatchingRules(DateTime timestamp, string spell)
    {
        foreach (var rule in MatchingEnabledRules(spell))
        {
            var state = _states[rule.Id];
            state.PendingCastStartedAt = timestamp;
            state.PendingStart = timestamp.AddSeconds(rule.CastTimeSeconds);
            state.PendingConfirmationEndsAt = null;
            state.PendingTargetKeys.Clear();
            state.PendingTargetOccurrences.Clear();
            // Buffs: only unique land text can prove a land (shared lines like "yawns."
            // only rename a cast-timed instance). DoT/Control: shared land text during
            // our cast window is enough to open a per-target timer (e.g. poison DoTs).
            state.PendingRequiresConfirmation =
                _selfAppliedMessages.GetValueOrDefault(rule.Id)?.Count > 0 ||
                _uniqueOtherSuffixes.GetValueOrDefault(rule.Id)?.Length > 0 ||
                (IsEnemyEffectCategory(rule) &&
                 _ambiguousOtherSuffixes.GetValueOrDefault(rule.Id)?.Length > 0);
            state.StopReason = BuffStopReason.None;
        }
    }

    private void CancelMatchingRules(string spell)
    {
        foreach (var rule in MatchingEnabledRules(spell)) ClearPendingCast(_states[rule.Id]);
    }

    private bool ConfirmSelfApplication(DateTime timestamp, string message)
    {
        var matches = PendingRules(timestamp).Where(rule =>
            _selfAppliedMessages.GetValueOrDefault(rule.Id)?.Contains(message) == true).ToArray();
        if (matches.Length == 0) return false;
        foreach (var rule in matches)
        {
            var state = _states[rule.Id];
            ClearPendingCast(state);
            if (rule.TrackSelf) Activate(state, rule, SelfTargetKey, "Self", true, timestamp);
        }
        return true;
    }

    private bool ConfirmOtherApplication(DateTime timestamp, string message)
    {
        var matched = false;
        BuffRuleSettings? bestAmbiguousEnemy = null;
        DateTime bestAmbiguousPending = DateTime.MinValue;
        string? bestAmbiguousTarget = null;

        foreach (var rule in _rules.Values.Where(rule => rule.IsEnabled && rule.TrackOthers).ToArray())
        {
            var state = _states[rule.Id];
            var isPending = state.PendingCastStartedAt is { } castStarted && timestamp >= castStarted &&
                            state.PendingStart is { } expected &&
                            timestamp <= (state.PendingConfirmationEndsAt ?? expected + ConfirmationGrace);

            var uniqueSuffix = _uniqueOtherSuffixes.GetValueOrDefault(rule.Id)?
                .FirstOrDefault(value => message.EndsWith(value, StringComparison.OrdinalIgnoreCase));
            if (uniqueSuffix is not null && isPending)
            {
                var target = message[..^uniqueSuffix.Length].Trim();
                if (target.Length == 0) continue;
                matched = true;
                if (rule.Category == SpellTrackerCategory.Control && rule.ControlType == ControlEffectType.Charm)
                {
                    foreach (var charmRule in _rules.Values.Where(item =>
                                 item.Category == SpellTrackerCategory.Control &&
                                 item.ControlType == ControlEffectType.Charm))
                        _states[charmRule.Id].Instances.Clear();
                }
                var targetKey = ResolveEnemyLandTargetKey(state, rule, target);
                state.Instances.Remove(UnconfirmedTargetKey);
                Activate(state, rule, targetKey, target, false, timestamp, clearPending: false,
                    clearsOnTargetDeath: true);
                NoteLandConfirmation(state, rule, timestamp);
                continue;
            }

            var ambiguousSuffix = _ambiguousOtherSuffixes.GetValueOrDefault(rule.Id)?
                .FirstOrDefault(value => message.EndsWith(value, StringComparison.OrdinalIgnoreCase));
            if (ambiguousSuffix is null) continue;
            var ambiguousTarget = message[..^ambiguousSuffix.Length].Trim();
            if (ambiguousTarget.Length == 0) continue;

            // Shared land text can match multiple pending DoT/Control rules. Confirm only
            // the cast that finished most recently so one poison line cannot arm every
            // overlapping poison DoT.
            if (isPending && IsEnemyEffectCategory(rule) &&
                state.PendingStart is { } pendingStart && pendingStart >= bestAmbiguousPending)
            {
                bestAmbiguousPending = pendingStart;
                bestAmbiguousEnemy = rule;
                bestAmbiguousTarget = ambiguousTarget;
                continue;
            }

            // Buffs: shared land text (e.g. "begins to regenerate.") proves our pending
            // cast landed and must refresh the timer. Without a pending cast it may only
            // rename the cast-timed placeholder — never invent a death-linked bystander.
            if (!IsEnemyEffectCategory(rule) &&
                (isPending || state.Instances.ContainsKey(UnconfirmedTargetKey)))
            {
                matched = true;
                if (isPending)
                {
                    var startedAt = state.PendingStart ?? timestamp;
                    // Prefer refreshing an existing named entry for this target (recast
                    // on a charmed pet). Fall back to the unconfirmed placeholder.
                    var key = FindTargetInstanceKey(state, ambiguousTarget) ?? UnconfirmedTargetKey;
                    if (key != UnconfirmedTargetKey)
                        state.Instances.Remove(UnconfirmedTargetKey);
                    // Our pending cast landed on a named target (often a charmed pet) —
                    // clear it when that target dies or charm breaks.
                    Activate(state, rule, key, ambiguousTarget, false, startedAt,
                        clearPending: false, clearsOnTargetDeath: true);
                    NoteLandConfirmation(state, rule, timestamp);
                }
                else if (state.Instances.ContainsKey(UnconfirmedTargetKey))
                    state.Instances[UnconfirmedTargetKey].TargetName = ambiguousTarget;
            }
        }

        if (bestAmbiguousEnemy is not null && bestAmbiguousTarget is not null)
        {
            var state = _states[bestAmbiguousEnemy.Id];
            var targetKey = ResolveEnemyLandTargetKey(state, bestAmbiguousEnemy, bestAmbiguousTarget);
            state.Instances.Remove(UnconfirmedTargetKey);
            Activate(state, bestAmbiguousEnemy, targetKey, bestAmbiguousTarget, false, timestamp,
                clearPending: false, clearsOnTargetDeath: true);
            NoteLandConfirmation(state, bestAmbiguousEnemy, timestamp);
            // Drop overlapping pending casts that shared this land text so they cannot
            // steal later AE lands or sit armed forever without a timer.
            ClearOtherPendingAmbiguousEnemyCasts(bestAmbiguousEnemy.Id, bestAmbiguousTarget, message);
            matched = true;
        }

        return matched;
    }

    private void ClearOtherPendingAmbiguousEnemyCasts(Guid confirmedRuleId, string target, string message)
    {
        foreach (var rule in _rules.Values)
        {
            if (rule.Id == confirmedRuleId || !rule.IsEnabled || !IsEnemyEffectCategory(rule)) continue;
            var suffix = _ambiguousOtherSuffixes.GetValueOrDefault(rule.Id)?
                .FirstOrDefault(value => message.EndsWith(value, StringComparison.OrdinalIgnoreCase));
            if (suffix is null) continue;
            var landTarget = message[..^suffix.Length].Trim();
            if (!landTarget.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            ClearPendingCast(_states[rule.Id]);
        }
    }

    private bool ExpireByFadeMessage(DateTime timestamp, string message)
    {
        var matches = _rules.Values.Where(rule => rule.IsEnabled &&
            _fadeMessages.GetValueOrDefault(rule.Id)?.Contains(message) == true).ToArray();
        if (matches.Length == 0) return false;

        // Shared fade text (e.g. poison) only when exactly one matching rule has a single
        // live instance — otherwise we cannot attribute the sample or clear safely.
        var attributable = matches
            .Where(rule => _states[rule.Id].Instances.Count == 1)
            .ToArray();
        if (attributable.Length != 1) return false;

        var rule = attributable[0];
        var state = _states[rule.Id];
        var instance = state.Instances.Values.First();
        if (instance.IsSelf)
            StopSelf(rule.Id, BuffStopReason.Expired, preserveNewerPending: true);
        else
        {
            state.Instances.Clear();
            // Do not ClearPendingCast — a refresh cast may already be pending.
            state.StopReason = BuffStopReason.Expired;
            if (rule.Category == SpellTrackerCategory.Control)
                _queuedAlerts.Enqueue(new BuffExpirationAlert(rule));
        }

        _lastSpecificFadeAt = timestamp;
        return true;
    }

    private void ExpireOtherTarget(DateTime timestamp, string spell, string target)
    {
        foreach (var rule in MatchingEnabledRules(spell))
        {
            var state = _states[rule.Id];
            // Do not ClearPendingCast here — worn-off on target A must not cancel a
            // recast already pending for target B.
            var key = FindTargetInstanceKey(state, target);
            if (key is null) continue;
            var removed = state.Instances[key];
            state.Instances.Remove(key);

            // Remes/reroot overwrite: a newer land for the same name just stacked a
            // replacement timer. Drop the previous application without alerting.
            // Same-second AE lands share StartedAt, so a real early break still alerts
            // (sibling StartedAt is not strictly greater).
            var isOverwrite = IsMezOrRoot(rule) &&
                state.Instances.Values.Any(item =>
                    item.TargetName.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    item.StartedAt > removed.StartedAt &&
                    timestamp - item.StartedAt <= ControlOverwriteGrace);

            if (!isOverwrite && rule.Category == SpellTrackerCategory.Control)
                _queuedAlerts.Enqueue(new BuffExpirationAlert(rule));
            if (state.Instances.Count == 0 && state.StopReason == BuffStopReason.None)
                state.StopReason = BuffStopReason.Expired;
        }
    }

    private static void Activate(RuleRuntime state, BuffRuleSettings rule, string targetKey,
        string targetName, bool isSelf, DateTime startedAt, bool clearPending = true,
        bool clearsOnTargetDeath = true)
    {
        if (clearPending) ClearPendingCast(state);
        state.Instances[targetKey] = new ActiveInstance
        {
            TargetName = targetName,
            IsSelf = isSelf,
            StartedAt = startedAt,
            ExpiresAt = startedAt.AddSeconds(rule.DurationSeconds),
            ClearsOnTargetDeath = clearsOnTargetDeath
        };
        state.StopReason = BuffStopReason.None;
    }

    private IEnumerable<BuffRuleSettings> PendingRules(DateTime timestamp) =>
        _rules.Values.Where(rule => rule.IsEnabled &&
            _states[rule.Id].PendingCastStartedAt is { } castStarted && timestamp >= castStarted &&
            _states[rule.Id].PendingStart is { } expected &&
            timestamp <= (_states[rule.Id].PendingConfirmationEndsAt ?? expected + ConfirmationGrace));

    private static void ClearPendingCast(RuleRuntime state)
    {
        state.PendingCastStartedAt = null;
        state.PendingStart = null;
        state.PendingConfirmationEndsAt = null;
        state.PendingTargetKeys.Clear();
        state.PendingTargetOccurrences.Clear();
        state.PendingRequiresConfirmation = false;
    }

    private void StopSelf(Guid ruleId, BuffStopReason reason, bool preserveNewerPending = false)
    {
        var state = _states[ruleId];
        if (preserveNewerPending &&
            state.Instances.TryGetValue(SelfTargetKey, out var self) &&
            state.PendingCastStartedAt is { } pending &&
            pending > self.StartedAt)
        {
            // Keep the in-flight refresh cast.
        }
        else ClearPendingCast(state);
        state.Instances.Remove(SelfTargetKey);
        state.StopReason = reason;
    }

    private void StopAll(BuffStopReason reason)
    {
        foreach (var state in _states.Values)
        {
            ResetState(state);
            state.StopReason = reason;
        }
    }

    private void RemoveTarget(string target, DateTime timestamp)
    {
        var normalized = target.Trim();
        foreach (var rule in _rules.Values)
        {
            // A death line contains only the NPC's visible name. When a charmed pet
            // and an enemy share that name, removing charm here produces a false
            // break. Charm is instead ended by its explicit worn-off line, a new
            // charm landing, player death, or the configured maximum duration.
            if (rule.Category == SpellTrackerCategory.Control && rule.ControlType == ControlEffectType.Charm)
                continue;
            if (rule.Category == SpellTrackerCategory.Buff &&
                PreserveBuffTargetOnDeath?.Invoke(normalized, timestamp) == true)
                continue;
            var state = _states[rule.Id];
            var matching = state.Instances
                .Where(item => item.Value.ClearsOnTargetDeath &&
                               item.Value.TargetName.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Value.ExpiresAt)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matching.Length == 0) continue;

            // One death line is one NPC. For DoT/mez/root AE packs of identical names,
            // clear a single stack — not every timer sharing that name.
            if (IsEnemyEffectCategory(rule))
                state.Instances.Remove(matching[0].Key);
            else
                foreach (var pair in matching)
                    state.Instances.Remove(pair.Key);
        }
    }

    private static bool IsMezOrRoot(BuffRuleSettings rule) =>
        rule.Category == SpellTrackerCategory.Control &&
        rule.ControlType is ControlEffectType.Mez or ControlEffectType.Root;

    /// <summary>
    /// Mez/Root always open a new application (remes stacks; overwrite worn-off pops the
    /// old one). DoT/Charm keep pending-key reuse so a refresh can retarget existing slots.
    /// </summary>
    private static string ResolveEnemyLandTargetKey(RuleRuntime state, BuffRuleSettings rule, string target) =>
        IsMezOrRoot(rule) ? ResolveNewControlTargetKey(state, target) : ResolvePendingTargetKey(state, target);

    /// <summary>
    /// Always allocate a fresh instance key. Do not reuse existing same-name keys — that
    /// would refresh the wrong slot when only some identically named mobs are remesed.
    /// </summary>
    private static string ResolveNewControlTargetKey(RuleRuntime state, string target)
    {
        var normalized = target.Trim();
        if (!state.PendingTargetKeys.TryGetValue(normalized, out var keys))
        {
            keys = [];
            state.PendingTargetKeys[normalized] = keys;
        }

        var newKey = $"{normalized}\0{Guid.NewGuid():N}";
        keys.Add(newKey);
        return newKey;
    }

    private static string ResolvePendingTargetKey(RuleRuntime state, string target)
    {
        if (!state.PendingTargetKeys.TryGetValue(target, out var keys))
        {
            keys = state.Instances
                .Where(pair => pair.Value.TargetName.Equals(target, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Value.StartedAt)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key)
                .ToList();
            state.PendingTargetKeys[target] = keys;
        }

        var occurrence = state.PendingTargetOccurrences.GetValueOrDefault(target);
        state.PendingTargetOccurrences[target] = occurrence + 1;
        if (occurrence < keys.Count) return keys[occurrence];

        var newKey = $"{target}\0{Guid.NewGuid():N}";
        keys.Add(newKey);
        return newKey;
    }

    private static string? FindTargetInstanceKey(RuleRuntime state, string target) =>
        state.Instances
            .Where(pair => pair.Value.TargetName.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Value.ExpiresAt)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .FirstOrDefault();

    private IEnumerable<BuffRuleSettings> MatchingEnabledRules(string spell) =>
        _rules.Values.Where(rule => rule.IsEnabled &&
            SpellNameNormalizer.BelongsToFamily(spell, rule.SpellName));

    private static bool IsEnemyEffectCategory(BuffRuleSettings rule) =>
        rule.Category is SpellTrackerCategory.DamageOverTime or SpellTrackerCategory.Control;

    /// <summary>
    /// Buffs clamp the pending window to 1s after the first land (bystander spam).
    /// DoT / Control / AE mez must keep the full cast grace so every land in the
    /// burst can open its own target instance.
    /// </summary>
    private static void NoteLandConfirmation(RuleRuntime state, BuffRuleSettings rule, DateTime timestamp)
    {
        if (IsEnemyEffectCategory(rule)) return;
        state.PendingConfirmationEndsAt ??= timestamp.AddSeconds(1);
    }

    private void ClearEnemyEffectsOnZone()
    {
        foreach (var rule in _rules.Values)
        {
            if (!IsEnemyEffectCategory(rule)) continue;
            var state = _states[rule.Id];
            var hadInstances = state.Instances.Count > 0;
            ClearPendingCast(state);
            state.Instances.Clear();
            if (hadInstances) state.StopReason = BuffStopReason.Zone;
        }
    }

    private static void ResetState(RuleRuntime state)
    {
        ClearPendingCast(state);
        state.Instances.Clear();
        state.StopReason = BuffStopReason.None;
    }

    private static BuffRuntimeSnapshot EmptySnapshot(Guid ruleId) =>
        new(ruleId, null, null, TimeSpan.Zero, false, false, false, false, false, BuffStopReason.None);
}
