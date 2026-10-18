using RelentlessEye.Hub;

namespace RelentlessEye.Tests;

/// <summary>A real hub on an ephemeral localhost port, for end-to-end tests.</summary>
internal sealed class TestHub : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _run;

    public TestHub(string token = "test-token")
    {
        Config = new HubConfig { Port = 0, Bind = "localhost", Token = token };
        Server = new HubServer(Config, new HubLog(null, false));
        Server.Bind();
        _run = Task.Run(() => Server.RunAsync(_cts.Token));
    }

    public HubConfig Config { get; }
    public HubServer Server { get; }
    public string Address => $"127.0.0.1:{Server.Port}";

    public MonitorClientOptions AppOptions(string? instance = null) => new()
    {
        Hub = Address, Instance = instance, Machine = "TESTPC",
        ConnectTimeout = TimeSpan.FromMilliseconds(300), RetryInterval = TimeSpan.FromMilliseconds(200),
    };

    public HubOptions ViewerOptions() => new()
    {
        Hub = Address, ConnectTimeout = TimeSpan.FromMilliseconds(300), RetryInterval = TimeSpan.FromMilliseconds(200),
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* test teardown */ }
    }
}

internal static class Wait
{
    /// <summary>Polls until <paramref name="cond"/> holds; fails the test after <paramref name="seconds"/>.</summary>
    public static async Task Until(Func<bool> cond, string what, double seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!cond())
        {
            if (DateTime.UtcNow > until) Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }
}
