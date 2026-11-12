using System.Diagnostics;
using System.Text.Json;
using Cluewren.Kit;
using Cluewren.Tests.Sim;

namespace Cluewren.Tests;

public sealed record Enemy(string Actor, double Speed, double Reach, double Period, double Damage, double Regen = 0, double Max = 0);

public sealed class ArenaLink : SimLink
{
    public const string Hero = "Arena:/Hero_C_0";
    public const string Gob1 = "Arena:/BP_Goblin_C_0";
    public const string Gob2 = "Arena:/BP_Goblin_C_1";
    public const string Crate = "Arena:/Crate_C_0";

    readonly Lock _gate = new();
    long _wall = Stopwatch.GetTimestamp();
    double _bitAt = -1e9;

    public ArenaLink() => GameTime = 0;

    public Dictionary<string, Vec> Pos { get; } = new()
    {
        [Hero] = new Vec(0, 0, 90), [Gob1] = new Vec(600, 0, 90), [Gob2] = new Vec(-900, 300, 90), [Crate] = new Vec(0, 400, 50),
    };

    public Dictionary<string, double> Health { get; } = new() { [Hero] = 100, [Gob1] = 30, [Gob2] = 30 };

    public HashSet<string> Gone { get; } = [];

    public double Yaw { get; set; }

    public double Pitch { get; set; }

    public HashSet<string> Held { get; } = [];

    public double Slope { get; set; }

    public bool ReadonlyHealth { get; set; }

    public Enemy? Enemy { get; set; }

    // Game time passing with each pose read, a tick's, instead of the wall clock's: a loaded machine then can't let the
    // enemy cover more ground between two reads than an idle one does.
    public double? SecondsPerPose { get; set; }

    public double Ground(double x) => Slope * (x - 600);

    static double Mod(double value, double by) => value - by * Math.Floor(value / by);

    bool Facing(Vec a, Vec b)
    {
        var want = Geom.ToDegrees(Math.Atan2(b.Y - a.Y, b.X - a.X));
        return Math.Abs(Mod(Yaw - want + 180, 360) - 180) < 10;
    }

