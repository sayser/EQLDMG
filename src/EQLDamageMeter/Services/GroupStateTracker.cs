using System.Text.RegularExpressions;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public enum GroupChangeKind
{
    None,
    EnteredGroup,
    LocalPlayerLeft,
    MemberJoined,
    MemberLeft,
    PetControlled
}

public sealed record GroupChange(GroupChangeKind Kind, string? Member = null, string? Owner = null);

public sealed class GroupStateTracker(string localPlayerName)
{
    private sealed class PendingHealCast(DateTime timestamp)
    {
        public DateTime Timestamp { get; } = timestamp;
        public HashSet<string> Participants { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ConfirmedAsGroupCast { get; set; }
    }

    private static readonly TimeSpan CastAttributionWindow = TimeSpan.FromSeconds(3);

    private sealed record PendingCharmCast(DateTime Timestamp, string Caster);
    private sealed record PendingCompanionSummon(DateTime Timestamp, string Owner);

    private static readonly Regex Invitation = new(@"^(?<name>.+?) invites you to join a group\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MemberJoined = new(@"^(?<name>.+?) has joined the group\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MemberLeft = new(@"^(?<name>.+?) has left the group\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GroupSpeaker = new(@"^(?<name>.+?) tell(?:s)? the group,",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BeginsCasting = new(
        @"^(?<name>You|.+?) begin(?:s)? casting (?<ability>.+?)\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Charmed = new(@"^(?<name>.+?) has been charmed\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalSpellWornOff = new(
        @"^Your (?<ability>.+?) spell has worn off of (?<name>.+?)\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string SpellFailureSuffix = @" spell (?:is interrupted\.|fizzles!|did not take hold(?: on .+?)?\..*)$";
    private static readonly Regex LocalSpellFailed = new(
        @"^Your (?<ability>.+?)" + SpellFailureSuffix,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OtherSpellFailed = new(
        @"^(?<caster>.+?)(?:'|`|\u2019)s (?<ability>.+?)" + SpellFailureSuffix,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalSpellResisted = new(
        @"^.+? resisted your (?<ability>.+?)!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OtherSpellResisted = new(
        @"^.+? resisted (?<caster>.+?)(?:'|`|\u2019)s (?<ability>.+?)!$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CharmAbility = new(
        @"(?:^|\b)(?:charm|allure|beguile|dominat\w*|enslav\w*|captivat\w*|cajol\w*|befriend\w*|tame\w*)(?:\b|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LocalCompanionSummon = new(
        @"^You summon a companion spirit\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OtherCompanionSummon = new(
        @"^(?<name>.+?) summons a companion spirit\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PetMasterTell = new(
        @"^(?<pet>.+?) told you, '(?:Attacking .+|I am unable to wake .+?,) Master\.'$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PetMasterSay = new(
        @"^(?<pet>.+?) says, '(?:Now holding, Master\.|I beg forgiveness, Master\.  That is not a legal target\.|As you wish, oh great one\.)'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TargetSlain = new(
        @"^(?<name>.+?) (?:has been slain by .+!?|(?:has )?died\.)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ZoneLoading = new(
        @"^LOADING, PLEASE WAIT\.\.\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ZoneEntered = new(
        @"^You have entered .+\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string? _pendingInviter;
    private readonly Dictionary<string, PendingHealCast> _pendingHealCasts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingCharmCast> _pendingCharmCasts = [];
    private readonly List<PendingCompanionSummon> _pendingCompanionSummons = [];
    private readonly Dictionary<string, string> _controlledPetOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _memberConfirmedAt =
        new(StringComparer.OrdinalIgnoreCase) { [localPlayerName] = DateTime.MinValue };
    private readonly Dictionary<string, DateTime> _controlledPetSince =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _companionPets = new(StringComparer.OrdinalIgnoreCase);

    // A group heal that lands on a pet before its Charm binding is seen promotes that
    // pet into KnownMembers, and a promoted name is never offered to the pet binders
    // again. Remembering the last owner of every bound pet lets such a name still
    // resolve to its owner without widening what counts as group output.
    private readonly Dictionary<string, string> _petOwnerHistory =
        new(StringComparer.OrdinalIgnoreCase);
    private PendingCharmCast? _lastEligibleCast;
    public bool IsGrouped { get; private set; }
    public HashSet<string> KnownMembers { get; } = new(StringComparer.OrdinalIgnoreCase) { localPlayerName };

    public bool IsConfirmedMemberOrPet(string name) =>
        KnownMembers.Contains(name) || _controlledPetOwners.ContainsKey(name) ||
        FindOwnerByPetName(name) is not null;

    public bool WasConfirmedMemberOrPetAt(string name, DateTime timestamp)
    {
        if (_controlledPetOwners.ContainsKey(name))
            return !_controlledPetSince.TryGetValue(name, out var since) || since <= timestamp;
        if (KnownMembers.Contains(name))
            return !_memberConfirmedAt.TryGetValue(name, out var since) || since <= timestamp;
        var owner = FindOwnerByPetName(name);
        return owner is not null &&
               (!_memberConfirmedAt.TryGetValue(owner, out var ownerSince) || ownerSince <= timestamp);
    }

    // Runs for every combat event, so it avoids the closure and interpolated-string
    // allocations that a LINQ predicate over KnownMembers would produce.
    private string? FindOwnerByPetName(string candidate)
    {
        foreach (var member in KnownMembers)
        {
            if (IsOwnedPet(candidate, member)) return member;
        }

        return null;
    }

    public bool TryGetControlledPetOwner(string pet, out string? owner) =>
        _controlledPetOwners.TryGetValue(pet, out owner);

    // A landed control effect names the effect but not the caster, so the cast that
    // started moments earlier is the only attribution the log offers.
    public bool TryGetRecentEligibleCaster(DateTime timestamp, out string? caster)
    {
        caster = null;
        if (_lastEligibleCast is not { } cast ||
            timestamp < cast.Timestamp || timestamp - cast.Timestamp > CastAttributionWindow)
        {
            return false;
        }

        caster = cast.Caster;
        return true;
    }

    public bool TryGetPetOwner(string pet, out string? owner)
    {
        if (_controlledPetOwners.TryGetValue(pet, out owner)) return true;
        owner = FindOwnerByPetName(pet);
        if (owner is not null) return true;

        // Only names already treated as group entities fall back to history, so this
        // resolves ownership for damage that is counted either way and never makes a
        // hostile NPC eligible.
        return KnownMembers.Contains(pet) && _petOwnerHistory.TryGetValue(pet, out owner);
    }

    // Matches "Owner's pet" where the client may emit a straight, back or curly quote.
    public static bool IsOwnedPet(string candidate, string owner)
    {
        if (candidate.Length < owner.Length + 3 ||
            !candidate.StartsWith(owner, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return candidate[owner.Length] is '\'' or '`' or '\u2019' &&
               candidate[owner.Length + 1] is 's' or 'S' &&
               candidate[owner.Length + 2] == ' ';
    }

    public bool ObserveHealing(HealingEvent healing)
    {
        if (!IsGrouped) return false;

        foreach (var expired in _pendingHealCasts
                     .Where(item => healing.Timestamp - item.Value.Timestamp > TimeSpan.FromSeconds(2))
                     .Select(item => item.Key).ToArray())
        {
            _pendingHealCasts.Remove(expired);
        }

        var key = $"{healing.Timestamp.Ticks}|{healing.Source}|{healing.Ability}";
        if (!_pendingHealCasts.TryGetValue(key, out var cast))
        {
            cast = new PendingHealCast(healing.Timestamp);
            _pendingHealCasts[key] = cast;
        }

        cast.Participants.Add(healing.Source);
        cast.Participants.Add(healing.Target);
        if (healing.Target.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) &&
            IsConfirmedMemberOrPet(healing.Source))
        {
            cast.ConfirmedAsGroupCast = true;
        }

        if (!cast.ConfirmedAsGroupCast) return false;

        var changed = false;
        foreach (var participant in cast.Participants)
        {
            // Group heals may include regular and charmed pets. Keep the player-member
            // set free of pet identities so an owner departure cannot leave a stale
            // pet name eligible for outgoing group damage.
            if (!IsConfirmedPet(participant, cast.Participants))
            {
                changed |= MarkMember(participant, healing.Timestamp);
            }
        }
        return changed;
    }

    public bool ObserveDamage(DamageEvent damage)
    {
        if (!_controlledPetOwners.TryGetValue(damage.Source, out var owner) ||
            damage.Source.Equals(damage.Target, StringComparison.OrdinalIgnoreCase) ||
            !IsConfirmedMemberOrPet(damage.Target)) return false;

        // The local client reports an authoritative Charm wear-off line. Do not infer a
        // local break from combat because multiple living NPCs can have the pet's name.
        if (owner.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase)) return false;

        return RemoveControlledPet(damage.Source);
    }

    public bool ObserveOutcome(CombatOutcomeEvent outcome)
    {
        if (outcome.Target is null ||
            !_controlledPetOwners.TryGetValue(outcome.Source, out var owner) ||
            outcome.Source.Equals(outcome.Target, StringComparison.OrdinalIgnoreCase) ||
            !IsConfirmedMemberOrPet(outcome.Target)) return false;

        // Local Charm has an explicit wear-off message; name-only combat outcomes are
        // not authoritative when several living NPCs can share the controlled name.
        if (owner.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase)) return false;

        return RemoveControlledPet(outcome.Source);
    }

    public GroupChange Process(string message, DateTime? timestamp = null)
    {
        // Gate / zone ends charm and companion control instantly.
        if (ZoneLoading.IsMatch(message) || ZoneEntered.IsMatch(message))
        {
            ClearLocalControlledPets();
            _pendingCharmCasts.Clear();
            _pendingCompanionSummons.Clear();
            return new GroupChange(GroupChangeKind.None);
        }

        if (timestamp.HasValue)
        {
            if (_pendingCharmCasts.Count > 0)
            {
                _pendingCharmCasts.RemoveAll(item =>
                    timestamp.Value - item.Timestamp > TimeSpan.FromSeconds(12));
            }

            if (_pendingCompanionSummons.Count > 0)
            {
                _pendingCompanionSummons.RemoveAll(item =>
                    timestamp.Value - item.Timestamp > TimeSpan.FromSeconds(3));
            }

            if (LocalCompanionSummon.IsMatch(message))
            {
                _pendingCompanionSummons.RemoveAll(item =>
                    item.Owner.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase));
                _pendingCompanionSummons.Add(new PendingCompanionSummon(timestamp.Value, localPlayerName));
            }

            // A summon is followed by the pet's first cast, but any bystander casting in
            // that window would be bound instead. Tracking only group summoners keeps a
            // stranger's pet - or a nearby hostile caster - out of the group's totals.
            var otherSummon = OtherCompanionSummon.Match(message);
            if (otherSummon.Success && IsGrouped && KnownMembers.Contains(otherSummon.Groups["name"].Value))
            {
                var owner = otherSummon.Groups["name"].Value;
                _pendingCompanionSummons.RemoveAll(item =>
                    item.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase));
                _pendingCompanionSummons.Add(new PendingCompanionSummon(timestamp.Value, owner));
            }

            var masterTell = PetMasterTell.Match(message);
            if (masterTell.Success)
            {
                var bound = BindCompanionPet(masterTell.Groups["pet"].Value, localPlayerName, timestamp.Value);
                if (bound is not null) return bound;
            }

            var masterSay = PetMasterSay.Match(message);
            if (masterSay.Success)
            {
                var bound = BindCompanionPet(masterSay.Groups["pet"].Value, localPlayerName, timestamp.Value);
                if (bound is not null) return bound;
            }

            var companionDeath = TargetSlain.Match(message);
            if (companionDeath.Success)
            {
                var slain = companionDeath.Groups["name"].Value;
                if (_companionPets.Contains(slain) &&
                    _controlledPetOwners.TryGetValue(slain, out var slainOwner) &&
                    slainOwner.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveControlledPet(slain);
                }
            }

            var localFailure = LocalSpellFailed.Match(message);
            if (localFailure.Success && CharmAbility.IsMatch(localFailure.Groups["ability"].Value))
            {
                RemoveLatestPendingCharm(localPlayerName);
            }

            var otherFailure = OtherSpellFailed.Match(message);
            if (otherFailure.Success && CharmAbility.IsMatch(otherFailure.Groups["ability"].Value))
            {
                RemoveLatestPendingCharm(otherFailure.Groups["caster"].Value);
            }

            var localResist = LocalSpellResisted.Match(message);
            if (localResist.Success && CharmAbility.IsMatch(localResist.Groups["ability"].Value))
            {
                RemoveLatestPendingCharm(localPlayerName);
            }

            var otherResist = OtherSpellResisted.Match(message);
            if (otherResist.Success && CharmAbility.IsMatch(otherResist.Groups["ability"].Value))
            {
                RemoveLatestPendingCharm(otherResist.Groups["caster"].Value);
            }

            var casting = BeginsCasting.Match(message);
            if (casting.Success)
            {
                var caster = casting.Groups["name"].Value.Equals("You", StringComparison.OrdinalIgnoreCase)
                    ? localPlayerName
                    : casting.Groups["name"].Value;
                var ability = casting.Groups["ability"].Value;
                var isEligibleCaster = caster.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                                       IsGrouped && KnownMembers.Contains(caster);
                if (isEligibleCaster) _lastEligibleCast = new PendingCharmCast(timestamp.Value, caster);
                if (isEligibleCaster && CharmAbility.IsMatch(ability))
                {
                    _pendingCharmCasts.Add(new PendingCharmCast(timestamp.Value, caster));
                }
                else if (!caster.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) &&
                         !KnownMembers.Contains(caster) &&
                         !_controlledPetOwners.ContainsKey(caster) &&
                         TryBindPendingCompanion(caster, timestamp.Value) is { } companionBind)
                {
                    return companionBind;
                }
            }

            var charmed = Charmed.Match(message);
            if (charmed.Success && _pendingCharmCasts.Count > 0)
            {
                // Equal-duration casts complete in start order. Retain unrelated later
                // casts instead of dropping every pending owner on the first success.
                var cast = _pendingCharmCasts[0];
                var pet = charmed.Groups["name"].Value;
                foreach (var previousPet in _controlledPetOwners
                             .Where(item => item.Value.Equals(cast.Caster, StringComparison.OrdinalIgnoreCase))
                             .Select(item => item.Key).ToArray())
                {
                    RemoveControlledPet(previousPet);
                }
                _companionPets.Remove(pet);
                _controlledPetOwners[pet] = cast.Caster;
                _controlledPetSince[pet] = timestamp.Value;
                _petOwnerHistory[pet] = cast.Caster;
                _pendingCharmCasts.RemoveAt(0);
                return new GroupChange(GroupChangeKind.PetControlled, pet, cast.Caster);
            }

            var wornOff = LocalSpellWornOff.Match(message);
            if (wornOff.Success && CharmAbility.IsMatch(wornOff.Groups["ability"].Value))
            {
                var pet = wornOff.Groups["name"].Value;
                if (_controlledPetOwners.TryGetValue(pet, out var owner) &&
                    owner.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) &&
                    !_companionPets.Contains(pet))
                {
                    RemoveControlledPet(pet);
                }
            }

            // Generic slain messages carry only an NPC name. Do not release charm by name:
            // another living NPC can share the controlled pet's name. Local Charm has
            // an explicit wear-off event, and remote ownership is released by friendly
            // targeting, owner departure, or the owner's next successful Charm.
        }

        var match = Invitation.Match(message);
        if (match.Success)
        {
            _pendingInviter = match.Groups["name"].Value;
            return new GroupChange(GroupChangeKind.None);
        }

        if (message.Equals("You have joined the group.", StringComparison.OrdinalIgnoreCase))
        {
            IsGrouped = true;
            _pendingHealCasts.Clear();
            RetainLocalCharmState();
            KnownMembers.Clear();
            _memberConfirmedAt.Clear();
            MarkMember(localPlayerName, DateTime.MinValue);
            if (!string.IsNullOrWhiteSpace(_pendingInviter))
            {
                MarkMember(_pendingInviter, timestamp ?? DateTime.MinValue);
            }
            _pendingInviter = null;
            return new GroupChange(GroupChangeKind.EnteredGroup);
        }

        if (message.Equals("You have been removed from the group.", StringComparison.OrdinalIgnoreCase) ||
            message.Equals("You have left the group.", StringComparison.OrdinalIgnoreCase) ||
            message.Equals("You leave the group.", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("group has been disbanded", StringComparison.OrdinalIgnoreCase))
        {
            IsGrouped = false;
            _pendingHealCasts.Clear();
            RetainLocalCharmState();
            _pendingInviter = null;
            KnownMembers.Clear();
            _memberConfirmedAt.Clear();
            MarkMember(localPlayerName, DateTime.MinValue);
            return new GroupChange(GroupChangeKind.LocalPlayerLeft, localPlayerName);
        }

        match = MemberJoined.Match(message);
        if (IsGrouped && match.Success)
        {
            var member = match.Groups["name"].Value;
            MarkMember(member, timestamp ?? DateTime.MinValue);
            return new GroupChange(GroupChangeKind.MemberJoined, member);
        }

        match = MemberLeft.Match(message);
        if (IsGrouped && match.Success)
        {
            var member = match.Groups["name"].Value;
            KnownMembers.Remove(member);
            _memberConfirmedAt.Remove(member);
            _pendingHealCasts.Clear();
            _pendingCharmCasts.RemoveAll(item =>
                item.Caster.Equals(member, StringComparison.OrdinalIgnoreCase));
            var departingPets = _controlledPetOwners
                         .Where(item => item.Value.Equals(member, StringComparison.OrdinalIgnoreCase))
                         .Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var inferredPet in KnownMembers.Where(name =>
                         departingPets.Contains(name) || IsOwnedPet(name, member)).ToArray())
            {
                KnownMembers.Remove(inferredPet);
                _memberConfirmedAt.Remove(inferredPet);
            }
            foreach (var pet in departingPets)
            {
                RemoveControlledPet(pet);
            }
            RemovePetHistoryExcept(name => !name.Equals(member, StringComparison.OrdinalIgnoreCase));
            return new GroupChange(GroupChangeKind.MemberLeft, member);
        }

        match = GroupSpeaker.Match(message);
        if (match.Success)
        {
            var speaker = match.Groups["name"].Value.Equals("You", StringComparison.OrdinalIgnoreCase)
                ? localPlayerName
                : match.Groups["name"].Value;
            if (!IsGrouped)
            {
                IsGrouped = true;
                _pendingInviter = null;
                _pendingHealCasts.Clear();
                RetainLocalCharmState();
                KnownMembers.Clear();
                _memberConfirmedAt.Clear();
                MarkMember(localPlayerName, DateTime.MinValue);
                MarkMember(speaker, timestamp ?? DateTime.MinValue);
                return new GroupChange(GroupChangeKind.EnteredGroup, speaker);
            }

            if (MarkMember(speaker, timestamp ?? DateTime.MinValue))
            {
                return new GroupChange(GroupChangeKind.MemberJoined, speaker);
            }
        }

        if (message.StartsWith("You cancel the invitation", StringComparison.OrdinalIgnoreCase))
        {
            _pendingInviter = null;
        }

        return new GroupChange(GroupChangeKind.None);
    }

    private void RemoveLatestPendingCharm(string caster)
    {
        var index = _pendingCharmCasts.FindLastIndex(item =>
            item.Caster.Equals(caster, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _pendingCharmCasts.RemoveAt(index);
    }

    private GroupChange? TryBindPendingCompanion(string pet, DateTime timestamp)
    {
        var pendingIndex = _pendingCompanionSummons.FindLastIndex(item =>
            timestamp - item.Timestamp <= TimeSpan.FromSeconds(3));
        if (pendingIndex < 0) return null;
        var pending = _pendingCompanionSummons[pendingIndex];
        _pendingCompanionSummons.RemoveAt(pendingIndex);
        return BindCompanionPet(pet, pending.Owner, timestamp);
    }

    private GroupChange? BindCompanionPet(string pet, string owner, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(pet) ||
            pet.Equals(owner, StringComparison.OrdinalIgnoreCase) ||
            pet.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
            KnownMembers.Contains(pet))
        {
            return null;
        }

        // A pet re-announces itself throughout its life. Rebinding an unchanged pairing
        // would push its ownership start forward, discard damage still awaiting a
        // hostile confirmation, and reclassify a charmed pet as a companion, which
        // would then survive its own Charm wear-off message.
        if (_controlledPetOwners.TryGetValue(pet, out var currentOwner) &&
            currentOwner.Equals(owner, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var previousPet in _controlledPetOwners
                     .Where(item => item.Value.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                                    _companionPets.Contains(item.Key))
                     .Select(item => item.Key).ToArray())
        {
            RemoveControlledPet(previousPet);
        }

        _controlledPetOwners[pet] = owner;
        _controlledPetSince[pet] = timestamp;
        _petOwnerHistory[pet] = owner;
        _companionPets.Add(pet);
        return new GroupChange(GroupChangeKind.PetControlled, pet, owner);
    }

    private void RetainLocalCharmState()
    {
        _pendingCharmCasts.RemoveAll(item =>
            !item.Caster.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase));
        _pendingCompanionSummons.RemoveAll(item =>
            !item.Owner.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase));
        foreach (var pet in _controlledPetOwners
                     .Where(item => !item.Value.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
                     .Select(item => item.Key).ToArray())
        {
            RemoveControlledPet(pet);
        }
        RemovePetHistoryExcept(owner => owner.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase));
    }

    private void RemovePetHistoryExcept(Func<string, bool> ownerIsRetained)
    {
        foreach (var pet in _petOwnerHistory
                     .Where(item => !ownerIsRetained(item.Value))
                     .Select(item => item.Key).ToArray())
        {
            _petOwnerHistory.Remove(pet);
        }
    }


    private bool IsConfirmedPet(string name, IEnumerable<string>? possibleOwners = null) =>
        _controlledPetOwners.ContainsKey(name) ||
        KnownMembers.Concat(possibleOwners ?? []).Any(member =>
            !member.Equals(name, StringComparison.OrdinalIgnoreCase) && IsOwnedPet(name, member));

    private bool MarkMember(string member, DateTime timestamp)
    {
        var added = KnownMembers.Add(member);
        if (!_memberConfirmedAt.TryGetValue(member, out var current) || timestamp < current)
            _memberConfirmedAt[member] = timestamp;
        return added;
    }

    private void ClearLocalControlledPets()
    {
        foreach (var pet in _controlledPetOwners
                     .Where(pair => pair.Value.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
            RemoveControlledPet(pet);
    }

    private bool RemoveControlledPet(string pet)
    {
        _companionPets.Remove(pet);
        _controlledPetSince.Remove(pet);
        return _controlledPetOwners.Remove(pet);
    }
}
