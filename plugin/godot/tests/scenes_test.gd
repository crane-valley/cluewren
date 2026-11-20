extends SceneTree

const Export = preload("res://addons/cluewren/lib/export.gd")
const TEXT := "res://tests/cluewren_scene_map.tscn"
const BINARY := "res://tests/cluewren_scene_map_binary.scn"
const SHARED := "res://tests/cluewren_scene_map.scn"


class Editor extends "res://addons/cluewren/editor_bridge.gd":
	var playing := true
	var launched := ""
	var forwarded := ""
	var answer := {}
	var error := ""
	var status := 0

	func _is_playing_scene() -> bool:
		return playing

	func _play(scene: String) -> void:
		launched = scene

	func _write_summary() -> void:
		pass

	func _forward(_id, text: String) -> bool:
		forwarded = text
		return true

	func _respond(_id, result) -> void:
		answer = result

	func _respond_error(_id, message: String, code := 400) -> void:
		error = message
		status = code


class Game extends "res://addons/cluewren/game.gd":
	var answer := {}
	var error := ""
	var status := 0

	func _ready() -> void:
		set_process(false)

	func respond(_id, result) -> void:
		answer = result

	func respond_error(_id, message: String, code := 400) -> void:
		error = message
		status = code


var passes := 0
var failures := 0


func _initialize() -> void:
	_run_standalone.call_deferred()


func _run_standalone() -> void:
	run(self)
	print("%d passed, %d failed" % [passes, failures])
	quit(0 if passes > 0 and failures == 0 else 1)


func expect(section: String, label: String, ok: bool) -> void:
	if ok:
		passes += 1
	else:
		failures += 1
		printerr("FAIL %s %s" % [section, label])


static func run(test) -> void:
	for path in [TEXT, BINARY, SHARED]:
		if FileAccess.file_exists(path):
			test.expect("scenes", "fixture path is available: " + path, false)
			return
	var node := Node.new()
	node.name = "SceneMapFixture"
	var packed := PackedScene.new()
	var packed_ok := packed.pack(node) == OK
	node.free()
	var saved := packed_ok
	for path in [TEXT, BINARY, SHARED]:
		saved = ResourceSaver.save(packed, path) == OK and saved
	test.expect("scenes", "text and binary scenes saved", saved)
	if saved:
		var listed := Export.scenes()
		test.expect("scenes", "enumerates both formats", TEXT in listed and BINARY in listed and SHARED in listed)
		test.expect("scenes", "finds a binary basename", Export.find_scene("cluewren_scene_map_binary") == BINARY)
		test.expect("scenes", "finds an explicit binary filename", Export.find_scene("CLUEWREN_SCENE_MAP.SCN") == SHARED)
		test.expect("scenes", "keeps text scene preference for shared basenames", Export.find_scene("cluewren_scene_map") == TEXT)
		var summary := Export.summary_text()
		test.expect("scenes", "outlines only text scenes", TEXT in summary and "SceneMapFixture" in summary
			and BINARY not in summary and SHARED not in summary)
		_editor_start(test)
		_game_start(test)
	for path in [TEXT, BINARY, SHARED]:
		if FileAccess.file_exists(path):
			DirAccess.remove_absolute(path)


static func _editor_start(test) -> void:
	var args := {"action": "start", "map": "cluewren_scene_map_binary"}
	var request := JSON.stringify({"type": "request", "id": 1, "op": "session", "args": args})
	var editor := Editor.new()
	editor._on_request(JSON.parse_string(request), request)
	var tick: Callable = editor._pending.pop_back().tick
	test.expect("scenes", "waits for the debugger without relaunching", not tick.call(editor._now())
		and editor.launched == "" and editor.forwarded == "")
	editor._game_ready = true
	test.expect("scenes", "checks the running scene after attachment", tick.call(editor._now())
		and editor.forwarded == request and editor.answer.is_empty())
	editor.forwarded = ""
	editor._session(2, {"action": "start"}, "")
	tick = editor._pending.pop_back().tick
	test.expect("scenes", "mapless start keeps the running game", tick.call(editor._now())
		and editor.forwarded == "" and editor.answer.get("running", false))
	editor._game_ready = false
	editor.playing = false
	editor._on_request(JSON.parse_string(request), request)
	test.expect("scenes", "launches the requested binary map when stopped", editor.launched == BINARY)
	tick = editor._pending.pop_back().tick
	test.expect("scenes", "names the requested scene when attachment times out", tick.call(editor._now() + editor.SESSION_TIMEOUT + 1.0)
		and editor.status == 500 and BINARY in editor.error)
	editor.free()


static func _game_start(test) -> void:
	var game := Game.new()
	test.root.add_child(game)
	var args := {"action": "start", "map": "cluewren_scene_map_binary"}
	test.expect("scenes", "explicit game start checks the current scene", game.dispatch(1, "session", args) == null)
	var tick: Callable = game._pending.pop_back().tick
	test.expect("scenes", "waits for the first scene", not tick.call(game._now()))
	var scene: Node = load(TEXT).instantiate()
	test.root.add_child(scene)
	test.current_scene = scene
	test.expect("scenes", "running the wrong scene returns actionable conflict", tick.call(game._now())
		and game.status == 409 and TEXT in game.error and BINARY in game.error
		and "retry start_run with level" in game.error and game.answer.is_empty())
	test.current_scene = null
	scene.free()
	scene = load(BINARY).instantiate()
	test.root.add_child(scene)
	test.current_scene = scene
	game.dispatch(2, "session", args)
	tick = game._pending.pop_back().tick
	test.expect("scenes", "running the requested scene succeeds", tick.call(game._now()) and game.answer.get("running", false))
	test.current_scene = null
	scene.free()
	game.free()
