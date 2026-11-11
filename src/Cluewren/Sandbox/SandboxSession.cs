using System.Buffers;
using System.Collections.Concurrent;
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

    readonly ISandboxProcess _process;
    readonly IGameLink _link;
    readonly IJevRelay? _jev;
    readonly Func<bool> _spendDecision;
    readonly IEpisodeRecorder? _recorder;
    readonly Action<string> _say;
    readonly LineWriter _out;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument?>> _calls = new();
    readonly CancellationTokenSource _closing = new();
    readonly Action<JsonElement> _forward;
    readonly Lock _ending = new();
    long _ids;
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
        var reader = new LineReader(_process.Output);
        try
        {
            while (reader.Next() is { } line)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line.Rented.AsMemory(0, line.Length));
                }
                catch (JsonException)
                {
                    ArrayPool<byte>.Shared.Return(line.Rented);
                    continue;
                }
                try
                {
                    Dispatch(document, line.Rented);
                }
                // Harness code can write JSON of any shape to the child's stdout: it ends the session, never this process.
                catch (Exception e) when (e is KeyNotFoundException or FormatException
                                              or InvalidOperationException and not ObjectDisposedException)
                {
                    document.Dispose();
                    ArrayPool<byte>.Shared.Return(line.Rented);
                    Fail($"the child broke the protocol ({e.GetType().Name}: {e.Message})");
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
        foreach (var call in _calls.Values)
            call.TrySetResult(null);
    }

    void Fail(string why)
    {
        Interlocked.CompareExchange(ref _failure, why, null);
        // Under the lock Dispose takes: a job handle closed and reused by the next child must never be killed from here.
        lock (_ending)
            if (_disposed == 0)
                _process.Kill();
        foreach (var call in _calls.Values)
            call.TrySetResult(null);
    }

    static string Text(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw new FormatException($"{name} is null");

    // Every field is read before anything is handed on, so a line that throws here has handed nothing on.
    void Dispatch(JsonDocument document, byte[] rented)
    {
        var root = document.RootElement;
        switch (Text(root, "type"))
        {
            case "reply":
                if (_calls.TryRemove(root.GetProperty("id").GetInt64(), out var call))
                {
                    call.TrySetResult(document);
                    return;
                }
                break;
            case "req":
                _ = Serve(ServeRequest(root.GetProperty("rid").GetInt64(), Text(root, "op"), root), document, rented);
                return;
            case "post":
                var op = Text(root, "op");
                var args = root.TryGetProperty("args", out var posted) ? posted : default;
                _link.Post(op, json => WriteFields(json, args));
                break;
            case "send":
                var frame = root.GetProperty("frame");
                _link.Send(json => WriteFields(json, frame));
                break;
            case "status":
                _say(root.GetProperty("text").GetString() ?? "");
                break;
            case "jev":
                _ = Serve(ServeJev(root.GetProperty("rid").GetInt64(), root), document, rented);
                return;
            case "episode":
                ServeEpisode(root.GetProperty("rid").GetInt64(), Text(root, "mark"), root.GetProperty("n").GetInt32());
                break;
        }
        document.Dispose();
        ArrayPool<byte>.Shared.Return(rented);
    }

    async Task Serve(Task handler, JsonDocument document, byte[] rented)
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
            document.Dispose();
            ArrayPool<byte>.Shared.Return(rented);
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
        var id = Interlocked.Increment(ref _ids);
        var waiter = new TaskCompletionSource<JsonDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = waiter;
        _out.Write((id, method, args), static (json, s) =>
        {
            json.WriteString("type", "call");
            json.WriteNumber("id", s.id);
            json.WriteString("method", s.method);
            json.WritePropertyName("args");
            Jsonable.Write(json, s.args ?? new Dictionary<string, object?>(), 0, round: false);
        });
        try
        {
            using var document = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct).ConfigureAwait(false)
                                 ?? throw new SandboxException(Volatile.Read(ref _failure) ?? "the child ended before it answered");
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new SandboxException(error.GetString() ?? "the child failed");
            return root.GetProperty("value").Clone();
        }
        catch (TimeoutException)
        {
            throw new SandboxException($"the sandbox didn't answer {method} in time");
        }
        finally
        {
            _calls.TryRemove(id, out _);
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
        _link.Event -= _forward;
        _closing.Cancel();
        _process.Dispose();
        _closing.Dispose();
    }
}
