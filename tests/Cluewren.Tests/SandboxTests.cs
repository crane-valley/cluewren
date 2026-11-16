using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Cluewren.Decisions;
using Cluewren.Kit;
using Cluewren.Sandbox;
using Cluewren.Tests.Sim;

namespace Cluewren.Tests;

sealed class StillGame : SimLink
{
    public readonly List<string> Ops = [];
    public double Time = 1;
    public Vec Pos = new(0, 0, 90);
    public double Yaw;
    public bool Paused;
    public bool W;

    protected override object? Answer(string op, JsonElement args)
    {
        lock (Ops)
        {
            Ops.Add(op);
            Time += 0.02;
            GameTime = Time;
            if (W)
                Pos += Geom.Forward(Yaw) * 8;
        }
        switch (op)
        {
            case "pause":
                Paused = Flag(args, "paused");
                return new Dictionary<string, object?> { ["paused"] = Paused };
            case "context":
                return new Dictionary<string, object?> { ["world"] = "Main", ["character"] = "Main:/Player", ["controller"] = "" };
            case "session":
                return new Dictionary<string, object?> { ["running"] = true };
            case "pose":
                return new Dictionary<string, object?>
                {
                    ["at"] = Pos.Ue(), ["eye"] = (Pos + new Vec(0, 0, 60)).Ue(), ["yaw"] = Yaw, ["pitch"] = 0.0,
                    ["velocity"] = new Vec().Ue(), ["on_ground"] = true,
                };
            case "key":
                if (Str(args, "key") == "MouseX")
                    Yaw = Geom.Wrap(Yaw + 0.1 * Num(args, "value"));
                else if (Str(args, "key") == "W")
                    W = Str(args, "mode") == "hold";
                else if (Str(args, "mode") == "release_all")
                    W = false;
                return new Dictionary<string, object?>();
            case "view":
                return new Dictionary<string, object?>
                {
                    ["visible"] = new List<object?>(),
                    ["body"] = new Dictionary<string, object?>
                    {
                        ["sonar"] = new Dictionary<string, object?> { ["ways"] = new List<object?>() },
                    },
                };
            case "screen":
                return new Dictionary<string, object?> { ["texts"] = new Dictionary<string, object?>() };
            case "rc":
                return new Dictionary<string, object?>();
            default:
                throw new GameException(400, $"unknown op {op}");
        }
    }
}

sealed class CannedJev(string pick) : IDecisionRelay
{
    public int Calls;
    public string? LastBody;

    public Task<(byte[] Body, long LatencyTicks)> DecideRawAsync(JsonElement stateAndQuestions, CancellationToken ct)
    {
        Calls++;
        LastBody = stateAndQuestions.GetRawText();
        var names = stateAndQuestions.GetProperty("questions").GetProperty("pick").GetProperty("criteria").EnumerateObject()
            .Select(p => p.Name).ToList();
        var probabilities = names.ToDictionary(n => n, n => n == pick ? 0.9 : 0.1 / Math.Max(1, names.Count - 1));
        var answer = new Dictionary<string, object?>
        {
            ["answers"] = new Dictionary<string, object?>
            {
                ["pick"] = new Dictionary<string, object?>
                {
                    ["type"] = "choice", ["choice"] = pick, ["confidence"] = 0.9, ["probabilities"] = probabilities,
                },
            },
            ["usage"] = new Dictionary<string, object?> { ["input_tokens"] = 100, ["output_tokens"] = 2 },
        };
        return Task.FromResult((Jsonable.Encode(answer, round: false), 1000L));
    }
}

sealed class FixedJev(string? answer) : IDecisionRelay
{
    int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<(byte[] Body, long LatencyTicks)> DecideRawAsync(JsonElement stateAndQuestions, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return answer is null
            ? Task.FromException<(byte[], long)>(new DecisionException("HTTP 503"))
            : Task.FromResult((System.Text.Encoding.UTF8.GetBytes(answer), 1000L));
    }
}

[CollectionDefinition("sandbox", DisableParallelization = true)]
public class SandboxCollection;

