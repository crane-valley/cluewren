// How an editor and a run find each other: the advert the editor writes, the invite it takes, and what it does with
// the welcome that follows. The hello's known answer is the same as in the Godot and Unreal tests.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
#if CLUEWREN_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Cluewren.Tests
{
    public class BindingTests
    {
        const string KnownMac = "b0e5b16410e69ae2e7ab99cb08045c3b0f6ac8cf250e9898d151a05c964a31a3";
        const string Mine = "0123456789abcdef0123456789abcdef";
        const string Url = "ws://127.0.0.1:1/v1/bridge";

        static readonly DateTime Written = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        string folder;
        string path;

        [SetUp]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            path = Discovery.InvitePath(folder, 4321);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
        }

        [TearDown]
        public void RemoveFolder() => Directory.Delete(folder, true);

        static byte[] Range(int from) => Enumerable.Range(from, 32).Select(i => (byte)i).ToArray();

        static int Self()
        {
            using (var self = Process.GetCurrentProcess()) return self.Id;
        }

        static Dictionary<string, object> Said(string editorId = Mine, object protocol = null, string secret = null, int? pid = null,
            string url = Url)
        {
            return new Dictionary<string, object>
            {
                ["url"] = url, ["secret"] = secret ?? Convert.ToBase64String(Range(0)), ["pid"] = pid ?? Self(), ["born"] = 1,
                ["started"] = "2026-11-01T00:00:00+00:00", ["protocol"] = protocol ?? 2, ["editor_id"] = editorId,
            };
        }

        // The write time is set: two writes in one tick of the clock would leave it the same.
        void Put(Dictionary<string, object> said, int age = 0) => PutText(Json.Write(said), age);

        void PutText(string text, int age = 0)
        {
            File.WriteAllText(path, text);
            File.SetLastWriteTimeUtc(path, Written.AddSeconds(age));
            TestFiles.MakeMine(Path.GetDirectoryName(path), path);
        }

        string Read(out Invite invite) => Discovery.ReadInvite(path, Mine, out invite);

        [Test]
        public void TheAdvertSaysWhatTheContractNamesAndNothingElse()
        {
            var advert = Discovery.Advert(1234, Mine, @"C:\Games\Foo", "editor");
            CollectionAssert.AreEquivalent(new[]
            {
                "pid", "id", "engine", "engine_version", "plugin_version", "protocol_min", "protocol_max", "project_dir", "host",
            }, advert.Keys);
            var file = Discovery.AdvertPath(folder, 1234);
            Assert.AreEqual(Path.Combine(folder, "editors", "1234.json"), file);
            Discovery.WriteAdvert(file, Discovery.Advert(1234, Mine, @"C:\Games\Old", "editor"));
            Discovery.WriteAdvert(file, advert);
            Assert.AreEqual((byte)'{', File.ReadAllBytes(file)[0], "no byte order mark");
            CollectionAssert.AreEqual(new[] { file }, Directory.GetFiles(Path.GetDirectoryName(file)));
            var read = Json.ParseObject(File.ReadAllText(file));
            Assert.AreEqual(1234.0, read["pid"]);
            Assert.AreEqual(Mine, read["id"]);
            Assert.AreEqual("unity", read["engine"]);
            Assert.AreEqual(UnityEngine.Application.unityVersion, read["engine_version"]);
            Assert.AreEqual("0.2.0", read["plugin_version"]);
            Assert.AreEqual(2.0, read["protocol_min"]);
            Assert.AreEqual(2.0, read["protocol_max"]);
            Assert.AreEqual(@"C:\Games\Foo", read["project_dir"]);
            Assert.AreEqual("editor", read["host"]);
        }

        [Test]
        public void AnIdIs32LowercaseHexCharactersAndItsOwn()
        {
            var id = Discovery.NewId();
            Assert.IsTrue(Regex.IsMatch(id, "^[0-9a-f]{32}$"), id);
            Assert.AreNotEqual(id, Discovery.NewId());
        }

        [Test]
        public void AnInviteForThisEditorFromARunningBrainIsTaken()
        {
            Put(Said());
            Assert.IsNull(Read(out var invite));
            Assert.AreEqual(Range(0), invite.Secret);
            Assert.AreEqual(new Uri(Url), invite.Url);
            Assert.AreEqual(Self(), invite.Pid);
        }

        [TestCase("not json")]
        [TestCase("[]")]
        [TestCase("\"text\"")]
        public void WhatIsNoJsonObjectIsNoInvite(string text)
        {
            PutText(text);
            StringAssert.EndsWith("isn't an invite", Read(out var invite));
            Assert.IsNull(invite);
        }

        [Test]
        public void AnInviteWithAnotherEditorIdIsPassedOverBeforeAnythingElseIsJudged()
        {
            Put(Said(editorId: "fedcba9876543210fedcba9876543210", protocol: 3, secret: "short", pid: 0, url: "ws://example.invalid:1/"));
            Assert.IsNull(Read(out var invite));
            Assert.IsNull(invite);
            var none = Said();
            none.Remove("editor_id");
            Put(none);
            Assert.IsNull(Read(out invite));
            Assert.IsNull(invite);
        }

        [Test]
        public void AnotherProtocolGivesTheReleaseTextBeforeTheSecretIsJudged()
        {
            Put(Said(protocol: 3, secret: "short", pid: 0, url: "ws://example.invalid:1/"));
            Assert.AreEqual("The cluewren command and this plugin are of different releases: install the same release of both", Read(out var invite));
            Assert.IsNull(invite);
            Put(Said(protocol: "2"));
            Assert.AreEqual(Discovery.OtherRelease, Read(out _));
            var none = Said();
            none.Remove("protocol");
            Put(none);
            Assert.AreEqual(Discovery.OtherRelease, Read(out _));
        }

        [TestCase("short")]
        [TestCase("not base64!")]
        [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
        public void ASecretUnder32BytesIsRefusedBeforeTheBrainIsLookedFor(string secret)
        {
            Put(Said(secret: secret, pid: 0, url: "ws://example.invalid:1/"));
            StringAssert.EndsWith("has no usable secret", Read(out var invite));
            Assert.IsNull(invite);
        }

        [Test]
        public void ABrainThatIsGoneEndsTheRunBeforeTheUrlIsJudged()
        {
            Put(Said(pid: 0, url: "ws://example.invalid:1/"));
            Assert.AreEqual("The run this editor was invited to has ended", Read(out var invite));
            Assert.IsNull(invite);
        }

        [TestCase("ws://example.invalid:1/")]
        [TestCase("ws://user@127.0.0.1:1/")]
        [TestCase("wss://127.0.0.1:1/")]
        public void AUrlOffThisMachineIsRefusedLast(string url)
        {
            Put(Said(url: url));
            StringAssert.EndsWith("refused", Read(out var invite));
            Assert.IsNull(invite);
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void AnInviteOfAnotherOwnerIsRefusedUnread()
        {
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            StringAssert.EndsWith("isn't owned by this user: refused", Discovery.ReadInvite(system, Mine, out var invite));
            Assert.IsNull(invite);
        }
#endif

        [Test]
        public void TheInviteIsReadAgainOnlyWhenItHasChanged()
        {
            var watch = new InviteWatch(path, Mine);
            Assert.IsNull(watch.Look());
            Assert.IsNull(watch.Problem);

            Put(Said());
            var first = watch.Look();
            Assert.IsNotNull(first);
            Put(Said(url: "ws://127.0.0.1:2/v1/bridge"));
            Assert.AreSame(first, watch.Look(), "the same write time: not read again");

            Put(Said(url: "ws://127.0.0.1:2/v1/bridge"), 1);
            Assert.AreEqual(2, watch.Look().Url.Port);

            File.Delete(path);
            Assert.IsNull(watch.Look());
            Assert.IsNull(watch.Problem);
        }

        [Test]
        public void TheWatchSaysWhyAnInviteIsNotTakenAndNothingForAnotherEditors()
        {
            var watch = new InviteWatch(path, Mine);
            Put(Said(protocol: 1));
            Assert.IsNull(watch.Look());
            Assert.AreEqual(Discovery.OtherRelease, watch.Problem);

            Put(Said(editorId: "fedcba9876543210fedcba9876543210"), 1);
            Assert.IsNull(watch.Look());
            Assert.IsNull(watch.Problem);

            Put(Said(pid: 0), 2);
            Assert.IsNull(watch.Look());
            Assert.AreEqual(Discovery.RunEnded, watch.Problem);

            Put(Said(), 3);
            Assert.IsNotNull(watch.Look());
            Assert.IsNull(watch.Problem);
        }

        [Test]
        public void ARefusedInviteIsNotUsedAgainUntilItChanges()
        {
            var watch = new InviteWatch(path, Mine);
            Put(Said());
            Assert.IsNotNull(watch.Look());
            watch.Refuse();
            Assert.IsNull(watch.Look());

            // (the editor keeps the refusal across a script reload, where the watch itself starts afresh)
            var reloaded = new InviteWatch(path, Mine) { Refused = watch.Refused };
            Assert.IsNull(reloaded.Look());

            Put(Said(), 1);
            Assert.IsNotNull(watch.Look());
            Assert.IsNotNull(reloaded.Look());
        }

        [Test]
        public void AProjectAtADriveRootIsAdvertisedAsTheRoot()
        {
            Assert.AreEqual(@"C:\", Cluewren.Editor.EditorHost.ProjectDirOf("C:/Assets"));
            Assert.AreEqual(@"C:\Games\Foo", Cluewren.Editor.EditorHost.ProjectDirOf("C:/Games/Foo/Assets"));
        }

        [Test]
        public void TheHelloMacMatchesTheKnownAnswer()
        {
            var mac = Handshake.HelloMac(Range(0), Range(32), "C:\\Users\\Dev\\Projects\\Gr\u00f6\u00dfe");
            Assert.AreEqual(KnownMac, BitConverter.ToString(mac).Replace("-", "").ToLowerInvariant());
        }
    }

    public class ReloadTests
    {
        const string Key = "Cluewren.Tests.IdBeforeReload";

        [UnityTest]
        public IEnumerator TheEditorsIdSurvivesAScriptReload()
        {
            var id = Cluewren.Editor.EditorHost.EditorId();
            Assert.IsTrue(Regex.IsMatch(id, "^[0-9a-f]{32}$"), id);
            SessionState.SetString(Key, id);
            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();
            var before = SessionState.GetString(Key, "");
            SessionState.EraseString(Key);
            Assert.AreEqual(before, Cluewren.Editor.EditorHost.EditorId());
        }

        const string AdvertKey = "Cluewren.Tests.AdvertBeforeReload";
        const string AskedKey = "Cluewren.Tests.AskedToAdvertise";

        static string Advert()
        {
            using (var self = Process.GetCurrentProcess()) return Discovery.AdvertPath(Discovery.Folder(), self.Id);
        }

        static void NoteAdvert() => SessionState.SetBool(AdvertKey, File.Exists(Advert()));

        [UnityTest]
        public IEnumerator TheAdvertIsWithdrawnForAScriptReloadAndWrittenAgainAfterIt()
        {
            // The batch-mode editor that runs these tests advertises only when asked to (EditorHost.HeadlessKey): the
            // test asks, and takes it back. An editor with its window advertises anyway, and is left as it was.
            var asked = UnityEngine.Application.isBatchMode && !SessionState.GetBool(Cluewren.Editor.EditorHost.HeadlessKey, false);
            SessionState.SetBool(AskedKey, asked);
            if (asked) SessionState.SetBool(Cluewren.Editor.EditorHost.HeadlessKey, true);
            Cluewren.Editor.EditorHost.Advertise();
            Assert.IsTrue(File.Exists(Advert()));
            SessionState.SetBool(AdvertKey, true);
            AssemblyReloadEvents.beforeAssemblyReload += NoteAdvert;
            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();
            try
            {
                Assert.IsFalse(SessionState.GetBool(AdvertKey, true), "still there when the scripts were unloaded");
                var advert = Json.ParseObject(File.ReadAllText(Advert()));
                Assert.AreEqual(Cluewren.Editor.EditorHost.EditorId(), advert["id"]);
            }
            finally
            {
                SessionState.EraseBool(AdvertKey);
                if (SessionState.GetBool(AskedKey, false))
                {
                    SessionState.EraseBool(Cluewren.Editor.EditorHost.HeadlessKey);
                    Discovery.Withdraw();
                }
                SessionState.EraseBool(AskedKey);
            }
        }
    }

    /// <summary>What an elevated administrator writes is owned by the Administrators group, which the plugin rightly
    /// refuses: a test's own invite is given to this user, the one Native.OwnedByCurrentUser compares with.</summary>
    static class TestFiles
    {
        public static void MakeMine(params string[] paths)
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)) return;
            string sid = null;
            foreach (var path in paths)
            {
                if (Native.OwnedByCurrentUser(path)) continue;
                sid = sid ?? Run("whoami.exe", "/user /fo csv /nh").Split(',').Last().Trim().Trim('"');
                var said = Run("icacls.exe", $"\"{path}\" /setowner *{sid} /q");
                if (!Native.OwnedByCurrentUser(path)) Assert.Fail($"{path} stays owned by another than this user ({sid}): {said}");
            }
        }

        public static string ShortPath(string path) => Run("cmd.exe", $"/c for %I in (\"{path}\") do @echo %~sI");

        static string Run(string tool, string arguments)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), tool), arguments)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            using (var p = Process.Start(info))
            {
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return output.Trim();
            }
        }
    }

