using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class EncounterHistoryViewModel
{
    public required EncounterSnapshot Snapshot { get; init; }
    public required string Mode { get; init; }
    public required string CharacterName { get; init; }
    public DateTime StartedAt => Snapshot.StartedAt;
    public double Seconds => Math.Max(1, (Snapshot.EndedAt - Snapshot.StartedAt).TotalSeconds);
    public long Damage => Snapshot.Combatants.FirstOrDefault(item =>
        item.Name.Equals(CharacterName, StringComparison.OrdinalIgnoreCase))?.Damage ?? 0;
    public double Dps => Damage / Seconds;
    public string TargetNames => Snapshot.Targets.Count == 0 ? "Unknown target" : string.Join(", ", Snapshot.Targets.Take(2)) +
        (Snapshot.Targets.Count > 2 ? $" +{Snapshot.Targets.Count - 2}" : string.Empty);
}
