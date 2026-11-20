using Cluewren.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;

namespace Cluewren.Tests
{
    public class BuildHookTests
    {
        [Test]
        public void TheBuildUsesTheFirstSceneFromItsOwnOptions()
        {
            var settings = EditorBuildSettings.scenes;
            try
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Default.unity", true) };
                BuildOptionsNote.Note(new BuildPlayerOptions { scenes = new[] { "Assets/Custom.unity", "Assets/Default.unity" } });
                Assert.AreEqual("Assets/Custom.unity", BuildOptionsNote.FirstScene);
            }
            finally
            {
                EditorBuildSettings.scenes = settings;
            }
        }

        [Test]
        public void ADedicatedServerBuildIsJudgedByItsOwnDefines()
        {
            const int server = (int)StandaloneBuildSubtarget.Server;
            Assert.AreEqual(NamedBuildTarget.Server, BuildHook.NamedTarget(BuildTarget.StandaloneWindows64, server));
            Assert.AreEqual(NamedBuildTarget.Server, BuildHook.NamedTarget(BuildTarget.StandaloneLinux64, server));
            Assert.AreEqual(NamedBuildTarget.Standalone, BuildHook.NamedTarget(BuildTarget.StandaloneWindows64, (int)StandaloneBuildSubtarget.Player));
            Assert.AreEqual(NamedBuildTarget.Android, BuildHook.NamedTarget(BuildTarget.Android, server), "another platform's subtargets are its own");
        }

        [TestCase("CLUEWREN_PLAYTEST", null, true)]
        [TestCase("A;CLUEWREN_PLAYTEST;B", null, true)]
        [TestCase("", "CLUEWREN_PLAYTEST", true)]
        [TestCase("A", "B;CLUEWREN_PLAYTEST", true)]
        [TestCase("A", "B", false)]
        [TestCase("CLUEWREN_PLAYTEST_OFF", null, false)]
        [TestCase("", null, false)]
        public void TheBridgeIsInABuildThatDefinesItInItsSettingsOrForItself(string settings, string extra, bool included)
        {
            Assert.AreEqual(included, BuildHook.Defined(settings, extra?.Split(';')));
        }
    }
}
