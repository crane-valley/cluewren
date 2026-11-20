// Modified by Crane Valley: a build no longer bakes in the editor's path, since a player no longer starts the editor;
// a Development build no longer has the bridge unless the project asks for it.
// Player builds: the bridge's runtime is compiled only into a build of a project with the scripting define
// CLUEWREN_PLAYTEST (its assembly's define constraint), Development or not, so any other build has none of it. For a
// build that has it, the first scene gets a hidden PlaytestBuildInfo: the project's productGUID (hello's project_id),
// and where the project was, which the player advertises and the brain matches with a run's project; and the legacy
// Input Manager's axes, for LegacyInput.

using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Cluewren.Tests")]

namespace Cluewren.Editor
{
    sealed class BuildHook : IProcessSceneWithReport
    {
        const string Define = "CLUEWREN_PLAYTEST";

        public int callbackOrder => 0;

        // A dedicated server is a subtarget of the standalone platforms with scripting defines of its own.
        internal static NamedBuildTarget NamedTarget(BuildTarget platform, int subtarget)
        {
            var group = BuildPipeline.GetBuildTargetGroup(platform);
            return group == BuildTargetGroup.Standalone && subtarget == (int)StandaloneBuildSubtarget.Server
                ? NamedBuildTarget.Server : NamedBuildTarget.FromBuildTargetGroup(group);
        }

        internal static bool Defined(string settings, string[] extra) =>
            settings.Split(';').Contains(Define) || extra != null && extra.Contains(Define);

        public static bool Included(BuildReport report)
        {
            var summary = report.summary;
            if (summary.buildType != BuildType.Player) return false;
            var extra = BuildOptionsNote.Platform == summary.platform ? BuildOptionsNote.ExtraDefines : null;
            var standalone = BuildPipeline.GetBuildTargetGroup(summary.platform) == BuildTargetGroup.Standalone;
            var subtarget = standalone ? (int)summary.GetSubtarget<StandaloneBuildSubtarget>() : 0;
            return Defined(PlayerSettings.GetScriptingDefineSymbols(NamedTarget(summary.platform, subtarget)), extra);
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || !Included(report)) return;  // (play mode in the editor, or a build without the bridge)
            if (BuildOptionsNote.Platform != report.summary.platform || BuildOptionsNote.FirstScene != scene.path) return;
            var go = new GameObject("CluewrenBuildInfo") { hideFlags = HideFlags.HideInHierarchy };
            var info = go.AddComponent<PlaytestBuildInfo>();
            info.productGuid = PlayerSettings.productGUID.ToString();
            info.projectDir = EditorHost.ProjectDirOf(Application.dataPath);
            info.legacyAxes = Json.Write(LegacyAxes.Read());
            SceneManager.MoveGameObjectToScene(go, scene);
        }
    }

    // BuildPlayerOptions.extraScriptingDefines compile a build with defines that no setting holds, and the report of
    // the build doesn't tell them: they are noted here, where every player build passes before it starts.
    sealed class BuildOptionsNote : BuildPlayerProcessor
    {
        public static BuildTarget Platform { get; private set; }
        public static string[] ExtraDefines { get; private set; }
        public static string FirstScene { get; private set; }

        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
            => Note(buildPlayerContext.BuildPlayerOptions);

        internal static void Note(BuildPlayerOptions options)
        {
            Platform = options.target;
            ExtraDefines = options.extraScriptingDefines;
            FirstScene = options.scenes?.FirstOrDefault() ?? SceneManager.GetActiveScene().path;
        }
    }
}
