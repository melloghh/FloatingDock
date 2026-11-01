using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace FloatingDock.Ipc;

/// <summary>The engine answered a request with an error.</summary>
public sealed class IpcRemoteException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Minimal C# client: used by the engine's own <c>--quit</c> command and by the tests. The Flutter UI has its own Dart client.</summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly NetworkStream _stream;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _readLoop;
    private long _nextId;

    private IpcClient(Socket socket)
    {
        _stream = new NetworkStream(socket, ownsSocket: true);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public event Action<string, JsonNode?>? EventReceived;
    public JsonNode? HelloResult { get; private set; }

    public static async Task<IpcClient> ConnectAsync(string socketPath, string clientName, CancellationToken ct = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false); }
        catch { socket.Dispose(); throw; }
        var client = new IpcClient(socket);
        client.HelloResult = await client.RequestAsync(Protocol.Methods.Hello,
            new JsonObject { ["client"] = clientName, ["protocol"] = Protocol.Version }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        return client;
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? p = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Protocol.RequestLine(id, method, p) + "\n");
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try { await _stream.WriteAsync(bytes, ct).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var reader = new FrameReader(_stream, Protocol.MaxFrameBytes);
            while (await reader.ReadAsync(_cts.Token).ConfigureAwait(false) is { } frame)
            {
                if (JsonNode.Parse(frame) is not JsonObject msg) continue;
                if (msg["event"] is { } name)
                {
                    EventReceived?.Invoke(name.GetValue<string>(), msg["data"]);
                    continue;
                }
                if (msg["id"] is not { } idNode || !_pending.TryGetValue(idNode.GetValue<long>(), out var tcs)) continue;
                if (msg["error"] is JsonObject err)
                    tcs.TrySetException(new IpcRemoteException(err["code"]?.GetValue<string>() ?? "", err["message"]?.GetValue<string>() ?? ""));
                else tcs.TrySetResult(msg["result"]?.DeepClone());
            }
        }
        catch (Exception) { /* connection ended */ }
        finally
        {
            foreach (var tcs in _pending.Values) tcs.TrySetException(new IOException("The engine closed the connection."));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        try { await _readLoop.ConfigureAwait(false); } catch (Exception) { }
        _cts.Dispose();
    }
}
