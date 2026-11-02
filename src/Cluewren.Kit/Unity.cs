using System.Text;

namespace Cluewren.Kit;

public sealed class MovementInfo
{
    public string Pawn { get; init; } = "";

    public double? WalkSpeed { get; init; }

    public double? JumpZ { get; init; }

    public double? Gravity { get; init; }

    public double? HalfHeight { get; init; }

    public double? Radius { get; init; }

    public double? Step { get; init; }

    public double? MaxSlope { get; init; }

    public double? AirControl { get; init; }

    public double? Peak { get; init; }

    public double? ReachUp { get; init; }

    public double? ReachUpMax { get; init; }
}

public sealed class Unity : Engine
{
    public const string Id = "unity";

    public override string Name => Id;

    public override string NameOf(string path)
    {
        var obj = UnescapedSplit(path, '|')[0].TrimEnd('/');
        return UnescapedSplit(obj, '/')[^1];
    }

    static List<string> UnescapedSplit(string text, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(text, i, 2);
                i++;
            }
            else if (c == separator)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        parts.Add(current.ToString());
        return parts;
    }

    public override Vec ToKit(Vec native) => new(100.0 * native.Z, 100.0 * native.X, 100.0 * native.Y);

    public override Dictionary<string, object?> ToNative(Vec kit) => new()
    {
        ["x"] = kit.Y / 100.0, ["y"] = kit.Z / 100.0, ["z"] = kit.X / 100.0,
    };

    public override (double Yaw, double Pitch) RotationToKit(GameValue rotation)
    {
        var r = rotation["euler"].IsObject ? rotation["euler"] : rotation;
        return (Geom.Wrap(r["y"].Number ?? 0), Geom.Wrap(-(r["x"].Number ?? 0)));
    }

    public override Dictionary<string, object?> RotationToNative(double yaw, double pitch = 0) => new()
    {
        ["x"] = Modulo(-pitch, 360), ["y"] = Modulo(yaw, 360), ["z"] = 0.0,
    };

    static double Modulo(double value, double by) => value - by * Math.Floor(value / by);
}