    void Advance(string op)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = SecondsPerPose is { } frame ? (op == "pose" ? frame : 0) : Stopwatch.GetElapsedTime(_wall, now).TotalSeconds;
        _wall = now;
        GameTime = (GameTime ?? 0) + dt;
        var me = Pos[Hero];
        if (Held.Contains("W") && Health[Hero] > 0)
            me += Geom.Forward(Yaw) * (200 * dt);
        Pos[Hero] = new Vec(me.X, me.Y, Ground(me.X) + 90);
        if (Enemy is not { } e || Gone.Contains(e.Actor) || Health[Hero] <= 0)
            return;
        var g = Pos[e.Actor];
        var d = g.Dist2d(Pos[Hero]);
        if (d > e.Reach && e.Speed != 0)
        {
            var step = Math.Min(e.Speed * dt, d - e.Reach);
            g += (Pos[Hero] - g).Flat.Unit * step;
            Pos[e.Actor] = new Vec(g.X, g.Y, Ground(g.X) + 90);
        }
        else if (d <= e.Reach && GameTime - _bitAt >= e.Period)
        {
            Health[Hero] -= e.Damage;
            _bitAt = GameTime.Value;
        }
        if (e.Regen != 0)
            Health[e.Actor] = Math.Min(e.Max, Health[e.Actor] + e.Regen * dt);
    }

    static Dictionary<string, object?> D(params (string Key, object? Value)[] items)
    {
        var fields = new Dictionary<string, object?>();
        foreach (var (key, value) in items)
            fields[key] = value;
        return fields;
    }

    protected override object? Answer(string op, JsonElement args)
    {
        lock (_gate)
        {
            Advance(op);
            switch (op)
            {
                case "context":
                    return D(("world", "Arena:/"), ("character", Health[Hero] > 0 ? Hero : ""), ("controller", ""),
                        ("camera_manager", ""), ("game_time", GameTime));
                case "key":
                    Key(args);
                    return D();
                case "pose":
                    return D(("at", Pos[Hero].Ue()), ("eye", (Pos[Hero] + new Vec(0, 0, 60)).Ue()), ("yaw", Geom.Wrap(Yaw)),
                        ("pitch", Pitch), ("velocity", new Vec().Ue()));
                case "movement":
                    return D(("walk_speed", 600));
                case "find":
                    return Find(args);
                case "transforms":
                    var found = new Dictionary<string, object?>();
                    foreach (var item in args.GetProperty("objects").EnumerateArray())
                    {
                        var obj = item.GetString()!;
                        found[obj] = !Pos.TryGetValue(obj, out var at) ? null : D(("location", at.Ue()), ("rotation", new Vec().Ue()),
                            ("velocity", new Vec().Ue()), ("center", at.Ue()), ("extent", new Vec(40, 40, 90).Ue()));
                    }
                    return D(("objects", found));
                case "rc":
                    return Rc(args);
                default:
                    return D();
            }
        }
    }

    void Key(JsonElement args)
    {
        var (key, mode) = (Str(args, "key"), Str(args, "mode"));
        if (mode == "release_all")
        {
            Held.Clear();
        }
        else if (mode == "hold")
        {
            Held.Add(key!);
        }
        else if (mode == "release")
        {
            Held.Remove(key!);
        }
        else if (key == "MouseX" && mode == "press")
        {
            Yaw = Mod(Yaw + 0.1 * Num(args, "value"), 360);
        }
        else if (key == "MouseY" && mode == "press")
        {
            Pitch = Math.Max(-89, Math.Min(89, Pitch + 0.1 * Num(args, "value")));
        }
        else if (key == "LeftMouseButton" && mode == "press")
        {
            var me = Pos[Hero];
            var live = new[] { Gob1, Gob2 }.Where(g => !Gone.Contains(g)).ToList();
            if (live.Count == 0)
                return;
            var g = live.MinBy(a => Pos[a].Dist(me))!;
            var height = (Pos[g].Z - 90) - (me.Z - 90);
            if (Pos[g].Dist2d(me) <= 200 && Math.Abs(height) < 50 && Facing(me, Pos[g]))
            {
                Health[g] -= 10;
                Health[Hero] -= 3;
                if (Health[g] <= 0)
                    Gone.Add(g);
            }
        }
    }

    object? Find(JsonElement args)
    {
        var cls = Str(args, "class");
        if (cls is null)
            return D(("objects", Pos.Keys.Where(a => !Gone.Contains(a)).ToList()));
        if (!cls.StartsWith('/'))
            throw new GameException(400, $"no class named {cls}");
        if (cls.Contains("Goblin"))
            return D(("objects", new[] { Gob1, Gob2 }.Where(g => !Gone.Contains(g)).ToList()));
        return D(("objects", new List<string>()));
    }

    object? Rc(JsonElement args)
    {
        var (body, verb) = (args.GetProperty("body"), Str(args, "verb"));
        var (obj, fn) = (Str(body, "objectPath") ?? "", Str(body, "functionName"));
        if (verb == "describe")
        {
            if (Pos.ContainsKey(obj) && !Gone.Contains(obj))
                return D(("Name", obj), ("Class", "x"),
                    ("Properties", new List<object> { D(("Name", "Health"), ("Type", "float")) }), ("Functions", new List<object>()));
            throw new GameException(400, $"Object {obj} does not exist");
        }
        if (verb == "property")
        {
            var name = Str(body, "propertyName");
            if (Str(body, "access") == "WRITE_ACCESS")
            {
                var value = body.GetProperty("propertyValue").GetProperty(name!);
                if (name == "position" && Pos.ContainsKey(obj))
                {
                    Pos[obj] = new Unity().ToKit(new GameValue(value).Vector!.Value);
                    return D();
                }
                if (Health.ContainsKey(obj) && name == "Health" && !ReadonlyHealth)
                {
                    Health[obj] = value.GetDouble();
                    return D();
                }
                throw new GameException(400, $"Property {name} is read-only on object {obj}");
            }
            if (Health.TryGetValue(obj, out var health) && name == "Health")
                return D(("Health", health));
            if (obj == Hero && name == "MaxHealth")
                return D(("MaxHealth", 100));
            throw new GameException(400, $"Property {name} does not exist on object {obj}");
        }
        if (Pos.ContainsKey(obj) && fn == "AddItem")
        {
            var parameters = body.GetProperty("parameters");
            return D(("ReturnValue", true), ("Added", parameters.TryGetProperty("Amount", out var amount) ? amount.GetInt32() : 1));
        }
        throw new GameException(400, $"Function {fn} does not exist on object {obj}");
    }
}

