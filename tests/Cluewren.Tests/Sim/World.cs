using System.Text.Json;
using Cluewren.Kit;

namespace Cluewren.Tests.Sim;

public sealed record Box(double X0, double Y0, double X1, double Y1);

public sealed record WorldSetup
{
    public IReadOnlyList<Box> Walls { get; init; } = [];

    public IReadOnlyList<Box> Pits { get; init; } = [];

    public double StartX { get; init; }

    public double StartY { get; init; }

    public double Yaw { get; init; }

    public double Speed { get; init; } = 500;

    public double TickSeconds { get; init; } = 1 / 30.0;

    public double Radius { get; init; } = World.DefaultRadius;

    public int Lag { get; init; }

    public bool ViewLocked { get; init; }
}

// tests/navsim.py: a flat floor, boxes, pits, a round body that slides along what it meets, and the plugin's sonar.
public sealed class World(WorldSetup setup) : SimLink
{
    public const double DefaultRadius = 35.0;
    public const double HalfHeight = 90.0;

    readonly List<(int At, string Mode, string? Key, double Value)> _pending = [];
    readonly HashSet<string> _held = [];
    int _ticks;

    public WorldSetup Setup { get; } = setup;

    public VirtualClock Clock { get; } = new();

    public Vec Pos { get; set; } = new(setup.StartX, setup.StartY, HalfHeight);

    public double Yaw { get; set; } = setup.Yaw;

    public double Pitch { get; set; }

    public bool Fell { get; private set; }

    public double Travelled { get; private set; }

    public int SonarLooks { get; private set; }

    public double Time { get; private set; }

    public Game Game() => new(this, Clock) { Look = new LookScale { X = 0.1, Y = 0.1 } };

    public bool Inside(double x, double y)
    {
        var r = Setup.Radius;
        foreach (var w in Setup.Walls)
        {
            var (nx, ny) = (Math.Min(Math.Max(x, w.X0), w.X1), Math.Min(Math.Max(y, w.Y0), w.Y1));
            if ((nx - x) * (nx - x) + (ny - y) * (ny - y) < r * r)
                return true;
        }
        return false;
    }

    public bool InPit(double x, double y) =>
        Setup.Pits.Any(p => p.X0 <= x && x <= p.X1 && p.Y0 <= y && y <= p.Y1);

    (double Clear, string By, Vec? Normal) Cast(double x, double y, double dx, double dy, double reach)
    {
        const double step = 4.0;
        var s = 0.0;
        while (s < reach)
        {
            var n = s + step;
            var (px, py) = (x + dx * n, y + dy * n);
            if (Inside(px, py))
                return (s, "wall", Normal(px, py));
            if (InPit(px, py))
                return (s, "drop", null);
            s = n;
        }
        return (reach, "reach", null);
    }

    Vec Normal(double px, double py)
    {
        var (best, nrm) = (1e18, new Vec(1, 0));
        foreach (var w in Setup.Walls)
        {
            var (nx, ny) = (Math.Min(Math.Max(px, w.X0), w.X1), Math.Min(Math.Max(py, w.Y0), w.Y1));
            var d = (nx - px) * (nx - px) + (ny - py) * (ny - py);
            if (d < best)
            {
                best = d;
                var (vx, vy) = (px - nx, py - ny);
                var length = Math.Sqrt(vx * vx + vy * vy);
                if (length == 0)
                    length = 1;
                nrm = new Vec(vx / length, vy / length);
            }
        }
        return new Vec(Math.Round(nrm.X, 2), Math.Round(nrm.Y, 2));
    }

