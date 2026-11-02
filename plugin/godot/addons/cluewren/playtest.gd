# Modified by Crane Valley: the game connects only with --playtest-connect, through brain.json and the mutual handshake;
# the run requested on the command line, report files, token, sharing, preferences and codex ops are removed; the addon
# stays idle until a brain is there, does nothing in a release build, and keeps its per-frame path free of allocation.
## Cluewren: the game-side bridge (an autoload, /root/Cluewren). Runs inside the game process, in the editor's
## play sessions and in debug builds alike, and keeps working while the game is paused (process_mode ALWAYS).
##
## It is idle until one of these:
##  - the command line asks for it (`-- --playtest-connect`): it connects out to the brain itself (hello host "game");
##  - the editor attaches to this play session over the editor's debugger connection: editor_bridge.gd holds the one
##    link to the brain (hello host "editor") and relays requests here, and this answers them back the same way.
## Ops: find/transforms/pose/movement, `record` and `capture` (the game's picture) in lib/media.gd, and the rest below.
extends Node

const Frame = preload("lib/frame.gd")
const Keys = preload("lib/keys.gd")
const Rc = preload("lib/rc.gd")
const World = preload("lib/world.gd")
const Sight = preload("lib/sight.gd")
const Ui = preload("lib/ui.gd")
const Codex = preload("lib/codex.gd")
const Detectors = preload("lib/detectors.gd")
const Link = preload("lib/link.gd")
const Media = preload("lib/media.gd")

const CAPTURE := "cluewren"
const KEY_TAP_SECONDS := 0.05
const KEY_TAP_FRAMES := 2  # a tap stays down this many frames too: input read once a frame misses a shorter one
const MOVE_KEYS := ["W", "S", "A", "D"]
const NO_ID := -1

var rc
var world
var sight
var ui
var detectors
var media
var link = null  # direct mode: our own connection to the brain
var relay := false  # the editor relays for us
var active := false  # a brain is there (welcomed, or the editor attached)

var game_time := 0.0
var _begun := false
var _scene: Node = null
var _launch_scene := ""
var _events := []
var _emit: Callable
var _pending := []  # deferred answers: {"id", "op", "tick": Callable(now) -> bool}
var _held := {}  # key name -> {"spec", "value", "rate", "renewed"}
var _expired := []
var _lease := 1.0
var _pressed_actions := {}  # actions move pressed (Input.action_press), released with the keys
var _heartbeat_at := 0.0
var _last_real := 0.0
var _timestep_saved = null
var _summary_task := -1

var cli_connect := false
var cli_map := ""  # --playtest-map: the scene to start in (a res:// path or a scene file's name)
var cli_connect_timeout := 120.0  # --playtest-connect-timeout: seconds to wait for the welcome
var _cli_deadline := 0.0
var _exiting := false


func _ready() -> void:
	set_process(false)
	# A release build plays no part in this, as a Unity release build has none of the runtime.
	if not OS.is_debug_build():
		return
	process_mode = Node.PROCESS_MODE_ALWAYS
	process_priority = -1000  # before the game's nodes: inputs land before they read them
	_parse_command_line()
	if EngineDebugger.is_active():
		EngineDebugger.register_message_capture(CAPTURE, _on_editor_message)
	if cli_connect:
		_begin()
		link = Link.new()
		link.hello_fields = _hello
		link.on_frame = _on_brain_frame
		link.on_welcomed = _on_welcomed
		link.on_closed = _on_closed
		link.on_refused = _on_refused
		link.open()
		_cli_deadline = _now() + cli_connect_timeout
		print("Cluewren: connecting to the brain")
	if cli_map != "":
		var scene := Codex.find_scene(cli_map)
		if scene == "":
			push_warning("Cluewren: no scene %s to start in" % cli_map)
		else:
			_launch_scene = scene  # (what a restart reopens)
			get_tree().change_scene_to_file.call_deferred(scene)


