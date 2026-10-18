using System.Net.Sockets;
using RelentlessMonitor.Hub;

// RelentlessMonitor.Hub [--port N] [--bind localhost|all|IP] [--config PATH] [--console]
//   Background process that owns the channel. Apps on this or other machines connect over TCP,
//   describe their panel and push values; monitors connect to display them. Settings come from
//   %LOCALAPPDATA%\RelentlessMonitor\hub.json (created on first run); flags override them.

int? port = null;
string? bind = null, configPath = null;
bool console = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p): port = p; i++; break;
        case "--bind" when i + 1 < args.Length: bind = args[++i]; break;
        case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
        case "--console": console = true; break;
        default:
            if (console) NativeConsole.AttachConsole(-1);
            Console.Error.WriteLine($"unknown argument '{args[i]}'. Usage: RelentlessMonitor.Hub [--port N] [--bind localhost|all|IP] [--config PATH] [--console]");
            return 2;
    }
}

if (console) NativeConsole.AttachConsole(-1);

HubLog? log = null;
// A WinExe that dies of an unhandled exception brings up a Windows error dialog: never let one out.
try
{
    var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelentlessMonitor", "logs", "hub.log");
    log = new HubLog(logPath, console);

    var config = HubConfig.LoadOrCreate(configPath ?? HubConfig.DefaultPath, log);
    if (port is not null) config.Port = port.Value;
    if (bind is not null) config.Bind = bind;
    if (string.IsNullOrWhiteSpace(config.Bind)) config.Bind = "localhost";
    if (config.Port is < 1 or > 65535)
    {
        log.Error($"port {config.Port} is not between 1 and 65535");
        return 2;
    }

    var server = new HubServer(config, log);
    try { server.Bind(); }
    catch (SocketException ex)
    {
        log.Info($"cannot listen on port {config.Port} ({ex.SocketErrorCode}); another hub is probably running. Exiting.");
        return 1;
    }
    catch (FormatException)
    {
        log.Error($"bind '{config.Bind}' is not localhost, all, or an IP address");
        return 2;
    }

    // Not disposed: ProcessExit can fire after Main returns.
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    log.Info($"hub {Router.Version} starting (pid {Environment.ProcessId})");
    await server.RunAsync(cts.Token);
    log.Info("hub stopped");
    return 0;
}
catch (Exception ex)
{
    try { log?.Error($"hub failed: {ex}"); } catch { /* nowhere left to report it */ }
    if (console) Console.Error.WriteLine($"hub failed: {ex.Message}");
    return 3;
}

internal static class NativeConsole
{
    /// <summary>The hub is a WinExe; with --console, borrow the parent's console so log lines show up there.</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    internal static extern bool AttachConsole(int processId);
}
