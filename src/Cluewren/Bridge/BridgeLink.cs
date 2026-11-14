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
    const int MaxMessages = 32;
    const long MaxQueuedBytes = 16 * 1024 * 1024;
    const long MaxReplayBytes = 32 * 1024 * 1024;

    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> _pending = new();
    readonly Channel<(long Id, byte[] Frame)> _outgoing =
        Channel.CreateUnbounded<(long, byte[])>(new UnboundedChannelOptions { SingleReader = true });
    // Frames and other messages nobody takes (a recording the harness child started under `cluewren run`) must not pile up.
    readonly Channel<JsonDocument> _messages = Channel.CreateBounded<JsonDocument>(
        new BoundedChannelOptions(MaxMessages) { FullMode = BoundedChannelFullMode.DropOldest }, static dropped => dropped.Dispose());
    readonly Queue<(long Id, byte[] Frame)> _sent = new();
    readonly Queue<(long Id, byte[] Frame)> _replay = new();
    (long Id, byte[] Frame)? _unsent;
    // The id, the room check and the queue go together: ids leave in queue order, which Rewind relies on. What a harness
    // child has the link send is taken only while less than MaxQueuedBytes waits; the brain's own frames (releasing input,
    // pausing, ending a run) always are. With the replay window that bounds what the link holds for a child.
    readonly Lock _queue = new();
    long _nextId;
    long _queuedBytes;
    long _sentBytes;
    long _droppedThrough;
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

    public VerifiedFolder? Project { get; internal init; }

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

    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    public Task<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default) => RequestAsync(op, args, timeout, false, ct);

    public Task<GameAnswer> RequestWithinAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default) => RequestAsync(op, args, timeout, true, ct);

    async Task<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args, TimeSpan? timeout, bool within, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Enqueue(op, args, answer, within) is not { } id)
            throw new GameDisconnectedException(_closed ?? Stalled);
        var returned = false;
        try
        {
            var document = await answer.Task.WaitAsync(timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
            var frame = document.RootElement;
            if (frame.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                returned = true;
                return new GameAnswer(document);
            }
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
            // A reply can land between the wait giving up and the removal above: nobody reads it then.
            if (!returned)
                _ = answer.Task.ContinueWith(static reply => reply.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    const string Stalled = "the game isn't reading what it is sent";

    long? Enqueue(string op, Action<Utf8JsonWriter>? args, TaskCompletionSource<JsonDocument>? answer, bool within)
    {
        // Written before the lock: the caller's writer may itself ask the link for something.
        var written = EncodeArgs(args);
        lock (_queue)
        {
            if (_closed is not null)
                return null;
            var id = _nextId + 1;
            var frame = EncodeRequest(id, op, written);
            if (within && _queuedBytes + frame.Length > MaxQueuedBytes)
                return null;
            if (answer is not null)
                _pending[id] = answer;
            if (!_outgoing.Writer.TryWrite((id, frame)))
            {
                _pending.TryRemove(id, out _);
                return null;
            }
            _nextId = id;
            Interlocked.Add(ref _queuedBytes, frame.Length);
            return id;
        }
    }

    ValueTask<GameAnswer> IGameLink.RequestAsync(string op, Action<Utf8JsonWriter>? args, TimeSpan? timeout,
        CancellationToken ct) => new(RequestAsync(op, args, timeout, ct));

    // A harness child's statuses come this way too, so a status is dropped while the plugin isn't reading.
    public void Status(string text) => Send(json =>
    {
        json.WriteString("type", "status");
        json.WriteString("text", text);
    }, true);

    public void Send(Action<Utf8JsonWriter> frame) => Send(frame, false);

    void Send(Action<Utf8JsonWriter> frame, bool within)
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
        var bytes = buffer.WrittenSpan.ToArray();
        lock (_queue)
        {
            if ((!within || _queuedBytes + bytes.Length <= MaxQueuedBytes) && _outgoing.Writer.TryWrite((0, bytes)))
                Interlocked.Add(ref _queuedBytes, bytes.Length);
        }
    }

    public void Post(string op, Action<Utf8JsonWriter>? args = null) => Enqueue(op, args, null, false);

    public bool TryPost(string op, Action<Utf8JsonWriter>? args = null) => Enqueue(op, args, null, true) is not null;

    static byte[] EncodeArgs(Action<Utf8JsonWriter>? args)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            args?.Invoke(json);
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    static byte[] EncodeRequest(long id, string op, byte[] args)
    {
        var buffer = new ArrayBufferWriter<byte>(64 + args.Length);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "request");
            json.WriteNumber("id", id);
            json.WriteString("op", op);
            json.WritePropertyName("args");
            json.WriteRawValue(args, skipInputValidation: true);
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
                var size = _unsent.Value.Frame.Length;
                Interlocked.Add(ref _queuedBytes, -size);
                _sent.Enqueue(_unsent.Value);
                _sentBytes += size;
                while (_sent.Count > MaxReplayFrames || _sentBytes > MaxReplayBytes)
                {
                    var dropped = _sent.Dequeue();
                    _sentBytes -= dropped.Frame.Length;
                    _droppedThrough = Math.Max(_droppedThrough, dropped.Id);
                }
                _unsent = null;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ChannelClosedException)
        {
        }
    }

    // A send can succeed after the plugin's 1012 close left it, while the socket still reads Open, so only the plugin
    // knows what arrived. Runs between pumps: no send loop is alive.
    // False when a request the plugin didn't get is no longer kept: going on would leave a gap nobody sees.
    internal bool Rewind(long? received)
    {
        if (_droppedThrough > (received ?? 0))
            return false;
        // What was already waiting for replay comes after these: it was queued later.
        var later = _replay.ToArray();
        _replay.Clear();
        foreach (var frame in _sent)
        {
            if (frame.Id > (received ?? long.MaxValue))
            {
                _replay.Enqueue(frame);
                Interlocked.Add(ref _queuedBytes, frame.Frame.Length);
            }
        }
        _sent.Clear();
        _sentBytes = 0;
        // The send that was cut off can have arrived all the same: a request the plugin says it has is not sent twice.
        if (_unsent is { } unsent)
        {
            if (unsent.Id > 0 && unsent.Id <= received)
                Interlocked.Add(ref _queuedBytes, -unsent.Frame.Length);
            else
                _replay.Enqueue(unsent);
        }
        _unsent = null;
        foreach (var frame in later)
            _replay.Enqueue(frame);
        return true;
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