func _begin() -> void:
	if _begun:
		return
	_begun = true
	_emit = emit_event
	rc = Rc.new(self)
	world = World.new(self)
	ui = Ui.new(self)
	sight = Sight.new(self, world)
	media = Media.new(self)
	detectors = Detectors.new(self, world, ui)
	_prepare_headless()
	set_process(true)


func _exit_tree() -> void:
	if detectors:
		detectors.shutdown()
	if link:
		link.close()
	if EngineDebugger.is_active() and EngineDebugger.has_capture(CAPTURE):
		EngineDebugger.unregister_message_capture(CAPTURE)
	if _summary_task >= 0:
		WorkerThreadPool.wait_for_task_completion(_summary_task)


func _now() -> float:
	return Time.get_ticks_msec() / 1000.0


## Headless: give the root viewport the project's window size (headless it is 64x64, which would squeeze the UI and the
## camera's view), and cap the frame rate the way a display's refresh would.
func _prepare_headless() -> void:
	if DisplayServer.get_name() != "headless":
		return
	var w := int(ProjectSettings.get_setting("display/window/size/viewport_width", 1152))
	var h := int(ProjectSettings.get_setting("display/window/size/viewport_height", 648))
	get_tree().root.size = Vector2i(w, h)
	if Engine.max_fps == 0:
		Engine.max_fps = 60


func _parse_command_line() -> void:
	var args := OS.get_cmdline_user_args()
	if args.is_empty():
		args = OS.get_cmdline_args()
	# Both spellings: --playtest-connect-timeout=... (Godot style) and -PlaytestConnectTimeout=... (as Unity's plugin takes)
	for a in args:
		var s := str(a)
		var name := s.get_slice("=", 0)
		var key := name.lstrip("-").to_lower().replace("-", "").replace("_", "")
		if not key.begins_with("playtest"):
			continue
		var value := s.substr(name.length() + 1) if "=" in s else ""
		value = value.trim_prefix("\"").trim_suffix("\"")
		match key:
			"playtestconnect":
				cli_connect = true
			"playtestmap":
				cli_map = value
			"playtestconnecttimeout":
				if value.is_valid_float() and float(value) > 0.0:
					cli_connect_timeout = float(value)
				else:
					push_warning("Cluewren: --playtest-connect-timeout wants a number of seconds (got \"%s\")" % value)


static func plugin_version() -> String:
	var cfg := ConfigFile.new()
	if cfg.load("res://addons/cluewren/plugin.cfg") == OK:
		return str(cfg.get_value("plugin", "version", "1.0.0"))
	return "1.0.0"


func _hello() -> Dictionary:
	return {"plugin_version": plugin_version(), "engine": "godot", "engine_version": Engine.get_version_info().string,
		"project": str(ProjectSettings.get_setting("application/config/name", "")), "host": "game",
		# what this addon does beyond the protocol's first version: the brain falls back for one without them
		"features": ["timed_hold", "levels", "media"] if Media.can_capture() else ["timed_hold", "levels"]}


func t_value():
	return Frame.r2(game_time) if get_tree().current_scene != null else null


# --- sending ----------------------------------------------------------------------------------------

func _send(frame: Dictionary) -> void:
	var text := JSON.stringify(frame)
	if relay:
		EngineDebugger.send_message(CAPTURE + ":frame", [NO_ID, true, text])
	elif link:
		link.send_text(text)


## A picture already made as JSON text, off the game thread: a recording's frame, or a capture's answer.
func send_picture(text: String, answer: bool) -> void:
	if not link:
		return
	if answer:
		link.send_answer(text, true)
	else:
		link.send_text(text, true)


## An answer already made as JSON text. Through the editor it carries its id and outcome beside the text, so the editor
## needn't parse every answer on its way past.
func send_answer(id, ok: bool, text: String) -> void:
	if relay:
		EngineDebugger.send_message(CAPTURE + ":frame", [id, ok, text])
	elif link:
		link.send_answer(text)


