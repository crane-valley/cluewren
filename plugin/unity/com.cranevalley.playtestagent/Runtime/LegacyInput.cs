// The legacy Input Manager (UnityEngine.Input) with the bridge's hands in it. The package's IL post-processor
// (CodeGen/LegacyInputRewriter.cs) points the game's own calls to UnityEngine.Input's key, button, axis and mouse reads
// here: each public member below has the name and signature of the UnityEngine.Input member it stands for.
//
// No brain connected (Active false): each is UnityEngine.Input's own, so the game behaves as it always did, exceptions
// and all. Connected: what a person does (when the Input Manager is on) together with what the bridge presses (Hands),
// except while a run is going in play (Isolated), when the bridge's input alone counts. As the Input Manager would
// report it: keys and mouse buttons by KeyCode, the gamepad as joystick 1 (an Xbox
// pad's buttons and axes on Windows), the mouse at the bridge's pointer, and axes and buttons by the project's
// InputManager axes (the editor reads ProjectSettings/InputManager.asset, a build carries them in PlaytestBuildInfo).
// With the Input Manager off (Active Input Handling: Input System), UnityEngine.Input throws: during a run the game
// then reads only the bridge's input.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Autopilot.Playtest
{
    /// <summary>Put on each assembly the IL post-processor rewrote: how many of its calls to UnityEngine.Input come here.</summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class LegacyInputRewrittenAttribute : Attribute
    {
        public readonly int Calls;

        public LegacyInputRewrittenAttribute(int calls)
        {
            Calls = calls;
        }
    }

    public static class LegacyInput
    {
        /// <summary>A brain is connected: the bridge's input counts (Hands.Tick sets it every frame).</summary>
        public static bool Active;

        /// <summary>A run is going in play: the bridge's input alone counts, not the person's (Hands.Tick sets it every frame).</summary>
        public static bool Isolated;

        // (UnityEngine.Input throws when the Input Manager is off)
        static bool Real => Hands.LegacyInputEnabled && !Isolated;
        static int Frame => Time.frameCount;

        // -- what the bridge holds -------------------------------------------------------------------------

        static readonly HashSet<KeyCode> down = new HashSet<KeyCode>();
        static readonly Dictionary<KeyCode, int> downAt = new Dictionary<KeyCode, int>();
        static readonly Dictionary<KeyCode, int> upAt = new Dictionary<KeyCode, int>();
        static readonly float[] padAxes = new float[10];  // joystick 1's axes 1 to 10 (index 0: the X axis)
        static Vector2 pointer;
        static bool pointerSet;
        static Vector2 delta;
        static float scroll;
        static int movedAt = -1;  // the frame delta and scroll are for
        static readonly StringBuilder typed = new StringBuilder();
        static int typedAt = -1;

        /// <summary>A key, mouse button (KeyCode.Mouse0...) or joystick button goes down or up now.</summary>
        public static void Key(KeyCode code, bool pressed)
        {
            if (code == KeyCode.None) return;
            if (pressed)
            {
                if (!down.Add(code)) return;
                downAt[code] = Frame;
                var c = CharOf(code);
                if (c != '\0')
                {
                    if (typedAt != Frame) typed.Clear();
                    typedAt = Frame;
                    typed.Append(c);
                }
            }
            else if (down.Remove(code))
            {
                upAt[code] = Frame;
            }
        }

        /// <summary>A gamepad button (0 A, 1 B, 2 X, 3 Y, 4 LB, 5 RB, 6 Back, 7 Start, 8 left stick, 9 right stick):
        /// joystick 1's, which is also any joystick's.</summary>
        public static void PadButton(int button, bool pressed)
        {
            if (button < 0 || button > 19) return;
            Key(KeyCode.JoystickButton0 + button, pressed);
            Key(KeyCode.Joystick1Button0 + button, pressed);
        }

        /// <summary>Joystick 1's axis (0-based: 0 X, 1 Y, 2 the 3rd...) at a value.</summary>
        public static void PadAxis(int index, float value)
        {
            if (index >= 0 && index < padAxes.Length) padAxes[index] = value;
        }

        /// <summary>The mouse is here now (pixels from the bottom left, as Input.mousePosition), without moving to it.</summary>
        public static void Pointer(Vector2 pixels)
        {
            pointer = pixels;
            pointerSet = true;
        }

        /// <summary>Where the bridge's pointer is: where it was put, else the middle of the screen.</summary>
        public static Vector2 PointerNow => pointerSet ? pointer : new Vector2(Screen.width / 2f, Screen.height / 2f);

        /// <summary>The mouse moves by this much this frame (Mouse X / Mouse Y axes).</summary>
        public static void MouseDelta(Vector2 d)
        {
            Moved();
            delta += d;
        }

        /// <summary>The wheel turns this many notches this frame.</summary>
        public static void Scroll(float notches)
        {
            Moved();
            scroll += notches;
        }

        static void Moved()
        {
            if (movedAt == Frame) return;
            movedAt = Frame;
            delta = Vector2.zero;
            scroll = 0;
        }

        static Vector2 DeltaNow => movedAt == Frame ? delta : Vector2.zero;
        static float ScrollNow => movedAt == Frame ? scroll : 0f;

        /// <summary>Everything let go and the pointer given back (the brain went away).</summary>
        public static void Clear()
        {
            foreach (var code in down.ToList()) Key(code, false);
            Array.Clear(padAxes, 0, padAxes.Length);
            pointerSet = false;
            movedAt = -1;
            foreach (var list in axes.Values)
                foreach (var a in list)
                    a.Value = 0;
        }

        static bool Held(KeyCode code) => down.Contains(code);
        static bool DownNow(KeyCode code) => downAt.TryGetValue(code, out var f) && f == Frame;
        static bool UpNow(KeyCode code) => upAt.TryGetValue(code, out var f) && f == Frame;

        // (a method group makes a new delegate each time it's converted, and the game reads buttons every frame)
        static readonly Func<KeyCode, bool> isHeld = Held, isDownNow = DownNow, isUpNow = UpNow;

        // -- UnityEngine.Input's reads ---------------------------------------------------------------------

        public static bool GetKey(KeyCode key) => !Active ? Input.GetKey(key) : Real && Input.GetKey(key) || Held(key);
        public static bool GetKeyDown(KeyCode key) => !Active ? Input.GetKeyDown(key) : Real && Input.GetKeyDown(key) || DownNow(key);
        public static bool GetKeyUp(KeyCode key) => !Active ? Input.GetKeyUp(key) : Real && Input.GetKeyUp(key) || UpNow(key);

        public static bool GetKey(string name) => !Active ? Input.GetKey(name) : Real && Input.GetKey(name) || Code(name, out var k) && Held(k);
        public static bool GetKeyDown(string name) => !Active ? Input.GetKeyDown(name) : Real && Input.GetKeyDown(name) || Code(name, out var k) && DownNow(k);
        public static bool GetKeyUp(string name) => !Active ? Input.GetKeyUp(name) : Real && Input.GetKeyUp(name) || Code(name, out var k) && UpNow(k);

        public static bool GetMouseButton(int button) => !Active ? Input.GetMouseButton(button) : Real && Input.GetMouseButton(button) || Held(MouseCode(button));
        public static bool GetMouseButtonDown(int button) => !Active ? Input.GetMouseButtonDown(button) : Real && Input.GetMouseButtonDown(button) || DownNow(MouseCode(button));
        public static bool GetMouseButtonUp(int button) => !Active ? Input.GetMouseButtonUp(button) : Real && Input.GetMouseButtonUp(button) || UpNow(MouseCode(button));

        static KeyCode MouseCode(int button) => button >= 0 && button <= 6 ? KeyCode.Mouse0 + button : KeyCode.None;

        public static Vector3 mousePosition
        {
            get
            {
                if (!Active) return Input.mousePosition;
                if (pointerSet) return new Vector3(pointer.x, pointer.y, 0);
                return Real ? Input.mousePosition : new Vector3(Screen.width / 2f, Screen.height / 2f, 0);
            }
        }

        public static Vector2 mouseScrollDelta
        {
            get
            {
                if (!Active) return Input.mouseScrollDelta;
                return (Real ? Input.mouseScrollDelta : Vector2.zero) + new Vector2(0, ScrollNow);
            }
        }

        public static bool anyKey => !Active ? Input.anyKey : Real && Input.anyKey || down.Count > 0;

        public static bool anyKeyDown => !Active ? Input.anyKeyDown : Real && Input.anyKeyDown || AnyDownNow();

        static bool AnyDownNow()
        {
            foreach (var pair in downAt)
                if (pair.Value == Frame)
                    return true;
            return false;
        }

        public static string inputString
        {
            get
            {
                if (!Active) return Input.inputString;
                var mine = typedAt == Frame ? typed.ToString() : "";
                return Real ? Input.inputString + mine : mine;
            }
        }

        public static void ResetInputAxes()
        {
            if (Real || !Active) Input.ResetInputAxes();
            foreach (var list in axes.Values)
                foreach (var a in list)
                    a.Value = 0;
        }

        public static float GetAxis(string axisName)
        {
            if (!Active) return Input.GetAxis(axisName);
            var real = Real ? Input.GetAxis(axisName) : 0f;
            var mine = AxisValue(axisName, false);
            return Mathf.Abs(mine) > Mathf.Abs(real) ? mine : real;
        }

        public static float GetAxisRaw(string axisName)
        {
            if (!Active) return Input.GetAxisRaw(axisName);
            var real = Real ? Input.GetAxisRaw(axisName) : 0f;
            var mine = AxisValue(axisName, true);
            return Mathf.Abs(mine) > Mathf.Abs(real) ? mine : real;
        }

        public static bool GetButton(string buttonName) => !Active ? Input.GetButton(buttonName) : Real && Input.GetButton(buttonName) || Button(buttonName, isHeld);
        public static bool GetButtonDown(string buttonName) => !Active ? Input.GetButtonDown(buttonName) : Real && Input.GetButtonDown(buttonName) || Button(buttonName, isDownNow);

        public static bool GetButtonUp(string buttonName)
        {
            if (!Active) return Input.GetButtonUp(buttonName);
            return Real && Input.GetButtonUp(buttonName) || Button(buttonName, isUpNow) && !Button(buttonName, isHeld);
        }

        // -- axes (ProjectSettings/InputManager.asset) -------------------------------------------------------

        sealed class Axis
        {
            public KeyCode Pos, Neg, AltPos, AltNeg;
            public float Gravity, Dead, Sensitivity;
            public bool Snap, Invert;
            public int Type, Index, Joy;
            public float Value;  // a key axis, moving toward where its keys put it
            public int At = -1;
            public float Time;
        }

        static readonly Dictionary<string, List<Axis>> axes = new Dictionary<string, List<Axis>>();
        static bool axesLoaded, buildChecked;

        /// <summary>The project's axes: each {"name", "positive", "negative", "alt_positive", "alt_negative", "gravity",
        /// "dead", "sensitivity", "snap", "invert", "type", "axis", "joy"} (InputManager.asset's fields).</summary>
        public static void SetAxes(List<object> list)
        {
            axes.Clear();
            foreach (var item in list ?? new List<object>())
            {
                if (!(item is Dictionary<string, object> d)) continue;
                var name = Json.Str(d, "name");
                if (name.Length == 0) continue;
                Code(Json.Str(d, "positive"), out var pos);
                Code(Json.Str(d, "negative"), out var neg);
                Code(Json.Str(d, "alt_positive"), out var altPos);
                Code(Json.Str(d, "alt_negative"), out var altNeg);
                var a = new Axis
                {
                    Pos = pos, Neg = neg, AltPos = altPos, AltNeg = altNeg,
                    Gravity = (float)Json.Num(d, "gravity", 0), Dead = (float)Json.Num(d, "dead", 0),
                    Sensitivity = (float)Json.Num(d, "sensitivity", 1),
                    Snap = Json.Bool(d, "snap", false), Invert = Json.Bool(d, "invert", false),
                    Type = (int)Json.Num(d, "type", 0), Index = (int)Json.Num(d, "axis", 0), Joy = (int)Json.Num(d, "joy", 0),
                };
                if (!axes.TryGetValue(name, out var same)) axes[name] = same = new List<Axis>();
                same.Add(a);
            }
            axesLoaded = true;
        }

        public static int AxisCount => axes.Count;

        static List<Axis> AxesNamed(string name)
        {
            if (!axesLoaded && !buildChecked && !Application.isEditor)
            {
                // (a build: the axes its BuildHook baked in)
                buildChecked = true;
                var info = PlaytestBuildInfo.Get();
                if (info != null && !string.IsNullOrEmpty(info.legacyAxes)) SetAxes(Json.Parse(info.legacyAxes) as List<object>);
            }
            return name != null && axes.TryGetValue(name, out var list) ? list : null;
        }

        static bool Button(string name, Func<KeyCode, bool> test)
        {
            var list = AxesNamed(name);
            if (list == null) return false;
            foreach (var a in list)
                if (a.Type == 0 && (test(a.Pos) || test(a.AltPos)))
                    return true;
            return false;
        }

        static float AxisValue(string name, bool raw)
        {
            var list = AxesNamed(name);
            if (list == null) return 0;
            float best = 0;
            foreach (var a in list)
            {
                var v = Value(a, raw);
                if (Mathf.Abs(v) > Mathf.Abs(best)) best = v;
            }
            return best;
        }

        static float Value(Axis a, bool raw)
        {
            float v;
            switch (a.Type)
            {
                case 0:  // keys or mouse buttons
                {
                    var target = (Held(a.Pos) || Held(a.AltPos) ? 1f : 0f) - (Held(a.Neg) || Held(a.AltNeg) ? 1f : 0f);
                    if (a.At != Frame)
                    {
                        // (moved once a frame, by the time since it was last read)
                        var now = UnityEngine.Time.unscaledTime;
                        var dt = a.At < 0 ? 0f : Mathf.Clamp(now - a.Time, 0f, 1f);
                        a.At = Frame;
                        a.Time = now;
                        if (target != 0)
                        {
                            if (a.Snap && a.Value != 0 && Mathf.Sign(a.Value) != Mathf.Sign(target)) a.Value = 0;
                            a.Value = Mathf.MoveTowards(a.Value, target, a.Sensitivity * dt);
                        }
                        else
                        {
                            a.Value = Mathf.MoveTowards(a.Value, 0, a.Gravity * dt);
                        }
                    }
                    v = raw ? target : a.Value;
                    break;
                }
                case 1:  // mouse movement: X, Y, the wheel
                    v = (a.Index == 0 ? DeltaNow.x : a.Index == 1 ? DeltaNow.y : a.Index == 2 ? ScrollNow : 0f) * a.Sensitivity;
                    break;
                case 2:  // a joystick axis: the bridge's gamepad is joystick 1
                    v = a.Joy == 0 || a.Joy == 1 ? (a.Index >= 0 && a.Index < padAxes.Length ? padAxes[a.Index] : 0f) : 0f;
                    if (Mathf.Abs(v) < a.Dead) v = 0;
                    break;
                default:
                    v = 0;
                    break;
            }
            return a.Invert ? -v : v;
        }

        // -- the Input Manager's key names ("a", "left shift", "[1]", "mouse 0", "joystick 1 button 3") ------------

        static readonly Dictionary<string, KeyCode> names = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase);

        static LegacyInput()
        {
            for (var c = 'a'; c <= 'z'; ++c) names[c.ToString()] = KeyCode.A + (c - 'a');
            for (var i = 0; i <= 9; ++i)
            {
                names[i.ToString(CultureInfo.InvariantCulture)] = KeyCode.Alpha0 + i;
                names["[" + i + "]"] = KeyCode.Keypad0 + i;
            }
            for (var i = 1; i <= 15; ++i) names["f" + i] = KeyCode.F1 + (i - 1);
            for (var i = 0; i <= 6; ++i) names["mouse " + i] = KeyCode.Mouse0 + i;
            for (var b = 0; b <= 19; ++b)
            {
                names["joystick button " + b] = KeyCode.JoystickButton0 + b;
                for (var j = 1; j <= 8; ++j)
                    if (Enum.TryParse("Joystick" + j + "Button" + b, out KeyCode jc))
                        names["joystick " + j + " button " + b] = jc;
            }
            var named = new (string, KeyCode)[]
            {
                ("[.]", KeyCode.KeypadPeriod), ("[/]", KeyCode.KeypadDivide), ("[*]", KeyCode.KeypadMultiply),
                ("[-]", KeyCode.KeypadMinus), ("[+]", KeyCode.KeypadPlus), ("equals", KeyCode.KeypadEquals), ("enter", KeyCode.KeypadEnter),
                ("up", KeyCode.UpArrow), ("down", KeyCode.DownArrow), ("right", KeyCode.RightArrow), ("left", KeyCode.LeftArrow),
                ("insert", KeyCode.Insert), ("home", KeyCode.Home), ("end", KeyCode.End), ("page up", KeyCode.PageUp), ("page down", KeyCode.PageDown),
                ("numlock", KeyCode.Numlock), ("caps lock", KeyCode.CapsLock), ("scroll lock", KeyCode.ScrollLock),
                ("right shift", KeyCode.RightShift), ("left shift", KeyCode.LeftShift), ("right ctrl", KeyCode.RightControl),
                ("left ctrl", KeyCode.LeftControl), ("right alt", KeyCode.RightAlt), ("left alt", KeyCode.LeftAlt),
                ("right cmd", KeyCode.RightCommand), ("left cmd", KeyCode.LeftCommand), ("left windows", KeyCode.LeftWindows),
                ("right windows", KeyCode.RightWindows), ("alt gr", KeyCode.AltGr), ("help", KeyCode.Help), ("print", KeyCode.Print),
                ("sys req", KeyCode.SysReq), ("break", KeyCode.Break), ("menu", KeyCode.Menu),
                ("backspace", KeyCode.Backspace), ("tab", KeyCode.Tab), ("clear", KeyCode.Clear), ("return", KeyCode.Return),
                ("pause", KeyCode.Pause), ("escape", KeyCode.Escape), ("space", KeyCode.Space), ("delete", KeyCode.Delete),
                ("!", KeyCode.Exclaim), ("\"", KeyCode.DoubleQuote), ("#", KeyCode.Hash), ("$", KeyCode.Dollar), ("%", KeyCode.Percent),
                ("&", KeyCode.Ampersand), ("'", KeyCode.Quote), ("(", KeyCode.LeftParen), (")", KeyCode.RightParen), ("*", KeyCode.Asterisk),
                ("+", KeyCode.Plus), (",", KeyCode.Comma), ("-", KeyCode.Minus), (".", KeyCode.Period), ("/", KeyCode.Slash),
                (":", KeyCode.Colon), (";", KeyCode.Semicolon), ("<", KeyCode.Less), ("=", KeyCode.Equals), (">", KeyCode.Greater),
                ("?", KeyCode.Question), ("@", KeyCode.At), ("[", KeyCode.LeftBracket), ("\\", KeyCode.Backslash), ("]", KeyCode.RightBracket),
                ("^", KeyCode.Caret), ("_", KeyCode.Underscore), ("`", KeyCode.BackQuote), ("{", KeyCode.LeftCurlyBracket),
                ("|", KeyCode.Pipe), ("}", KeyCode.RightCurlyBracket), ("~", KeyCode.Tilde),
            };
            foreach (var (n, k) in named) names[n] = k;
        }

        /// <summary>The KeyCode an Input Manager key name stands for (false, KeyCode.None: none, or an empty name).</summary>
        public static bool Code(string name, out KeyCode code)
        {
            code = KeyCode.None;
            return !string.IsNullOrEmpty(name) && names.TryGetValue(name.Trim(), out code);
        }

        /// <summary>The character a key types (shift held: its upper case or symbol), '\0' for none.</summary>
        static char CharOf(KeyCode code)
        {
            var shift = Held(KeyCode.LeftShift) || Held(KeyCode.RightShift);
            if (code >= KeyCode.A && code <= KeyCode.Z) return (char)((shift ? 'A' : 'a') + (code - KeyCode.A));
            if (code >= KeyCode.Alpha0 && code <= KeyCode.Alpha9) return shift ? ")!@#$%^&*("[code - KeyCode.Alpha0] : (char)('0' + (code - KeyCode.Alpha0));
            if (code >= KeyCode.Keypad0 && code <= KeyCode.Keypad9) return (char)('0' + (code - KeyCode.Keypad0));
            switch (code)
            {
                case KeyCode.Space: return ' ';
                case KeyCode.Return:
                case KeyCode.KeypadEnter: return '\n';
                case KeyCode.Backspace: return '\b';
                case KeyCode.Minus: return shift ? '_' : '-';
                case KeyCode.Equals: return shift ? '+' : '=';
                case KeyCode.LeftBracket: return shift ? '{' : '[';
                case KeyCode.RightBracket: return shift ? '}' : ']';
                case KeyCode.Backslash: return shift ? '|' : '\\';
                case KeyCode.Semicolon: return shift ? ':' : ';';
                case KeyCode.Quote: return shift ? '"' : '\'';
                case KeyCode.Comma: return shift ? '<' : ',';
                case KeyCode.Period: return shift ? '>' : '.';
                case KeyCode.Slash: return shift ? '?' : '/';
                case KeyCode.BackQuote: return shift ? '~' : '`';
                case KeyCode.KeypadPeriod: return '.';
                case KeyCode.KeypadDivide: return '/';
                case KeyCode.KeypadMultiply: return '*';
                case KeyCode.KeypadMinus: return '-';
                case KeyCode.KeypadPlus: return '+';
            }
            return '\0';
        }

        // -- which assemblies read through here ------------------------------------------------------------

        static List<(string name, int calls)> rewritten;
        static int assembliesSeen;

        /// <summary>The game's assemblies whose UnityEngine.Input calls come here, with how many calls each.</summary>
        public static List<(string name, int calls)> Rewritten()
        {
#if AUTOPILOT_CURRENT_ASSEMBLIES
            var all = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies().ToList();
#else
            var all = AppDomain.CurrentDomain.GetAssemblies().ToList();
#endif
            if (rewritten != null && all.Count == assembliesSeen) return rewritten;
            assembliesSeen = all.Count;
            rewritten = new List<(string, int)>();
            foreach (var asm in all)
            {
                try
                {
                    foreach (var attr in asm.GetCustomAttributes(typeof(LegacyInputRewrittenAttribute), false))
                        rewritten.Add((asm.GetName().Name, ((LegacyInputRewrittenAttribute)attr).Calls));
                }
                catch
                {
                    // (an assembly whose attributes can't be read: not one of ours)
                }
            }
            return rewritten;
        }
    }
}
