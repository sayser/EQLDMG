using System.Diagnostics;
using System.IO;
using System.Text;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public sealed class SkyInventoryPiles
{
    public int Inventory { get; set; }
    public int Bank { get; set; }
    public int Hoard { get; set; }
    public int Other { get; set; }
    public int Total => Inventory + Bank + Hoard + Other;

    public void Add(SkyItemLocation location, int count)
    {
        if (count <= 0) return;
        switch (location)
        {
            case SkyItemLocation.Bank:
                Bank += count;
                break;
            case SkyItemLocation.Hoard:
                Hoard += count;
                break;
            case SkyItemLocation.Other:
                Other += count;
                break;
            default:
                Inventory += count;
                break;
        }
    }
}

public readonly record struct SkyInventoryDumpParse(
    Dictionary<string, SkyInventoryPiles> Piles,
    bool IsComplete,
    bool HasHoardSection);

public readonly record struct SkyInventoryDumpFile(
    Dictionary<string, SkyInventoryPiles> Piles,
    DateTime WrittenAt,
    bool IsComplete,
    bool HasHoardSection);

public static class SkyInventoryDump
{
    public static string FileNameFor(LogIdentity identity) =>
        $"{identity.Character}_{identity.Server}-Inventory.txt";

    public static bool TryFindPath(string? logPath, out string? dumpPath, out string expectedFileName,
        out string searchFolder)
    {
        dumpPath = null;
        expectedFileName = "Character_server-Inventory.txt";
        searchFolder = string.Empty;
        if (!LogIdentity.TryFromPath(logPath ?? string.Empty, out var identity) || identity is null)
            return false;

        expectedFileName = FileNameFor(identity);
        var logDir = Path.GetDirectoryName(Path.GetFullPath(logPath!));
        if (string.IsNullOrEmpty(logDir))
            return false;

        var gameRoot = Directory.GetParent(logDir)?.FullName;
        searchFolder = gameRoot ?? logDir;
        foreach (var dir in new[] { gameRoot, logDir })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, expectedFileName);
            if (!File.Exists(candidate)) continue;
            dumpPath = candidate;
            searchFolder = dir;
            return true;
        }

        return false;
    }

    public static SkyInventoryDumpFile Load(string path, CancellationToken cancellationToken = default)
    {
        const int retryDelayMs = 150;
        const int maxWaitMs = 8000;
        var elapsed = Stopwatch.StartNew();
        var parsed = Parse(ReadAllTextShared(path));
        while (!parsed.IsComplete && elapsed.ElapsedMilliseconds < maxWaitMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.WaitHandle.WaitOne(retryDelayMs);
            cancellationToken.ThrowIfCancellationRequested();
            parsed = Parse(ReadAllTextShared(path));
        }

        return new(parsed.Piles, File.GetLastWriteTime(path), parsed.IsComplete, parsed.HasHoardSection);
    }

    public static bool TryLoadFromLog(string? logPath, out SkyInventoryDumpFile dump,
        CancellationToken cancellationToken = default)
    {
        dump = default;
        if (!TryFindPath(logPath, out var dumpPath, out _, out _) || dumpPath is null)
            return false;
        try
        {
            dump = Load(dumpPath, cancellationToken);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static SkyInventoryDumpParse Parse(string text)
    {
        var result = new Dictionary<string, SkyInventoryPiles>(StringComparer.OrdinalIgnoreCase);
        var isComplete = false;
        var hasHoardSection = false;
        if (string.IsNullOrWhiteSpace(text))
            return new(result, isComplete, hasHoardSection);

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (IsKeyRingSectionStart(line))
            {
                isComplete = true;
                break;
            }

            var parts = line.Split('\t');
            if (parts.Length < 4) continue;

            var locationText = parts[0].Trim();
            if (locationText.StartsWith("Hoard", StringComparison.OrdinalIgnoreCase))
                hasHoardSection = true;

            var name = parts[1].Trim();
            if (locationText.Equals("Location", StringComparison.OrdinalIgnoreCase) &&
                name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Length == 0 || name.Equals("Empty", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains("(Exaltation)", StringComparison.OrdinalIgnoreCase))
                continue;
            if (parts[2].Trim() == "0") continue;
            if (!int.TryParse(parts[3].Trim(), out var count) || count <= 0) continue;
            if (!TryClassifyLocation(locationText, out var location)) continue;

            var key = SkyItemName.Normalize(name);
            if (key.Length == 0) continue;
            if (!result.TryGetValue(key, out var piles))
            {
                piles = new SkyInventoryPiles();
                result[key] = piles;
            }

            piles.Add(location, count);
        }

        return new(result, isComplete, hasHoardSection);
    }

    public static bool TryClassifyLocation(string location, out SkyItemLocation classified)
    {
        classified = SkyItemLocation.Unknown;
        if (string.IsNullOrWhiteSpace(location)) return false;
        var loc = location.Trim();
        if (loc.StartsWith("KeyRing", StringComparison.OrdinalIgnoreCase))
            return false;
        if (loc.StartsWith("Hoard", StringComparison.OrdinalIgnoreCase))
        {
            classified = SkyItemLocation.Hoard;
            return true;
        }

        if (loc.StartsWith("SharedBank", StringComparison.OrdinalIgnoreCase) ||
            loc.StartsWith("Bank", StringComparison.OrdinalIgnoreCase))
        {
            classified = SkyItemLocation.Bank;
            return true;
        }

        if (loc.StartsWith("Personal-Depot", StringComparison.OrdinalIgnoreCase))
        {
            classified = SkyItemLocation.Other;
            return true;
        }

        classified = SkyItemLocation.Inventory;
        return true;
    }

    private static bool IsKeyRingSectionStart(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.StartsWith("KeyRing\t", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("KeyRing ", StringComparison.OrdinalIgnoreCase);
    }
}