[Collection("wall clock")]
public class ResearchTests
{
    const string Hero = ArenaLink.Hero;
    const string Gob1 = ArenaLink.Gob1;
    const string Gob2 = ArenaLink.Gob2;
    const string Crate = ArenaLink.Crate;

    readonly ArenaLink _link = new();
    readonly Game _game;

    public ResearchTests() => _game = Looking(_link);

    static Game Looking(ArenaLink link) => new(link) { Look = new LookScale { X = 0.1, Y = 0.1 } };

    static long Stat(Dictionary<string, object?> r, string which, string field) =>
        (long)((Dictionary<string, object?>)r[which]!)[field]!;

    static double?[] Pair(Dictionary<string, object?> r, string field) => (double?[])r[field]!;

    [Fact]
    public async Task Foes_are_the_living_nearest_first()
    {
        const string cls = "/Game/BP_Goblin.BP_Goblin_C";
        _link.Health[Gob2] = 20;
        var foes = await Research.Foes(_game, cls);
        Assert.Equal([Gob1, Gob2], foes.Select(foe => foe.Actor));
        Assert.Equal(new Foe(Gob1, new Vec(600, 0, 90), 600.0, 30, false, false), foes[0]);
        _link.Health[Gob1] = 0;
        Assert.Equal([Gob2], (await Research.Foes(_game, cls)).Select(foe => foe.Actor));
        _link.Health[Gob2] = 0;
        Assert.Empty(await Research.Foes(_game, cls));
    }

    [Fact]
    public async Task Nearest_sorts_by_distance_from_the_player()
    {
        var found = await Research.Nearest(_game, "/Game/BP_Goblin.BP_Goblin_C");
        Assert.Equal([(Gob1, 600.0), (Gob2, Math.Round(Math.Sqrt(900 * 900 + 300 * 300), 1))],
            found.Select(near => (near.Actor, near.Distance)));
        Assert.Equal(new Vec(600, 0, 90), found[0].At);
        Assert.Equal([new Near(Gob1, new Vec(600, 0, 90), 600.0)], await Research.Nearest(_game, "/Game/BP_Goblin.BP_Goblin_C", within: 700));
        Assert.Equal([Crate], (await Research.Nearest(_game, new[] { Crate, Gob1, Hero }, n: 1)).Select(near => near.Actor));
        Assert.Empty(await Research.Nearest(_game, "/Game/Nothing.Nothing_C"));
        var error = await Assert.ThrowsAsync<GameException>(async () => await Research.Nearest(_game, "Nothing"));
        Assert.Matches("nearest.*no class named Nothing", error.Message);
    }

    [Fact]
    public async Task Teleport_goes_through_the_games_call_and_is_reported()
    {
        var at = await Research.Teleport(_game, new Vec(100, 200, 90), why: "measure the gap");
        Assert.Equal(Hero, at["actor"]);
        Assert.Equal(new Vec(100, 200, 90), at["asked"]);
        Assert.Equal(new Vec(100, 200, 90), at["at"]);
        Assert.Equal(true, at["landed"]);
        Assert.Equal(4, at.Count);
        var moved = Assert.Single(_game.Writes);
        Assert.Equal(Hero, moved["object"]);
        Assert.Equal("position", moved["property"]);
        Assert.Equal("measure the gap", moved["why"]);
        Assert.Equal(new Vec(100, 200, 90), new Unity().ToKit(Native(moved["value"])));
        at = await Research.Teleport(_game, Gob1);
        var side = (new Vec(100, 200) - new Vec(600)).Unit;
        var expected = new Vec(600) + side * 150;
        Assert.Equal(new Vec(Math.Round(expected.X), Math.Round(expected.Y), 95), at["asked"]);
        Assert.Equal(true, at["landed"]);
        Assert.Equal(new Vec(Math.Round(expected.X), Math.Round(expected.Y), 90), at["at"]);
        at = await Research.Teleport(_game, Gob2, actor: Crate, why: "clear the way", distance: 300);
        var crate = _link.Pos[Crate];
        Assert.True(Math.Abs(crate.Dist2d(_link.Pos[Gob2]) - 300) < 1);
        Assert.Equal(90 - 90 + 90 + 5, crate.Z);
        Assert.Equal(Crate, _game.Writes[^1]["object"]);
        Assert.Equal("clear the way", _game.Writes[^1]["why"]);
        var error = await Assert.ThrowsAsync<GameException>(async () => await Research.Teleport(_game, "Arena:/Nobody_0"));
        Assert.Contains("teleport", error.Message);
    }

