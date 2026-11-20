using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cluewren.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Cluewren.Tests
{
    public class EditorSessionTests
    {
        const string FolderKey = "Cluewren.Tests.SceneFolder";
        const string OriginalSceneKey = "Cluewren.Tests.StartScene";
        const string PreparedKey = "Cluewren.Tests.StartPrepared";

        static string ScenePath(string name) => SessionState.GetString(FolderKey, "") + "/" + name + ".unity";

        [SetUp]
        public void SaveTestScenes()
        {
            if (SessionState.GetString(FolderKey, "").Length != 0) return;
            SessionState.SetString(OriginalSceneKey, AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene) ?? "");
            var name = "CluewrenTests-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", name);
            SessionState.SetString(FolderKey, "Assets/" + name);
            foreach (var sceneName in new[] { "Preferred", "Requested" })
                Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath(sceneName), true));
        }

        [TearDown]
        public void RestoreEditor()
        {
            var folder = SessionState.GetString(FolderKey, "");
            if (folder.Length == 0) return;
            EditorHost.RestoreStartScene();
            var original = SessionState.GetString(OriginalSceneKey, "");
            EditorSceneManager.playModeStartScene = original.Length == 0 ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(original);
            AssetDatabase.DeleteAsset(folder);
            SessionState.EraseString(FolderKey);
            SessionState.EraseString(OriginalSceneKey);
            SessionState.EraseBool(PreparedKey);
        }

        [Test]
        public void ADirtyUntitledSceneStaysOpenWhenPreparingToPlay()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            for (var i = 0; i < SceneManager.sceneCount; ++i)
                if (SceneManager.GetSceneAt(i).isDirty) Assert.Ignore("Run with clean scenes open.");
            var untitledSceneOpen = setup.Length == 0 || setup.Any(scene => string.IsNullOrEmpty(scene.path));
            if (!Application.isBatchMode && untitledSceneOpen)
                Assert.Ignore("Run in batch mode or with saved scenes open.");
            var scenes = EditorBuildSettings.scenes;
            var untitled = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                var unsaved = new GameObject("Unsaved change");
                Assert.IsTrue(EditorSceneManager.MarkSceneDirty(untitled));
                EditorSceneManager.playModeStartScene = null;
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath("Requested"), true) };

                Assert.IsTrue(EditorHost.PrepareStartScene(null));

                Assert.IsNull(EditorSceneManager.playModeStartScene);
                Assert.AreEqual(untitled, SceneManager.GetActiveScene());
                Assert.IsTrue(untitled.isDirty);
                Assert.AreEqual(untitled, unsaved.scene);
            }
            finally
            {
                EditorBuildSettings.scenes = scenes;
                EditorSceneManager.SaveScene(untitled, ScenePath("Scratch"));
                if (untitledSceneOpen)
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                else
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
        }

        [UnityTest]
        public IEnumerator ARequestedPlayModeSceneOverridesAndThenRestoresThePreferredScene()
        {
            PrepareTestStart(ScenePath("Requested"));

            yield return new EnterPlayMode();
            Assert.AreEqual(ScenePath("Requested"), SceneManager.GetActiveScene().path);
            AssertAlreadyPlaying(EditorHost.Instance, ScenePath("Requested"), ScenePath("Preferred"));
            AssertAlreadyPlaying(new PlayerHost(), "Requested", ScenePath("Preferred"), expectedError: 404);
            yield return new ExitPlayMode();

            Assert.AreEqual(ScenePath("Preferred"), AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene));
        }

        static void AssertAlreadyPlaying(IPlaytestHost host, string current, string different, double expectedError = 409)
        {
            var sent = new List<string>();
            var client = new PlaytestClient(host) { TestWire = sent.Add };
            try
            {
                client.TestConnected(true);
                Assert.IsTrue(host.Session(client, 1, "start", new Dictionary<string, object> { ["map"] = different }));
                var error = Json.ParseObject(sent.Single());
                Assert.AreEqual(expectedError, error["status"]);
                if (expectedError == 409) StringAssert.Contains("Restart", (string)error["error"]);
                Assert.AreEqual(ScenePath("Requested"), SceneManager.GetActiveScene().path);

                sent.Clear();
                Assert.IsTrue(host.Session(client, 2, "start", new Dictionary<string, object> { ["map"] = current }));
                var result = (Dictionary<string, object>)Json.ParseObject(sent.Single())["result"];
                Assert.AreEqual(ScenePath("Requested"), result["scene"]);
            }
            finally
            {
                client.Shutdown();
            }
        }

        [UnityTest]
        public IEnumerator ASessionWithNoOverridePlaysAndKeepsThePreferredScene()
        {
            PrepareTestStart(null);

            yield return new EnterPlayMode();
            Assert.AreEqual(ScenePath("Preferred"), SceneManager.GetActiveScene().path);
            yield return new ExitPlayMode();

            Assert.AreEqual(ScenePath("Preferred"), AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene));
        }

        [UnityTest]
        public IEnumerator ARepeatedStartDropsAnExportForADeletedSceneWhilePlaying()
        {
            if (!Application.isBatchMode) Assert.Ignore("Run this test in an isolated batch project.");
            PrepareTestStart(null, export: true);

            yield return new EnterPlayMode();
            var path = ScenePath("Requested");
            var sceneExport = Path.Combine(ProjectExport.Folder(EditorHost.Instance.ProjectDir), "scenes", path.Substring("Assets/".Length) + ".txt");
            Assert.IsTrue(File.Exists(sceneExport));
            Assert.IsTrue(AssetDatabase.DeleteAsset(path));
            Assert.AreEqual("stale", ProjectExport.Needed(EditorHost.Instance.ProjectDir));

            var sent = new List<string>();
            var client = new PlaytestClient(EditorHost.Instance) { TestWire = sent.Add };
            try
            {
                client.TestConnected(true);
                Assert.IsTrue(EditorHost.Instance.Session(client, 1, "start", new Dictionary<string, object>()));
                Assert.IsTrue((bool)Json.ParseObject(sent.Single())["ok"]);
                Assert.IsFalse(File.Exists(sceneExport));
                Assert.IsNull(ProjectExport.Needed(EditorHost.Instance.ProjectDir));
            }
            finally
            {
                client.Shutdown();
            }
            yield return new ExitPlayMode();
            Assert.AreEqual("stale", ProjectExport.Needed(EditorHost.Instance.ProjectDir));
        }

        static void PrepareTestStart(string path, bool export = false)
        {
            // The test runner repeats the code before the yield when the editor reloads scripts.
            if (SessionState.GetBool(PreparedKey, false)) return;
            EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath("Preferred"));
            Assert.IsTrue(EditorHost.PrepareStartScene(path));
            if (export) ProjectExport.Export(EditorHost.Instance.ProjectDir);
            SessionState.SetBool(PreparedKey, true);
        }
    }
}
