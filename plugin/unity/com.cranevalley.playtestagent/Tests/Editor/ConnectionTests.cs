using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Autopilot.Playtest.Tests
{
    public class ConnectionTests
    {
        sealed class Host : IPlaytestHost
        {
            public string HostName => "test";
            public string ProjectDir => "";
            public string ProjectId => "";
            public bool SessionRunning => false;
            public bool Session(PlaytestClient client, long id, string action, Dictionary<string, object> args) => false;
            public void Exit(int code) { }
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

        static string WriteBrain(string dir, int pid, string secret)
        {
            var path = Path.Combine(dir, "brain.json");
            File.WriteAllText(path, Json.Write(new Dictionary<string, object>
            {
                ["url"] = "ws://127.0.0.1:1/v1/bridge", ["secret"] = secret, ["pid"] = pid, ["started"] = "2026-10-31T00:00:00+00:00",
            }));
            return path;
        }

        [Test]
        public void BrainJsonIsReadOnlyWhenItsBrainRuns()
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                var secret = Convert.ToBase64String(Range(0));
                Assert.IsNull(Discovery.Read(WriteBrain(dir, Process.GetCurrentProcess().Id, secret), out var brain));
                Assert.AreEqual(Range(0), brain.Secret);
                Assert.IsNotNull(Discovery.Read(WriteBrain(dir, 0, secret), out _));
                Assert.IsNotNull(Discovery.Read(WriteBrain(dir, Process.GetCurrentProcess().Id, "short"), out _));
                Assert.IsNotNull(Discovery.Read(Path.Combine(dir, "missing.json"), out _));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void AFileOfAnotherOwnerIsNotTrusted()
        {
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            Assert.IsFalse(Native.OwnedByCurrentUser(system));
            var mine = Path.GetTempFileName();
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

        [TestCase("{\"type\":\"request\",\"id\":1,\"op\":\"ping\",\"args\":{}}")]
        [TestCase("{\"type\":\"welcome\",\"role\":\"service\",\"session\":\"s\",\"nonce\":\"AAAA\",\"proof\":\"AAAA\"}")]
        [TestCase("{\"type\":\"welcome\"}")]
        [TestCase("not json")]
        public void NothingIsActedOnBeforeTheProofAndARefusalIsFinal(string frame)
        {
            var sent = new List<string>();
            var client = new PlaytestClient(new Host()) { TestWire = sent.Add };
            try
            {
                client.Connect();
                client.TestReceive(frame);
                Assert.IsFalse(client.Connected);
                Assert.IsFalse(client.WantConnected);
                Assert.IsNotEmpty(client.Problem);
                Assert.IsEmpty(sent);
            }
            finally
            {
                client.Shutdown();
            }
        }
    }
}
