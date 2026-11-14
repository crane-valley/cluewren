# Modified by Crane Valley: reduced to the link to the brain, starting and stopping play, and the relay to the game. The
# editor connects by itself once brain.json appears; runs, steering, memory, usage, the account, sign-in and codex ops are
# removed, and a restart without "fresh" stays in the running game; a restart never plays a scene under res://.godot/.
## The editor's link to the brain (hello host "editor"). The editor and the game are separate processes in Godot, so:
##  - this holds the one WebSocket to the brain (the brain expects one link per run, whatever the game does);
##  - the brain's `session` requests start, stop and restart the play session here (EditorInterface.play_main_scene /
##    play_custom_scene / stop_playing_scene); `levels` is answered here;
##  - every other request goes to the running game's autoload over the editor's debugger connection (debugger.gd), and
##    the game's answers and events come back the same way, passed on to the brain unchanged;
##  - with no game running, `context` and `ping` are answered here (session_running false) and the rest get an error;
##  - `record` is followed here: a recording goes on in the next play session of the same connection (each is a new
##    game process) unless the brain stopped it, `stop` is answered here, and the game's pictures come over the debugger
##    as JPEG bytes ("media" messages) that are made into `frame` messages and `capture` answers here.
@tool
extends Node

signal state_changed

const Link = preload("lib/link.gd")
const Codex = preload("lib/codex.gd")
const Keys = preload("lib/keys.gd")
const Media = preload("lib/media.gd")
const Discovery = preload("lib/discovery.gd")
const SESSION_TIMEOUT := 60.0
const AUTO_CONNECT_SECONDS := 2.0
const RESUME_META := &"cluewren_resume"
const BUSY_SLEEP_USEC := 1000

var debugger  # debugger.gd
var link = Link.new()
var run_active := false
var disconnected_by_user := false

var _game_session := -1
var _game_ready := false
var _game_gen := 0
var _attach_at := 0.0
var _attach_until := 0.0
var _forwarded := {}  # request ids in flight to the game
var _pending := []  # session waits: {"tick": Callable(now) -> bool}
var _heartbeat_at := 0.0
var _auto_connect_at := 0.0
var _shown_state := ""
var _detector_config := {}
var _summary_task := -1
var _last_played := ""
var _record = null  # the brain's `record` start args while it records (kept across play sessions; null: not recording)
var _record_frames := 0  # frames passed on since that start
var _record_next := 0  # the next frame's number (a new play session goes on from it)
var _record_starts := {}  # request id -> args of the `record` starts forwarded to the game, until it answers
var _quitting := false
var _editor_sleep := -1


func _ready() -> void:
	link.hello_fields = _hello
	link.on_frame = _on_brain_frame
	link.on_welcomed = _on_welcomed
	link.on_closed = _on_closed
	link.on_refused = _on_refused
	debugger.game_message.connect(_on_game_message)
	debugger.session_started.connect(_on_session_started)
	debugger.session_stopped.connect(_on_session_stopped)
	debugger.session_breaked.connect(_on_session_breaked)
	if Engine.has_meta(RESUME_META):
		var kept: Dictionary = Engine.get_meta(RESUME_META)
		Engine.remove_meta(RESUME_META)
		link.session = kept.session
		link.received = kept.received
		run_active = kept.run_active
		_record = kept.record
		_record_frames = kept.frames
		_record_next = kept.next
		link.open()


func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_CLOSE_REQUEST:
		_quitting = true


func _exit_tree() -> void:
	_set_busy(false)
	if _quitting or not link.connected:
		link.close()
	else:
		# The plugin was reloaded or disabled, not the editor closed: the same run goes on with the next instance.
		Engine.set_meta(RESUME_META, {"session": link.session, "received": link.received, "run_active": run_active,
			"record": _record, "frames": _record_frames, "next": _record_next})
		link.close_for_reload()
	if _summary_task >= 0:
		WorkerThreadPool.wait_for_task_completion(_summary_task)


