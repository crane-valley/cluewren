using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using Cluewren.Kit;

namespace Cluewren.Sandbox.Child;

sealed class ProxyLink : IGameLink
{
    sealed class Waiter : IValueTaskSource<GameAnswer>
    {
        ManualResetValueTaskSourceCore<GameAnswer> _core = new() { RunContinuationsAsynchronously = true };
        CancellationTokenRegistration _registration;
        int _finished;

        public ProxyLink Owner = null!;
        public long Id;
        public long Deadline;
        public string What = "";

        public short Version => _core.Version;

        public void Arm(ProxyLink owner, long id, double seconds, string what, CancellationToken ct)
        {
            (Owner, Id, What) = (owner, id, what);
            Deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
            Volatile.Write(ref _finished, 0);
            if (ct.CanBeCanceled)
                _registration = ct.UnsafeRegister(static (state, token) => ((Waiter)state!).Fail(new OperationCanceledException(token)), this);
        }

        public void Finish(GameAnswer answer)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
                _core.SetResult(answer);
            else
                answer.Dispose();
        }

        public void Fail(Exception e)
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
            {
                Owner._pending.TryRemove(Id, out _);
                _core.SetException(e);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        public GameAnswer GetResult(short token)
        {
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                _registration.Dispose();
                _registration = default;
                _core.Reset();
                Owner._free.Enqueue(this);
            }
        }
    }

    readonly ConcurrentDictionary<long, Waiter> _pending = new();
    readonly ConcurrentQueue<Waiter> _free = new();
    readonly LineWriter _out;
    readonly string[] _features;
    long _ids;
    long _gameTime = BitConverter.DoubleToInt64Bits(double.NaN);
    volatile string? _closed;

    public ProxyLink(LineWriter output, string[] features, string engine)
    {
        _out = output;
        _features = features;
        Engine = engine;
        new Thread(Sweep) { IsBackground = true, Name = "proxy-timeouts" }.Start();
    }

    public event Action<JsonElement>? Event;

    public string Engine { get; }

    public IReadOnlyCollection<string> Features => _features;

    public string? Closed => _closed;

    public double? GameTime
    {
        get
        {
            var t = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _gameTime));
            return double.IsNaN(t) ? null : t;
        }
    }

    public void Cork() => _out.Cork();

    public void Uncork() => _out.Uncork();

    public void Raise(JsonElement evt) => Event?.Invoke(evt);

    void Sweep()
    {
        while (true)
        {
            Thread.Sleep(1000);
            var now = Stopwatch.GetTimestamp();
            foreach (var waiter in _pending.Values)
                if (now > waiter.Deadline)
                    waiter.Fail(new GameDisconnectedException($"no answer to {waiter.What} in time"));
        }
    }

    Waiter Rent(string what, double seconds, CancellationToken ct, out long id)
    {
        id = Interlocked.Increment(ref _ids);
        if (!_free.TryDequeue(out var waiter))
            waiter = new Waiter();
        waiter.Arm(this, id, seconds, what, ct);
        _pending[id] = waiter;
        return waiter;
    }

    public ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (_closed is { } why)
            return ValueTask.FromException<GameAnswer>(new GameDisconnectedException(why));
        var seconds = timeout?.TotalSeconds;
        var waiter = Rent(op, (seconds ?? 30) + 15, ct, out var id);
        var version = waiter.Version;
        _out.Write((id, op, args, seconds), static (json, s) =>
        {
            json.WriteString("type", "req");
            json.WriteNumber("rid", s.id);
            json.WriteString("op", s.op);
            json.WriteStartObject("args");
            s.args?.Invoke(json);
            json.WriteEndObject();
            if (s.seconds is { } limit)
                json.WriteNumber("timeout", limit);
        });
        return new ValueTask<GameAnswer>(waiter, version);
    }

    public void Post(string op, Action<Utf8JsonWriter>? args = null) =>
        _out.Write((op, args), static (json, s) =>
        {
            json.WriteString("type", "post");
            json.WriteString("op", s.op);
            json.WriteStartObject("args");
            s.args?.Invoke(json);
            json.WriteEndObject();
        });

    public void Status(string text) => _out.Write(text, static (json, s) =>
    {
        json.WriteString("type", "status");
        json.WriteString("text", s);
    });

    public void Send(Action<Utf8JsonWriter> frame) => _out.Write(frame, static (json, s) =>
    {
        json.WriteString("type", "send");
        json.WriteStartObject("frame");
        s(json);
        json.WriteEndObject();
    });

    public ValueTask<GameAnswer> AskJev(byte[] body, CancellationToken ct)
    {
        var waiter = Rent("jev", 10, ct, out var id);
        var version = waiter.Version;
        _out.Write((id, body), static (json, s) =>
        {
            json.WriteString("type", "jev");
            json.WriteNumber("rid", s.id);
            json.WritePropertyName("body");
            json.WriteRawValue(s.body, skipInputValidation: true);
        });
        return new ValueTask<GameAnswer>(waiter, version);
    }

    public ValueTask<GameAnswer> Mark(string mark, int episode)
    {
        var waiter = Rent("episode", 20, default, out var id);
        var version = waiter.Version;
        _out.Write((id, mark, episode), static (json, s) =>
        {
            json.WriteString("type", "episode");
            json.WriteNumber("rid", s.id);
            json.WriteString("mark", s.mark);
            json.WriteNumber("n", s.episode);
        });
        return new ValueTask<GameAnswer>(waiter, version);
    }

    public void Answer(JsonDocument document, byte[] rented)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number)
            Interlocked.Exchange(ref _gameTime, BitConverter.DoubleToInt64Bits(t.GetDouble()));
        else if (root.TryGetProperty("game_time", out var gameTime) && gameTime.ValueKind == JsonValueKind.Number)
            Interlocked.Exchange(ref _gameTime, BitConverter.DoubleToInt64Bits(gameTime.GetDouble()));
        if (root.TryGetProperty("closed", out var closed) && closed.ValueKind == JsonValueKind.String)
            _closed ??= closed.GetString();
        var answer = new GameAnswer(document, rented);
        if (!root.TryGetProperty("rid", out var rid) || !_pending.TryRemove(rid.GetInt64(), out var waiter))
        {
            answer.Dispose();
            return;
        }
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            waiter.Finish(answer);
            return;
        }
        Exception failure;
        if (root.TryGetProperty("conn", out var conn) && conn.ValueKind == JsonValueKind.String)
        {
            failure = new GameDisconnectedException(conn.GetString() ?? "the game is disconnected");
        }
        else
        {
            var status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : 400;
            failure = new GameException(status, root.TryGetProperty("error", out var e) ? e.GetString() ?? "error" : "error");
        }
        answer.Dispose();
        waiter.Fail(failure);
    }

    public void Close(string why)
    {
        _closed ??= why;
        foreach (var waiter in _pending.Values)
            waiter.Fail(new GameDisconnectedException(why));
    }
}

sealed class RemoteJev(ProxyLink link) : IDecider
{
    public async Task<JevDecision> DecideAsync(JevRequest request, CancellationToken ct)
    {
        using var answer = await link.AskJev(request.EncodeBody(), ct).ConfigureAwait(false);
        var latency = answer.Frame.TryGetProperty("latency", out var ticks) ? ticks.GetInt64() : 0;
        return JevDecision.Parse(answer.Result, latency);
    }
}