## A picture for the editor to pass on: head ({"kind": "frame"|"still", ...}) and the JPEG's bytes (the editor makes
## the base64 and the message: the game sends the bytes as they are).
func send_media(head: Dictionary, jpeg: PackedByteArray) -> void:
	if relay:
		EngineDebugger.send_message(CAPTURE + ":media", [JSON.stringify(head), jpeg])


func respond(id, result) -> void:
	send_answer(id, true, JSON.stringify({"type": "response", "id": id, "ok": true, "result": result, "t": t_value()}))


func respond_error(id, error: String, status := 400) -> void:
	send_answer(id, false, JSON.stringify({"type": "response", "id": id, "ok": false, "status": status, "error": error, "t": t_value()}))


func emit_event(event: Dictionary) -> void:
	if active:
		_events.append(event)


# --- the editor's relay -----------------------------------------------------------------------------

func _on_editor_message(message: String, data: Array) -> bool:
	match message:
		"attach":
			relay = true
			active = true
			_begin()
			if data.size() > 0 and typeof(data[0]) == TYPE_STRING:
				var cfg = JSON.parse_string(data[0])
				if typeof(cfg) == TYPE_DICTIONARY:
					if typeof(cfg.get("detectors")) == TYPE_DICTIONARY:
						detectors.configure(cfg.detectors)
					# a recording the brain started in an earlier play session of this connection goes on in this one
					if typeof(cfg.get("record")) == TYPE_DICTIONARY:
						if not media.recording:
							media.start(cfg.record, int(cfg.record.get("n", 0)))
					elif media.recording:
						media.stop()
			EngineDebugger.send_message(CAPTURE + ":ready", [JSON.stringify({"scene": _scene_path(), "t": t_value()})])
			return true
		"detach":
			if _begun:
				_release_all()
				media.stop()
			active = false
			return true
		"release":
			if _begun:
				_release_all()
			return true
		"record_stop":
			if _begun:
				media.stop()
			return true
		"request":
			if data.size() > 0:
				var frame = JSON.parse_string(str(data[0]))
				if typeof(frame) == TYPE_DICTIONARY:
					_handle_request(frame)
			return true
	return false


func _scene_path() -> String:
	var s := get_tree().current_scene
	return s.scene_file_path if s else ""


# --- the frame --------------------------------------------------------------------------------------

func _process(delta: float) -> void:
	var now := _now()
	var real_dt := now - _last_real if _last_real > 0.0 else delta
	_last_real = now
	var tree := get_tree()
	if not tree.paused:
		game_time += delta
	if tree.current_scene != _scene:
		var had := _scene != null
		_scene = tree.current_scene
		game_time = 0.0
		if _launch_scene == "" and _scene and _scene.scene_file_path != "":
			_launch_scene = _scene.scene_file_path
		if had or _scene != null:
			_release_all()
			emit_event({"kind": "session", "t": t_value(), "running": _scene != null})
	_flush_events()
	if link:
		link.poll()
		if cli_connect and not _exiting and not link.connected and link.session == "" and _cli_deadline > 0.0 and now > _cli_deadline:
			_exit_failed("no welcome from the brain within %d s (%s)" % [int(cli_connect_timeout), link.problem])
	if not active:
		if not _events.is_empty():
			_events.clear()
		return
	_tick_keys(now, real_dt)
	var i := 0
	while i < _pending.size():
		var p: Dictionary = _pending[i]
		if p.tick.call(now):
			_pending.remove_at(i)
		else:
			i += 1
	media.tick(now)
	detectors.tick(_emit, game_time, real_dt, tree.paused)
	if now - _heartbeat_at >= 1.0:
		_heartbeat_at = now
		emit_event({"kind": "heartbeat", "t": t_value(), "fps": Engine.get_frames_per_second(), "paused": tree.paused})
	_flush_events()


