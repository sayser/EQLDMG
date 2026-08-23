using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

/// <summary>
/// Tags multi-attack extra swings (same log ability, same target, same second) as
/// MultiAttackLevel 2/3/4 for DOUBLE/TRIPLE/QUAD ATT % cards. Ability names stay
/// exactly as logged (Kick, Bash, Cleave, Punch, etc.) — no autoskill remapping.
/// </summary>
public sealed class MeleeAbilityResolver
{
    private readonly record struct SwingKey(long Second, string Source, string Target);

    private readonly Dictionary<SwingKey, Dictionary<string, int>> _swingHits = new();

    public void ClearRuntime()
    {
        _swingHits.Clear();
    }

    public DamageEvent ResolveOutgoingDamage(DamageEvent damage, string localPlayerName)
    {
        if (damage.Category != DamageCategory.Melee ||
            !damage.Source.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase))
            return damage;

        var level = NextMultiAttackLevel(damage.Timestamp, damage.Source, damage.Target, damage.Ability);
        return level == damage.MultiAttackLevel
            ? damage
            : damage with { MultiAttackLevel = level };
    }

    private int NextMultiAttackLevel(DateTime timestamp, string source, string target, string ability)
    {
        var second = timestamp.Ticks / TimeSpan.TicksPerSecond;
        PruneOldSwings(second);

        var key = new SwingKey(second, source, target);
        if (!_swingHits.TryGetValue(key, out var counts))
        {
            counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _swingHits[key] = counts;
        }

        counts.TryGetValue(ability, out var hitIndex);
        hitIndex++;
        counts[ability] = hitIndex;
        return hitIndex;
    }

    private void PruneOldSwings(long currentSecond)
    {
        if (_swingHits.Count == 0) return;
        var minSecond = currentSecond - 2;
        foreach (var key in _swingHits.Keys.Where(key => key.Second < minSecond).ToArray())
            _swingHits.Remove(key);
    }
}
