// Modified by Crane Valley: the settings test also holds in a project with no input settings asset.
#if AUTOPILOT_INPUT_SYSTEM
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;

namespace Autopilot.Playtest.Tests
{
    /// <summary>
    /// A run's input is the bridge's alone (Hands.IsolateDevices): the person's own devices are disabled for it, a
    /// PlayerInput paired with them takes the bridge's, the Input System's settings send all input to the game, and all
    /// of it is put back after. InputTestFixture stands in for the Input System's runtime, so the "person's" devices are
    /// native ones it reports, and this editor's own devices and settings are never touched.
    /// </summary>
    public class IsolationTests : InputTestFixture
    {
        public override void TearDown()
        {
            Hands.IsolateDevices(false);
            Hands.RemoveDevices();
            base.TearDown();
        }

        static InputDevice[] Persons() => InputSystem.devices.Where(d => d.native).ToArray();

        // A device as the person's hardware would come: reported by the fixture's runtime, so native (the runtime is
        // internal to the Input System's test framework)
        void Report<T>() where T : InputDevice
        {
            var runtime = typeof(InputTestFixture).GetProperty("runtime", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(this);
            runtime.GetType().GetMethod("ReportNewInputDevice", new[] { typeof(string), typeof(int) }).Invoke(runtime,
                new object[] { new InputDeviceDescription { deviceClass = typeof(T).Name, interfaceName = "Test" }.ToJson(), InputDevice.InvalidDeviceId });
            InputSystem.Update();
        }

        [Test]
        public void ARunDisablesThePersonsDevicesAndPutsThemBack()
        {
            Report<Keyboard>();
            Report<Mouse>();
            Report<Gamepad>();
            var persons = Persons();
            Assert.That(persons.Length, Is.EqualTo(3));
            var onScreen = InputSystem.AddDevice<Gamepad>("OnScreenStick");  // (a game's own virtual device)
            Hands.EnsureDevices();

            Hands.IsolateDevices(true);
            Assert.That(persons.All(d => !d.enabled), Is.True);
            Assert.That(onScreen.enabled, Is.True);
            var bridges = InputSystem.devices.Where(d => d.name.StartsWith("AutopilotPlaytest")).ToArray();
            Assert.That(bridges.Length, Is.EqualTo(3));
            Assert.That(bridges.All(d => d.enabled), Is.True);

            Hands.IsolateDevices(false);
            Assert.That(persons.All(d => d.enabled), Is.True);
            Assert.That(Hands.Isolating, Is.False);
        }

        [Test]
        public void ADeviceConnectedDuringARunIsDisabledToo()
        {
            Hands.EnsureDevices();
            Hands.IsolateDevices(true);
            Report<Gamepad>();
            Hands.IsolateDevices(true);  // (the next frame)
            var pad = Persons().Single();
            Assert.That(pad.enabled, Is.False);

            Hands.IsolateDevices(false);
            Assert.That(pad.enabled, Is.True);
        }

        [Test]
        public void ARunSendsAllInputToTheGameAndPutsTheSettingsBack()
        {
            var before = InputSystem.settings;
            var behavior = before.editorInputBehaviorInPlayMode;
            var background = before.backgroundBehavior;

            Hands.IsolateDevices(true);
            Assert.That(InputSystem.settings, Is.Not.SameAs(before));
            Assert.That(InputSystem.settings.editorInputBehaviorInPlayMode,
                Is.EqualTo(InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView));
            Assert.That(InputSystem.settings.backgroundBehavior, Is.EqualTo(InputSettings.BackgroundBehavior.IgnoreFocus));
            // (the copy is the run's: the settings it was made from are as they were)
            Assert.That(before.editorInputBehaviorInPlayMode, Is.EqualTo(behavior));
            Assert.That(before.backgroundBehavior, Is.EqualTo(background));

            Hands.IsolateDevices(false);
            // The Input System destroys in-memory settings it replaces, so only an asset comes back as itself.
            if (before != null) Assert.That(InputSystem.settings, Is.SameAs(before));
            Assert.That(InputSystem.settings.editorInputBehaviorInPlayMode, Is.EqualTo(behavior));
            Assert.That(InputSystem.settings.backgroundBehavior, Is.EqualTo(background));
        }

        static void Call(object target, string method) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

        [Test]
        public void APlayerInputPairedWithThePersonsDevicesTakesTheBridgesAndGetsThemBack()
        {
            Report<Keyboard>();
            Report<Mouse>();
            var personsKeyboard = Persons().OfType<Keyboard>().Single();

            var actions = ScriptableObject.CreateInstance<InputActionAsset>();
            actions.AddControlScheme("KeyboardMouse").WithRequiredDevice("<Keyboard>").WithRequiredDevice("<Mouse>");
            actions.AddActionMap("Player").AddAction("Jump", binding: "<Keyboard>/space", groups: "KeyboardMouse");
            var go = new GameObject("Player");
            go.SetActive(false);
            var player = go.AddComponent<PlayerInput>();
            player.actions = actions;
            player.defaultControlScheme = "KeyboardMouse";
            player.neverAutoSwitchControlSchemes = true;  // (a game that never follows the device used)
            go.SetActive(true);
            // (edit mode calls no OnEnable: called as play mode would, so the PlayerInput pairs its devices)
            Call(player, "OnEnable");
            try
            {
                Assert.That(player.devices.Contains(personsKeyboard), Is.True);
                Hands.EnsureDevices();

                Hands.IsolateDevices(true);
                Assert.That(player.currentControlScheme, Is.EqualTo("KeyboardMouse"));
                Assert.That(player.devices.Any(d => d.name.StartsWith("AutopilotPlaytestKeyboard")), Is.True);
                Assert.That(player.devices.Contains(personsKeyboard), Is.False);

                Hands.IsolateDevices(false);
                Assert.That(player.devices.Contains(personsKeyboard), Is.True);
            }
            finally
            {
                Call(player, "OnDisable");
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(actions);
            }
        }

        [Test]
        public void OnlyARunInPlayIsolates()
        {
            Report<Keyboard>();
            // (edit mode: Tick never isolates, run or not)
            Hands.Tick(true, true);
            Assert.That(Hands.Isolating, Is.False);
            Assert.That(Persons().Single().enabled, Is.True);
            Assert.That(LegacyInput.Isolated, Is.False);
            Hands.Tick(false);
        }
    }
}
#endif
