using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Cluewren.Tests
{
    public class RcRefusalTests
    {
        GameObject go;

        [SetUp]
        public void Make() => go = new GameObject("RcRefusalProbe");

        [TearDown]
        public void Clean() => Object.DestroyImmediate(go);

        static Dictionary<string, object> Body(params (string, object)[] entries) => entries.ToDictionary(e => e.Item1, e => e.Item2);

        sealed class NoHost : IPlaytestHost
        {
            public string HostName => "test";
            public string ProjectDir => "";
            public string ProjectId => "";
            public bool SessionRunning => false;
            public bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args) => false;
            public void Exit(int code) { }
        }

        static RcError Refuses(TestDelegate request)
        {
            var error = Assert.Throws<RcError>(request);
            Assert.AreEqual(403, error.Status, error.Message);
            return error;
        }

        static void RefusesCall(string path, string function, params (string, object)[] parameters) =>
            Refuses(() => Reflect.Call(Body(("objectPath", path), ("functionName", function), ("parameters", Body(parameters))), null));

        static MethodInfo Method(Type t, string name, params Type[] parameters) => t.GetMethod(name, parameters)
                                                                                 ?? throw new MissingMethodException(t.FullName, name);

        [TestCase(typeof(Stream))]
        [TestCase(typeof(FileStream))]
        [TestCase(typeof(Process))]
        [TestCase(typeof(Assembly))]
        [TestCase(typeof(Type))]
        [TestCase(typeof(Socket))]
        [TestCase(typeof(SafeFileHandle))]
        [TestCase(typeof(Stream[]))]
        [TestCase(typeof(List<Stream>))]
        [TestCase(typeof(Action<Stream>))]
        [TestCase(typeof(UnityWebRequest))]
        [TestCase(typeof(Ping))]
        [TestCase(typeof(Caching))]
        [TestCase(typeof(Microphone))]
        [TestCase(typeof(PlayerPrefs))]
        public void RefusedTypes(Type type)
        {
            Assert.IsTrue(Reflect.RefusedType(type), type.FullName);
        }

        [TestCase(typeof(int))]
        [TestCase(typeof(string))]
        [TestCase(typeof(object))]
        [TestCase(typeof(Vector3))]
        [TestCase(typeof(GameObject))]
        [TestCase(typeof(List<Vector3>))]
        [TestCase(typeof(Dictionary<string, Transform>))]
        [TestCase(typeof(System.Threading.Tasks.Task))]
        [TestCase(typeof(Action))]
        [TestCase(typeof(ScreenCapture))]
        [TestCase(typeof(AssetBundle))]
        public void TypesLeftAlone(Type type)
        {
            Assert.IsFalse(Reflect.RefusedType(type), type.FullName);
        }

        [Test]
        public void ReadsAndWritesKeepWorkingOnUnityMembers()
        {
            var path = Paths.Of(go.transform);
            Reflect.Property(Body(("objectPath", path), ("propertyName", "position"),
                ("propertyValue", Body(("position", Body(("x", 1.0), ("y", 2.0), ("z", 3.0)))))));
            Assert.AreEqual(new Vector3(1, 2, 3), go.transform.position);
            var read = Reflect.Property(Body(("objectPath", path), ("propertyName", "position")));
            Assert.AreEqual(2.0, Convert.ToDouble(((Dictionary<string, object>)read["position"])["y"]), 1e-5);
        }

        [Test]
        public void CallsKeepWorkingOnUnityMembers()
        {
            Reflect.Call(Body(("objectPath", Paths.Of(go)), ("functionName", "SetActive"), ("parameters", Body(("value", false)))), null);
            Assert.IsFalse(go.activeSelf);
            var version = Reflect.Property(Body(("objectPath", "type:UnityEngine.Application"), ("propertyName", "unityVersion")));
            Assert.AreEqual(Application.unityVersion, version["unityVersion"]);
            var time = Reflect.Property(Body(("objectPath", "type:UnityEngine.Time"), ("propertyName", "timeScale")));
            Assert.IsTrue(time.ContainsKey("timeScale"));
        }

        [TestCase("type:System.IO.File")]
        [TestCase("type:System.Diagnostics.Process")]
        [TestCase("type:System.Reflection.Assembly")]
        [TestCase("type:System.Environment")]
        public void TypesOutsideTheReachAreRefusedAsTargets(string path)
        {
            var error = Assert.Throws<RcError>(() => Reflect.Describe(Body(("objectPath", path))));
            Assert.AreEqual(403, error.Status, error.Message);
        }

        [Test]
        public void MembersDeclaredBySystemObjectAreOutOfReach()
        {
            Assert.IsTrue(Reflect.Refused(typeof(object).GetMethod(nameof(GetType))));
            Assert.Throws<RcError>(() => Reflect.Call(Body(("objectPath", Paths.Of(go)), ("functionName", "GetType")), null));
        }

        [Test]
        public void DescribeListsNothingRefused()
        {
            var description = Reflect.Describe(Body(("objectPath", "type:UnityEngine.AssetBundle")));
            var functions = ((List<object>)description["Functions"]).Cast<Dictionary<string, object>>().Select(f => (string)f["Name"]).ToList();
            CollectionAssert.DoesNotContain(functions, nameof(AssetBundle.LoadFromStream));
            CollectionAssert.DoesNotContain(functions, nameof(AssetBundle.LoadFromFile));
            CollectionAssert.DoesNotContain(functions, nameof(AssetBundle.LoadFromMemory));
            CollectionAssert.Contains(functions, nameof(AssetBundle.GetAllLoadedAssetBundles));
        }

        [TestCase(typeof(FileStream))]
        [TestCase(typeof(Process))]
        [TestCase(typeof(Type))]
        [TestCase(typeof(List<Stream>))]
        public void FromJsonBuildsNoRefusedType(Type type)
        {
            var error = Assert.Throws<RcError>(() => Reflect.FromJson(new Dictionary<string, object>(), type, null));
            Assert.AreEqual(403, error.Status);
        }

        [Test]
        public void FromJsonStillBuildsPlainValues()
        {
            Assert.AreEqual(new Vector3(1, 2, 3), Reflect.FromJson(Body(("x", 1.0), ("y", 2.0), ("z", 3.0)), typeof(Vector3), null));
            Assert.AreEqual(new List<int> { 4, 5 }, Reflect.FromJson(new List<object> { 4.0, 5.0 }, typeof(List<int>), null));
        }

        static int ran;

        static IEnumerable<int> Lazy()
        {
            ++ran;
            yield return 1;
        }

        sealed class Listed : IEnumerable
        {
            public IEnumerator GetEnumerator()
            {
                ++ran;
                yield return 1;
            }
        }

        [Test]
        public void ToJsonNeverRunsALazyEnumerable()
        {
            ran = 0;
            Reflect.ToJson(Lazy(), 0);
            Reflect.ToJson(new Listed(), 0);
            Assert.AreEqual(0, ran);
        }

        [Test]
        public void ToJsonKeepsMaterializedCollections()
        {
            Assert.AreEqual(new List<object> { 1.0 }, Reflect.ToJson(new HashSet<int> { 1 }, 0));
            Assert.AreEqual(new List<object> { 2.0 }, Reflect.ToJson(new Queue<int>(new[] { 2 }), 0));
        }

        [Test]
        public void ToJsonKeepsListsArraysAndDictionaries()
        {
            Assert.AreEqual(new List<object> { 1.0, 2.0 }, Reflect.ToJson(new List<int> { 1, 2 }, 0));
            Assert.AreEqual(new List<object> { 1.0, 2.0 }, Reflect.ToJson(new[] { 1, 2 }, 0));
            Assert.AreEqual(1.0, ((Dictionary<string, object>)Reflect.ToJson(new Dictionary<string, int> { ["a"] = 1 }, 0))["a"]);
        }

        [Test]
        public void ToJsonNamesARefusedValueWithoutTouchingIt()
        {
            using (var stream = new MemoryStream()) Assert.AreEqual("MemoryStream", Reflect.ToJson(stream, 0));
        }

        static void Named() => ran = 1;

        [Test]
        public void ADelegateIsReadAsNamesAndNeverInvoked()
        {
            ran = 0;
            var names = (List<object>)Reflect.ToJson(new Action(Named), 0);
            Assert.AreEqual(new List<object> { "Named" }, names);
            Assert.AreEqual(0, ran);
        }

        [Test]
        public void UnityApisOutsideTheRefusedRoutesStayReachable()
        {
            Assert.IsFalse(Reflect.Refused(typeof(Application.LogCallback).GetMethod("Invoke")));
            Assert.IsFalse(Reflect.Refused(typeof(object).GetMethod(nameof(ToString))));
            Assert.IsFalse(Reflect.Refused(Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), typeof(IEnumerator))));
            Assert.IsFalse(Reflect.Refused(Method(typeof(ScreenCapture), nameof(ScreenCapture.CaptureScreenshotAsTexture))));
            Assert.IsFalse(Reflect.Refused(Method(typeof(AssetBundle), nameof(AssetBundle.GetAllLoadedAssetBundles))));
        }

        [Test]
        public void AnObjectOfARefusedClassIsRefusedWhole()
        {
            var webcam = new WebCamTexture();
            try
            {
                var path = Paths.OfObject(webcam);
                Refuses(() => Reflect.Property(Body(("objectPath", path), ("propertyName", "filterMode"))));
                Refuses(() => Reflect.Describe(Body(("objectPath", path))));
            }
            finally
            {
                Object.DestroyImmediate(webcam);
            }
        }

        [Test]
        public void AComponentAnArgumentResolvesToIsJudgedByItsOwnType()
        {
            var holder = new GameObject("RcEditorOnlyHolder");
            try
            {
                holder.AddComponent<Unity.Netcode.RefusedNetworkProbe>();
                var error = Assert.Throws<RcError>(() => Reflect.FromJson(Paths.Of(holder), typeof(Behaviour), null));
                Assert.AreEqual(403, error.Status, error.Message);
                Assert.IsInstanceOf<Transform>(Reflect.FromJson(Paths.Of(holder), typeof(Transform), null));
            }
            finally
            {
                Object.DestroyImmediate(holder);
            }
        }

        [Test]
        public void AValueNestedPastTheDepthCheckedIsRefused()
        {
            object deep = ".cluewren/x.unity";
            object flat = 1.0;
            for (var i = 0; i < 8; ++i)
            {
                deep = new List<object> { deep };
                flat = new List<object> { flat };
            }
            RefusesCall("type:UnityEngine.Debug", "Log", ("message", deep));
            RefusesCall("type:UnityEngine.Debug", "Log", ("message", flat));
        }

        [Test]
        public void APathInsideAnObjectTypedArgumentIsRefused()
        {
            RefusesCall("type:UnityEngine.Debug", "Log", ("message", Body(("path", ".cluewren/runs/x/evil.unity"))));
            RefusesCall("type:UnityEngine.Debug", "Log", ("message", new List<object> { ".cluewren/x.unity" }));
        }

        [Test]
        public void C1ProcessesAndUrls()
        {
            Assert.IsTrue(Reflect.Refused(Method(typeof(Application), nameof(Application.OpenURL), typeof(string))));
            Assert.IsTrue(Reflect.Refused(Method(typeof(UnityEngine.Device.Application), nameof(Application.OpenURL), typeof(string))));
            RefusesCall("type:UnityEngine.Application", "OpenURL", ("url", "https://example.com"));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:System.Diagnostics.Process"))));
        }

        [Test]
        public void C2FilesByPath()
        {
            Assert.IsTrue(Reflect.Refused(Method(typeof(AssetBundle), nameof(AssetBundle.LoadFromFile), typeof(string))));
            Assert.IsTrue(Reflect.Refused(Method(typeof(AssetBundle), nameof(AssetBundle.LoadFromMemory), typeof(byte[]))));
            Assert.IsTrue(Reflect.Refused(Method(typeof(ScreenCapture), nameof(ScreenCapture.CaptureScreenshot), typeof(string))));
            Assert.IsTrue(Reflect.Refused(typeof(UnityEngine.Profiling.Profiler).GetProperty(nameof(UnityEngine.Profiling.Profiler.logFile))));
            RefusesCall("type:UnityEngine.AssetBundle", "LoadFromFile", ("path", "C:/x"));
            RefusesCall("type:UnityEngine.ScreenCapture", "CaptureScreenshot", ("filename", "C:/x.png"));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:UnityEngine.Windows.File"))));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:UnityEngine.Caching"))));
        }

        [Test]
        public void C3Network()
        {
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:UnityEngine.Networking.UnityWebRequest"))));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:UnityEngine.Ping"))));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:System.Net.Sockets.Socket"))));
        }

        [Test]
        public void C4CodeAndCallsByName()
        {
            foreach (var name in new[] { "SendMessage", "SendMessageUpwards", "BroadcastMessage" })
            {
                Assert.IsTrue(Reflect.Refused(Method(typeof(GameObject), name, typeof(string))), name);
                Assert.IsTrue(Reflect.Refused(Method(typeof(Component), name, typeof(string))), name);
                RefusesCall(Paths.Of(go), name, ("methodName", "SetActive"));
            }
            Assert.IsTrue(Reflect.Refused(Method(typeof(MonoBehaviour), nameof(MonoBehaviour.Invoke), typeof(string), typeof(float))));
            Assert.IsTrue(Reflect.Refused(Method(typeof(MonoBehaviour), nameof(MonoBehaviour.InvokeRepeating), typeof(string), typeof(float), typeof(float))));
            Assert.IsTrue(Reflect.Refused(Method(typeof(MonoBehaviour), nameof(MonoBehaviour.StartCoroutine), typeof(string))));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:System.Reflection.Assembly"))));
        }

        [Test]
        public void C5EditorScripting()
        {
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:UnityEditor.EditorApplication"))));
            Assert.IsTrue(Reflect.EditorOnly(typeof(RcRefusalTests).Assembly), "the editor-only test assembly");
            Assert.IsFalse(Reflect.EditorOnly(typeof(Reflect).Assembly), "the runtime assembly");
            Assert.IsFalse(Reflect.EditorOnly(typeof(GameObject).Assembly));
            var editorPackage = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.StartsWith("Unity.", StringComparison.Ordinal) && Reflect.EditorOnly(a))
                .SelectMany(a => a.GetExportedTypes().Where(t => t.IsClass && !t.IsGenericType)).FirstOrDefault();
            Assert.IsNotNull(editorPackage, "an editor-only Unity package assembly in this project");
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:" + editorPackage.FullName))));
        }

        [Test]
        public void C6HostData()
        {
            Assert.IsTrue(Reflect.Refused(typeof(GUIUtility).GetProperty(nameof(GUIUtility.systemCopyBuffer))));
            Assert.IsTrue(Reflect.Refused(typeof(SystemInfo).GetProperty(nameof(SystemInfo.deviceUniqueIdentifier))));
            Refuses(() => Reflect.Property(Body(("objectPath", "type:UnityEngine.GUIUtility"), ("propertyName", "systemCopyBuffer"))));
            Refuses(() => Reflect.Call(Body(("objectPath", "type:UnityEngine.GUIUtility"), ("functionName", "get_systemCopyBuffer")), null));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:System.Environment"))));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:UnityEngine.Microphone"))));
        }

        [Test]
        public void C7NoConsoleRoute()
        {
            Assert.Throws<RcError>(() => Reflect.Call(Body(("objectPath", "type:UnityEngine.Application"), ("functionName", "ExternalEval"),
                ("parameters", Body(("script", "x")))), null));
        }

        [Test]
        public void C8EditorSettings()
        {
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:UnityEditor.EditorPrefs"))));
            Refuses(() => Reflect.Describe(Body(("objectPath", "type:UnityEditor.PlayerSettings"))));
        }

        [TestCase(".cluewren")]
        [TestCase(".cluewren/runs/x/work/evil.unity")]
        [TestCase("a/.CLUEWREN/x")]
        [TestCase(@"C:\other\.cluewren\x")]
        [TestCase(@"\\server\share\.cluewren\x")]
        [TestCase(@"\\?\C:\other\.cluewren\x")]
        [TestCase(@"C:.cluewren\x")]
        [TestCase(".cluewren.")]
        [TestCase(".cluewren ")]
        [TestCase(@".cluewren.\x.unity")]
        [TestCase(".cluewren::$INDEX_ALLOCATION")]
        [TestCase(".cluewren:$I30:$INDEX_ALLOCATION")]
        [TestCase(".cluewren:x")]
        [TestCase(@".cluewren\x.unity::$DATA")]
        [TestCase("Assets/../.cluewren/x.unity")]
        [TestCase("Main:/../.cluewren/x.unity")]
        [TestCase("file:///C:/p/%2Ecluewren/x")]
        [TestCase("file:///C:/p/.cluewre%6e%2Fx")]
        [TestCase("CLUEWR~1")]
        [TestCase("a/cluewr~4/x")]
        [TestCase("cl12ab~1")]
        [TestCase(@"C:\other\CLUEW~12\x")]
        [TestCase("CL~1")]
        [TestCase("CLUEWR~1::$INDEX_ALLOCATION")]
        public void C9APathWithASegmentThatNamesTheDataFolderIsRefused(string path)
        {
            Assert.IsTrue(Paths.DataPath(path));
            RefusesCall(Paths.Of(go), "CompareTag", ("tag", path));
        }

        [TestCase(".cluewrens/x")]
        [TestCase("x.cluewren/y")]
        [TestCase("my.cluewren")]
        [TestCase("a/cluewren/b")]
        [TestCase("Assets/NUL")]
        [TestCase("Assets/Scenes/Main.unity")]
        [TestCase("UserSettings/Cluewren/x.unity")]
        [TestCase(@"C:\PROGRA~1\Game\Main.unity")]
        [TestCase("Main:/Player|PlayerMover")]
        [TestCase("/Level/Pickups/Gem@2")]
        [TestCase("asset:Material/Red#4")]
        [TestCase("100%/x")]
        [TestCase("")]
        [TestCase("CLUEWREN~1")]
        [TestCase("CLUEWR~12")]
        [TestCase("CLUEWR~1.txt")]
        [TestCase("PROGRA~1")]
        [TestCase("CL~")]
        [TestCase("CLUEWR~1a")]
        [TestCase("a/CLUEWR/x")]
        public void C9OtherPathsAreLeftAlone(string path)
        {
            Assert.IsFalse(Paths.DataPath(path));
        }

