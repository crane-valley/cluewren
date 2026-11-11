// Modified by Crane Valley: the editor connects when asked (the window, the command line), never by itself; the agent
// service, sign-in, uninstall and preferences paths are removed, and a command line asks for a connection, never a run;
// rc is told which assemblies the editor alone compiles.
// The bridge in the editor: one client for the editor's life, in edit mode and play mode alike (play mode is the
// session: the brain's `session start` enters it, as Unreal's Play In Editor). Ticked by the editor's update while
// the game isn't playing (or is paused), by the player loop while it plays.
//
// Entering play mode reloads the scripts (unless the project turned that off): the connection closes with code 1012
// and the client connects again afterwards with hello {"resume": <session>}, so the brain keeps the same run; requests
// waiting on the change (session start) are answered on the new connection.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cluewren.Editor
{
    [InitializeOnLoad]
    public sealed class EditorHost : IPlaytestHost
    {
        const string StateKey = "Cluewren.Client";
        const string PendingKey = "Cluewren.PendingSessions";
        // Play mode started in a scene of the brain's (playModeStartScene): set back once play mode ends, so the
        // editor's own Play plays the open scene again. And the scene open in the editor while it plays
        const string StartSceneKey = "Cluewren.StartSceneSet";
        const string EditSceneKey = "Cluewren.EditScene";
        public const string HeadlessKey = "Cluewren.Headless";
        const string DisconnectedKey = "Cluewren.Disconnected";
        const double AutoConnectSeconds = 2;
        static double nextAutoConnect;
        static bool restored;

        public static readonly EditorHost Instance = new EditorHost();
        public static PlaytestClient Client { get; private set; }

        sealed class SessionWait
        {
            public long Id;
            public string Action;  // start | stop | fresh
            public int Stage;
            public double Since;
        }

        static readonly List<SessionWait> waits = new List<SessionWait>();
        static int playFrames;

        static EditorHost()
        {
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.quitting += () => Client?.Shutdown();
            Bootstrap.InstallTick();
            Ops.EditorLevels = Levels;
            Ops.EditorLoad = LoadInPlayMode;
            var editorOnly = new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Where(a => (a.flags & AssemblyFlags.EditorAssembly) != 0).Select(a => a.name));
            var player = new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name));
            Reflect.EditorOnly = a =>
            {
                var name = a.GetName().Name;
                // A Unity.* assembly that no player is built with is the editor's own tooling (Gradle, build service, compiler).
                return editorOnly.Contains(name) || name.StartsWith("UnityEditor", StringComparison.Ordinal)
                       || name.StartsWith("Unity.", StringComparison.Ordinal) && !player.Contains(name);
            };
        }

        // -- the client --------------------------------------------------------------------------------------

        public static PlaytestClient Ensure()
        {
            if (Client != null) return Client;
            Client = new PlaytestClient(Instance);
            Hands.EnsureDevices();
            return Client;
        }

        public static void Connect()
        {
            SessionState.EraseBool(DisconnectedKey);
            Ensure().Connect();
        }

        public static void Disconnect()
        {
            SessionState.SetBool(DisconnectedKey, true);
            Client?.Disconnect();
            Hands.RemoveDevices();
            SessionState.EraseString(StateKey);
        }

        static void AutoConnect()
        {
            if (Application.isBatchMode || EditorApplication.timeSinceStartup < nextAutoConnect) return;
            nextAutoConnect = EditorApplication.timeSinceStartup + AutoConnectSeconds;
            if (SessionState.GetBool(DisconnectedKey, false)) return;
            // A brain that failed its proof is refused until the user asks again.
            if (Client != null && (Client.WantConnected || !string.IsNullOrEmpty(Client.Problem))) return;
            if (Discovery.Read(Discovery.FilePath(), out _) == null) Connect();
        }

        static void Restore()
        {
            var saved = SessionState.GetString(StateKey, "");
            if (!string.IsNullOrEmpty(saved))
            {
                SessionState.EraseString(StateKey);
                Dictionary<string, object> state = null;
                try
                {
                    state = Json.ParseObject(saved);
                }
                catch
                {
                    // (unreadable: start afresh)
                }
                if (state != null && Json.Bool(state, "want", false))
                {
                    var client = Ensure();
                    client.ExitOnFailure = Json.Bool(state, "exit_on_failure", false);
                    var pendingText = SessionState.GetString(PendingKey, "");
                    SessionState.EraseString(PendingKey);
                    if (!string.IsNullOrEmpty(pendingText))
                    {
                        foreach (var o in Json.Parse(pendingText) as List<object> ?? new List<object>())
                        {
                            if (!(o is Dictionary<string, object> w)) continue;
                            waits.Add(new SessionWait
                            {
                                Id = (long)Json.Num(w, "id", 0),
                                Action = Json.Str(w, "action"),
                                Stage = (int)Json.Num(w, "stage", 0),
                                Since = Json.Num(w, "since", EditorApplication.timeSinceStartup),
                            });
                        }
                    }
                    var session = Json.Str(state, "session");
                    // (a recording the brain asked for goes on over the resumed connection, once the game runs)
                    if (!string.IsNullOrEmpty(session)) Media.RestoreState(Json.Obj(state, "record"));
                    client.Resume(Json.Str(state, "url"), string.IsNullOrEmpty(session) ? null : session,
                        Json.Bool(state, "run_active", false), Json.Num(state, "connect_timeout", 0), Json.Num(state, "connect_since", 0), Json.Bool(state, "welcomed", false), (long)Json.Num(state, "received", 0));
                    return;
                }
            }
            if (Application.isBatchMode && PlaytestClient.Flag("PlaytestConnect") && !SessionState.GetBool(HeadlessKey, false))
                Headless.Start();
        }

        static void BeforeReload()
        {
            if (Client == null) return;
            var state = Client.SaveState();
            if (Json.Bool(state, "want", false))
            {
                SessionState.SetString(StateKey, Json.Write(state));
                SessionState.SetString(PendingKey, Json.Write(waits.Select(w => (object)new Dictionary<string, object>
                {
                    ["id"] = w.Id, ["action"] = w.Action, ["stage"] = w.Stage, ["since"] = w.Since,
                }).ToList()));
            }
            Client.CloseForReload();
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.ExitingEditMode) playFrames = 0;
            if (change == PlayModeStateChange.ExitingEditMode) SessionState.SetString(EditSceneKey, OpenScene());
            if (change == PlayModeStateChange.EnteredEditMode)
            {
                Ops.ResetSession();
                if (SessionState.GetBool(StartSceneKey, false))
                {
                    EditorSceneManager.playModeStartScene = null;
                    SessionState.EraseBool(StartSceneKey);
                }
            }
            // (a project that doesn't reload scripts for play mode: no reload restores the waiting connection)
            if (change == PlayModeStateChange.EnteredPlayMode && Client == null && !string.IsNullOrEmpty(SessionState.GetString(StateKey, "")))
                Restore();
        }

        static void Update()
        {
            if (EditorApplication.isPlaying) ++playFrames;
            // Not by delayCall: an unfocused editor runs it long after Update, and the resume would tear down what AutoConnect opened.
            if (!restored)
            {
                restored = true;
                Restore();
            }
            AutoConnect();
            var client = Client;
            if (client == null) return;
            if (!EditorApplication.isPlaying || EditorApplication.isPaused)
            {
                try
                {
                    client.Tick();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Cluewren] tick failed: " + e);
                }
            }
            TickWaits(client);
        }

        static void TickWaits(PlaytestClient client)
        {
            if (waits.Count == 0) return;
            var now = EditorApplication.timeSinceStartup;
            foreach (var w in waits.ToList())
            {
                var done = false;
                var playing = EditorApplication.isPlaying && !EditorApplication.isCompiling;
                switch (w.Action)
                {
                    case "start":
                        if (playing && playFrames >= 3 && SceneManager.GetActiveScene().isLoaded)
                        {
                            client.Respond(w.Id, new Dictionary<string, object> { ["running"] = true, ["scene"] = SceneManager.GetActiveScene().path });
                            done = true;
                        }
                        break;
                    case "stop":
                        if (!EditorApplication.isPlayingOrWillChangePlaymode)
                        {
                            client.Respond(w.Id, new Dictionary<string, object> { ["running"] = false });
                            done = true;
                        }
                        break;
                    case "fresh":
                        if (w.Stage == 0 && !EditorApplication.isPlayingOrWillChangePlaymode)
                        {
                            w.Stage = 1;
                            EditorApplication.EnterPlaymode();
                        }
                        else if (w.Stage == 1 && playing && playFrames >= 3)
                        {
                            client.Respond(w.Id, new Dictionary<string, object> { ["running"] = true, ["scene"] = SceneManager.GetActiveScene().path });
                            done = true;
                        }
                        break;
                }
                if (!done && now - w.Since > 90)
                {
                    client.RespondError(w.Id, $"Timed out: session {w.Action}", 500);
                    done = true;
                }
                if (done) waits.Remove(w);
            }
        }

        // -- IPlaytestHost -------------------------------------------------------------------------------------

        public string HostName => "editor";

        public string ProjectDir => Directory.GetParent(Application.dataPath).FullName;

        public string ProjectId => PlayerSettings.productGUID.ToString();

        public bool SessionRunning => EditorApplication.isPlaying;

        public bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args)
        {
            var map = Json.Str(args, "map");
            if (!string.IsNullOrEmpty(map) && action != "stop" && ScenePath(map) == null)
            {
                client.RespondError(id, $"No scene {map} in the project", 404);  // (before anything changes)
                return true;
            }
            switch (action)
            {
                case "start":
                    if (EditorApplication.isPlaying)
                    {
                        client.Respond(id, new Dictionary<string, object> { ["running"] = true });
                        return true;
                    }
                    EnterPlayMode(client, map);
                    waits.Add(new SessionWait { Id = id, Action = "start", Since = EditorApplication.timeSinceStartup });
                    return true;
                case "stop":
                    if (!EditorApplication.isPlaying)
                    {
                        client.Respond(id, new Dictionary<string, object> { ["running"] = false });
                        return true;
                    }
                    Hands.ReleaseAll();
                    EditorApplication.ExitPlaymode();
                    waits.Add(new SessionWait { Id = id, Action = "stop", Since = EditorApplication.timeSinceStartup });
                    return true;
                case "restart":
                    if (!EditorApplication.isPlaying)
                    {
                        EnterPlayMode(client, map);
                        waits.Add(new SessionWait { Id = id, Action = "start", Since = EditorApplication.timeSinceStartup });
                        return true;
                    }
                    if (Json.Bool(args, "fresh", false))
                    {
                        // Out of play mode and in again: everything starts over (static state too, when scripts reload)
                        Hands.ReleaseAll();
                        if (!string.IsNullOrEmpty(map)) SetStartScene(map);
                        EditorApplication.ExitPlaymode();
                        waits.Add(new SessionWait { Id = id, Action = "fresh", Since = EditorApplication.timeSinceStartup });
                        return true;
                    }
                    Ops.Restart(client, id, map);
                    return true;
            }
            return false;
        }

        static void EnterPlayMode(PlaytestClient client, string map)
        {
            // The project's export for the codex, while scenes can still be opened (edit mode)
            try
            {
                var dir = Path.Combine(Instance.ProjectDir, "Library", "CluewrenCodex");
                if (CodexExport.Needed(Instance.ProjectDir) != null)
                {
                    client.Status("Indexing the project (scenes, prefabs, input)");
                    CodexExport.Export(dir);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Cluewren] The project index export failed: " + e.Message);
            }
            if (!string.IsNullOrEmpty(map)) SetStartScene(map);
            else
            {
                EditorSceneManager.playModeStartScene = null;
                var active = SceneManager.GetActiveScene();
                if (string.IsNullOrEmpty(active.path))
                {
                    // An untitled scene (the editor in batch mode): the build's first scene
                    var first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
                    if (first != null) EditorSceneManager.OpenScene(first.path, OpenSceneMode.Single);
                }
            }
            client.Status("Entering play mode");
            EditorApplication.EnterPlaymode();
        }

        /// <summary>Play mode starts in this scene (a path, or a scene's name in the build list or the project); the
        /// scenes open in the editor stay as they are, and the editor's own Play plays them again once play mode ends.</summary>
        public static bool SetStartScene(string map)
        {
            var path = ScenePath(map);
            var asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            EditorSceneManager.playModeStartScene = asset;
            if (asset == null) Debug.LogWarning($"[Cluewren] No scene {map}: play mode starts with the open scene");
            else SessionState.SetBool(StartSceneKey, true);
            return asset != null;
        }

        /// <summary>What a session start without a map plays: the active scene, or the build's first when it is untitled.</summary>
        static string OpenScene()
        {
            var active = SceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path)) return active.path;
            var first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
            return first != null ? first.path : "";
        }

        /// <summary>`levels` (docs/protocol.md): the project's scenes (under Assets), the one open in the editor and the
        /// one playing.</summary>
        static Dictionary<string, object> Levels()
        {
            var levels = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(p => p, StringComparer.Ordinal)
                .Cast<object>().ToList();
            var playing = EditorApplication.isPlaying ? SceneManager.GetActiveScene().path : "";
            var open = EditorApplication.isPlaying ? SessionState.GetString(EditSceneKey, "") : OpenScene();
            return new Dictionary<string, object> { ["levels"] = levels, ["open"] = open, ["playing"] = playing ?? "" };
        }

        /// <summary>A scene of the project loaded in play mode, in the build list or not (Ops.Restart's `map`).</summary>
        static bool LoadInPlayMode(string map)
        {
            var path = ScenePath(map);
            if (string.IsNullOrEmpty(path) || !EditorApplication.isPlaying) return false;
            EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
            return true;
        }

        public static string ScenePath(string map)
        {
            if (string.IsNullOrEmpty(map)) return null;
            if (map.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && File.Exists(map)) return map.Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(map);
            var inBuild = EditorBuildSettings.scenes.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s.path) == name);
            if (inBuild != null) return inBuild.path;
            var guid = AssetDatabase.FindAssets("t:Scene " + name).FirstOrDefault(g => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == name);
            return guid != null ? AssetDatabase.GUIDToAssetPath(guid) : null;
        }

        public void Exit(int code)
        {
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
