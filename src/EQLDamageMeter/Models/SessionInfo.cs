namespace EQLDamageMeter.Models;

public sealed class SessionInfoDocument
{
    public List<SessionRecord> Sessions { get; set; } = [];
}

public sealed class SessionRecord
{
    public string Id { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Character { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public double LevelXpPercent { get; set; }
    public int LevelsGained { get; set; }
    public int? StartLevel { get; set; }
    public int? EndLevel { get; set; }
    public int AaPointsGained { get; set; }
    public int MotesLooted { get; set; }
    public Dictionary<string, int> MotesByName { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
