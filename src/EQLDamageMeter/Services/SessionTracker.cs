using System.Globalization;
using System.Text.RegularExpressions;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

/// <summary>
/// Accumulates per-app-session leveling, AA, and mote loot stats from live log lines.
/// </summary>
public sealed partial class SessionTracker
{
    private SessionRecord? _current;

    public SessionRecord? Current => _current;

    public void StartSession(string character, string server, DateTime startedAt)
    {
        SessionLootParser.ResetRuntime();
        _current = new SessionRecord
        {
            Id = startedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            StartedAt = startedAt,
            EndedAt = null,
            Character = character,
            Server = server,
            Loot = new SessionLootData()
        };
    }

    public SessionRecord? EndSession(DateTime endedAt)
    {
        if (_current is null) return null;
        _current.EndedAt = endedAt;
        var finished = _current;
        _current = null;
        return finished;
    }

    public void UpdateIdentity(string character, string server)
    {
        if (_current is null) return;
        _current.Character = character;
        _current.Server = server;
    }

    public bool Observe(DateTime timestamp, string message)
    {
        if (_current is null || string.IsNullOrWhiteSpace(message)) return false;

        var changed = false;
        if (TryReadExperiencePercent(message, out var xpPercent))
        {
            _current.LevelXpPercent += xpPercent;
            changed = true;
        }

        if (TryReadLevelUp(message, out var level))
        {
            _current.LevelsGained++;
            _current.StartLevel ??= level - 1;
            _current.EndLevel = level;
            changed = true;
        }

        if (TryReadAbilityPoints(message, out var aaPoints))
        {
            _current.AaPointsGained += aaPoints;
            changed = true;
        }

        if (TryReadMoteLoot(message, out var moteName))
        {
            _current.MotesLooted++;
            _current.MotesByName.TryGetValue(moteName, out var count);
            _current.MotesByName[moteName] = count + 1;
            changed = true;
        }

        if (IsLocalPlayerDeath(message))
        {
            _current.Deaths++;
            changed = true;
        }

        if (SessionLootParser.TryObserve(_current, timestamp, message))
            changed = true;

        return changed;
    }

    public SessionRecord? CreateSnapshot()
    {
        if (_current is null) return null;
        return Clone(_current);
    }

    public static SessionRecord Clone(SessionRecord source) => new()
    {
        Id = source.Id,
        StartedAt = source.StartedAt,
        EndedAt = source.EndedAt,
        Character = source.Character,
        Server = source.Server,
        LevelXpPercent = source.LevelXpPercent,
        LevelsGained = source.LevelsGained,
        StartLevel = source.StartLevel,
        EndLevel = source.EndLevel,
        AaPointsGained = source.AaPointsGained,
        MotesLooted = source.MotesLooted,
        Deaths = source.Deaths,
        MotesByName = new Dictionary<string, int>(source.MotesByName, StringComparer.OrdinalIgnoreCase),
        Loot = SessionLootParser.Clone(source.Loot)
    };

    private static bool TryReadExperiencePercent(string message, out double percent)
    {
        percent = 0;
        var match = ExperiencePercentRegex().Match(message);
        if (!match.Success) return false;
        return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
    }

    private static bool TryReadLevelUp(string message, out int level)
    {
        level = 0;
        var match = LevelUpRegex().Match(message);
        if (!match.Success) return false;
        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
    }

    private static bool TryReadAbilityPoints(string message, out int points)
    {
        points = 0;
        var multi = AbilityPointsRegex().Match(message);
        if (multi.Success)
            return int.TryParse(multi.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out points);

        if (AbilityPointSingularRegex().IsMatch(message))
        {
            points = 1;
            return true;
        }

        return false;
    }

    private static bool TryReadMoteLoot(string message, out string moteName)
    {
        moteName = string.Empty;
        var match = MoteLootRegex().Match(message);
        if (match.Success)
        {
            moteName = match.Groups[1].Value.Trim();
            return moteName.Length > 0;
        }

        // Auto-store / sold / plain kept lines (no -- brackets).
        if (SessionLootParser.TryReadLootedItemName(message, out var itemName) &&
            MoteNameRegex().IsMatch(itemName))
        {
            moteName = itemName;
            return true;
        }

        return false;
    }

    private static bool IsLocalPlayerDeath(string message) => LocalDeathRegex().IsMatch(message);

    [GeneratedRegex(@"^You gain(?:ed)? (?:party |raid )?experience(?: \(with a (?:bonus|penalty)\))?!\s*\((\d+(?:\.\d+)?)%\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExperiencePercentRegex();

    [GeneratedRegex(@"^You have gained a level! Welcome to level (\d+)!", RegexOptions.CultureInvariant)]
    private static partial Regex LevelUpRegex();

    [GeneratedRegex(@"^You have gained (\d+) ability point\(s\)!", RegexOptions.CultureInvariant)]
    private static partial Regex AbilityPointsRegex();

    [GeneratedRegex(@"^You have gained an ability point!", RegexOptions.CultureInvariant)]
    private static partial Regex AbilityPointSingularRegex();

    [GeneratedRegex(@"^--You have looted a (Mote of .+? Potential) from .+--$", RegexOptions.CultureInvariant)]
    private static partial Regex MoteLootRegex();

    [GeneratedRegex(@"^Mote of .+ Potential$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MoteNameRegex();

    [GeneratedRegex(@"^(?:You died\.|You have been slain by .+?!)$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalDeathRegex();
}