#if UNITY_EDITOR_WIN
        static string ShortNameOf(string folder)
        {
            var given = Path.GetFileName(TestFiles.ShortPath(folder));
            return string.Equals(given, Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase) ? null : given;
        }

        [Test]
        public void C9TheShortNameWindowsGaveTheDataFolderNamesItToo()
        {
            var project = Path.GetDirectoryName(Application.dataPath);
            var folder = Path.Combine(project, ".cluewren");
            Directory.CreateDirectory(folder);
            var given = ShortNameOf(folder);
            if (given == null) Assert.Ignore("this volume gives no 8.3 short names");
            Assert.IsTrue(Paths.DataPath(given), given);
            Assert.IsTrue(Paths.DataPath(given + "/x.unity"), given);
            Assert.IsTrue(Paths.DataPath(Path.Combine(project, given.ToLowerInvariant(), "runs", "x.unity")), given);
            Assert.IsTrue(Paths.DataPath(@"D:\elsewhere\" + given + @"\x"), given);
        }

        [Test]
        public void C9SoDoesTheOneItGivesWhenTheFirstIsTaken()
        {
            var project = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + "-a-project-folder");
            // (made first, it takes the short name the data folder would have got)
            Directory.CreateDirectory(Path.Combine(project, ".cluewren-made-first"));
            Directory.CreateDirectory(Path.Combine(project, ".cluewren"));
            try
            {
                var given = ShortNameOf(Path.Combine(project, ".cluewren"));
                if (given == null) Assert.Ignore("this volume gives no 8.3 short names");
                Assert.AreNotEqual(ShortNameOf(Path.Combine(project, ".cluewren-made-first")), given);
                Assert.IsTrue(Paths.DataPath(given + "/x.unity"), given);
                var ancestor = TestFiles.ShortPath(project);
                Assert.IsTrue(Paths.DataPath(Path.Combine(ancestor, given, "x.unity")), ancestor);
                Assert.IsFalse(Paths.DataPath(Path.Combine(ancestor, "Assets", "Main.unity")), ancestor);
            }
            finally
            {
                Directory.Delete(project, true);
            }
        }
#endif

        [Test]
        public void C9AnObjectNamedAsTheDataFolderIsNoTarget()
        {
            var named = new GameObject(".cluewren");
            try
            {
                Refuses(() => Reflect.Call(Body(("objectPath", Paths.Of(named)), ("functionName", "SetActive"), ("parameters", Body(("value", false)))), null));
                Assert.IsTrue(named.activeSelf);
                Refuses(() => Reflect.Property(Body(("objectPath", Paths.Of(named)), ("propertyName", "name"))));
                Refuses(() => Reflect.Describe(Body(("objectPath", Paths.Of(named)))));
                Refuses(() => Reflect.Describe(Body(("objectPath", "Main:/.cluewren"))));
            }
            finally
            {
                Object.DestroyImmediate(named);
            }
        }

        [TestCase(".cluewren")]
        [TestCase(".cluewren::$INDEX_ALLOCATION")]
        [TestCase("%2Ecluewren%2Fruns%2Fx")]
        [TestCase("CLUEWR~1")]
        public void C9AStringWithNoSeparatorIsJudgedAsAnyOther(string text)
        {
            RefusesCall(Paths.Of(go), "CompareTag", ("tag", text));
            Refuses(() => Reflect.Property(Body(("objectPath", Paths.Of(go)), ("propertyName", "name"), ("propertyValue", Body(("name", text))))));
            Assert.AreEqual("RcRefusalProbe", go.name);
            RefusesCall("type:UnityEngine.Debug", "Log", ("message", text));
            RefusesCall("type:UnityEngine.Debug", "Log", ("message", Body(("path", text))));
            RefusesCall("type:UnityEngine.Debug", "Log", ("message", new List<object> { text }));
        }

        [TestCase("start")]
        [TestCase("restart")]
        public void C9ASessionStartsWithNoSceneInTheDataFolder(string action)
        {
            const string map = ".cluewren/runs/rc-refusal-test/work/evil.unity";
            var run = Path.Combine(Path.GetDirectoryName(Application.dataPath), ".cluewren", "runs", "rc-refusal-test");
            Directory.CreateDirectory(Path.Combine(run, "work"));
            File.WriteAllText(Path.Combine(run, "work", "evil.unity"), "");
            var said = new List<string>();
            var client = new PlaytestClient(Cluewren.Editor.EditorHost.Instance) { TestWire = said.Add };
            try
            {
                client.TestConnected(true);
                Assert.IsTrue(Cluewren.Editor.EditorHost.Instance.Session(client, 7, action, Body(("map", map))));
                Assert.IsFalse(UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode);
                Assert.AreEqual(1, said.Count);
                var answer = Json.ParseObject(said[0]);
                Assert.AreEqual(7.0, answer["id"]);
                Assert.AreEqual(403.0, answer["status"]);
                Assert.AreEqual(map + " is refused: nothing in .cluewren is opened as a scene", answer["error"]);
            }
            finally
            {
                // Without the refusal the editor is on its way into play mode, and would take this test run with it.
                if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) UnityEditor.EditorApplication.ExitPlaymode();
                client.Shutdown();
                Directory.Delete(run, true);
            }
        }

        [Test]
        public void C9ARestartOpensNoSceneInTheDataFolder()
        {
            var said = new List<string>();
            var client = new PlaytestClient(new NoHost()) { TestWire = said.Add };
            try
            {
                client.TestConnected(true);
                Ops.Restart(client, 7, ".cluewren/runs/x/work/evil.unity");
                var answer = Json.ParseObject(said.Single());
                Assert.AreEqual(7.0, answer["id"]);
                Assert.AreEqual(403.0, answer["status"]);
                Assert.AreEqual(".cluewren/runs/x/work/evil.unity is refused: nothing in .cluewren is opened as a scene", answer["error"]);
            }
            finally
            {
                client.Shutdown();
            }
        }

        [TestCase("Main")]
        [TestCase("Main.unity")]
        [TestCase("3")]
        [TestCase("Assets/Scenes/Main.unity")]
        [TestCase(@"Assets\Scenes\Main.unity")]
        [TestCase("Packages/com.example.game/Scenes/Demo.unity")]
        [TestCase("Scenes/Main")]
        public void AMapIsASceneOfTheProjectByNameIndexOrPath(string map)
        {
            Assert.IsFalse(Ops.MapOutsideProject(map));
        }

        [TestCase(@"\\host\share\x.unity")]
        [TestCase(@"\\?\C:\other\x.unity")]
        [TestCase(@"C:\other\x.unity")]
        [TestCase("C:x.unity")]
        [TestCase("/x.unity")]
        [TestCase("Assets/../x.unity")]
        [TestCase(@"Assets\.. \x.unity")]
        [TestCase("file:///C:/other/x.unity")]
        public void ARestartLooksForNoSceneOutsideTheProject(string map)
        {
            var said = new List<string>();
            var client = new PlaytestClient(new NoHost()) { TestWire = said.Add };
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            try
            {
                client.TestConnected(true);
                Ops.Restart(client, 7, map);
                var answer = Json.ParseObject(said.Single());
                Assert.AreEqual(7.0, answer["id"]);
                Assert.AreEqual(404.0, answer["status"]);
                Assert.AreEqual("No scene " + map + " in the project", answer["error"]);
                Assert.AreEqual(active, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            }
            finally
            {
                client.Shutdown();
            }
        }

        [Test]
        public void ASessionLooksForNoSceneOutsideTheProject(
            [Values("start", "restart")] string action, [Values("drive", "device", "share", "above", "drive's folder", "rooted")] string kind)
        {
            var project = Path.GetDirectoryName(Application.dataPath);
            var outside = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".unity");
            var above = "outside-" + Path.GetRandomFileName() + ".unity";
            File.WriteAllText(outside, "");
            File.WriteAllText(Path.Combine(project, above), "");
            // (scene files that are there: looked for, they would be found, and the session would go on with them)
            var map = kind == "drive" ? outside
                : kind == "device" ? @"\\?\" + outside
                : kind == "share" ? @"\\localhost\" + outside[0] + "$" + outside.Substring(2)
                : kind == "above" ? "Assets/../" + above
                : kind == "drive's folder" ? project.Substring(0, 2) + above
                : "/" + above;
            var said = new List<string>();
            var client = new PlaytestClient(Cluewren.Editor.EditorHost.Instance) { TestWire = said.Add };
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            try
            {
                client.TestConnected(true);
                Assert.IsTrue(Cluewren.Editor.EditorHost.Instance.Session(client, 7, action, Body(("map", map))));
                Assert.IsFalse(UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode, map);
                Assert.AreEqual(1, said.Count, map);
                var answer = Json.ParseObject(said[0]);
                Assert.AreEqual(7.0, answer["id"]);
                Assert.AreEqual(404.0, answer["status"], map);
                Assert.AreEqual("No scene " + map + " in the project", answer["error"]);
                Assert.AreEqual(active, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            }
            finally
            {
                // Without the answer the editor is on its way into play mode, and would take this test run with it.
                if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) UnityEditor.EditorApplication.ExitPlaymode();
                client.Shutdown();
                File.Delete(outside);
                File.Delete(Path.Combine(project, above));
            }
        }

        static readonly HashSet<string> PathWords = new HashSet<string>
        {
            "path", "paths", "file", "files", "filename", "dir", "directory", "folder", "url", "uri", "host", "hostname", "address",
        };

        static bool PathLike(string name) =>
            Regex.Split(name ?? "", "(?<=[a-z0-9])(?=[A-Z])|_").Any(w => PathWords.Contains(w.ToLowerInvariant()));

        static readonly Dictionary<string, string> Reviewed = new Dictionary<string, string>
        {
            ["UnityEngine.Resources.Load"] = "a Resources folder inside Assets",
            ["UnityEngine.Resources.LoadAll"] = "a Resources folder inside Assets",
            ["UnityEngine.Resources.LoadAsync"] = "a Resources folder inside Assets",
            ["UnityEngine.Resources.GetBuiltinResource"] = "the engine's built-in resources",
            ["UnityEngine.SceneManagement.SceneManager.GetSceneByPath"] = "a scene already loaded",
            ["UnityEngine.SceneManagement.SceneUtility.GetBuildIndexByScenePath"] = "the build's scene list",
            ["UnityEngine.Audio.AudioMixer.FindMatchingGroups"] = "a mixer group path",
            ["UnityEngine.AvatarMask.SetTransformPath"] = "a transform path in the hierarchy",
            ["UnityEngine.CreateAssetMenuAttribute.fileName"] = "an attribute's default asset name",
            ["UnityEngine.Debug.ExtractStackTraceNoAlloc"] = "a stack trace into a buffer",
            ["UnityEngine.TextCore.Text.TextSettings.defaultColorGradientPresetsPath"] = "a path inside Resources",
            ["UnityEngine.TextCore.Text.TextSettings.defaultFontAssetPath"] = "a path inside Resources",
            ["UnityEngine.TextCore.Text.TextSettings.defaultSpriteAssetPath"] = "a path inside Resources",
        };

        static readonly string[] ReviewedNamespaces =
        {
            "UnityEngine.InputSystem",  // control paths (<Keyboard>/space), not files
            "Unity.Properties",  // property paths
            "UnityEngine.UIElements",  // menu and binding paths
        };

        [Test]
        public void EveryReachablePathTakingMemberIsRefusedOrReviewed()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var unreviewed = new SortedSet<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (!name.StartsWith("UnityEngine", StringComparison.Ordinal) && !name.StartsWith("Unity.", StringComparison.Ordinal)) continue;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                foreach (var t in types.Where(t => t.IsPublic || t.IsNestedPublic))
                {
                    var members = t.GetMethods(flags)
                        .Where(m => !m.IsSpecialName && m.GetParameters().Any(p => p.ParameterType == typeof(string) && PathLike(p.Name)))
                        .Cast<MemberInfo>()
                        .Concat(t.GetProperties(flags).Where(p => p.CanWrite && p.PropertyType == typeof(string) && PathLike(p.Name)));
                    foreach (var m in members)
                    {
                        if (m.IsDefined(typeof(ObsoleteAttribute), true) || Reflect.Refused(m)) continue;
                        var key = t.FullName + "." + m.Name;
                        if (Reviewed.ContainsKey(key) || ReviewedNamespaces.Any(ns => (t.Namespace ?? "").StartsWith(ns, StringComparison.Ordinal))) continue;
                        unreviewed.Add(key);
                    }
                }
            }
            Assert.IsEmpty(unreviewed, string.Join("\n", unreviewed));
        }
    }
}