    static Vec Native(object? value)
    {
        var fields = (Dictionary<string, object?>)value!;
        return new Vec((double)fields["x"]!, (double)fields["y"]!, (double)fields["z"]!);
    }

    sealed class DuckLink(string pawn) : SimLink
    {
        Vec _at;

        protected override object? Answer(string op, JsonElement args)
        {
            switch (op)
            {
                case "context":
                    return new Dictionary<string, object?> { ["character"] = pawn };
                case "rc":
                    var body = args.GetProperty("body");
                    var value = body.GetProperty("propertyValue").GetProperty("position");
                    _at = new Unity().ToKit(new GameValue(value).Vector!.Value);
                    return new Dictionary<string, object?>();
                case "transforms":
                    return new Dictionary<string, object?>
                    {
                        ["objects"] = new Dictionary<string, object?>
                        {
                            [pawn] = new Dictionary<string, object?> { ["location"] = _at.Ue() },
                        },
                    };
                default:
                    throw new GameException(400, $"unknown op {op}");
            }
        }
    }

    [Fact]
    public async Task Teleport_on_the_other_engines()
    {
        var unity = new Game(new DuckLink("Main:/Player"));
        var at = await Research.Teleport(unity, new Vec(100, 200, 300), why: "check");
        var write = Assert.Single(unity.Writes);
        Assert.Equal("Main:/Player", write["object"]);
        Assert.Equal("position", write["property"]);
        Assert.Equal(new Vec(2.0, 3.0, 1.0), Native(write["value"]));
        Assert.Equal("check", write["why"]);
        Assert.Equal(true, at["landed"]);
        var error = await Assert.ThrowsAsync<GameException>(async () =>
            await Research.Teleport(new Game(new DuckLink("")), new Vec(1, 2, 3)));
        Assert.Contains("no player pawn", error.Message);
    }

    [Fact]
    public async Task Fight_trial_measures_hits_and_says_whether_it_can_be_won()
    {
        var r = await Research.FightTrial(_game, "/Game/BP_Goblin.BP_Goblin_C", seconds: 6, every: 0.05);
        Assert.Equal(Gob1, r["target"]);
        Assert.Equal(true, r["killed"]);
        Assert.Equal(3, r["hits"]);
        Assert.Equal(10.0, r["damage_per_hit"]);
        Assert.Equal([30.0, 0.0], Pair(r, "target_health"));
        Assert.Equal([100.0, 91.0], Pair(r, "player_health"));
        Assert.Equal(9.0, r["damage_taken"]);
        var props = (Dictionary<string, object?>)r["health_props"]!;
        Assert.Equal("Health", props["target"]);
        Assert.Equal("Health", props["player"]);
        Assert.Equal("on target", r["aim"]);
        var verdict = (string)r["verdict"]!;
        Assert.StartsWith("killed: 3 hits of 3 swings", verdict);
        Assert.Contains("lost 9 health", verdict);
        Assert.Equal(true, r["landed"]);
        Assert.Equal("arrived", r["walked"]);
        Assert.Equal("target down", r["stopped"]);
        Assert.True(Stat(r, "stance", "distance") is >= 90 and <= 160);
        Assert.Equal(0, Stat(r, "stance", "height"));
        Assert.Null(r["closer"]);
        Assert.Equal("position", _game.Writes[0]["property"]);
        Assert.Contains("fight trial", (string)_game.Writes[0]["why"]!);
        Assert.True(Math.Abs(new Unity().ToKit(Native(_game.Writes[0]["value"])).X - 280) < 1);
        Assert.Equal(true, r["healed"]);
        Assert.Equal(100.0, r["player_health_max"]);
        Assert.Equal(false, r["player_dead"]);
        Assert.Equal(100.0, _link.Health[Hero]);
        Assert.Equal("Health", _game.Writes[^1]["property"]);
        Assert.Equal(100.0, _game.Writes[^1]["value"]);
        Assert.Equal("research: undo the trial's damage", _game.Writes[^1]["why"]);
        Assert.Contains("the player's health was written back to 100", verdict);
        _link.Pos[Gob2] = new Vec(-900, 300, 90);
        _link.Health[Gob2] = 30;
        r = await Research.FightTrial(_game, Gob2, seconds: 0.3, every: 0.05, reach: 400);
        verdict = (string)r["verdict"]!;
        Assert.Equal(0, r["hits"]);
        Assert.Equal(false, r["killed"]);
        Assert.Contains("no swing", verdict);
        Assert.Contains("out of reach", verdict);
        Assert.Equal("time up", r["stopped"]);
        Assert.Null(r["closer"]);
        Assert.True(Stat(r, "stance", "distance") is >= 370 and <= 440);
        Assert.Contains($"from {Stat(r, "stance", "distance")} units and", verdict);
        Assert.Null(r["healed"]);
    }

