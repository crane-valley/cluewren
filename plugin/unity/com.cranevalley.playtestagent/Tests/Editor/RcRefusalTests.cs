using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Autopilot.Playtest.Tests
{
    public class RcRefusalTests
    {
        GameObject go;

        [SetUp]
        public void Make() => go = new GameObject("RcRefusalProbe");

        [TearDown]
        public void Clean() => Object.DestroyImmediate(go);

        static Dictionary<string, object> Body(params (string, object)[] entries) => entries.ToDictionary(e => e.Item1, e => e.Item2);

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
        [TestCase(typeof(UnityWebRequest))]
        [TestCase(typeof(ScreenCapture))]
        [TestCase(typeof(UnityEditor.EditorApplication))]
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
            CollectionAssert.Contains(functions, nameof(AssetBundle.LoadFromFile));
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
        public void UnityApisStayReachableAsUpstream()
        {
            Assert.IsFalse(Reflect.Refused(typeof(AssetBundle).GetMethods().First(m => m.Name == nameof(AssetBundle.LoadFromFile) && m.GetParameters().Length == 1)));
            Assert.IsFalse(Reflect.Refused(typeof(Application).GetMethod(nameof(Application.OpenURL))));
            Assert.IsFalse(Reflect.Refused(typeof(Application.LogCallback).GetMethod("Invoke")));
            Assert.IsFalse(Reflect.Refused(typeof(object).GetMethod(nameof(ToString))));
        }
    }
}
