using System.Text.Json;
using Cluewren.Kit;

namespace Cluewren.Tests;

public sealed class RecordingJev(string likes) : IDecider
{
    public sealed record Call(List<string> Names, JsonElement State, string Instructions);

    readonly List<Call> _calls = [];

    public List<Call> Calls
    {
        get
        {
            lock (_calls)
                return [.. _calls];
        }
    }

    public Task<JevDecision> DecideAsync(JevRequest request, CancellationToken ct)
    {
        using var state = JsonDocument.Parse(request.StateJson());
        lock (_calls)
            _calls.Add(new Call(request.Options.Select(o => o.Name).ToList(), state.RootElement.Clone(), request.Instructions));
        return Task.FromResult(PickingJev.Answer(request, likes));
    }
}

public class LeverHarness : Harness
{
    public int Pulls { get; set; }

    public string Gate { get; set; } = "shut";

    public bool LeverWorks { get; set; } = true;

    public Progress? Resumed { get; private set; }

    public LeverHarness()
    {
        Goal = "Pull the lever and go through the gate";
        Tempo = 0.0;
        TickHz = 30.0;
        StallSeconds = 0.0;
        Plan =
        [
            new Step("lever", "Pull the lever", o => o.Num("pulls") >= 1, ["pull_lever"]),
            new Step("gate", "Go through the gate", o => o.Text("gate") == "open", ["open_gate"]),
        ];
    }

    public override Task Setup(Game game)
    {
        (Pulls, Gate, Resumed, LeverWorks) = (0, "shut", null, true);
        return Task.CompletedTask;
    }

    public override Task Resume(Game game, Progress progress)
    {
        Resumed = progress;
        return Task.CompletedTask;
    }

    public override async Task<Obs> Observe(Game game) =>
        new() { ["_pose"] = await game.Pose(), ["pulls"] = Pulls, ["gate"] = Gate };

    protected async Task<Result> Pull(Game game, Obs obs)
    {
        for (var i = 0; i < 3; i++)
            await game.Tick();
        if (LeverWorks)
            Pulls++;
        return new Result(LeverWorks, LeverWorks ? "pulled" : "jammed");
    }

    async Task<Result> OpenGate(Game game, Obs obs)
    {
        for (var i = 0; i < 3; i++)
            await game.Tick();
        Gate = "open";
        return new Result(true, "pushed");
    }

    public override List<Option> Options(Obs obs) =>
    [
        new("pull_lever", "Pull the lever", Pull) { Effect = (a, b) => b.Num("pulls") > a.Num("pulls") },
        new("open_gate", "Push the gate open", OpenGate) { Effect = (a, b) => b.Text("gate") != a.Text("gate") },
        new("flee", "Run from danger", TempTests.Ticks(2)) { Urgent = true },
    ];

    public override Status Status(Obs obs) => obs.Text("gate") == "open"
        ? new Status("done", "through the gate", new() { ["gate"] = obs["gate"] })
        : new Status();
}

public class StuckHarness : LeverHarness
{
    public StuckHarness() => StallSeconds = 1.0;

    public override async Task Setup(Game game)
    {
        await base.Setup(game);
        LeverWorks = false;
    }

    public override List<Option> Options(Obs obs) => base.Options(obs)[..1];
}

[Collection("wall clock")]
public class PlanTests : TempTests
{
    readonly Game _game = Looking(new FakeLink());

