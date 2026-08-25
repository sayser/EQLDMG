using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EQLDamageMeter.Models;

namespace EQLDamageMeter.Services;

public static class SkyLogScanner
{
    private static readonly UTF8Encoding LogEncoding = new(false, false);

    public static SkyLootLedger Scan(string path, IReadOnlyList<SkyClassCatalog> classes, long maxPosition,
        CancellationToken cancellationToken = default)
    {
        var ledger = new SkyLootLedger();
        ledger.LoadCatalog(classes);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || maxPosition <= 0)
            return ledger;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        var toRead = Math.Min(maxPosition, stream.Length);
        var parser = new LogLineParser("You");
        var pending = new List<byte>(256);
        var buffer = new byte[64 * 1024];
        long consumed = 0;
        while (consumed < toRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, toRead - consumed));
            if (n <= 0) break;
            consumed += n;
            for (var index = 0; index < n; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (pending.Count > 0 && pending[^1] == (byte)'\r') pending.RemoveAt(pending.Count - 1);
                    if (pending.Count > 0)
                    {
                        var line = LogEncoding.GetString(CollectionsMarshal.AsSpan(pending));
                        ObserveLine(ledger, parser, line);
                    }

                    pending.Clear();
                }
                else
                {
                    pending.Add(value);
                }
            }
        }

        if (pending.Count > 0 && consumed >= stream.Length)
        {
            if (pending[^1] == (byte)'\r') pending.RemoveAt(pending.Count - 1);
            if (pending.Count > 0)
            {
                var line = LogEncoding.GetString(CollectionsMarshal.AsSpan(pending));
                ObserveLine(ledger, parser, line);
            }
        }

        return ledger;
    }

    private static void ObserveLine(SkyLootLedger ledger, LogLineParser parser, string line)
    {
        if (!SkyLogEvents.IsCandidate(line)) return;
        if (!parser.TryParseEnvelope(line, out _, out var message)) return;
        ledger.Observe(message);
    }
}
