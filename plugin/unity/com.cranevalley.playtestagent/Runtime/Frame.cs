// The kit frame (every op but rc speaks it): Unreal's axes and units. Z up, left-handed, centimetres; yaw in degrees
// about Z from +X toward +Y; pitch positive up. Unity is y up, z forward, x right, metres, left-handed:
//     kit (X, Y, Z) = 100 * (z, x, y);  kit yaw = euler y;  kit pitch = -euler x;  kit roll = -euler z
// (a cyclic permutation of the axes, so handedness is kept and so are the turning directions).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Autopilot.Playtest
{
    public static class Frame
    {
        public const double Scale = 100.0;

        public static Dictionary<string, object> Point(Vector3 v, int digits = 1)
        {
            return new Dictionary<string, object>
            {
                ["X"] = Json.Round(v.z * Scale, digits),
                ["Y"] = Json.Round(v.x * Scale, digits),
                ["Z"] = Json.Round(v.y * Scale, digits),
            };
        }

        /// <summary>A half size or full size (no sign change: the same permutation).</summary>
        public static Dictionary<string, object> Size(Vector3 v, int digits = 1)
        {
            return Point(v, digits);
        }

        public static Vector3 ToUnity(double x, double y, double z)
        {
            return new Vector3((float)(y / Scale), (float)(z / Scale), (float)(x / Scale));
        }

        /// <summary>A kit vector {"X","Y","Z"} as a Unity vector (metres).</summary>
        public static Vector3 ToUnity(Dictionary<string, object> v)
        {
            if (v == null) return Vector3.zero;
            return ToUnity(Json.Num(v, "X", 0), Json.Num(v, "Y", 0), Json.Num(v, "Z", 0));
        }

        /// <summary>A kit direction (no scaling) as a Unity direction.</summary>
        public static Vector3 DirectionToUnity(Dictionary<string, object> v)
        {
            if (v == null) return Vector3.zero;
            return new Vector3((float)Json.Num(v, "Y", 0), (float)Json.Num(v, "Z", 0), (float)Json.Num(v, "X", 0));
        }

        public static double Wrap(double degrees)
        {
            var d = degrees % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d <= -180.0) d += 360.0;
            return d;
        }

        public static double Yaw(Quaternion q)
        {
            return Wrap(q.eulerAngles.y);
        }

        public static double Pitch(Quaternion q)
        {
            return Wrap(-q.eulerAngles.x);
        }

        public static double Roll(Quaternion q)
        {
            return Wrap(-q.eulerAngles.z);
        }

        public static Dictionary<string, object> Rotation(Quaternion q, int digits = 2)
        {
            return new Dictionary<string, object>
            {
                ["Pitch"] = Json.Round(Pitch(q), digits),
                ["Yaw"] = Json.Round(Yaw(q), digits),
                ["Roll"] = Json.Round(Roll(q), digits),
            };
        }

        /// <summary>The kit yaw of a direction across the floor.</summary>
        public static double YawOf(Vector3 direction)
        {
            return Math.Atan2(direction.x, direction.z) * 180.0 / Math.PI;
        }

        /// <summary>A native (rc) vector: lowercase, metres, Unity's axes.</summary>
        public static Dictionary<string, object> Native(Vector3 v, int digits = 4)
        {
            return new Dictionary<string, object>
            {
                ["x"] = Json.Round(v.x, digits),
                ["y"] = Json.Round(v.y, digits),
                ["z"] = Json.Round(v.z, digits),
            };
        }

        public static bool Finite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                     || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }
    }
}
