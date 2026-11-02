using System.Diagnostics;
using System.Text.Json;
using Cluewren.Kit;
using Cluewren.Tests.Sim;

namespace Cluewren.Tests;

[CollectionDefinition("wall clock", DisableParallelization = true)]
public sealed class WallClockCollection;

public abstract class TempTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "playtest-tests", Guid.NewGuid().ToString("N"));

    protected string Tmp(string name) => Path.Combine(_dir, name);

    protected static List<JsonElement> Events(string path) => Cluewren.Kit.Trace.Read(path);

    protected static IEnumerable<JsonElement> OfKind(string path, string kind) =>
        Events(path).Where(e => e.GetProperty("kind").GetString() == kind);

    protected static Game Looking(IGameLink link) => new(link) { Look = new LookScale { X = 0.1, Y = 0.1 } };

    public static Executor Ticks(int n) => async (game, _) =>
    {
        for (var i = 0; i < n; i++)
            await game.Tick();
        return null!;
    };

    public static async Task<Result> Drive(Game game, Func<CancellationToken, Task<Result>> controller, double seconds = 20)
    {
        var watch = Stopwatch.StartNew();
        var stepper = new Stepper(game, controller);
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            game.Renew();
            await stepper.Step();
            if (stepper.Done)
                return await stepper.Task!;
            await Task.Delay(TimeSpan.FromSeconds(1.0 / 30));
        }
        await stepper.Stop();
        throw new Xunit.Sdk.XunitException("the controller didn't finish");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// time.sleep is good to a millisecond on Windows; Task.Delay rounds up to the 15.6 ms timer tick, which makes Control.Run's
// 15 Hz about 13 Hz. The wolf's bite is phased against those ticks, so the tactic tests need upstream's real rate.
public sealed class PreciseClock : IClock
{
    public static readonly PreciseClock Instance = new();

    public double Monotonic() => RealClock.Instance.Monotonic();

    public async ValueTask Delay(double seconds, CancellationToken ct = default)
    {
        var end = Monotonic() + seconds;
        if (seconds > 0.02)
            await Task.Delay(TimeSpan.FromSeconds(seconds - 0.02), ct);
        while (Monotonic() < end)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Yield();
        }
    }

    public ITicker Ticker(double seconds) => new PreciseTicker(this, seconds);

    sealed class PreciseTicker(PreciseClock clock, double seconds) : ITicker
    {
        double _next = clock.Monotonic() + seconds;

        public async ValueTask Wait(CancellationToken ct = default)
        {
            var now = clock.Monotonic();
            await clock.Delay(Math.Max(0, _next - now), ct);
            _next = Math.Max(_next, now) + seconds;
        }

        public void Dispose()
        {
        }
    }
}

public sealed record Input(string? Key, string? Mode, double? Seconds);

public class FakeLink : SimLink
{
    public const string HeroPath = "Main:/Level/Hero_C_0";
    public const string CratePath = "Main:/Level/Crate_C_0";

    protected readonly Lock Gate = new();
    long _wall = Stopwatch.GetTimestamp();

    public FakeLink() => GameTime = 0;

    public Vec Pos { get; set; } = new(0, 0, 100);

    public double Yaw { get; set; }

    public double Pitch { get; set; }

    public HashSet<string> Held { get; } = [];

    public Vec Crate { get; set; } = new(800, 600, 100);

    public bool Opened { get; set; }

    public bool Paused { get; set; }

    public List<Input> Inputs { get; } = [];

