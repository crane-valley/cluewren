using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Cluewren.Tests
{
    public class ConnectionTests
    {
        sealed class Host : IPlaytestHost
        {
            public int? Exited;
            public string HostName => "test";
            public string ProjectDir => @"C:\Games\Foo";
            public string ProjectId => "";
            public bool SessionRunning => false;
            public bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args) => false;
            public void Exit(int code) => Exited = code;
        }

        static byte[] Range(int from) => Enumerable.Range(from, 32).Select(i => (byte)i).ToArray();

        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        [Test]
        public void ProofsMatchAnIndependentHmac()
        {
            var proof = Handshake.Prove(Range(0), Handshake.BrainRole, Range(32), Range(64));
            Assert.AreEqual("fe40e5b282410056b29732fdd44c4c792ace5ada314518f812083d05792c4dd1", Hex(proof));
            Assert.AreEqual("4274a0c9533a757fd4f277c3e81a4dabdd9a3978d975d827126341d1b927d826",
                Hex(Handshake.Prove(Range(0), Handshake.PluginRole, Range(32), Range(64))));
        }

        [Test]
        public void AReflectedOrTamperedProofIsRefused()
        {
            var brain = Handshake.Prove(Range(0), Handshake.BrainRole, Range(32), Range(64));
            Assert.IsTrue(Handshake.Verify(Range(0), Handshake.BrainRole, Range(32), Range(64), brain));
            Assert.IsFalse(Handshake.Verify(Range(0), Handshake.PluginRole, Range(32), Range(64), brain));
            brain[0] ^= 1;
            Assert.IsFalse(Handshake.Verify(Range(0), Handshake.BrainRole, Range(32), Range(64), brain));
        }

        [TestCase("ws://127.0.0.1:8765/v1/bridge")]
        [TestCase("ws://[::1]:8765/v1/bridge")]
        public void LoopbackIsAccepted(string url)
        {
            Assert.IsNull(Discovery.CheckUrl(url, out var uri));
            Assert.IsNotNull(uri);
        }

        [TestCase("wss://127.0.0.1:1/")]
        [TestCase("http://127.0.0.1:1/")]
        [TestCase("ws://localhost:1/")]
        [TestCase("ws://10.0.0.1:1/")]
        [TestCase("ws://example.invalid:1/")]
        [TestCase("ws://127.0.0.1.example.invalid:1/")]
        [TestCase("ws://user@127.0.0.1:1/")]
        [TestCase("ws://user:pass@[::1]:1/")]
        [TestCase("ws://127.0.0.1@example.invalid:1/")]
        [TestCase("not a url")]
        [TestCase("")]
        public void AnythingElseIsRefused(string url)
        {
            Assert.IsNotNull(Discovery.CheckUrl(url, out var uri));
            Assert.IsNull(uri);
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void AFileOfAnotherOwnerIsNotTrusted()
        {
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            Assert.IsFalse(Native.OwnedByCurrentUser(system));
            var mine = Path.GetTempFileName();
            TestFiles.MakeMine(mine);
            try
            {
                Assert.IsTrue(Native.OwnedByCurrentUser(mine));
            }
            finally
            {
                File.Delete(mine);
            }
        }
#endif

        [Test]
        public void TheHelloSpeaksProtocol2AndBindsItsProjectFolderToTheSecret()
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                var hello = Json.ParseObject(sent.Single());
                Assert.AreEqual("hello", hello["type"]);
                Assert.AreEqual(2.0, hello["protocol"]);
                Assert.AreEqual("0.2.0", hello["plugin_version"]);
                Assert.AreEqual("unity", hello["engine"]);
                Assert.AreEqual("test", hello["host"]);
                Assert.AreEqual(@"C:\Games\Foo", hello["project_dir"]);
                CollectionAssert.IsSubsetOf(new[] { "timed_hold", "pointer", "levels" }, (List<object>)hello["features"]);
                var nonce = Convert.FromBase64String((string)hello["nonce"]);
                Assert.AreEqual(Handshake.NonceBytes, nonce.Length);
                Assert.AreEqual(Convert.ToBase64String(Handshake.HelloMac(Range(0), nonce, @"C:\Games\Foo")), hello["mac"]);
                Assert.IsFalse(hello.ContainsKey("resume"));
                Assert.IsFalse(hello.ContainsKey("received"));
            }
            finally
            {
                client.Shutdown();
            }
        }

        [TestCase("protocol", "The cluewren command and this plugin are of different releases: install the same release of both")]
        [TestCase("not_authenticated", "The brain did not accept this editor's proof of the invite")]
        [TestCase("project", "The run is for another project than this editor's")]
        [TestCase("resume", "The session this editor tried to resume belongs to another project or engine")]
        [TestCase("hello", "The brain refused the connection (hello)")]
        [TestCase("proof", "The brain refused the connection (proof)")]
        [TestCase("something_new", "The brain refused the connection (something_new)")]
        public void ARefusalIsShownByItsCodeAndNeverInTheBrainsWords(string code, string text)
        {
            var sent = new List<string>();
            var host = new Host();
            var client = new PlaytestClient(host) { TestWire = sent.Add, ExitOnFailure = true };
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                sent.Clear();
                client.TestReceive(Json.Write(new Dictionary<string, object> { ["type"] = "error", ["code"] = code, ["message"] = "the brain's own words" }));
                Assert.AreEqual("Refused: " + text, client.Problem);
                Assert.AreEqual(client.Problem, client.LastStatus);
                Assert.IsFalse(client.Connected);
                Assert.IsFalse(client.WantConnected);
                Assert.AreEqual(1, host.Exited);
                Assert.IsEmpty(sent);
            }
            finally
            {
                client.Shutdown();
            }
        }

        [TestCase("{\"type\":\"request\",\"id\":1,\"op\":\"ping\",\"args\":{}}")]
        [TestCase("{\"type\":\"welcome\",\"role\":\"service\",\"session\":\"s\",\"nonce\":\"AAAA\",\"proof\":\"AAAA\"}")]
        [TestCase("{\"type\":\"welcome\"}")]
        [TestCase("{\"type\":\"error\",\"message\":\"no code\"}")]
        [TestCase("{\"type\":\"error\",\"code\":4403,\"message\":\"a number\"}")]
        [TestCase("{\"type\":\"error\",\"code\":\"Not a plain word\",\"message\":\"shown nowhere\"}")]
        [TestCase("{\"type\":\"error\",\"code\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"message\":\"33 characters\"}")]
        [TestCase("not json")]
        public void NothingIsActedOnBeforeTheProofAndARefusalIsFinal(string frame)
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                sent.Clear();
                client.TestReceive(frame);
                Assert.IsFalse(client.Connected);
                Assert.IsFalse(client.WantConnected);
                StringAssert.EndsWith("it didn't prove it is this machine's brain", client.Problem);
                Assert.IsEmpty(sent);
            }
            finally
            {
                client.Shutdown();
            }
        }

        const string Editor = "0123456789abcdef0123456789abcdef";

        static void Invite(string path, int age)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            int self;
            using (var process = Process.GetCurrentProcess()) self = process.Id;
            File.WriteAllText(path, Json.Write(new Dictionary<string, object>
            {
                ["url"] = "ws://127.0.0.1:1/v1/bridge", ["secret"] = Convert.ToBase64String(Range(0)), ["pid"] = self,
                ["protocol"] = 2, ["editor_id"] = Editor,
            }));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(age));
            TestFiles.MakeMine(Path.GetDirectoryName(path), path);
        }

        [Test]
        public void ARefusedInviteIsLeftAloneUntilItChanges()
        {
            var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var path = Discovery.InvitePath(folder, 4321);
            var watch = new InviteWatch(path, Editor);
            var client = new PlaytestClient(new Host()) { TestWire = _ => { }, TestInvites = watch };
            try
            {
                Invite(path, 0);
                Assert.IsNotNull(watch.Look());
                client.Connect();
                client.TestOpen(Range(0));
                client.TestReceive("{\"type\":\"error\",\"code\":\"project\",\"message\":\"\"}");
                Assert.IsNull(watch.Look());
                Invite(path, 1);
                Assert.IsNotNull(watch.Look());
            }
            finally
            {
                client.Shutdown();
                Directory.Delete(folder, true);
            }
        }

        [TestCase("project", "Refused: The run is for another project than this editor's")]
        [TestCase("protocol", "Refused: The cluewren command and this plugin are of different releases: install the same release of both")]
        [TestCase("busy_2", "Refused: The brain refused the connection (busy_2)")]
        [TestCase("Not a plain word", null)]
        [TestCase("", null)]
        public void APolicyCloseBeforeTheWelcomeIsTheRefusalItsReasonNames(string reason, string text)
        {
            var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var path = Discovery.InvitePath(folder, 4321);
            var watch = new InviteWatch(path, Editor);
            var sent = new List<string>();
            var host = new Host();
            var client = new PlaytestClient(host) { TestWire = sent.Add, TestInvites = watch, ExitOnFailure = true };
            try
            {
                Invite(path, 0);
                Assert.IsNotNull(watch.Look());
                client.Connect();
                client.TestOpen(Range(0));
                sent.Clear();
                client.Closed(1008, reason);
                if (text != null) Assert.AreEqual(text, client.Problem);
                else StringAssert.EndsWith("it didn't prove it is this machine's brain", client.Problem);
                Assert.AreEqual(client.Problem, client.LastStatus);
                Assert.IsFalse(client.WantConnected);
                Assert.AreEqual(1, host.Exited);
                Assert.IsNull(watch.Look(), "the invite it refused");
                Assert.IsEmpty(sent);
            }
            finally
            {
                client.Shutdown();
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void JsonNestedTooDeepIsUnreadable()
        {
            Assert.IsNotNull(Json.Parse(new string('[', 64) + new string(']', 64)));
            Assert.Throws<FormatException>(() => Json.Parse(new string('[', 65) + new string(']', 65)));
            var deep = string.Concat(Enumerable.Repeat("{\"a\":", 100000)) + "1" + new string('}', 100000);
            Assert.Throws<FormatException>(() => Json.Parse(deep));

            var client = new PlaytestClient(new Host()) { TestWire = _ => { } };
            try
            {
                client.Connect();
                client.TestOpen(Range(0));
                client.TestReceive(deep);
                Assert.IsFalse(client.WantConnected);
                StringAssert.EndsWith("it didn't prove it is this machine's brain", client.Problem);
            }
            finally
            {
                client.Shutdown();
            }
        }
    }
}
