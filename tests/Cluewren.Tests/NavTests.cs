using Cluewren.Kit;
using Cluewren.Tests.Sim;

namespace Cluewren.Tests;

public class NavTests
{
    const double Z = World.HalfHeight;

    static List<SonarWay> Look(World world, IEnumerable<double>? turns = null, double reach = 1500) =>
        world.Look(turns ?? Enumerable.Range(0, 36).Select(i => i * 10.0 - 180), reach).Select(w => new SonarWay(
            Convert.ToDouble(w["turn"]), Convert.ToDouble(w["clear"]), (string?)w["by"],
            w.TryGetValue("drop", out var drop) ? Convert.ToDouble(drop) : null,
            w.TryGetValue("normal", out var normal) && normal is Dictionary<string, object?> n
                ? new Vec(Convert.ToDouble(n["X"]), Convert.ToDouble(n["Y"])) : null)).ToList();

    [Fact]
    public void Map_marks_a_wall_and_plans_round_it()
    {
        var world = new World(new WorldSetup { Walls = [new Box(500, -1500, 560, 1500)] });
        var nav = new NavMap();
        nav.Observe(world.Pos, world.Yaw, Look(world), 1.0);
        var goal = new Vec(1100, 0, Z);
        Assert.False(nav.LineFree(world.Pos, goal, Z));
        var way = nav.Plan(world.Pos, goal, 100, Z);
        Assert.NotNull(way);
        Assert.True(way.Count >= 2, "a way with a corner, not straight through");
        Assert.False(nav.WayBlocked(world.Pos, way, Z));
        Assert.All(way.SkipLast(1), p => Assert.True(Math.Abs(p.Y) > 150));
    }

    [Fact]
    public void A_sweep_through_a_cell_opens_it_again()
    {
        var nav = new NavMap();
        var at = new Vec(0, 0, Z);
        nav.Observe(at, 0, [new SonarWay(0, 300, "wall", Normal: new Vec(-1))], 1.0);
        Assert.True(nav.Blocked(new Vec(325, 0, Z), Z));
        nav.Observe(at, 0, [new SonarWay(0, 1000, "reach")], 2.0);
        Assert.False(nav.Blocked(new Vec(325, 0, Z), Z));
    }

    [Fact]
    public void Marks_belong_to_their_floor()
    {
        var nav = new NavMap();
        nav.Observe(new Vec(0, 0, Z), 0, [new SonarWay(0, 300, "wall")], 1.0, feet: 0.0);
        Assert.True(nav.Blocked(new Vec(325, 0), 0.0));
        Assert.False(nav.Blocked(new Vec(325, 0), 400.0));
    }

    static double Shortest(World world, Vec start, Vec target, double within)
    {
        var nav = new NavMap();
        var boxes = world.Setup.Walls.Concat(world.Setup.Pits).ToList();
        var xs = boxes.SelectMany(w => new[] { w.X0, w.X1 }).Append(start.X).Append(target.X).ToList();
        var ys = boxes.SelectMany(w => new[] { w.Y0, w.Y1 }).Append(start.Y).Append(target.Y).ToList();
        var c = nav.Cell;
        for (var i = (int)Math.Floor(xs.Min() / c) - 8; i < (int)Math.Floor(xs.Max() / c) + 8; i++)
        {
            for (var j = (int)Math.Floor(ys.Min() / c) - 8; j < (int)Math.Floor(ys.Max() / c) + 8; j++)
            {
                var (x, y) = ((i + 0.5) * c, (j + 0.5) * c);
                if (world.Inside(x, y) || world.InPit(x, y))
                    nav.Hits[(i, j, NavMap.K(Z))] = 0.0;
            }
        }
        var way = nav.Plan(start, target, within, Z, 5000)!;
        return new[] { start }.Concat(way).Zip(way, (a, b) => a.Dist2d(b)).Sum();
    }

    [Theory]
    [InlineData("open field")]
    [InlineData("wide wall")]
    [InlineData("very wide wall")]
    [InlineData("dead end")]
    [InlineData("room door far side")]
    [InlineData("zigzag")]
    [InlineData("wall with a gap")]
    [InlineData("pit")]
    [InlineData("pillars")]
    [InlineData("narrow door")]
    public void Walk_to_finds_its_way(string name)
    {
        var (_, setup, target, within) = Levels.All.First(level => level.Name == name);
        var world = new World(setup);
        var game = world.Game();
        var (res, _) = world.Run(game, ct => Control.WalkTo(game, target, within: within, timeout: 40, ct: ct), 45);
        Assert.True(res is { Ok: true }, $"{name}: {res}");
        Assert.False(world.Fell);
        var best = Shortest(new World(setup), new Vec(0, 0, Z), target, within);
        var bound = name == "room door far side" ? 2.5 : 1.35;
        Assert.True(world.Travelled < bound * best + 300, $"{name}: {world.Travelled:0} vs {best:0}");
    }