    protected double Elapse()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Stopwatch.GetElapsedTime(_wall, now).TotalSeconds;
        _wall = now;
        return dt;
    }

    protected void Pass(double dt) => GameTime = (GameTime ?? 0) + dt;

    protected virtual void Advance()
    {
        var dt = Elapse();
        if (Paused)
            return;
        Pass(dt);
        if (Held.Contains("W"))
            Pos += Geom.Forward(Yaw) * (400 * dt);
    }

    protected virtual Vec ActorAt(string obj) => obj.Contains("Crate") ? Crate : Pos;

    protected virtual Vec ExtentOf(string obj) => new(50, 50, 50);

    protected virtual Vec EyeOf() => Pos + new Vec(0, 0, 60);

    protected virtual Vec VelocityOf() => new();

    protected static Dictionary<string, object?> D(params (string Key, object? Value)[] items)
    {
        var fields = new Dictionary<string, object?>();
        foreach (var (key, value) in items)
            fields[key] = value;
        return fields;
    }

    protected sealed override object? Answer(string op, JsonElement args)
    {
        lock (Gate)
        {
            Advance();
            return Read(op, args);
        }
    }

    protected virtual object? Read(string op, JsonElement args)
    {
        switch (op)
        {
            case "context":
                return D(("world", "Main:/"), ("character", HeroPath), ("controller", ""), ("camera_manager", ""),
                    ("game_time", GameTime));
            case "pause":
                Paused = !args.TryGetProperty("paused", out var paused) || paused.GetBoolean();
                return D(("paused", Paused));
            case "session":
                (Pos, Yaw, Opened) = (new Vec(0, 0, 100), 0.0, false);
                Held.Clear();
                return D(("running", true));
            case "key":
                return Key(args);
            case "view":
                return D(("body", D(("sonar", D(("ways", new List<object>()))))));
            case "pose":
                return D(("at", Pos.Ue()), ("eye", EyeOf().Ue()), ("yaw", Geom.Wrap(Yaw)), ("pitch", Pitch),
                    ("velocity", VelocityOf().Ue()));
            case "find":
                return Find(args);
            case "transforms":
                var found = new Dictionary<string, object?>();
                foreach (var item in args.GetProperty("objects").EnumerateArray())
                {
                    var obj = item.GetString()!;
                    found[obj] = D(("location", ActorAt(obj).Ue()), ("rotation", new Vec().Ue()), ("velocity", new Vec().Ue()),
                        ("center", ActorAt(obj).Ue()), ("extent", ExtentOf(obj).Ue()));
                }
                return D(("objects", found));
            case "rc":
                return Rc(args);
            default:
                return D();
        }
    }

    object? Key(JsonElement args)
    {
        var (mode, key) = (Str(args, "mode"), Str(args, "key"));
        Inputs.Add(new Input(key, mode, args.TryGetProperty("seconds", out var s) ? s.GetDouble() : null));
        if (mode == "release_all")
            Held.Clear();
        else if (mode == "hold")
            Held.Add(key!);
        else if (mode == "release")
            Held.Remove(key!);
        else if (key == "MouseX")
            Yaw = Mod(Yaw + 0.1 * Num(args, "value"), 360);
        else if (key == "MouseY")
            Pitch = Math.Max(-89, Math.Min(89, Pitch + 0.1 * Num(args, "value")));
        else if (key == "E" && Pos.Dist2d(Crate) < 150)
            Opened = true;
        return D();
    }

    protected static double Mod(double value, double by) => value - by * Math.Floor(value / by);

    object? Find(JsonElement args)
    {
        var cls = Str(args, "class");
        if (cls is null)
            return D(("objects", new List<string> { CratePath, HeroPath }));
        return D(("objects", cls.Contains("Crate") ? new List<string> { CratePath } : []));
    }

    object? Rc(JsonElement args)
    {
        var body = args.GetProperty("body");
        var (obj, fn, verb) = (Str(body, "objectPath") ?? "", Str(body, "functionName"), Str(args, "verb"));
        if (verb == "describe")
        {
            if (obj.Contains("Crate") || obj.Contains("Hero"))
                return D(("Name", obj), ("Class", "x"));
            throw new GameException(400, $"Object {obj} does not exist");
        }
        if (verb == "property")
        {
            if (obj.EndsWith("Crate_C_0") && Str(body, "propertyName") == "bOpened")
                return D(("bOpened", Opened));
            throw new GameException(400, "no property");
        }
        throw new GameException(400, $"no function {fn}");
    }
}

public sealed class PickingJev(string likes) : IDecider
{
    int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public static JevDecision Answer(JevRequest request, string likes)
    {
        var names = request.Options.Select(o => o.Name).ToList();
        var best = names.Contains(likes) ? likes : names[0];
        var probabilities = names.ToDictionary(n => n, n => n == best ? 0.8 : 0.2 / Math.Max(1, names.Count - 1));
        return new JevDecision(probabilities, 0, 0, 0);
    }