## Send the events gathered so far (once a frame; and before the frame's answers, so a scene change is told before
## an answer from the new scene).
func _flush_events() -> void:
	if _events.size() > 0 and active:
		var batch := _events
		_events = []
		_send({"type": "events", "events": batch, "t": t_value()})


func _on_welcomed(resumed: bool) -> void:
	active = true
	_cli_deadline = 0.0
	if not resumed:
		_pending.clear()
		media.stop()  # (a new connection: no recording goes on from the one before)
	_write_summary()
	print("Cluewren: connected (%s)" % link.role)


func _on_closed(was_connected: bool) -> void:
	if not was_connected:
		return
	active = false
	_release_all()
	print("Cluewren: %s" % link.problem)


func _on_refused() -> void:
	active = false
	_release_all()
	media.stop()
	_exit_failed(link.problem)


func _exit_failed(why: String) -> void:
	if _exiting:
		return
	_exiting = true
	push_error("Cluewren: " + why)
	print("Cluewren: exiting with code 1")
	get_tree().quit(1)


func _on_brain_frame(frame: Dictionary, _text: String) -> void:
	match str(frame.get("type", "")):
		"request":
			_handle_request(frame)
		"status":
			print("Cluewren: %s" % frame.get("text", ""))
		"finding":
			var f = frame.get("finding", {})
			if typeof(f) == TYPE_DICTIONARY:
				print("Cluewren: finding: %s" % f.get("summary", ""))


## The agent reads this with its own tools, so it is there before the agent is told about the run.
func _write_summary() -> void:
	if _summary_task >= 0:
		if not WorkerThreadPool.is_task_completed(_summary_task):
			return
		WorkerThreadPool.wait_for_task_completion(_summary_task)
	_summary_task = WorkerThreadPool.add_task(Codex.write_summary, false, "Cluewren summary")


# --- requests ---------------------------------------------------------------------------------------

func _handle_request(frame: Dictionary) -> void:
	var id = frame.get("id")
	if typeof(id) == TYPE_FLOAT:
		id = int(id)
	var op := str(frame.get("op", ""))
	var args = frame.get("args", {})
	if typeof(args) != TYPE_DICTIONARY:
		args = {}
	var r = dispatch(id, op, args)
	if r == null:
		return  # deferred: answered later
	if r[0]:
		respond(id, r[1])
	else:
		respond_error(id, str(r[1]), r[2] if r.size() > 2 else 400)


## [true, result] | [false, error, status?] | null (the op answers later).
func dispatch(id, op: String, args: Dictionary):
	match op:
		"rc":
			return rc.handle(str(args.get("verb", "")), args.get("body", null))
		"context":
			return [true, context()]
		"session":
			return session(id, args)
		"levels":
			return [true, {"levels": Codex.scenes(), "open": str(ProjectSettings.get_setting("application/run/main_scene", "")), "playing": _scene_path()}]
		"pause":
			get_tree().paused = bool(args.get("paused", false))
			return [true, {"paused": get_tree().paused}]
		"input":
			return input_op(id, args)
		"key":
			return key_op(id, args)
		"move":
			return move_op(id, args)
		"watch":
			return watch_op(id, args)
		"view":
			return sight.view(args)
		"screen":
			return [true, ui.screen(args)]
		"ui":
			return [true, {"elements": ui.list()}]
		"ui_act":
			var r: Array = ui.act(args)
			if r[0] and r[1] is Dictionary:
				return [true, r[1]]
			return r
		"timestep":
			return timestep(args)
		"detectors":
			detectors.configure(args)
			return [true, {}]
		"ping":
			return [true, {"game_time": Frame.r2(game_time), "wall_time": Time.get_unix_time_from_system()}]
		"record":
			match str(args.get("action", "")):
				"start":
					return media.start(args)
				"stop":
					return [true, media.stop()]
			return [false, "record wants action start or stop"]
		"capture":
			return media.capture(id, args)
		"find":
			return [true, world.find(args)]
		"transforms":
			return [true, world.transforms(args, rc.resolve)]
		"pose":
			return world.pose(args)
		"movement":
			return world.movement(args)
	return [false, "unknown op %s" % op]


