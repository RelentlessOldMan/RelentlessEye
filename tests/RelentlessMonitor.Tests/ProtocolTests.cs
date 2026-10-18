using System.Text;
using RelentlessMonitor.Wire;

namespace RelentlessMonitor.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData(null, "localhost", 47800)]
    [InlineData("", "localhost", 47800)]
    [InlineData("box", "box", 47800)]
    [InlineData("box:5000", "box", 5000)]
    [InlineData("10.0.0.2:81", "10.0.0.2", 81)]
    [InlineData("[::1]:9000", "::1", 9000)]
    [InlineData("fe80::1", "fe80::1", 47800)]
    public void Hub_addresses_parse(string? address, string host, int port)
    {
        if (address is null && Environment.GetEnvironmentVariable(Protocol.HubEnvVar) is not null) return;
        Assert.Equal((host, port), Protocol.ResolveHub(address));
    }

    [Fact]
    public void Parse_rejects_garbage_and_future_versions()
    {
        Assert.False(Protocol.TryParse("{nope", out _, out var e1));
        Assert.Contains("JSON", e1);
        Assert.False(Protocol.TryParse("""{"v":99,"type":"update"}""", out _, out var e2));
        Assert.Contains("version", e2);
        Assert.False(Protocol.TryParse("""{"v":1}""", out _, out _));
        Assert.True(Protocol.TryParse("""{"type":"UPDATE","extra":1}""", out var m, out _));
        Assert.Equal("update", m!.Type);
    }

    [Fact]
    public void Nulls_are_left_off_the_wire()
    {
        var json = Protocol.Serialize(new Message { Type = MessageTypes.Ping });
        Assert.Equal("""{"v":1,"type":"ping"}""", json);
    }

    [Fact]
    public async Task Line_reader_skips_overlong_lines_and_keeps_going()
    {
        var text = "short\n" + new string('x', 100) + "\nafter\r\nlast";
        var reader = new LineReader(new MemoryStream(Encoding.UTF8.GetBytes(text)), maxChars: 50);
        Assert.Equal(("short", false), await reader.ReadLineAsync(default));
        Assert.Equal(("", true), await reader.ReadLineAsync(default));
        Assert.Equal(("after", false), await reader.ReadLineAsync(default));
        Assert.Equal(("last", false), await reader.ReadLineAsync(default));
        Assert.Equal(((string?)null, false), await reader.ReadLineAsync(default));
    }

    [Fact]
    public void Merge_appends_streams_replaces_values_and_trims_on_relayout()
    {
        var layout = new LayoutBuilder().Section(null, s => s.Text("t").Chart("c", limit: 3).Feed("f")).Build();
        var state = new AppState { Layout = layout };
        StateMerge.Apply(state, new Message
        {
            Values = new() { ["t"] = Protocol.ToElement("a"), ["c"] = Protocol.ToElement(1) },
            Append = new() { ["c"] = [Protocol.ToElement(2), Protocol.ToElement(3), Protocol.ToElement(4)] },
        });
        Assert.Equal("a", state.Values["t"].GetString());
        Assert.Equal([2, 3, 4], state.Streams["c"].Select(e => e.GetInt32()));

        StateMerge.Apply(state, new Message { Streams = new() { ["c"] = [Protocol.ToElement(9)] } });
        Assert.Equal([9], state.Streams["c"].Select(e => e.GetInt32()));

        StateMerge.Relayout(state, new LayoutBuilder().Section(null, s => s.Text("t")).Build());
        Assert.False(state.Streams.ContainsKey("c"));
        Assert.True(state.Values.ContainsKey("t"));
    }
}