    public Task<JevDecision> DecideAsync(JevRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Answer(request, likes));
    }
}

public class CrateHarness : Harness
{
    public string Crate { get; set; } = "";

    public CrateHarness()
    {
        Goal = "Open the crate";
        Tempo = 2.0;
        TickHz = 30.0;
    }

    public override async Task Setup(Game game) => Crate = (await game.Actors("Crate"))[0];

    public override async Task<Obs> Observe(Game game)
    {
        var (pose, opened) = await game.Batch(() => game.Pose(), () => game.Get(Crate, "bOpened"));
        return new Obs
        {
            ["_pose"] = pose.Value, ["opened"] = opened.Value.Truthy,
            ["crate"] = Geom.Where(pose.Value, await game.Loc(Crate)),
        };
    }

    public override List<Option> Options(Obs obs) =>
    [
        new("open_crate", "Walk to the crate and open it", (g, _) => Control.GoAndPress(g, Crate, "E", within: 120))
        {
            Facts = new() { ["crate"] = obs["crate"] }, Effect = (_, b) => b.Bool("opened"),
        },
        new("wait", "Stand still", TempTests.Ticks(5)),
    ];

    public override Status Status(Obs obs) =>
        obs.Bool("opened") ? new Status("done", "opened", new() { ["opened"] = true }) : new Status();
}

public sealed class LidHarness : Harness
{
    public bool Started { get; set; }

    public bool Lid { get; set; }

    public LidHarness()
    {
        Goal = "Open the lid";
        Tempo = 5.0;
        TickHz = 30.0;
    }

    public override Task Setup(Game game)
    {
        (Started, Lid) = (false, false);
        return Task.CompletedTask;
    }

    public override Task<Obs> Observe(Game game) => Task.FromResult(new Obs { ["opened"] = Lid });

    async Task<Result> OpenLid(Game game, Obs obs)
    {
        Started = true;
        for (var i = 0; i < 10; i++)
            await game.Tick();
        Lid = true;
        return new Result(true, "lid open");
    }

    public override List<Option> Options(Obs obs)
    {
        List<Option> opts = [new("wait", "Stand still", TempTests.Ticks(3))];
        if (!Started)
            opts.Insert(0, new Option("open_lid", "Open the lid", OpenLid));
        return opts;
    }

    public override Status Status(Obs obs) => obs.Bool("opened") ? new Status("done", "opened") : new Status();
}

public sealed class TurningHarness : Harness
{
    double _t0;

    public TurningHarness()
    {
        Goal = "Look around";
        Tempo = 0.0;
        TickHz = 20.0;
    }

    public override Task Setup(Game game)
    {
        _t0 = game.T;
        return Task.CompletedTask;
    }

    public override async Task<Obs> Observe(Game game)
    {
        await game.Pose();
        return new Obs { ["t"] = game.T - _t0 };
    }

    static async Task<Result> Spin(Game game, Obs obs)
    {
        while (true)
        {
            await game.Turn(15, 0);
            await game.Tick();
        }
    }

    public override List<Option> Options(Obs obs) => [new("look_around", "Turn round", Spin) { MaxSeconds = 60 }];

    public override Status Status(Obs obs) => obs.Num("t") > 5.0 ? new Status("done", "looked") : new Status();
}

public sealed class SlowFrameLink : FakeLink
{
    public const double Frame = 0.06;

    public override async ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (op != "key")
            await Task.Delay(TimeSpan.FromSeconds(Frame), ct);
        return await base.RequestAsync(op, args, timeout, ct);
    }

    protected override Vec VelocityOf() => Held.Contains("W") ? Geom.Forward(Yaw) * 400 : new Vec();
}

public sealed class RampLink(bool ways = true) : FakeLink
{
    public List<Vec> Path { get; } = [];

    protected override object? Read(string op, JsonElement args)
    {
        if (op == "view" && Flag(args, "ways_up"))
        {
            var body = D(("sonar", D(("ways", new List<object>()))));
            if (ways)
                body["ways_up"] = new List<object>
                {
                    D(("foot", new Vec(600, -400, 0).Ue()), ("top", new Vec(600, 200, 300).Ue()), ("rises_by", 300)),
                };
            return D(("body", body));
        }
        var answer = base.Read(op, args);
        if (op == "pose")
            Path.Add(Pos);
        return answer;
    }
}

