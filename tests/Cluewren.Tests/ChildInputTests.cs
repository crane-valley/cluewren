using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cluewren.Decisions;
using Cluewren.Kit;
using Cluewren.Sandbox;

namespace Cluewren.Tests;

sealed class ChildStdout : Stream
{
    readonly Lock _lock = new();
    readonly Queue<byte[]> _chunks = new();
    readonly SemaphoreSlim _available = new(0);
    byte[]? _current;
    int _offset;
    bool _done;
    Exception? _fault;
    int _reads;

    public Action<int>? Reading;

    public void Write(string text) => Feed(Encoding.UTF8.GetBytes(text));

    public void Feed(byte[] bytes)
    {
        lock (_lock)
            _chunks.Enqueue(bytes);
        _available.Release();
    }

    public void End()
    {
        lock (_lock)
            _done = true;
        _available.Release();
    }

    public void Fault(Exception e)
    {
        lock (_lock)
            _fault = e;
        _available.Release();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Reading?.Invoke(Interlocked.Increment(ref _reads));
        while (true)
        {
            lock (_lock)
            {
                if (_current is null && _chunks.TryDequeue(out var chunk))
                    (_current, _offset) = (chunk, 0);
                if (_current is not null)
                {
                    var n = Math.Min(count, _current.Length - _offset);
                    Array.Copy(_current, _offset, buffer, offset, n);
                    _offset += n;
                    if (_offset == _current.Length)
                        _current = null;
                    return n;
                }
                if (_fault is not null)
                    throw _fault;
                if (_done)
                    return 0;
            }
            _available.Wait();
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class ChildStdin : Stream
{
    readonly List<byte> _partial = [];

    public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();

    public Func<Exception?>? Refuse;

    public async Task<JsonElement> Next(string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            using var line = JsonDocument.Parse(await Lines.Reader.ReadAsync(timeout.Token));
            if (line.RootElement.GetProperty("type").GetString() == type)
                return line.RootElement.Clone();
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Refuse?.Invoke() is { } refused)
            throw refused;
        lock (_partial)
            foreach (var b in buffer)
            {
                if (b != (byte)'\n')
                {
                    _partial.Add(b);
                    continue;
                }
                Lines.Writer.TryWrite(Encoding.UTF8.GetString(_partial.ToArray()));
                _partial.Clear();
            }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class ScriptedChild : ISandboxProcess
{
    readonly List<string> _events = [];

    public ScriptedChild(bool holdError = false)
    {
        if (!holdError)
            Err.End();
    }

    public ChildStdout Err { get; } = new();

    public ChildStdout Out { get; } = new();

    public ChildStdin In { get; } = new();

    public TaskCompletionSource KillStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action? Killing;

    public Stream Input => In;

    public Stream Output => Out;

    public Stream Error => Err;

    public string Kind => "scripted";

    public string[] Events
    {
        get
        {
            lock (_events)
                return [.. _events];
        }
    }

    public void Kill()
    {
        Note("kill");
        KillStarted.TrySetResult();
        Killing?.Invoke();
        Note("killed");
        Out.End();
    }

    public void Dispose()
    {
        Note("dispose");
        Out.End();
    }

    void Note(string what)
    {
        lock (_events)
            _events.Add(what);
    }
}

sealed class RecordingLink : IGameLink
{
    readonly List<string> _posts = [];

    public TaskCompletionSource<CancellationToken> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string[] Posts
    {
        get
        {
            lock (_posts)
                return [.. _posts];
        }
    }

    public async ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        Asked.TrySetResult(ct);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        finally
        {
            Abandoned.TrySetResult();
        }
        throw new InvalidOperationException("unreachable");
    }

    public void Post(string op, Action<Utf8JsonWriter>? args = null)
    {
        lock (_posts)
            _posts.Add(op);
    }

    public void Send(Action<Utf8JsonWriter> frame)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            frame(json);
            json.WriteEndObject();
        }
        lock (_posts)
            _posts.Add("send " + Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}

sealed class HeldJev : IDecisionRelay
{
    int _asked, _cancelled;

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Asked => Volatile.Read(ref _asked);

    public int Cancelled => Volatile.Read(ref _cancelled);

    public async Task<(byte[] Body, long LatencyTicks)> DecideRawAsync(JsonElement stateAndQuestions, CancellationToken ct)
    {
        Interlocked.Increment(ref _asked);
        try
        {
            await Release.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
        return ("{}"u8.ToArray(), 0);
    }
}

sealed class HookedRecorder(Action<int> begin) : IEpisodeRecorder
{
    public void Begin(int episode) => begin(episode);

    public void End(int episode)
    {
    }
}

public sealed class ChildInputTests : IDisposable
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never held");
            await Task.Yield();
        }
    }

    readonly string _log = Path.Combine(Path.GetTempPath(), $"cluewren-child-input-{Guid.NewGuid():N}.log");

    public void Dispose() => File.Delete(_log);

    SandboxSession Start(ScriptedChild child, IGameLink link, IDecisionRelay? jev = null, IEpisodeRecorder? recorder = null) =>
        new(child, link, jev, () => true, recorder, _ => { }, _log);

    const string Broken = """{"type":"reply","id":"x"}""" + "\n";

    static string Reply(long id, string value = "pong") => $$"""{"type":"reply","id":{{id}},"value":"{{value}}"}""" + "\n";

    static async Task<long> CallId(ScriptedChild child) => (await child.In.Next("call")).GetProperty("id").GetInt64();

    static async Task<string> Ping(SandboxSession session, ScriptedChild child, double seconds = 30)
    {
        var call = session.Call("ping", null, seconds, CancellationToken.None);
        child.Out.Write(Reply(await CallId(child)));
        return (await call.WaitAsync(Patience)).GetString()!;
    }

    // Before, the log belonged to the thread draining the child's stderr and stayed open until the child closed it, so
    // a session disposed while the child still held its stderr left the log open after Dispose had returned.
    [Fact]
    public async Task Once_dispose_returns_the_log_is_closed_even_while_the_child_holds_its_stderr()
    {
        var child = new ScriptedChild(holdError: true);
        var session = Start(child, new RecordingLink());
        try
        {
            child.Err.Write("boom\n");
            await Until(() =>
            {
                if (!File.Exists(_log))
                    return false;
                using var reading = new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return reading.Length > 0;
            });
            session.Dispose();
            File.Delete(_log);
            Assert.False(File.Exists(_log));
        }
        finally
        {
            child.Err.End();
            session.Dispose();
        }
    }

    // A reply with neither field used to reach the caller as a KeyNotFoundException and leave the child running.
    [Fact]
    public async Task A_reply_without_value_or_error_ends_the_session_and_kills_the_child()
    {
        var child = new ScriptedChild();
        using var session = Start(child, new RecordingLink());
        var call = session.Call("ping", null, 30, CancellationToken.None);
        child.Out.Write($$"""{"type":"reply","id":{{await CallId(child)}}}""" + "\n");
        var failed = await Assert.ThrowsAsync<SandboxException>(() => call.WaitAsync(Patience));
        Assert.Contains("broke the protocol", failed.Message);
        Assert.Contains("kill", child.Events);
    }

    [Fact]
    public async Task A_reply_to_an_unknown_or_an_answered_call_is_dropped_and_the_session_goes_on()
    {
        var child = new ScriptedChild();
        using var session = Start(child, new RecordingLink());
        var call = session.Call("ping", null, 30, CancellationToken.None);
        var id = await CallId(child);
        child.Out.Write(Reply(id + 1000) + Reply(id, "first") + Reply(id, "second"));
        Assert.Equal("first", (await call.WaitAsync(Patience)).GetString());
        Assert.Equal("pong", await Ping(session, child));
        Assert.DoesNotContain("kill", child.Events);
    }

    [Fact]
    public async Task A_reply_after_its_call_timed_out_is_dropped_and_the_session_goes_on()
    {
        var child = new ScriptedChild();
        using var session = Start(child, new RecordingLink());
        var call = session.Call("ping", null, 0.2, CancellationToken.None);
        var id = await CallId(child);
        var late = await Assert.ThrowsAsync<SandboxException>(() => call.WaitAsync(Patience));
        Assert.Contains("in time", late.Message);
        child.Out.Write(Reply(id));
        Assert.Equal("pong", await Ping(session, child));
    }

    // The reader's next read starts once the reply was handed over: cancelling there races the caller's wake-up.
    [Fact]
    public async Task A_cancel_at_the_delivery_of_a_reply_gives_the_value_or_the_cancellation_only()
    {
        var child = new ScriptedChild();
        CancellationTokenSource? cancel = null;
        var armed = 0;
        child.Out.Reading = _ =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
                cancel!.Cancel();
        };
        using var session = Start(child, new RecordingLink());
        var (answered, cancelled) = (0, 0);
        for (var i = 0; i < 40; i++)
        {
            using var source = new CancellationTokenSource();
            cancel = source;
            var call = session.Call("ping", null, 30, source.Token);
            var id = await CallId(child);
            Volatile.Write(ref armed, 1);
            child.Out.Write(Reply(id));
            try
            {
                Assert.Equal("pong", (await call.WaitAsync(Patience)).GetString());
                answered++;
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }
        }
        Assert.Equal(40, answered + cancelled);
        Assert.Equal("pong", await Ping(session, child));
    }

