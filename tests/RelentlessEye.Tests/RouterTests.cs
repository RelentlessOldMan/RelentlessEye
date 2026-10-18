using RelentlessEye.Hub;
using RelentlessEye.Wire;

namespace RelentlessEye.Tests;

public class RouterTests
{
    private sealed class FakePeer(int id, bool loopback) : IPeer
    {
        public int Id { get; } = id;
        public bool IsLoopback { get; } = loopback;
        public string Remote => IsLoopback ? "127.0.0.1:1" : "10.0.0.9:1";
        public List<Message> Sent { get; } = [];
        public void SendLine(string json) { Protocol.TryParse(json, out var m, out _); Sent.Add(m!); }
        public void Close() { }
    }

    private static Router NewRouter(out HubConfig cfg, Action? shutdown = null)
    {
        cfg = new HubConfig { Token = "secret" };
        return new Router(cfg, new HubLog(null, false), shutdown ?? (() => { }));
    }

    private static Message AppHello(string app, string? token = null)
        => new() { Type = MessageTypes.Hello, Role = Roles.App, App = app, Machine = "PC", Token = token };

    [Fact]
    public void Remote_clients_need_the_token_and_local_ones_do_not()
    {
        var r = NewRouter(out _);
        var remote = new FakePeer(1, loopback: false);
        Assert.False(r.Hello(remote, AppHello("A")));
        Assert.Equal(MessageTypes.Error, remote.Sent.Single().Type);
        Assert.Contains("token", remote.Sent.Single().Text);

        Assert.False(r.Hello(new FakePeer(2, false), AppHello("A", "wrong")));
        Assert.True(r.Hello(new FakePeer(3, false), AppHello("A", "secret")));
        Assert.True(r.Hello(new FakePeer(4, true), AppHello("B")));
    }

    [Fact]
    public void Bad_hellos_are_refused()
    {
        var r = NewRouter(out _);
        Assert.False(r.Hello(new FakePeer(1, true), new Message { Type = MessageTypes.Update }));
        Assert.False(r.Hello(new FakePeer(2, true), new Message { Type = MessageTypes.Hello, Role = "boss", App = "x" }));
        Assert.False(r.Hello(new FakePeer(3, true), new Message { Type = MessageTypes.Hello, Role = Roles.App }));
    }

    [Fact]
    public void Shutdown_is_local_only()
    {
        bool stopped = false;
        var r = NewRouter(out _, () => stopped = true);
        var remote = new FakePeer(1, false);
        r.Hello(remote, new Message { Type = MessageTypes.Hello, Role = Roles.Monitor, App = "m", Token = "secret" });
        r.Handle(remote, new Message { Type = MessageTypes.Request, Id = "1", Name = "hub.shutdown" });
        Assert.False(stopped);
        Assert.Contains("only", remote.Sent[^1].Error);

        var local = new FakePeer(2, true);
        r.Hello(local, new Message { Type = MessageTypes.Hello, Role = Roles.Monitor, App = "m" });
        r.Handle(local, new Message { Type = MessageTypes.Request, Id = "2", Name = "hub.shutdown" });
        Assert.True(stopped);
    }

    [Fact]
    public void Requests_time_out_and_fail_when_the_target_leaves()
    {
        var r = NewRouter(out _);
        r.RequestTimeout = TimeSpan.FromSeconds(10);
        var asker = new FakePeer(1, true);
        var target = new FakePeer(2, true);
        r.Hello(asker, AppHello("Asker"));
        r.Hello(target, AppHello("Target"));

        r.Handle(asker, new Message { Type = MessageTypes.Request, Id = "q1", Name = "Target.slow" });
        var forwarded = target.Sent[^1];
        Assert.Equal("slow", forwarded.Name);
        Assert.Equal("PC/Asker", forwarded.Key);

        r.Sweep(DateTime.UtcNow.AddSeconds(11));
        Assert.Equal("q1", asker.Sent[^1].Corr);
        Assert.Contains("no answer", asker.Sent[^1].Error);

        r.Handle(asker, new Message { Type = MessageTypes.Request, Id = "q2", Name = "Target.slow" });
        r.Disconnected(target);
        Assert.Equal("q2", asker.Sent[^1].Corr);
        Assert.Contains("disconnected", asker.Sent[^1].Error);
    }

    [Fact]
    public void A_response_from_the_wrong_peer_is_ignored()
    {
        var r = NewRouter(out _);
        var asker = new FakePeer(1, true);
        var target = new FakePeer(2, true);
        var other = new FakePeer(3, true);
        r.Hello(asker, AppHello("Asker"));
        r.Hello(target, AppHello("Target"));
        r.Hello(other, AppHello("Other"));
        r.Handle(asker, new Message { Type = MessageTypes.Request, Id = "q", Name = "Target.x" });
        var hubId = target.Sent[^1].Id;
        int before = asker.Sent.Count;
        r.Handle(other, new Message { Type = MessageTypes.Response, Corr = hubId, Result = Protocol.ToElement("forged") });
        Assert.Equal(before, asker.Sent.Count);
    }

    [Fact]
    public void Offline_apps_are_forgotten_after_the_configured_time()
    {
        var r = NewRouter(out var cfg);
        cfg.ForgetOfflineAfterHours = 0.0001; // about a third of a second
        var app = new FakePeer(1, true);
        r.Hello(app, AppHello("Brief"));
        r.Disconnected(app);
        Assert.Equal(1, r.AppCount);
        Thread.Sleep(600);
        r.Sweep(DateTime.UtcNow);
        Assert.Equal(0, r.AppCount);
    }

    [Fact]
    public void Monitors_only_messages_are_rejected_from_monitors()
    {
        var r = NewRouter(out _);
        var mon = new FakePeer(1, true);
        r.Hello(mon, new Message { Type = MessageTypes.Hello, Role = Roles.Monitor, App = "m" });
        r.Handle(mon, new Message { Type = MessageTypes.Update, Values = new() { ["x"] = Protocol.ToElement(1) } });
        Assert.Equal(MessageTypes.Error, mon.Sent[^1].Type);
    }
}
