// Modified by Crane Valley: no token; the brain's address comes from its invite alone; the brain starts a run, so
// nothing here asks for one or reports one.
// The editor in batch mode as a headless game (CI, scripted runs):
//
//   Unity.exe -batchmode -nographics -projectPath <project> -executeMethod Cluewren.Editor.Headless.Run
//       [-CluewrenMap=<scene>] [-CluewrenConnectTimeout=<seconds>] [-logFile <log>]
//
// (never with -quit: the editor stays up for the brain). With -CluewrenEditMode it connects in edit mode and waits for
// the brain to enter play mode (session start), as an editor with its window does. It advertises itself, opens the
// scene (-CluewrenMap, else the build's first), writes the project export when it is missing or stale, enters play
// mode, and connects once the scripts have reloaded for it and a brain has invited it. Without -executeMethod,
// -CluewrenConnect on a batch-mode editor does the same.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Cluewren.Editor
{
    public static class Headless
    {
        internal const string SceneKey = "Cluewren.HeadlessScene";

        public static void Run()
        {
            Start();
        }

        public static void Start()
        {
            if (SessionState.GetBool(EditorHost.HeadlessKey, false)) return;
            SessionState.SetBool(EditorHost.HeadlessKey, true);
            EditorHost.Advertise();
            var state = new Dictionary<string, object>
            {
                ["want"] = true,
                ["session"] = "",
                ["run_active"] = false,
                ["exit_on_failure"] = true,
                ["connect_timeout"] = PlaytestClient.CommandLineConnectTimeout(),
                ["welcomed"] = false,
            };
            Debug.Log("[Cluewren] Headless editor: waiting for a run to invite it");
            var map = PlaytestClient.Arg("CluewrenMap");
            var project = EditorHost.ProjectDirOf(Application.dataPath);
            try
            {
                if (ProjectExport.Needed(project) != null) ProjectExport.Export(project);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Cluewren] The project index export failed: " + e.Message);
            }
            var path = EditorHost.ScenePath(map);
            if (!string.IsNullOrEmpty(map) && path == null) Debug.LogWarning($"[Cluewren] No scene {map}");
            if (path != null) SessionState.SetString(SceneKey, path);
            if (PlaytestClient.Flag("CluewrenEditMode"))
            {
                // Connect now, in edit mode, as an editor with its window does: the brain enters play mode (session start)
                var client = EditorHost.Ensure();
                client.ExitOnFailure = true;
                client.ConnectTimeout = PlaytestClient.CommandLineConnectTimeout();
                client.Connect();
                return;
            }
            if (!EditorHost.PrepareStartScene(path))
            {
                Debug.LogError($"[Cluewren] Couldn't start Play mode in {path}");
                EditorHost.Instance.Exit(1);
                return;
            }
            // The client connects after the scripts reload for play mode (EditorHost.Restore); the wait for the brain's
            // welcome starts now, across the reload
            state["connect_since"] = System.DateTime.UtcNow.Subtract(System.DateTime.UnixEpoch).TotalSeconds;
            SessionState.SetString("Cluewren.Client", Json.Write(state));
            EditorApplication.EnterPlaymode();
        }
    }
}
