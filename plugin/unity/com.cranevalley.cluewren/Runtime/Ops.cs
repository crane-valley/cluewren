// Modified by Crane Valley: names renamed from Autopilot to Cluewren.
// The brain's requests (docs/protocol.md), handled on the main thread.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cluewren
{
    public static class Ops
    {
        static bool paused;
        static float timeScaleBefore = 1f;

        /// <summary>The scene the game was launched with (play mode entered with): what restart reopens.</summary>
        public static string LaunchScene = "";

        /// <summary>The editor's answer to `levels` (EditorHost): the project's scenes, not only the build's.</summary>
        public static Func<Dictionary<string, object>> EditorLevels;

        /// <summary>The editor's scene loading in play mode (EditorHost): any scene of the project, not only the
        /// build's. False when it can't load `map` (SceneManager then tries).</summary>
        public static Func<string, bool> EditorLoad;

        public static bool IsPaused => Application.isPlaying && (paused || Time.timeScale == 0f);

        public static void Handle(PlaytestClient client, long id, string op, Dictionary<string, object> args)
        {
            switch (op)
            {
                case "key":
                    Hands.KeyOp(client, id, args);
                    break;
                case "input":
                    Hands.InputOp(client, id, args);
                    break;
                case "pointer":
                    Hands.PointerOp(client, id, args);
                    break;
                case "view":
                    client.Respond(id, View.Look(args));
                    break;
                case "rc":
                    Rc(client, id, args);
                    break;
                case "context":
                    client.Respond(id, Context(client, args));
                    break;
                case "session":
                    Session(client, id, args);
                    break;
                case "levels":
                    client.Respond(id, EditorLevels != null ? EditorLevels() : PlayerLevels());
                    break;
                case "pause":
                    Pause(client, id, args);
                    break;
                case "move":
                    Move(client, id, args);
                    break;
                case "watch":
                    Watch.Handle(client, id, args);
                    break;
                case "screen":
                    client.Respond(id, new Dictionary<string, object> { ["texts"] = ScreenText.Texts().ToDictionary(p => p.Key, p => (object)p.Value) });
                    break;
                case "ui":
                    client.Respond(id, new Dictionary<string, object> { ["elements"] = ScreenUI.List() });
                    break;
                case "ui_act":
                {
                    var error = ScreenUI.Act(args);
                    if (string.IsNullOrEmpty(error)) client.Respond(id, new Dictionary<string, object>());
                    else client.RespondError(id, error);
                    break;
                }
                case "timestep":
                {
                    var fixedStep = Json.Bool(args, "fixed", false);
                    Time.captureFramerate = fixedStep ? Math.Max(1, (int)Json.Num(args, "fps", 30)) : 0;
                    client.Respond(id, new Dictionary<string, object>());
                    break;
                }
                case "detectors":
                    client.Detectors.Configure(args);
                    client.Respond(id, new Dictionary<string, object>());
                    break;
                case "ping":
                    client.Respond(id, new Dictionary<string, object>
                    {
                        ["game_time"] = Application.isPlaying ? Json.Round(Time.timeAsDouble, 3) : 0.0,
                        ["wall_time"] = Json.Round((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds, 3),
                    });
                    break;
                case "find":
                    client.Respond(id, Find(args));
                    break;
                case "transforms":
                    client.Respond(id, Transforms(args));
                    break;
                case "pose":
                    client.Respond(id, Player.Pose());
                    break;
                case "movement":
                    client.Respond(id, Player.Movement());
                    break;
                case "record":
                    Media.Record(client, id, args);
                    break;
                case "capture":
                    Media.Capture(client, id, args);
                    break;
                default:
                    client.RespondError(id, $"unknown op {op}");
                    break;
            }
        }

        static void Rc(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var verb = Json.Str(args, "verb");
            var body = Json.Obj(args, "body");
            if (body == null)
            {
                client.RespondError(id, "rc needs a body");
                return;
            }
            switch (verb)
            {
                case "call":
                    client.Respond(id, Reflect.Call(body, Bootstrap.CoroutineHost));
                    break;
                case "property":
                    client.Respond(id, Reflect.Property(body));
                    break;
                case "describe":
                    client.Respond(id, Reflect.Describe(body));
                    break;
                default:
                    client.RespondError(id, $"unknown rc verb {verb}");
                    break;
            }
        }

        // -- context ---------------------------------------------------------------------------------------

        static Dictionary<string, object> Context(PlaytestClient client, Dictionary<string, object> args)
        {
            if (args.ContainsKey("player")) Player.Choose(Json.Str(args, "player"));
            var running = client.Host.SessionRunning;
            var player = running ? Player.Find() : null;
            var cam = running ? Player.Eyes(player) : null;
            var scene = SceneManager.GetActiveScene();
            var controller = "";
#if CLUEWREN_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            if (player != null)
            {
                var pi = player.GetComponentInParent<UnityEngine.InputSystem.PlayerInput>() ?? player.GetComponentInChildren<UnityEngine.InputSystem.PlayerInput>();
                if (pi != null) controller = Paths.Of(pi);
            }
#endif
            var scenes = new List<object>();
            for (var i = 0; i < SceneManager.sceneCount; ++i)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isLoaded) scenes.Add(string.IsNullOrEmpty(s.path) ? s.name : s.path);
            }
            var build = new List<object>();
            for (var i = 0; i < SceneManager.sceneCountInBuildSettings; ++i) build.Add(SceneUtility.GetScenePathByBuildIndex(i));
            return new Dictionary<string, object>
            {
                ["world"] = scene.IsValid() ? (string.IsNullOrEmpty(scene.path) ? scene.name : scene.path) : "",
                ["character"] = player != null ? Paths.Of(player) : "",
                ["player_found_by"] = player != null ? Player.FoundBy : "",
                ["controller"] = controller,
                ["input_subsystem"] = "",
                ["camera_manager"] = cam != null ? Paths.Of(cam.gameObject) : "",
                ["game_name"] = Application.productName,
                ["host"] = client.Host.HostName,
                ["engine"] = "unity",
                ["engine_version"] = Application.unityVersion,
                ["project_dir"] = client.Host.ProjectDir ?? "",
                ["session_running"] = running,
                ["paused"] = IsPaused,
                ["game_time"] = Application.isPlaying ? Json.Round(Time.timeAsDouble, 3) : 0.0,
                ["controls"] = running ? Hands.Controls() : new List<object>(),
                ["input"] = Hands.Capabilities(),
                ["scenes"] = scenes,
                ["build_scenes"] = build,
                ["launch_scene"] = LaunchScene,
                ["rendering"] = View.Rendering,
            };
        }

        // -- session (a player build; the editor's host handles its own) -------------------------------------

        static void Session(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var action = Json.Str(args, "action", "status");
            if (action == "status" || action == "")
            {
                client.Respond(id, new Dictionary<string, object> { ["running"] = client.Host.SessionRunning });
                return;
            }
            if (client.Host.Session(client, id, action, args)) return;
            if (action != "restart")
            {
                client.Respond(id, new Dictionary<string, object> { ["running"] = Application.isPlaying });
                return;
            }
            Restart(client, id, Json.Str(args, "map"));
        }

        /// <summary>`levels` in a player: the build's scenes, the one playing open (docs/protocol.md).</summary>
        static Dictionary<string, object> PlayerLevels()
        {
            var levels = new List<object>();
            for (var i = 0; i < SceneManager.sceneCountInBuildSettings; ++i) levels.Add(SceneUtility.GetScenePathByBuildIndex(i));
            var active = SceneManager.GetActiveScene();
            var playing = active.IsValid() ? active.path : "";
            return new Dictionary<string, object> { ["levels"] = levels, ["open"] = playing, ["playing"] = playing };
        }

        /// <summary>Reopen the launch scene (or `map`: a scene name, path or build index) and answer once it is loaded.</summary>
        public static void Restart(PlaytestClient client, long id, string map)
        {
            var target = string.IsNullOrEmpty(map) ? LaunchScene : map;
            if (string.IsNullOrEmpty(target)) target = SceneManager.GetActiveScene().path;
            Hands.ReleaseAll();
            if (paused) SetPaused(false);
            var old = SceneManager.GetActiveScene().handle;
            try
            {
                if (int.TryParse(target, out var index)) SceneManager.LoadScene(index, LoadSceneMode.Single);
                else if (EditorLoad == null || !EditorLoad(target)) SceneManager.LoadScene(target, LoadSceneMode.Single);
            }
            catch (Exception e)
            {
                client.RespondError(id, $"Couldn't open {target}: {e.Message}", 500);
                return;
            }
            var frames = 0;
            client.AddPending(new Pending
            {
                Id = id,
                Op = "session",
                Seconds = 30,
                Tick = (p, now) =>
                {
                    var active = SceneManager.GetActiveScene();
                    if (active.handle != old && active.isLoaded && ++frames >= 2)
                    {
                        client.Respond(p.Id, new Dictionary<string, object> { ["running"] = true, ["scene"] = active.path });
                        return true;
                    }
                    if (now - p.Start > p.Seconds)
                    {
                        client.RespondError(p.Id, $"Timed out opening {target} (is it in the build's scene list?)", 500);
                        return true;
                    }
                    return false;
                },
            });
        }

        // -- pause -----------------------------------------------------------------------------------------

        static void SetPaused(bool value)
        {
            if (value && !paused)
            {
                timeScaleBefore = Time.timeScale > 0 ? Time.timeScale : 1f;
                Time.timeScale = 0f;
                AudioListener.pause = true;
            }
            else if (!value && paused)
            {
                if (Time.timeScale == 0f) Time.timeScale = timeScaleBefore;
                AudioListener.pause = false;
            }
            paused = value;
        }

        static void Pause(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            if (!Application.isPlaying)
            {
                client.RespondError(id, "No game is running");
                return;
            }
            SetPaused(Json.Bool(args, "paused", false));
            client.Respond(id, new Dictionary<string, object> { ["paused"] = IsPaused });
        }

        /// <summary>A new session starts unpaused (the bridge's own pause doesn't outlive it).</summary>
        public static void ResetSession()
        {
            paused = false;
            Time.captureFramerate = 0;
        }

        // -- find, transforms --------------------------------------------------------------------------------

        static Dictionary<string, object> Find(Dictionary<string, object> args)
        {
            var className = Json.Str(args, "class");
            var name = Json.Str(args, "name");
            var limit = (int)Json.Num(args, "limit", 10000);
            var inactive = Json.Bool(args, "inactive", false);
            // things: the scene's things as a player tells them apart (Unreal's actors): objects that aren't plain
            // groups (nothing but a Transform), at the top or under a plain group; not the parts under them
            var things = Json.Bool(args, "things", false);
            Type type = null;
            if (!string.IsNullOrEmpty(className) && className != "GameObject" && className != "Actor" && className != "Object")
            {
                type = Reflect.FindType(className);
                if (type == null || !(typeof(Component).IsAssignableFrom(type) || type.IsInterface))
                    throw new RcError($"no component type named {className}");
            }
            var objects = new List<object>();
            foreach (var go in Paths.All(inactive))
            {
                if (objects.Count >= limit) break;
                if (!string.IsNullOrEmpty(name) && go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (type != null && go.GetComponent(type) == null) continue;
                if (things && (View.IsGroup(go.transform) || go.transform.parent != null && !View.IsGroup(go.transform.parent))) continue;
                objects.Add(Paths.Of(go));
            }
            return new Dictionary<string, object> { ["objects"] = objects };
        }

        /// <summary>A particle system's renderer (only when the project has the Particle System module).</summary>
        public static bool IsParticles(Renderer r)
        {
#if CLUEWREN_PARTICLES
            return r is ParticleSystemRenderer;
#else
            return false;
#endif
        }

        public static Bounds? BoundsOf(GameObject go)
        {
            Bounds? box = null;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || IsParticles(r)) continue;
                if (box == null) box = r.bounds;
                else
                {
                    var b = box.Value;
                    b.Encapsulate(r.bounds);
                    box = b;
                }
            }
#if CLUEWREN_PHYSICS
            foreach (var c in go.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled) continue;
                if (box == null) box = c.bounds;
                else
                {
                    var b = box.Value;
                    b.Encapsulate(c.bounds);
                    box = b;
                }
            }
