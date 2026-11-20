// Modified by Crane Valley: the editor advertises itself to the brain and connects by itself once a brain invites it
// (unless it was disconnected in the window; in batch mode only when the command line asks); the agent service,
// sign-in, uninstall and preferences paths are removed, and a command line asks for a connection, never a run; rc is
// told which assemblies the editor alone compiles.
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
        const string StartSceneKey = "Cluewren.PreviousStartScene";
        const string EditSceneKey = "Cluewren.EditScene";
        public const string HeadlessKey = "Cluewren.Headless";
        const string DisconnectedKey = "Cluewren.Disconnected";
        const string IdKey = "Cluewren.EditorId";
        const string RefusedKey = "Cluewren.RefusedInvite";
        const double AutoConnectSeconds = 1;
        static double nextAutoConnect;
        static bool restored;

        public static readonly EditorHost Instance = new EditorHost();
        public static PlaytestClient Client { get; private set; }

        sealed class SessionWait
        {
            public long Id;
            public string Action;  // start | stop | fresh
            public string Map;
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
            EditorApplication.quitting += () =>
            {
                RestoreStartScene();
                Discovery.Withdraw();
                Client?.Shutdown();
            };
            // A batch-mode editor is a build, an import or a test run unless its command line asks for a connection.
            if (!Application.isBatchMode || PlaytestClient.Flag("CluewrenConnect") || SessionState.GetBool(HeadlessKey, false))
                Advertise();
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
            Client.SessionEnded += waits.Clear;
            return Client;
        }

        // Kept across script reloads: with a new id the editor would pass over the invite of the run it is in.
        public static string EditorId()
        {
            var id = SessionState.GetString(IdKey, "");
            if (id.Length == 0)
            {
                id = Discovery.NewId();
                SessionState.SetString(IdKey, id);
            }
            return id;
        }

        public static void Advertise()
        {
            if (Discovery.Id != null) return;
            Discovery.Advertise(EditorId(), Instance.ProjectDir, Instance.HostName);
            if (long.TryParse(SessionState.GetString(RefusedKey, ""), out var refused))
                Discovery.Invites.Refused = new DateTime(refused, DateTimeKind.Utc);
        }

        public static bool Wanted =>
            Client != null && Client.WantConnected || !Application.isBatchMode && !SessionState.GetBool(DisconnectedKey, false);

        public static void Connect()
        {
            SessionState.EraseBool(DisconnectedKey);
            Unusable = null;
            Advertise();
            Ensure().Connect();
        }

        public static void Disconnect()
        {
            SessionState.SetBool(DisconnectedKey, true);
            Unusable = null;
            Client?.Disconnect();
            Hands.RemoveDevices();
            SessionState.EraseString(StateKey);
        }

        static void AutoConnect()
        {
            if (Application.isBatchMode || EditorApplication.timeSinceStartup < nextAutoConnect) return;
            nextAutoConnect = EditorApplication.timeSinceStartup + AutoConnectSeconds;
            if (SessionState.GetBool(DisconnectedKey, false)) return;
            // (a client that wants a connection looks for the invite itself)
            if (Client != null && Client.WantConnected) return;
            var invite = Discovery.Invites.Look();
            // (shown in the window: an invite is there, and this editor doesn't take it)
            Unusable = Discovery.Invites.Problem;
            if (invite != null) Connect();
        }

        public static string Unusable { get; private set; }

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
                    Advertise();
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
                                Map = Json.Str(w, "map"),
                                Stage = (int)Json.Num(w, "stage", 0),
                                Since = Json.Num(w, "since", EditorApplication.timeSinceStartup),
                            });
                        }
                    }
                    var session = Json.Str(state, "session");
                    // (a recording the brain asked for goes on over the resumed connection, once the game runs)
                    if (!string.IsNullOrEmpty(session)) Media.RestoreState(Json.Obj(state, "record"));
                    client.Resume(string.IsNullOrEmpty(session) ? null : session,
                        Json.Bool(state, "run_active", false), Json.Num(state, "connect_timeout", 0), Json.Num(state, "connect_since", 0), Json.Bool(state, "welcomed", false), (long)Json.Num(state, "received", 0));
                    return;
                }
            }
            if (Application.isBatchMode && PlaytestClient.Flag("CluewrenConnect") && !SessionState.GetBool(HeadlessKey, false))
                Headless.Start();
        }

        static void BeforeReload()
        {
            // A reload that takes this package out of the project runs none of its code afterwards, and the advert of a
            // living editor would stay for a brain to pick. The code that does load writes it again, with the same id.
            Discovery.Withdraw();
            if (Discovery.Invites != null)
                SessionState.SetString(RefusedKey, Discovery.Invites.Refused.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Client == null) return;
            var state = Client.SaveState();
            if (Json.Bool(state, "want", false))
            {
                SessionState.SetString(StateKey, Json.Write(state));
                SessionState.SetString(PendingKey, Json.Write(waits.Select(w => (object)new Dictionary<string, object>
                {
                    ["id"] = w.Id, ["action"] = w.Action, ["map"] = w.Map, ["stage"] = w.Stage, ["since"] = w.Since,
                }).ToList()));
            }
            Client.CloseForReload();
        }

        static void OnPlayMode(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode || change == PlayModeStateChange.ExitingEditMode) playFrames = 0;
            if (change == PlayModeStateChange.ExitingEditMode) SessionState.SetString(EditSceneKey, SceneManager.GetActiveScene().path ?? "");
            if (change == PlayModeStateChange.EnteredEditMode)
            {
                Ops.ResetSession();
                RestoreStartScene();
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
            // Not while there is no connection: whether the session these wait in goes on is known only by the next
            // welcome, and a step taken before it (into play mode again) can't be taken back.
            if (waits.Count == 0 || !client.Connected) return;
            var now = EditorApplication.timeSinceStartup;
            foreach (var w in waits.ToList())
            {
                var done = false;
                var playing = EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling;
                switch (w.Action)
                {
                    case "start":
                        if (playing && playFrames >= 3 && SceneManager.GetActiveScene().isLoaded)
                        {
                            RespondStarted(client, w.Id, w.Map);
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
                            done = !EnterPlayMode(client, w.Id, w.Map);
                        }
                        else if (w.Stage == 1 && playing && playFrames >= 3 && SceneManager.GetActiveScene().isLoaded)
                        {
                            RespondStarted(client, w.Id, w.Map);
                            done = true;
                        }
                        break;
                }
                if (!done && now - w.Since > 90)
                {
                    if (!EditorApplication.isPlayingOrWillChangePlaymode) RestoreStartScene();
                    client.RespondError(w.Id, $"Timed out: session {w.Action}", 500);
                    done = true;
                }
                if (done) waits.Remove(w);
            }
        }

        // -- IPlaytestHost -------------------------------------------------------------------------------------

        public string HostName => "editor";

        public string ProjectDir => ProjectDirOf(Application.dataPath);

        public static string ProjectDirOf(string dataPath) => Directory.GetParent(dataPath).FullName;

        public string ProjectId => PlayerSettings.productGUID.ToString();

        public bool SessionRunning => EditorApplication.isPlaying;

        public bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args)
        {
            var map = Json.Str(args, "map");
            string path = null;
            if (!string.IsNullOrEmpty(map) && action != "stop")
            {
                // Before anything changes, and before the 404: what is in .cluewren is not told apart from what isn't.
                if (Ops.RefusesScene(client, id, map)) return true;
                path = MapScene(map);
                if (path == null)
                {
                    Ops.NoScene(client, id, map);
                    return true;
                }
                if (Ops.RefusesScene(client, id, path)) return true;
            }
            switch (action)
            {
                case "start":
                    if (EditorApplication.isPlaying)
                    {
                        if (EditorApplication.isPlayingOrWillChangePlaymode)
                            RespondStarted(client, id, path, refreshExport: true);
                        else
                            client.RespondError(id, "Wait for Unity to return to Edit mode, then start the session again.", 409);
                        return true;
                    }
                    if (EnterPlayMode(client, id, path))
                        waits.Add(new SessionWait { Id = id, Action = "start", Map = path, Since = EditorApplication.timeSinceStartup });
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
                        if (EnterPlayMode(client, id, path))
                            waits.Add(new SessionWait { Id = id, Action = "start", Map = path, Since = EditorApplication.timeSinceStartup });
                        return true;
                    }
                    if (Json.Bool(args, "fresh", false))
                    {
                        // Out of play mode and in again: everything starts over (static state too, when scripts reload)
                        Hands.ReleaseAll();
                        EditorApplication.ExitPlaymode();
                        waits.Add(new SessionWait { Id = id, Action = "fresh", Map = path, Since = EditorApplication.timeSinceStartup });
                        return true;
                    }
                    Ops.Restart(client, id, map);
                    return true;
            }
            return false;
        }

        static void RespondStarted(PlaytestClient client, long id, string path, bool refreshExport = false)
        {
            var active = SceneManager.GetActiveScene().path;
            if (!string.IsNullOrEmpty(path) && path != active)
            {
                client.RespondError(id, "Unity is in Play mode in another scene. Restart the session with the requested scene, or stop the session and start it again.", 409);
                return;
            }
            if (refreshExport) RefreshExport(client);
            client.Respond(id, new Dictionary<string, object> { ["running"] = true, ["scene"] = active });
        }

        static void RefreshExport(PlaytestClient client)
        {
            try
            {
                if (ProjectExport.Needed(Instance.ProjectDir) != null)
                {
                    client.Status("Indexing the project (scenes, prefabs, input)");
                    ProjectExport.Export(Instance.ProjectDir);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Cluewren] The project index export failed: " + e.Message);
            }
        }

        static bool EnterPlayMode(PlaytestClient client, long id, string path)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                client.RespondError(id, "Wait for Unity to enter Play mode, then start the session again.", 409);
                return false;
            }
            RefreshExport(client);
            if (string.IsNullOrEmpty(path) && SessionState.GetBool(HeadlessKey, false))
                path = SessionState.GetString(Headless.SceneKey, "");
            if (!PrepareStartScene(path))
            {
                Ops.NoScene(client, id, path);
                return false;
            }
            client.Status("Entering play mode");
            EditorApplication.EnterPlaymode();
            return true;
        }

        internal static bool PrepareStartScene(string path)
        {
            var previous = EditorSceneManager.playModeStartScene;
            if (string.IsNullOrEmpty(path))
            {
                var active = SceneManager.GetActiveScene();
                if (previous != null || SceneManager.sceneCount != 1 || !string.IsNullOrEmpty(active.path) || active.isDirty)
                    return true;
                path = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled)?.path;
                if (string.IsNullOrEmpty(path)) return true;
            }
            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (asset == null) return false;
            if (asset == previous) return true;

            // SessionState keeps the original asset's GUID through Play mode and script reloads.
            if (SessionState.GetString(StartSceneKey, "-") == "-")
                SessionState.SetString(StartSceneKey, previous != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(previous)) : "");
            EditorSceneManager.playModeStartScene = asset;
            return true;
        }

        internal static void RestoreStartScene()
        {
            var previous = SessionState.GetString(StartSceneKey, "-");
            if (previous == "-") return;
            EditorSceneManager.playModeStartScene = previous.Length == 0 ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(AssetDatabase.GUIDToAssetPath(previous));
            SessionState.EraseString(StartSceneKey);
        }

        /// <summary>`levels` (docs/protocol.md): the project's scenes (under Assets), the one open in the editor and the
        /// one playing.</summary>
        static Dictionary<string, object> Levels()
        {
            var levels = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(p => p, StringComparer.Ordinal)
                .Cast<object>().ToList();
            var playing = EditorApplication.isPlaying ? SceneManager.GetActiveScene().path : "";
            var open = EditorApplication.isPlaying ? SessionState.GetString(EditSceneKey, "") : SceneManager.GetActiveScene().path ?? "";
            return new Dictionary<string, object> { ["levels"] = levels, ["open"] = open, ["playing"] = playing ?? "" };
        }

        /// <summary>A scene of the project loaded in play mode, in the build list or not (Ops.Restart's `map`).</summary>
        static bool LoadInPlayMode(string map)
        {
            var path = MapScene(map);
            if (string.IsNullOrEmpty(path) || !EditorApplication.isPlaying) return false;
            EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
            return true;
        }

        // The command line can use an absolute asset path. A session is limited to project-relative paths and names.
        static string MapScene(string map) => string.IsNullOrEmpty(map) || Ops.MapOutsideProject(map) ? null : ScenePath(map);

        public static string ScenePath(string map)
        {
            if (string.IsNullOrEmpty(map)) return null;
            if (int.TryParse(map, out var index))
                return index >= 0 ? EditorBuildSettings.scenes.Where(s => s.enabled).ElementAtOrDefault(index)?.path : null;
            var path = map.Replace('\\', '/');
            if (path.IndexOf('/') >= 0)
            {
                if (Path.IsPathRooted(path)) path = Path.GetRelativePath(Instance.ProjectDir, path).Replace('\\', '/');
                if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) path += ".unity";
                var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                return asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            }
            var name = map.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(map) : map;
            var inBuild = EditorBuildSettings.scenes.FirstOrDefault(s => Path.GetFileNameWithoutExtension(s.path) == name);
            if (inBuild != null) return inBuild.path;
            var guid = AssetDatabase.FindAssets("t:Scene " + name).FirstOrDefault(g => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == name);
            return guid != null ? AssetDatabase.GUIDToAssetPath(guid) : null;
        }

        public void Exit(int code)
        {
            EditorApplication.delayCall += () =>
            {
                Discovery.Withdraw();
                EditorApplication.Exit(code);
            };
        }
    }
}