    [Fact]
    public async Task Only_the_current_steps_options_are_offered()
    {
        var jev = new RecordingJev("pull_lever");
        var path = Tmp("t.jsonl");
        var res = await new Runtime(_game, new LeverHarness(), path, jev, 10).Run();
        Assert.Equal("done", res.Status!.State);
        Assert.Equal(1, res.Options["pull_lever"].Get("started"));
        Assert.Equal(1, res.Options["open_gate"].Get("worked"));
        var offered = jev.Calls.Select(c => string.Join(",", c.Names)).ToList();
        Assert.Equal("pull_lever,flee", offered[0]);
        Assert.Contains("open_gate,flee", offered);
        Assert.DoesNotContain("pull_lever,open_gate,flee", offered);
        var steps = OfKind(path, "step").Select(e => (Name(e, "from"), Name(e, "to"))).ToList();
        Assert.Equal([(null, "lever"), ("lever", "gate"), ("gate", null)], steps);
        Assert.Equal(["lever", "gate"], res.Progress.Done);
        Assert.Null(res.Progress.Step);
        Assert.Null(res.Progress.Stalled);
        Assert.True(res.Progress.T > 0);
    }

    static string? Name(JsonElement e, string field) => e.GetProperty(field).GetString();

    static List<string?> Strings(JsonElement list) => list.EnumerateArray().Select(item => item.GetString()).ToList();

    [Fact]
    public async Task Jev_is_told_where_the_plan_stands()
    {
        var jev = new RecordingJev("pull_lever");
        await new Runtime(_game, new LeverHarness(), Tmp("t.jsonl"), jev, 10).Run();
        var first = jev.Calls[0].State.GetProperty("plan");
        Assert.Empty(Strings(first.GetProperty("done")));
        Assert.Equal(["Go through the gate"], Strings(first.GetProperty("next")));
        var now = first.GetProperty("now");
        Assert.Equal("lever", Name(now, "step"));
        Assert.Equal("Pull the lever", Name(now, "what"));
        Assert.True(now.GetProperty("for_seconds").GetDouble() >= 0);
        var later = jev.Calls.First(c => c.Names[0] == "open_gate").State.GetProperty("plan");
        var seconds = later.GetProperty("now").GetProperty("for_seconds").GetRawText();
        Assert.Equal($$"""{"done":["lever"],"now":{"step":"gate","what":"Go through the gate","for_seconds":{{seconds}}},"next":[]}""",
            later.GetRawText());
        Assert.Contains("plan.now", jev.Calls[0].Instructions);
    }

    [Fact]
    public async Task A_plan_that_doesnt_advance_ends_the_episode_as_stalled()
    {
        var path = Tmp("t.jsonl");
        var res = await new Runtime(_game, new StuckHarness(), path, maxSeconds: 10).Run();
        Assert.Equal("stalled", res.Reason);
        Assert.Equal("running", res.Status!.State);
        var stalled = res.Progress.Stalled!;
        Assert.Equal("lever", stalled.Step);
        Assert.True(stalled.Seconds >= 1.0);
        var tried = stalled.Tried["pull_lever"];
        Assert.True(tried["started"] >= 1 && tried["failed"] >= 1);
        Assert.Equal(0, tried.GetValueOrDefault("worked"));
        Assert.Equal(0, stalled.Player!.Moved);
        Assert.Equal([0, 0, 100], stalled.Player.At);
        Assert.Equal([0, 0, 100], stalled.Player.Now);
        Assert.Empty(res.Progress.Done);
        Assert.Equal("lever", res.Progress.Step);
        var end = OfKind(path, "end").First();
        Assert.Equal("lever", end.GetProperty("progress").GetProperty("stalled").GetProperty("step").GetString());
        Assert.NotEmpty(OfKind(path, "stalled"));
    }

    sealed class NoPlan : StuckHarness
    {
        readonly bool _works;

        public NoPlan(bool works)
        {
            _works = works;
            Plan = [];
        }

        public override async Task Setup(Game game)
        {
            await base.Setup(game);
            LeverWorks = _works;
        }

        public override Status Status(Obs obs) => obs.Num("pulls") >= 4 ? new Status("done", "enough") : new Status();
    }

