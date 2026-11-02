using System.Diagnostics;
using System.Text.Json;
using PlaytestAgent.Kit;
using PlaytestAgent.Tests.Sim;

namespace PlaytestAgent.Tests;

public sealed class OpLink : SimLink
{
    readonly Lock _gate = new();
    long _wall = Stopwatch.GetTimestamp();
    readonly HashSet<string> _held = [];

    readonly Dictionary<string, Vec> _things = new()
    {
        ["Level/Door_A"] = new Vec(500, 0, 100), ["Level/door_b"] = new Vec(-300, 400, 100), ["Level/Lamp"] = new Vec(0, 900, 300),
    };

    readonly Dictionary<string, List<string>> _classes = new()
    {
        ["Door"] = ["Level/Door_A", "Level/door_b"], ["Lamp"] = ["Level/Lamp"],
    };

    public OpLink() => GameTime = 0;

    public Vec Pos { get; private set; } = new(0, 0, 90);

    public double Yaw { get; private set; }

    public double Pitch { get; private set; }

    public List<string> Log { get; } = [];

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
            var now = Stopwatch.GetTimestamp();
            var dt = Stopwatch.GetElapsedTime(_wall, now).TotalSeconds;
            _wall = now;
            GameTime = (GameTime ?? 0) + dt;
            if (_held.Contains("W"))
                Pos += Geom.Forward(Yaw) * (400 * dt);
            Log.Add(op);
            switch (op)
            {
                case "context":
                    return D(("world", "Level"), ("character", "Level/Player"), ("controller", ""), ("camera_manager", ""),
                        ("project_dir", ""));
                case "find":
                    return Find(args);
                case "transforms":
                    return Transforms(args);
                case "pose":
                    return D(("at", Pos.Ue()), ("eye", (Pos + new Vec(0, 0, 70)).Ue()), ("yaw", Yaw), ("pitch", Pitch),
                        ("velocity", new Vec().Ue()), ("on_ground", true));
                case "movement":
                    return D(("walk_speed", 400), ("jump_z", 500), ("gravity", 980), ("half_height", 90), ("radius", 40),
                        ("step", 30), ("max_slope", 45));
                case "view":
                    return D(("visible", new List<object>()), ("body", D()));
                case "pause":
                    return D(("paused", !args.TryGetProperty("paused", out var paused) || paused.GetBoolean()));
                case "key":
                    Key(args);
                    return D();
                case "rc":
                    var body = args.GetProperty("body");
                    var obj = Str(body, "objectPath");
                    if (Str(args, "verb") == "describe")
                    {
                        if (_things.ContainsKey(obj!))
                            return D(("Name", obj), ("Class", "GameObject"));
                        throw new GameException(400, $"Object {obj} does not exist");
                    }
                    if (Str(args, "verb") == "property" && Str(body, "propertyName") == "position")
                        return D(("position", D(("x", 2.0), ("y", 1.0), ("z", 5.0))));
                    break;
            }
            throw new GameException(400, $"unknown op {op}");
        }
    }

    object? Find(JsonElement args)
    {
        var objects = Str(args, "class") is { } cls ? _classes.GetValueOrDefault(cls, []) : [.. _things.Keys, "Level/Player"];
        return D(("objects", objects));
    }

    object? Transforms(JsonElement args)
    {
        var found = new Dictionary<string, object?>();
        foreach (var item in args.GetProperty("objects").EnumerateArray())
        {
            var obj = item.GetString()!;
            Vec? at = obj == "Level/Player" ? Pos : _things.TryGetValue(obj, out var thing) ? thing : null;
            found[obj] = at is not { } point ? null : D(("location", point.Ue()),
                ("rotation", D(("Pitch", 0), ("Yaw", 0), ("Roll", 0))), ("velocity", new Vec().Ue()),
                ("center", (point + new Vec(0, 0, 50)).Ue()), ("extent", new Vec(50, 10, 100).Ue()));
        }
        return D(("objects", found));
    }

    void Key(JsonElement args)
    {
        var (key, mode) = (Str(args, "key"), Str(args, "mode"));
        if (mode == "release_all")
            _held.Clear();
        else if (mode == "hold")
            _held.Add(key!);
        else if (mode == "release")
            _held.Remove(key!);
        else if (key == "MouseX")
            Yaw = Geom.Wrap(Yaw + 0.1 * Num(args, "value"));
        else if (key == "MouseY")
            Pitch = Math.Max(-89, Math.Min(89, Pitch + 0.1 * Num(args, "value")));
    }
}