func _now() -> float:
	return Time.get_ticks_msec() / 1000.0


func wants_connection() -> bool:
	return link.want


func state_text() -> String:
	if link.connected:
		return "Connected"
	if link.problem != "" and (link.want or link.refused_stamp >= 0):
		return link.problem
	return "Connecting" if link.want else "Disconnected"


func connect_to_brain() -> void:
	disconnected_by_user = false
	link.refused_stamp = -1
	link.open()
	state_changed.emit()


func disconnect_from_brain() -> void:
	disconnected_by_user = true
	link.close()
	link.problem = ""
	link.problem_code = 0
	run_active = false
	_set_busy(false)
	_record = null
	_detach()
	state_changed.emit()


## While the brain plays, the editor mustn't sleep between frames: unfocused it wakes ten times a second, and each request
## on its way to the game waits for that. _process holds this, the editor setting its own value at every focus change.
func _set_busy(busy: bool) -> void:
	if busy and _editor_sleep < 0:
		_editor_sleep = OS.low_processor_usage_mode_sleep_usec
		OS.low_processor_usage_mode_sleep_usec = BUSY_SLEEP_USEC
	elif not busy and _editor_sleep >= 0:
		OS.low_processor_usage_mode_sleep_usec = _editor_sleep
		_editor_sleep = -1


func _hello() -> Dictionary:
	var cfg := ConfigFile.new()
	var version := "0.1.0"
	if cfg.load("res://addons/cluewren/plugin.cfg") == OK:
		version = str(cfg.get_value("plugin", "version", version))
	return {"plugin_version": version, "engine": "godot", "engine_version": Engine.get_version_info().string,
		"project": str(ProjectSettings.get_setting("application/config/name", "")), "host": "editor",
		# the games it starts can be captured (a headless one answers record and capture with 409); `levels`, and
		# `session` playing the `map` asked for
		"features": ["timed_hold", "media", "levels"]}


# --- the frame --------------------------------------------------------------------------------------

func _process(_delta: float) -> void:
	var now := _now()
	if _editor_sleep >= 0:
		OS.low_processor_usage_mode_sleep_usec = BUSY_SLEEP_USEC
	link.poll()
	if not link.want:
		_auto_connect(now)
	# (the link changes its problem as it retries, with no event of its own: the dock shows why it doesn't connect)
	if state_text() != _shown_state:
		_shown_state = state_text()
		state_changed.emit()
	# attach to a game that started, until it answers (its autoload may not be listening yet); only for a brain that has
	# proved itself, as Unity and Unreal leave a game alone until then
	if link.connected and _game_session >= 0 and not _game_ready and now < _attach_until and now >= _attach_at:
		_attach_at = now + 0.5
		var cfg := {"detectors": _detector_config}
		if _record != null:
			cfg.record = _record.merged({"n": _record_next}, true)  # (the recording goes on in this play session)
		debugger.send(_game_session, "attach", [JSON.stringify(cfg)])
	var i := 0
	while i < _pending.size():
		if _pending[i].tick.call(now):
			_pending.remove_at(i)
		else:
			i += 1
	if link.connected and not _game_ready and now - _heartbeat_at >= 1.0:
		_heartbeat_at = now
		link.send_text(JSON.stringify({"type": "events", "events": [{"kind": "heartbeat", "t": null, "fps": Engine.get_frames_per_second(), "paused": false}], "t": null}))


## A brain that failed its proof, or that the brain's error turned away, isn't asked again: only a new brain.json is.
func _auto_connect(now: float) -> void:
	if now < _auto_connect_at or disconnected_by_user:
		return
	_auto_connect_at = now + AUTO_CONNECT_SECONDS
	var path := Discovery.file_path()
	if path == "" or not FileAccess.file_exists(path):
		return
	if link.refused_stamp >= 0 and FileAccess.get_modified_time(path) == link.refused_stamp:
		return
	link.refused_stamp = -1
	link.open()
	state_changed.emit()


