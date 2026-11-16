using System.Buffers;
using System.Text.Json;
using Cluewren.Bridge;
using Cluewren.Decisions;
using Cluewren.Kit;

namespace Cluewren.Sandbox;

public interface IEpisodeRecorder
{
    // An episode that was numbered and then not played: its number is given to the next one.
    void Forget(int episode)
    {
    }

    void Begin(int episode);

    void End(int episode);
}

// What harness code may ask of the game. The rest is the brain's: what it films and measures a run by, and where the
// game starts and ends.
public static class ChildOps
{
    static readonly HashSet<string> Allowed =
    [
        "rc", "find", "transforms", "pose", "movement", "view", "watch", "screen", "ui", "ui_act", "key", "input", "move",
        "pointer", "pause", "context", "levels", "ping",
    ];

    public static bool Allows(string op, JsonElement args) =>
        Allowed.Contains(op)
        || (op == "session" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var action)
            && action.ValueKind == JsonValueKind.String && action.ValueEquals("restart") && !StartsWhatItWrote(args));

    // The plugin refuses a map under the folder harness code can write, after resolving what the engine resolves. This
    // is the same refusal by the name alone, for a level that would run outside the container.
    static bool StartsWhatItWrote(JsonElement args) =>
        args.TryGetProperty("map", out var map)
        && (map.ValueKind != JsonValueKind.String || map.GetString()!.Contains(".cluewren", StringComparison.OrdinalIgnoreCase));
}

public sealed class SandboxSession : IDisposable
{
    const long MaxLog = 16 << 20;
    // The kit's own messages stay under 4 KB; what a probe prints is the open-ended part, and the host keeps 12000
    // characters of it.
    const int MaxLine = 4 << 20;
    const int MaxHeldItems = 512;
    const long MaxHeldBytes = 16 << 20;
    // Task.WaitAsync takes no longer timeout.
    const double MaxCallSeconds = (uint.MaxValue - 1) / 1000.0;

    sealed class Line(JsonDocument document, byte[] rented, int length) : IDisposable
    {
        int _disposed;

        public JsonElement Root => document.RootElement;

        public int Length => length;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            document.Dispose();
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    readonly ISandboxProcess _process;
    readonly IGameLink _link;
    readonly IDecisionRelay? _jev;
    readonly Func<bool> _spendDecision;
    readonly IEpisodeRecorder? _recorder;
    readonly Action<string> _say;
    readonly LineWriter _out;
    readonly Dictionary<long, TaskCompletionSource<Line?>> _calls = new();
    readonly CancellationTokenSource _closing = new();
    readonly Action<JsonElement> _forward;
    // Orders call registration, reply delivery, accepting what the child forwards, and the end of the session.
    readonly Lock _gate = new();
    readonly Lock _ending = new();
    readonly Queue<(string Text, int Bytes)> _statuses = new();
    readonly Lock _logging = new();
    // Its own lock, not the one a failing session takes to kill the child: a recorder that is slow must not hold that up.
    readonly Lock _marking = new();
    bool _marksClosed;
    FileStream? _log;
    long _ids;
    int _served;
    int _heldItems;
    long _heldBytes;
    bool _ended;
    bool _saying;
    bool _overrun;
    int _disposed;
    string? _failure;

    public SandboxSession(ISandboxProcess process, IGameLink link, IDecisionRelay? jev, Func<bool> spendDecision,
        IEpisodeRecorder? recorder, Action<string> say, string logPath)
    {
        _process = process;
        _link = link;
        _jev = jev;
        _spendDecision = spendDecision;
        _recorder = recorder;
        _say = say;
        _out = new LineWriter(process.Input);
        _forward = Forward;
        _link.Event += _forward;
        _log = OpenLog(logPath);
        new Thread(ReadLoop) { IsBackground = true, Name = "sandbox-read" }.Start();
        new Thread(DrainError) { IsBackground = true, Name = "sandbox-log" }.Start();
    }

    public string Kind => _process.Kind;

    // Counted here, where the decision model answered: what the child says it decided is its own word.
    public int DecisionsServed => Volatile.Read(ref _served);

    public int Episode { get; set; }

