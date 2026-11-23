using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace FloatingDock.Ipc;

/// <summary>A failure the handler wants the client to see as <c>{"error":{"code","message"}}</c>.</summary>
public sealed class IpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record IpcRequest(IpcSession Session, string Method, JsonObject Params);

public delegate Task<JsonNode?> IpcHandler(IpcRequest request);

/// <summary>One connected UI process.</summary>
public sealed class IpcSession
{
    private readonly Channel<string> _outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private HashSet<string> _topics = [];

    internal IpcSession(long id) => Id = id;

    public long Id { get; }
    /// <summary>Name the client gave in <c>hello</c>; null until then.</summary>
    public string? ClientName { get; internal set; }
    /// <summary>Process id the client reported in <c>hello</c> (0 if it did not).</summary>
    public int ClientPid { get; internal set; }
    internal ChannelReader<string> Outgoing => _outbox.Reader;
    internal bool Wants(string topic) { var t = Volatile.Read(ref _topics); return t.Contains("*") || t.Contains(topic); }
    internal void SetTopics(IEnumerable<string> topics) => Volatile.Write(ref _topics, new HashSet<string>(topics, StringComparer.Ordinal));
    internal bool TryEnqueue(string line) => _outbox.Writer.TryWrite(line);
    internal void Complete() => _outbox.Writer.TryComplete();
}

