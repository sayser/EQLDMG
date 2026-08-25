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
    private static readonly DateTime IndefiniteSongExpiresAt = DateTime.MaxValue;
    /// <summary>Fallback pulse silence when a pulse-tracked song has no configured duration.</summary>
    private static readonly TimeSpan DefaultSongLandPulseSilence = TimeSpan.FromSeconds(12);
    /// <summary>
    /// Incoming from this name shortly before charm land means another living NPC (or the
    /// same one) was already hitting you. Logs cannot tell two "an ire ghast" apart.
    /// </summary>
    private static readonly TimeSpan CharmIncomingLookback = TimeSpan.FromSeconds(4);
    /// <summary>
    /// After land, a hit on you this soon is the other same-named NPC continuing, not a break.
    /// </summary>
    private static readonly TimeSpan CharmLandGrace = TimeSpan.FromSeconds(3);
    /// <summary>Quiet stretch with no hits from that name — incoming later is a real break.</summary>
    private static readonly TimeSpan CharmIncomingGap = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Hits on you from the charmed name are a break only after the pet has stopped
    /// attacking anyone else. Same-named hostiles are common; logs cannot tell them apart.
    /// </summary>
    private static readonly TimeSpan CharmPetAllySilence = TimeSpan.FromSeconds(5);

    private static TimeSpan SongLandPulseSilence(BuffRuleSettings rule) =>
        rule.DurationSeconds > 0
            ? TimeSpan.FromSeconds(rule.DurationSeconds)
            : DefaultSongLandPulseSilence;
    private sealed class ActiveInstance
    {
        public required string TargetName { get; set; }
        public required bool IsSelf { get; init; }
        public required DateTime StartedAt { get; set; }
        public required DateTime ExpiresAt { get; set; }
        public DateTime LastLandPulseAt { get; set; }
        public bool Alerted { get; set; }
        /// <summary>
        /// False when the land message is shared by many spells (e.g. "yawns.") or the
        /// timer was started from cast time alone. Those instances must not disappear
        /// just because some nearby mob with that name died.
        /// </summary>
        public bool ClearsOnTargetDeath { get; set; }
        /// <summary>True once this name has stopped hitting you since the charm landed.</summary>
        public bool SawIncomingGap { get; set; } = true;
        public DateTime? LastIncomingAt { get; set; }
        public DateTime? LastPetAllyAt { get; set; }
    }

    private sealed class RuleRuntime
    {
        public DateTime? PendingCastStartedAt { get; set; }
        public DateTime? PendingStart { get; set; }
        public DateTime? PendingConfirmationEndsAt { get; set; }
        public bool PendingRequiresConfirmation { get; set; }
        /// <summary>
        /// True after this mez/root cast wave removed timers from a prior cast. Used so
        /// EQ's overwrite worn-off lines do not clear the fresh land timers.
        /// </summary>
        public bool ReplacedPreviousWaveThisCast { get; set; }
        public BuffStopReason StopReason { get; set; }
        public Dictionary<string, List<string>> PendingTargetKeys { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ActiveInstance> Instances { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Regex LocalCast = new(
        @"^You begin casting (?<spell>.+?)\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalSongBegin = new(
        @"^You begin singing (?<spell>.+?)\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalFailure = new(
        @"^Your (?<spell>.+?) spell (?:is interrupted\.|fizzles!|did not take hold(?: on .+?)?\..*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalResist = new(
        @"^.+? resisted your (?<spell>.+?)!$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalWornOff = new(
        @"^Your (?<spell>.+?) spell has worn off of (?<target>.+?)\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalWornOffSelf = new(
        @"^Your (?<spell>.+?) spell has worn off\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalSongEnds = new(
        @"^Your song ends(?: abruptly)?\.$",
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
    /// <summary>
    /// Direct spell land attribution, e.g. "You hit X for 55 points of poison damage by Envenomed Bolt."
    /// or "Bzzazzt hit X for 100 points of poison damage by Deadly Poison." Shared land text
    /// ("X has been poisoned.") has no caster — this line is how we tell yours from a pet/group/NPC.
    /// </summary>
    private static readonly Regex NamedSpellHit = new(
        @"^(?<source>.+?) hit (?<target>.+?) for (?<amount>\d+) points? of \S+ damage by (?<ability>.+?)\.(?: \([^)]+\))*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalDotTick = new(
        @"^(?<target>.+?) has taken (?<amount>\d+) damage from your (?<ability>.+?)\.(?: \([^)]+\))*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly TimeSpan SpellHitAttributionWindow = TimeSpan.FromSeconds(1.5);
    /// <summary>
    /// After "Your song ends." the next self land line is treated as the local bard's twist.
    /// </summary>
    private static readonly TimeSpan OwnSongLandWindow = TimeSpan.FromSeconds(6);

    private readonly Dictionary<Guid, BuffRuleSettings> _rules = [];
    private readonly Dictionary<Guid, RuleRuntime> _states = [];
    private readonly Dictionary<Guid, HashSet<string>> _fadeMessages = [];
    private readonly Dictionary<Guid, HashSet<string>> _selfAppliedMessages = [];
    private readonly Dictionary<Guid, string[]> _uniqueOtherSuffixes = [];
    private readonly Dictionary<Guid, string[]> _ambiguousOtherSuffixes = [];
    private readonly Queue<BuffExpirationAlert> _queuedAlerts = [];
    private DateTime? _lastGenericDispelAt;
    private DateTime? _ownSongLandWindowUntil;
    private RecentSpellHit? _lastSpellHit;
    private Func<string, bool>? _isAmbiguousSelfAppliedMessage;
    private readonly HashSet<Guid> _pulseSongRuleIds = [];
    private readonly HashSet<Guid> _songDamageRuleIds = [];
    private bool _hasEnabledRules;
    private bool _hasEnabledCharmRule;
    private readonly Dictionary<string, DateTime> _recentIncomingOnYou = new(StringComparer.OrdinalIgnoreCase);
    private List<BuffRuleSettings> _enabledRules = [];
    private HashSet<string> _configuredSelfMessages = new(StringComparer.OrdinalIgnoreCase);
    private string[] _configuredFadeFragments = [];
    private string[] _configuredOtherSuffixes = [];
    private Dictionary<string, List<Guid>> _rulesByFamily = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct RecentSpellHit(
        string Target, string Source, string Ability, DateTime Timestamp);

    public Func<string, DateTime, bool>? PreserveBuffTargetOnDeath { get; set; }

    public void Configure(IEnumerable<BuffRuleSettings> rules,
        Func<string, IReadOnlyList<string>>? fadeMessageResolver = null,
        Func<string, IReadOnlyList<string>>? selfAppliedMessageResolver = null,
        Func<string, IReadOnlyList<string>>? otherAppliedMessageResolver = null,
        Func<string, bool>? isAmbiguousOtherSuffix = null,
        Func<string, bool>? isAmbiguousSelfAppliedMessage = null,
        Func<string, bool>? isPulseTrackedSong = null,
        Func<string, bool>? isSongDamageSong = null,
        bool pruneMissing = true)
    {
        _isAmbiguousSelfAppliedMessage = isAmbiguousSelfAppliedMessage;
        _pulseSongRuleIds.Clear();
        _songDamageRuleIds.Clear();
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
            if (IsIndefiniteSong(rule) && isPulseTrackedSong?.Invoke(rule.SpellName) == true)
                _pulseSongRuleIds.Add(rule.Id);
            if (isSongDamageSong?.Invoke(rule.SpellName) == true)
                _songDamageRuleIds.Add(rule.Id);
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

        RebuildMessageRelevanceHints();
        RebuildRuleFamilyIndex();
    }

    public bool HasEnabledRules => _hasEnabledRules;

    /// <summary>
    /// Fast prefilter before regex/rule scans. Returns false for high-volume combat spam
    /// that cannot affect spell tracking for the currently configured rules.
    /// </summary>
    public bool ShouldProcessMessage(string message)
    {
        if (!_hasEnabledRules || message.Length < 4) return false;
        if (ContainsRelevanceKeyword(message)) return true;
        if (_configuredSelfMessages.Contains(message)) return true;
        foreach (var fade in _configuredFadeFragments)
        {
            if (message.Contains(fade, StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (var suffix in _configuredOtherSuffixes)
        {
            if (message.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (_hasEnabledCharmRule && LooksLikeIncomingSwingOnYou(message)) return true;
        return HasLiveCharmTarget() && StartsWithLiveCharmTarget(message);
    }

    public void ClearRuntime()
    {
        foreach (var state in _states.Values) ResetState(state);
        _queuedAlerts.Clear();
        _lastGenericDispelAt = null;
        _ownSongLandWindowUntil = null;
        _lastSpellHit = null;
        _recentIncomingOnYou.Clear();
    }

    public void Observe(DateTime timestamp, string message)
    {
        if (!ShouldProcessMessage(message)) return;

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

        // Remember caster+ability hits before land text so shared lines like
        // "has been poisoned." can be attributed (or rejected as foreign).
        NoteSpellHit(timestamp, message);
        NoteOwnSongEnds(timestamp, message);

        if (ConfirmSongDamageFromOwnedHit(timestamp, message)) return;

        // Land/fade texts are spell-specific and often have no shared keywords, so these
        // confirmation paths stay ungated.
        if (ConfirmSelfApplication(timestamp, message)) return;
        if (TryConfirmTimedDamageSongEnemyLand(timestamp, message)) return;
        if (ConfirmOtherApplication(timestamp, message)) return;
        if (ConfirmOwnedDotTick(timestamp, message)) return;
        if (ExpireByFadeMessage(timestamp, message)) return;

        if (message.Contains("worn off", StringComparison.OrdinalIgnoreCase))
        {
            var wornOffSelf = LocalWornOffSelf.Match(message);
            if (wornOffSelf.Success)
            {
                ExpireSelfBySpellName(wornOffSelf.Groups["spell"].Value);
                return;
            }

            var wornOff = LocalWornOff.Match(message);
            if (wornOff.Success)
            {
                ExpireOtherTarget(timestamp, wornOff.Groups["spell"].Value, wornOff.Groups["target"].Value);
                return;
            }
        }

        if (LooksLikeIncomingSwingOnYou(message))
            NoteRecentIncomingOnYou(timestamp, message);
        RefreshCharmIncomingGaps(timestamp);
        NoteCharmPetAlly(timestamp, message);
        if (TryBreakCharmOnHostileSwing(timestamp, message)) return;

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
            // Generic "You feel a bit dispelled" never names the spell. Cancel Magic
            // also does not print the stripped buff's fade line. Dropping any overlay
            // timer here would guess — often the wrong one. Remember the stamp so a
            // real fade/worn-off in the next second can be labeled Dispelled; otherwise
            // tracked buffs stay until their own stop wording, duration, or death.
            _lastGenericDispelAt = timestamp;
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

        if (message.Contains("begin singing", StringComparison.OrdinalIgnoreCase))
        {
            var song = LocalSongBegin.Match(message);
            if (song.Success)
            {
                BeginMatchingRules(timestamp, song.Groups["spell"].Value);
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
        foreach (var rule in _enabledRules)
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
                if (IsIndefiniteSong(rule))
                {
                    if (_pulseSongRuleIds.Contains(rule.Id) &&
                        now > instance.LastLandPulseAt + SongLandPulseSilence(rule))
                    {
                        StopSelf(rule.Id, BuffStopReason.Expired, preserveNewerPending: true);
                    }
                    continue;
                }
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

        while (_queuedAlerts.TryDequeue(out var queued))
        {
            alerts ??= [];
            alerts.Add(queued);
        }

        return alerts ?? [];
    }

    public BuffRuntimeSnapshot GetSnapshot(Guid ruleId, DateTime now)
    {
        if (!_states.TryGetValue(ruleId, out var state))
            return EmptySnapshot(ruleId);

        var rule = _rules.GetValueOrDefault(ruleId);
        var isCharm = rule?.Category == SpellTrackerCategory.Control &&
                      rule?.ControlType == ControlEffectType.Charm;
        var isSong = rule is not null && IsIndefiniteSong(rule);
        var active = state.Instances.Values
            .Where(instance => isSong ||
                               now < instance.ExpiresAt ||
                               isCharm == true && now < instance.ExpiresAt + CharmMaxOverdue)
            .OrderBy(instance => isSong ? instance.StartedAt : instance.ExpiresAt).ToArray();
        var isCasting = state.PendingStart is { } pending && now < pending + ConfirmationGrace;
        if (active.Length == 0)
            return new BuffRuntimeSnapshot(ruleId, null, null, TimeSpan.Zero, isCasting, false,
                state.StopReason == BuffStopReason.Expired, false, false, state.StopReason);

        var next = active[0];
        var isOverdue = !isSong && isCharm == true && now >= next.ExpiresAt;
        var remaining = isSong ? TimeSpan.Zero : isOverdue ? TimeSpan.Zero : next.ExpiresAt - now;
        return new BuffRuntimeSnapshot(ruleId, next.StartedAt, isSong ? next.StartedAt : next.ExpiresAt,
            remaining, isCasting, true, false,
            false, isOverdue, state.StopReason);
    }

    public IReadOnlyList<BuffInstanceSnapshot> GetActiveSnapshots(DateTime now)
    {
        if (_enabledRules.Count == 0) return [];
        var snapshots = new List<BuffInstanceSnapshot>();
        foreach (var rule in _enabledRules)
        {
            var isCharm = rule.Category == SpellTrackerCategory.Control &&
                          rule.ControlType == ControlEffectType.Charm;
            var isIndefiniteSong = IsIndefiniteSong(rule);
            var showsPlaying = isIndefiniteSong || IsTimedDamageSong(rule);
            foreach (var pair in _states[rule.Id].Instances)
            {
                if (!isIndefiniteSong &&
                    now >= pair.Value.ExpiresAt &&
                    !(isCharm && now < pair.Value.ExpiresAt + CharmMaxOverdue))
                    continue;
                snapshots.Add(new BuffInstanceSnapshot(rule.Id, pair.Key, rule.SpellName,
                    pair.Value.TargetName, pair.Value.IsSelf, pair.Value.StartedAt,
                    showsPlaying ? pair.Value.StartedAt : pair.Value.ExpiresAt,
                    showsPlaying ? TimeSpan.Zero : pair.Value.ExpiresAt > now
                        ? pair.Value.ExpiresAt - now
                        : now - pair.Value.ExpiresAt,
                    !showsPlaying && pair.Value.ExpiresAt > now &&
                    pair.Value.ExpiresAt - now <= ExpiringSoonWindow,
                    !showsPlaying && pair.Value.ExpiresAt <= now,
                    showsPlaying));
            }
        }

        snapshots.Sort(static (left, right) =>
        {
            var selfCompare = right.IsSelf.CompareTo(left.IsSelf);
            if (selfCompare != 0) return selfCompare;
            var targetCompare = string.Compare(
                left.IsSelf ? string.Empty : left.TargetName,
                right.IsSelf ? string.Empty : right.TargetName,
                StringComparison.OrdinalIgnoreCase);
            if (targetCompare != 0) return targetCompare;
            var expiryCompare = left.ExpiresAt.CompareTo(right.ExpiresAt);
            if (expiryCompare != 0) return expiryCompare;
            return string.Compare(left.SpellName, right.SpellName, StringComparison.OrdinalIgnoreCase);
        });
        return snapshots;
    }

    public bool HasActiveCharmTarget(string target, DateTime now) =>
        _rules.Values.Any(rule => rule.IsEnabled && IsAnyCharmRule(rule) &&
            _states[rule.Id].Instances.Values.Any(instance =>
                NamesMatchCharmTarget(instance.TargetName, target) &&
                (IsCharmControl(rule)
                    ? now < instance.ExpiresAt + CharmMaxOverdue
                    : now < instance.ExpiresAt)));

    private bool HasLiveCharmTarget() =>
        _rules.Values.Any(rule => rule.IsEnabled && IsAnyCharmRule(rule) &&
            _states[rule.Id].Instances.Values.Any(instance => !instance.IsSelf));

    private void BeginMatchingRules(DateTime timestamp, string spell)
    {
        var matched = MatchingEnabledRules(spell).ToArray();
        var startingDamageSong = matched.FirstOrDefault(IsTimedDamageSong);
        if (startingDamageSong is not null)
            ClearOtherTimedDamageSongInstances(startingDamageSong.Id, silent: true);

        foreach (var rule in matched)
        {
            // Hostile timers start from enemy land text on you — never from your cast bar.
            if (rule.Category == SpellTrackerCategory.Hostile) continue;
            // Buff songs still arm a pending window from "You begin singing" so shared
            // land text (clicky copies) can be attributed. Overlay starts on land, not here.

            var state = _states[rule.Id];
            state.PendingCastStartedAt = timestamp;
            state.PendingStart = timestamp.AddSeconds(rule.CastTimeSeconds);
            state.PendingConfirmationEndsAt = null;
            state.PendingTargetKeys.Clear();
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
        foreach (var rule in MatchingEnabledRules(spell))
        {
            if (rule.Category == SpellTrackerCategory.Hostile) continue;
            ClearPendingCast(_states[rule.Id]);
        }
    }

    private bool ConfirmSelfApplication(DateTime timestamp, string message)
    {
        var songMatches = _rules.Values.Where(rule =>
            rule.IsEnabled &&
            rule.TrackingMode == BuffTrackingMode.Song &&
            rule.Category == SpellTrackerCategory.Buff &&
            rule.TrackSelf &&
            _selfAppliedMessages.GetValueOrDefault(rule.Id)?.Contains(message) == true &&
            AcceptsOwnSongLand(rule, timestamp, message)).ToArray();
        if (songMatches.Length > 1)
            songMatches = ResolveAmbiguousSongLand(songMatches);
        if (songMatches.Length == 1)
        {
            var songRule = songMatches[0];
            var songState = _states[songRule.Id];
            ClearPendingCast(songState);
            ActivateAndAlert(songState, songRule, SelfTargetKey, "Self", true, timestamp,
                clearsOnTargetDeath: true);
            // Keep the twist window open through the next song change.
            _ownSongLandWindowUntil = timestamp + OwnSongLandWindow;
            return true;
        }
        if (songMatches.Length > 1) return false;

        var damageSongSelf = _rules.Values.Where(rule =>
            rule.IsEnabled &&
            IsTimedDamageSong(rule) &&
            rule.TrackSelf &&
            _selfAppliedMessages.GetValueOrDefault(rule.Id)?.Contains(message) == true &&
            AcceptsOwnDamageSongLand(rule, timestamp)).ToArray();
        if (damageSongSelf.Length > 1)
            damageSongSelf = ResolveAmbiguousSongLand(damageSongSelf);
        if (damageSongSelf.Length == 1)
        {
            ActivateSongDamage(_states[damageSongSelf[0].Id], damageSongSelf[0], timestamp);
            return true;
        }

        var matches = PendingRules(timestamp).Where(rule =>
            rule.TrackingMode != BuffTrackingMode.Song &&
            _selfAppliedMessages.GetValueOrDefault(rule.Id)?.Contains(message) == true).ToArray();
        if (matches.Length == 0)
        {
            // Hostile: enemy land text on you needs no "You begin casting" pending window.
            matches = _rules.Values.Where(rule =>
                rule.IsEnabled &&
                rule.Category == SpellTrackerCategory.Hostile &&
                rule.TrackSelf &&
                _selfAppliedMessages.GetValueOrDefault(rule.Id)?.Contains(message) == true).ToArray();
        }
        if (matches.Length == 0) return false;
        foreach (var rule in matches)
        {
            var state = _states[rule.Id];
            ClearPendingCast(state);
            if (rule.TrackSelf)
                ActivateAndAlert(state, rule, SelfTargetKey, "Self", true, timestamp,
                    clearsOnTargetDeath: rule.Category != SpellTrackerCategory.Hostile);
        }
        return true;
    }

    private bool ConfirmOtherApplication(DateTime timestamp, string message)
    {
        var matched = false;
        BuffRuleSettings? bestAmbiguousEnemy = null;
        DateTime bestAmbiguousPending = DateTime.MinValue;
        string? bestAmbiguousTarget = null;
        var bestAmbiguousFromHit = false;

        foreach (var rule in _rules.Values.Where(rule =>
                     rule.IsEnabled && rule.TrackOthers && !IsTimedDamageSong(rule)).ToArray())
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
                if (!AcceptsLandAttribution(rule, target, timestamp, requireAbilityMatch: false))
                    continue;
                matched = true;
                if (IsCharmControl(rule))
                    ClearAllCharmInstances();
                PrepareControlWaveLand(state, rule);
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

            // Shared land text (poison/disease/etc.) can come from your DoT, a pet, a
            // group member, or an NPC. Prefer a matching "You hit … by {spell}" line;
            // reject lands attributed to anyone else.
            if (isPending && IsEnemyEffectCategory(rule) &&
                state.PendingStart is { } pendingStart)
            {
                var attribution = ClassifyLandAttribution(rule, ambiguousTarget, timestamp);
                if (attribution == LandAttribution.Foreign) continue;
                var fromHit = attribution == LandAttribution.LocalMatchingAbility;
                // A local ability-matched hit always beats a no-hit pending guess, and
                // among equals the newest cast wins so overlapping poison DoTs stay singular.
                var better =
                    fromHit && !bestAmbiguousFromHit ||
                    fromHit == bestAmbiguousFromHit && pendingStart >= bestAmbiguousPending;
                if (better && (fromHit || attribution == LandAttribution.Unattributed))
                {
                    bestAmbiguousPending = pendingStart;
                    bestAmbiguousEnemy = rule;
                    bestAmbiguousTarget = ambiguousTarget;
                    bestAmbiguousFromHit = fromHit;
                }
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
            if (IsCharmControl(bestAmbiguousEnemy))
                ClearAllCharmInstances();
            PrepareControlWaveLand(state, bestAmbiguousEnemy);
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

    private enum LandAttribution
    {
        Unattributed,
        LocalMatchingAbility,
        Foreign
    }

    private void NoteSpellHit(DateTime timestamp, string message)
    {
        if (!message.Contains(" damage by ", StringComparison.OrdinalIgnoreCase)) return;
        var match = NamedSpellHit.Match(message);
        if (!match.Success) return;
        _lastSpellHit = new RecentSpellHit(
            match.Groups["target"].Value.Trim(),
            match.Groups["source"].Value.Trim(),
            match.Groups["ability"].Value.Trim(),
            timestamp);
    }

    private void NoteOwnSongEnds(DateTime timestamp, string message)
    {
        if (!LocalSongEnds.IsMatch(message)) return;
        _ownSongLandWindowUntil = timestamp + OwnSongLandWindow;
    }

    private static bool IsIndefiniteSong(BuffRuleSettings rule) =>
        rule.TrackingMode == BuffTrackingMode.Song && rule.Category == SpellTrackerCategory.Buff;

    private static bool IsTimedDamageSong(BuffRuleSettings rule, IReadOnlySet<Guid> songDamageRuleIds) =>
        songDamageRuleIds.Contains(rule.Id);

    private bool IsTimedDamageSong(BuffRuleSettings rule) => IsTimedDamageSong(rule, _songDamageRuleIds);

    private static int SongDurationSeconds(BuffRuleSettings rule) =>
        rule.DurationSeconds > 0 ? rule.DurationSeconds : 3;

    /// <summary>
    /// Chords, Denon, and other AE songs share the same " winces." land suffix.
    /// Only the song with a pending "begin singing" (or a sole tracked match) may start.
    /// </summary>
    private bool TryConfirmTimedDamageSongEnemyLand(DateTime timestamp, string message)
    {
        var timedRules = _rules.Values.Where(rule => rule.IsEnabled && rule.TrackOthers && IsTimedDamageSong(rule))
            .ToArray();
        if (timedRules.Length == 0) return false;

        string? suffix = null;
        foreach (var rule in timedRules)
        {
            suffix = _uniqueOtherSuffixes.GetValueOrDefault(rule.Id)?
                .Concat(_ambiguousOtherSuffixes.GetValueOrDefault(rule.Id) ?? [])
                .FirstOrDefault(value => message.EndsWith(value, StringComparison.OrdinalIgnoreCase));
            if (suffix is not null) break;
        }
        if (suffix is null) return false;

        var target = message[..^suffix.Length].Trim();
        if (target.Length == 0) return false;

        var matching = timedRules.Where(rule =>
            _uniqueOtherSuffixes.GetValueOrDefault(rule.Id)?.Contains(suffix) == true ||
            _ambiguousOtherSuffixes.GetValueOrDefault(rule.Id)?.Contains(suffix) == true).ToArray();
        if (matching.Length == 0) return false;

        var pending = matching.Where(rule => IsPendingCast(rule, timestamp)).ToArray();
        if (pending.Length > 0)
        {
            var rule = pending.OrderByDescending(item => _states[item.Id].PendingCastStartedAt).First();
            ActivateSongDamage(_states[rule.Id], rule, timestamp, target);
            return true;
        }

        if (matching.Length == 1)
        {
            ActivateSongDamage(_states[matching[0].Id], matching[0], timestamp, target);
            return true;
        }

        var active = matching.Where(rule => _states[rule.Id].Instances.ContainsKey(SelfTargetKey)).ToArray();
        if (active.Length == 1)
        {
            RefreshSongDamage(_states[active[0].Id], active[0], timestamp);
            return true;
        }

        var attributed = matching
            .Where(rule => AcceptsLandAttribution(rule, target, timestamp, requireAbilityMatch: true)).ToArray();
        if (attributed.Length == 1)
        {
            ActivateSongDamage(_states[attributed[0].Id], attributed[0], timestamp, target);
            return true;
        }

        return false;
    }

    private bool AcceptsOwnDamageSongLand(BuffRuleSettings rule, DateTime timestamp)
    {
        if (IsPendingCast(rule, timestamp)) return true;
        if (_states[rule.Id].Instances.ContainsKey(SelfTargetKey)) return true;
        return CountEnabledTimedSongRules() == 1;
    }

    private void ClearOtherTimedDamageSongInstances(Guid exceptRuleId, bool silent = true)
    {
        foreach (var rule in _rules.Values.Where(item => item.IsEnabled && IsTimedDamageSong(item) &&
                                                         item.Id != exceptRuleId))
        {
            var state = _states[rule.Id];
            if (!state.Instances.Remove(SelfTargetKey)) continue;
            if (!silent && state.StopReason == BuffStopReason.None)
                state.StopReason = BuffStopReason.Expired;
        }
    }

    private void RefreshSongDamage(RuleRuntime state, BuffRuleSettings rule, DateTime timestamp)
    {
        if (!state.Instances.TryGetValue(SelfTargetKey, out var instance)) return;
        instance.StartedAt = timestamp;
        instance.ExpiresAt = timestamp.AddSeconds(SongDurationSeconds(rule));
        instance.LastLandPulseAt = timestamp;
        state.StopReason = BuffStopReason.None;
        ClearPendingCast(state);
    }

    private void ActivateSongDamage(RuleRuntime state, BuffRuleSettings rule, DateTime timestamp,
        string? landTarget = null)
    {
        ClearOtherTimedDamageSongInstances(rule.Id);
        ClearPendingCast(state);
        // Bard charm songs are TrackSelf=false: one pet, one timer on that name.
        // AE damage songs stay on Self so one twist covers every wince.
        if (!rule.TrackSelf && !string.IsNullOrWhiteSpace(landTarget))
        {
            foreach (var key in state.Instances.Keys.ToArray())
                state.Instances.Remove(key);
            var target = landTarget.Trim();
            Activate(state, rule, target, target, false, timestamp, clearsOnTargetDeath: true);
            return;
        }

        Activate(state, rule, SelfTargetKey, "Self", true, timestamp, clearsOnTargetDeath: false);
    }

    private int CountEnabledTimedSongRules() =>
        _rules.Values.Count(rule => rule.IsEnabled && IsTimedDamageSong(rule));

    private bool ConfirmSongDamageFromOwnedHit(DateTime timestamp, string message)
    {
        if (!message.Contains(" damage by ", StringComparison.OrdinalIgnoreCase)) return false;
        var match = NamedSpellHit.Match(message);
        if (!match.Success || !IsLocalHitSource(match.Groups["source"].Value)) return false;

        var ability = match.Groups["ability"].Value.Trim();
        if (ability.Length == 0) return false;

        var matched = false;
        foreach (var rule in MatchingEnabledRules(ability).Where(IsTimedDamageSong))
        {
            var hitTarget = match.Groups["target"].Value.Trim();
            ActivateSongDamage(_states[rule.Id], rule, timestamp, rule.TrackSelf ? null : hitTarget);
            matched = true;
        }

        return matched;
    }

    private bool AcceptsOwnSongLand(BuffRuleSettings rule, DateTime timestamp, string message)
    {
        var state = _states[rule.Id];
        if (state.Instances.ContainsKey(SelfTargetKey)) return true;
        if (IsPendingCast(rule, timestamp)) return true;
        if (_ownSongLandWindowUntil is { } until && timestamp <= until) return true;
        // Unique catalog land text (e.g. Anthem de Arms) needs no prior "Your song ends."
        if (_isAmbiguousSelfAppliedMessage?.Invoke(message) == false) return true;
        // Shared Selo-style text: accept when only one enabled song rule uses this line.
        return CountEnabledSongRulesMatchingSelfMessage(message) == 1;
    }

    private int CountEnabledSongRulesMatchingSelfMessage(string message) =>
        _rules.Values.Count(rule =>
            rule.IsEnabled &&
            rule.TrackingMode == BuffTrackingMode.Song &&
            rule.Category == SpellTrackerCategory.Buff &&
            rule.TrackSelf &&
            _selfAppliedMessages.GetValueOrDefault(rule.Id)?.Contains(message) == true);

    private BuffRuleSettings[] ResolveAmbiguousSongLand(IReadOnlyList<BuffRuleSettings> matches)
    {
        var withActive = matches.Where(rule => _states[rule.Id].Instances.ContainsKey(SelfTargetKey)).ToArray();
        if (withActive.Length == 1) return withActive;
        return matches.Count == 1 ? matches.ToArray() : [];
    }

    private bool IsPendingCast(BuffRuleSettings rule, DateTime timestamp)
    {
        var state = _states[rule.Id];
        return state.PendingCastStartedAt is { } castStarted && timestamp >= castStarted &&
               state.PendingStart is { } expected &&
               timestamp <= (state.PendingConfirmationEndsAt ?? expected + ConfirmationGrace);
    }

    private void ExpireSelfBySpellName(string spell)
    {
        foreach (var rule in MatchingEnabledRules(spell))
        {
            var state = _states[rule.Id];
            if (!state.Instances.ContainsKey(SelfTargetKey)) continue;
            StopSelf(rule.Id, BuffStopReason.Expired, preserveNewerPending: true);
        }
    }

    private bool TryGetRecentSpellHit(string target, DateTime timestamp, out string source, out string ability)
    {
        source = string.Empty;
        ability = string.Empty;
        if (_lastSpellHit is not { } hit) return false;
        if (timestamp - hit.Timestamp > SpellHitAttributionWindow || timestamp < hit.Timestamp)
            return false;
        if (!hit.Target.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        source = hit.Source;
        ability = hit.Ability;
        return true;
    }

    private static bool IsLocalHitSource(string source) =>
        source.Equals("You", StringComparison.OrdinalIgnoreCase) ||
        source.Equals("YOUR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Unique land text proves the spell. A pet/group hit only blocks when it is the
    /// same spell family (their Odium, not Puma Maw). Unrelated procs must not hide yours.
    /// </summary>
    private bool AcceptsLandAttribution(BuffRuleSettings rule, string target, DateTime timestamp,
        bool requireAbilityMatch)
    {
        if (!TryGetRecentSpellHit(target, timestamp, out var source, out var ability))
            return !requireAbilityMatch;
        var sameFamily = SpellNameNormalizer.BelongsToFamily(ability, rule.SpellName);
        if (!IsLocalHitSource(source))
            return !sameFamily;
        return !requireAbilityMatch || sameFamily;
    }

    private LandAttribution ClassifyLandAttribution(BuffRuleSettings rule, string target, DateTime timestamp)
    {
        if (!TryGetRecentSpellHit(target, timestamp, out var source, out var ability))
            return LandAttribution.Unattributed;
        // Shared land text ("has been poisoned.") — any other caster on this target
        // could own the line. Unrelated *your* procs must not hide a pending DoT.
        if (!IsLocalHitSource(source))
            return LandAttribution.Foreign;
        return SpellNameNormalizer.BelongsToFamily(ability, rule.SpellName)
            ? LandAttribution.LocalMatchingAbility
            : LandAttribution.Unattributed;
    }

    /// <summary>
    /// "X has taken N damage from your Odium VIII" is unambiguous. Use it to open the
    /// overlay when land text was missed (pet procs, delayed messages, no catalog suffix).
    /// Ticks never refresh an existing timer.
    /// </summary>
    private bool ConfirmOwnedDotTick(DateTime timestamp, string message)
    {
        if (!message.Contains("from your", StringComparison.OrdinalIgnoreCase))
            return false;
        var match = LocalDotTick.Match(message);
        if (!match.Success)
            return false;

        var target = match.Groups["target"].Value.Trim();
        var ability = match.Groups["ability"].Value.Trim();
        if (target.Length == 0 || ability.Length == 0)
            return false;

        var matched = false;
        foreach (var rule in MatchingEnabledRules(ability))
        {
            if (IsTimedDamageSong(rule))
            {
                var state = _states[rule.Id];
                if (rule.TrackSelf && state.Instances.ContainsKey(SelfTargetKey))
                    RefreshSongDamage(state, rule, timestamp);
                else
                    ActivateSongDamage(state, rule, timestamp, rule.TrackSelf ? null : target);
                matched = true;
                continue;
            }

            if (!rule.TrackOthers || rule.Category != SpellTrackerCategory.DamageOverTime)
                continue;

            var dotState = _states[rule.Id];
            if (FindTargetInstanceKey(dotState, target) is not null)
            {
                matched = true;
                continue;
            }

            var isPending = dotState.PendingCastStartedAt is { } castStarted && timestamp >= castStarted &&
                            dotState.PendingStart is { } expected &&
                            timestamp <= (dotState.PendingConfirmationEndsAt ?? expected + ConfirmationGrace);
            var startedAt = isPending ? dotState.PendingStart ?? timestamp : timestamp;
            var targetKey = ResolveEnemyLandTargetKey(dotState, rule, target);
            dotState.Instances.Remove(UnconfirmedTargetKey);
            Activate(dotState, rule, targetKey, target, false, startedAt, clearPending: false,
                clearsOnTargetDeath: true);
            if (isPending)
                NoteLandConfirmation(dotState, rule, timestamp);
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
        {
            var reason = IsRecentGenericDispel(timestamp)
                ? BuffStopReason.Dispelled
                : BuffStopReason.Expired;
            StopSelf(rule.Id, reason, preserveNewerPending: true);
            if (rule.Category == SpellTrackerCategory.Hostile)
                _queuedAlerts.Enqueue(new BuffExpirationAlert(rule, BuffAlertPhase.Expired));
        }
        else
        {
            state.Instances.Clear();
            // Do not ClearPendingCast — a refresh cast may already be pending.
            state.StopReason = BuffStopReason.Expired;
            if (rule.Category == SpellTrackerCategory.Control || IsBardCharmSongRule(rule))
                _queuedAlerts.Enqueue(new BuffExpirationAlert(rule));
        }

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
            // Charm songs used to live on Self; Leave still names the pet in worn-off.
            if (key is null && IsBardCharmSongRule(rule) && state.Instances.ContainsKey(SelfTargetKey))
                key = SelfTargetKey;
            if (key is null && IsBardCharmSongRule(rule) && state.Instances.Count > 0)
            {
                state.Instances.Clear();
                state.StopReason = BuffStopReason.Expired;
                _queuedAlerts.Enqueue(new BuffExpirationAlert(rule));
                continue;
            }
            if (key is null) continue;
            var removed = state.Instances[key];
            var normalized = target.Trim();

            // Remes/reroot overwrite: a newer land for the same name just stacked a
            // replacement timer. Drop the previous application without alerting.
            // Same-second AE lands share StartedAt, so a real early break still alerts
            // (sibling StartedAt is not strictly greater).
            var isOverwrite = IsMezOrRoot(rule) &&
                state.Instances.Values.Any(item =>
                    item.TargetName.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
                    item.StartedAt > removed.StartedAt &&
                    timestamp - item.StartedAt <= ControlOverwriteGrace);

            // Recast wave already dropped prior timers before the new lands. EQ still
            // emits worn-off for the previous applications — ignore those so they do
            // not clear the fresh wave (no sibling left to prove overwrite).
            var isStaleWaveWornOff = !isOverwrite &&
                IsMezOrRoot(rule) &&
                state.ReplacedPreviousWaveThisCast &&
                state.PendingCastStartedAt is { } castAt &&
                removed.StartedAt >= castAt &&
                timestamp - removed.StartedAt <= ControlOverwriteGrace &&
                !state.Instances.Any(pair =>
                    pair.Key != key &&
                    pair.Value.TargetName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (isStaleWaveWornOff)
                continue;

            state.Instances.Remove(key);

            if (!isOverwrite && ShouldAlertOnWearOff(rule))
                _queuedAlerts.Enqueue(new BuffExpirationAlert(rule));
            if (state.Instances.Count == 0 && state.StopReason == BuffStopReason.None)
                state.StopReason = BuffStopReason.Expired;
        }
    }

    private static bool UsesTimedSongDuration(BuffRuleSettings rule) =>
        rule.TrackingMode == BuffTrackingMode.Song &&
        rule.Category == SpellTrackerCategory.DamageOverTime;

    private void Activate(RuleRuntime state, BuffRuleSettings rule, string targetKey,
        string targetName, bool isSelf, DateTime startedAt, bool clearPending = true,
        bool clearsOnTargetDeath = true)
    {
        if (clearPending) ClearPendingCast(state);
        var instance = new ActiveInstance
        {
            TargetName = targetName,
            IsSelf = isSelf,
            StartedAt = startedAt,
            ExpiresAt = IsIndefiniteSong(rule) ? IndefiniteSongExpiresAt
                : UsesTimedSongDuration(rule) ? startedAt.AddSeconds(SongDurationSeconds(rule))
                : startedAt.AddSeconds(rule.DurationSeconds),
            LastLandPulseAt = startedAt,
            ClearsOnTargetDeath = clearsOnTargetDeath
        };
        if (!isSelf && IsAnyCharmRule(rule))
            SeedCharmCollisionState(instance, startedAt);
        state.Instances[targetKey] = instance;
        state.StopReason = BuffStopReason.None;
    }

    private void SeedCharmCollisionState(ActiveInstance instance, DateTime startedAt)
    {
        var incomingAtLand = _recentIncomingOnYou.TryGetValue(instance.TargetName, out var hitAt) &&
                             startedAt - hitAt <= CharmIncomingLookback;
        instance.LastIncomingAt = null;
        instance.LastPetAllyAt = null;
        instance.SawIncomingGap = !incomingAtLand;
    }

    private void ActivateAndAlert(RuleRuntime state, BuffRuleSettings rule, string targetKey,
        string targetName, bool isSelf, DateTime startedAt, bool clearPending = true,
        bool clearsOnTargetDeath = true)
    {
        Activate(state, rule, targetKey, targetName, isSelf, startedAt, clearPending, clearsOnTargetDeath);
        if (rule.Category == SpellTrackerCategory.Hostile && isSelf)
            _queuedAlerts.Enqueue(new BuffExpirationAlert(rule, BuffAlertPhase.Landed));
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
        state.PendingRequiresConfirmation = false;
        state.ReplacedPreviousWaveThisCast = false;
    }

    private static bool IsCharmControl(BuffRuleSettings rule) =>
        rule.Category == SpellTrackerCategory.Control && rule.ControlType == ControlEffectType.Charm;

    private static bool IsBardCharmSongRule(BuffRuleSettings rule) =>
        rule.TrackingMode == BuffTrackingMode.Song &&
        !rule.TrackSelf &&
        SpellDataCatalog.LooksLikeBardCharmSongName(rule.SpellName);

    private static bool IsAnyCharmRule(BuffRuleSettings rule) =>
        IsCharmControl(rule) || IsBardCharmSongRule(rule);

    private static bool ShouldAlertOnWearOff(BuffRuleSettings rule) =>
        rule.Category == SpellTrackerCategory.Control || IsBardCharmSongRule(rule);

    /// <summary>
    /// Bard charm often breaks without a worn-off line. Hits on you from that name are a
    /// break only when logs are not also showing a same-named hostile already swinging.
    /// </summary>
    private bool TryBreakCharmOnHostileSwing(DateTime timestamp, string message)
    {
        if (!LooksLikeIncomingSwingOnYou(message)) return false;
        var broke = false;
        foreach (var rule in _rules.Values.Where(item => item.IsEnabled && IsAnyCharmRule(item)))
        {
            var state = _states[rule.Id];
            var keys = state.Instances
                .Where(pair => !pair.Value.IsSelf &&
                               MessageStartsWithCharmTarget(message, pair.Value.TargetName) &&
                               ShouldBreakCharmOnIncoming(pair.Value, timestamp))
                .Select(pair => pair.Key)
                .ToArray();
            if (keys.Length == 0) continue;
            foreach (var key in keys)
                state.Instances.Remove(key);
            if (state.Instances.Count == 0)
                state.StopReason = BuffStopReason.Expired;
            _queuedAlerts.Enqueue(new BuffExpirationAlert(rule));
            broke = true;
        }

        return broke;
    }

    private static bool ShouldBreakCharmOnIncoming(ActiveInstance instance, DateTime timestamp)
    {
        if (instance.LastPetAllyAt is { } ally && timestamp - ally < CharmPetAllySilence)
        {
            instance.LastIncomingAt = timestamp;
            instance.SawIncomingGap = false;
            return false;
        }

        if (instance.SawIncomingGap)
            return true;

        if (instance.LastIncomingAt is null)
        {
            if (timestamp - instance.StartedAt >= CharmLandGrace)
                return true;
            instance.LastIncomingAt = timestamp;
            return false;
        }

        instance.LastIncomingAt = timestamp;
        return instance.LastPetAllyAt is not null;
    }

    private void NoteRecentIncomingOnYou(DateTime timestamp, string message)
    {
        if (!TryGetIncomingAttackerOnYou(message, out var name)) return;
        _recentIncomingOnYou[name] = timestamp;
        foreach (var stale in _recentIncomingOnYou
                     .Where(pair => timestamp - pair.Value > CharmIncomingLookback + CharmIncomingGap)
                     .Select(pair => pair.Key)
                     .ToArray())
            _recentIncomingOnYou.Remove(stale);
    }

    private void RefreshCharmIncomingGaps(DateTime timestamp)
    {
        foreach (var instance in LiveCharmInstances())
        {
            if (instance.SawIncomingGap || instance.LastIncomingAt is not { } last) continue;
            if (timestamp - last >= CharmIncomingGap)
                instance.SawIncomingGap = true;
        }
    }

    private void NoteCharmPetAlly(DateTime timestamp, string message)
    {
        if (LooksLikeIncomingSwingOnYou(message)) return;
        if (message.Contains("glaze over", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("told you", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("tells you", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("says,", StringComparison.OrdinalIgnoreCase))
            return;
        if (!message.Contains(" for ", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("tries to ", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var instance in LiveCharmInstances())
        {
            if (MessageStartsWithCharmTarget(message, instance.TargetName))
                instance.LastPetAllyAt = timestamp;
        }
    }

    private IEnumerable<ActiveInstance> LiveCharmInstances() =>
        _rules.Values.Where(rule => rule.IsEnabled && IsAnyCharmRule(rule))
            .SelectMany(rule => _states[rule.Id].Instances.Values)
            .Where(instance => !instance.IsSelf);

    private bool StartsWithLiveCharmTarget(string message) =>
        LiveCharmInstances().Any(instance => MessageStartsWithCharmTarget(message, instance.TargetName));

    private static bool LooksLikeIncomingSwingOnYou(string message)
    {
        if (message.StartsWith("You ", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Your ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (message.Contains("told you", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("tells you", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("says,", StringComparison.OrdinalIgnoreCase))
            return false;
        return message.Contains(" YOU for ", StringComparison.OrdinalIgnoreCase) ||
               (message.Contains("tries to ", StringComparison.OrdinalIgnoreCase) &&
                message.Contains(" YOU", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetIncomingAttackerOnYou(string message, out string name)
    {
        name = string.Empty;
        var marker = message.IndexOf(" YOU for ", StringComparison.OrdinalIgnoreCase);
        string before;
        if (marker >= 0)
        {
            before = message[..marker];
        }
        else
        {
            var tries = message.IndexOf("tries to ", StringComparison.OrdinalIgnoreCase);
            if (tries <= 0 ||
                message.IndexOf(" YOU", StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            before = message[..tries];
        }

        var lastSpace = before.LastIndexOf(' ');
        if (lastSpace <= 0) return false;
        name = before[..lastSpace].Trim();
        return name.Length > 0;
    }

    private static bool MessageStartsWithCharmTarget(string message, string target)
    {
        var name = target.Trim();
        if (name.Length == 0 || message.Length <= name.Length) return false;
        if (!message.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        var next = message[name.Length];
        return next is ' ' or '\'';
    }

    private static bool NamesMatchCharmTarget(string left, string right) =>
        left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Charm is always a single pet. Any successful charm land replaces all prior charm tracks.
    /// </summary>
    private void ClearAllCharmInstances()
    {
        foreach (var charmRule in _rules.Values.Where(IsCharmControl))
            _states[charmRule.Id].Instances.Clear();
    }

    /// <summary>
    /// Mez/Root recast: drop timers from earlier casts so only successful lands from the
    /// current cast wave remain. Lands within the same AE burst keep stacking.
    /// </summary>
    private static void PrepareControlWaveLand(RuleRuntime state, BuffRuleSettings rule)
    {
        if (!IsMezOrRoot(rule) || state.PendingCastStartedAt is not { } castAt) return;
        var stale = state.Instances
            .Where(pair => pair.Value.StartedAt < castAt)
            .Select(pair => pair.Key)
            .ToArray();
        if (stale.Length == 0) return;
        foreach (var key in stale)
            state.Instances.Remove(key);
        state.ReplacedPreviousWaveThisCast = true;
    }

    private void StopSelf(Guid ruleId, BuffStopReason reason, bool preserveNewerPending = false)
    {
        var state = _states[ruleId];
        var hadSelf = state.Instances.ContainsKey(SelfTargetKey);
        if (preserveNewerPending &&
            hadSelf &&
            state.Instances.TryGetValue(SelfTargetKey, out var self) &&
            state.PendingCastStartedAt is { } pending &&
            pending > self.StartedAt)
        {
            // Keep the in-flight refresh cast.
        }
        else ClearPendingCast(state);
        state.Instances.Remove(SelfTargetKey);
        state.StopReason = reason;
        if (hadSelf && reason == BuffStopReason.Expired &&
            _rules.TryGetValue(ruleId, out var rule) && IsIndefiniteSong(rule))
            _queuedAlerts.Enqueue(new BuffExpirationAlert(rule, BuffAlertPhase.Expired));
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
            // break. Charm is instead ended by worn-off, a swing on you that is not
            // a same-named twin, a new charm landing, player death, or max duration.
            if (IsAnyCharmRule(rule))
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
    /// old one). DoT/Charm keep one live slot per target display name so shared land text
    /// cannot stack duplicate rows for the same mob.
    /// </summary>
    private static string ResolveEnemyLandTargetKey(RuleRuntime state, BuffRuleSettings rule, string target) =>
        IsMezOrRoot(rule)
            ? ResolveNewControlTargetKey(state, target)
            : FindTargetInstanceKey(state, target) ?? target.Trim();

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

    private static string? FindTargetInstanceKey(RuleRuntime state, string target) =>
        state.Instances
            .Where(pair => pair.Value.TargetName.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Value.ExpiresAt)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key)
            .FirstOrDefault();

    private IEnumerable<BuffRuleSettings> MatchingEnabledRules(string spell)
    {
        var family = SpellNameNormalizer.GetFamilyName(spell);
        if (family.Length == 0) yield break;
        if (!_rulesByFamily.TryGetValue(family, out var ruleIds)) yield break;
        foreach (var ruleId in ruleIds)
        {
            if (!_rules.TryGetValue(ruleId, out var rule) || !rule.IsEnabled) continue;
            if (SpellNameNormalizer.BelongsToFamily(spell, rule.SpellName)) yield return rule;
        }
    }

    private static bool IsEnemyEffectCategory(BuffRuleSettings rule) =>
        rule.Category is SpellTrackerCategory.DamageOverTime or SpellTrackerCategory.Control;

    /// <summary>
    /// Buffs and DoTs clamp the pending window to 1s after the first land so shared
    /// land text (pets/group/NPCs) cannot keep arming the same cast. AE mez/root keep
    /// the full cast grace so every land in the burst can open its own target instance.
    /// </summary>
    private static void NoteLandConfirmation(RuleRuntime state, BuffRuleSettings rule, DateTime timestamp)
    {
        if (IsMezOrRoot(rule)) return;
        state.PendingConfirmationEndsAt ??= timestamp.AddSeconds(1);
    }

    private void ClearEnemyEffectsOnZone()
    {
        foreach (var rule in _rules.Values)
        {
            // Outgoing DoT/Control on mobs end on zone. Hostile-on-you persists across
            // zone (same as beneficial self buffs) until fade, duration, or death.
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

    private void RebuildMessageRelevanceHints()
    {
        _hasEnabledRules = _rules.Values.Any(rule => rule.IsEnabled);
        _hasEnabledCharmRule = _rules.Values.Any(rule => rule.IsEnabled && IsAnyCharmRule(rule));
        _enabledRules = _hasEnabledRules
            ? _rules.Values.Where(rule => rule.IsEnabled).ToList()
            : [];
        if (!_hasEnabledRules)
        {
            _configuredSelfMessages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _configuredFadeFragments = [];
            _configuredOtherSuffixes = [];
            return;
        }

        var self = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fades = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _rules.Values.Where(rule => rule.IsEnabled))
        {
            if (_selfAppliedMessages.TryGetValue(rule.Id, out var selfMessages))
            {
                foreach (var message in selfMessages) self.Add(message);
            }

            if (_fadeMessages.TryGetValue(rule.Id, out var fadeMessages))
            {
                foreach (var message in fadeMessages)
                {
                    if (message.Length >= 4) fades.Add(message);
                }
            }

            if (_uniqueOtherSuffixes.TryGetValue(rule.Id, out var uniqueSuffixes))
            {
                foreach (var suffix in uniqueSuffixes)
                {
                    if (suffix.Length >= 4) suffixes.Add(suffix);
                }
            }

            if (_ambiguousOtherSuffixes.TryGetValue(rule.Id, out var ambiguousSuffixes))
            {
                foreach (var suffix in ambiguousSuffixes)
                {
                    if (suffix.Length >= 4) suffixes.Add(suffix);
                }
            }
        }

        _configuredSelfMessages = self;
        _configuredFadeFragments = fades.ToArray();
        _configuredOtherSuffixes = suffixes.OrderByDescending(suffix => suffix.Length).ToArray();
    }

    private void RebuildRuleFamilyIndex()
    {
        var index = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _enabledRules)
        {
            var family = SpellNameNormalizer.GetFamilyName(rule.SpellName);
            if (family.Length == 0) continue;
            if (!index.TryGetValue(family, out var ruleIds))
            {
                ruleIds = [];
                index[family] = ruleIds;
            }

            ruleIds.Add(rule.Id);
        }

        _rulesByFamily = index;
    }

    private bool IsRecentGenericDispel(DateTime timestamp) =>
        _lastGenericDispelAt is { } at &&
        timestamp >= at &&
        timestamp - at <= TimeSpan.FromSeconds(1);

    private static bool ContainsRelevanceKeyword(string message) =>
        message.Contains("begin casting", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("begin singing", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("worn off", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("dispelled", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("resisted your", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("fizzles", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("interrupted", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("LOADING", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("entered ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("song ends", StringComparison.OrdinalIgnoreCase) ||
        message.Contains(" damage by ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("has taken ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("winces", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("mesmerized", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("charmed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("poisoned", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("glaze over", StringComparison.OrdinalIgnoreCase) ||
        message.Contains(" has been ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("begins to ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("You feel ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Your ", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("died", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("slain", StringComparison.OrdinalIgnoreCase);
}