public sealed class MoveLink(string engine) : SimLink, IGameLink
{
    public string Engine => engine;

    public string? Moved { get; private set; }

    protected override object? Answer(string op, JsonElement args)
    {
        if (op == "rc")
        {
            var body = args.GetProperty("body");
            Moved = body.TryGetProperty("functionName", out var function) ? function.GetString() : body.GetProperty("propertyName").GetString();
            return new Dictionary<string, object?>();
        }
        var at = new Dictionary<string, object?> { ["location"] = new Vec().Ue() };
        return new Dictionary<string, object?> { ["objects"] = new Dictionary<string, object?> { ["Level/Player"] = at } };
    }
}

[Collection("wall clock")]
public class EngineTests : TempTests
{
    static readonly Unity Engine = new();

    [Fact]
    public void An_anonymous_objects_names_reach_the_game_as_written()
    {
        var json = Fmt.Json(new { NewLocation = new Dictionary<string, object?> { ["X"] = 1 }, bSweep = false, new_name = 2 });
        Assert.Equal("""{"NewLocation":{"X":1},"bSweep":false,"new_name":2}""", json);
    }

    [Theory]
    [InlineData("unity", "position")]
    [InlineData("godot", "global_position")]
    [InlineData("unreal", "K2_TeleportTo")]
    public async Task Teleport_uses_what_each_engine_moves_an_actor_with(string engine, string how)
    {
        var link = new MoveLink(engine);
        var game = new Game(link);
        await Research.Teleport(game, new Vec(100, 200, 300), actor: "Level/Player", why: "set up");
        Assert.Equal(how, link.Moved);
        Assert.Equal("set up", game.Writes[^1]["why"]);
    }

    static GameValue Value(object? value)
    {
        using var document = JsonDocument.Parse(Jsonable.Encode(value, round: false));
        return GameValue.Of(document.RootElement);
    }

    static Dictionary<string, object?> Euler(double x, double y, double z) => new() { ["x"] = x, ["y"] = y, ["z"] = z };

    static Vec Native(Dictionary<string, object?> v) => new((double)v["x"]!, (double)v["y"]!, (double)v["z"]!);

    static Vec UnityForward(Dictionary<string, object?> rot)
    {
        var (x, y) = (Geom.Radians((double)rot["x"]!), Geom.Radians((double)rot["y"]!));
        return new Vec(Math.Cos(x) * Math.Sin(y), -Math.Sin(x), Math.Cos(x) * Math.Cos(y));
    }

    static bool Close(Vec a, Vec b, double tol = 1e-6) =>
        Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol && Math.Abs(a.Z - b.Z) <= tol;

    [Fact]
    public void Frames_round_trip()
    {
        foreach (var v in new[] { new Vec(120, -35.5, 980), new Vec(), new Vec(-1, 2, -3) })
            Assert.True(Close(Engine.ToKit(Native(Engine.ToNative(v))), v), v.ToString());
        foreach (var yaw in new double[] { -170, -90, -30, 0, 45, 90, 135, 180 })
        {
            foreach (var pitch in new double[] { -60, -10, 0, 25, 80 })
            {
                var rot = Engine.RotationToNative(yaw, pitch);
                var (ky, kp) = Engine.RotationToKit(Value(rot));
                var turned = (ky - yaw + 180) % 360;
                Assert.True(Math.Abs((turned < 0 ? turned + 360 : turned) - 180) < 1e-6 && Math.Abs(kp - pitch) < 1e-6, $"{yaw}, {pitch}");
                Assert.True(Close(Engine.ToKit(UnityForward(rot)) / 100.0, Geom.Forward(yaw, pitch)), $"{yaw}, {pitch}");
            }
        }
    }

