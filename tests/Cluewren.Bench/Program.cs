using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cluewren.Kit;
using Cluewren.Sandbox;
using Cluewren.Tests.Sim;

// Same levels as upstream_bench.py: a walk per level in simulated time, each step timed alone, and the first pass thrown
// away so the JIT doesn't count.
static class Bench
{
    const int Passes = 6;

    static IEnumerable<(string Name, WorldSetup Setup, Vec Target, double Within)> Levels()
    {
        foreach (var level in Cluewren.Tests.Sim.Levels.All)
            yield return level;
        for (uint seed = 0; seed < 12; seed++)
        {
            var (setup, target) = Cluewren.Tests.Sim.Levels.Clutter(seed);
            yield return ($"clutter {seed}", setup, target, 100);
        }
    }

    static double Percentile(List<double> sorted, double p) => sorted[(int)(sorted.Count * p)];

    static void Kit()
    {
        var samples = new List<double>(400000);
        var bytes = new List<double>(400000);
        long allocated = 0, steps = 0;
        double walked = 0;
        var outcomes = new List<string>();
        var coldMs = 0.0;
        for (var pass = 0; pass < Passes; pass++)
        {
            foreach (var (name, setup, target, within) in Levels())
            {
                var world = new World(setup);
                var game = world.Game();
                var stepper = new Stepper(game, ct => Control.WalkTo(game, target, within: within, timeout: 40, ct: ct));
                var t0 = world.Clock.T;
                Result? result = null;
                while (world.Clock.T - t0 < 45)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    stepper.Step().GetAwaiter().GetResult();
                    var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    var used = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (pass == 0)
                    {
                        coldMs = Math.Max(coldMs, ms);
                    }
                    else
                    {
                        samples.Add(ms);
                        allocated += used;
                        bytes.Add(used);
                        steps++;
                    }
                    if (stepper.Done)
                    {
                        result = stepper.Task!.Result;
                        break;
                    }
                    world.Step();
                }
                if (pass == Passes - 1)
                {
                    walked += world.Travelled;
                    outcomes.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{name}: ok={(result?.Ok == true ? 1 : 0)} why={result?.Why} seconds={world.Clock.T - t0:F2} travelled={world.Travelled:F1} sonar={world.SonarLooks}"));
                }
            }
        }
        samples.Sort();
        Console.WriteLine($"csharp kit: passes {Passes - 1} steps {steps}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  step ms mean {samples.Average():F4} p50 {Percentile(samples, .5):F4} p90 {Percentile(samples, .9):F4} p99 {Percentile(samples, .99):F4} max {samples[^1]:F3}"));
        bytes.Sort();
        Console.WriteLine($"  allocated bytes per step mean {allocated / steps} p50 {Percentile(bytes, .5)} p90 {Percentile(bytes, .9)} p99 {Percentile(bytes, .99)}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  slowest cold step ms {coldMs:F1}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  travelled total {walked:F1}"));
        foreach (var outcome in outcomes)
            Console.WriteLine("  " + outcome);
    }

    const string Walker = """
        class Walker : Harness
        {
            public override string Goal => "walk";
            public override double TickHz => 30;
            public override double StallSeconds => 0;

            public override async Task<Obs> Observe(Game game)
            {
                var (pose, again) = await game.Batch(() => game.Pose(), () => game.Pose());
                return new Obs { ["_pose"] = pose.Value, ["x"] = again.Value.At.X };
            }

            public override List<Option> Options(Obs obs) =>
            [
                new("sway", "Keep moving", async (g, o) =>
                {
                    for (var i = 0; ; i++)
                    {
                        var p = await g.Pose();
                        g.Hold("W");
                        g.Mouse(i % 2 == 0 ? 30 : -30);
                        await g.Tick();
                    }
                }) { MaxSeconds = 1000 },
            ];
        }
        """;

    sealed class Quick : SimLink
    {
        protected override object? Answer(string op, JsonElement args)
        {
            GameTime = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            return op switch
            {
                "pose" => new Dictionary<string, object?>
                {
                    ["at"] = new Vec(1, 2, 3).Ue(), ["eye"] = new Vec(1, 2, 4).Ue(), ["yaw"] = 10.0, ["pitch"] = 0.0,
                    ["velocity"] = new Vec().Ue(), ["on_ground"] = true,
                },
                "context" => new Dictionary<string, object?> { ["world"] = "Main", ["character"] = "Main:/Player" },
                _ => new Dictionary<string, object?>(),
            };
        }
    }

    static async Task Contained(double seconds)
    {
        var folder = Path.Combine(Path.GetTempPath(), "cluewren-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "harness.cs"), Walker);
        await using var host = new HarnessHost(new HostOptions(new Quick(), folder) { Mode = SandboxMode.Contained, Say = _ => { } });
        var clock = Stopwatch.StartNew();
        var ping = await host.ProbeAsync("1");
        Console.WriteLine($"child in {host.Sandbox}: first probe, compile included, {clock.ElapsedMilliseconds} ms ({ping.Text.Trim()})");
        clock.Restart();
        await host.ProbeAsync("2");
        Console.WriteLine($"second probe, Roslyn warm: {clock.ElapsedMilliseconds} ms");
        var result = await host.RunHarnessAsync(seconds, restart: false);
        Console.WriteLine($"reason {result.Reason} sandbox {result.Sandbox}");
        Console.WriteLine(result.Stats?.GetRawText());
    }

    static async Task Main(string[] args)
    {
        if (args.FirstOrDefault() == "contained")
            await Contained(args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 20);
        else
            Kit();
    }
}
