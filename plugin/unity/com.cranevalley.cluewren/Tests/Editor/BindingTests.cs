// Which brain an editor takes (docs/adr/0015), with the same vectors and known answer as the Godot and Unreal tests.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
#if CLUEWREN_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Cluewren.Tests
{
    public class BindingTests
    {
        const string Absent = "absent";

        static readonly object[][] Vectors =
        {
            new object[] { "1", "related", new[] { @"C:\Games\Foo" }, @"C:\Games\Foo", true },
            new object[] { "2", "related", new[] { @"C:\Games\Foo" }, @"C:\Games\Foo\", true },
            new object[] { "3", "related", new[] { @"C:\Games\Foo" }, "c:/games/foo", true },
            new object[] { "4", "related", new[] { @"C:\Games" }, @"C:\Games\Foo", true },
            new object[] { "5", "related", new[] { @"C:\Games\Foo\Assets\Scripts" }, @"C:\Games\Foo", true },
            new object[] { "6", "related", new[] { @"C:\Games\Foo" }, @"C:\Games\FooBar", false },
            new object[] { "7", "related", new[] { @"C:\Games\FooBar" }, @"C:\Games\Foo", false },
            new object[] { "8", "related", new[] { @"C:\" }, @"C:\Games\Foo", true },
            new object[] { "9", "related", new[] { @"C:\Games\Foo" }, @"D:\Games\Foo", false },
            new object[] { "10", "exact", new[] { @"C:\Games" }, @"C:\Games\Foo", false },
            new object[] { "11", "exact", new[] { @"C:\Games\Foo" }, @"C:\Games\Foo\", true },
            new object[] { "12", "exact", new[] { @"C:\Games\Foo\Assets" }, @"C:\Games\Foo", false },
            new object[] { "13", "related", new[] { @"C:\Link\Foo", @"C:\Games\Foo" }, @"C:\Games\Foo", true },
            new object[] { "14", "related", new[] { @"C:\Games\Foo" }, @"S:\Foo", false },
            new object[] { "15", "related", new[] { @"C:\Games\LongName" }, @"C:\Games\LONGNA~1", false },
            new object[] { "16", "exact", new[] { @"S:\Foo", @"C:\Games\Foo" }, @"S:\Foo", true },
            new object[] { "17 absent", "related", Absent, @"C:\Games\Foo", true },
            new object[] { "17 empty", "related", new string[0], @"C:\Games\Foo", true },
            new object[] { "18 empty", "related", new[] { @"C:\Games\Foo" }, "", false },
            new object[] { "18 relative", "related", new[] { @"C:\Games\Foo" }, @"Games\Foo", false },
            new object[] { "19", "nearby", new[] { @"C:\Games\Foo" }, @"C:\Games\Foo", false },
        };

        // On macOS and Linux (unsupported): rooted POSIX paths, no case folding.
        static readonly object[][] PosixVectors =
        {
            new object[] { "P1", "related", new[] { "/home/u/game" }, "/home/u/game", true },
            new object[] { "P2", "related", new[] { "/home/u" }, "/home/u/game/", true },
            new object[] { "P3", "related", new[] { "/home/u/game/Assets" }, "/home/u/game", true },
            new object[] { "P4", "related", new[] { "/home/u/game" }, "/home/u/gamebar", false },
            new object[] { "P5", "related", new[] { "/home/u/Game" }, "/home/u/game", false },
            new object[] { "P6", "exact", new[] { "/" }, "/home/u/game", false },
            new object[] { "P7", "related", new[] { "/" }, "/home/u/game", true },
            new object[] { "P8", "related", new[] { "/home/u/game" }, @"C:\home\u\game", false },
        };

        const string KnownMac = "b0e5b16410e69ae2e7ab99cb08045c3b0f6ac8cf250e9898d151a05c964a31a3";

        static Dictionary<string, object> Said(string match, object folders)
        {
            var said = new Dictionary<string, object> { ["match"] = match };
            if (folders is string[] list) said["folders"] = list.Cast<object>().ToList();
            return said;
        }

        [TestCaseSource(nameof(Vectors))]
        public void Vector(string vector, string match, object folders, string project, bool taken)
        {
            var why = Discovery.ForAnotherProject(Said(match, folders), project, true);
            Assert.AreEqual(taken, why == null, $"vector {vector}: {why}");
        }

        [TestCaseSource(nameof(PosixVectors))]
        public void PosixVector(string vector, string match, object folders, string project, bool taken)
        {
            var why = Discovery.ForAnotherProject(Said(match, folders), project, false);
            Assert.AreEqual(taken, why == null, $"vector {vector}: {why}");
        }

        [Test]
        public void APosixPathNeverMatchesOnWindows()
        {
            Assert.IsNotNull(Discovery.ForAnotherProject(Said("related", new[] { "/home/u/game" }), "/home/u/game", true));
        }

        [Test]
        public void TheVerdictNamesBothFolders()
        {
            Assert.AreEqual("For another project: the run is for \"C:\\Link\\Foo\" or \"C:\\Games\\Foo\" (match \"related\"), "
                            + "this project is \"C:\\Games\\Bar\"",
                Discovery.ForAnotherProject(Said("related", new[] { @"C:\Link\Foo", @"C:\Games\Foo" }), @"C:\Games\Bar", true));
        }

        static byte[] Range(int from) => Enumerable.Range(from, 32).Select(i => (byte)i).ToArray();

        [Test]
        public void TheHelloMacMatchesTheKnownAnswer()
        {
            var mac = Handshake.HelloMac(Range(0), Range(32), "C:\\Users\\Dev\\Projects\\Gr\u00f6\u00dfe");
            Assert.AreEqual(KnownMac, BitConverter.ToString(mac).Replace("-", "").ToLowerInvariant());
        }

        [Test]
        public void ABrainForAnotherProjectGivesNoAddress()
        {
            var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "brain.json");
                var other = dir + "-other";
                File.WriteAllText(path, Json.Write(new Dictionary<string, object>
                {
                    ["url"] = "ws://127.0.0.1:1/v1/bridge", ["secret"] = Convert.ToBase64String(Range(0)),
                    ["pid"] = Process.GetCurrentProcess().Id, ["folders"] = new List<object> { other }, ["match"] = "related",
                }));
                var why = Discovery.Read(path, dir, out var brain);
                Assert.IsNull(brain);
                StringAssert.Contains(other, why);
                StringAssert.Contains(dir, why);
                Assert.IsNull(Discovery.Read(path, other, out brain));
                Assert.IsNotNull(brain);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }

#if CLUEWREN_INPUT_SYSTEM
    /// <summary>The bridge's devices and the editor's input passthrough come with a verified welcome, on no path before
    /// it. InputTestFixture stands in for the Input System, so this editor's own devices and settings are never touched.</summary>
    public class BindingInputTests : InputTestFixture
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

        public override void TearDown()
        {
            Hands.RemoveDevices();
            base.TearDown();
        }

        static bool AnyOfTheBridges() => InputSystem.devices.Any(d => d.name.StartsWith("Cluewren", StringComparison.Ordinal));

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
        [TestCase("{\"type\":\"error\",\"code\":4403,\"message\":\"not authenticated\"}")]
        public void AWelcomeThatFailsItsProofOrARefusalLeavesInputAlone(string frame)
        {
            var settings = InputSystem.settings;
            var behavior = settings.editorInputBehaviorInPlayMode;
            var client = new PlaytestClient(new Host()) { TestWire = _ => { } };
            try
            {
                client.Connect();
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
    }
#endif
}