    [Fact]
    public async Task Fight_trial_on_a_slope_reports_the_stance()
    {
        _link.Slope = 0.25;
        var r = await Research.FightTrial(_game, Gob1, seconds: 6, every: 0.05);
        Assert.Equal(true, r["killed"]);
        Assert.Equal(3, r["hits"]);
        Assert.Null(r["closer"]);
        Assert.True(Stat(r, "stance", "distance") is >= 100 and <= 160 && Stat(r, "stance", "height") is >= 20 and <= 45,
            Fmt.Json(r["stance"]));
        Assert.Equal(_link.Ground(_link.Pos[Hero].X), _link.Pos[Hero].Z - 90, 6);
    }

    [Fact]
    public async Task Fight_trial_steps_closer_once_when_nothing_lands()
    {
        _link.Slope = 0.6;
        var r = await Research.FightTrial(_game, Gob1, seconds: 6, every: 0.05);
        Assert.NotNull(r["closer"]);
        Assert.True(Stat(r, "closer", "distance") < Stat(r, "stance", "distance"), Fmt.Json(r));
        var (height, closer) = (Stat(r, "stance", "height"), Stat(r, "closer", "height"));
        Assert.True(height >= 50 && 50 > closer && closer > 0, Fmt.Json(r));
        Assert.Equal(true, r["killed"]);
        Assert.Equal(3, r["hits"]);
        Assert.True((int)r["swings"]! >= Research.CloserAfter + 3);
        Assert.StartsWith("killed", (string)r["verdict"]!);
        var link = new ArenaLink { Slope = 1.0 };
        var game = Looking(link);
        r = await Research.FightTrial(game, Gob1, seconds: 1.0, every: 0.05);
        Assert.Equal(0, r["hits"]);
        Assert.NotNull(r["closer"]);
        Assert.Equal("time up", r["stopped"]);
        Assert.StartsWith($"no hit from {Stat(r, "stance", "distance")} units and {Stat(r, "stance", "height")} height; closer "
                          + $"({Stat(r, "closer", "distance")}) neither", (string)r["verdict"]!);
        Assert.True((double)r["seconds"]! < 3.0);
    }

    [Fact]
    public async Task Fight_trial_heals_the_player_or_says_it_died()
    {
        _link.Health[Hero] = 5;
        var r = await Research.FightTrial(_game, Gob1, seconds: 6, every: 0.05);
        Assert.Equal(true, r["player_dead"]);
        Assert.Null(r["healed"]);
        Assert.Equal(false, r["killed"]);
        Assert.Equal("the player died", r["stopped"]);
        Assert.Equal([5.0, -1.0], Pair(r, "player_health"));
        Assert.Equal(-1.0, _link.Health[Hero]);
        Assert.Empty(await _game.Pawn());
        var verdict = (string)r["verdict"]!;
        Assert.Contains("the player died: the level must be restarted", verdict);
        Assert.Contains("run_harness", verdict);
        Assert.DoesNotContain(_game.Writes, w => (string?)w.GetValueOrDefault("property") == "Health");
        var link = new ArenaLink { ReadonlyHealth = true };
        var game = Looking(link);
        r = await Research.FightTrial(game, Gob1, seconds: 6, every: 0.05);
        Assert.Equal(true, r["killed"]);
        Assert.Equal(false, r["healed"]);
        Assert.Equal(91.0, link.Health[Hero]);
        Assert.Contains("the player is left at 91 health (its Health couldn't be written back)", (string)r["verdict"]!);
        link = new ArenaLink();
        game = Looking(link);
        r = await Research.FightTrial(game, Gob1, seconds: 6, every: 0.05, heal: false);
        Assert.Null(r["healed"]);
        Assert.Equal(91.0, link.Health[Hero]);
        Assert.StartsWith("killed", (string)r["verdict"]!);
    }