public sealed class BoomLink : FakeLink
{
    public double Speed { get; private set; }

    protected override void Advance()
    {
        var dt = Elapse();
        if (Paused)
            return;
        Pass(dt);
        Speed = Held.Contains("W") ? 400.0 : Math.Max(0.0, Speed - 1500.0 * dt);
        Pos += Geom.Forward(Yaw) * (Speed * dt);
    }

    protected override Vec EyeOf() => Pos + new Vec(0, 0, 60) - Geom.Forward(Yaw, Pitch) * 300;

    protected override Vec VelocityOf() => Geom.Forward(Yaw) * Speed;

    protected override Vec ExtentOf(string obj) => obj.Contains("Crate") ? new Vec(12, 12, 8) : base.ExtentOf(obj);
}

[Collection("wall clock")]
public class KitRuntimeTests : TempTests
{
    readonly FakeLink _link;
    readonly Game _game;

    public KitRuntimeTests()
    {
        _link = new FakeLink();
        _game = Looking(_link);
    }

    [Fact]
    public async Task Walk_to_reaches_target()
    {
        var watch = Stopwatch.StartNew();
        var res = await Drive(_game, ct => Control.WalkTo(_game, new Vec(500, -400, 100), within: 60, timeout: 8, ct: ct));
        Assert.True(res.Ok, res.ToString());
        Assert.True(_link.Pos.Dist2d(new Vec(500, -400)) < 80);
        Assert.DoesNotContain("W", _link.Held);
        Assert.True(watch.Elapsed.TotalSeconds < 6);
    }

    [Fact]
    public async Task Runtime_plays_harness_to_done()
    {
        var jev = new PickingJev("open_crate");
        var path = Tmp("t.jsonl");
        var res = await new Runtime(_game, new CrateHarness(), path, jev, 20).Run();
        Assert.Equal("done", res.Status!.State);
        Assert.True(res.Options["open_crate"].Get("started") >= 1);
        Assert.True(jev.Calls >= 1);
        var text = Runtime.Summarize(path);
        Assert.Contains("open_crate", text);
        Assert.Contains("status=done", text);
    }

    sealed class Broken : CrateHarness
    {
        public override Task<Obs> Observe(Game game) => throw new KeyNotFoundException("nope");
    }

    [Fact]
    public async Task Runtime_reports_harness_errors()
    {
        var path = Tmp("t.jsonl");
        var res = await new Runtime(_game, new Broken(), path, new PickingJev("x"), 5).Run();
        Assert.Contains("observe kept failing", res.Reason);
        Assert.Contains("KeyNotFoundException", Runtime.Summarize(path));
    }

    [Fact]
    public async Task Switch_needs_margin()
    {
        var h = new CrateHarness();
        await h.Setup(_game);
        var rt = new Runtime(_game, h, Tmp("t.jsonl"), maxSeconds: 5);
        var obs = new Obs { ["_pose"] = await _game.Pose(), ["opened"] = false, ["crate"] = new Dictionary<string, object?>() };
        var opts = h.Options(obs);
        rt.Start(opts[0], obs, 0.5);
        Assert.Equal("open_crate", rt.RunningOption);
        await rt.Apply(new Dictionary<string, double> { ["open_crate"] = 0.45, ["wait"] = 0.55 }, [], 0.1, opts, obs, 0.0);
        Assert.Equal("open_crate", rt.RunningOption);
        await rt.Apply(new Dictionary<string, double> { ["open_crate"] = 0.2, ["wait"] = 0.8 }, [], 0.1, opts, obs, 0.0);
        Assert.Equal("wait", rt.RunningOption);
    }

    [Fact]
    public async Task Direct_changes_are_kept_with_the_objects_name()
    {
        const string crate = "Main:/Map/Crate_C_0";
        await _game.Set(crate, "bOpened", true, "start open");
        try
        {
            await _game.Call(crate, "K2_DestroyActor");
        }
        catch (GameException)
        {
        }
        Assert.Equal(
            [("Crate_C_0", "bOpened"), ("Crate_C_0", "K2_DestroyActor")],
            _game.Writes.Select(w => ((string)w["name"]!, (string)(w.GetValueOrDefault("property") ?? w["call"])!)));
    }