func context() -> Dictionary:
	var p: Node = world.player()
	var s := get_tree().current_scene
	var cam = world.camera3d()
	if cam == null:
		cam = world.camera2d()
	return {"world": str(s.get_path()) if s else "", "scene": _scene_path(), "character": str(p.get_path()) if p else "",
		"player_found_by": world.found_by if p else "", "controller": "", "input_subsystem": "",
		"camera_manager": str(cam.get_path()) if cam else "", "camera": str(cam.get_path()) if cam else "",
		"dimension": world.dimension(), "game_name": str(ProjectSettings.get_setting("application/config/name", "")),
		"host": "editor" if relay else "game", "engine": "godot", "engine_version": Engine.get_version_info().string,
		"session_running": s != null, "paused": get_tree().paused, "game_time": Frame.r2(game_time),
		"project_dir": ProjectSettings.globalize_path("res://").trim_suffix("/"), "controls": Keys.controls(),
		"launch_scene": _launch_scene, "rendering": DisplayServer.get_name() != "headless"}


# --- session ----------------------------------------------------------------------------------------

func session(id, args: Dictionary):
	var action := str(args.get("action", "status"))
	if action != "restart":
		return [true, {"running": get_tree().current_scene != null}]
	var target := ""
	if str(args.get("map", "")) != "":
		target = Codex.find_scene(str(args.map))
		if target == "":
			return [false, "No scene %s" % args.map]
	else:
		target = _launch_scene if _launch_scene != "" else _scene_path()
	if target == "":
		return [false, "No scene to restart"]
	_release_all()
	if args.get("fresh", false):
		_reset_autoloads()
	var old := get_tree().current_scene
	var old_id := old.get_instance_id() if old else 0  # (the scene itself is freed: never capture it)
	get_tree().paused = false
	var err := get_tree().change_scene_to_file(target)
	if err != OK:
		return [false, "Couldn't open %s (error %d)" % [target, err], 500]
	var started := _now()
	var state := {"seen": 0}
	_pending.append({"id": id, "op": "session", "tick": func(now: float) -> bool:
		var cur := get_tree().current_scene
		if cur != null and cur.get_instance_id() != old_id and cur.is_inside_tree():
			state.seen += 1
			if state.seen >= 2:  # a frame after it is in: its _ready has run
				respond(id, {"running": true, "scene": cur.scene_file_path})
				return true
		if now - started > 30.0:
			respond_error(id, "Timed out reopening the scene", 500)
			return true
		return false})
	return null


## Put the game's autoloads' script variables back to the values a fresh instance starts with (session restart with
## "fresh": true). Their nodes stay (scripts hold them by name); what their _ready() set isn't redone.
func _reset_autoloads() -> void:
	for p in ProjectSettings.get_property_list():
		var key: String = p.name
		if not key.begins_with("autoload/"):
			continue
		var name := key.substr(9)
		if name == "Cluewren":
			continue
		var node := get_tree().root.get_node_or_null(name)
		if node == null or not (node.get_script() is Script):
			continue
		var fresh = node.get_script().new()
		for prop in node.get_script().get_script_property_list():
			if prop.usage & PROPERTY_USAGE_SCRIPT_VARIABLE:
				node.set(prop.name, fresh.get(prop.name))
		if fresh is Node:
			fresh.free()


func timestep(args: Dictionary):
	if args.get("fixed", false):
		if _timestep_saved == null:
			_timestep_saved = [Engine.max_fps, Engine.physics_ticks_per_second]
		var fps := maxi(1, int(args.get("fps", 30)))
		Engine.max_fps = fps
		return [true, {"note": "Godot has no runtime fixed frame time: the frame rate is capped at %d" % fps}]
	if _timestep_saved != null:
		Engine.max_fps = _timestep_saved[0]
		_timestep_saved = null
	return [true, {}]


