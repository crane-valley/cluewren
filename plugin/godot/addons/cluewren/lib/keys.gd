# Modified by Crane Valley: key lookups are cached, a held key being looked up on every lease renewal.
## Unreal FKey names (what the kit presses) as Godot input events, plus Godot's own key names and InputMap actions.
##
## A key name resolves, in order, to: an Unreal key name (W, SpaceBar, LeftShift, One, LeftMouseButton, MouseX,
## Gamepad_FaceButton_Bottom, Gamepad_LeftX...), an InputMap action of the game (jump, move_forward), or a Godot key
## name (Space, Shift, Ctrl, F5: OS.find_keycode_from_string). The result is a spec: {"kind": "key"|"mouse_button"|
## "wheel"|"mouse_axis"|"joy_button"|"joy_axis"|"action", ...}.
@tool
extends RefCounted

const NAMED := {
	"SpaceBar": KEY_SPACE, "Enter": KEY_ENTER, "Escape": KEY_ESCAPE, "Tab": KEY_TAB, "BackSpace": KEY_BACKSPACE,
	"LeftShift": KEY_SHIFT, "RightShift": KEY_SHIFT, "LeftControl": KEY_CTRL, "RightControl": KEY_CTRL,
	"LeftAlt": KEY_ALT, "RightAlt": KEY_ALT, "LeftCommand": KEY_META, "RightCommand": KEY_META,
	"Up": KEY_UP, "Down": KEY_DOWN, "Left": KEY_LEFT, "Right": KEY_RIGHT,
	"Delete": KEY_DELETE, "Insert": KEY_INSERT, "Home": KEY_HOME, "End": KEY_END, "PageUp": KEY_PAGEUP,
	"PageDown": KEY_PAGEDOWN, "CapsLock": KEY_CAPSLOCK, "Pause": KEY_PAUSE,
	"Zero": KEY_0, "One": KEY_1, "Two": KEY_2, "Three": KEY_3, "Four": KEY_4, "Five": KEY_5, "Six": KEY_6,
	"Seven": KEY_7, "Eight": KEY_8, "Nine": KEY_9,
	"NumPadZero": KEY_KP_0, "NumPadOne": KEY_KP_1, "NumPadTwo": KEY_KP_2, "NumPadThree": KEY_KP_3,
	"NumPadFour": KEY_KP_4, "NumPadFive": KEY_KP_5, "NumPadSix": KEY_KP_6, "NumPadSeven": KEY_KP_7,
	"NumPadEight": KEY_KP_8, "NumPadNine": KEY_KP_9, "Multiply": KEY_KP_MULTIPLY, "Add": KEY_KP_ADD,
	"Subtract": KEY_KP_SUBTRACT, "Decimal": KEY_KP_PERIOD, "Divide": KEY_KP_DIVIDE,
	"Semicolon": KEY_SEMICOLON, "Equals": KEY_EQUAL, "Comma": KEY_COMMA, "Hyphen": KEY_MINUS, "Underscore": KEY_UNDERSCORE,
	"Period": KEY_PERIOD, "Slash": KEY_SLASH, "Tilde": KEY_QUOTELEFT, "LeftBracket": KEY_BRACKETLEFT,
	"Backslash": KEY_BACKSLASH, "RightBracket": KEY_BRACKETRIGHT, "Apostrophe": KEY_APOSTROPHE,
}

const MOUSE := {
	"LeftMouseButton": MOUSE_BUTTON_LEFT, "RightMouseButton": MOUSE_BUTTON_RIGHT,
	"MiddleMouseButton": MOUSE_BUTTON_MIDDLE, "ThumbMouseButton": MOUSE_BUTTON_XBUTTON1,
	"ThumbMouseButton2": MOUSE_BUTTON_XBUTTON2,
}

