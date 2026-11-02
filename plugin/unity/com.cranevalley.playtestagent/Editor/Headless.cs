// Modified by Crane Valley: no token; the brain comes from brain.json unless -PlaytestUrl names one on this machine; the
// brain starts a run, so nothing here asks for one or reports one.
// The editor in batch mode as a headless game (CI, scripted runs):
//
//   Unity.exe -batchmode -nographics -projectPath <project> -executeMethod Autopilot.Playtest.Editor.Headless.Run
//       [-PlaytestUrl=ws://127.0.0.1:...] [-PlaytestMap=<scene>] [-logFile <log>]
//
// (never with -quit: the editor stays up for the brain). With -PlaytestEditMode it connects in edit mode and
// waits for the brain to enter play mode (session start), as the Playtest window does. It opens the scene (-PlaytestMap, else the build's
// first), writes the codex export when it is missing or stale, enters play mode, and connects once the scripts have
// reloaded for it. Without -executeMethod, -PlaytestConnect on a batch-mode editor does the same.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autopilot.Playtest.Editor
{
    public static class Headless
    {
        public static void Run()
        {
            Start();
        }

        public static void Start()
        {
            if (SessionState.GetBool(EditorHost.HeadlessKey, false)) return;
            SessionState.SetBool(EditorHost.HeadlessKey, true);
            var url = PlaytestClient.Arg("PlaytestUrl");
            var state = new Dictionary<string, object>
            {
                ["url"] = url,
                ["want"] = true,
                ["session"] = "",
                ["run_active"] = false,
                ["exit_on_failure"] = true,
                ["connect_timeout"] = PlaytestClient.CommandLineConnectTimeout(),
                ["welcomed"] = false,
            };
            Debug.Log($"[Autopilot] Headless editor -> {(string.IsNullOrEmpty(url) ? "brain.json" : url)}");
            var map = PlaytestClient.Arg("PlaytestMap");
            if (string.IsNullOrEmpty(map)) map = PlaytestClient.Arg("PlaytestScene");
            var project = Directory.GetParent(Application.dataPath).FullName;
            try
            {
                if (CodexExport.Needed(project) != null)
                    CodexExport.Export(Path.Combine(project, "Library", "AutopilotCodex"));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Autopilot] The project index export failed: " + e.Message);
            }
            if (!string.IsNullOrEmpty(map))
            {
                var path = EditorHost.ScenePath(map);
                if (path != null) EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                else Debug.LogWarning($"[Autopilot] No scene {map}");
            }
            else if (string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
            {
                var first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
                if (first != null) EditorSceneManager.OpenScene(first.path, OpenSceneMode.Single);
            }
            if (PlaytestClient.Flag("PlaytestEditMode"))
            {
                // Connect now, in edit mode, as the Playtest window does: the brain enters play mode (session start)
                var client = EditorHost.Ensure();
                client.ExitOnFailure = true;
                client.ConnectTimeout = PlaytestClient.CommandLineConnectTimeout();
                client.Connect(url);
                return;
            }
            // The client connects after the scripts reload for play mode (EditorHost.Restore); the wait for the brain's
            // welcome starts now, across the reload
            state["connect_since"] = System.DateTime.UtcNow.Subtract(System.DateTime.UnixEpoch).TotalSeconds;
            SessionState.SetString("AutopilotPlaytest.Client", Json.Write(state));
            EditorApplication.EnterPlaymode();
        }
    }
}