# --- the player's hands -----------------------------------------------------------------------------

func _deliver(ev: InputEvent) -> void:
	Input.parse_input_event(ev)
	Input.flush_buffered_events()  # headless, nothing else flushes them (and windowed, they land this frame)


func _crosshair() -> Vector2:
	return get_viewport().get_visible_rect().size / 2.0


func _event(spec: Dictionary, pressed: bool, value := 1.0) -> InputEvent:
	match spec.kind:
		"key":
			var ev := InputEventKey.new()
			ev.keycode = spec.keycode
			ev.physical_keycode = spec.keycode
			ev.key_label = spec.keycode
			ev.pressed = pressed
			var s := OS.get_keycode_string(spec.keycode)
			if pressed and s.length() == 1:
				ev.unicode = s.to_lower().unicode_at(0)
			elif pressed and spec.keycode == KEY_SPACE:
				ev.unicode = 32
			return ev
		"mouse_button", "wheel":
			var ev := InputEventMouseButton.new()
			ev.button_index = spec.button
			ev.pressed = pressed
			ev.position = _crosshair()
			ev.global_position = ev.position
			ev.button_mask = (1 << (spec.button - 1)) if pressed else 0
			if spec.kind == "wheel":
				ev.factor = absf(value)
			return ev
		"mouse_axis":
			var ev := InputEventMouseMotion.new()
			# Unreal's MouseY is positive up, Godot's relative.y positive down
			var rel := Vector2(value, 0.0) if spec.axis == "MouseX" else Vector2(0.0, -value)
			ev.relative = rel
			ev.screen_relative = rel
			ev.position = _crosshair()
			ev.global_position = ev.position
			return ev
		"wheel_axis":
			var ev := InputEventMouseButton.new()
			ev.button_index = MOUSE_BUTTON_WHEEL_UP if value > 0 else MOUSE_BUTTON_WHEEL_DOWN
			ev.pressed = pressed
			ev.factor = absf(value)
			ev.position = _crosshair()
			return ev
		"joy_button":
			var ev := InputEventJoypadButton.new()
			ev.button_index = spec.button
			ev.pressed = pressed
			ev.pressure = 1.0 if pressed else 0.0
			return ev
		"joy_axis":
			var ev := InputEventJoypadMotion.new()
			ev.axis = spec.axis
			ev.axis_value = clampf(value * spec.sign, -1.0, 1.0) if pressed else 0.0
			return ev
		"joy_trigger":
			var ev := InputEventJoypadMotion.new()
			ev.axis = spec.axis
			ev.axis_value = 1.0 if pressed else 0.0
			return ev
		"action":
			var ev := InputEventAction.new()
			ev.action = spec.action
			ev.pressed = pressed
			ev.strength = clampf(absf(value), 0.0, 1.0) if pressed else 0.0
			return ev
	return null


func _send_key(spec: Dictionary, pressed: bool, value := 1.0) -> void:
	var ev := _event(spec, pressed, value)
	if ev:
		_deliver(ev)
	if spec.kind == "wheel" and pressed:
		_deliver(_event(spec, false, value))