    // Before: a call made while the session was failing waited for the failure to finish, and a waiter could wake before Kill.
    [Fact]
    public async Task While_a_failing_session_kills_its_child_calls_are_refused_and_waiters_wait_for_the_kill()
    {
        var child = new ScriptedChild();
        using var hold = new ManualResetEventSlim();
        child.Killing = () => hold.Wait(Patience);
        var session = Start(child, new RecordingLink());
        var waiting = session.Call("ping", null, 30, CancellationToken.None);
        await CallId(child);
        child.Out.Write(Broken);
        await child.KillStarted.Task.WaitAsync(Patience);
        var refused = await Assert.ThrowsAsync<SandboxException>(
            () => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
        Assert.Contains("broke the protocol", refused.Message);
        Assert.False(waiting.IsCompleted);
        var disposer = new Thread(session.Dispose);
        disposer.Start();
        while ((disposer.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) == 0)
            Thread.Yield();
        hold.Set();
        Assert.True(disposer.Join(Patience));
        await Assert.ThrowsAsync<SandboxException>(() => waiting.WaitAsync(Patience));
        Assert.Equal(["kill", "killed", "dispose"], child.Events);
    }

    // Before: a failure left the requests the child had asked for running until the session was disposed.
    [Fact]
    public async Task A_failure_while_a_request_is_served_cancels_the_request()
    {
        var child = new ScriptedChild();
        var link = new RecordingLink();
        using var session = Start(child, link);
        child.Out.Write("""{"type":"req","rid":1,"op":"pose","args":{}}""" + "\n");
        var ct = await link.Asked.Task.WaitAsync(Patience);
        child.Out.Write(Broken);
        await link.Abandoned.Task.WaitAsync(Patience);
        Assert.True(ct.IsCancellationRequested);
        Assert.Contains("kill", child.Events);
    }

    // The reader is held in the episode mark while a Jev answer fails the session on another thread: the post read in
    // the same chunk must not reach the game. Before, it did.
    [Fact]
    public async Task A_post_buffered_when_the_session_fails_elsewhere_is_not_forwarded()
    {
        var child = new ScriptedChild();
        var link = new RecordingLink();
        var jev = new HeldJev();
        var answering = 0;
        child.In.Refuse = () => Volatile.Read(ref answering) == 1 ? new InvalidOperationException("the pipe broke") : null;
        var recorder = new HookedRecorder(_ =>
        {
            Volatile.Write(ref answering, 1);
            jev.Release.TrySetResult();
            child.KillStarted.Task.Wait(Patience);
            Volatile.Write(ref answering, 0);
        });
        using var session = Start(child, link, jev, recorder);
        session.Episode = 1;
        child.Out.Write("""{"type":"decide","rid":1,"body":{}}""" + "\n");
        await Until(() => jev.Asked > 0);
        child.Out.Write("""{"type":"episode","rid":2,"mark":"begin","n":1}""" + "\n"
                        + """{"type":"post","op":"key","args":{"key":"W","mode":"hold"}}""" + "\n");
        await child.KillStarted.Task.WaitAsync(Patience);
        await Assert.ThrowsAsync<SandboxException>(() => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
        Assert.Empty(link.Posts);
    }

    // Before: every request was served at once whatever their number, each holding its line.
    [Fact]
    public async Task A_flood_of_jev_requests_ends_the_session_at_the_budget()
    {
        var child = new ScriptedChild();
        var jev = new HeldJev();
        using var session = Start(child, new StillGame(), jev);
        var flood = new StringBuilder();
        for (var i = 1; i <= 2000; i++)
            flood.Append($$"""{"type":"decide","rid":{{i}},"body":{"state":{} } }""").Append('\n');
        child.Out.Write(flood.ToString());
        await child.KillStarted.Task.WaitAsync(Patience);
        var failed = await Assert.ThrowsAsync<SandboxException>(
            () => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
        Assert.Contains("hold more than", failed.Message);
        Assert.True(jev.Asked < 2000, $"{jev.Asked} asked");
        await Until(() => jev.Cancelled == jev.Asked);
    }

    // Before: requests were served at once whatever their number, each holding its line and its game request.
    [Fact]
    public async Task A_flood_of_requests_ends_the_session_at_the_budget_and_cancels_what_it_asked()
    {
        var child = new ScriptedChild();
        var link = new RecordingLink();
        using var session = Start(child, link);
        var flood = new StringBuilder();
        for (var i = 1; i <= 2000; i++)
            flood.Append($$"""{"type":"req","rid":{{i}},"op":"pose","args":{} }""").Append('\n');
        child.Out.Write(flood.ToString());
        await child.KillStarted.Task.WaitAsync(Patience);
        var failed = await Assert.ThrowsAsync<SandboxException>(
            () => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
        Assert.Contains("hold more than", failed.Message);
        await link.Abandoned.Task.WaitAsync(Patience);
    }

    // Before: statuses were shown one by one on the reader, and a host that showed them slowly held up everything else;
    // now they wait in the session's queue, which counts on the budget.
    [Fact]
    public async Task A_flood_of_statuses_behind_a_slow_host_ends_the_session_at_the_budget()
    {
        var child = new ScriptedChild();
        using var release = new ManualResetEventSlim();
        var session = new SandboxSession(child, new RecordingLink(), null, () => true, null, _ => release.Wait(Patience), _log);
        try
        {
            var flood = new StringBuilder();
            for (var i = 1; i <= 2000; i++)
                flood.Append($$"""{"type":"status","text":"step {{i}}"}""").Append('\n');
            child.Out.Write(flood.ToString());
            await child.KillStarted.Task.WaitAsync(Patience);
            var failed = await Assert.ThrowsAsync<SandboxException>(
                () => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
            Assert.Contains("hold more than", failed.Message);
        }
        finally
        {
            release.Set();
            session.Dispose();
        }
    }

    // Before: an episode mark was taken with any number, so a child could add spans to the recorder without end.
    [Fact]
    public async Task A_mark_for_another_episode_than_the_one_played_ends_the_session()
    {
        var child = new ScriptedChild();
        using var session = Start(child, new RecordingLink());
        session.Episode = 3;
        child.Out.Write("""{"type":"episode","rid":1,"mark":"begin","n":7}""" + "\n");
        await child.KillStarted.Task.WaitAsync(Patience);
        var failed = await Assert.ThrowsAsync<SandboxException>(
            () => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
        Assert.Contains("another episode", failed.Message);
    }

    // Before: the child of a probe, which plays no episode, could mark episode 0 and have what it filmed looked at as
    // an episode's pictures.
    [Fact]
    public async Task A_mark_from_a_child_that_plays_no_episode_ends_the_session()
    {
        var child = new ScriptedChild();
        using var session = Start(child, new RecordingLink());
        child.Out.Write("""{"type":"episode","rid":1,"mark":"begin","n":0}""" + "\n");
        await child.KillStarted.Task.WaitAsync(Patience);
        var failed = await Assert.ThrowsAsync<SandboxException>(
            () => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
        Assert.Contains("another episode", failed.Message);
    }

    // Before: a raw frame reached the plugin as the child wrote it; typed "REQUEST" it passed the case-sensitive check,
    // and a plugin that reads the type without case took it as a request with an id the child chose.
    [Theory]
    [InlineData("""{"type":"request","id":1,"op":"key","args":{}}""")]
    [InlineData("""{"type":"REQUEST","id":1,"op":"key","args":{}}""")]
    [InlineData("""{"type":"note","n":1}""")]
    public async Task A_raw_frame_from_the_child_ends_the_session_and_is_not_sent(string frame)
    {
        var child = new ScriptedChild();
        var link = new RecordingLink();
        using var session = Start(child, link);
        child.Out.Write("""{"type":"send","frame":""" + frame + "}\n");
        await child.KillStarted.Task.WaitAsync(Patience);
        var failed = await Assert.ThrowsAsync<SandboxException>(
            () => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
        Assert.Contains("may not send raw frames", failed.Message);
        Assert.Empty(link.Posts);
    }

    // Before, the status callback ran under the gate on the reader: a callback that blocked kept the failure from ever
    // reaching Kill, and Dispose waited on the same gate.
    [Fact]
    public async Task A_status_callback_that_blocks_keeps_neither_a_failure_nor_dispose_from_ending_the_child()
    {
        var child = new ScriptedChild();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new SandboxSession(child, new RecordingLink(), null, () => true, null, _ =>
        {
            entered.Set();
            release.Wait(Patience);
        }, _log);
        try
        {
            child.Out.Write("""{"type":"status","text":"hello"}""" + "\n");
            Assert.True(entered.Wait(Patience));
            child.Out.Write(Broken);
            await child.KillStarted.Task.WaitAsync(Patience);
            var refused = await Assert.ThrowsAsync<SandboxException>(
                () => session.Call("ping", null, 30, CancellationToken.None).WaitAsync(Patience));
            Assert.Contains("broke the protocol", refused.Message);
            await Task.Run(session.Dispose).WaitAsync(Patience);
            Assert.Contains("dispose", child.Events);
        }
        finally
        {
            release.Set();
            session.Dispose();
        }
    }

    // Before, a timeout no TimeSpan or WaitAsync takes threw only after the call was registered and written: the child
    // answered a call nobody waits for, and that reply was handed to the abandoned waiter instead of being freed.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public async Task A_call_with_a_timeout_that_cannot_be_waited_is_refused_before_it_reaches_the_child(double seconds)
    {
        var child = new ScriptedChild();
        using var session = Start(child, new RecordingLink());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.Call("ping", null, seconds, CancellationToken.None));
        Assert.Equal("pong", await Ping(session, child));
    }

    sealed class SeenLink : Sim.SimLink
    {
        readonly List<string> _seen = [];

        public string[] Seen
        {
            get
            {
                lock (_seen)
                    return [.. _seen];
            }
        }

        protected override object? Answer(string op, JsonElement args)
        {
            lock (_seen)
                _seen.Add($"{op} {args.GetRawText()}");
            return new Dictionary<string, object?> { ["took"] = op };
        }
    }

    static string Line(string type, string op, string? args, long? rid = null) =>
        $$"""{"type":"{{type}}",{{(rid is null ? "" : $"\"rid\":{rid},")}}"op":"{{op}}"{{(args is null ? "" : $",\"args\":{args}")}}}""" + "\n";

    static async Task<JsonElement> Res(ScriptedChild child, long rid)
    {
        var res = await child.In.Next("res");
        Assert.Equal(rid, res.GetProperty("rid").GetInt64());
        return res;
    }

    [Theory]
    [InlineData("record", """{"action":"start","fps":20}""")]
    [InlineData("record", """{"action":"stop"}""")]
    [InlineData("capture", "{}")]
    [InlineData("detectors", """{"enabled":false}""")]
    [InlineData("timestep", """{"scale":20}""")]
    [InlineData("session", """{"action":"stop"}""")]
    [InlineData("session", """{"action":"start"}""")]
    [InlineData("session", """{"action":"start","map":"Levels/Main"}""")]
    [InlineData("session", """{"action":"RESTART"}""")]
    [InlineData("session", """{"action":7}""")]
    [InlineData("session", "{}")]
    [InlineData("session", null)]
    [InlineData("session", """["restart"]""")]
    [InlineData("session", """{"action":"restart","map":"C:/Games/Foo/.cluewren/runs/20250101-000000/work/made.tscn"}""")]
    [InlineData("session", """{"action":"restart","map":"res://.Cluewren/runs/1/work/made.tscn"}""")]
    [InlineData("session", """{"action":"restart","map":["Levels/Main"]}""")]
    [InlineData("session", """{"action":"restart","map":null}""")]
    [InlineData("Pose", "{}")]
    [InlineData("no_such_op", "{}")]
    public async Task What_harness_code_may_not_ask_is_answered_403_or_dropped_and_the_session_goes_on(string op, string? args)
    {
        var child = new ScriptedChild();
        var link = new SeenLink();
        using var session = Start(child, link);
        child.Out.Write(Line("req", op, args, rid: 5));
        var refused = await Res(child, 5);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal(403, refused.GetProperty("status").GetInt32());
        Assert.Equal($"{op} is refused: harness code may not send it", refused.GetProperty("error").GetString());
        child.Out.Write(Line("post", op, args) + Line("req", "pose", "{}", rid: 6));
        Assert.True((await Res(child, 6)).GetProperty("ok").GetBoolean());
        Assert.Equal("pong", await Ping(session, child));
        Assert.Equal(["pose {}"], link.Seen);
        Assert.DoesNotContain("kill", child.Events);
    }

    [Theory]
    [InlineData("pose", "{}", "{}")]
    [InlineData("ping", null, "{}")]
    [InlineData("key", """{"key":"W","mode":"hold"}""", """{"key":"W","mode":"hold"}""")]
    [InlineData("pause", """{"paused":true}""", """{"paused":true}""")]
    [InlineData("rc", """{"object":"Main:/Door","property":"open","value":true}""", """{"object":"Main:/Door","property":"open","value":true}""")]
    [InlineData("session", """{"action":"restart"}""", """{"action":"restart"}""")]
    [InlineData("session", """{"action":"restart","map":"Levels/Main"}""", """{"action":"restart","map":"Levels/Main"}""")]
    public async Task What_harness_code_may_ask_reaches_the_game_as_a_request_and_as_a_post(string op, string? args, string reached)
    {
        var child = new ScriptedChild();
        var link = new SeenLink();
        using var session = Start(child, link);
        child.Out.Write(Line("req", op, args, rid: 5));
        var answered = await Res(child, 5);
        Assert.True(answered.GetProperty("ok").GetBoolean(), answered.GetRawText());
        Assert.Equal(op, answered.GetProperty("result").GetProperty("took").GetString());
        child.Out.Write(Line("post", op, args));
        Assert.Equal("pong", await Ping(session, child));
        Assert.Equal([$"{op} {reached}", $"{op} {reached}"], link.Seen);
    }

    [Fact]
    public void Every_op_the_kit_sends_for_play_is_allowed_and_what_the_brain_runs_a_run_by_is_not()
    {
        using var none = JsonDocument.Parse("{}");
        foreach (var op in (string[])
                 [
                     "rc", "find", "transforms", "pose", "movement", "view", "watch", "screen", "ui", "ui_act", "key", "input", "move",
                     "pointer", "pause", "context", "levels", "ping",
                 ])
            Assert.True(ChildOps.Allows(op, none.RootElement), op);
        foreach (var op in (string[])["record", "capture", "detectors", "timestep", "session", "hello", ""])
            Assert.False(ChildOps.Allows(op, none.RootElement), op);
        Assert.False(ChildOps.Allows("session", default));
    }

    const string TwoOptions = """{"pick":{"type":"choice","instructions":"Choose.","criteria":{"a":"go left","b":"go right"}}}""";
    const string PicksA = """{"answers":{"pick":{"type":"choice","choice":"a","confidence":0.9,"probabilities":{"a":0.9,"b":0.1}}},"usage":{"input_tokens":100,"output_tokens":2}}""";

    [Theory]
    [InlineData(TwoOptions, PicksA, 1)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","choice":"b"}}}""", 1)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"probabilities":{"b":1.0,"elsewhere":0.0}}}}""", 1)]
    [InlineData("""{"pick":{"type":"choice","criteria":{"a":"go left","b":"go right"}},"sure":{"type":"noul","criteria":"is it safe"}}""", PicksA, 1)]
    [InlineData("""{"pick":{"type":"choice","criteria":{"a":"go left"}}}""", PicksA, 0)]
    [InlineData("""{"pick":{"type":"choice","criteria":{}}}""", PicksA, 0)]
    [InlineData("""{"pick":{"type":"choice","criteria":{"a":"go left","a":"go left again"}}}""", PicksA, 0)]
    [InlineData("""{"pick":{"type":"score","criteria":{"a":"go left","b":"go right"}}}""", PicksA, 0)]
    [InlineData("""{"pick":{"type":"noul","criteria":{"a":"go left","b":"go right"}}}""", PicksA, 0)]
    [InlineData("""{"pick":{"criteria":{"a":"go left","b":"go right"}}}""", PicksA, 0)]
    [InlineData("""{"pick":{"type":"choice","criteria":["a","b"]}}""", PicksA, 0)]
    [InlineData("""{"pick":{"type":"choice"}}""", PicksA, 0)]
    [InlineData("""{"other":{"type":"choice","criteria":{"a":"go left","b":"go right"}}}""", PicksA, 0)]
    [InlineData("""["pick"]""", PicksA, 0)]
    [InlineData(null, PicksA, 0)]
    [InlineData(TwoOptions, null, 0)]
    [InlineData(TwoOptions, "{}", 0)]
    [InlineData(TwoOptions, "[]", 0)]
    [InlineData(TwoOptions, """{"answers":{}}""", 0)]
    [InlineData(TwoOptions, """{"answers":[]}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"other":{"type":"choice","choice":"a"}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":"a"}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice"}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","choice":"elsewhere"}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","choice":"A"}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","choice":7}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","choice":"elsewhere","probabilities":{"elsewhere":1.0}}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","probabilities":{}}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","probabilities":["a","b"]}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","choice":"a","probabilities":{"a":"high"}}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","choice":"a","probabilities":{"elsewhere":1.0}}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","probabilities":{"a":0.0}}}}""", 0)]
    [InlineData(TwoOptions, """{"answers":{"pick":{"type":"choice","choice":"a","probabilities":{"a":0.0,"b":-1.0}}}}""", 0)]
    public async Task Only_an_answer_that_picks_among_two_or_more_options_offered_as_a_choice_counts_as_served(string? questions,
        string? answer, int counted)
    {
        var child = new ScriptedChild();
        var jev = new FixedJev(answer);
        using var session = Start(child, new RecordingLink(), jev);
        var body = questions is null ? """{"state":{}}""" : $$"""{"state":{},"questions":{{questions}}}""";
        for (var rid = 1; rid <= 2; rid++)
        {
            child.Out.Write($$"""{"type":"decide","rid":{{rid}},"body":{{body}}}""" + "\n");
            var res = await Res(child, rid);
            Assert.Equal(answer is not null, res.GetProperty("ok").GetBoolean());
            if (answer is null)
                Assert.Equal("DecisionException: HTTP 503", res.GetProperty("error").GetString());
            else
                Assert.Equal(answer, res.GetProperty("result").GetRawText());
            Assert.Equal(rid * counted, session.DecisionsServed);
        }
        Assert.Equal(2, jev.Calls);
        Assert.Equal("pong", await Ping(session, child));
    }

    [Fact]
    public async Task A_decision_past_the_runs_limit_or_without_a_decider_is_answered_as_failed_and_not_counted()
    {
        var decide = """{"type":"decide","rid":1,"body":{"state":{},"questions":""" + TwoOptions + "}}\n";
        var jev = new FixedJev(PicksA);
        var limited = new ScriptedChild();
        using (var session = new SandboxSession(limited, new RecordingLink(), jev, () => false, null, _ => { }, _log))
        {
            limited.Out.Write(decide);
            var res = await Res(limited, 1);
            Assert.False(res.GetProperty("ok").GetBoolean());
            Assert.Contains("limit on Jev decisions is reached", res.GetProperty("error").GetString());
            Assert.Equal((0, 0), (session.DecisionsServed, jev.Calls));
        }
        var alone = new ScriptedChild();
        using (var session = Start(alone, new RecordingLink()))
        {
            alone.Out.Write(decide);
            Assert.Contains("no Jev key in this run", (await Res(alone, 1)).GetProperty("error").GetString());
            Assert.Equal(0, session.DecisionsServed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_child_is_told_the_run_folder_the_work_folder_and_whether_a_decider_is_there(bool decider)
    {
        var child = new ScriptedChild();
        var link = new SeenLink();
        link.FeatureList.Add("media");
        using var session = Start(child, link, decider ? new FixedJev(PicksA) : null);
        session.Init(@"C:\Games\Foo\.cluewren\runs\1", @"C:\Games\Foo\.cluewren\runs\1\work", "Levels/Main", 42);
        var init = await child.In.Next("init");
        Assert.Equal(["type", "run_folder", "work_folder", "level", "decider", "max_decisions", "engine", "features"],
            init.EnumerateObject().Select(field => field.Name));
        Assert.Equal(@"C:\Games\Foo\.cluewren\runs\1", init.GetProperty("run_folder").GetString());
        Assert.Equal(@"C:\Games\Foo\.cluewren\runs\1\work", init.GetProperty("work_folder").GetString());
        Assert.Equal("Levels/Main", init.GetProperty("level").GetString());
        Assert.Equal(decider, init.GetProperty("decider").GetBoolean());
        Assert.Equal(42, init.GetProperty("max_decisions").GetInt32());
        Assert.Equal("unity", init.GetProperty("engine").GetString());
        Assert.Equal(["media"], init.GetProperty("features").EnumerateArray().Select(feature => feature.GetString()));
    }

    [Fact]
    public async Task A_child_that_ignores_the_stop_is_ended_once_the_grace_is_over()
    {
        var child = new ScriptedChild();
        using var session = Start(child, new RecordingLink());
        var playing = session.Call("run_harness", null, 600, CancellationToken.None);
        await CallId(child);
        session.StopWithin(TimeSpan.FromMilliseconds(50));
        await child.In.Next("stop");
        var ended = await Assert.ThrowsAsync<SandboxException>(() => playing.WaitAsync(Patience));
        Assert.Equal("it didn't stop when asked and was ended", ended.Message);
        await child.KillStarted.Task.WaitAsync(Patience);
        var after = await Assert.ThrowsAsync<SandboxException>(() => session.Call("ping", null, 30, CancellationToken.None));
        Assert.Equal("it didn't stop when asked and was ended", after.Message);
    }

    [Fact]
    public async Task A_child_that_stops_when_asked_gives_its_result_and_is_not_killed_after_the_grace()
    {
        var grace = TimeSpan.FromMilliseconds(200);
        var child = new ScriptedChild();
        var session = Start(child, new RecordingLink());
        try
        {
            var playing = session.Call("run_harness", null, 600, CancellationToken.None);
            var id = await CallId(child);
            session.StopWithin(grace);
            await child.In.Next("stop");
            child.Out.Write(Reply(id, "summary"));
            Assert.Equal("summary", (await playing.WaitAsync(Patience)).GetString());
        }
        finally
        {
            session.Dispose();
        }
        await Task.Delay(grace * 3);
        Assert.Equal(["dispose"], child.Events);
    }

    // Before, the stop line was written first and on the caller's thread: a child that read nothing more held the caller
    // in the write, and the end after the grace was never set.
    [Fact]
    public async Task A_child_that_reads_nothing_more_is_ended_after_the_grace_and_the_stop_does_not_wait_for_it()
    {
        var child = new ScriptedChild();
        using var reading = new ManualResetEventSlim(true);
        child.In.Refuse = () =>
        {
            reading.Wait();
            return null;
        };
        var session = Start(child, new RecordingLink());
        try
        {
            var playing = session.Call("run_harness", null, 600, CancellationToken.None);
            await CallId(child);
            reading.Reset();
            await Task.Run(() => session.StopWithin(TimeSpan.FromMilliseconds(50))).WaitAsync(Patience);
            var ended = await Assert.ThrowsAsync<SandboxException>(() => playing.WaitAsync(Patience));
            Assert.Equal("it didn't stop when asked and was ended", ended.Message);
            Assert.Contains("kill", child.Events);
        }
        finally
        {
            reading.Set();
            session.Dispose();
        }
    }
}

public class LineReaderFaultTests
{
    // The shared pool hands a thread back the array that thread returned last: a line given back on the throw comes
    // back here with its bytes. Before, the array was lost to the pool.
    [Fact]
    public void A_stream_that_throws_mid_line_gives_the_rented_line_back()
    {
        var stream = new ChildStdout();
        stream.Feed(Enumerable.Repeat((byte)'q', 1000).ToArray());
        stream.Fault(new IOException("the pipe broke"));
        var reader = new LineReader(stream);
        Assert.Throws<IOException>(() => reader.Next());
        var again = ArrayPool<byte>.Shared.Rent(1000);
        try
        {
            Assert.True(again.AsSpan(0, 1000).IndexOfAnyExcept((byte)'q') < 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(again);
        }
    }
}
