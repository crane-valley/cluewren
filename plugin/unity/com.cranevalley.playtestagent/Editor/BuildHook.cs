// Modified by Crane Valley: a build no longer bakes in the editor's path, since a player no longer starts the editor.
// Player builds: the bridge's runtime is compiled only into Development builds (or with the scripting define
// AUTOPILOT_PLAYTEST: its assembly's define constraint), so a release build has none of it. For a build that has it, the
// first scene gets a hidden PlaytestBuildInfo: the project's productGUID (hello's project_id), and where the project
// was, so the player can serve the codex when it runs on the build machine; and the legacy Input Manager's axes, for
// LegacyInput.

using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Autopilot.Playtest.Editor
{
    sealed class BuildHook : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public static bool Included(BuildReport report)
        {
            if ((report.summary.options & BuildOptions.Development) != 0) return true;
            var target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(report.summary.platform));
            return PlayerSettings.GetScriptingDefineSymbols(target).Split(';').Contains("AUTOPILOT_PLAYTEST");
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || !Included(report)) return;  // (play mode in the editor, or a release build)
            var first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled);
            if (first == null || first.path != scene.path) return;
            var go = new GameObject("AutopilotPlaytestBuildInfo") { hideFlags = HideFlags.HideInHierarchy };
            var info = go.AddComponent<PlaytestBuildInfo>();
            info.productGuid = PlayerSettings.productGUID.ToString();
            info.projectDir = System.IO.Directory.GetParent(Application.dataPath).FullName;
            info.legacyAxes = Json.Write(LegacyAxes.Read());
            SceneManager.MoveGameObjectToScene(go, scene);
        }
    }
}