/// <summary>
/// Accepts UI processes on a loopback TCP port (see <see cref="IpcEndpoint"/>), answers their requests and pushes events to those subscribed to the topic.
/// Frames are newline-delimited JSON; see <see cref="Protocol"/>.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private readonly string _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    private string _endpoint = "";
    private readonly string _engineVersion;
    private readonly Dictionary<string, IpcHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, IpcSession> _sessions = new();
    private readonly CancellationTokenSource _stop = new();
    private Socket? _listener;
    private long _nextSession;
    private bool _started;

    public IpcServer(string engineVersion)
    {
        _engineVersion = engineVersion;
        Register(Protocol.Methods.Ping, _ => Task.FromResult<JsonNode?>(new JsonObject { ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
    }

    /// <summary>What clients connect to (known once <see cref="Start"/> ran); the engine publishes it with <see cref="IpcEndpoint.Publish"/>.</summary>
    public string Endpoint => _endpoint;

    public int SessionCount => _sessions.Count;
    public event Action<IpcSession>? SessionOpened;
    public event Action<IpcSession>? SessionClosed;

    /// <summary>Raised when a client has said hello (its name, protocol and pid are known).</summary>
    public event Action<IpcSession>? SessionHello;

    public void Register(string method, IpcHandler handler)
    {
        if (_started) throw new InvalidOperationException("Register handlers before Start().");
        _handlers[method] = handler;
    }

    /// <summary>For handlers that answer at once: <paramref name="handler"/> returns the result (or null) and throws <see cref="IpcException"/> to refuse.</summary>
    public void RegisterSync(string method, Func<IpcRequest, JsonNode?> handler) =>
        Register(method, request => Task.FromResult(handler(request)));

    public void Start()
    {
        _started = true;
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        _listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        _listener.Listen(16);
        _endpoint = IpcEndpoint.Format(((System.Net.IPEndPoint)_listener.LocalEndPoint!).Port, _token);
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Sends <paramref name="eventName"/> to every session subscribed to its topic.</summary>
    public void Publish(string eventName, JsonNode? data)
    {
        if (_sessions.IsEmpty) return;
        var topic = Protocol.TopicOf(eventName);
        string? line = null;
        foreach (var s in _sessions.Values)
        {
            if (!s.Wants(topic)) continue;
            line ??= Protocol.EventLine(eventName, data);
            if (!s.TryEnqueue(line)) s.Complete();      // a client that stopped reading is dropped, never waited for
        }
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener!.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (ct.IsCancellationRequested) return; continue; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        socket.NoDelay = true;
        var session = new IpcSession(Interlocked.Increment(ref _nextSession));
        _sessions[session.Id] = session;
        SessionOpened?.Invoke(session);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        // A connection that never says hello (a stray local process) must not hold a session open for ever.
        _ = Task.Delay(HelloTimeout, cts.Token).ContinueWith(t => { if (!t.IsCanceled && session.ClientName is null) cts.Cancel(); }, TaskScheduler.Default);
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in session.Outgoing.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await stream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception) { /* the reader notices the broken connection */ }
            finally { cts.Cancel(); }
        });
        try
        {
            var reader = new FrameReader(stream, Protocol.MaxFrameBytes);
            while (await reader.ReadAsync(cts.Token).ConfigureAwait(false) is { } frame)
                _ = Task.Run(() => HandleAsync(session, frame));
        }
        catch (Exception) { /* closed, cancelled, or a frame that was too large: drop the client */ }
        finally
        {
            session.Complete();
            cts.Cancel();
            await writer.ConfigureAwait(false);
            _sessions.TryRemove(session.Id, out _);
            SessionClosed?.Invoke(session);
        }
    }

    private async Task HandleAsync(IpcSession session, string frame)
    {
        JsonNode? id = null;
        try
        {
            if (JsonNode.Parse(frame) is not JsonObject msg) throw new IpcException(Protocol.Errors.BadRequest, "A request must be a JSON object.");
            id = msg["id"];
            if (id is null) throw new IpcException(Protocol.Errors.BadRequest, "A request needs an id.");
            var method = msg["method"]?.GetValue<string>() ?? throw new IpcException(Protocol.Errors.BadRequest, "A request needs a method.");
            var p = msg["params"] as JsonObject ?? new JsonObject();
            var result = await DispatchAsync(session, method, p).ConfigureAwait(false);
            session.TryEnqueue(Protocol.ResponseLine(id, result));
        }
        catch (IpcException ex) { session.TryEnqueue(Protocol.ErrorLine(id, ex.Code, ex.Message)); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        { session.TryEnqueue(Protocol.ErrorLine(id, Protocol.Errors.BadRequest, ex.Message)); }
        catch (Exception ex) { session.TryEnqueue(Protocol.ErrorLine(id, Protocol.Errors.Failed, ex.Message)); }
    }

    private Task<JsonNode?> DispatchAsync(IpcSession session, string method, JsonObject p)
    {
        if (method == Protocol.Methods.Hello) return Task.FromResult(Hello(session, p));
        if (session.ClientName is null && method != Protocol.Methods.Ping) throw new IpcException(Protocol.Errors.HelloRequired, "Send hello first.");
        if (method == Protocol.Methods.Subscribe) return Task.FromResult(Subscribe(session, p));
        if (!_handlers.TryGetValue(method, out var handler)) throw new IpcException(Protocol.Errors.UnknownMethod, "Unknown method: " + method);
        return handler(new IpcRequest(session, method, p));
    }

    private JsonNode? Hello(IpcSession session, JsonObject p)
    {
        int version = p["protocol"]?.GetValue<int>() ?? 0;
        if (version != Protocol.Version)
            throw new IpcException(Protocol.Errors.ProtocolMismatch, $"Engine speaks protocol {Protocol.Version}, client asked for {version}.");
        // Any local process can reach a loopback port: only a client that read the endpoint file knows the token.
        if (!string.Equals(p["token"]?.GetValue<string>(), _token, StringComparison.Ordinal))
            throw new IpcException(Protocol.Errors.Unauthorized, "Wrong or missing token.");
        session.ClientName = p["client"]?.GetValue<string>() ?? "unknown";
        session.ClientPid = p["pid"]?.GetValue<int>() ?? 0;
        SessionHello?.Invoke(session);
        return new JsonObject
        {
            ["protocol"] = Protocol.Version,
            ["engine"] = _engineVersion,
            ["pid"] = Environment.ProcessId,
        };
    }

    private static JsonNode? Subscribe(IpcSession session, JsonObject p)
    {
        var topics = (p["topics"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? "").Where(t => t.Length > 0).ToArray()
            ?? throw new IpcException(Protocol.Errors.BadParams, "topics must be an array of strings.");
        session.SetTopics(topics);
        return new JsonObject { ["topics"] = new JsonArray(topics.Select(t => (JsonNode?)t).ToArray()) };
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Dispose(); } catch (ObjectDisposedException) { }
        foreach (var s in _sessions.Values) s.Complete();
        _stop.Dispose();
    }
}
