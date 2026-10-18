using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Hub;

/// <summary>One client socket: a read loop feeding the router and a write loop draining a bounded queue.</summary>
internal sealed class TcpPeer : IPeer
{
    /// <summary>Lines queued for a client before it's considered stuck and dropped (it reconnects and gets a full resync).</summary>
    public const int QueueLimit = 20_000;

    private static int _nextId;
    private readonly TcpClient _tcp;
    private readonly Router _router;
    private readonly HubLog _log;
    private readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts;

    public TcpPeer(TcpClient tcp, Router router, HubLog log, CancellationToken hubStopping)
    {
        _tcp = tcp;
        _router = router;
        _log = log;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(hubStopping);
        Id = Interlocked.Increment(ref _nextId);
        var ep = tcp.Client.RemoteEndPoint as IPEndPoint;
        var addr = ep?.Address is { IsIPv4MappedToIPv6: true } a ? a.MapToIPv4() : ep?.Address;
        IsLoopback = addr is not null && IPAddress.IsLoopback(addr);
        Remote = ep is null ? "?" : $"{addr}:{ep.Port}";
    }

    public int Id { get; }
    public bool IsLoopback { get; }
    public string Remote { get; }

    public void SendLine(string json)
    {
        if (_out.Writer.TryWrite(json)) return;
        if (_cts.IsCancellationRequested) return;
        _log.Warn($"{Remote} is not reading (more than {QueueLimit} messages behind); dropping it");
        Close();
    }

    public void Close()
    {
        _out.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        bool helloed = false;
        try
        {
            _tcp.NoDelay = true;
            var stream = _tcp.GetStream();
            var reader = new LineReader(stream);
            var writeTask = WriteLoopAsync(stream, ct);

            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(TimeSpan.FromSeconds(10));
                var (line, _) = await reader.ReadLineAsync(t.Token).ConfigureAwait(false);
                if (line is null) return;
                if (!Protocol.TryParse(line, out var hello, out var err) || hello is null)
                {
                    ((IPeer)this).Send(new Message { Type = MessageTypes.Error, Text = err });
                    await DrainThenCloseAsync(writeTask).ConfigureAwait(false);
                    return;
                }
                helloed = _router.Hello(this, hello);
                if (!helloed) { await DrainThenCloseAsync(writeTask).ConfigureAwait(false); return; }
            }

            long lastRead = Environment.TickCount64;
            var readTask = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var (line, tooLong) = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) return;
                    Volatile.Write(ref lastRead, Environment.TickCount64);
                    if (tooLong)
                    {
                        ((IPeer)this).Send(new Message { Type = MessageTypes.Error, Text = $"message over {Protocol.MaxLineChars} characters dropped" });
                        continue;
                    }
                    if (line.Length == 0) continue;
                    if (!Protocol.TryParse(line, out var m, out var err) || m is null)
                    {
                        ((IPeer)this).Send(new Message { Type = MessageTypes.Error, Text = err });
                        continue;
                    }
                    _router.Handle(this, m);
                }
            }, ct);

            // Watchdog: a peer silent past DeadAfter (no data, no ping) is gone even if TCP hasn't noticed.
            while (!readTask.IsCompleted && !writeTask.IsCompleted)
            {
                await Task.WhenAny(readTask, writeTask, Task.Delay(1000, ct)).ConfigureAwait(false);
                if (Environment.TickCount64 - Volatile.Read(ref lastRead) > Protocol.DeadAfter.TotalMilliseconds)
                {
                    _log.Info($"{Remote} silent for {Protocol.DeadAfter.TotalSeconds:0}s; dropping it");
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { _log.Error($"{Remote}: {ex}"); }
        finally
        {
            Close();
            if (helloed) _router.Disconnected(this);
            _tcp.Dispose();
        }
    }

    private async Task DrainThenCloseAsync(Task writeTask)
    {
        _out.Writer.TryComplete();
        try { await writeTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* closing anyway */ }
    }

    private async Task WriteLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
        long lastWrite = Environment.TickCount64;
        try
        {
            Task<bool>? ready = null; // one outstanding wait at a time, carried across ping checks
            while (true)
            {
                ready ??= _out.Reader.WaitToReadAsync(ct).AsTask();
                var done = await Task.WhenAny(ready, Task.Delay(1000, ct)).ConfigureAwait(false);
                if (done == ready)
                {
                    bool more = await ready.ConfigureAwait(false);
                    ready = null;
                    if (!more) return; // completed: closing
                    while (_out.Reader.TryRead(out var line)) await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                }
                else if (Environment.TickCount64 - lastWrite >= Protocol.PingInterval.TotalMilliseconds)
                {
                    await writer.WriteLineAsync(Protocol.Serialize(new Message { Type = MessageTypes.Ping }).AsMemory(), ct).ConfigureAwait(false);
                }
                else continue;
                await writer.FlushAsync(ct).ConfigureAwait(false);
                lastWrite = Environment.TickCount64;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
}
