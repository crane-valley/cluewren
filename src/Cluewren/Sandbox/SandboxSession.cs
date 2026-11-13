using System.Buffers;
using System.Text.Json;
using Cluewren.Jev;
using Cluewren.Kit;

namespace Cluewren.Sandbox;

public interface IEpisodeRecorder
{
    void Begin(int episode);

    void End(int episode);
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
    readonly IJevRelay? _jev;
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
    long _ids;
    int _heldItems;
    long _heldBytes;
    bool _ended;
    bool _saying;
    bool _overrun;
    int _disposed;
    string? _failure;

    public SandboxSession(ISandboxProcess process, IGameLink link, IJevRelay? jev, Func<bool> spendDecision,
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
        new Thread(ReadLoop) { IsBackground = true, Name = "sandbox-read" }.Start();
        new Thread(() => DrainError(logPath)) { IsBackground = true, Name = "sandbox-log" }.Start();
    }

    public string Kind => _process.Kind;

    public void Init(string runFolder, string? level, int maxDecisions) =>
        _out.Write((runFolder, level, maxDecisions, Link: _link, Jev: _jev is not null), static (json, s) =>
        {
            json.WriteString("type", "init");
            json.WriteString("run_folder", s.runFolder);
            if (s.level is not null)
                json.WriteString("level", s.level);
            json.WriteBoolean("jev", s.Jev);
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

    void DrainError(string path)
    {
        try
        {
            using var log = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var chunk = new byte[4096];
            while (true)
            {
                var read = _process.Error.Read(chunk);
                if (read <= 0)
                    return;
                if (log.Length < MaxLog)
                {
                    log.Write(chunk, 0, read);
                    log.Flush();
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
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
                if (!Admit(line))
                    break;
                _ = Serve(ServeRequest(rid, op, root), line);
                return;
            }
            case "post":
            {
                var op = Text(root, "op");
                var args = root.TryGetProperty("args", out var posted) ? posted : default;
                lock (_gate)
                    if (!_ended)
                        _link.Post(op, json => WriteFields(json, args));
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
            case "jev":
            {
                var rid = root.GetProperty("rid").GetInt64();
                if (!Admit(line))
                    break;
                _ = Serve(ServeJev(rid, root), line);
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
            using var answer = await _link.RequestAsync(op, json => WriteFields(json, args), timeout, _closing.Token).ConfigureAwait(false);
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

    async Task ServeJev(long rid, JsonElement root)
    {
        try
        {
            if (_jev is null)
                throw new JevException("no Jev key in this run");
            if (!_spendDecision())
                throw new JevException("the run's limit on Jev decisions is reached");
            var (body, latency) = await _jev.DecideRawAsync(root.GetProperty("body"), _closing.Token).ConfigureAwait(false);
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

    void ServeEpisode(long rid, string mark, int n)
    {
        try
        {
            if (mark == "begin")
                _recorder?.Begin(n);
            else if (mark == "end")
                _recorder?.End(n);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            _say($"The episode's recording failed: {e.Message}");
        }
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
        _closing.Dispose();
        foreach (var call in calls)
            call.TrySetResult(null);
    }
}