func _on_welcomed(resumed: bool) -> void:
	state_changed.emit()
	_set_busy(true)
	if not resumed:
		run_active = false
		_pending.clear()
		_forwarded.clear()
		_record = null
		_record_starts.clear()
		_game_ready = false  # (a game already playing is attached again, for this brain)
	if _game_session >= 0 and not _game_ready:
		_attach_at = 0.0
		_attach_until = _now() + SESSION_TIMEOUT


func _on_closed(was_connected: bool) -> void:
	if not was_connected:
		return
	_set_busy(false)
	if link.session == "":
		run_active = false
		_record = null
		_record_starts.clear()
		_detach()
	elif _game_session >= 0:
		debugger.send(_game_session, "release", [])
	state_changed.emit()


func _on_refused() -> void:
	push_warning("Cluewren: " + link.problem)
	state_changed.emit()


func _on_brain_frame(frame: Dictionary, text: String) -> void:
	match str(frame.get("type", "")):
		"request":
			_on_request(frame, text)
		"run_started":
			run_active = true
		"result":
			run_active = false
			if _game_ready:
				debugger.send(_game_session, "release", [])
		"status":
			print("Cluewren: %s" % frame.get("text", ""))
		"finding":
			var f = frame.get("finding", {})
			if typeof(f) == TYPE_DICTIONARY:
				print("Cluewren: finding: %s" % f.get("summary", ""))


# --- requests from the brain ------------------------------------------------------------------------

func _respond(id, result) -> void:
	link.send_answer(JSON.stringify({"type": "response", "id": id, "ok": true, "result": result, "t": null}))


func _respond_error(id, error: String, code := 400) -> void:
	link.send_answer(JSON.stringify({"type": "response", "id": id, "ok": false, "status": code, "error": error, "t": null}))


func _on_request(frame: Dictionary, text: String) -> void:
	var id = frame.get("id")
	if typeof(id) == TYPE_FLOAT:
		id = int(id)
	var op := str(frame.get("op", ""))
	var args = frame.get("args", {})
	if typeof(args) != TYPE_DICTIONARY:
		args = {}
	match op:
		"session":
			_session(id, args, text)
			return
		"levels":
			_respond(id, _levels())
			return
		"detectors":
			_detector_config = args
		"record":
			var action := str(args.get("action", ""))
			if action == "stop":
				# (always succeeds; answered here, the recording spanning play sessions)
				_respond(id, {"recording": false, "frames": _record_frames if _record != null else 0})
				_record = null
				if _game_ready:
					debugger.send(_game_session, "record_stop", [])
				return
			if action != "start":
				_respond_error(id, "record wants action start or stop")
				return
			if not _game_ready:
				_respond_error(id, "no picture: no game is running", 409)
				return
			_record_starts[id] = args
		"capture":
			if not _game_ready:
				_respond_error(id, "no picture: no game is running", 409)
				return
	if _forward(id, text):
		return
	match op:
		"context":
			_respond(id, _editor_context())
		"ping":
			_respond(id, {"game_time": null, "wall_time": Time.get_unix_time_from_system()})
		"detectors":
			_respond(id, {})
		_:
			_respond_error(id, "No game is running (start one with session start)", 409)


func _forward(id, text: String) -> bool:
	if _game_ready and debugger.send(_game_session, "request", [text]):
		_forwarded[id] = true
		return true
	return false