func key_op(id, args: Dictionary):
	var mode := str(args.get("mode", "press"))
	if args.has("lease") and float(args.lease) > 0.0:
		_lease = clampf(float(args.lease), 0.2, 10.0)
	if mode == "release_all":
		_release_all()
		return [true, {}]
	var name := str(args.get("key", ""))
	var spec := Keys.spec(name)
	if spec.is_empty():
		return [false, "Unknown key %s" % name]
	var value := float(args.get("value", 1.0))
	var rate := bool(args.get("rate", false))
	var axis := Keys.is_axis(spec)
	if mode == "release":
		if _held.has(name):
			if not axis:
				_send_key(spec, false)
			elif spec.kind == "joy_axis":
				_send_key(spec, false)
			_held.erase(name)
		return [true, {}]
	if mode == "hold":
		if not _held.has(name):
			_held[name] = {"spec": spec}
			if not axis:
				_send_key(spec, true, value)
		elif spec.kind == "action" and _held[name].value != value:
			_send_key(spec, true, value)
		_held[name].value = value
		_held[name].rate = rate
		_held[name].renewed = _now()
		# a timed hold ("seconds"): let go after that much game time, without renewal, however late the brain is
		var seconds := float(args.get("seconds", 0.0))
		_held[name].until = game_time + seconds if seconds > 0.0 else 0.0
		return [true, {}]
	# a tap: an axis moves once; a key goes down, and up again a moment later, as a quick press does
	if axis:
		_send_key(spec, true, value)
		if spec.kind == "joy_axis":
			_send_key(spec, false)
		return [true, {}]
	_send_key(spec, true, value)
	var started := _now()
	var frame := Engine.get_process_frames()
	_pending.append({"id": id, "op": "key", "tick": func(now: float) -> bool:
		if now - started < KEY_TAP_SECONDS or Engine.get_process_frames() < frame + KEY_TAP_FRAMES:
			return false
		if not _held.has(name):
			_send_key(spec, false)
		respond(id, {})
		return true})
	return null


func _tick_keys(now: float, dt: float) -> void:
	if _held.is_empty():
		return
	for name in _held:
		var h: Dictionary = _held[name]
		var release_at := float(h.get("until", 0.0))
		if (game_time >= release_at) if release_at > 0.0 else (now - h.renewed > _lease):
			_expired.append(name)
		elif h.spec.kind == "mouse_axis" or h.spec.kind == "wheel_axis":
			_send_key(h.spec, true, h.value * dt if h.rate else h.value)
		elif h.spec.kind == "joy_axis":
			_send_key(h.spec, true, h.value)
	for name in _expired:
		var spec: Dictionary = _held[name].spec
		if not Keys.is_axis(spec) or spec.kind == "joy_axis":
			_send_key(spec, false)
		_held.erase(name)
	_expired.clear()


func _release_all() -> void:
	for name in _held:
		var h: Dictionary = _held[name]
		if not Keys.is_axis(h.spec) or h.spec.kind == "joy_axis":
			_send_key(h.spec, false)
	_held.clear()
	for a in _pressed_actions:
		if InputMap.has_action(a):
			Input.action_release(a)
	_pressed_actions.clear()


## `input`: an InputMap action by name ({"action": "jump", "value"?: {"X"..} or a number, "mode"}). press: down,
## then up a moment later; hold: down until release (no lease, as Unreal's continuous injection).
func input_op(id, args: Dictionary):
	var action := str(args.get("action", ""))
	if not InputMap.has_action(action):
		return [false, "No input action named %s" % action]
	var mode := str(args.get("mode", "press"))
	var v = args.get("value", 1.0)
	var strength := 1.0
	if typeof(v) == TYPE_DICTIONARY:
		strength = absf(float(v.get("X", v.get("x", 1.0))))
	elif typeof(v) in [TYPE_FLOAT, TYPE_INT]:
		strength = absf(float(v))
	var spec := {"kind": "action", "action": action}
	if mode == "release":
		_send_key(spec, false)
		return [true, {}]
	_send_key(spec, true, strength)
	if mode == "hold":
		return [true, {}]
	var started := _now()
	var frame := Engine.get_process_frames()
	_pending.append({"id": id, "op": "input", "tick": func(now: float) -> bool:
		if now - started < KEY_TAP_SECONDS or Engine.get_process_frames() < frame + KEY_TAP_FRAMES:
			return false
		_send_key(spec, false)
		respond(id, {})
		return true})
	return null


