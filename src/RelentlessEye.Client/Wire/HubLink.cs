using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using RelentlessEye;

namespace RelentlessEye.Wire;

/// <summary>
/// One reconnecting TCP connection to the hub: connect, hello, wait for welcome, then a write loop
/// and a read loop until it drops; repeat. Sends never block and never throw.
/// </summary>
internal sealed class HubLink
{
    private readonly HubOptions _opt;
    private readonly Func<Message> _hello;
    private readonly Func<IEnumerable<Message>> _afterWelcome;
    private readonly Func<IEnumerable<Message>> _pull;
    private readonly Action<Message> _onMessage;
    private readonly Action<bool, string?> _onConnection;
    private readonly Channel<Message> _queue;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private TaskCompletionSource _up = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private volatile bool _connected;
    private int _queued;
    private volatile bool _writing;
    private long _lastRead;
    private int _signaled; // 1 while a wake is pending, so _wake's count never passes 1

    public HubLink(HubOptions opt, Func<Message> hello, Func<IEnumerable<Message>> afterWelcome, Func<IEnumerable<Message>> pull,
        Action<Message> onMessage, Action<bool, string?> onConnection)
    {
        _opt = opt;
        _hello = hello;
        _afterWelcome = afterWelcome;
        _pull = pull;
        _onMessage = onMessage;
        _onConnection = onConnection;
        _queue = Channel.CreateBounded<Message>(new BoundedChannelOptions(Math.Max(1, opt.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    public void Start() => _loop = Task.Run(RunAsync);

    public bool Connected => _connected;

    /// <summary>Last reason the hub refused us (bad token...), or null.</summary>
    public string? LastRejection { get; private set; }

    /// <summary>Queues a message; dropped (false) while not connected.</summary>
    public bool Send(Message m)
    {
        if (!_connected) return false;
        if (!_queue.Writer.TryWrite(m)) return false;
        Interlocked.Increment(ref _queued);
        Wake();
        return true;
    }

    /// <summary>Asks the write loop to call the pull callback soon.</summary>
    public void Wake()
    {
        if (Interlocked.Exchange(ref _signaled, 1) == 0) _wake.Release();
    }

    public async Task<bool> WaitConnectedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_connected) return true;
        Task up;
        lock (_lock) up = _up.Task;
        try { await up.WaitAsync(timeout, ct).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return _connected; }
    }

    /// <summary>True once nothing is queued and the writer is idle (the pull side tracks its own pending items).</summary>
    public bool Idle => Volatile.Read(ref _queued) <= 0 && !_writing;

    public async Task StopAsync()
    {
        _cts.Cancel();
        Wake();
        try { await _loop.ConfigureAwait(false); } catch { /* stopping */ }
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait = _opt.RetryInterval;
            try { wait = await AttemptAsync(ct).ConfigureAwait(false) ?? wait; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* whatever went wrong, this loop keeps looking for the hub */ }
            if (wait < TimeSpan.FromMilliseconds(100)) wait = TimeSpan.FromMilliseconds(100);
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One connect attempt and, if it works, the whole connection. Returns how long to wait before the next (null: the retry interval).</summary>
    private async Task<TimeSpan?> AttemptAsync(CancellationToken ct)
    {
        var (host, port) = Protocol.ResolveHub(_opt.Hub);
        // The hub listens on 127.0.0.1 by default; skip the ::1 attempt "localhost" would make first.
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";

        using var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attempt.CancelAfter(_opt.ConnectTimeout);
                await tcp.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);
            }
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // not there (or unreachable, or a bad address): try again later

        string? rejected = null;
        try { rejected = await RunConnectedAsync(tcp.GetStream(), ct).ConfigureAwait(false); }
        finally { SetConnected(false, rejected); }
        // A refusal (bad token, bad hello) won't fix itself quickly; anything else, retry soon.
        return rejected is null ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(Math.Max(30, _opt.RetryInterval.TotalSeconds));
    }

    /// <summary>Runs one connection; returns the hub's refusal text if it refused the hello.</summary>
    private async Task<string?> RunConnectedAsync(NetworkStream stream, CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = linked.Token;
        var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
        var reader = new LineReader(stream, Protocol.MaxHubLineChars);
        try
        {
            var hello = _hello();
            hello.Token ??= _opt.Token ?? Environment.GetEnvironmentVariable(Protocol.TokenEnvVar);
            await writer.WriteLineAsync(Protocol.Serialize(hello).AsMemory(), ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);

            Message? first;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(TimeSpan.FromSeconds(5));
                var (line, _) = await reader.ReadLineAsync(t.Token).ConfigureAwait(false);
                if (line is null || !Protocol.TryParse(line, out first, out _) || first is null) return null;
            }
            if (first.Type == MessageTypes.Error) return first.Text ?? "refused by hub";
            if (first.Type != MessageTypes.Welcome) return "unexpected first message from hub";

            // Drop anything queued for an earlier connection: the replay below brings the hub up to date.
            while (_queue.Reader.TryRead(out _)) { }
            Volatile.Write(ref _queued, 0);
            foreach (var m in _afterWelcome()) await WriteAsync(writer, m, ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            LastRejection = null;
            try { _onMessage(first); } catch { /* a handler bug must not drop the connection */ } // welcome first, so Key is set by the time ConnectionChanged fires
            SetConnected(true, null);

            Volatile.Write(ref _lastRead, Environment.TickCount64);
            long lastWrite = Environment.TickCount64;
            var readTask = Task.Run(() => ReadLoopAsync(reader, ct), ct);
            while (!ct.IsCancellationRequested && !readTask.IsCompleted)
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                Volatile.Write(ref _signaled, 0); // before pulling, so a change made during the pull wakes us again
                long now = Environment.TickCount64;
                if (now - Volatile.Read(ref _lastRead) > Protocol.DeadAfter.TotalMilliseconds) break; // hub went silent: assume it's gone
                _writing = true;
                int fromQueue = 0;
                bool any = false;
                foreach (var m in _pull()) { await WriteAsync(writer, m, ct).ConfigureAwait(false); any = true; }
                while (_queue.Reader.TryRead(out var m))
                {
                    await WriteAsync(writer, m, ct).ConfigureAwait(false);
                    fromQueue++;
                    any = true;
                }
                if (!any && now - lastWrite > Protocol.PingInterval.TotalMilliseconds)
                {
                    await writer.WriteLineAsync(Protocol.Serialize(new Message { Type = MessageTypes.Ping }).AsMemory(), ct).ConfigureAwait(false);
                    any = true;
                }
                if (any) { await writer.FlushAsync(ct).ConfigureAwait(false); lastWrite = now; }
                Interlocked.Add(ref _queued, -fromQueue);
                _writing = false;
            }
            linked.Cancel();
            try { await readTask.ConfigureAwait(false); } catch { /* gone */ }
            return null;
        }
        catch (Exception)
        {
            // Dropped, or a callback failed: either way this connection is done and the loop reconnects.
            return null;
        }
        finally
        {
            linked.Cancel();
            _writing = false;
        }
    }

    private static async Task WriteAsync(StreamWriter writer, Message m, CancellationToken ct)
    {
        foreach (var line in Protocol.SerializeLines(m)) await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(LineReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (line, tooLong) = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) return;
                Volatile.Write(ref _lastRead, Environment.TickCount64);
                if (tooLong || line.Length == 0) continue;
                if (Protocol.TryParse(line, out var m, out _) && m is not null && m.Type != MessageTypes.Ping)
                    try { _onMessage(m); } catch { /* a handler bug must not drop the connection */ }
            }
        }
        catch (Exception) { /* dropped; the write loop notices and reconnects */ }
    }

    private void SetConnected(bool value, string? rejection)
    {
        if (rejection is not null) LastRejection = rejection;
        if (_connected == value && rejection is null) return;
        _connected = value;
        lock (_lock)
        {
            if (value) _up.TrySetResult();
            else if (_up.Task.IsCompleted) _up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { _onConnection(value, rejection); } catch { /* subscriber bug */ }
    }
}
