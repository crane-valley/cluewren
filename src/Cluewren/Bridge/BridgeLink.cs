using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Cluewren.Kit;

namespace Cluewren.Bridge;

public sealed class BridgeLink : IGameLink
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    const int MaxFrameBytes = 16 * 1024 * 1024;
    const int MaxReplayFrames = 1024;

    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> _pending = new();
    readonly Channel<(long Id, byte[] Frame)> _outgoing =
        Channel.CreateUnbounded<(long, byte[])>(new UnboundedChannelOptions { SingleReader = true });
    readonly Channel<JsonDocument> _messages = Channel.CreateUnbounded<JsonDocument>();
    readonly Queue<(long Id, byte[] Frame)> _sent = new();
    readonly Queue<(long Id, byte[] Frame)> _replay = new();
    (long Id, byte[] Frame)? _unsent;
    long _nextId;
    long _gameTime = BitConverter.DoubleToInt64Bits(double.NaN);
    volatile string? _closed;
    JsonElement _hello;
    string[] _features = [];
    string _engine = "unity";

    public BridgeLink(string session, JsonElement hello)
    {
        Session = session;
        Hello = hello;
    }

    public string Session { get; }

    public string Project { get; internal init; } = "";

    public JsonElement Hello
    {
        get => _hello;
        internal set
        {
            _hello = value;
            if (value.ValueKind != JsonValueKind.Object)
                return;
            if (value.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
                _features = features.EnumerateArray().Select(feature => feature.GetString() ?? "").ToArray();
            if (value.TryGetProperty("engine", out var engine) && engine.GetString() is { Length: > 0 } name)
                _engine = name.ToLowerInvariant();
        }
    }

    public string? Closed => _closed;

    public IReadOnlyCollection<string> Features => _features;

    public string Engine => _engine;

    public double? GameTime
    {
        get
        {
            var t = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _gameTime));
            return double.IsNaN(t) ? null : t;
        }
    }

    public event Action<JsonElement>? Event;

    public ChannelReader<JsonDocument> Messages => _messages.Reader;

    internal int Generation;

    internal volatile bool Suspended;

    public async Task<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        if (_closed is { } why)
        {
            _pending.TryRemove(id, out _);
            throw new GameDisconnectedException(why);
        }
        _outgoing.Writer.TryWrite((id, EncodeRequest(id, op, args)));
        try
        {
            var document = await answer.Task.WaitAsync(timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
            var frame = document.RootElement;
            if (frame.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return new GameAnswer(document);
            var status = frame.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : 400;
            var error = frame.TryGetProperty("error", out var e) ? e.GetString() : null;
            document.Dispose();
            throw new GameException(status, error ?? "error");
        }
        catch (TimeoutException)
        {
            throw new GameDisconnectedException($"the game didn't answer {op} in time");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    ValueTask<GameAnswer> IGameLink.RequestAsync(string op, Action<Utf8JsonWriter>? args, TimeSpan? timeout,
        CancellationToken ct) => new(RequestAsync(op, args, timeout, ct));

    public void Status(string text) => Send(json =>
    {
        json.WriteString("type", "status");
        json.WriteString("text", text);
    });

    public void Send(Action<Utf8JsonWriter> frame)
    {
        if (_closed is not null)
            return;
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            frame(json);
            json.WriteEndObject();
        }
        _outgoing.Writer.TryWrite((0, buffer.WrittenSpan.ToArray()));
    }

    public void Post(string op, Action<Utf8JsonWriter>? args = null)
    {
        if (_closed is null)
        {
            var id = Interlocked.Increment(ref _nextId);
            _outgoing.Writer.TryWrite((id, EncodeRequest(id, op, args)));
        }
    }

    static byte[] EncodeRequest(long id, string op, Action<Utf8JsonWriter>? args)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "request");
            json.WriteNumber("id", id);
            json.WriteString("op", op);
            json.WriteStartObject("args");
            args?.Invoke(json);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    internal async Task<WebSocketCloseStatus?> PumpAsync(WebSocket socket, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender = SendLoopAsync(socket, stop.Token);
        try
        {
            while (true)
            {
                var frame = await ReceiveAsync(socket, ct).ConfigureAwait(false);
                if (frame is null)
                    return socket.CloseStatus;
                Feed(frame);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException)
        {
            return null;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await sender.ConfigureAwait(false);
        }
    }

    async Task SendLoopAsync(WebSocket socket, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // Held until sent: a Unity script reload can drop the socket mid-send, and the frame then goes out on
                // the resumed connection.
                _unsent ??= _replay.TryDequeue(out var replayed)
                    ? replayed
                    : await _outgoing.Reader.ReadAsync(ct).ConfigureAwait(false);
                await socket.SendAsync(_unsent.Value.Frame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                _sent.Enqueue(_unsent.Value);
                if (_sent.Count > MaxReplayFrames)
                    _sent.Dequeue();
                _unsent = null;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ChannelClosedException)
        {
        }
    }

    // A send can succeed after the plugin's 1012 close left it, while the socket still reads Open, so only the plugin
    // knows what arrived. Runs between pumps: no send loop is alive.
    internal void Rewind(long received)
    {
        foreach (var frame in _sent)
        {
            if (frame.Id > received)
                _replay.Enqueue(frame);
        }
        _sent.Clear();
        if (_unsent is { } unsent)
            _replay.Enqueue(unsent);
        _unsent = null;
    }

    void Feed(JsonDocument frame)
    {
        var root = frame.RootElement;
        var kind = root.TryGetProperty("type", out var type) ? type.GetString() : null;
        if (root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number)
            Interlocked.Exchange(ref _gameTime, BitConverter.DoubleToInt64Bits(t.GetDouble()));
        if (kind == "response" && root.TryGetProperty("id", out var id) && id.TryGetInt64(out var rid)
            && _pending.TryRemove(rid, out var waiting))
        {
            if (!waiting.TrySetResult(frame))
                frame.Dispose();
            return;
        }
        if (kind == "events")
        {
            if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                foreach (var one in events.EnumerateArray())
                    Event?.Invoke(one);
            frame.Dispose();
        }
        else if (kind == "response")
            frame.Dispose();
        else if (!_messages.Writer.TryWrite(frame))
            frame.Dispose();
    }

    internal void Close(string why)
    {
        _closed ??= why;
        _outgoing.Writer.TryComplete();
        _messages.Writer.TryComplete();
        foreach (var pending in _pending.Values)
            pending.TrySetException(new GameDisconnectedException(why));
    }

    internal static async Task<JsonDocument?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        ArrayBufferWriter<byte>? large = null;
        try
        {
            var used = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk.AsMemory(used), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                used += result.Count;
                if (result.EndOfMessage)
                {
                    // From a stream: Parse(ReadOnlyMemory) keeps that memory, and chunk goes back to the pool below.
                    if (large is null)
                        return JsonDocument.Parse(new MemoryStream(chunk, 0, used));
                    large.Write(chunk.AsSpan(0, used));
                    return JsonDocument.Parse(large.WrittenMemory);
                }
                if (used == chunk.Length)
                {
                    large ??= new ArrayBufferWriter<byte>(chunk.Length * 2);
                    large.Write(chunk);
                    used = 0;
                    if (large.WrittenCount > MaxFrameBytes)
                        throw new WebSocketException(WebSocketError.Faulted, "frame too large");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