    [Fact]
    public void Walk_to_straight_across_open_floor_is_as_quick_as_before()
    {
        var world = new World(new WorldSetup());
        var game = world.Game();
        var (res, seconds) = world.Run(game, ct => Control.WalkTo(game, new Vec(2000, 300, Z), within: 100, timeout: 20, ct: ct));
        Assert.True(res!.Ok);
        Assert.Equal(0, res.Data["replans"]);
        Assert.True(seconds < 2000 / world.Setup.Speed + 0.8);
    }

    [Fact]
    public async Task A_way_planned_over_ticks_is_checked_against_what_was_seen_meanwhile()
    {
        var world = new World(new WorldSetup());
        var game = world.Game();
        var nav = await NavMap.Of(game);
        var k = NavMap.K(Z);
        // A stale wall across the whole map: the first plan runs over many ticks and fails, and the retry, with the wall
        // forgotten, searches the copy of the map taken before the obstacle below was seen.
        for (var j = -1200; j <= 1200; j++)
            nav.Hits[(100, j, k)] = -100.0;
        (int, int, int)[] obstacle = [(16, -1, k), (16, 0, k)];
        var stepper = new Stepper(game, ct => Control.WalkTo(game, new Vec(5000, 0, Z), within: 100, timeout: 40, ct: ct));
        var crossed = false;
        for (var tick = 0; tick < 45 * 30 && !stepper.Done; tick++)
        {
            await stepper.Step();
            if (tick == 2)
            {
                foreach (var cell in obstacle)
                    nav.Hits[cell] = 1e9;
                nav.Version++;
            }
            world.Step();
            crossed |= world.Pos.X is >= 400 and < 425 && world.Pos.Y is >= -25 and < 25;
        }
        Assert.True(stepper.Done, "the walk didn't end");
        var result = await stepper.Task!;
        Assert.True(result.Ok, result.ToString());
        Assert.False(crossed, "walked through a cell known to be blocked");
    }

    [Fact]
    public void A_wide_arrival_circle_is_not_gone_through_cell_by_cell()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var way = new NavMap().Plan(new Vec(0, 0, Z), new Vec(30000, 0, Z), 25000, Z);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(way);
        Assert.True(allocated < 20_000_000, $"{allocated} bytes");
    }

    [Fact]
    public void Walk_to_gives_up_on_a_target_it_cant_get_to()
    {
        var (_, setup, target, within) = Levels.All.First(level => level.Name == "closed room");
        var world = new World(setup);
        var game = world.Game();
        var (res, _) = world.Run(game, ct => Control.WalkTo(game, target, within: within, timeout: 30, ct: ct), 35);
        Assert.NotNull(res);
        Assert.False(res.Ok);
        Assert.Contains(res.Why, new[] { "stuck", "walk timed out" });
    }

    [Fact]
    public void Walk_to_remembers_the_level_for_the_next_walk()
    {
        var (_, setup, target, within) = Levels.All.First(level => level.Name == "dead end");
        var world = new World(setup);
        var game = world.Game();
        var (first, t1) = world.Run(game, ct => Control.WalkTo(game, target, within: within, timeout: 40, ct: ct), 45);
        (world.Pos, world.Yaw) = (new Vec(0, 0, Z), 0.0);
        var (second, t2) = world.Run(game, ct => Control.WalkTo(game, target, within: within, timeout: 40, ct: ct), 45);
        Assert.True(first!.Ok && second!.Ok);
        Assert.True(t2 < t1, $"{t2} vs {t1}");
    }

    [Fact]
    public void Walk_to_through_random_clutter()
    {
        var arrived = 0;
        for (uint seed = 0; seed < 12; seed++)
        {
            var (setup, target) = Levels.Clutter(seed);
            var world = new World(setup);
            var game = world.Game();
            var (res, _) = world.Run(game, ct => Control.WalkTo(game, target, within: 100, timeout: 40, ct: ct), 45);
            arrived += res is { Ok: true } ? 1 : 0;
        }
        Assert.Equal(12, arrived);
    }

    [Fact]
    public void Walk_to_never_walks_off_a_deep_drop()
    {
        var world = new World(new WorldSetup { Pits = [new Box(400, -5000, 900, 5000)] });
        var game = world.Game();
        var (res, _) = world.Run(game, ct => Control.WalkTo(game, new Vec(1300, 0, Z), within: 100, timeout: 20, ct: ct), 25);
        Assert.NotNull(res);
        Assert.False(res.Ok);
        Assert.False(world.Fell);
    }

    [Fact]
    public void Walk_to_gives_up_soon_when_the_view_wont_turn()
    {
        var world = new World(new WorldSetup { Yaw = 180, ViewLocked = true });
        var game = world.Game();
        var (res, seconds) = world.Run(game, ct => Control.WalkTo(game, new Vec(1500, 0, Z), within: 100, timeout: 25, ct: ct), 30);
        Assert.NotNull(res);
        Assert.False(res.Ok);
        Assert.Contains("turn", res.Why);
        Assert.True(seconds < 4.0);
    }

    [Fact]
    public void Python_random_gives_upstreams_numbers()
    {
        var rnd = new PyRandom(0);
        Assert.Equal(0.8444218515250481, rnd.NextDouble());
        Assert.Equal(0.7579544029403025, rnd.NextDouble());
    }
}