    sealed class Reporting(FakeLink link) : CrateHarness
    {
        public override async Task Setup(Game game)
        {
            await base.Setup(game);
            link.Raise(new Dictionary<string, object?>
            {
                ["kind"] = "finding", ["t"] = 1.0,
                ["finding"] = new Dictionary<string, object?> { ["detector"] = "hud_fraction", ["summary"] = "HUD shows 6/5" },
            });
            link.Raise(new Dictionary<string, object?>
            {
                ["kind"] = "log", ["t"] = 1.2, ["verbosity"] = "Error", ["category"] = "LogScript", ["message"] = "Accessed None",
            });
        }
    }

    [Fact]
    public async Task Findings_reach_the_trace()
    {
        var path = Tmp("t.jsonl");
        await new Runtime(_game, new Reporting(_link), path, new PickingJev("open_crate"), 20).Run();
        var text = Runtime.Summarize(path);
        Assert.Contains("HUD shows 6/5", text);
        Assert.Contains("Accessed None", text);
        Assert.Equal(0, _link.Listeners);
        Assert.DoesNotContain("no errors logged", Runtime.Summarize(path, errorLog: true));
    }

    [Fact]
    public async Task Summary_says_when_no_errors_were_logged()
    {
        var path = Tmp("t.jsonl");
        await new Runtime(_game, new CrateHarness(), path, new PickingJev("open_crate"), 20).Run();
        Assert.Contains("no errors logged during this episode", Runtime.Summarize(path, errorLog: true));
        Assert.DoesNotContain("error log", Runtime.Summarize(path));
    }

    sealed class SeeingLink : FakeLink
    {
        protected override object? Read(string op, JsonElement args) => op == "view"
            ? D(("visible", new List<object> { D(("name", "Crate_C_0"), ("distance", 500), ("screen_x", 0.1), ("screen_y", 0.0)) }))
            : base.Read(op, args);
    }

    [Fact]
    public async Task Sees_always_says_whether_the_crosshair_is_on()
    {
        var game = new Game(new SeeingLink());
        Assert.Equal(new Seen(500, 0.1, 0.0, null, false), (await game.Sees([FakeLink.CratePath]))[FakeLink.CratePath]);
    }

    [Fact]
    public async Task A_running_option_the_harness_stops_listing_runs_to_its_end()
    {
        var res = await new Runtime(_game, new LidHarness(), Tmp("t.jsonl"), new PickingJev("open_lid"), 10).Run();
        Assert.Equal("done", res.Status!.State);
        Assert.Equal(1, res.Options["open_lid"].Get("worked"));
        Assert.Equal(0, res.Options["open_lid"].Get("interrupted"));
    }

    [Fact]
    public async Task Jev_can_still_switch_from_an_option_no_longer_listed()
    {
        var h = new LidHarness();
        var rt = new Runtime(_game, h, Tmp("t.jsonl"), new PickingJev("wait"), 2);
        await h.Setup(_game);
        var obs = await h.Observe(_game);
        var opts = h.Options(obs);
        rt.Start(opts[0], obs, 0.5);
        h.Started = true;
        var now = h.Options(obs);
        Assert.Equal(["wait"], now.Select(o => o.Name));
        List<Option> offered = [.. now, opts[0]];
        await rt.Apply(new Dictionary<string, double> { ["wait"] = 0.55, ["open_lid"] = 0.45 }, [], 0.1, offered, obs, 0.0);
        Assert.Equal("open_lid", rt.RunningOption);
        await rt.Apply(new Dictionary<string, double> { ["wait"] = 0.9, ["open_lid"] = 0.1 }, [], 0.1, offered, obs, 0.0);
        Assert.Equal("wait", rt.RunningOption);
    }

    sealed class DeadLink : FakeLink
    {
        protected override object? Read(string op, JsonElement args) =>
            op == "key" && Str(args, "key") is "MouseX" or "MouseY" ? D() : base.Read(op, args);
    }