    [Fact]
    public void Godots_forward_is_minus_z_and_its_angles_are_radians()
    {
        var godot = PlaytestAgent.Kit.Engine.For("godot");
        Assert.Equal(new Vec(100, 0, 0), godot.ToKit(new Vec(0, 0, -1)));
        Assert.Equal(new Vec(0, 100, 0), godot.ToKit(new Vec(1)));
        Assert.Equal(new Vec(0, 0, 100), godot.ToKit(new Vec(0, 1)));
        var (yaw, pitch) = godot.RotationToKit(Value(godot.RotationToNative(-90, 30)));
        Assert.Equal(-90, yaw, 9);
        Assert.Equal(30, pitch, 9);
        Assert.Equal("Player", godot.NameOf("/root/Main/Player:position"));
        var native = godot.ToNative(new Vec(120, -35.5, 980));
        Assert.True(Close(godot.ToKit(new Vec((double)native["x"]!, (double)native["y"]!, (double)native["z"]!)), new Vec(120, -35.5, 980)));
    }

    [Fact]
    public void Unreals_frame_is_the_kits()
    {
        var unreal = PlaytestAgent.Kit.Engine.For("unreal");
        Assert.Equal(new Vec(1, 2, 3), unreal.ToKit(new Vec(1, 2, 3)));
        Assert.Equal((90.0, -30.0), unreal.RotationToKit(Value(unreal.RotationToNative(90, -30))));
        Assert.Equal("BP_Door_C_3", unreal.NameOf("/Game/M.M:PersistentLevel.BP_Door_C_3"));
    }

    [Fact]
    public void Frames_known_values()
    {
        Assert.Equal(new Vec(100, 0, 0), Engine.ToKit(Value(Euler(0, 0, 1))));
        Assert.Equal(new Vec(0, 100, 0), Engine.ToKit(Value(Euler(1, 0, 0))));
        Assert.Equal(new Vec(0, 0, 100), Engine.ToKit(Value(new[] { 0, 1, 0 })));
        Assert.Equal((90, -30), Engine.RotationToKit(Value(Euler(30, 90, 0))));
        Assert.Equal((-90, 30), Engine.RotationToKit(Value(Euler(330, 270, 0))));
        var (x, y, z) = (Engine.ToKit(new Vec(1)), Engine.ToKit(new Vec(0, 1)), Engine.ToKit(new Vec(0, 0, 1)));
        var det = x.X * (y.Y * z.Z - y.Z * z.Y) - x.Y * (y.X * z.Z - y.Z * z.X) + x.Z * (y.X * z.Y - y.Y * z.X);
        Assert.True(det > 0);
    }