const JOY_BUTTONS := {
	"Gamepad_FaceButton_Bottom": JOY_BUTTON_A, "Gamepad_FaceButton_Right": JOY_BUTTON_B,
	"Gamepad_FaceButton_Left": JOY_BUTTON_X, "Gamepad_FaceButton_Top": JOY_BUTTON_Y,
	"Gamepad_LeftShoulder": JOY_BUTTON_LEFT_SHOULDER, "Gamepad_RightShoulder": JOY_BUTTON_RIGHT_SHOULDER,
	"Gamepad_Special_Left": JOY_BUTTON_BACK, "Gamepad_Special_Right": JOY_BUTTON_START,
	"Gamepad_LeftThumbstick": JOY_BUTTON_LEFT_STICK, "Gamepad_RightThumbstick": JOY_BUTTON_RIGHT_STICK,
	"Gamepad_DPad_Up": JOY_BUTTON_DPAD_UP, "Gamepad_DPad_Down": JOY_BUTTON_DPAD_DOWN,
	"Gamepad_DPad_Left": JOY_BUTTON_DPAD_LEFT, "Gamepad_DPad_Right": JOY_BUTTON_DPAD_RIGHT,
}

# Axis keys: [axis, sign]. Unreal's Y axes are positive up, Godot's positive down.
const JOY_AXES := {
	"Gamepad_LeftX": [JOY_AXIS_LEFT_X, 1.0], "Gamepad_LeftY": [JOY_AXIS_LEFT_Y, -1.0],
	"Gamepad_RightX": [JOY_AXIS_RIGHT_X, 1.0], "Gamepad_RightY": [JOY_AXIS_RIGHT_Y, -1.0],
	"Gamepad_LeftTriggerAxis": [JOY_AXIS_TRIGGER_LEFT, 1.0], "Gamepad_RightTriggerAxis": [JOY_AXIS_TRIGGER_RIGHT, 1.0],
}

# Triggers pressed as buttons
const JOY_TRIGGERS := {"Gamepad_LeftTrigger": JOY_AXIS_TRIGGER_LEFT, "Gamepad_RightTrigger": JOY_AXIS_TRIGGER_RIGHT}


static var _specs := {}
static var _names := {}
static var _default_ui := {}


## An action's spec isn't kept: a game can add and remove actions while it runs.
static func spec(name: String) -> Dictionary:
	var known = _specs.get(name)
	if known != null:
		return known
	var found := _find_spec(name)
	if found.get("kind", "") != "action" and not found.is_empty():
		_specs[name] = found
	return found


static func _find_spec(name: String) -> Dictionary:
	if name == "":
		return {}
	if name.length() == 1 and name.to_upper() >= "A" and name.to_upper() <= "Z":
		return {"kind": "key", "keycode": OS.find_keycode_from_string(name.to_upper())}
	if NAMED.has(name):
		return {"kind": "key", "keycode": NAMED[name]}
	if name.length() >= 2 and name.length() <= 3 and name.begins_with("F") and name.substr(1).is_valid_int():
		var n := int(name.substr(1))
		if n >= 1 and n <= 12:
			return {"kind": "key", "keycode": KEY_F1 + n - 1}
	if MOUSE.has(name):
		return {"kind": "mouse_button", "button": MOUSE[name]}
	if name == "MouseScrollUp":
		return {"kind": "wheel", "button": MOUSE_BUTTON_WHEEL_UP}
	if name == "MouseScrollDown":
		return {"kind": "wheel", "button": MOUSE_BUTTON_WHEEL_DOWN}
	if name == "MouseX" or name == "MouseY":
		return {"kind": "mouse_axis", "axis": name}
	if name == "MouseWheelAxis":
		return {"kind": "wheel_axis"}
	if JOY_BUTTONS.has(name):
		return {"kind": "joy_button", "button": JOY_BUTTONS[name]}
	if JOY_AXES.has(name):
		return {"kind": "joy_axis", "axis": JOY_AXES[name][0], "sign": JOY_AXES[name][1]}
	if JOY_TRIGGERS.has(name):
		return {"kind": "joy_trigger", "axis": JOY_TRIGGERS[name]}
	if InputMap.has_action(name):
		return {"kind": "action", "action": name}
	var code := OS.find_keycode_from_string(name)
	if code != KEY_NONE:
		return {"kind": "key", "keycode": code}
	return {}


