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
    public int Deaths { get; set; }
    public Dictionary<string, int> MotesByName { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public SessionLootData Loot { get; set; } = new();
}

public sealed class SessionLootData
{
    public long CoinCopper { get; set; }
    public string? LastMobName { get; set; }
    public List<SessionMobLoot> Mobs { get; set; } = [];
}

public sealed class SessionMobLoot
{
    public string Name { get; set; } = string.Empty;
    public int CorpsesLooted { get; set; }
    public long CoinCopper { get; set; }
    public List<SessionLootItem> Items { get; set; } = [];
    public List<SessionMobKill> Kills { get; set; } = [];
}

public sealed class SessionMobKill
{
    public DateTime Timestamp { get; set; }
    public long CoinCopper { get; set; }
    public List<SessionLootItem> Items { get; set; } = [];
}

public sealed class SessionLootItem
{
    public string Name { get; set; } = string.Empty;
    public string Disposition { get; set; } = "Kept";
    public int Count { get; set; }
    public long ValueCopper { get; set; }
    public string? Note { get; set; }
}
