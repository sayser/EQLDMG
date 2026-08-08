using EQLDamageMeter.Services;

namespace EQLDamageMeter.ViewModels;

public sealed class EncounterHistoryViewModel : ObservableObject
{
    private string _targetNames = "Current encounter";
    private long _damage;
    private double _dps;
    private string _mode = "SOLO";
    private DateTime _startedAt;
    private string _timeLabel = "LIVE";

    public bool IsLive { get; private init; }
    public EncounterSnapshot? Snapshot { get; private init; }
    public string CharacterName { get; private init; } = string.Empty;
    public DateTime StartedAt
    {
        get => _startedAt;
        private set => SetProperty(ref _startedAt, value);
    }

    public string TargetNames
    {
        get => _targetNames;
        private set => SetProperty(ref _targetNames, value);
    }

    public long Damage
    {
        get => _damage;
        private set => SetProperty(ref _damage, value);
    }

    public double Dps
    {
        get => _dps;
        private set => SetProperty(ref _dps, value);
    }

    public string Mode
    {
        get => _mode;
        private set => SetProperty(ref _mode, value);
    }

    public string TimeLabel
    {
        get => _timeLabel;
        private set => SetProperty(ref _timeLabel, value);
    }

    public double Seconds => Snapshot is null
        ? 0
        : Math.Max(1, (Snapshot.EndedAt - Snapshot.StartedAt).TotalSeconds);

    public static EncounterHistoryViewModel CreateLive(string characterName) => new()
    {
        IsLive = true,
        CharacterName = characterName,
        TargetNames = "Current encounter",
        TimeLabel = "LIVE"
    };

    public static EncounterHistoryViewModel CreateArchived(EncounterSnapshot snapshot, string mode, string characterName)
    {
        var damage = snapshot.Combatants.FirstOrDefault(item =>
            item.Name.Equals(characterName, StringComparison.OrdinalIgnoreCase))?.Damage ?? 0;
        var seconds = Math.Max(1, (snapshot.EndedAt - snapshot.StartedAt).TotalSeconds);
        return new EncounterHistoryViewModel
        {
            IsLive = false,
            Snapshot = snapshot,
            CharacterName = characterName,
            StartedAt = snapshot.StartedAt,
            Mode = mode,
            Damage = damage,
            Dps = damage / seconds,
            TargetNames = FormatTargets(snapshot.Targets),
            TimeLabel = snapshot.StartedAt.ToString("t")
        };
    }

    public void UpdateLive(string mode, long damage, double dps, DateTime? startedAt)
    {
        if (!IsLive) return;
        Mode = mode;
        Damage = damage;
        Dps = dps;
        StartedAt = startedAt ?? default;
        TimeLabel = "LIVE";
        TargetNames = "Current encounter";
    }

    private static string FormatTargets(IReadOnlyList<string> targets) =>
        targets.Count == 0
            ? "Unknown target"
            : string.Join(", ", targets.Take(2)) + (targets.Count > 2 ? $" +{targets.Count - 2}" : string.Empty);
}
