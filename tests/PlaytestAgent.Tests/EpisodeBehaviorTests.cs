using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlaytestAgent.Jev;
using PlaytestAgent.Kit;
using PlaytestAgent.Tests.Sim;

namespace PlaytestAgent.Tests;

[Collection("wall clock")]
public class EpisodeBehaviorTests : TempTests
{
    sealed class FakeGame(TimeSpan delay = default) : SimLink
    {
        readonly long _start = Stopwatch.GetTimestamp();
        int _keys;
        int _releases;

        public int Keys => Volatile.Read(ref _keys);

        public int Releases => Volatile.Read(ref _releases);

        void Stamp() => GameTime = Stopwatch.GetElapsedTime(_start).TotalSeconds;

        public override async ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null,
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            await Task.Delay(delay, ct);
            return await base.RequestAsync(op, args, timeout, ct);
        }

        protected override object? Answer(string op, JsonElement args)
        {
            Stamp();
            if (op == "key")
            {
                if (Str(args, "mode") == "release_all")
                    Interlocked.Increment(ref _releases);
                else
                    Interlocked.Increment(ref _keys);
                return new Dictionary<string, object?>();
            }
            if (op == "pose")
                return new Dictionary<string, object?> { ["at"] = new Vec().Ue() };
            return new Dictionary<string, object?> { ["character"] = "Main:/Hero" };
        }
    }

    sealed class ScriptedHarness : Harness
    {
        readonly Func<int, Status> _status;
        readonly Func<List<Option>> _options;
        int _seen;

        public ScriptedHarness(Func<int, Status> status, Func<List<Option>> options)
        {
            (_status, _options) = (status, options);
            TickHz = 500;
        }

        public override async Task<Obs> Observe(Game game)
        {
            await game.Pose();
            return new Obs();
        }

        public override List<Option> Options(Obs obs) => _options();

        public override Status Status(Obs obs) => _status(++_seen);
    }

    sealed class TempoHarness : Harness
    {
        readonly Func<List<Option>> _options;

        public TempoHarness(Func<List<Option>> options)
        {
            _options = options;
            TickHz = 200;
            Tempo = 50;
        }

        public override async Task<Obs> Observe(Game game)
        {
            await game.Pose();
            return new Obs();
        }

        public override List<Option> Options(Obs obs) => _options();

        public override Status Status(Obs obs) => new();
    }

    sealed class FakeDecider(Func<JevRequest, IReadOnlyDictionary<string, double>> pick, TimeSpan delay = default) : IDecider
    {
        public readonly List<JevRequest> Requests = [];

        public async Task<JevDecision> DecideAsync(JevRequest request, CancellationToken ct)
        {
            lock (Requests)
                Requests.Add(request);
            await Task.Delay(delay, ct);
            return new JevDecision(pick(request), 100, 2, 1000);
        }
    }

    sealed class FailingDecider : IDecider
    {
        public Task<JevDecision> DecideAsync(JevRequest request, CancellationToken ct) =>
            Task.FromException<JevDecision>(new JevException("down"));
    }

    const double Limit = 10;
    const double Short = 0.4;

    Task<EpisodeResult> Play(Harness harness, IGameLink link, double seconds, IDecider? client = null) =>
        new Runtime(new Game(link), harness, Tmp("t.jsonl"), client, seconds).Run();

    static List<Option> OnePress() => [new("press", "press E", (g, _) => Control.Press(g, "E"))];

    static Option Runs(string name, Action? onStart = null, bool urgent = false, bool interruptible = true) =>
        new(name, name, async (g, _) =>
        {
            onStart?.Invoke();
            while (true)
                await g.Tick();
        })
        { Urgent = urgent, Interruptible = interruptible };

    static Option Counts(string name, Action onStep) =>
        new(name, name, async (g, _) =>
        {
            while (true)
            {
                onStep();
                await g.Tick();
            }
        });

    static Option Fails(string name, Action? onStart = null) =>
        new(name, name, (_, _) =>
        {
            onStart?.Invoke();
            return Task.FromResult(new Result(false, "failed"));
        });

    static Dictionary<string, double> Picks(double a, double b) => new() { ["a"] = a, ["b"] = b };

    [Fact]
    public async Task The_only_legal_option_runs_without_a_policy()
    {
        var game = new FakeGame();
        var harness = new ScriptedHarness(n => n >= 3 ? new Status("done", "seen") : new Status(), OnePress);
        var result = await Play(harness, game, Limit);
        Assert.Equal("done", result.Status!.State);
        Assert.True(game.Keys >= 1);
    }

    [Fact]
    public async Task Two_legal_options_stop_the_run_rather_than_guess()
    {
        var harness = new ScriptedHarness(_ => new Status(), () => [Runs("a"), Runs("b")]);
        var result = await Play(harness, new FakeGame(), Limit);
        Assert.Equal("failed", result.Status!.State);
        Assert.Equal(Runtime.NoPolicy, result.Reason);
        Assert.Contains("no policy", result.Status.Note);
    }

    [Fact]
    public async Task A_time_limit_keeps_the_last_evidence()
    {
        var harness = new ScriptedHarness(_ => new Status(Status.Running, "", new { air_jumps = 2 }), () => []);
        var result = await Play(harness, new FakeGame(), 0.05);
        Assert.Equal("time limit", result.Status!.Note);
        Assert.Contains("air_jumps", Encoding.UTF8.GetString(Jsonable.Encode(result.Status.Evidence)));
    }

    [Fact]
    public async Task Every_end_releases_held_input()
    {
        var game = new FakeGame();
        var harness = new ScriptedHarness(_ => new Status(), () => []);
        var result = await Play(harness, game, 0.05);
        Assert.Equal("inconclusive", result.Status!.State);
        Assert.Equal("time limit", result.Status.Note);
        Assert.Equal("time limit", result.Reason);
        Assert.Equal(1, game.Releases);
    }

    [Fact]
    public async Task Slow_observation_does_not_stall_controller_steps()
    {
        var steps = 0;
        var harness = new ScriptedHarness(_ => new Status(), () => [Counts("a", () => steps++)]);
        await Play(harness, new FakeGame(TimeSpan.FromMilliseconds(100)), 0.3);
        Assert.True(steps > 10, $"only {steps} steps ran");
    }

    [Fact]
    public async Task Stats_report_ticks_and_latency()
    {
        var harness = new ScriptedHarness(n => n >= 5 ? new Status("done") : new Status(), OnePress);
        var result = await Play(harness, new FakeGame(TimeSpan.FromMilliseconds(1)), Limit);
        Assert.True(result.Stats!.Ticks > 0);
        Assert.True(result.Stats.ObserveRoundTrip.Count > 0);
        Assert.True(result.Stats.Step.Count > 0);
    }

    [Fact]
    public async Task Jev_picks_the_most_likely_option()
    {
        var started = new List<string>();
        var harness = new ScriptedHarness(_ => new Status(), () => [Runs("a", () => started.Add("a")), Runs("b", () => started.Add("b"))]);
        var decider = new FakeDecider(_ => Picks(0.2, 0.8));
        var result = await Play(harness, new FakeGame(), Short, decider);
        Assert.Equal("b", Assert.Single(started));
        Assert.True(result.Stats!.Decisions.Answered >= 1);
        Assert.Equal(100 * result.Stats.Decisions.Answered, result.Stats.Decisions.InputTokens);
    }

    [Fact]
    public async Task A_small_preference_does_not_stop_the_running_option()
    {
        var starts = 0;
        var harness = new TempoHarness(() => [Runs("a", () => starts++), Runs("b", () => starts += 100)]);
        var calls = 0;
        var decider = new FakeDecider(_ => Interlocked.Increment(ref calls) == 1 ? Picks(0.9, 0.1) : Picks(0.45, 0.55));
        var result = await Play(harness, new FakeGame(), Short, decider);
        Assert.Equal(1, starts);
        Assert.True(result.Stats!.Decisions.Held > 0);
    }

    [Fact]
    public async Task A_clear_preference_switches_and_is_remembered_as_stopped()
    {
        var harness = new TempoHarness(() => [Runs("a"), Runs("b")]);
        var calls = 0;
        var decider = new FakeDecider(_ => Interlocked.Increment(ref calls) == 1 ? Picks(0.9, 0.1) : Picks(0.1, 0.9));
        var result = await Play(harness, new FakeGame(), Short, decider);
        Assert.True(result.Stats!.Decisions.Switched >= 1);
        var stopped = Assert.Single(decider.Requests.Last(r => r.Recent.Count > 0).Recent);
        Assert.Equal("a", stopped.Option);
        Assert.Null(stopped.Worked);
    }

    [Fact]
    public async Task An_urgent_option_ignores_the_margin()
    {
        var harness = new TempoHarness(() => [Runs("a"), Runs("b", urgent: true)]);
        var calls = 0;
        var decider = new FakeDecider(_ => Interlocked.Increment(ref calls) == 1 ? Picks(0.9, 0.1) : Picks(0.49, 0.51));
        var result = await Play(harness, new FakeGame(), Short, decider);
        Assert.Equal(1, result.Stats!.Decisions.Switched);
    }

    [Fact]
    public async Task An_uninterruptible_option_is_never_switched_away()
    {
        var harness = new TempoHarness(() => [Runs("a", interruptible: false), Runs("b", urgent: true)]);
        var calls = 0;
        var decider = new FakeDecider(_ => Interlocked.Increment(ref calls) == 1 ? Picks(0.9, 0.1) : Picks(0.0, 1.0));
        var result = await Play(harness, new FakeGame(), Short, decider);
        Assert.Equal(0, result.Stats!.Decisions.Switched);
    }

    [Fact]
    public async Task A_slow_decision_never_holds_a_controller_back()
    {
        var steps = 0;
        var option = Counts("a", () => steps++);
        var harness = new TempoHarness(() => [option, Runs("b")]);
        var decider = new FakeDecider(_ => new Dictionary<string, double> { ["a"] = 1 }, TimeSpan.FromMilliseconds(60));
        await Play(harness, new FakeGame(), Short, decider);
        Assert.True(steps >= 15, $"only {steps} steps ran");
    }

    [Fact]
    public async Task An_option_failing_at_once_is_held_back()
    {
        var starts = 0;
        var failing = Fails("a", () => starts++);
        var harness = new TempoHarness(() => [failing]);
        var result = await Play(harness, new FakeGame(), 0.3);
        Assert.Equal(1, starts);
        Assert.Equal(1, result.Options["a"].Get("failed"));
        Assert.Equal(1, result.Options["a"].Get("held_back"));
    }

    [Fact]
    public async Task The_next_choice_sees_how_the_last_option_ended()
    {
        var harness = new ScriptedHarness(_ => new Status(), () => [Fails("a") with { Effect = (_, _) => true }, Runs("b")]);
        var decider = new FakeDecider(_ => Picks(0.9, 0.1));
        await Play(harness, new FakeGame(), Short, decider);
        var later = decider.Requests.First(r => r.Recent.Count > 0);
        Assert.True(later.Recent[0].Worked);
    }

    [Fact]
    public async Task Repeated_jev_failures_end_the_run()
    {
        var harness = new ScriptedHarness(_ => new Status(), () => [Runs("a"), Runs("b")]);
        var result = await Play(harness, new FakeGame(), Limit, new FailingDecider());
        Assert.Equal("failed", result.Status!.State);
        Assert.Contains("Jev failed", result.Status.Note);
        Assert.Equal("down", result.Stats!.Decisions.LastError);
    }

    sealed class Hung(Func<Game, Task>? setup = null, Func<Game, Task<Obs>>? observe = null) : Harness
    {
        public override Task Setup(Game game) => setup?.Invoke(game) ?? Task.CompletedTask;

        public override Task<Obs> Observe(Game game) => observe?.Invoke(game) ?? Task.FromResult(new Obs());
    }

    sealed class SilentLink : SimLink
    {
        public override async ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null,
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("never answered");
        }

        protected override object? Answer(string op, JsonElement args) => new Dictionary<string, object?>();
    }

    async Task AssertCutOff(Harness harness, IGameLink link)
    {
        var watch = Stopwatch.StartNew();
        var result = await new Runtime(new Game(link), harness, Tmp("t.jsonl"), maxSeconds: 120, deadlineSeconds: 0.2).Run()
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("inconclusive", result.Status!.State);
        Assert.Equal("time limit", result.Status.Note);
        Assert.Equal("time limit", result.Reason);
        Assert.True(watch.Elapsed.TotalSeconds < 15, $"{watch.Elapsed.TotalSeconds}");
    }

    [Fact]
    public async Task An_option_stopped_in_a_read_ends_though_it_passed_no_token()
    {
        var game = new Game(new SilentLink());
        var stepper = new Stepper(game, async _ =>
        {
            await game.Pose();
            return new Result(true);
        });
        _ = stepper.Step().AsTask();
        Assert.True(await stepper.Stop(patience: 2));
    }

    [Fact]
    public Task The_deadline_cuts_off_a_setup_waiting_on_the_game() =>
        AssertCutOff(new Hung(setup: async game => await game.Pose()), new SilentLink());

    [Fact]
    public Task The_deadline_cuts_off_a_setup_that_never_ends() =>
        AssertCutOff(new Hung(setup: _ => new TaskCompletionSource().Task), new FakeGame());

    [Fact]
    public Task The_deadline_cuts_off_an_observe_waiting_on_the_game() =>
        AssertCutOff(new Hung(observe: async game =>
        {
            await game.Pose();
            return new Obs();
        }), new SilentLink());

    [Fact]
    public Task The_deadline_cuts_off_an_observe_that_never_ends() =>
        AssertCutOff(new Hung(observe: async _ =>
        {
            await new TaskCompletionSource().Task;
            return new Obs();
        }), new FakeGame());

    sealed class Holding : Harness
    {
        public Holding()
        {
            TickHz = 100;
            Tempo = 20;
        }

        public override async Task<Obs> Observe(Game game)
        {
            await game.Pose();
            return new Obs();
        }

        static async Task<Result> Hold(Game game, Obs obs)
        {
            game.Hold("W");
            while (true)
                await game.Tick();
        }

        public override List<Option> Options(Obs obs) =>
            [new("hold", "Hold W", Hold), new("other", "Stand", Ticks(1000))];
    }

    sealed class BlockingDecider : IDecider
    {
        int _calls;

        public TaskCompletionSource Cancelled { get; } = new();

        public async Task<JevDecision> DecideAsync(JevRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return PickingJev.Answer(request, "hold");
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("never answered");
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    [Theory]
    [InlineData(0.5, null)]
    [InlineData(120.0, 0.5)]
    public async Task The_end_releases_held_input_and_cancels_jev_in_flight(double maxSeconds, double? deadline)
    {
        var link = new FakeLink();
        var game = new Game(link);
        var decider = new BlockingDecider();
        var result = await new Runtime(game, new Holding(), Tmp("t.jsonl"), decider, maxSeconds, deadlineSeconds: deadline).Run();
        Assert.Equal("inconclusive", result.Status!.State);
        Assert.Equal("time limit", result.Reason);
        Assert.Contains(link.Inputs, i => i is { Key: "W", Mode: "hold" });
        Assert.Equal(new Input(null, "release_all", null), link.Inputs[^1]);
        Assert.Empty(link.Held);
        Assert.Empty(game.Held());
        Assert.True(decider.Cancelled.Task.IsCompleted, "the question in flight wasn't cancelled");
    }

    [Fact]
    public async Task The_decision_limit_ends_the_episode_inconclusive()
    {
        var game = new Game(new FakeLink());
        var harness = new Holding { Tempo = 50 };
        var result = await new Runtime(game, harness, Tmp("t.jsonl"), new PickingJev("hold"), 30, maxDecisions: 3).Run();
        Assert.Equal("inconclusive", result.Status!.State);
        Assert.Equal("decision limit", result.Status.Note);
        Assert.Equal("decision limit", result.Reason);
        Assert.Equal(3, result.Stats!.Decisions.Asked);
    }
}