    public void Init(string runFolder, string workFolder, string? level, int maxDecisions) =>
        _out.Write((runFolder, workFolder, level, maxDecisions, Link: _link, Decider: _jev is not null), static (json, s) =>
        {
            json.WriteString("type", "init");
            json.WriteString("run_folder", s.runFolder);
            json.WriteString("work_folder", s.workFolder);
            if (s.level is not null)
                json.WriteString("level", s.level);
            json.WriteBoolean("decider", s.Decider);
            json.WriteNumber("max_decisions", s.maxDecisions);
            json.WriteString("engine", s.Link.Engine);
            json.WriteStartArray("features");
            foreach (var feature in s.Link.Features)
                json.WriteStringValue(feature);
            json.WriteEndArray();
        });

    void Forward(JsonElement evt)
    {
        if (!evt.TryGetProperty("kind", out var kind) || !(kind.ValueEquals("finding") || kind.ValueEquals("log")))
            return;
        _out.Write(evt, static (json, s) =>
        {
            json.WriteString("type", "event");
            json.WritePropertyName("event");
            s.WriteTo(json);
        });
    }

    static FileStream? OpenLog(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The log belongs to the session, not to this thread: Dispose closes it, so it is closed once Dispose returns even
    // while this thread still waits on a stderr the child has not closed.
    void DrainError()
    {
        try
        {
            var chunk = new byte[4096];
            while (true)
            {
                var read = _process.Error.Read(chunk);
                if (read <= 0)
                    return;
                lock (_logging)
                {
                    if (_log is null)
                        return;
                    if (_log.Length < MaxLog)
                    {
                        _log.Write(chunk, 0, read);
                        _log.Flush();
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
        }
        finally
        {
            CloseLog();
        }
    }

    void CloseLog()
    {
        lock (_logging)
        {
            var log = _log;
            _log = null;
            // Disposing flushes again, and a disk that failed one flush fails this one too: on the drain thread that
            // would end the brain, in Dispose it would skip the rest.
            try
            {
                log?.Dispose();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
            }
        }
    }

    void ReadLoop()
    {
        var reader = new LineReader(_process.Output, MaxLine);
        try
        {
            while (reader.Next() is { } next)
            {
                if (Volatile.Read(ref _ended))
                {
                    ArrayPool<byte>.Shared.Return(next.Rented);
                    return;
                }
                Line line;
                try
                {
                    line = new Line(JsonDocument.Parse(next.Rented.AsMemory(0, next.Length)), next.Rented, next.Length);
                }
                catch (JsonException e)
                {
                    ArrayPool<byte>.Shared.Return(next.Rented);
                    Fail($"the child broke the protocol ({e.Message})");
                    return;
                }
                try
                {
                    Dispatch(line);
                }
                // Harness code can write JSON of any shape to the child's stdout: it ends the session, never this process.
                catch (Exception e)
                {
                    line.Dispose();
                    Fail($"the child broke the protocol ({e.GetType().Name}: {e.Message})");
                    return;
                }
                if (_overrun)
                {
                    Fail($"the child had the brain hold more than {MaxHeldItems} messages or {MaxHeldBytes >> 20} MB for it");
                    return;
                }
            }
        }
        catch (InvalidDataException e)
        {
            Fail($"the child broke the protocol ({e.Message})");
            return;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        foreach (var call in End())
            call.TrySetResult(null);
    }

    List<TaskCompletionSource<Line?>> End()
    {
        lock (_gate)
        {
            _ended = true;
            while (_statuses.TryDequeue(out var status))
            {
                _heldItems--;
                _heldBytes -= status.Bytes;
            }
            var calls = _calls.Values.ToList();
            _calls.Clear();
            return calls;
        }
    }

    void Fail(string why)
    {
        Interlocked.CompareExchange(ref _failure, why, null);
        var calls = End();
        // Under the lock Dispose takes: a job handle closed and reused by the next child must never be killed from here.
        lock (_ending)
            if (_disposed == 0)
                _process.Kill();
        try
        {
            _closing.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        foreach (var call in calls)
            call.TrySetResult(null);
    }

    bool Admit(Line line)
    {
        lock (_gate)
            return !_ended && Hold(line.Length);
    }

    // Under the gate. What the session holds for the child: requests being served and statuses waiting to be shown.
    bool Hold(int bytes)
    {
        if (_heldItems >= MaxHeldItems || _heldBytes + bytes > MaxHeldBytes)
        {
            _overrun = true;
            return false;
        }
        _heldItems++;
        _heldBytes += bytes;
        return true;
    }

    void Let(int bytes)
    {
        lock (_gate)
        {
            _heldItems--;
            _heldBytes -= bytes;
        }
    }

    static string Text(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw new FormatException($"{name} is null");

    // Every field is read before anything is handed on, so a line that throws here has handed nothing on.
    void Dispatch(Line line)
    {
        var root = line.Root;
        switch (Text(root, "type"))
        {
            case "reply":
            {
                var id = root.GetProperty("id").GetInt64();
                if (root.TryGetProperty("error", out var error)
                        ? error.ValueKind != JsonValueKind.String
                        : !root.TryGetProperty("value", out _))
                    throw new FormatException("a reply without a value or an error");
                lock (_gate)
                {
                    if (_calls.Remove(id, out var call))
                    {
                        call.TrySetResult(line);
                        return;
                    }
                }
                break;
            }
            case "req":
            {
                var rid = root.GetProperty("rid").GetInt64();
                var op = Text(root, "op");
                if (!ChildOps.Allows(op, root.TryGetProperty("args", out var asked) ? asked : default))
                {
                    _out.Write((rid, op), static (json, s) =>
                    {
                        json.WriteString("type", "res");
                        json.WriteNumber("rid", s.rid);
                        json.WriteBoolean("ok", false);
                        json.WriteNumber("status", 403);
                        json.WriteString("error", $"{s.op} is refused: harness code may not send it");
                    });
                    break;
                }
                if (!Admit(line))
                    break;
                _ = Serve(ServeRequest(rid, op, root), line);
                return;
            }
            case "post":
            {
                var op = Text(root, "op");
                var args = root.TryGetProperty("args", out var posted) ? posted : default;
                if (!ChildOps.Allows(op, args))
                    break;
                var taken = true;
                lock (_gate)
                    if (!_ended)
                    {
                        if (_link is BridgeLink bridge)
                            taken = bridge.TryPost(op, json => WriteFields(json, args));
                        else
                            _link.Post(op, json => WriteFields(json, args));
                    }
                if (!taken && _link.Closed is null)
                    throw new FormatException("the game isn't taking what the harness posts");
                break;
            }
            // Only the brain writes raw frames to the plugin: one from the child could pass for any of them, a request
            // included, and no kit path sends one.
            case "send":
                throw new FormatException("the child may not send raw frames to the game");
            case "status":
            {
                var text = root.GetProperty("text").GetString() ?? "";
                bool start;
                lock (_gate)
                {
                    if (_ended || !Hold(line.Length))
                        break;
                    _statuses.Enqueue((text, line.Length));
                    start = !_saying;
                    _saying = true;
                }
                if (start)
                    _ = Task.Run(SayQueued);
                break;
            }
            case "decide":
            {
                var rid = root.GetProperty("rid").GetInt64();
                if (!Admit(line))
                    break;
                _ = Serve(ServeDecision(rid, root), line);
                return;
            }
            case "episode":
                ServeEpisode(root.GetProperty("rid").GetInt64(), Text(root, "mark"), root.GetProperty("n").GetInt32());
                break;
        }
        line.Dispose();
    }

    // The host's callback is arbitrary code (the CLI's writes to a console nobody may be reading): it never runs under the
    // gate, so a callback that blocks cannot keep Fail from killing the child.
    void SayQueued()
    {
        while (true)
        {
            (string Text, int Bytes) status;
            lock (_gate)
            {
                if (!_statuses.TryDequeue(out status))
                {
                    _saying = false;
                    return;
                }
            }
            try
            {
                _say(status.Text);
            }
            catch (Exception e)
            {
                Fail($"the brain couldn't show the child's status ({e.GetType().Name}: {e.Message})");
            }
            finally
            {
                Let(status.Bytes);
            }
        }
    }

    async Task Serve(Task handler, Line line)
    {
        try
        {
            await handler.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fail($"the brain couldn't answer the child ({e.GetType().Name}: {e.Message})");
        }
        finally
        {
            Let(line.Length);
            line.Dispose();
        }
    }

    static void WriteFields(Utf8JsonWriter json, JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Object)
            return;
        foreach (var field in fields.EnumerateObject())
            field.WriteTo(json);
    }

    async Task ServeRequest(long rid, string op, JsonElement root)
    {
        try
        {
            var args = root.TryGetProperty("args", out var given) ? given : default;
            TimeSpan? timeout = root.TryGetProperty("timeout", out var limit) ? TimeSpan.FromSeconds(limit.GetDouble()) : null;
            using var answer = await (_link is BridgeLink bridge
                ? bridge.RequestWithinAsync(op, json => WriteFields(json, args), timeout, _closing.Token)
                : _link.RequestAsync(op, json => WriteFields(json, args), timeout, _closing.Token).AsTask()).ConfigureAwait(false);
            Restarted(op);
            _out.Write((rid, answer, Link: _link), static (json, s) =>
            {
                json.WriteString("type", "res");
                json.WriteNumber("rid", s.rid);
                json.WriteBoolean("ok", true);
                json.WritePropertyName("result");
                var result = s.answer.Result;
                if (result.ValueKind == JsonValueKind.Undefined)
                    json.WriteNullValue();
                else
                    result.WriteTo(json);
                if (s.answer.GameTime is { } t)
                    json.WriteNumber("t", t);
                if (s.Link.GameTime is { } latest)
                    json.WriteNumber("game_time", latest);
            });
        }
        catch (GameException e)
        {
            Restarted(op);
            _out.Write((rid, e, Link: _link), static (json, s) =>
            {
                json.WriteString("type", "res");
                json.WriteNumber("rid", s.rid);
                json.WriteBoolean("ok", false);
                json.WriteNumber("status", s.e.Status);
                json.WriteString("error", s.e.Message);
                if (s.Link.GameTime is { } latest)
                    json.WriteNumber("game_time", latest);
            });
        }
        // The game gave no answer (the link went, or this session is ending): nothing is known to have started over.
        catch (Exception e)
        {
            _out.Write((rid, Text: $"{e.GetType().Name}: {e.Message}", Link: _link), static (json, s) =>
            {
                json.WriteString("type", "res");
                json.WriteNumber("rid", s.rid);
                json.WriteBoolean("ok", false);
                json.WriteString("conn", s.Text);
                if (s.Link.Closed is { } closed)
                    json.WriteString("closed", closed);
            });
        }
    }

    // The level starting over is where an episode's pictures begin, and this process saw it: a harness that marks its
    // beginning later moves nothing. Before the answer is written to the child, which may not be reading.
    void Restarted(string op)
    {
        if (op == "session" && Episode > 0)
            Mark(begin: true, Episode);
    }

    // A decision is a choice among two or more options: a request that offers one is a script asking for a signature.
    // Only this counts toward the report's gate: two or more options by different names were offered as a choice, and
    // the decision model's answer picks among them.
    static bool Decided(JsonElement asked, ReadOnlySpan<byte> answered)
    {
        if (asked.ValueKind != JsonValueKind.Object || !asked.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Object
            || !questions.TryGetProperty(DecisionRequest.PickQuestion, out var pick) || pick.ValueKind != JsonValueKind.Object
            || !pick.TryGetProperty("type", out var type) || !type.ValueEquals("choice")
            || !pick.TryGetProperty("criteria", out var options) || options.ValueKind != JsonValueKind.Object)
            return false;
        var offered = options.EnumerateObject().Select(option => option.Name).ToHashSet(StringComparer.Ordinal);
        if (offered.Count < 2)
            return false;
        // Read as the kit reads it, so that what counts here is what a harness can act on.
        try
        {
            return Decision.Parse(answered, 0).Probabilities
                .Any(weight => offered.Contains(weight.Key) && double.IsFinite(weight.Value) && weight.Value > 0);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    async Task ServeDecision(long rid, JsonElement root)
    {
        try
        {
            if (_jev is null)
                throw new DecisionException("no Jev key in this run");
            if (!_spendDecision())
                throw new DecisionException("the run's limit on Jev decisions is reached");
            var asked = root.GetProperty("body");
            var (body, latency) = await _jev.DecideRawAsync(asked, _closing.Token).ConfigureAwait(false);
            if (Decided(asked, body))
                Interlocked.Increment(ref _served);
            _out.Write((rid, body, latency), static (json, s) =>
            {
                json.WriteString("type", "res");
                json.WriteNumber("rid", s.rid);
                json.WriteBoolean("ok", true);
                json.WriteNumber("latency", s.latency);
                json.WritePropertyName("result");
                json.WriteRawValue(s.body);
            });
        }
        catch (Exception e)
        {
            _out.Write((rid, Text: Fmt.Cut($"{e.GetType().Name}: {e.Message}", 500)), static (json, s) =>
            {
                json.WriteString("type", "res");
                json.WriteNumber("rid", s.rid);
                json.WriteBoolean("ok", false);
                json.WriteString("error", s.Text);
            });
        }
    }

    // Not once the session is disposed: its episode may have been given up by then, and its number to another.
    void Mark(bool begin, int n)
    {
        string? failed = null;
        lock (_marking)
        {
            if (_marksClosed)
                return;
            try
            {
                if (begin)
                    _recorder?.Begin(n);
                else
                    _recorder?.End(n);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                failed = e.Message;
            }
        }
        if (failed is not null)
            _say($"The episode's recording failed: {failed}");
    }

    void ServeEpisode(long rid, string mark, int n)
    {
        // A probe or a check plays no episode: a mark from one would make pictures to look at where none was played.
        if (Episode <= 0 || n != Episode)
            throw new FormatException("an episode mark for another episode than the one being played");
        if (mark is "begin" or "end")
            Mark(mark == "begin", n);
        _out.Write(rid, static (json, s) =>
        {
            json.WriteString("type", "res");
            json.WriteNumber("rid", s);
            json.WriteBoolean("ok", true);
        });
    }

    public async Task<JsonElement> Call(string method, object? args, double timeoutSeconds, CancellationToken ct)
    {
        if (!(timeoutSeconds is >= 0 and <= MaxCallSeconds))
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), timeoutSeconds, "not a call timeout");
        var waiter = new TaskCompletionSource<Line?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_gate)
        {
            if (_ended)
                throw new SandboxException(Volatile.Read(ref _failure) ?? "the child ended before it answered");
            id = ++_ids;
            _calls[id] = waiter;
        }
        Line? line;
        try
        {
            _out.Write((id, method, args), static (json, s) =>
            {
                json.WriteString("type", "call");
                json.WriteNumber("id", s.id);
                json.WriteString("method", s.method);
                json.WritePropertyName("args");
                Jsonable.Write(json, s.args ?? new Dictionary<string, object?>(), 0, round: false);
            });
            line = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Line? late = null;
            lock (_gate)
                if (!_calls.Remove(id) && waiter.Task.IsCompletedSuccessfully)
                    late = waiter.Task.Result;
            late?.Dispose();
            if (e is TimeoutException)
                throw new SandboxException($"the sandbox didn't answer {method} in time");
            throw;
        }
        using (line)
        {
            if (line is null)
                throw new SandboxException(Volatile.Read(ref _failure) ?? "the child ended before it answered");
            var root = line.Root;
            if (root.TryGetProperty("error", out var error))
                throw new SandboxException(error.GetString() ?? "the child failed");
            return root.GetProperty("value").Clone();
        }
    }

    public void Stop() => _out.Write(0, static (json, _) => json.WriteString("type", "stop"));

    // The child is asked, so that an episode ends with its summary. Stopping does not rest on its answer, nor on the
    // asking: a child that reads nothing blocks the write, so the end is set before it and the write waits elsewhere.
    public void StopWithin(TimeSpan grace)
    {
        _ = Task.Delay(grace).ContinueWith(_ =>
        {
            if (Volatile.Read(ref _disposed) == 0 && !Volatile.Read(ref _ended))
                Fail("it didn't stop when asked and was ended");
        }, TaskScheduler.Default);
        _ = Task.Run(Stop);
    }

    public void Dispose()
    {
        lock (_ending)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
        }
        var calls = End();
        _link.Event -= _forward;
        _closing.Cancel();
        _process.Dispose();
        CloseLog();
        _closing.Dispose();
        foreach (var call in calls)
            call.TrySetResult(null);
        // Last, when the child is gone and its calls are answered: a recorder still busy with a mark delays only this
        // return, after which no mark of this session reaches it.
        lock (_marking)
            _marksClosed = true;
    }
}