static func is_axis(s: Dictionary) -> bool:
	return s.get("kind", "") in ["mouse_axis", "joy_axis", "wheel_axis"]


## The Unreal name of a Godot input event (for listing the game's controls), or "" when it has none.
static func unreal_name(ev: InputEvent) -> String:
	if ev is InputEventKey:
		var code: int = ev.keycode if ev.keycode != KEY_NONE else ev.physical_keycode
		if code == KEY_NONE:
			code = ev.key_label
		if _names.is_empty():
			for k in NAMED:
				if not k.begins_with("Right"):
					_names[NAMED[k]] = k
		if _names.has(code):
			return _names[code]
		if code >= KEY_F1 and code <= KEY_F12:
			return "F%d" % (code - KEY_F1 + 1)
		var s := OS.get_keycode_string(code)
		if s.length() == 1:
			return s.to_upper()
		return s
	if ev is InputEventMouseButton:
		for k in MOUSE:
			if MOUSE[k] == ev.button_index:
				return k
		if ev.button_index == MOUSE_BUTTON_WHEEL_UP:
			return "MouseScrollUp"
		if ev.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			return "MouseScrollDown"
	if ev is InputEventJoypadButton:
		for k in JOY_BUTTONS:
			if JOY_BUTTONS[k] == ev.button_index:
				return k
	if ev is InputEventJoypadMotion:
		for k in JOY_AXES:
			if JOY_AXES[k][0] == ev.axis:
				return k
	return ""


## Whether an action is one of Godot's built-in ui_* actions as the engine defines it (the project didn't change it).
static func is_default_ui(action: String) -> bool:
	if not action.begins_with("ui_"):
		return false
	var key := "input/" + action
	if not ProjectSettings.has_setting(key) or not ProjectSettings.property_can_revert(key):
		return true
	return _events_text(ProjectSettings.get_setting(key)) == _events_text(ProjectSettings.property_get_revert(key))


static func _events_text(v) -> String:
	var out := []
	if typeof(v) == TYPE_DICTIONARY:
		for ev in v.get("events", []):
			if ev is InputEvent:
				out.append(ev.as_text())
	return ",".join(out)


## The game's InputMap actions with their keys as Unreal names: [{"name", "action", "keys", "godot_keys", "digital"}].
## Godot's built-in ui_* actions are left out unless the project changed them.
static func controls() -> Array:
	var out := []
	for action in InputMap.get_actions():
		var a := str(action)
		if not _default_ui.has(a):
			_default_ui[a] = is_default_ui(a)
		if _default_ui[a]:
			continue
		var keys := []
		var godot := []
		var digital := true
		for ev in InputMap.action_get_events(action):
			var n := unreal_name(ev)
			if n != "" and not keys.has(n):
				keys.append(n)
			godot.append(ev.as_text())
			if ev is InputEventJoypadMotion:
				digital = false
		out.append({"name": a, "action": a, "keys": keys, "godot_keys": godot, "digital": digital})
	return out


## The action a key (an Unreal name) is bound to, preferring the game's own actions over ui_* ones; "" when none.
static func action_for_key(name: String) -> String:
	var s := spec(name)
	if s.get("kind", "") != "key":
		return ""
	var found := ""
	for action in InputMap.get_actions():
		for ev in InputMap.action_get_events(action):
			if ev is InputEventKey and (ev.keycode == s.keycode or ev.physical_keycode == s.keycode):
				if not str(action).begins_with("ui_"):
					return str(action)
				if found == "":
					found = str(action)
	return found
