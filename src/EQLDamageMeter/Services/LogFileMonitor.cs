using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EQLDamageMeter.Services;

public sealed record LogMonitorStart(long ResumePosition, bool DiscardInitialPartialLine);

public sealed class LogFileMonitor : IAsyncDisposable
{
    private const int MaxLogLineBytes = 64 * 1024;
    private static readonly UTF8Encoding LogEncoding = new(false, false);
    private readonly string _path;
    private readonly Func<string, CancellationToken, Task> _onLine;
    private readonly Func<bool, Task>? _onHealthChanged;
    private readonly long _initialPosition;
    private readonly bool _discardInitialPartialLine;
    private bool? _lastReportedHealth;
    private CancellationTokenSource? _cancellation;
    private Task? _monitorTask;

    public LogFileMonitor(string path, long initialPosition, Func<string, CancellationToken, Task> onLine,
        Func<bool, Task>? onHealthChanged = null, bool discardInitialPartialLine = false)
    {
        _path = path;
        _initialPosition = initialPosition;
        _onLine = onLine;
        _onHealthChanged = onHealthChanged;
        _discardInitialPartialLine = discardInitialPartialLine;
    }

    public static async Task<LogMonitorStart> CaptureLiveStartAsync(string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);

        var endPosition = stream.Length;
        if (endPosition == 0) return new LogMonitorStart(0, false);

        stream.Position = endPosition - 1;
        var finalByte = new byte[1];
        var read = await stream.ReadAsync(finalByte, cancellationToken);
        return new LogMonitorStart(endPosition, read == 1 && finalByte[0] != (byte)'\n');
    }

    public void Start()
    {
        if (_monitorTask is not null) return;
        _cancellation = new CancellationTokenSource();
        _monitorTask = MonitorAsync(_cancellation.Token);
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var position = _initialPosition;
        var pending = new List<byte>(512);
        var buffer = new byte[32 * 1024];
        var discardOversizedLine = false;
        var discardInitialPartialLine = _discardInitialPartialLine;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (stream.Length < position)
                {
                    position = 0;
                    pending.Clear();
                    discardOversizedLine = false;
                    discardInitialPartialLine = false;
                }

                stream.Position = position;
                var foundData = false;
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    foundData = true;
                    position += count;
                    for (var index = 0; index < count; index++)
                    {
                        var value = buffer[index];
                        if (value == (byte)'\n')
                        {
                            if (discardInitialPartialLine)
                            {
                                pending.Clear();
                                discardInitialPartialLine = false;
                                continue;
                            }
                            if (pending.Count > 0 && pending[^1] == (byte)'\r') pending.RemoveAt(pending.Count - 1);
                            if (!discardOversizedLine && pending.Count > 0)
                            {
                                var line = LogEncoding.GetString(CollectionsMarshal.AsSpan(pending));
                                await _onLine(line, cancellationToken);
                            }
                            pending.Clear();
                            discardOversizedLine = false;
                        }
                        else if (!discardInitialPartialLine && !discardOversizedLine)
                        {
                            pending.Add(value);
                            if (pending.Count > MaxLogLineBytes)
                            {
                                pending.Clear();
                                discardOversizedLine = true;
                            }
                        }
                    }
                }

                await ReportHealthAsync(true);
                await Task.Delay(foundData ? 40 : 150, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (FileNotFoundException)
            {
                await ReportHealthAsync(false);
                await Task.Delay(500, cancellationToken);
            }
            catch (IOException)
            {
                await ReportHealthAsync(false);
                await Task.Delay(250, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                await ReportHealthAsync(false);
                await Task.Delay(500, cancellationToken);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                await ReportHealthAsync(false);
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    private async Task ReportHealthAsync(bool isHealthy)
    {
        if (_lastReportedHealth == isHealthy) return;
        _lastReportedHealth = isHealthy;
        if (_onHealthChanged is not null) await _onHealthChanged(isHealthy);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is null) return;
        await _cancellation.CancelAsync();
        if (_monitorTask is not null)
        {
            try { await _monitorTask; }
            catch (OperationCanceledException) { }
        }
        _cancellation.Dispose();
        _cancellation = null;
        _monitorTask = null;
    }
}
