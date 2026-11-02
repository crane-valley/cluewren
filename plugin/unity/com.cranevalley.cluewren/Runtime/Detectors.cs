// Modified by Crane Valley: names renamed from Autopilot to Cluewren.
// In-engine detectors (docs/protocol.md, Findings): cheap checks every frame or so, so glitches between the brain's
// steps are caught too. log_error (errors, exceptions and failed asserts in the log), nan (a body's transform not
// finite), out_of_world (a body far below the level or beyond any sane distance), long_fall (the player falling for
// 8 s), hitch (a frame longer than 0.75 s), hud_fraction (on-screen text a/b with a > b > 0).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Cluewren
{
    public static class LogCapture
    {
        public struct Line
        {
            public string Verbosity;
            public string Category;
            public string Message;
        }

        static readonly ConcurrentQueue<Line> lines = new ConcurrentQueue<Line>();
        static bool installed;
        [ThreadStatic] static int quiet;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static IDisposable Quiet()
        {
            return new QuietScope();
        }

        sealed class QuietScope : IDisposable
        {
            public QuietScope()
            {
                ++quiet;
            }

            public void Dispose()
            {
                --quiet;
            }
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (quiet > 0) return;
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            if (condition != null && condition.StartsWith("[Cluewren]", StringComparison.Ordinal)) return;
            if (lines.Count >= 500) return;
            var message = condition ?? "";
            if (type == LogType.Exception && !string.IsNullOrEmpty(stack))
            {
                // The first frames say where it was thrown
                var frames = stack.Split('\n');
                message += "\n" + string.Join("\n", frames, 0, Math.Min(4, frames.Length)).TrimEnd();
            }
            lines.Enqueue(new Line { Verbosity = "Error", Category = type.ToString(), Message = message });
        }

        static readonly List<Line> none = new List<Line>();

        public static List<Line> Drain()
        {
            if (lines.IsEmpty) return none;
            var list = new List<Line>();
            while (lines.TryDequeue(out var l)) list.Add(l);
            return list;
        }
    }

    public sealed class Detectors
    {
        readonly PlaytestClient client;
        bool enabled = true;
        double longFallSeconds = 8.0;
        double hitchSeconds = 0.75;
        double hudEvery = 1.0;
        double bodyEvery = 0.5;
        double? killY;  // native metres; else worked out from the level
        double farAway = 10000.0;

        readonly Dictionary<string, int> counts = new Dictionary<string, int>();
        double sessionStart;
        double nextHud, nextBodies;
        double fallingSince = -1;
        double levelBottom = double.NaN;

        static readonly Regex Fraction = new Regex(@"(?<![\d.])(\d{1,9})\s*/\s*(\d{1,9})(?![\d.])", RegexOptions.Compiled);

        public Detectors(PlaytestClient client)
        {
            this.client = client;
        }

        public static IDisposable Quiet()
        {
            return LogCapture.Quiet();
        }

        public void Configure(Dictionary<string, object> args)
        {
            enabled = Json.Bool(args, "enabled", enabled);
            longFallSeconds = Json.Num(args, "long_fall_seconds", longFallSeconds);
            hitchSeconds = Json.Num(args, "hitch_seconds", hitchSeconds);
            hudEvery = Json.Num(args, "hud_every", hudEvery);
            if (Json.Has(args, "kill_y")) killY = Json.Num(args, "kill_y", 0);
        }

        public void Reset()
        {
            counts.Clear();
            sessionStart = PlaytestClient.Now;
            nextHud = nextBodies = 0;
            fallingSince = -1;
            levelBottom = double.NaN;
        }

        void Report(string detector, string severity, string summary, string signature, GameObject actor = null,
            Dictionary<string, object> details = null)
        {
            counts.TryGetValue(signature, out var count);
            counts[signature] = ++count;
            if (count != 1 && count != 10 && count != 100 && count != 1000) return;
            var finding = new Dictionary<string, object>
            {
                ["detector"] = detector,
                ["severity"] = severity,
                ["summary"] = summary,
                ["signature"] = signature,
                ["t"] = client.GameTime,
                ["details"] = details ?? new Dictionary<string, object>(),
                ["count"] = count,
                ["source"] = "engine",
            };
            if (actor != null)
            {
                finding["actor"] = Paths.Of(actor);
                finding["where"] = Frame.Point(actor.transform.position);
            }
            client.QueueEvent(new Dictionary<string, object> { ["kind"] = "finding", ["t"] = client.GameTime, ["finding"] = finding });
        }

        static string Normalize(string message)
        {
            var first = message.Split('\n')[0];
            var sb = new StringBuilder(first.Length);
            var inNumber = false;
            foreach (var c in first)
            {
                if (char.IsDigit(c))
                {
                    if (!inNumber) sb.Append('#');
                    inNumber = true;
                    continue;
                }
                inNumber = false;
                sb.Append(c);
            }
            var s = sb.ToString();
            return s.Length > 160 ? s.Substring(0, 160) : s;
        }

        public void OnLogLines(List<LogCapture.Line> lines)
        {
            if (!enabled) return;
            foreach (var line in lines)
            {
                var first = line.Message.Split('\n')[0];
                Report("log_error", "error", (line.Category == "Exception" ? "Exception: " : line.Category == "Assert" ? "Assert failed: " : "Error logged: ") + Trim(first, 300),
                    "log_error:" + Normalize(line.Message), null,
                    new Dictionary<string, object> { ["type"] = line.Category, ["message"] = Trim(line.Message, 2000) });
            }
        }

        static string Trim(string s, int n)
        {
            return s.Length > n ? s.Substring(0, n) : s;
        }

        public void Tick()
        {
            if (!enabled || !Application.isPlaying) return;
            var now = PlaytestClient.Now;
            var settled = now - sessionStart > 2.0;
            // hitch: a frame that took too long while the game plays
            if (settled && !Ops.IsPaused && Time.unscaledDeltaTime > hitchSeconds)
            {
                Report("hitch", "warning", $"A frame took {Time.unscaledDeltaTime:0.00} s (hitch)", "hitch", null,
                    new Dictionary<string, object> { ["seconds"] = Json.Round(Time.unscaledDeltaTime, 3) });
            }
            if (now >= nextBodies)
            {
                nextBodies = now + bodyEvery;
                CheckBodies(now);
            }
            if (now >= nextHud && hudEvery > 0)
            {
                nextHud = now + hudEvery;
                CheckHud();
            }
        }

        double Bottom()
        {
            if (killY.HasValue) return killY.Value;
            if (double.IsNaN(levelBottom))
            {
                // The lowest solid thing of the level, and 100 m below it
                var lowest = double.PositiveInfinity;
#if CLUEWREN_PHYSICS
                foreach (var c in Loaded.All<Collider>(FindObjectsInactive.Exclude))
                {
                    if (c == null || c.isTrigger || c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                    lowest = Math.Min(lowest, c.bounds.min.y);
                }
#endif
                levelBottom = double.IsInfinity(lowest) ? -1000.0 : lowest - 100.0;
            }
            return levelBottom;
        }

        readonly List<GameObject> bodies = new List<GameObject>();

        void CheckBodies(double now)
        {
            bodies.Clear();
            var player = Player.Find();
            if (player != null) bodies.Add(player);
#if CLUEWREN_PHYSICS
            foreach (var rb in Loaded.All<Rigidbody>(FindObjectsInactive.Exclude))
                if (rb != null && !rb.isKinematic && bodies.Count < 2000) bodies.Add(rb.gameObject);
#endif
            var bottom = Bottom();
            foreach (var go in bodies)
            {
                if (go == null) continue;
                var t = go.transform;
                if (!Frame.Finite(t.position) || !Frame.Finite(t.localScale) || float.IsNaN(t.rotation.x))
                {
                    var label = Label(go);
                    Report("nan", "error", $"{label} has an invalid (NaN or infinite) transform", "nan:" + label, null);
                    continue;
                }
                var p = t.position;
                if (p.y < bottom)
                {
                    var label = Label(go);
                    Report("out_of_world", "warning", $"{label} is {bottom - p.y:0} m below the level (y {p.y:0.0})", "out_of_world:" + label, go,
                        new Dictionary<string, object> { ["y"] = Json.Round(p.y, 2), ["bottom"] = Json.Round(bottom, 2) });
                }
                else if (Math.Abs(p.x) > farAway || Math.Abs(p.y) > farAway || Math.Abs(p.z) > farAway)
                {
                    var label = Label(go);
                    Report("out_of_world", "warning", $"{label} is beyond the world bounds ({p.magnitude:0} m)", "out_of_world:" + label, go);
                }
            }
            // long_fall: the player falling for a long time (fell through the floor or off the level)
            if (player != null)
            {
                var pose = Player.Grounded(player, out var velocity);
                if (pose == false && velocity.y < -0.5f)
                {
                    if (fallingSince < 0) fallingSince = now;
                    else if (now - fallingSince > longFallSeconds)
                        Report("long_fall", "warning", $"{Label(player)} has been falling for {now - fallingSince:0} s (fell through the floor or off the level?)",
                            "long_fall:" + Label(player), player);
                }
                else fallingSince = -1;
            }
        }

        static string Label(GameObject go)
        {
            // A class-like label: the game's own script on it, else its name without a number
            foreach (var c in go.GetComponents<MonoBehaviour>())
                if (c != null && Reflect.IsGameType(c.GetType()))
                    return c.GetType().Name;
            return Regex.Replace(go.name, @"\s*\(\d+\)$", "");
        }

        void CheckHud()
        {
            Dictionary<string, string> texts;
            try
            {
                texts = ScreenText.Texts();
            }
            catch
            {
                return;
            }
            foreach (var pair in texts)
            {
                if (pair.Value.IndexOf('/') < 0) continue;
                foreach (Match m in Fraction.Matches(pair.Value))
                {
                    if (!long.TryParse(m.Groups[1].Value, out var a) || !long.TryParse(m.Groups[2].Value, out var b)) continue;
                    if (a > b && b > 0)
                    {
                        Report("hud_fraction", "warning",
                            $"On-screen text {pair.Key} shows \"{Trim(pair.Value, 80)}\": {a} is more than its stated maximum {b}",
                            "hud_fraction:" + pair.Key, null,
                            new Dictionary<string, object> { ["text"] = pair.Value, ["value"] = a, ["max"] = b, ["key"] = pair.Key });
                    }
                }
            }
        }
    }
}
