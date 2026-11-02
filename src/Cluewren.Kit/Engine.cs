namespace Cluewren.Kit;

// Harness code works in one frame whatever the engine: X forward, Y right, Z up, centimetres, yaw right, pitch up.
public abstract class Engine
{
    public static readonly IReadOnlyDictionary<string, string> KeyNames = new Dictionary<string, string>
    {
        ["forward"] = "W", ["back"] = "S", ["left"] = "A", ["right"] = "D", ["jump"] = "SpaceBar",
    };

    public static Engine For(string name) => name switch
    {
        Unity.Id => new Unity(),
        Godot.Id => new Godot(),
        Unreal.Id => new Unreal(),
        _ => throw new NotSupportedException($"no engine named {name}"),
    };

    public abstract string Name { get; }

    public abstract string NameOf(string path);

    public abstract Vec ToKit(Vec native);

    public Vec? ToKit(GameValue value) => value.Vector is { } native ? ToKit(native) : null;

    public abstract Dictionary<string, object?> ToNative(Vec kit);

    public abstract (double Yaw, double Pitch) RotationToKit(GameValue rotation);

    public abstract Dictionary<string, object?> RotationToNative(double yaw, double pitch = 0);
}

public sealed class Unreal : Engine
{
    public const string Id = "unreal";

    public override string Name => Id;

    public override string NameOf(string path) => path[(path.LastIndexOf('.') + 1)..];

    public override Vec ToKit(Vec native) => native;

    public override Dictionary<string, object?> ToNative(Vec kit) => new() { ["X"] = kit.X, ["Y"] = kit.Y, ["Z"] = kit.Z };

    public override (double Yaw, double Pitch) RotationToKit(GameValue rotation) =>
        (Geom.Wrap(rotation["Yaw"].Number ?? 0), Geom.Wrap(rotation["Pitch"].Number ?? 0));

    public override Dictionary<string, object?> RotationToNative(double yaw, double pitch = 0) => new()
    {
        ["Pitch"] = pitch, ["Yaw"] = yaw, ["Roll"] = 0.0,
    };
}

public sealed class Godot : Engine
{
    public const string Id = "godot";

    public override string Name => Id;

    public override string NameOf(string path)
    {
        var last = path.TrimEnd('/');
        last = last[(last.LastIndexOf('/') + 1)..];
        var property = last.IndexOf(':');
        return property < 0 ? last : last[..property];
    }

    public override Vec ToKit(Vec native) => new(-100.0 * native.Z, 100.0 * native.X, 100.0 * native.Y);

    public override Dictionary<string, object?> ToNative(Vec kit) => new()
    {
        ["x"] = kit.Y / 100.0, ["y"] = kit.Z / 100.0, ["z"] = -kit.X / 100.0,
    };

    public override (double Yaw, double Pitch) RotationToKit(GameValue rotation) =>
        (Geom.Wrap(-double.RadiansToDegrees(rotation["y"].Number ?? 0)),
            Geom.Wrap(double.RadiansToDegrees(rotation["x"].Number ?? 0)));

    public override Dictionary<string, object?> RotationToNative(double yaw, double pitch = 0) => new()
    {
        ["x"] = double.DegreesToRadians(pitch), ["y"] = -double.DegreesToRadians(Geom.Wrap(yaw)), ["z"] = 0.0,
    };
}
