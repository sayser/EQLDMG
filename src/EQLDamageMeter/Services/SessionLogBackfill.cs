using System.IO;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

/// <summary>
/// Builds a finished session by replaying the trailing lookback window of a character log
/// (relative to the last timestamp in the file, not wall-clock now).
/// </summary>
public static class SessionLogBackfill
{
    public static string BackfillId(string character, string server) =>
        $"backfill:{character}:{server}";

    public static bool IsBackfillId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.StartsWith("backfill:", StringComparison.OrdinalIgnoreCase);

    public static SessionRecord? TryBuild(string path, string character, string server, TimeSpan lookback)
    {
        if (!File.Exists(path) || lookback <= TimeSpan.Zero) return null;

        var parser = new LogLineParser(character);
        if (!TryFindLastTimestamp(path, parser, out var lastTimestamp))
            return null;

        var cutoff = lastTimestamp - lookback;

        // Hold the shared loot-runtime lock for the whole replay so live Observe
        // cannot interleave, then clear runtime state before returning to live.
        return SessionLootParser.WithExclusiveRuntime(() =>
        {
            var tracker = new SessionTracker();
            DateTime? startedAt = null;
            var endAt = lastTimestamp;

            foreach (var line in File.ReadLines(path))
            {
                if (!parser.TryParseEnvelope(line, out var timestamp, out var message)) continue;
                if (timestamp < cutoff) continue;

                startedAt ??= timestamp;
                if (tracker.Current is null)
                    tracker.StartSession(character, server, startedAt.Value);

                tracker.Observe(timestamp, message);
                endAt = timestamp;
            }

            var finished = tracker.EndSession(endAt);
            SessionLootParser.ResetRuntime();
            if (finished is null) return null;

            finished.Id = BackfillId(character, server);
            finished.StartedAt = startedAt ?? cutoff;
            finished.EndedAt = endAt;
            return finished;
        });
    }

    private static bool TryFindLastTimestamp(string path, LogLineParser parser, out DateTime lastTimestamp)
    {
        lastTimestamp = default;
        // Read from the end so large logs stay responsive.
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length == 0) return false;

            const int chunkSize = 256 * 1024;
            var buffer = new byte[chunkSize];
            var hold = string.Empty;
            var cursor = stream.Length;

            while (cursor > 0)
            {
                var start = Math.Max(0, cursor - chunkSize);
                var length = checked((int)(cursor - start));
                stream.Position = start;
                var read = stream.Read(buffer, 0, length);
                if (read <= 0) break;

                var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read) + hold;
                var lines = text.Split('\n');
                // First fragment may be partial when not at file start.
                var firstIndex = start == 0 ? 0 : 1;
                for (var i = lines.Length - 1; i >= firstIndex; i--)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (!parser.TryParseEnvelope(line, out var timestamp, out _)) continue;
                    lastTimestamp = timestamp;
                    return true;
                }

                hold = start == 0 ? string.Empty : lines[0];
                cursor = start;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // Fallback full scan if reverse search missed a stamp.
        foreach (var line in File.ReadLines(path))
        {
            if (!parser.TryParseEnvelope(line, out var timestamp, out _)) continue;
            lastTimestamp = timestamp;
        }

        return lastTimestamp != default;
    }
}