    static Dictionary<string, object?> With(Dictionary<string, object?> r, params (string Key, object? Value)[] changes)
    {
        var copy = new Dictionary<string, object?>(r);
        foreach (var (key, value) in changes)
            copy[key] = value;
        return copy;
    }

    [Fact]
    public void Fight_trial_verdict_projects_the_fight()
    {
        var stance = new Dictionary<string, object?> { ["distance"] = 118L, ["height"] = 3L };
        var props = new Dictionary<string, object?> { ["target"] = "Health", ["player"] = "Health" };
        var fight = new Dictionary<string, object?>
        {
            ["killed"] = false, ["hits"] = 2, ["swings"] = 4, ["damage_per_hit"] = 10.0, ["seconds"] = 2.0, ["aim"] = "on target",
            ["target_health"] = new double?[] { 100, 80 }, ["player_health"] = new double?[] { 100, 60 }, ["damage_taken"] = 40.0,
            ["health_props"] = props, ["stance"] = stance, ["closer"] = null, ["player_dead"] = false, ["healed"] = null,
        };
        var words = Research.Verdict(fight, 0.6);
        Assert.StartsWith("8 more hits (10 each) would kill it, about 8 s more", words);
        Assert.Contains("the player would die first, in about 3 s", words);
        Assert.Contains("the player would outlast it", Research.Verdict(With(fight, ("damage_taken", 4.0)), 0.6));
        Assert.Contains("took no damage", Research.Verdict(With(fight, ("damage_taken", 0.0)), 0.6));
        Assert.Contains("no health property", Research.Verdict(With(fight, ("hits", 0),
            ("health_props", new Dictionary<string, object?> { ["target"] = null, ["player"] = "Health" })), 0.6));
        Assert.StartsWith("no swing of 4 changed its health from 118 units and 3 height: out of reach",
            Research.Verdict(With(fight, ("hits", 0)), 0.6));
        var closer = Research.Verdict(With(fight, ("hits", 0),
            ("closer", new Dictionary<string, object?> { ["distance"] = 70L, ["height"] = 2L })), 0.6);
        Assert.StartsWith("no hit from 118 units and 3 height; closer (70) neither", closer);
        Assert.Contains("reach under 70", closer);
        Assert.StartsWith("no swing of 4 changed its health from an unread distance",
            Research.Verdict(With(fight, ("hits", 0), ("stance", null)), 0.6));
        Assert.EndsWith("(run_harness restarts it by default)", Research.Verdict(With(fight, ("player_dead", true)), 0.6));
    }