func _editor_context() -> Dictionary:
	var controls := []
	for p in ProjectSettings.get_property_list():
		var n: String = p.name
		if not n.begins_with("input/") or Keys.is_default_ui(n.substr(6)):
			continue
		var v = ProjectSettings.get_setting(n)
		var keys := []
		if typeof(v) == TYPE_DICTIONARY:
			for ev in v.get("events", []):
				if ev is InputEvent:
					var k := Keys.unreal_name(ev)
					if k != "" and not keys.has(k):
						keys.append(k)
		controls.append({"name": n.substr(6), "action": n.substr(6), "keys": keys, "digital": true})
	return {"world": "", "scene": "", "character": "", "controller": "", "input_subsystem": "", "camera_manager": "", "camera": "",
		"game_name": str(ProjectSettings.get_setting("application/config/name", "")), "host": "editor", "engine": "godot",
		"engine_version": Engine.get_version_info().string, "session_running": false, "paused": false, "game_time": 0.0,
		"project_dir": Discovery.project_dir(), "controls": controls}


# --- the play session -------------------------------------------------------------------------------

## `levels`: the project's scenes, what a start without a map plays (the main scene) and the one playing.
func _levels() -> Dictionary:
	var playing := EditorInterface.get_playing_scene() if EditorInterface.is_playing_scene() else ""
	return {"levels": Codex.scenes(), "open": str(ProjectSettings.get_setting("application/run/main_scene", "")), "playing": playing}


func _play(scene: String) -> void:
	var main := str(ProjectSettings.get_setting("application/run/main_scene", ""))
	if scene == "" or scene == main:
		EditorInterface.play_main_scene()
	else:
		EditorInterface.play_custom_scene(scene)


func _write_summary() -> void:
	if _summary_task >= 0:
		if not WorkerThreadPool.is_task_completed(_summary_task):
			return
		WorkerThreadPool.wait_for_task_completion(_summary_task)
	_summary_task = WorkerThreadPool.add_task(Codex.write_summary, false, "Cluewren summary")


func _session(id, args: Dictionary, text: String) -> void:
	var action := str(args.get("action", "status"))
	var scene := ""
	if str(args.get("map", "")) != "":
		scene = Codex.find_scene(str(args.map))
		if scene == "":
			_respond_error(id, "No scene %s" % args.map)
			return
	match action:
		"start":
			_write_summary()
			if _game_ready:
				_wait_ready(id)
				return
			if not EditorInterface.is_playing_scene():
				_play(scene)
				_last_played = scene  # (what a restart without a map plays again)
			_wait_ready(id)
		"stop":
			if not EditorInterface.is_playing_scene():
				_respond(id, {"running": false})
				return
			EditorInterface.stop_playing_scene()
			var started := _now()
			_pending.append({"tick": func(now: float) -> bool:
				if not EditorInterface.is_playing_scene():
					_respond(id, {"running": false})
					return true
				if now - started > SESSION_TIMEOUT:
					_respond_error(id, "Timed out waiting for the play session to stop", 500)
					return true
				return false})
		"restart":
			# As Unity's restart: the running game reopens the scene, which is far quicker than a new process. "fresh"
			# asks for everything to start over: a new game process, autoloads and all.
			if not args.get("fresh", false) and _game_ready:
				if scene != "":
					_last_played = scene
				if _forward(id, text):
					return
			var target := scene
			if target == "":
				target = EditorInterface.get_playing_scene() if EditorInterface.is_playing_scene() else _last_played
			if Codex.data_path(target):
				_respond_error(id, "%s is refused: nothing under res://.godot/ is opened as a scene" % target)
				return
			_last_played = target
			var gen := _game_gen
			var phase := {"stopping": EditorInterface.is_playing_scene()}
			if phase.stopping:
				EditorInterface.stop_playing_scene()
			else:
				_play(target)
			var started := _now()
			_pending.append({"tick": func(now: float) -> bool:
				if phase.stopping:
					if not EditorInterface.is_playing_scene():
						phase.stopping = false
						_play(target)
					elif now - started > SESSION_TIMEOUT:
						_respond_error(id, "Timed out waiting for the play session to stop", 500)
						return true
					return false
				if _game_ready and _game_gen > gen:
					_respond(id, {"running": true})
					return true
				if now - started > SESSION_TIMEOUT:
					_respond_error(id, "Timed out waiting for the play session", 500)
					return true
				return false})
		_:
			_respond(id, {"running": _game_ready})


