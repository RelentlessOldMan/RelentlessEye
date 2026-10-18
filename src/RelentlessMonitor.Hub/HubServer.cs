using System.Net;
using System.Net.Sockets;

namespace RelentlessMonitor.Hub;

/// <summary>Listens for clients and runs the router's housekeeping until stopped.</summary>
internal sealed class HubServer
{
    private readonly HubConfig _config;
    private readonly HubLog _log;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public HubServer(HubConfig config, HubLog log)
    {
        _config = config;
        _log = log;
        Router = new Router(config, log, () => _cts.Cancel());
    }

    public Router Router { get; }

    /// <summary>The port actually bound (useful with port 0 in tests).</summary>
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _config.Port;

    /// <summary>Binds the socket. Throws <see cref="SocketException"/> if the port is taken (another hub?).</summary>
    public void Bind()
    {
        var bind = (_config.Bind ?? "localhost").Trim();
        if (bind.Equals("localhost", StringComparison.OrdinalIgnoreCase) || bind.Length == 0)
            _listener = new TcpListener(IPAddress.Loopback, _config.Port);
        else if (bind.Equals("all", StringComparison.OrdinalIgnoreCase) || bind is "*" or "0.0.0.0")
        {
            _listener = new TcpListener(IPAddress.IPv6Any, _config.Port);
            _listener.Server.DualMode = true;
        }
        else
            _listener = new TcpListener(IPAddress.Parse(bind), _config.Port);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(backlog: 64);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_listener is null) Bind();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var stop = linked.Token;
        _log.Info($"listening on {_listener!.LocalEndpoint} (bind '{_config.Bind}'{(_config.Bind is "localhost" or "" ? ", this machine only" : ", token required off-machine")})");

        var sweep = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(1000, stop).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                try { Router.Sweep(DateTime.UtcNow); } catch (Exception ex) { _log.Error($"sweep: {ex}"); }
            }
        }, stop);

        var peers = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient tcp;
                try { tcp = await _listener.AcceptTcpClientAsync(stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException ex) { _log.Warn($"accept: {ex.Message}"); continue; }
                var peer = new TcpPeer(tcp, Router, _log, stop);
                lock (peers)
                {
                    peers.RemoveAll(t => t.IsCompleted);
                    peers.Add(Task.Run(peer.RunAsync));
                }
            }
        }
        finally
        {
            _listener.Stop();
            Task[] running;
            lock (peers) running = [.. peers];
            try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { /* exiting */ }
            try { await sweep.ConfigureAwait(false); } catch { /* exiting */ }
        }
    }

    public void Stop() => _cts.Cancel();
}