    [Fact]
    public async Task An_option_that_works_is_progress_without_a_plan()
    {
        var res = await new Runtime(_game, new NoPlan(true), Tmp("t.jsonl"), maxSeconds: 10).Run();
        Assert.Equal("status done", res.Reason);
        Assert.Empty(res.Progress.Done);
        Assert.Null(res.Progress.Step);
        res = await new Runtime(_game, new NoPlan(false), Tmp("t2.jsonl"), maxSeconds: 10).Run();
        Assert.Equal("stalled", res.Reason);
        Assert.Null(res.Progress.Stalled!.Step);
    }

    [Fact]
    public async Task The_next_episode_continues_from_the_progress()
    {
        var first = await new Runtime(_game, new StuckHarness(), Tmp("t1.jsonl"), maxSeconds: 10).Run();
        var progress = new Progress
        {
            Episode = 1, Done = ["lever"], Step = first.Progress.Step, T = first.Progress.T,
            Stalled = first.Progress.Stalled, LastFacts = first.Progress.LastFacts,
        };
        var h = new LeverHarness();
        var jev = new RecordingJev("pull_lever");
        var path = Tmp("t2.jsonl");
        var res = await new Runtime(_game, h, path, jev, 10, progress: progress).Run();
        Assert.Same(progress, h.Resumed);
        Assert.Equal("done", res.Status!.State);
        Assert.False(res.Options.ContainsKey("pull_lever"));
        Assert.Equal(["lever"], Strings(jev.Calls[0].State.GetProperty("plan").GetProperty("done")));
        Assert.Equal(["open_gate", "flee"], jev.Calls[0].Names);
        var continued = OfKind(path, "continued").First().GetProperty("progress");
        Assert.Equal(1, continued.GetProperty("episode").GetInt32());
        Assert.Equal("lever", continued.GetProperty("stalled").GetProperty("step").GetString());
        Assert.Equal(["lever"], Strings(OfKind(path, "plan").First().GetProperty("done")));
        Assert.Equal(["lever", "gate"], res.Progress.Done);
        var text = Runtime.Summarize(path);
        Assert.Contains("1. lever: done before this episode", text);
        Assert.Contains("2. gate: done at t=", text);
    }

    [Fact]
    public async Task The_summary_shows_the_plan_and_the_stall()
    {
        var path = Tmp("t.jsonl");
        await new Runtime(_game, new StuckHarness(), path, maxSeconds: 10).Run();
        var text = Runtime.Summarize(path);
        Assert.Contains("Plan (the steps in order):", text);
        Assert.Contains("1. lever: not reached  <- the current step at the end  (Pull the lever)", text);
        Assert.Contains("2. gate: not reached  (Go through the gate)", text);
        Assert.Contains("STALLED at step lever for", text);
        Assert.Contains("tried: pull_lever started", text);
        Assert.Contains("the player moved 0 units (from [0,0,100] to [0,0,100])", text);
        Assert.Contains("run_harness(restart=False)", text);
        Assert.Contains("Ended: stalled", text);
        var done = Tmp("done.jsonl");
        await new Runtime(_game, new LeverHarness(), done, new RecordingJev("pull_lever"), 10).Run();
        text = Runtime.Summarize(done);
        Assert.Contains("1. lever: done at t=", text);
        Assert.Contains("2. gate: done at t=", text);
        Assert.DoesNotContain("STALLED", text);
    }

    sealed class Mismatch : LeverHarness
    {
        public Mismatch() => Plan =
        [
            new Step("lever", "Pull the lever", o => o.Num("pulls") >= 1, ["find_lever"]),
            new Step("gate", "Go through the gate", o => ((Dictionary<string, object?>)o["door"]!)["open"] is true),
        ];

        public override Status Status(Obs obs) => obs.Num("pulls") >= 1 ? new Status("done", "pulled") : new Status();
    }