func _wait_ready(id) -> void:
	var started := _now()
	_pending.append({"tick": func(now: float) -> bool:
		if _game_ready and (_summary_task < 0 or WorkerThreadPool.is_task_completed(_summary_task)):
			_respond(id, {"running": true})
			return true
		if now - started > SESSION_TIMEOUT:
			_respond_error(id, "Timed out waiting for the play session", 500)
			return true
		return false})


# --- the game (over the debugger) -------------------------------------------------------------------

func _on_session_started(session_id: int) -> void:
	_game_session = session_id
	_game_ready = false
	_attach_at = 0.0
	_attach_until = _now() + SESSION_TIMEOUT
	if EditorInterface.is_playing_scene():
		_last_played = EditorInterface.get_playing_scene()


func _on_session_stopped(session_id: int) -> void:
	if session_id != _game_session:
		return
	var was := _game_ready
	_game_ready = false
	_game_session = -1
	for id in _forwarded:
		_respond_error(id, "the game stopped", 500)
	_forwarded.clear()
	if was and link.connected:
		link.send_text(JSON.stringify({"type": "events", "events": [{"kind": "session", "t": null, "running": false}], "t": null}))


func _on_session_breaked(session_id: int) -> void:
	# A script error stopped the game in the debugger. While the brain plays, it goes on (the error is reported to the
	# brain as a log event and a log_error finding); otherwise it stays stopped for the developer.
	if link.connected and session_id == _game_session:
		debugger.resume(session_id)


func _detach() -> void:
	if _game_session >= 0:
		debugger.send(_game_session, "detach", [])


func _on_game_message(message: String, data: Array, session_id: int) -> void:
	if session_id != _game_session:
		return
	match message:
		"ready":
			if not _game_ready:
				_game_ready = true
				_game_gen += 1
				if link.connected:
					link.send_text(JSON.stringify({"type": "events", "events": [{"kind": "session", "t": 0.0, "running": true}], "t": 0.0}))
				else:
					_detach()  # nobody to play for: let the game be
		"frame":
			if data.size() < 3:
				return
			var id = data[0]
			if id == -1:
				link.send_text(data[2])
				return
			_forwarded.erase(id)
			if _record_starts.has(id):
				if data[1]:
					_record = _record_starts[id]
					_record_frames = 0
					_record_next = 0
				_record_starts.erase(id)
			link.send_answer(data[2])
		"media":
			_on_game_media(data)


## A picture from the game: [head as JSON, the JPEG's bytes]. A recording's frame becomes a `frame` message, a still
## the answer to its `capture` (the base64 and the text are made here, in the editor, not in the game).
func _on_game_media(data: Array) -> void:
	if data.size() < 2 or typeof(data[1]) != TYPE_PACKED_BYTE_ARRAY:
		return
	var head = JSON.parse_string(str(data[0]))
	if typeof(head) != TYPE_DICTIONARY:
		return
	var b64 := Marshalls.raw_to_base64(data[1])
	match str(head.get("kind", "")):
		"frame":
			if _record == null:
				return  # (stopped meanwhile)
			var n := int(head.get("n", 0))
			_record_frames += 1
			_record_next = maxi(_record_next, n + 1)
			link.send_text(Media.frame_text({"n": n, "t": head.get("t"), "wall": head.get("wall", 0.0),
				"width": int(head.get("width", 0)), "height": int(head.get("height", 0))}, b64), true)
		"still":
			var id = head.get("id")
			id = int(id) if typeof(id) == TYPE_FLOAT else id
			_forwarded.erase(id)
			link.send_answer(Media.response_text(id, {"width": int(head.get("width", 0)), "height": int(head.get("height", 0)),
				"t": head.get("t")}, b64, head.get("t")), true)
