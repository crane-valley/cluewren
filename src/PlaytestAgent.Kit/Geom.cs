using System.Globalization;

namespace PlaytestAgent.Kit;

public readonly record struct Vec(double X = 0, double Y = 0, double Z = 0)
{
    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    public static Vec operator *(double k, Vec a) => a * k;

    public static Vec operator /(Vec a, double k) => new(a.X / k, a.Y / k, a.Z / k);

    public double Dot(Vec o) => X * o.X + Y * o.Y + Z * o.Z;

    public double Length => Math.Sqrt(Dot(this));

    public double Length2d => Math.Sqrt(X * X + Y * Y);

    public Vec Flat => new(X, Y, 0);

    public Vec Unit
    {
        get
        {
            var n = Length;
            return n > 1e-9 ? this / n : default;
        }
    }

    public double Dist(Vec o) => (this - o).Length;

    public double Dist2d(Vec o) => (this - o).Length2d;

    public Vec Rounded(int digits = 0) => new(
        Math.Round(X, digits, MidpointRounding.ToEven),
        Math.Round(Y, digits, MidpointRounding.ToEven),
        Math.Round(Z, digits, MidpointRounding.ToEven));

    public Dictionary<string, object?> Ue() => new() { ["X"] = X, ["Y"] = Y, ["Z"] = Z };

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Vec({X:0}, {Y:0}, {Z:0})");
}

public static class Geom
{
    const double Degrees = 180.0 / Math.PI;

    public static double Radians(double degrees) => degrees * (Math.PI / 180.0);

    public static double ToDegrees(double radians) => radians * Degrees;

    public static double Wrap(double deg)
    {
        deg %= 360.0;
        if (deg > 180)
            deg -= 360;
        else if (deg <= -180)
            deg += 360;
        return deg;
    }

    public static double YawOf(Vec v) => Math.Atan2(v.Y, v.X) * Degrees;

    public static double PitchOf(Vec v) => Math.Atan2(v.Z, v.Length2d) * Degrees;

    public static Vec Forward(double yaw, double pitch = 0)
    {
        var (sy, cy) = Math.SinCos(Radians(yaw));
        var (sp, cp) = Math.SinCos(Radians(pitch));
        return new Vec(cp * cy, cp * sy, sp);
    }

    public static Vec RightOf(double yaw) => Forward(yaw + 90.0);

    public static double Bearing(Vec from, double yaw, Vec to) => Wrap(YawOf(to - from) - yaw);

    public static (double Right, double Up) AimError(Vec eye, double yaw, double pitch, Vec target)
    {
        var d = target - eye;
        return (Wrap(YawOf(d) - yaw), PitchOf(d) - Wrap(pitch));
    }

    public static string Clock(double bearingDeg)
    {
        var b = Wrap(bearingDeg);
        var a = Math.Abs(b);
        var side = b > 0 ? "right" : "left";
        if (a <= 15)
            return "ahead";
        if (a <= 60)
            return $"ahead-{side}";
        if (a <= 120)
            return side;
        if (a <= 165)
            return $"behind-{side}";
        return "behind";
    }

    public static Dictionary<string, object?> Where(Pose pose, Vec point)
    {
        var b = Bearing(pose.At, pose.Yaw, point);
        return new Dictionary<string, object?>
        {
            ["distance"] = Fmt.Round(point.Dist2d(pose.At)),
            ["direction"] = Clock(b),
            ["bearing"] = Fmt.Round(b),
            ["height"] = Fmt.Round(point.Z - pose.At.Z),
        };
    }
}
