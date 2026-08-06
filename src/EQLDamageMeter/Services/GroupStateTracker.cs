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

    private sealed record PendingCharmCast(DateTime Timestamp, string Caster);

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
    private static readonly Regex LocalSpellFailed = new(
        @"^Your (?<ability>.+?) spell (?:is interrupted\.|fizzles!)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OtherSpellFailed = new(
        @"^(?<caster>.+?)(?:'|`|\u2019)s (?<ability>.+?) spell (?:is interrupted\.|fizzles!)$",
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

    private string? _pendingInviter;
    private readonly Dictionary<string, PendingHealCast> _pendingHealCasts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PendingCharmCast> _pendingCharmCasts = [];
    private readonly Dictionary<string, string> _controlledPetOwners =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _memberConfirmedAt =
        new(StringComparer.OrdinalIgnoreCase) { [localPlayerName] = DateTime.MinValue };
    private readonly Dictionary<string, DateTime> _controlledPetSince =
        new(StringComparer.OrdinalIgnoreCase);
    public bool IsGrouped { get; private set; }
    public HashSet<string> KnownMembers { get; } = new(StringComparer.OrdinalIgnoreCase) { localPlayerName };

    public bool IsConfirmedMemberOrPet(string name) =>
        KnownMembers.Contains(name) || _controlledPetOwners.ContainsKey(name) ||
        KnownMembers.Any(member => IsOwnedPet(name, member));

    public bool WasConfirmedMemberOrPetAt(string name, DateTime timestamp)
    {
        if (_controlledPetOwners.ContainsKey(name))
            return !_controlledPetSince.TryGetValue(name, out var since) || since <= timestamp;
        if (KnownMembers.Contains(name))
            return !_memberConfirmedAt.TryGetValue(name, out var since) || since <= timestamp;
        var owner = KnownMembers.FirstOrDefault(member => IsOwnedPet(name, member));
        return owner is not null &&
               (!_memberConfirmedAt.TryGetValue(owner, out var ownerSince) || ownerSince <= timestamp);
    }

    public bool TryGetControlledPetOwner(string pet, out string? owner) =>
        _controlledPetOwners.TryGetValue(pet, out owner);

    public bool TryGetPetOwner(string pet, out string? owner)
    {
        if (_controlledPetOwners.TryGetValue(pet, out owner)) return true;
        owner = KnownMembers.FirstOrDefault(member => IsOwnedPet(pet, member));
        return owner is not null;
    }

    public static bool IsOwnedPet(string candidate, string owner) =>
        candidate.StartsWith($"{owner}'s ", StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith($"{owner}`s ", StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith($"{owner}\u2019s ", StringComparison.OrdinalIgnoreCase);

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
        if (timestamp.HasValue)
        {
            _pendingCharmCasts.RemoveAll(item =>
                timestamp.Value - item.Timestamp > TimeSpan.FromSeconds(12));

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
                if (isEligibleCaster && CharmAbility.IsMatch(ability))
                {
                    _pendingCharmCasts.Add(new PendingCharmCast(timestamp.Value, caster));
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
                _controlledPetOwners[pet] = cast.Caster;
                _controlledPetSince[pet] = timestamp.Value;
                _pendingCharmCasts.RemoveAt(0);
                return new GroupChange(GroupChangeKind.PetControlled, pet, cast.Caster);
            }

            var wornOff = LocalSpellWornOff.Match(message);
            if (wornOff.Success && CharmAbility.IsMatch(wornOff.Groups["ability"].Value))
            {
                var pet = wornOff.Groups["name"].Value;
                if (_controlledPetOwners.TryGetValue(pet, out var owner) &&
                    owner.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveControlledPet(pet);
                }
            }

            // Generic slain messages carry only an NPC name. Do not release by name:
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

    private void RetainLocalCharmState()
    {
        _pendingCharmCasts.RemoveAll(item =>
            !item.Caster.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase));
        foreach (var pet in _controlledPetOwners
                     .Where(item => !item.Value.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
                     .Select(item => item.Key).ToArray())
        {
            RemoveControlledPet(pet);
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

    private bool RemoveControlledPet(string pet)
    {
        _controlledPetSince.Remove(pet);
        return _controlledPetOwners.Remove(pet);
    }
}