    [Fact]
    public async Task The_summary_says_when_the_player_stops_answering_its_inputs()
    {
        foreach (var (make, flagged) in new (Func<FakeLink>, bool)[] { (() => new FakeLink(), false), (() => new DeadLink(), true) })
        {
            var link = make();
            var game = Looking(link);
            var path = Tmp($"{link.GetType().Name}.jsonl");
            await new Runtime(game, new TurningHarness(), path, maxSeconds: 10).Run();
            var text = Runtime.Summarize(path);
            Assert.True(text.Contains("DIDN'T ANSWER ITS INPUTS") == flagged, text[..Math.Min(600, text.Length)]);
        }
    }

    [Fact]
    public async Task Probes_after_an_episode_log_as_before()
    {
        var before = _game.Log;
        await new Runtime(_game, new CrateHarness(), Tmp("t.jsonl"), new PickingJev("open_crate"), 5).Run();
        Assert.Same(before, _game.Log);
        _game.Press("E");
    }

    [Fact]
    public async Task Reads_waited_on_count_a_batch_once()
    {
        var crate = (await _game.Actors("Crate"))[0];
        var w0 = _game.Waits();
        await _game.Get(crate, "bOpened");
        await _game.Get(crate, "bOpened");
        Assert.Equal(2, _game.Waits() - w0);
        w0 = _game.Waits();
        await _game.Batch(Enumerable.Range(0, 5).Select(_ => (Func<ValueTask<GameValue>>)(() => _game.Get(crate, "bOpened"))));
        Assert.Equal(1, _game.Waits() - w0);
    }

    sealed class Steps : CrateHarness
    {
        public Steps()
        {
            Goal = "Watch the crate";
            Tempo = 0.0;
        }

        Executor Watcher(bool batched) => async (g, _) =>
        {
            for (var i = 0; i < 40; i++)
            {
                var reads = Enumerable.Range(0, 5).Select(_ => (Func<ValueTask<GameValue>>)(() => g.Get(Crate, "bOpened"))).ToList();
                if (batched)
                {
                    await g.Batch(reads);
                }
                else
                {
                    foreach (var read in reads)
                        await read();
                }
                await g.Tick();
            }
            return null!;
        };

        public override List<Option> Options(Obs obs) =>
        [
            new("read_one_by_one", "Watch the crate", Watcher(false)), new("read_in_a_batch", "Watch the crate", Watcher(true)),
        ];

        public override Status Status(Obs obs) => new();
    }

    [Fact]
    public async Task Summary_flags_steps_that_read_one_after_another()
    {
        foreach (var likes in new[] { "read_one_by_one", "read_in_a_batch" })
        {
            var path = Tmp($"{likes}.jsonl");
            var res = await new Runtime(_game, new Steps(), path, new PickingJev(likes), 3).Run();
            var mean = res.Steps[likes].WaitsMean;
            Assert.True(likes == "read_one_by_one" ? mean is >= 4.5 and <= 5 : mean is >= 0.8 and <= 1, $"{mean}");
            var text = Runtime.Summarize(path);
            Assert.Contains("Executor steps", text);
            Assert.Contains($"  {likes}: ", text);
            Assert.Equal(likes == "read_one_by_one", text.Contains("SLOW STEPS: read_one_by_one"));
        }
    }

    [Fact]
    public async Task Harness_code_can_run_a_controller_to_its_end()
    {
        var game = new Game(new FakeLink());
        var t0 = game.T;
        Assert.True((await Control.Run(game, ct => Control.Wait(game, 0.3, ct))).Ok);
        Assert.True(game.T - t0 >= 0.3);
    }

    sealed class SlowObserve : Harness
    {
        double _t0;

        public int Steps { get; private set; }

        public SlowObserve()
        {
            Goal = "Count steps";
            Tempo = 0.0;
            TickHz = 20.0;
        }

        public override Task Setup(Game game)
        {
            (Steps, _t0) = (0, game.T);
            return Task.CompletedTask;
        }

        public override async Task<Obs> Observe(Game game)
        {
            await game.Pose();
            await Task.Delay(400);
            return new Obs { ["t"] = game.T - _t0 };
        }

        async Task<Result> Count(Game game, Obs obs)
        {
            while (true)
            {
                Steps++;
                await game.Tick();
            }
        }