#if CLUEWREN_INPUT_SYSTEM
    /// <summary>The bridge's devices and the editor's input passthrough come with a verified welcome, on no path before
    /// it; and what a welcome keeps from the session before. InputTestFixture stands in for the Input System, so this
    /// editor's own devices and settings are never touched.</summary>
    public class BindingInputTests : InputTestFixture
    {
        sealed class Host : IPlaytestHost
        {
            public int? Exited;
            public string HostName => "test";
            public string ProjectDir => "";
            public string ProjectId => "";
            public bool SessionRunning => false;
            public bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args) => false;
            public void Exit(int code) => Exited = code;
        }

        public override void TearDown()
        {
            Hands.RemoveDevices();
            base.TearDown();
        }

        static bool AnyOfTheBridges() => InputSystem.devices.Any(d => d.name.StartsWith("Cluewren", StringComparison.Ordinal));

        internal static byte[] Range(int from) => Enumerable.Range(from, 32).Select(i => (byte)i).ToArray();

        internal static string Welcome(string hello, string session)
        {
            var nonce = Convert.FromBase64String((string)Json.ParseObject(hello)["nonce"]);
            return Json.Write(new Dictionary<string, object>
            {
                ["type"] = "welcome", ["role"] = "service", ["session"] = session, ["brain_version"] = "0.2.0",
                ["nonce"] = Convert.ToBase64String(Range(64)),
                ["proof"] = Convert.ToBase64String(Handshake.Prove(Range(0), Handshake.BrainRole, nonce, Range(64))),
            });
        }

        [Test]
        public void TheEditorsClientPreparesNoInput()
        {
            var settings = InputSystem.settings;
            var behavior = settings.editorInputBehaviorInPlayMode;
            Cluewren.Editor.EditorHost.Ensure();
            Assert.IsFalse(AnyOfTheBridges());
            Assert.AreSame(settings, InputSystem.settings);
            Assert.AreEqual(behavior, InputSystem.settings.editorInputBehaviorInPlayMode);
        }

        [TestCase("{\"type\":\"welcome\",\"role\":\"service\",\"session\":\"s\",\"nonce\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\",\"proof\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}")]
        [TestCase("{\"type\":\"error\",\"code\":\"not_authenticated\",\"message\":\"not authenticated\"}")]
        public void AWelcomeThatFailsItsProofOrARefusalLeavesInputAlone(string frame)
        {
            var settings = InputSystem.settings;
            var behavior = settings.editorInputBehaviorInPlayMode;
            var client = new PlaytestClient(new Host()) { TestWire = _ => { } };
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                client.TestReceive(frame);
                Assert.IsFalse(client.Connected);
                Assert.IsFalse(AnyOfTheBridges());
                Assert.AreSame(settings, InputSystem.settings);
                Assert.AreEqual(behavior, InputSystem.settings.editorInputBehaviorInPlayMode);
            }
            finally
            {
                client.Shutdown();
            }
        }

        [TestCase("new", false)]
        [TestCase("old", true)]
        public void OnlyAWelcomeThatResumesKeepsWhatTheSessionBeforeLeft(string session, bool resumed)
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            var dropped = 0;
            var ticks = 0;
            client.SessionEnded += () => ++dropped;
            try
            {
                client.Resume("old", true, received: 41);
                client.Respond(40, null);
                client.AddPending(new Pending
                {
                    Id = 41, Op = "watch", Tick = (p, now) =>
                    {
                        ++ticks;
                        return false;
                    },
                });
                Assert.IsEmpty(sent, "held while there is no connection");
                client.TestOpen(Range(0));
                var hello = Json.ParseObject(sent.Single());
                Assert.AreEqual("old", hello["resume"]);
                Assert.AreEqual(41.0, hello["received"]);

                client.TestReceive(Welcome(sent.Single(), session));
                Assert.IsTrue(client.Connected);
                Assert.AreEqual(session, client.SessionId);
                var proof = Json.ParseObject(sent[1]);
                Assert.AreEqual("proof", proof["type"]);
                var nonce = Convert.FromBase64String((string)hello["nonce"]);
                Assert.AreEqual(Convert.ToBase64String(Handshake.Prove(Range(0), Handshake.PluginRole, nonce, Range(64))), proof["proof"]);

                Assert.AreEqual(resumed ? 3 : 2, sent.Count);
                if (resumed) Assert.AreEqual(40.0, Json.ParseObject(sent[2])["id"]);
                Assert.AreEqual(resumed ? 41L : 0L, client.SaveState()["received"]);
                Assert.AreEqual(resumed, client.RunActive);
                Assert.AreEqual(resumed ? 0 : 1, dropped);
                client.Tick();
                Assert.AreEqual(resumed ? 2 : 1, ticks, "what waited to answer");
            }
            finally
            {
                client.Shutdown();
            }
        }

        [TestCase(1011)]
        [TestCase(0)]
        [TestCase(1000)]
        public void AConnectionLostOrClosedEndsItsSession(int status)
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            var ended = 0;
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                client.TestReceive(Welcome(sent.Single(), "first"));
                client.TestReceive("{\"type\":\"request\",\"id\":5,\"op\":\"ping\",\"args\":{}}");
                client.SessionEnded += () => ++ended;
                client.Closed(status, "gone");
                Assert.IsFalse(client.Connected);
                Assert.IsTrue(client.WantConnected);
                Assert.AreEqual(1, ended);
                Assert.AreEqual("", client.SaveState()["session"], "what a script reload now would carry over");
                client.Respond(6, null);

                sent.Clear();
                client.TestOpen(Range(0));
                var hello = Json.ParseObject(sent.Single());
                Assert.IsFalse(hello.ContainsKey("resume"));
                Assert.IsFalse(hello.ContainsKey("received"));
                client.TestReceive(Welcome(sent.Single(), "second"));
                Assert.AreEqual(2, sent.Count, "the hello and the proof, and no answer of the session before");
            }
            finally
            {
                client.Shutdown();
            }
        }

        [Test]
        public void TheEditorsOwnCloseForAReloadCarriesTheSessionOver()
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            var ended = 0;
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                client.TestReceive(Welcome(sent.Single(), "first"));
                client.TestReceive("{\"type\":\"request\",\"id\":5,\"op\":\"ping\",\"args\":{}}");
                client.SessionEnded += () => ++ended;
                var state = client.SaveState();
                client.CloseForReload();
                Assert.AreEqual(0, ended);

                // (the scripts reloaded, the editor's host makes its client anew from what was saved)
                sent.Clear();
                var reloaded = new PlaytestClient(new Host()) { TestWire = sent.Add };
                try
                {
                    reloaded.Resume((string)state["session"], (bool)state["run_active"], received: (long)state["received"]);
                    reloaded.TestOpen(Range(0));
                    var hello = Json.ParseObject(sent.Single());
                    Assert.AreEqual("first", hello["resume"]);
                    Assert.AreEqual(5.0, hello["received"]);
                }
                finally
                {
                    reloaded.Shutdown();
                }
            }
            finally
            {
                client.Shutdown();
            }
        }

        [TestCase(1000)]
        [TestCase(1008)]
        public void WhatWaitsInASessionIsToldWhenItEnds(int how)
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            var ended = 0;
            client.SessionEnded += () => ++ended;
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                if (how != 1008) client.TestReceive(Welcome(sent.Single(), "first"));
                ended = 0;
                client.Closed(how, "project");
                Assert.AreEqual(1, ended);
            }
            finally
            {
                client.Shutdown();
            }
        }

        [Test]
        public void ACloseBeforeTheWelcomeThatIsNoRefusalIsTriedAgain()
        {
            var host = new Host();
            var client = new PlaytestClient(host) { TestWire = _ => { }, ExitOnFailure = true };
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                client.Closed(1011, "project");
                Assert.IsTrue(client.WantConnected);
                Assert.AreEqual("", client.Problem);
                Assert.IsNull(host.Exited);
            }
            finally
            {
                client.Shutdown();
            }
        }

        [Test]
        public void WhatASessionLeftUnfinishedAnswersNothingInTheNext()
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                client.TestReceive(Welcome(sent.Single(), "first"));
                var first = client.SessionSerial;
                client.InSession(first, () => client.Respond(8, null));
                MainThread.Run();
                Assert.AreEqual(8.0, Json.ParseObject(sent.Last())["id"], "the session it was started in");

                client.InSession(first, () => client.Respond(9, null));
                client.Closed(1011, "gone");
                sent.Clear();
                client.TestOpen(Range(0));
                client.TestReceive(Welcome(sent.Single(), "second"));
                MainThread.Run();
                Assert.AreEqual(2, sent.Count, "the hello and the proof, and no answer of the session before");
            }
            finally
            {
                client.Shutdown();
            }
        }

        [Test]
        public void AFirstWelcomeStartsASessionWithNothingKept()
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            try
            {
                client.Connect();
                client.Respond(7, null);
                client.TestOpen(Range(0));
                Assert.IsFalse(Json.ParseObject(sent.Single()).ContainsKey("resume"));
                client.TestReceive(Welcome(sent.Single(), "first"));
                Assert.IsTrue(client.Connected);
                Assert.AreEqual(2, sent.Count, "the hello and the proof, and no answer held from before");
                client.TestReceive("{\"type\":\"run_started\"}");
                Assert.IsTrue(client.RunActive);
                client.TestReceive("{\"type\":\"result\"}");
                Assert.IsFalse(client.RunActive);
            }
            finally
            {
                client.Shutdown();
            }
        }
    }

    /// <summary>A fresh restart leaves play mode and enters it again once it is left: the second step is taken only
    /// in the session that asked, and only while the editor is connected.</summary>
    public class SessionWaitTests
    {
        static PlaytestClient AskedForAFreshRestart()
        {
            var sent = new List<string>();
            var client = Cluewren.Editor.EditorHost.Ensure();
            client.TestWire = sent.Add;
            client.Connect();
            client.TestOpen(BindingInputTests.Range(0));
            client.TestReceive(BindingInputTests.Welcome(sent.Single(), "first"));
            Assert.IsTrue(client.Connected);
            Assert.IsTrue(Cluewren.Editor.EditorHost.Instance.Session(client, 7, "restart", new Dictionary<string, object> { ["fresh"] = true }));
            return client;
        }

        [UnityTest]
        public IEnumerator AFreshRestartIsNotGoneOnWithOnceItsSessionHasEnded()
        {
            yield return new EnterPlayMode();
            var client = AskedForAFreshRestart();
            client.Closed(1011, "gone");
            for (var i = 0; i < 600 && EditorApplication.isPlaying; ++i) yield return null;
            for (var i = 0; i < 10; ++i) yield return null;
            var again = EditorApplication.isPlayingOrWillChangePlaymode;
            client.Disconnect();
            Hands.RemoveDevices();
            if (again) yield return new ExitPlayMode();
            Assert.IsFalse(again, "into play mode again for a session that had ended");
        }

        [UnityTest]
        public IEnumerator AFreshRestartWaitsWhileThereIsNoConnection()
        {
            yield return new EnterPlayMode();
            var client = AskedForAFreshRestart();
            // (as after a script reload, until the welcome that resumes the session)
            client.TestConnected(false);
            for (var i = 0; i < 600 && EditorApplication.isPlaying; ++i) yield return null;
            for (var i = 0; i < 10; ++i) yield return null;
            var again = EditorApplication.isPlayingOrWillChangePlaymode;
            client.Disconnect();
            Hands.RemoveDevices();
            if (again) yield return new ExitPlayMode();
            Assert.IsFalse(again, "into play mode again before the session was known to go on");
        }
    }
#endif
}