    public void Step()
    {
        _ticks++;
        for (var i = 0; i < _pending.Count;)
        {
            if (_pending[i].At <= _ticks)
            {
                var due = _pending[i];
                _pending.RemoveAt(i);
                Key(due.Mode, due.Key, due.Value);
            }
            else
            {
                i++;
            }
        }
        var dt = Setup.TickSeconds;
        Clock.T += dt;
        Time += dt;
        GameTime = Time;
        var f = new Vec(Math.Cos(Geom.Radians(Yaw)), Math.Sin(Geom.Radians(Yaw)));
        var r = new Vec(-f.Y, f.X);
        var move = new Vec();
        if (_held.Contains("W"))
            move += f;
        if (_held.Contains("S"))
            move -= f;
        if (_held.Contains("D"))
            move += r;
        if (_held.Contains("A"))
            move -= r;
        if (move.Length2d < 1e-6 || Fell)
            return;
        var v = move.Unit * (Setup.Speed * dt);
        var (x, y) = (Pos.X, Pos.Y);
        foreach (var (mx, my) in new[] { (v.X, v.Y), (v.X, 0.0), (0.0, v.Y) })
        {
            if (Inside(x + mx, y + my))
                continue;
            Pos = new Vec(x + mx, y + my, Pos.Z);
            Travelled += Math.Sqrt(mx * mx + my * my);
            break;
        }
        if (InPit(Pos.X, Pos.Y))
        {
            Fell = true;
            Pos = new Vec(Pos.X, Pos.Y, -1000.0);
        }
    }

    public (Result? Res, double Seconds) Run(Game game, Func<CancellationToken, Task<Result>> controller, double limit = 120)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var t0 = Clock.T;
            var stepper = new Stepper(game, controller);
            while (Clock.T - t0 < limit)
            {
                var step = stepper.Step();
                if (!step.IsCompleted)
                    throw new InvalidOperationException("a simulated step must finish without leaving its thread");
                step.GetAwaiter().GetResult();
                if (stepper.Done)
                    return (stepper.Task!.GetAwaiter().GetResult(), Clock.T - t0);
                Step();
            }
            stepper.Stop().AsTask().GetAwaiter().GetResult();
            return (null, Clock.T - t0);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    void Key(string? mode, string? key, double value)
    {
        if (mode == "release_all")
            _held.Clear();
        else if (mode == "hold")
            _held.Add(key!);
        else if (mode == "release")
            _held.Remove(key!);
        else if (key == "MouseX" && !Setup.ViewLocked)
            Yaw = (((Yaw + 0.1 * value) % 360) + 360) % 360;
        else if (key == "MouseY" && !Setup.ViewLocked)
            Pitch = Math.Max(-89, Math.Min(89, Pitch + 0.1 * value));
    }

    public List<Dictionary<string, object?>> Look(IEnumerable<double> turns, double reach)
    {
        SonarLooks++;
        var ways = new List<Dictionary<string, object?>>();
        foreach (var turn in turns)
        {
            var a = Geom.Radians(Yaw + turn);
            var (clear, by, normal) = Cast(Pos.X, Pos.Y, Math.Cos(a), Math.Sin(a), reach);
            var way = new Dictionary<string, object?> { ["turn"] = turn, ["clear"] = Math.Round(clear), ["by"] = by };
            if (normal is { } n)
                way["normal"] = new Dictionary<string, object?> { ["X"] = n.X, ["Y"] = n.Y };
            if (by == "drop")
                way["drop"] = 1000;
            ways.Add(way);
        }
        return ways;
    }

    protected override object? Answer(string op, JsonElement args)
    {
        switch (op)
        {
            case "context":
                return new Dictionary<string, object?>
                {
                    ["world"] = "/Game/Sim.Sim", ["character"] = "/Game/Sim.Sim:PersistentLevel.Hero_C_0",
                    ["controller"] = "/Game/Sim.Sim:PersistentLevel.PC_0",
                    ["camera_manager"] = "/Game/Sim.Sim:PersistentLevel.Cam_0", ["game_time"] = Time,
                };
            case "key":
                var (mode, key, value) = (Str(args, "mode"), Str(args, "key"), Num(args, "value"));
                if (Setup.Lag > 0)
                    _pending.Add((_ticks + Setup.Lag, mode ?? "", key, value));
                else
                    Key(mode, key, value);
                return new Dictionary<string, object?>();
            case "view":
                if (!Flag(args, "sonar"))
                    return new Dictionary<string, object?>();
                var turns = args.TryGetProperty("sonar_turns", out var list)
                    ? list.EnumerateArray().Select(turn => turn.GetDouble()).ToList() : [0];
                return new Dictionary<string, object?>
                {
                    ["body"] = new Dictionary<string, object?>
                    {
                        ["sonar"] = new Dictionary<string, object?> { ["ways"] = Look(turns, Num(args, "sonar_reach", 1500)) },
                    },
                };
            case "pose":
                return new Dictionary<string, object?>
                {
                    ["at"] = Pos.Ue(), ["eye"] = (Pos + new Vec(0, 0, 60)).Ue(), ["yaw"] = Geom.Wrap(Yaw), ["pitch"] = Pitch,
                    ["velocity"] = new Vec().Ue(),
                };
            default:
                throw new GameException(400, $"unknown op {op}");
        }
    }
}