    [Fact]
    public async Task Game_backend_over_the_engine_ops()
    {
        var link = new OpLink();
        var game = new Game(link);
        Assert.Equal("unity", Engine.Name);
        Assert.Equal(["Level/Door_A", "Level/door_b"], await game.Actors("Door"));
        Assert.Equal(new[] { "Level/Door_A", "Level/Lamp", "Level/Player", "Level/door_b" }, (await game.AllActors()).Order(StringComparer.Ordinal));
        Assert.Equal(["Level/Door_A", "Level/door_b"], await game.Named("door"));
        Assert.Equal("Door", game.ResolveClass("Door"));
        Assert.Equal(new Vec(0, 900, 300), await game.Loc("Level/Lamp"));
        link.Log.Clear();
        var locs = await game.Locs(["Level/Door_A", "Level/Lamp", "Level/Gone"]);
        Assert.Equal(2, locs.Count);
        Assert.Equal(new Vec(500, 0, 100), locs["Level/Door_A"]);
        Assert.Equal(new Vec(0, 900, 300), locs["Level/Lamp"]);
        Assert.Equal(["transforms"], link.Log);
        var (center, ext) = await game.Bounds("Level/Door_A");
        Assert.Equal(new Vec(500, 0, 150), center);
        Assert.Equal(new Vec(50, 10, 100), ext);
        Assert.Equal(0, (await game.Rot("Level/Door_A"))["Yaw"].Number);
        Assert.Equal(new Vec(), await game.Velocity("Level/Door_A"));
        await Assert.ThrowsAsync<GameException>(async () => await game.Loc("Level/Gone"));
        Assert.True(await game.Exists("Level/Lamp"));
        Assert.False(await game.Exists("Level/Gone"));
        var p = await game.Pose();
        Assert.Equal(new Vec(0, 0, 90), p.At);
        Assert.Equal(new Vec(0, 0, 160), p.Eye);
        Assert.True(p.OnGround);
        Assert.Equal(new Vec(0, 0, 160), await game.Eye());
        Assert.Equal((0.0, 0.0), await game.ViewRotation());
        Assert.Equal(new Vec(0, 0, 90), await game.Me());
        var m = await Control.Movement(game);
        Assert.Equal(400, m.WalkSpeed);
        Assert.Equal(90, m.HalfHeight);
        Assert.Equal(40, m.Radius);
        Assert.True(Math.Abs(m.Peak!.Value - 500.0 * 500 / (2 * 980)) < 1e-9);
        Assert.Equal(m.Peak + 30, m.ReachUp);
        Assert.Equal(new Vec(500, 200, 100), game.ToKit(await game.Get("Level/Lamp", "position")));
        Assert.Equal("Door_A", game.NameOf("Level/Door_A"));
        Assert.Equal("Gem@2", game.NameOf("Main:/Level/Pickups/Gem@2"));
        Assert.Equal("Player", game.NameOf("Main:/Player|PlayerMover"));
    }

    [Fact]
    public async Task Controllers_play_over_the_engine_ops()
    {
        var link = new OpLink();
        var game = Looking(link);
        var r = await Drive(game, ct => Control.WalkTo(game, "Level/Door_A", within: 80, timeout: 8, ct: ct), 10);
        Assert.True(r.Ok, r.Why);
        Assert.True(link.Pos.Dist2d(new Vec(500, 0, 100)) <= 80);
    }

    [Fact]
    public void Name_of_follows_the_plugins_paths()
    {
        Assert.Equal("Coin@2", Engine.NameOf("Main:/Level/Coins/Coin@2"));
        Assert.Equal("Coin", Engine.NameOf("Main:/Level/Coins/Coin"));
        Assert.Equal("Player", Engine.NameOf("Main:/Player|PlayerMover"));
        Assert.Equal("Player", Engine.NameOf("Main:/Player|PlayerMover@1"));
        Assert.Equal("Door", Engine.NameOf("/Level/Door"));
        Assert.Equal(@"Door 1\/2", Engine.NameOf(@"Main:/Level/Door 1\/2|Hinge"));
        Assert.Equal(@"Pipe\|Valve", Engine.NameOf(@"Main:/Pipe\|Valve"));
        Assert.Equal(@"A\@B@1", Engine.NameOf(@"Main:/A\@B@1"));
    }

    [Fact]
    public void Rotations_from_rc()
    {
        Assert.Equal((-90, -10), Engine.RotationToKit(Value(Euler(10, 270, 0))));
        var quaternion = new Dictionary<string, object?>
        {
            ["x"] = 0, ["y"] = 0.7071, ["z"] = 0, ["w"] = 0.7071, ["euler"] = Euler(0, 90, 0),
        };
        Assert.Equal((90, 0), Engine.RotationToKit(Value(quaternion)));
        var (yaw, pitch) = (45.0, 20.0);
        Assert.Equal((yaw, pitch), Engine.RotationToKit(Value(Engine.RotationToNative(yaw, pitch))));
    }

    sealed class AskingLink : SimLink
    {
        public List<(string Op, string Args)> Asked { get; } = [];

        protected override object? Answer(string op, JsonElement args)
        {
            Asked.Add((op, args.GetRawText()));
            return new Dictionary<string, object?> { ["objects"] = new List<string> { "Main:/Door" } };
        }
    }

    [Fact]
    public async Task All_actors_asks_for_things()
    {
        var link = new AskingLink();
        Assert.Equal(["Main:/Door"], await new Game(link).AllActors());
        Assert.Equal([("find", """{"things":true}""")], link.Asked);
    }
}