    [Fact]
    public async Task A_step_naming_options_that_arent_legal_offers_all()
    {
        var jev = new RecordingJev("pull_lever");
        var path = Tmp("t.jsonl");
        var res = await new Runtime(_game, new Mismatch(), path, jev, 10).Run();
        Assert.Equal("done", res.Status!.State);
        Assert.Equal(["pull_lever", "open_gate", "flee"], jev.Calls[0].Names);
        var mismatch = Assert.Single(OfKind(path, "plan_mismatch"));
        Assert.Equal("lever", mismatch.GetProperty("step").GetString());
        Assert.Equal(["find_lever"], Strings(mismatch.GetProperty("listed")));
        var error = Assert.Single(OfKind(path, "error"), e => e.GetProperty("where").GetString() == "plan step gate done()");
        Assert.Contains("KeyNotFoundException", error.GetProperty("error").GetString());
        Assert.Equal(["lever"], res.Progress.Done);
        Assert.Equal("gate", res.Progress.Step);
        Assert.Contains("PLAN MISMATCH at step lever", Runtime.Summarize(path));
    }

    [Fact]
    public async Task Check_tries_the_current_steps_options()
    {
        var text = await Runtime.Check(_game, new LeverHarness(), seconds: 3, restart: false);
        Assert.Contains("setup() ran.", text);
        Assert.Contains("""options legal now: ["pull_lever","open_gate","flee"]""", text);
        Assert.Contains("plan: at step 1 of 2, lever (Pull the lever); done: []", text);
        Assert.Contains("""tried (the current step's options): ["pull_lever","flee"]""", text);
        Assert.Contains("\npull_lever: ok", text);
        Assert.Contains("effect=true", text);
        Assert.Contains("\"pulls\":[0,1]", text);
        Assert.Contains("""plan moved on: at step 2 of 2, gate (Go through the gate); done: ["lever"]""", text);
        Assert.Contains("""legal now as well: ["open_gate"] (tried next)""", text);
        Assert.Contains("\nopen_gate: ok", text);
        Assert.Contains("status is done now", text);
        Assert.True(text.IndexOf("pull_lever: ok", StringComparison.Ordinal) < text.IndexOf("\nflee: ok", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\nflee: ok", StringComparison.Ordinal) < text.IndexOf("open_gate: ok", StringComparison.Ordinal));
    }

    sealed class NoPlanLever : LeverHarness
    {
        public NoPlanLever() => Plan = [];
    }

    [Fact]
    public async Task Check_without_a_plan_tries_every_option()
    {
        var text = await Runtime.Check(_game, new NoPlanLever(), seconds: 3, restart: false);
        Assert.DoesNotContain("plan:", text);
        Assert.Contains("\npull_lever: ok", text);
        Assert.Contains("\nopen_gate: ok", text);
        text = await Runtime.Check(_game, new LeverHarness(), seconds: 3, restart: false, only: ["open_gate"]);
        Assert.Contains("\nopen_gate: ok", text);
        Assert.DoesNotContain("pull_lever: ok", text);
    }

    sealed class Loop : LeverHarness
    {
        public Loop()
        {
            StallSeconds = 0.0;
            StepSeconds = 1.0;
            Plan =
            [
                new Step("lever", "Pull the lever a hundred times", o => o.Num("pulls") >= 100, ["pull_lever"]),
                new Step("gate", "Go through the gate", o => o.Text("gate") == "open", ["open_gate"]),
            ];
        }

        public override List<Option> Options(Obs obs) => base.Options(obs)[..1];
    }

    [Fact]
    public async Task A_step_that_takes_too_long_though_options_work_is_a_stall()
    {
        var path = Tmp("t.jsonl");
        var res = await new Runtime(_game, new Loop(), path, maxSeconds: 10).Run();
        Assert.Equal("stalled", res.Reason);
        var stalled = res.Progress.Stalled!;
        Assert.Equal("step", stalled.Kind);
        Assert.Equal("lever", stalled.Step);
        Assert.True(stalled.Seconds >= 1.0);
        Assert.True(stalled.Tried["pull_lever"]["worked"] >= 1);
        var text = Runtime.Summarize(path);
        Assert.Contains("STALLED at step lever", text);
        Assert.Contains("though options worked", text);
    }
}
