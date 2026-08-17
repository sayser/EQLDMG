using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

/// <summary>
/// Collapses same-named mez applications into one overlay row with a stack count
/// (Rat X3). Tracking still keeps one timer per land; only the overlay display is grouped.
/// </summary>
public static class MezOverlayGrouping
{
    public static IReadOnlyList<(BuffInstanceSnapshot Snapshot, int StackCount)> Collapse(
        IEnumerable<BuffInstanceSnapshot> snapshots,
        Func<Guid, ControlEffectType> controlTypeOf)
    {
        var mez = new List<BuffInstanceSnapshot>();
        var rest = new List<BuffInstanceSnapshot>();
        foreach (var snapshot in snapshots)
        {
            if (controlTypeOf(snapshot.RuleId) == ControlEffectType.Mez)
                mez.Add(snapshot);
            else
                rest.Add(snapshot);
        }

        var collapsed = mez
            .GroupBy(item => (item.RuleId, Name: item.TargetName.Trim().ToUpperInvariant()))
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(item => item.ExpiresAt)
                    .ThenBy(item => item.InstanceKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var head = ordered[0] with { InstanceKey = $"mezstack|{group.Key.Name}" };
                return (Snapshot: head, StackCount: ordered.Length);
            });

        return rest.Select(item => (Snapshot: item, StackCount: 1))
            .Concat(collapsed)
            .OrderByDescending(item => item.Snapshot.IsSelf)
            .ThenBy(item => item.Snapshot.TargetName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Snapshot.ExpiresAt)
            .ThenBy(item => item.Snapshot.SpellName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
