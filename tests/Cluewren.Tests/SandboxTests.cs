using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cluewren.Jev;
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

sealed class CannedJev(string pick) : IJevRelay
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

    HarnessHost Host(IJevRelay? jev = null, long memory = 2L << 30, SandboxMode mode = SandboxMode.Contained) =>
        new(new HostOptions(_game, Path.Combine(_root, "run")) { Jev = jev, MemoryLimitBytes = memory, Mode = mode, Say = _ => { } });

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
            await game.Request("nothing");
            """);
        Assert.Contains("before", text);
        Assert.Contains("GameException", text);
        Assert.Contains("unknown op nothing", text);
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
        Assert.True(File.Exists(Path.Combine(_root, "run", "trace1.jsonl")));
        Assert.True(File.Exists(Path.Combine(_root, "run", "trace1_summary.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "run", "harness1.cs")));
        Assert.True(host.JevDecisions >= 1);
    }

    [Fact]
    public async Task The_parent_holds_the_jev_decision_limit_whatever_the_child_does()
    {
        var jev = new CannedJev("wait");
        await using var host = new HarnessHost(new HostOptions(_game, Path.Combine(_root, "run"))
        {
            Jev = jev, MaxJevDecisions = 2, Mode = SandboxMode.Contained, Say = _ => { },
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
            Jev = jev, MaxJevDecisions = 1, Mode = SandboxMode.Contained, Say = _ => { },
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

    [Fact]
    public async Task What_a_walk_learned_outlives_the_child()
    {
        await using var host = Host();
        await Probe(host, """game.Look.X = 0.25; game.CoastSeconds = 0.3; game.NavMaps["Main"] = new NavMap(); game.NavMaps["Main"].Hits[(1, 2, 0)] = 5.0;""");
        var text = await Probe(host, """Console.WriteLine($"{game.Look.X} {game.CoastSeconds} {game.NavMaps["Main"].Hits[(1, 2, 0)]}");""");
        Assert.Contains("0.25 0.3 5", text);
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

    [Fact]
    public async Task The_child_writes_inside_the_run_folder_and_nowhere_else()
    {
        await using var host = Host();
        var inside = Path.Combine(_root, "run", "note.txt");
        Assert.Contains("ALLOWED", await Probe(host, Attempt($"File.WriteAllText({Literal(inside)}, \"x\");")));
        Assert.True(File.Exists(inside));
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

    [Fact]
    public async Task Closing_the_parent_kills_the_child()
    {
        var folder = Path.Combine(_root, "held");
        Directory.CreateDirectory(folder);
        var pidFile = Path.Combine(folder, "pid.txt");
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
