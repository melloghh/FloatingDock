using System.Text.Json.Nodes;
using FloatingDock.Ipc;
using Xunit;

namespace FloatingDock.Ipc.Tests;

public sealed class IpcTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ipc-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IpcServer _server;
    private string Socket => IpcEndpoint.SocketPathFor(_dir);

    public IpcTests()
    {
        Directory.CreateDirectory(_dir);
        _server = new IpcServer(Socket, "9.9.9");
        _server.Register("echo", r => Task.FromResult<JsonNode?>(new JsonObject { ["got"] = r.Params["text"]?.DeepClone() }));
        _server.Register("fail", _ => throw new IpcException("custom", "nope"));
        _server.Register("crash", _ => throw new InvalidDataException("boom"));
        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Hello_returns_engine_info_and_echo_round_trips_unicode()
    {
        await using var c = await IpcClient.ConnectAsync(Socket, "test");
        Assert.Equal("9.9.9", c.HelloResult!["engine"]!.GetValue<string>());
        var reply = await c.RequestAsync("echo", new JsonObject { ["text"] = "Привет, мир \"кавычки\"\nстрока 2" });
        Assert.Equal("Привет, мир \"кавычки\"\nстрока 2", reply!["got"]!.GetValue<string>());
    }

    [Fact]
    public async Task Requests_before_hello_are_refused_but_ping_is_allowed()
    {
        using var raw = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        await raw.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(Socket));
        await using var stream = new System.Net.Sockets.NetworkStream(raw);
        var reader = new StreamReader(stream);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"echo\",\"params\":{}}\n{\"id\":2,\"method\":\"ping\"}\n"));
        var lines = new[] { JsonNode.Parse((await reader.ReadLineAsync())!)!, JsonNode.Parse((await reader.ReadLineAsync())!)! };
        var refused = lines.Single(l => l["id"]!.GetValue<int>() == 1);
        var pong = lines.Single(l => l["id"]!.GetValue<int>() == 2);
        Assert.Equal(Protocol.Errors.HelloRequired, refused["error"]!["code"]!.GetValue<string>());
        Assert.NotNull(pong["result"]!["time"]);
    }

    [Fact]
    public async Task Wrong_protocol_version_is_refused()
    {
        using var raw = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        await raw.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(Socket));
        await using var stream = new System.Net.Sockets.NetworkStream(raw);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"hello\",\"params\":{\"client\":\"x\",\"protocol\":99}}\n"));
        var line = await new StreamReader(stream).ReadLineAsync();
        Assert.Equal(Protocol.Errors.ProtocolMismatch, JsonNode.Parse(line!)!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Handler_errors_become_error_responses()
    {
        await using var c = await IpcClient.ConnectAsync(Socket, "test");
        var custom = await Assert.ThrowsAsync<IpcRemoteException>(() => c.RequestAsync("fail"));
        Assert.Equal("custom", custom.Code);
        var unknown = await Assert.ThrowsAsync<IpcRemoteException>(() => c.RequestAsync("no.such.method"));
        Assert.Equal(Protocol.Errors.UnknownMethod, unknown.Code);
        var crashed = await Assert.ThrowsAsync<IpcRemoteException>(() => c.RequestAsync("crash"));
        Assert.Equal(Protocol.Errors.Failed, crashed.Code);
        Assert.NotNull(await c.RequestAsync("ping"));                  // the connection survives all three
    }

    [Fact]
    public async Task Events_reach_only_subscribed_topics()
    {
        await using var a = await IpcClient.ConnectAsync(Socket, "a");
        await using var b = await IpcClient.ConnectAsync(Socket, "b");
        var got = new List<string>();
        a.EventReceived += (name, _) => { lock (got) got.Add("a:" + name); };
        b.EventReceived += (name, _) => { lock (got) got.Add("b:" + name); };
        await a.RequestAsync(Protocol.Methods.Subscribe, new JsonObject { ["topics"] = new JsonArray("settings") });
        await b.RequestAsync(Protocol.Methods.Subscribe, new JsonObject { ["topics"] = new JsonArray("windows") });

        _server.Publish(Protocol.Events.SettingsChanged, new JsonObject { ["x"] = 1 });
        _server.Publish(Protocol.Events.WindowsChanged, new JsonObject { ["y"] = 2 });
        await Task.Delay(300);

        lock (got) Assert.Equal(new[] { "a:settings.changed", "b:windows.changed" }, got.OrderBy(s => s).ToArray());
    }

    [Fact]
    public async Task Many_concurrent_requests_are_matched_to_their_replies()
    {
        await using var c = await IpcClient.ConnectAsync(Socket, "test");
        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            var r = await c.RequestAsync("echo", new JsonObject { ["text"] = "m" + i });
            Assert.Equal("m" + i, r!["got"]!.GetValue<string>());
        });
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task A_frame_over_the_limit_drops_the_client_only()
    {
        using var raw = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        await raw.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(Socket));
        var huge = new byte[Protocol.MaxFrameBytes + 1024];
        Array.Fill(huge, (byte)'a');
        try { await raw.SendAsync(huge); } catch (System.Net.Sockets.SocketException) { }
        await Task.Delay(300);
        await using var ok = await IpcClient.ConnectAsync(Socket, "healthy");
        Assert.NotNull(await ok.RequestAsync("ping"));
    }
}