## The game's movement actions: [forward, back, left, right], the actions W, S, A, D are bound to (the arrow keys
## when those aren't bound), "" where none.
func _move_actions() -> Array:
	var out := []
	for pair in [["W", "Up"], ["S", "Down"], ["A", "Left"], ["D", "Right"]]:
		var a := Keys.action_for_key(pair[0])
		if a == "":
			a = Keys.action_for_key(pair[1])
		out.append(a)
	return out


## `move`: walk the player in a kit-frame direction for `seconds`, as its movement keys would (the actions bound to
## W/A/S/D, pressed with analog strengths that point the camera-relative input that way; the keys themselves when the
## game has no such actions). Answers {"moved": units across the floor, "via"}.
func move_op(id, args: Dictionary):
	var player: Node = world.player()
	if player == null:
		return [false, "No player"]
	var is3d := player is Node3D
	var d = args.get("direction", {})
	var scale := clampf(float(args.get("scale", 1.0)), 0.0, 1.0)
	var seconds := float(args.get("seconds", 0.35))
	var actions := _move_actions()
	var via := "keys" if actions.has("") else "actions"
	var start: Vector3 = player.global_position if is3d else Vector3(player.global_position.x, 0, player.global_position.y)
	var started := _now()
	var keys_down := {}
	var pid := player.get_instance_id()  # (never capture a node: it may be freed meanwhile)
	_pending.append({"id": id, "op": "move", "tick": func(now: float) -> bool:
		var p = instance_from_id(pid)
		var alive: bool = p != null and is_instance_valid(p) and p.is_inside_tree()
		if alive and now - started < seconds:
			var f := 0.0
			var r := 0.0
			if is3d:
				var want := Frame.from_kit_dir(d)
				want.y = 0.0
				if want.length() > 0.0001:
					want = want.normalized()
				var cam = world.camera3d()
				var basis: Basis = cam.global_basis if cam else p.global_basis
				var fwd := -basis.z
				fwd.y = 0.0
				var right := basis.x
				right.y = 0.0
				f = want.dot(fwd.normalized()) * scale
				r = want.dot(right.normalized()) * scale
			else:
				# 2D: kit X is screen right, kit Z screen up
				r = clampf(float(d.get("X", 0.0)), -1.0, 1.0) * scale
				f = clampf(float(d.get("Z", 0.0)), -1.0, 1.0) * scale
			_drive(f, r, actions, keys_down)
			return false
		_drive(0.0, 0.0, actions, keys_down)
		var end: Vector3 = start
		if alive:
			end = p.global_position if is3d else Vector3(p.global_position.x, 0, p.global_position.y)
		var moved := Vector2(end.x - start.x, end.z - start.z).length() * (Frame.SCALE if is3d else 1.0)
		respond(id, {"moved": snappedf(moved, 0.1), "via": via})
		return true})
	return null


func _drive(f: float, r: float, actions: Array, keys_down: Dictionary) -> void:
	for i in 4:
		var s: float = maxf(0.0, f if i == 0 else -f if i == 1 else -r if i == 2 else r)
		var a: String = actions[i]
		if a != "":
			if s > 0.01:
				Input.action_press(a, s)
				_pressed_actions[a] = true
			elif _pressed_actions.has(a):
				Input.action_release(a)
				_pressed_actions.erase(a)
		else:
			var down := s > 0.38
			if down != keys_down.get(i, false):
				keys_down[i] = down
				_send_key(Keys.spec(MOVE_KEYS[i]), down)


func watch_op(id, args: Dictionary):
	var st: Dictionary = sight.watch_start(args, rc.resolve)
	var seconds := float(args.get("seconds", 0.6))
	var started := _now()
	sight.watch_tick(st, game_time)
	_pending.append({"id": id, "op": "watch", "tick": func(now: float) -> bool:
		sight.watch_tick(st, game_time)
		if now - started < seconds:
			return false
		respond(id, sight.watch_finish(st))
		return true})
	return null
