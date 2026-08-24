using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

/// <summary>
/// Groups melee swing attempts into attack rounds for DBL/TRI/QUAD rates.
/// Extra annotated swings (riposte, flurry, rampage, frenzy) stay out of the round.
/// Misses count as attempts. Same-second hits of one skill on several targets collapse
/// to one firing (depth is the busiest target, so dual-wield still reads as a double).
/// </summary>
public sealed class MeleeAbilityResolver
{
    public readonly record struct RoundTally(
        int Rounds,
        int Doubles,
        int Triples,
        int Quads,
        bool HighDepthIsApproximate);

    private sealed class Lane
    {
        public string Ability = "";
        public int Tokens;
    }

    private sealed class SourceState
    {
        public long OpenSecond = long.MinValue;
        public readonly Dictionary<string, Lane> Pending = new(StringComparer.OrdinalIgnoreCase);
        public int Rounds;
        public int Doubles;
        public int Triples;
        public int Quads;
        public bool HighDepthIsApproximate;
    }

    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.OrdinalIgnoreCase);

    public void ClearRuntime() => _sources.Clear();

    public void ObserveLanded(DamageEvent damage)
    {
        if (damage.Category != DamageCategory.Melee) return;
        if (!damage.CountsAsAttackRound) return;
        Observe(damage.Timestamp, damage.Source, damage.Target, damage.Ability);
    }

    public void ObserveAttempt(DateTime timestamp, string source, string? target, string ability, bool extraSwing)
    {
        if (extraSwing || target is null) return;
        if (IsMultiHitSkill(ability)) return;
        Observe(timestamp, source, target, ability);
    }

    public RoundTally GetTally(string source)
    {
        if (!_sources.TryGetValue(source, out var state))
            return default;
        var rounds = state.Rounds;
        var doubles = state.Doubles;
        var triples = state.Triples;
        var quads = state.Quads;
        var approx = state.HighDepthIsApproximate;
        if (state.Pending.Count > 0)
            ApplyCollapsed(state.Pending.Values, ref rounds, ref doubles, ref triples, ref quads, ref approx);
        return new RoundTally(rounds, doubles, triples, quads, approx);
    }

    public void OverlayOnto(CombatantAggregate combatant)
    {
        var tally = GetTally(combatant.Name);
        combatant.MeleeSwingAttempts = tally.Rounds;
        combatant.DoubleAttacks = tally.Doubles;
        combatant.TripleAttacks = tally.Triples;
        combatant.QuadAttacks = tally.Quads;
        combatant.HighMultiAttackIsApproximate = tally.HighDepthIsApproximate;
    }

    public static bool IsTimedSkill(string ability) =>
        ability.Equals("Kick", StringComparison.OrdinalIgnoreCase) ||
        ability.Equals("Bash", StringComparison.OrdinalIgnoreCase) ||
        ability.Equals("Backstab", StringComparison.OrdinalIgnoreCase) ||
        ability.Equals("Strike", StringComparison.OrdinalIgnoreCase) ||
        ability.Equals("Smite", StringComparison.OrdinalIgnoreCase) ||
        ability.Equals("Cleave", StringComparison.OrdinalIgnoreCase) ||
        SpecialAttackNames.IsNamedSpecial(ability);

    private void Observe(DateTime timestamp, string source, string target, string ability)
    {
        if (IsMultiHitSkill(ability)) return;
        var second = timestamp.Ticks / TimeSpan.TicksPerSecond;
        if (!_sources.TryGetValue(source, out var state))
        {
            state = new SourceState();
            _sources[source] = state;
        }

        if (state.OpenSecond != second)
        {
            Commit(state);
            state.OpenSecond = second;
        }

        var key = ability + "\n" + target;
        if (!state.Pending.TryGetValue(key, out var lane))
        {
            lane = new Lane { Ability = ability };
            state.Pending[key] = lane;
        }

        lane.Tokens++;
    }

    private static void Commit(SourceState state)
    {
        if (state.Pending.Count == 0) return;
        ApplyCollapsed(state.Pending.Values, ref state.Rounds, ref state.Doubles, ref state.Triples,
            ref state.Quads, ref state.HighDepthIsApproximate);
        state.Pending.Clear();
    }

    private static void ApplyCollapsed(IEnumerable<Lane> lanes, ref int roundCount,
        ref int doubles, ref int triples, ref int quads, ref bool approximate)
    {
        // One skill in one second is one firing. Several targets share that firing;
        // depth is whatever the busiest defender saw (dual wield / DA still stack).
        Dictionary<string, int>? depthByAbility = null;
        foreach (var lane in lanes)
        {
            depthByAbility ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!depthByAbility.TryGetValue(lane.Ability, out var depth) || lane.Tokens > depth)
                depthByAbility[lane.Ability] = lane.Tokens;
        }

        if (depthByAbility is null) return;
        foreach (var pair in depthByAbility)
        {
            roundCount++;
            switch (pair.Value)
            {
                case >= 4:
                    quads++;
                    if (!IsTimedSkill(pair.Key)) approximate = true;
                    break;
                case 3:
                    triples++;
                    if (!IsTimedSkill(pair.Key)) approximate = true;
                    break;
                case 2:
                    doubles++;
                    break;
            }
        }
    }

    private static bool IsMultiHitSkill(string ability) =>
        ability.Equals("Frenzy", StringComparison.OrdinalIgnoreCase) ||
        ability.Equals("Flurry", StringComparison.OrdinalIgnoreCase);
}