[Collection("sandbox")]
public sealed class SandboxTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "playtest-sandbox-" + Guid.NewGuid().ToString("N"));
    readonly StillGame _game = new();

    public SandboxTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    HarnessHost Host(IDecisionRelay? jev = null, long memory = 2L << 30, SandboxMode mode = SandboxMode.Contained,
        IGameLink? link = null, IEpisodeRecorder? recorder = null) =>
        new(new HostOptions(link ?? _game, Path.Combine(_root, "run"))
        {
            Decider = jev, MemoryLimitBytes = memory, Mode = mode, Say = _ => { }, Recorder = recorder,
        });

    static string Literal(string text) => "@\"" + text.Replace("\"", "\"\"") + "\"";

    static string Attempt(string action) => $$"""
        try { {{action}} Console.WriteLine("ALLOWED"); }
        catch (Exception e) { Console.WriteLine("REFUSED " + e.GetType().Name); }
        """;

    async Task<string> Probe(HarnessHost host, string code, double seconds = 30)
    {
        var result = await host.ProbeAsync(code, seconds: seconds);
        Assert.True(result.Compiled, result.Text);
        return result.Text;
    }

    [Fact]
    public async Task The_child_runs_in_an_app_container_and_returns_console_output_and_its_last_value()
    {
        await using var host = Host();
        var text = await Probe(host, """
            Console.WriteLine("hello from the child");
            (await game.Pose()).At.Z + 1
            """);
        Assert.Equal("appcontainer", host.Sandbox);
        Assert.Contains("hello from the child", text);
        Assert.Contains("91", text);
        Assert.Contains("pause", _game.Ops);
    }

    [Fact]
    public async Task A_probe_that_fails_returns_its_exception()
    {
        await using var host = Host();
        var text = await Probe(host, """
            Console.WriteLine("before");
            await game.Request("find");
            """);
        Assert.Contains("before", text);
        Assert.Contains("GameException", text);
        Assert.Contains("unknown op find", text);
    }

    [Fact]
    public async Task A_probe_that_asks_what_harness_code_may_not_is_refused_and_goes_on()
    {
        await using var host = Host();
        var text = await Probe(host, """
            foreach (var (op, action) in new[] { ("record", "start"), ("session", "stop"), ("timestep", "") })
            {
                try { await game.Request(op, json => json.WriteString("action", action)); Console.WriteLine($"{op} ALLOWED"); }
                catch (GameException e) { Console.WriteLine($"{op} {e.Status} {e.Message}"); }
            }
            (await game.Pose()).At.Z + 1
            """);
        foreach (var op in (string[])["record", "session", "timestep"])
            Assert.Contains($"{op} 403", text);
        Assert.Contains("record: record is refused: harness code may not send it", text);
        Assert.Contains("91", text);
        lock (_game.Ops)
            Assert.DoesNotContain(_game.Ops, op => op is "record" or "session" or "timestep");
    }

    [Fact]
    public async Task A_probe_that_will_not_compile_says_where()
    {
        await using var host = Host();
        var result = await host.ProbeAsync("var x = ;\nint y = \"no\";");
        Assert.False(result.Compiled);
        Assert.Contains(result.Problems, p => p.File == "probe.cs" && p.Line == 1);
    }

    [Fact]
    public async Task A_harness_that_will_not_compile_says_file_line_and_message()
    {
        await using var host = Host();
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), "class A : Harness {\n  int x = \"no\";\n}\n");
        var result = await host.RunHarnessAsync(5);
        Assert.False(result.Played);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(("harness.cs", 2), (problem.File, problem.Line));
        Assert.Contains("CS0029", problem.Message);
    }

    [Fact]
    public void The_last_harness_subclass_in_the_file_runs()
    {
        var run = Path.Combine(_root, "run");
        Directory.CreateDirectory(run);
        var path = Path.Combine(run, "harness.cs");
        File.WriteAllText(path, "class A : Harness { }\nclass B : A { }\nclass Helper { }\nabstract class C : A { }\n");
        var compiled = new HarnessCompiler(run).CompileHarness(path, null);
        Assert.True(compiled.Ok, compiled.ProblemText);
        Assert.Equal("B", compiled.Type);
        File.WriteAllText(path, "class Nothing { }\n");
        Assert.Contains("no Harness subclass", new HarnessCompiler(run).CompileHarness(path, null).ProblemText);
    }

    [Theory]
    [InlineData("KIT-PLAN.md", "EscortHarness")]
    [InlineData("KIT-FIGHT.md", "DownTheGuards")]
    public void The_worked_example_in_the_kit_reference_compiles(string file, string type)
    {
        var doc = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", file)));
        var from = doc.IndexOf($"class {type}", StringComparison.Ordinal);
        Assert.True(from > 0);
        var run = Path.Combine(_root, "run");
        Directory.CreateDirectory(run);
        var path = Path.Combine(run, "harness.cs");
        File.WriteAllText(path, doc[from..doc.IndexOf("```", from, StringComparison.Ordinal)]);
        var compiled = new HarnessCompiler(run).CompileHarness(path, null);
        Assert.True(compiled.Ok, compiled.ProblemText);
        Assert.Equal(type, compiled.Type);
    }

    const string TwoChoices = """
        class TwoChoices : Harness
        {
            public override string Goal => "walk";
            public override double TickHz => 30;

            public override async Task<Obs> Observe(Game game) =>
                new() { ["_pose"] = await game.Pose(), ["x"] = (await game.Pose()).At.X };

            public override List<Option> Options(Obs obs) =>
            [
                new("walk", "Walk ahead", (g, o) => WalkTo(g, new Vec(300, 0, 90), within: 60, climb: false)),
                new("wait", "Stand still", (g, o) => Wait(g, 1)),
            ];

            public override Status Status(Obs obs) => obs.Num("x") > 200 ? new Status("done", "there") : new Status();
        }
        """;

    [Fact]
    public async Task A_harness_runs_in_the_container_with_jev_through_the_parent()
    {
        var jev = new CannedJev("walk");
        await using var host = Host(jev);
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), TwoChoices);
        var result = await host.RunHarnessAsync(10);
        Assert.True(result.Played, result.Text);
        Assert.Equal("appcontainer", result.Sandbox);
        Assert.Equal("done", result.State);
        Assert.True(jev.Calls >= 1);
        Assert.DoesNotContain("\"model\"", jev.LastBody);
        Assert.Contains("walk", result.Summary);
        Assert.Contains("Episode 1 (work/trace1.jsonl; harness saved as harness1.cs)", result.Text);
        var run = Path.Combine(_root, "run");
        Assert.Equal(new[] { "carry.json", "tmp", "trace1.jsonl", "trace1_summary.txt" },
            Directory.GetFileSystemEntries(Path.Combine(run, "work")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { ".container", "bin", "harness.cs", "harness1.cs", "sandbox.log", "work" },
            Directory.GetFileSystemEntries(run).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(host.DecisionsSpent >= 1);
        Assert.InRange(result.Decisions, 1, jev.Calls);
        Assert.Equal(result.Decisions, host.MostDecisions);
    }

    // The child counts every answer it applied: the gate takes only those the brain saw pick among what was offered.
    [Fact]
    public async Task Answers_that_pick_nothing_offered_are_no_decisions_whatever_the_child_counted()
    {
        var jev = new FixedJev("""{"answers":{"pick":{"type":"choice","choice":"elsewhere","probabilities":{"elsewhere":1.0}}}}""");
        await using var host = Host(jev);
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), TwoChoices);
        var result = await host.RunHarnessAsync(5);
        Assert.True(result.Played, result.Text);
        Assert.True(jev.Calls >= 1);
        Assert.True(result.Stats!.Value.GetProperty("decisions").GetProperty("answered").GetInt32() >= 1, result.Stats.Value.GetRawText());
        Assert.Equal(0, result.Decisions);
        Assert.Equal(0, host.MostDecisions);
    }

    [Fact]
    public async Task The_parent_holds_the_jev_decision_limit_whatever_the_child_does()
    {
        var jev = new CannedJev("wait");
        await using var host = new HarnessHost(new HostOptions(_game, Path.Combine(_root, "run"))
        {
            Decider = jev, MaxDecisions = 2, Mode = SandboxMode.Contained, Say = _ => { },
        });
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), TwoChoices);
        var result = await host.RunHarnessAsync(6);
        Assert.Equal("inconclusive", result.State);
        Assert.Equal(Runtime.DecisionLimit, result.Reason);
        Assert.True(jev.Calls <= 2, $"{jev.Calls} calls");
    }

    [Fact]
    public async Task A_second_episode_past_the_decision_limit_is_inconclusive_too()
    {
        var jev = new CannedJev("wait");
        await using var host = new HarnessHost(new HostOptions(_game, Path.Combine(_root, "run"))
        {
            Decider = jev, MaxDecisions = 1, Mode = SandboxMode.Contained, Say = _ => { },
        });
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), TwoChoices);
        await host.RunHarnessAsync(6);
        var second = await host.RunHarnessAsync(6, restart: false);
        Assert.Equal("inconclusive", second.State);
        Assert.Equal(Runtime.DecisionLimit, second.Reason);
        Assert.Equal(1, jev.Calls);
    }

    [Fact]
    public async Task Check_harness_tries_it_without_jev()
    {
        await using var host = Host();
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), TwoChoices);
        var text = await host.CheckHarnessAsync(3, restart: false, only: ["wait"]);
        Assert.Contains("setup() ran.", text);
        Assert.Contains("wait: ok", text);
        Assert.DoesNotContain("walk: ok", text);
    }

    // Before, a stop that came while a call compiled or started its child found nothing running and was lost: the call
    // then played in full.
    [Fact]
    public async Task A_stop_while_a_probe_starts_its_child_ends_it_before_it_begins_and_the_next_probe_plays()
    {
        var link = new StopOnAttach(_game);
        await using var host = Host(link: link);
        link.Stopping = host.EndEpisode;
        Assert.Equal("Stopped before it began.", (await host.ProbeAsync("\"played\"")).Text);
        Assert.Contains("played", await Probe(host, "\"played\""));
    }

    [Fact]
    public async Task A_stop_while_a_check_starts_its_child_ends_it_before_it_begins_and_the_next_check_plays()
    {
        var link = new StopOnAttach(_game);
        await using var host = Host(link: link);
        link.Stopping = host.EndEpisode;
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), TwoChoices);
        Assert.Equal("Stopped before it began.", await host.CheckHarnessAsync(3, restart: false, only: ["wait"]));
        Assert.Contains("wait: ok", await host.CheckHarnessAsync(3, restart: false, only: ["wait"]));
    }

    [Fact]
    public async Task A_stop_while_an_episode_starts_its_child_counts_no_episode_and_the_next_episode_plays()
    {
        var link = new StopOnAttach(_game);
        await using var host = Host(new CannedJev("walk"), link: link);
        link.Stopping = host.EndEpisode;
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), TwoChoices);
        var stopped = await host.RunHarnessAsync(10);
        Assert.Equal(("Stopped before it began.", false, 0), (stopped.Text, stopped.Played, host.Episodes));
        var played = await host.RunHarnessAsync(10);
        Assert.True(played.Played, played.Text);
        Assert.Equal((1, 1), (played.Episode, host.Episodes));
    }

    const string EndsItsEpisodeEarly = """
        class EndsEarly : Harness
        {
            public override async Task Setup(Game game)
            {
                var raw = Console.OpenStandardOutput();
                raw.Write(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"episode\",\"rid\":999999,\"mark\":\"end\",\"n\":1}\n"));
                raw.Flush();
                await game.Request("rc");
                Environment.Exit(0);
            }
        }
        """;

    // Before, an episode's frames ended at the last end its child marked: harness code that marked it early and then
    // played on without the child's own end left the rest of its play out of reach.
    [Fact]
    public async Task An_episode_is_recorded_to_where_its_call_returned_whatever_end_the_harness_marked_before()
    {
        await using var host = Host(new CannedJev("walk"), recorder: new LoggedRecorder(_game.Ops));
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), EndsItsEpisodeEarly);
        var result = await host.RunHarnessAsync(10);
        Assert.Contains("sandbox failed", result.Text);
        lock (_game.Ops)
        {
            var marked = _game.Ops.IndexOf("end 1");
            Assert.True(marked >= 0, string.Join(" ", _game.Ops));
            var played = _game.Ops.IndexOf("rc", marked);
            Assert.True(played > marked, string.Join(" ", _game.Ops));
            Assert.True(_game.Ops.LastIndexOf("end 1") > played, string.Join(" ", _game.Ops));
        }
    }

    // Before, an episode's frames began where its child marked it, after the harness was made: harness code could play
    // first and leave that play out of the episode's pictures.
    [Fact]
    public async Task An_episode_is_recorded_from_where_this_process_saw_it_begin()
    {
        await using var host = Host(new CannedJev("walk"), recorder: new LoggedRecorder(_game.Ops));
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "harness.cs"), TwoChoices);
        Assert.True((await host.RunHarnessAsync(6)).Played);
        int restarted;
        lock (_game.Ops)
        {
            restarted = _game.Ops.IndexOf("session");
            Assert.True(restarted >= 0, string.Join(" ", _game.Ops));
            Assert.Equal("begin 1", _game.Ops[restarted + 1]);
            _game.Ops.Clear();
        }
        Assert.True((await host.RunHarnessAsync(6, restart: false)).Played);
        lock (_game.Ops)
            Assert.Equal("begin 2", _game.Ops[0]);
    }

    [Fact]
    public async Task What_a_walk_learned_outlives_the_child()
    {
        await using var host = Host();
        await Probe(host, """game.Look.X = 0.25; game.CoastSeconds = 0.3; game.NavMaps["Main"] = new NavMap(); game.NavMaps["Main"].Hits[(1, 2, 0)] = 5.0;""");
        var text = await Probe(host, """Console.WriteLine($"{game.Look.X} {game.CoastSeconds} {game.NavMaps["Main"].Hits[(1, 2, 0)]}");""");
        Assert.Contains("0.25 0.3 5", text);
    }

    // Before, a carry.json whose shape was wrong in a way the reader didn't expect ended every call after the first,
    // and was never written over.
    [Theory]
    [InlineData("""{"look_x":0,"look_y":0,"coast":0,"keys":{},"maps":{"Main":{"hits":[[]]}}}""")]
    [InlineData("""{"maps":7}""")]
    [InlineData("[")]
    public async Task What_an_earlier_child_carried_is_ignored_when_it_cannot_be_read(string carried)
    {
        await using var host = Host();
        await Probe(host, "1");
        await File.WriteAllTextAsync(Path.Combine(_root, "run", "work", "carry.json"), carried);
        Assert.Contains("played", await Probe(host, "\"played\""));
        await Probe(host, """game.Look.X = 0.25;""");
        Assert.Contains("0.25", await Probe(host, """Console.WriteLine(game.Look.X);"""));
    }

    [Fact]
    public async Task A_direct_change_in_a_probe_is_reported_as_research()
    {
        await using var host = Host();
        await Probe(host, """await game.Set("Main:/Door", "open", true, "why not");""");
        var write = Assert.Single(host.Writes);
        Assert.Equal("Door", write["name"]!.GetValue<string>());
        Assert.Equal("why not", write["why"]!.GetValue<string>());
        Assert.True(write["probe"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_batch_leaves_the_child_as_one_write()
    {
        await using var host = Host();
        var text = await Probe(host, """
            var (a, b, c) = await game.Batch(() => game.Pose(), () => game.Pose(), () => game.Pose());
            a.Value.At.Z + b.Value.At.Z + c.Value.At.Z
            """);
        Assert.Contains("270", text);
    }

    [Fact]
    public async Task Each_probe_gets_a_new_process()
    {
        await using var host = Host();
        var first = await Probe(host, "Environment.ProcessId");
        var second = await Probe(host, "Environment.ProcessId");
        Assert.NotEqual(first.Trim(), second.Trim());
    }

    [Fact]
    public async Task A_probe_past_its_time_is_cut_off_and_its_child_killed()
    {
        await using var host = Host();
        var clock = Stopwatch.StartNew();
        var text = await Probe(host, """Console.WriteLine("started"); while (true) { }""", seconds: 2);
        Assert.Contains("started", text);
        Assert.Contains("CUT OFF", text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task The_child_cannot_read_the_users_profile()
    {
        var secret = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".playtest-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(secret, "secret");
        try
        {
            await using var host = Host();
            var text = await Probe(host, Attempt($"File.ReadAllText({Literal(secret)});"));
            Assert.Contains("REFUSED UnauthorizedAccessException", text);
            Assert.Contains("REFUSED", await Probe(host, Attempt($"Directory.GetFiles({Literal(Path.GetDirectoryName(secret)!)});")));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public async Task The_child_cannot_read_the_keys_file()
    {
        await using var host = Host();
        var keys = ApiKeys.KeysFile;
        var text = await Probe(host, Attempt($"File.ReadAllText({Literal(keys)});"));
        Assert.Contains("REFUSED", text);
    }

    [Fact]
    public async Task The_child_cannot_read_another_project_or_another_run()
    {
        var other = Path.Combine(_root, "other-run");
        Directory.CreateDirectory(other);
        var file = Path.Combine(other, "harness.cs");
        await File.WriteAllTextAsync(file, "secret");
        await using (var neighbour = new HarnessHost(new HostOptions(_game, other) { Mode = SandboxMode.Contained, Say = _ => { } }))
        {
            await Probe(neighbour, "1");
            await using var host = Host();
            Assert.Contains("REFUSED", await Probe(host, Attempt($"File.ReadAllText({Literal(file)});")));
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "NOTICE"));
            Assert.True(File.Exists(project));
            Assert.Contains("REFUSED", await Probe(host, Attempt($"File.ReadAllText({Literal(project)});")));
        }
    }

    // In an app container Windows gives the child the container's own temporary folder whatever TEMP was set to.
    [Fact]
    public async Task A_plain_child_starts_in_work_and_has_its_temporary_folder_there()
    {
        await using var host = Host(mode: SandboxMode.Process);
        var work = Path.Combine(_root, "run", "work");
        var said = await Probe(host, """
            Console.WriteLine("IN " + Environment.CurrentDirectory);
            Console.WriteLine("TEMPORARY " + Path.GetTempPath());
            """);
        Assert.Equal("process", host.Sandbox);
        Assert.Contains($"IN {work}\r\n", said);
        Assert.Contains($"TEMPORARY {Path.Combine(work, "tmp")}{Path.DirectorySeparatorChar}\r\n", said);
    }

    [Fact]
    public async Task The_child_writes_under_work_and_nowhere_else()
    {
        await using var host = Host();
        var run = Path.Combine(_root, "run");
        var work = Path.Combine(run, "work");
        var harness = Path.Combine(run, "harness.cs");
        await File.WriteAllTextAsync(harness, "class A : Harness { }\n");
        var inside = Path.Combine(work, "note.txt");
        Assert.Contains("ALLOWED", await Probe(host, Attempt($"File.WriteAllText({Literal(inside)}, \"x\");")));
        Assert.True(File.Exists(inside));
        var said = await Probe(host, Attempt("""
            Console.WriteLine("IN " + Environment.CurrentDirectory);
            File.WriteAllText("here.txt", "x");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory("deeper").FullName, "there.txt"), "x");
            File.WriteAllText(Path.Combine("tmp", "temporary.txt"), "x");
            File.Delete("note.txt");
            """));
        Assert.Contains("ALLOWED", said);
        Assert.Contains($"IN {work}\r\n", said);
        Assert.Equal(new[] { "carry.json", "deeper", "here.txt", "tmp" },
            Directory.GetFileSystemEntries(work).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(work, "deeper", "there.txt")));
        Assert.True(File.Exists(Path.Combine(work, "tmp", "temporary.txt")));

        Assert.Contains("ALLOWED", await Probe(host, Attempt($"File.ReadAllText({Literal(harness)});")));
        var compiled = Directory.GetFiles(Path.Combine(run, "bin"), "*.dll")[0];
        var before = Directory.GetFileSystemEntries(run, "*", SearchOption.AllDirectories).Where(entry => !entry.StartsWith(work)).ToList();
        foreach (var attempt in (string[])
                 [
                     $"File.WriteAllText({Literal(Path.Combine(run, "note.txt"))}, \"x\");",
                     $"File.WriteAllText({Literal(Path.Combine(run, "bin", "planted.dll"))}, \"x\");",
                     $"File.WriteAllBytes({Literal(compiled)}, new byte[1]);",
                     $"File.Delete({Literal(compiled)});",
                     $"File.AppendAllText({Literal(harness)}, \"// more\");",
                     $"File.Delete({Literal(harness)});",
                     $"File.Move({Literal(harness)}, {Literal(Path.Combine(work, "harness.cs"))});",
                     $"File.WriteAllText({Literal(Path.Combine(run, "report.json"))}, \"{{}}\");",
                     $"File.AppendAllText({Literal(Path.Combine(run, "sandbox.log"))}, \"x\");",
                     $"Directory.CreateDirectory({Literal(Path.Combine(run, "work2"))});",
                     $"Directory.Move({Literal(work)}, {Literal(Path.Combine(run, "moved"))});",
                 ])
            Assert.True((await Probe(host, Attempt(attempt))).Contains("REFUSED"), attempt);
        Assert.Equal("class A : Harness { }\n", await File.ReadAllTextAsync(harness));
        // Each probe is compiled into bin by the brain, so only what was there before is compared.
        Assert.Empty(before.Except(Directory.GetFileSystemEntries(run, "*", SearchOption.AllDirectories)));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(run), entry => Path.GetFileName(entry) is "note.txt" or "report.json" or "work2" or "moved");
        Assert.False(File.Exists(Path.Combine(run, "bin", "planted.dll")));

        var outside = Path.Combine(_root, "escaped.txt");
        Assert.Contains("REFUSED", await Probe(host, Attempt($"File.WriteAllText({Literal(outside)}, \"x\");")));
        Assert.False(File.Exists(outside));
        var system = Path.Combine(Path.GetPathRoot(_root)!, $"playtest-{Guid.NewGuid():N}.txt");
        Assert.Contains("REFUSED", await Probe(host, Attempt($"File.WriteAllText({Literal(system)}, \"x\");")));
        Assert.False(File.Exists(system));
    }

    [Fact]
    public async Task The_child_cannot_connect_out_or_to_loopback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var host = Host();
        var outside = await Probe(host, Attempt("""
            using var socket = new System.Net.Sockets.TcpClient();
            var connect = socket.ConnectAsync("1.1.1.1", 443);
            if (await Task.WhenAny(connect, Task.Delay(4000)) != connect) throw new TimeoutException();
            await connect;
            """));
        Assert.Contains("REFUSED", outside);
        var loopback = await Probe(host, Attempt($$"""
            using var socket = new System.Net.Sockets.TcpClient();
            var connect = socket.ConnectAsync("127.0.0.1", {{port}});
            if (await Task.WhenAny(connect, Task.Delay(4000)) != connect) throw new TimeoutException();
            await connect;
            """));
        Assert.Contains("REFUSED", loopback);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task The_child_cannot_start_another_process()
    {
        await using var host = Host();
        var text = await Probe(host, Attempt("""System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(@"C:\Windows\System32\whoami.exe") { UseShellExecute = false }); """));
        Assert.Contains("REFUSED", text);
    }

    [Fact]
    public async Task The_child_has_no_key_in_its_environment()
    {
        const string name = "TYPESAFE_API_KEY";
        var before = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "sk-test-value");
        Environment.SetEnvironmentVariable("SOME_OTHER_THING", "sk-other-value");
        try
        {
            foreach (var mode in new[] { SandboxMode.Contained, SandboxMode.Process })
            {
                await using var host = Host(mode: mode);
                var text = await Probe(host, """
                    foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                        Console.WriteLine($"{e.Key}={e.Value}");
                    """);
                Assert.DoesNotContain("sk-", text);
                Assert.DoesNotContain(name, text);
                Assert.DoesNotContain("SOME_OTHER_THING", text);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, before);
            Environment.SetEnvironmentVariable("SOME_OTHER_THING", null);
        }
    }

    [Fact]
    public async Task A_memory_hog_is_stopped_and_the_host_goes_on()
    {
        await using var host = Host(memory: 256L << 20);
        var result = await host.ProbeAsync("""
            var kept = new List<byte[]>();
            for (var i = 0; i < 400; i++)
            {
                var block = new byte[10 << 20];
                Array.Fill(block, (byte)1);
                kept.Add(block);
                Console.WriteLine(i);
            }
            Console.WriteLine("SURVIVED");
            """, seconds: 60);
        Assert.DoesNotContain("SURVIVED", result.Text);
        Assert.True(result.Text.Contains("OutOfMemory") || result.Text.Contains("sandbox failed"), result.Text);
        Assert.Contains("still here", await Probe(host, "\"still here\""));
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("not json", true)]
    [InlineData("""{"type":"reply","id":1}""", true)]
    [InlineData("""{"kind":"reply"}""", true)]
    [InlineData("""{"type":"reply","id":"x"}""", true)]
    [InlineData("""{"type":"post"}""", true)]
    [InlineData("""{"type":"status","text":5}""", true)]
    [InlineData("""{"type":"req","op":"pose"}""", true)]
    [InlineData("""{"type":"decide","rid":"x","body":{}}""", true)]
    [InlineData("""{"type":"episode","rid":1,"n":1}""", true)]
    public async Task A_line_off_the_protocol_ends_the_session_and_the_brain_goes_on(string line, bool ends)
    {
        await using var host = Host();
        var result = await host.ProbeAsync($$"""
            var raw = Console.OpenStandardOutput();
            raw.Write(System.Text.Encoding.UTF8.GetBytes({{Literal(line)}} + "\n"));
            raw.Flush();
            "after"
            """, seconds: 30);
        if (ends)
            Assert.Contains("broke the protocol", result.Text);
        else
            Assert.Contains("after", result.Text);
        Assert.Contains("still here", await Probe(host, "\"still here\""));
    }

    [Fact]
    public async Task Required_runs_no_harness_code_when_the_container_cannot_be_made()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var run = Directory.CreateDirectory(Path.Combine(_root, "run")).FullName;
        // Owner Rights takes away the owner's implicit right to change the folder's ACL, which the container's grant needs.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var who in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier("S-1-3-4") })
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(run).SetAccessControl(security);
        await using var host = new HarnessHost(new HostOptions(_game, run)
        {
            Mode = SandboxStarter.ModeFrom("required")!.Value, Say = _ => { },
        });
        var refused = await Assert.ThrowsAsync<SandboxException>(() => host.ProbeAsync("Console.WriteLine(\"RAN\");"));
        Assert.Contains("container couldn't be created", refused.Message);
        Assert.False(File.Exists(Path.Combine(run, "sandbox.log")));
    }

    // Before, what harness code left in work could fail the next contained child as it started, and the next revision
    // ran in a plain child: in the same run, or in the next one given the same folder.
    [Fact]
    public async Task What_a_child_left_in_work_buys_the_next_one_no_way_out_of_the_container()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await using (var host = Host(mode: SandboxMode.Auto))
        {
            Assert.Contains("contained", await Probe(host, "\"contained\""));
            Assert.Equal("appcontainer", host.Sandbox);
            // Left readable to this user alone: a contained child can't read it, and a plain one could.
            var carry = new FileInfo(Path.Combine(_root, "run", "work", "carry.json"));
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                AccessControlType.Allow));
            carry.SetAccessControl(security);
            Assert.Contains("again", await Probe(host, "\"again\""));
            Assert.Equal("appcontainer", host.Sandbox);
            Assert.Null(host.SandboxNote);
        }
        await using var next = Host(mode: SandboxMode.Auto);
        Assert.Contains("next", await Probe(next, "\"next\""));
        Assert.Equal("appcontainer", next.Sandbox);
        Assert.Null(next.SandboxNote);
    }

    [Theory]
    [InlineData(SandboxMode.Contained)]
    [InlineData(SandboxMode.Process)]
    public async Task A_child_whose_first_call_is_cancelled_does_not_outlive_it(SandboxMode mode)
    {
        var before = Children.Of(Environment.ProcessId);
        using var cancel = new CancellationTokenSource();
        await using var host = new HarnessHost(new HostOptions(new CancelOnAttach(_game, cancel), Path.Combine(_root, "run"))
        {
            Mode = mode, Say = _ => { },
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ProbeAsync("1", cancel.Token));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var left = Children.Of(Environment.ProcessId).Except(before).ToList();
        while (left.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            left = Children.Of(Environment.ProcessId).Except(before).ToList();
        }
        Assert.Empty(left);
    }

    [Fact]
    public async Task Closing_the_parent_kills_the_child()
    {
        var folder = Path.Combine(_root, "held");
        Directory.CreateDirectory(folder);
        var pidFile = Path.Combine(folder, "work", "pid.txt");
        var holder = Path.Combine(AppContext.BaseDirectory, "Cluewren.Tools.dll");
        using var parent = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Cluewren.Tools.exe"))
        {
            ArgumentList = { "hold", folder },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(File.Exists(holder));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(pidFile) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.True(File.Exists(pidFile), "the holder never started its child");
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        using var child = Process.GetProcessById(pid);
        Assert.False(child.HasExited);
        parent.Kill();
        await parent.WaitForExitAsync();
        Assert.True(child.WaitForExit(5000), "the child outlived its parent");
        if (OperatingSystem.IsWindows())
        {
            var claim = Path.Combine(folder, ".container");
            Assert.True(File.Exists(claim));
            WindowsContainer.RetireLeftovers(_root, Path.Combine(_root, "run"));
            Assert.False(File.Exists(claim), "the dead parent's container was not retired");
        }
    }
}

// The moment the brain attaches to a new child is when it subscribes to the game's events.
sealed class CancelOnAttach(IGameLink game, CancellationTokenSource cancel) : IGameLink
{
    public ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default) => game.RequestAsync(op, args, timeout, ct);

    public void Post(string op, Action<Utf8JsonWriter>? args = null) => game.Post(op, args);

    public event Action<JsonElement>? Event
    {
        add => cancel.Cancel();
        remove { }
    }
}

sealed class StopOnAttach(IGameLink game) : IGameLink
{
    public Action? Stopping;

    public ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default) => game.RequestAsync(op, args, timeout, ct);

    public void Post(string op, Action<Utf8JsonWriter>? args = null) => game.Post(op, args);

    public event Action<JsonElement>? Event
    {
        add => Interlocked.Exchange(ref Stopping, null)?.Invoke();
        remove { }
    }
}

sealed class LoggedRecorder(List<string> log) : IEpisodeRecorder
{
    public void Begin(int episode)
    {
        lock (log)
            log.Add($"begin {episode}");
    }

    public void End(int episode)
    {
        lock (log)
            log.Add($"end {episode}");
    }
}

static class Children
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public int Size;
        public int Usage;
        public int ProcessId;
        public IntPtr HeapId;
        public int ModuleId;
        public int Threads;
        public int ParentProcessId;
        public int PriorityBase;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    static extern bool First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    static extern bool Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static HashSet<int> Of(int parent)
    {
        var found = new HashSet<int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        try
        {
            var entry = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (var more = First(snapshot, ref entry); more; more = Next(snapshot, ref entry))
                if (entry.ParentProcessId == parent)
                    found.Add(entry.ProcessId);
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return found;
    }
}