        public override List<Option> Options(Obs obs) => [new("count", "Count", Count) { MaxSeconds = 60 }];

        public override Status Status(Obs obs) => obs.Num("t") > 3.0 ? new Status("done", "counted") : new Status();
    }

    [Fact]
    public async Task A_slow_observe_doesnt_slow_the_executor()
    {
        var h = new SlowObserve();
        await new Runtime(new Game(new FakeLink()), h, Tmp("t.jsonl"), maxSeconds: 10).Run();
        Assert.True(h.Steps >= 30, $"{h.Steps}");
    }

    [Fact]
    public async Task Hold_for_is_timed_by_the_game_when_the_plugin_can()
    {
        foreach (var (features, timed) in new[] { (new[] { "timed_hold" }, true), ([], false) })
        {
            var link = new FakeLink();
            link.FeatureList.AddRange(features);
            var game = new Game(link);
            await Drive(game, ct => Control.HoldFor(game, "D", 0.2, ct: ct));
            var holds = link.Inputs.Where(i => i is { Key: "D", Mode: "hold" }).ToList();
            Assert.NotEmpty(holds);
            Assert.Equal(timed, holds[0].Seconds == 0.2);
            Assert.False(game.Held().ContainsKey("D"));
        }
    }

    [Fact]
    public async Task Look_at_and_walk_to_work_when_every_read_takes_a_frame()
    {
        var link = new SlowFrameLink();
        var game = Looking(link);
        var r = await Drive(game, ct => Control.LookAt(game, new Vec(0, 3000, 100), tolerance: 1.5, timeout: 0.6, ct: ct));
        Assert.True(r.Ok, r.ToString());
        var target = new Vec(1200, 3000, 100);
        r = await Drive(game, ct => Control.WalkTo(game, target, within: 150, timeout: 20, ct: ct), 30);
        Assert.True(r.Ok, r.ToString());
        Assert.True(link.Pos.Dist2d(target) >= 100, $"{link.Pos.Dist2d(target)}");
    }

    sealed class CountHarness : Harness
    {
        int _n;
        double _t0;

        public CountHarness()
        {
            Goal = "Count";
            Tempo = 0.0;
            TickHz = 20.0;
        }

        public override Task Setup(Game game)
        {
            (_n, _t0) = (0, game.T);
            return Task.CompletedTask;
        }

        public override async Task<Obs> Observe(Game game)
        {
            var seen = _n;
            await game.Pose();
            await game.Clock.Delay(0.3);
            return new Obs { ["n"] = seen, ["t"] = game.T - _t0 };
        }

        async Task<Result> Bump(Game game, Obs obs)
        {
            await game.Tick();
            _n++;
            return null!;
        }

        public override List<Option> Options(Obs obs) =>
            [new("bump", "Add one", Bump) { Effect = (a, b) => b.Num("n") > a.Num("n") }];

        public override Status Status(Obs obs) => obs.Num("t") > 3.0 ? new Status("done", "counted") : new Status();
    }

    [Fact]
    public async Task An_option_is_judged_on_an_observation_begun_after_it_ended()
    {
        var path = Tmp("t.jsonl");
        var res = await new Runtime(new Game(new FakeLink(), PreciseClock.Instance), new CountHarness(), path, maxSeconds: 10).Run();
        var s = res.Options["bump"];
        Assert.True(s.Get("worked") >= 3 && s.Get("failed") == 0,
            string.Join(", ", s) + string.Join(" | ", OfKind(path, "option_end").Select(e => e.GetRawText())));
    }

    sealed class Take : Harness
    {
        int _coins;
        double _t0;

        public int StartsOnTaken { get; private set; }

        public Take()
        {
            Goal = "Take the coins";
            Tempo = 0.0;
            TickHz = 20.0;
        }

        public override Task Setup(Game game)
        {
            (_coins, StartsOnTaken, _t0) = (3, 0, game.T);
            return Task.CompletedTask;
        }

        public override async Task<Obs> Observe(Game game)
        {
            var left = _coins;
            await game.Pose();
            await game.Clock.Delay(0.3);
            return new Obs { ["left"] = left, ["t"] = game.T - _t0 };
        }