#endif
            return box;
        }

        static Dictionary<string, object> Transforms(Dictionary<string, object> args)
        {
            var result = new Dictionary<string, object>();
            foreach (var o in Json.List(args, "objects") ?? new List<object>())
            {
                var path = Convert.ToString(o);
                var r = Paths.Resolve(path);
                var go = r as GameObject ?? (r as Component)?.gameObject;
                if (go == null)
                {
                    result[path] = null;
                    continue;
                }
                var t = go.transform;
                var velocity = Vector3.zero;
#if CLUEWREN_PHYSICS
                var cc = go.GetComponent<CharacterController>();
                var rb = go.GetComponent<Rigidbody>();
                if (cc != null && cc.enabled) velocity = cc.velocity;
                else if (rb != null) velocity = rb.linearVelocity;
#endif
                var box = BoundsOf(go);
                result[path] = new Dictionary<string, object>
                {
                    ["location"] = Frame.Point(t.position),
                    ["rotation"] = Frame.Rotation(t.rotation),
                    ["velocity"] = Frame.Point(velocity),
                    ["center"] = Frame.Point(box?.center ?? t.position),
                    ["extent"] = Frame.Size(box?.extents ?? Vector3.zero),
                    ["active"] = go.activeInHierarchy,
                };
            }
            return new Dictionary<string, object> { ["objects"] = result };
        }

        // -- move ------------------------------------------------------------------------------------------

        /// <summary>move: {"direction": kit {"X","Y","Z"}, "scale", "seconds", "via"?: "auto"|"stick"|"keys"|"controller"}.
        /// The player walks that way for `seconds`: through the game's own move action when the bridge can drive it (a
        /// gamepad stick bound to it, else its keys, 8 ways), else by moving its CharacterController (or Rigidbody)
        /// at its walking speed. Answers {"moved": cm across the floor, "via"}.</summary>
        static void Move(PlaytestClient client, long id, Dictionary<string, object> args)
        {
            var player = Player.Find();
            if (player == null)
            {
                client.RespondError(id, "No player pawn");
                return;
            }
            var dir = Frame.DirectionToUnity(Json.Obj(args, "direction"));
            dir.y = 0;
            var scale = (float)Json.Num(args, "scale", 1.0);
            var asked = Json.Str(args, "via", "auto");
            string via = null;
#if CLUEWREN_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.InputAction action = null;
            if (asked != "controller") via = Hands.MoveVia(asked, out action);
#endif
            if (via == null && asked != "auto" && asked != "controller")
            {
                client.RespondError(id, $"move can't go via {asked} here (no move action bound to it)");
                return;
            }
            via = via ?? "controller";
            var start = player.transform.position;
            var speed = Player.WalkSpeed(player);
            client.AddPending(new Pending
            {
                Id = id,
                Op = "move",
                Seconds = Json.Num(args, "seconds", 0.35),
                Tick = (p, now) =>
                {
                    var alive = player != null;
                    if (alive && now - p.Start < p.Seconds)
                    {
                        var want = dir.sqrMagnitude > 1e-6f ? dir.normalized * Mathf.Clamp(scale, -1f, 1f) : Vector3.zero;
                        if (via == "controller")
                        {
                            Shove(player, want * speed * Time.deltaTime);
                        }
#if CLUEWREN_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                        else
                        {
                            var cam = Player.Eyes(player);
                            var view = cam != null ? cam.transform : player.transform;
                            var forward = view.forward;
                            forward.y = 0;
                            if (forward.sqrMagnitude < 1e-6f) forward = player.transform.forward;
                            forward.Normalize();
                            var right = new Vector3(forward.z, 0, -forward.x);
                            Hands.MoveInput(action, via, new Vector2(Vector3.Dot(want, right), Vector3.Dot(want, forward)));
                        }
#endif
                        return false;
                    }
#if CLUEWREN_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                    Hands.MoveStop();
#endif
                    var moved = 0.0;
                    if (alive)
                    {
                        var d = player.transform.position - start;
                        d.y = 0;
                        moved = Math.Round(d.magnitude * Frame.Scale * 10) / 10;
                    }
                    client.Respond(p.Id, new Dictionary<string, object> { ["moved"] = moved, ["via"] = via });
                    return true;
                },
            });
        }

        static void Shove(GameObject player, Vector3 step)
        {
#if CLUEWREN_PHYSICS
            var cc = player.GetComponent<CharacterController>();
            if (cc != null && cc.enabled)
            {
                cc.Move(step);
                return;
            }
            var rb = player.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.MovePosition(rb.position + step);
                return;
            }
#endif
            player.transform.position += step;
        }
    }
}