public static class Levels
{
    const double Z = World.HalfHeight;

    public static IReadOnlyList<(string Name, WorldSetup Setup, Vec Target, double Within)> All =>
    [
        ("open field", new WorldSetup(), new Vec(2000, 300, Z), 100),
        ("wide wall", new WorldSetup { Walls = [new Box(500, -1500, 560, 1500)] }, new Vec(1100, 0, Z), 100),
        ("very wide wall", new WorldSetup { Walls = [new Box(500, -3000, 560, 2600)] }, new Vec(1100, 0, Z), 100),
        ("dead end", new WorldSetup
        {
            Walls = [new Box(400, -500, 460, 500), new Box(-200, -560, 460, -500), new Box(-200, 500, 460, 560)],
        }, new Vec(1200, 0, Z), 100),
        ("room door far side", new WorldSetup
        {
            Walls =
            [
                new Box(800, -600, 860, 600), new Box(1660, -600, 1720, 600), new Box(800, -660, 1720, -600),
                new Box(800, 600, 1220, 660), new Box(1340, 600, 1720, 660),
            ],
        }, new Vec(1260, 0, Z), 100),
        ("zigzag", new WorldSetup { Walls = [new Box(400, -1200, 460, 300), new Box(900, -300, 960, 1200)] },
            new Vec(1500, 0, Z), 100),
        ("wall with a gap", new WorldSetup { Walls = [new Box(600, -3000, 660, 640), new Box(600, 760, 660, 3000)] },
            new Vec(1200, 0, Z), 100),
        ("pit", new WorldSetup { Pits = [new Box(500, -700, 900, 700)] }, new Vec(1300, 0, Z), 100),
        ("pillars", new WorldSetup
        {
            Walls = Enumerable.Range(0, 5).Select(i =>
                new Box(400 + 300 * i, -150 + (i % 2) * 200, 500 + 300 * i, 50 + (i % 2) * 200)).ToList(),
        }, new Vec(2200, 0, Z), 100),
        ("narrow door", new WorldSetup { Walls = [new Box(600, -2000, 640, -50), new Box(600, 50, 640, 2000)], Yaw = 180 },
            new Vec(1200, 300, Z), 100),
        ("closed room", new WorldSetup
        {
            Walls =
            [
                new Box(800, -400, 840, 400), new Box(1600, -400, 1640, 400), new Box(800, -440, 1640, -400),
                new Box(800, 400, 1640, 440),
            ],
        }, new Vec(1200, 0, Z), 100),
    ];

    public static (WorldSetup Setup, Vec Target) Clutter(uint seed, int n = 14, double low = 80, double high = 700,
        double span = 2400)
    {
        var rnd = new PyRandom(seed);
        var walls = new List<Box>();
        var target = new Vec(span, rnd.Uniform(-600, 600), Z);
        while (walls.Count < n)
        {
            var (w, h) = (rnd.Uniform(low, high), rnd.Uniform(low, high));
            if (rnd.NextDouble() < 0.5)
                w = rnd.Uniform(40, 80);
            else if (rnd.NextDouble() < 0.5)
                h = rnd.Uniform(40, 80);
            var (x, y) = (rnd.Uniform(150, span - 150), rnd.Uniform(-1200, 1200));
            var box = new Box(x - w / 2, y - h / 2, x + w / 2, y + h / 2);
            if (new[] { new Vec(), target }.Any(p => box.X0 - 120 < p.X && p.X < box.X1 + 120
                                                                        && box.Y0 - 120 < p.Y && p.Y < box.Y1 + 120))
                continue;
            walls.Add(box);
        }
        return (new WorldSetup { Walls = walls, Yaw = rnd.Uniform(0, 360) }, target);
    }
}