        async Task<Result> TakeCoin(Game game, Obs obs)
        {
            if (_coins < obs.Num("left"))
                StartsOnTaken++;
            await game.Tick();
            _coins--;
            return null!;
        }

        public override List<Option> Options(Obs obs) =>
            obs.Num("left") > 0 ? [new("take_coin", "Take a coin", TakeCoin)] : [];

        public override Status Status(Obs obs) => obs.Num("left") == 0 ? new Status("done", "all taken") : new Status();
    }

    [Fact]
    public async Task No_decision_on_a_world_from_before_the_last_option_ended()
    {
        var h = new Take();
        var res = await new Runtime(new Game(new FakeLink()), h, Tmp("t.jsonl"), maxSeconds: 10).Run();
        Assert.Equal("done", res.Status!.State);
        Assert.Equal(0, h.StartsOnTaken);
    }

    [Fact]
    public async Task Walk_to_a_higher_goal_goes_by_a_way_up_it_sees()
    {
        var link = new RampLink();
        var game = Looking(link);
        var r = await Drive(game, ct => Control.WalkTo(game, new Vec(600, 800, 400), within: 100, timeout: 30, ct: ct), 40);
        Assert.True(r.Ok, r.ToString());
        Assert.True(link.Path.Min(p => p.Dist2d(new Vec(600, -400))) < 120);
        Assert.True(link.Path.Min(p => p.Dist2d(new Vec(600, 200))) < 120);
    }

    [Fact]
    public async Task Walk_to_without_ways_up_walks_straight()
    {
        var link = new RampLink(ways: false);
        var game = Looking(link);
        var r = await Drive(game, ct => Control.WalkTo(game, new Vec(600, 800, 400), within: 100, timeout: 30, ct: ct), 40);
        Assert.True(r.Ok, r.ToString());
        Assert.True(link.Path.Min(p => p.Dist2d(new Vec(600, -400))) > 300);
    }

    [Fact]
    public async Task Look_at_puts_a_third_person_crosshair_on_a_small_thing_below()
    {
        var link = new BoomLink { Crate = new Vec(220, 0, 10) };
        var game = Looking(link);
        var r = await Drive(game, ct => Control.LookAt(game, FakeLink.CratePath, timeout: 1.5, ct: ct), 10);
        Assert.True(r.Ok, r.ToString());
        var (right, up) = await game.AimError(link.Crate);
        Assert.True(Math.Abs(up) < 1.0 && Math.Abs(right) < 1.0, $"{right}, {up}");
        Assert.True(link.Pitch < -25);
    }

    [Fact]
    public async Task Walk_to_lets_go_early_by_the_slide_it_measured()
    {
        var link = new BoomLink();
        var game = Looking(link);
        var ends = new List<double>();
        foreach (var target in new[] { new Vec(1500, 0, 100), new Vec(1500, 1500, 100), new Vec(0, 1500, 100) })
        {
            var r = await Drive(game, ct => Control.WalkTo(game, target, within: 150, timeout: 20, ct: ct), 30);
            Assert.True(r.Ok, r.ToString());
            Assert.True(link.Speed < 20.0);
            ends.Add(link.Pos.Dist2d(target));
        }
        Assert.True(game.CoastSeconds > 0.05);
        Assert.True(ends.Skip(1).All(d => d is >= 90 and <= 210), string.Join(", ", ends));
    }

    [Fact]
    public async Task Wait_counts_real_time_while_the_game_clock_stands_still()
    {
        var link = new FakeLink();
        var game = new Game(link);
        link.Paused = true;
        var watch = Stopwatch.StartNew();
        await Drive(game, ct => Control.Wait(game, 1.0, ct), 10);
        Assert.True(watch.Elapsed.TotalSeconds is > 0.9 and < 1.8, $"{watch.Elapsed.TotalSeconds}");
        link.Paused = false;
        await game.Context(fresh: true);
        var g0 = game.T;
        watch.Restart();
        await Drive(game, ct => Control.Wait(game, 1.0, ct), 10);
        Assert.True(game.T - g0 >= 0.95 && watch.Elapsed.TotalSeconds < 1.6, $"{game.T - g0}, {watch.Elapsed.TotalSeconds}");
    }
}
