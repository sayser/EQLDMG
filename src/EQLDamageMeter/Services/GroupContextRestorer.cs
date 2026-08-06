using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EQLDamageMeter.Services;

public static class GroupContextRestorer
{
    private const int SearchChunkBytes = 256 * 1024;
    private const int MaxLineBytes = 64 * 1024;

    private static readonly byte[][] BoundaryMarkers =
    [
        Encoding.UTF8.GetBytes("] You have joined the group."),
        Encoding.UTF8.GetBytes("] You have been removed from the group."),
        Encoding.UTF8.GetBytes("] You have left the group."),
        Encoding.UTF8.GetBytes("] You leave the group."),
        Encoding.UTF8.GetBytes("group has been disbanded")
    ];

    public static async Task RestoreAsync(string path, long endPosition, LogLineParser parser,
        GroupStateTracker group, CancellationToken cancellationToken = default)
    {
        if (endPosition <= 0) return;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        endPosition = Math.Min(endPosition, stream.Length);
        var boundary = await FindLatestBoundaryAsync(stream, endPosition, cancellationToken);
        if (boundary < 0)
        {
            // Without a definitive boundary, group chat or healing may be the only
            // evidence of current membership, so a complete replay is required.
            await ReplayRangeAsync(stream, 0, endPosition, parser, group, true, cancellationToken);
            return;
        }

        var boundaryLineStart = await FindLineStartAsync(stream, boundary, cancellationToken);
        // Rebuild message-only state before the boundary so a long-running local Charm
        // survives a later group join. Combat regexes are only needed for the current
        // group era, which keeps restoration responsive on large logs.
        await ReplayRangeAsync(stream, 0, boundaryLineStart, parser, group, false, cancellationToken);
        await ReplayRangeAsync(stream, boundaryLineStart, endPosition, parser, group, true, cancellationToken);
    }

    private static async Task<long> FindLatestBoundaryAsync(FileStream stream, long endPosition,
        CancellationToken cancellationToken)
    {
        var overlap = BoundaryMarkers.Max(marker => marker.Length) - 1;
        var cursor = endPosition;
        var buffer = new byte[SearchChunkBytes];
        while (cursor > 0)
        {
            var start = Math.Max(0, cursor - SearchChunkBytes);
            var length = checked((int)(cursor - start));
            stream.Position = start;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);

            var latest = -1;
            var chunk = buffer.AsSpan(0, length);
            foreach (var marker in BoundaryMarkers)
            {
                latest = Math.Max(latest, LastIndexOf(chunk, marker));
            }
            if (latest >= 0) return start + latest;
            if (start == 0) break;
            cursor = start + overlap;
        }
        return -1;
    }

    private static async Task ReplayRangeAsync(FileStream stream, long start, long end,
        LogLineParser parser, GroupStateTracker group, bool parseCombatEvents, CancellationToken cancellationToken)
    {
        stream.Position = start;
        // Every caller supplies either byte zero or a position resolved by
        // FindLineStartAsync, so the first line is complete and must be replayed.
        var discardPartialLine = false;
        var buffer = new byte[64 * 1024];
        var pending = new List<byte>(512);
        var discardOversizedLine = false;
        var remaining = end - start;

        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0) break;
            remaining -= read;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (discardPartialLine)
                    {
                        discardPartialLine = false;
                        pending.Clear();
                        continue;
                    }
                    if (pending.Count > 0 && pending[^1] == (byte)'\r') pending.RemoveAt(pending.Count - 1);
                    if (!discardOversizedLine && pending.Count > 0)
                    {
                        Process(Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(pending)), parser, group,
                            parseCombatEvents);
                    }
                    pending.Clear();
                    discardOversizedLine = false;
                }
                else if (!discardPartialLine && !discardOversizedLine)
                {
                    pending.Add(value);
                    if (pending.Count > MaxLineBytes)
                    {
                        pending.Clear();
                        discardOversizedLine = true;
                    }
                }
            }
        }
    }

    private static void Process(string line, LogLineParser parser, GroupStateTracker group, bool parseCombatEvents)
    {
        if (!parseCombatEvents)
        {
            if (!parser.TryParseEnvelope(line, out var timestamp, out var message)) return;
            group.Process(message, timestamp);
            // Healing is the fallback for discovering members when join messages are
            // absent. Parse only those lines in the historical portion so overlapping
            // remote Charm casts cannot be mistaken for the local player's result.
            if (message.Contains(" healed ", StringComparison.OrdinalIgnoreCase) &&
                parser.TryParse(line, out var healingLine) && healingLine?.Healing is not null)
            {
                group.ObserveHealing(healingLine.Healing);
            }
            return;
        }

        if (!parser.TryParse(line, out var parsed) || parsed is null) return;
        group.Process(parsed.Message, parsed.Timestamp);
        if (parsed.Healing is not null) group.ObserveHealing(parsed.Healing);
        if (parsed.Damage is not null) group.ObserveDamage(parsed.Damage);
        if (parsed.Outcome is not null) group.ObserveOutcome(parsed.Outcome);
    }

    private static async Task<long> FindLineStartAsync(FileStream stream, long position,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4 * 1024];
        var cursor = position;
        while (cursor > 0)
        {
            var start = Math.Max(0, cursor - buffer.Length);
            var length = checked((int)(cursor - start));
            stream.Position = start;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken);
            var newline = buffer.AsSpan(0, length).LastIndexOf((byte)'\n');
            if (newline >= 0) return start + newline + 1;
            cursor = start;
        }
        return 0;
    }

    private static int LastIndexOf(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        for (var index = source.Length - value.Length; index >= 0; index--)
        {
            if (source.Slice(index, value.Length).SequenceEqual(value)) return index;
        }
        return -1;
    }
}
