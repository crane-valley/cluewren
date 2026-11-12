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
    const long MaxReplayBytes = 8 << 20;

    sealed class Outgoing(long id, byte[] frame, ChildBudget? owner)
    {
        public readonly long Id = id;
        public readonly byte[] Frame = frame;
        public readonly ChildBudget? Owner = owner;
        public bool Dropped;

        public void Release() => Owner?.Release(Frame.Length);
    }

    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> _pending = new();
    readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    readonly Channel<JsonDocument> _messages = Channel.CreateUnbounded<JsonDocument>();
    readonly Lock _queues = new();
    readonly LinkedList<Outgoing> _outgoing = new();
    readonly LinkedList<Outgoing> _sent = new();
    readonly LinkedList<Outgoing> _replay = new();
    readonly LinkedList<Outgoing> _unsent = new();
    readonly List<Outgoing> _writing = new(1);
    long _sentBytes;
    long _lostThrough = long.MinValue;
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
        if (!Enqueue(id, EncodeRequest(id, op, args)))
        {
            _pending.TryRemove(id, out _);
            throw new GameDisconnectedException(_closed ?? "the harness child's session ended");
        }
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
        Enqueue(0, buffer.WrittenSpan.ToArray());
    }

    public void Post(string op, Action<Utf8JsonWriter>? args = null)
    {
        if (_closed is null)
        {
            var id = Interlocked.Increment(ref _nextId);
            Enqueue(id, EncodeRequest(id, op, args));
        }
    }

    bool Enqueue(long id, byte[] frame)
    {
        var owner = ChildBudget.Current;
        lock (_queues)
        {
            if (_closed is not null || owner is { Ended: true })
                return false;
            if (owner is not null && !Charge(owner, frame.Length))
            {
                owner.Overran();
                return false;
            }
            _outgoing.AddLast(new Outgoing(id, frame, owner));
        }
        _wake.Writer.TryWrite(true);
        return true;
    }

    public bool Reserve(ChildBudget owner, long bytes)
    {
        lock (_queues)
            if (Charge(owner, bytes))
                return true;
        owner.Overran();
        return false;
    }

    // A frame already written is kept only for a replay: this child's go first, oldest first, so a child that sends
    // many small messages is never ended by what is kept for a replay.
    bool Charge(ChildBudget owner, long bytes)
    {
        while (!owner.TryAdd(bytes))
        {
            var kept = _sent.First;
            while (kept is not null && kept.Value.Owner != owner)
                kept = kept.Next;
            if (kept is null)
                return false;
            Evict(kept);
        }
        return true;
    }

    void Evict(LinkedListNode<Outgoing> kept)
    {
        _sent.Remove(kept);
        _sentBytes -= kept.Value.Frame.Length;
        _lostThrough = Math.Max(_lostThrough, kept.Value.Id);
        kept.Value.Release();
    }

    // Nothing a failed child asked for goes out or comes back on a resume, except a frame already on the socket, which
    // finishes and is not kept.
    public void Drop(ChildBudget owner)
    {
        lock (_queues)
        {
            Remove(_outgoing, owner);
            Remove(_replay, owner);
            Remove(_unsent, owner);
            _sentBytes -= Remove(_sent, owner);
            foreach (var frame in _writing)
                if (frame.Owner == owner)
                    frame.Dropped = true;
        }
    }

    static long Remove(LinkedList<Outgoing> frames, ChildBudget? owner)
    {
        long removed = 0;
        for (var node = frames.First; node is not null;)
        {
            var next = node.Next;
            if (owner is null || node.Value.Owner == owner)
            {
                frames.Remove(node);
                removed += node.Value.Frame.Length;
                node.Value.Release();
            }
            node = next;
        }
        return removed;
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
                Outgoing? frame = null;
                lock (_queues)
                {
                    if ((_replay.First ?? _outgoing.First) is { } next)
                    {
                        next.List!.Remove(next);
                        frame = next.Value;
                        _writing.Add(frame);
                    }
                }
                if (frame is null)
                {
                    if (!await _wake.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                        return;
                    _wake.Reader.TryRead(out _);
                    continue;
                }
                var sent = false;
                try
                {
                    await socket.SendAsync(frame.Frame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                    sent = true;
                }
                finally
                {
                    lock (_queues)
                    {
                        _writing.Remove(frame);
                        if (frame.Dropped)
                            frame.Release();
                        else if (sent)
                            Keep(frame);
                        else
                            // Held until sent: a Unity script reload can drop the socket mid-send, and the frame then
                            // goes out on the resumed connection.
                            _unsent.AddLast(frame);
                    }
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ChannelClosedException)
        {
        }
    }

    void Keep(Outgoing frame)
    {
        _sent.AddLast(frame);
        _sentBytes += frame.Frame.Length;
        while (_sent.Count > MaxReplayFrames || _sentBytes > MaxReplayBytes)
            Evict(_sent.First!);
    }

    // A frame the plugin had not taken yet and the link let go to stay bounded makes the resume incomplete.
    internal bool CanResume(long received)
    {
        lock (_queues)
            return _lostThrough <= received;
    }

    // A send can succeed after the plugin's 1012 close left it, while the socket still reads Open, so only the plugin
    // knows what arrived. Runs between pumps: no send loop is alive.
    internal void Rewind(long received)
    {
        lock (_queues)
        {
            foreach (var frame in _sent)
            {
                if (frame.Id > received)
                    _replay.AddLast(frame);
                else
                    frame.Release();
            }
            _sent.Clear();
            _sentBytes = 0;
            foreach (var frame in _unsent)
                _replay.AddLast(frame);
            _unsent.Clear();
        }
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
        lock (_queues)
        {
            Remove(_outgoing, null);
            Remove(_replay, null);
            Remove(_unsent, null);
            Remove(_sent, null);
            _sentBytes = 0;
            foreach (var frame in _writing)
                frame.Dropped = true;
        }
        _wake.Writer.TryComplete();
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

public sealed class ChildBudget(int maxItems, long maxBytes)
{
    [ThreadStatic] static ChildBudget? t_current;

    readonly Lock _lock = new();
    int _items;
    long _bytes;
    volatile bool _ended;
    volatile bool _overrun;

    public static ChildBudget? Current => t_current;

    public int Items
    {
        get
        {
            lock (_lock)
                return _items;
        }
    }

    public long Bytes
    {
        get
        {
            lock (_lock)
                return _bytes;
        }
    }

    public bool Ended => _ended;

    public bool Overrun => _overrun;

    public void End() => _ended = true;

    public void Overran() => _overrun = true;

    public bool TryAdd(long bytes)
    {
        lock (_lock)
        {
            if (_items >= maxItems || _bytes + bytes > maxBytes)
                return false;
            _items++;
            _bytes += bytes;
            return true;
        }
    }

    public void Release(long bytes)
    {
        lock (_lock)
        {
            _items--;
            _bytes -= bytes;
        }
    }

    // The child's messages reach the link through IGameLink and the host's status callback, which carry no owner: what
    // a link queues on this thread while the scope lasts is charged here.
    public Scope Charge()
    {
        var previous = t_current;
        t_current = this;
        return new Scope(previous);
    }

    public readonly struct Scope(ChildBudget? previous) : IDisposable
    {
        public void Dispose() => t_current = previous;
    }
}