    [Fact]
    public async Task Enemy_profile_measures_an_enemy_without_fighting_it()
    {
        _link.Enemy = new Enemy(Gob1, 400, 110, 0.3, 25, 2.0, 30);
        _link.Health[Gob1] = 20;
        _link.SecondsPerPose = 1.0 / 15;
        var r = await Research.EnemyProfile(_game, "/Game/BP_Goblin.BP_Goblin_C", seconds: 6, safeDistance: 600);
        Assert.Equal(Gob1, r["target"]);
        Assert.True(r["comes"] is true, Fmt.Json(r));
        Assert.True((long)r["start_distance"]! is >= 480 and <= 610 && (long)r["min_distance"]! <= 115, Fmt.Json(r));
        Assert.True((long)r["approach_speed"]! is >= 300 and <= 500 && (long)r["speed"]! is >= 300 and <= 500, Fmt.Json(r));
        Assert.Equal(600.0, r["player_speed"]);
        Assert.Equal(3, r["hits"]);
        Assert.Equal(25.0, r["damage_per_hit"]);
        Assert.Equal(75.0, r["damage_taken"]);
        Assert.True((long)r["attack_range"]! is >= 100 and <= 125 && (double)r["attack_period"]! is >= 0.2 and <= 0.45, Fmt.Json(r));
        Assert.True(Pair(r, "target_health")[0] is >= 20 and < 21 && (double)r["regen_per_s"]! is >= 1.0 and <= 3.0, Fmt.Json(r));
        Assert.Equal([100.0, 25.0], Pair(r, "player_health"));
        Assert.Equal(100.0, r["player_health_max"]);
        Assert.Equal(false, r["player_dead"]);
        Assert.StartsWith("the player's health fell under 40% (25)", (string)r["stopped"]!);
        Assert.True((double)r["seconds"]! < 4);
        Assert.True(_link.Pos[Hero].Dist2d(_link.Pos[Gob1]) >= 450);
        Assert.Equal(true, r["healed"]);
        Assert.Equal(100.0, _link.Health[Hero]);
        Assert.Equal([$"enemy profile of {Gob1}", $"enemy profile of {Gob1}: back to safety", "research: undo the profile's damage"],
            _game.Writes.Where(w => w.GetValueOrDefault("why") is string { Length: > 0 }).Select(w => (string)w["why"]!));
        var summary = (string)r["summary"]!;
        Assert.StartsWith($"comes at the player at {r["approach_speed"]}/s (the player walks 600): can be out-run", summary);
        Assert.Contains($"hits for 25 every {Fmt.G((double)r["attack_period"]!)} s from {r["attack_range"]} away", summary);
        Assert.Contains($"health regenerates {Fmt.G((double)r["regen_per_s"]!)}/s", summary);
        Assert.Contains("stopped early", summary);
        var props = (Dictionary<string, object?>)r["health_props"]!;
        Assert.Equal("Health", props["target"]);
        Assert.Equal("Health", props["player"]);
        var link = new ArenaLink { Enemy = new Enemy(Gob2, 0, 110, 0.3, 25) };
        var game = Looking(link);
        r = await Research.EnemyProfile(game, Gob2, seconds: 0.6, safeDistance: 500);
        Assert.Equal(false, r["comes"]);
        Assert.Equal(0L, r["speed"]);
        Assert.Equal(0, r["hits"]);
        Assert.Null(r["attack_range"]);
        Assert.Equal(0.0, r["regen_per_s"]);
        Assert.Equal("time up", r["stopped"]);
        Assert.Null(r["healed"]);
        summary = (string)r["summary"]!;
        Assert.StartsWith($"doesn't come at the player in {Fmt.G((double)r["seconds"]!)} s from 500 away (passive until hit?); moves "
                          + "at 0/s at most (the player walks 600); never attacked (nearest ", summary);
        Assert.EndsWith("; health doesn't regenerate on its own", summary);
    }

    [Fact]
    public void Enemy_profile_summary_in_words()
    {
        var profile = new Dictionary<string, object?>
        {
            ["comes"] = true, ["approach_speed"] = 400L, ["speed"] = 420L, ["player_speed"] = 300.0, ["seconds"] = 20.0,
            ["start_distance"] = 1200L, ["min_distance"] = 100L, ["hits"] = 2, ["damage_per_hit"] = 25.0, ["attack_period"] = 1.6,
            ["attack_range"] = 110L, ["regen_per_s"] = 1.0, ["stopped"] = "time up",
        };
        Assert.Equal("comes at the player at 400/s (the player walks 300): can't be out-run; hits for 25 every 1.6 s from 110 away; "
                     + "health regenerates 1/s", Research.ProfileSummary(profile));
        Assert.Equal("comes at the player at 400/s; hits for 25 from 110 away; no health property found on it",
            Research.ProfileSummary(With(profile, ("player_speed", null), ("attack_period", null), ("regen_per_s", null))));
        Assert.EndsWith("; the player died: the level must be restarted (run_harness restarts it by default)",
            Research.ProfileSummary(With(profile, ("player_dead", true))));
    }

    [Fact]
    public async Task Call_any_takes_the_first_name_the_game_accepts()
    {
        var (called, result) = await Research.CallAny(_game, Hero, ["GiveItem", "AddItem"],
            new Dictionary<string, object?> { ["Item"] = "Sword", ["Amount"] = 2 });
        Assert.Equal("AddItem", called);
        Assert.Equal("""{"ReturnValue":true,"Added":2}""", result.Element.GetRawText());
        var error = await Assert.ThrowsAsync<GameException>(async () => await Research.CallAny(_game, Hero, ["Nope", "Nor"]));
        Assert.Matches("none accepted: Nope: .*Function Nope.*; Nor: ", error.Message);
    }
}
