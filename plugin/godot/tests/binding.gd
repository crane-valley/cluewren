## How an editor meets the brain (lib/discovery.gd, lib/link.gd): its advert, the checks an invite passes and their
## order, and the handshake against a brain played here. LOCALAPPDATA points at a folder of the test's meanwhile, so
## nothing is advertised to, or taken from, a brain running on this machine.
extends RefCounted

const Discovery = preload("res://addons/cluewren/lib/discovery.gd")
const Handshake = preload("res://addons/cluewren/lib/handshake.gd")
const Link = preload("res://addons/cluewren/lib/link.gd")
const EditorBridge = preload("res://addons/cluewren/editor_bridge.gd")
const Game = preload("res://addons/cluewren/game.gd")
const Media = preload("res://addons/cluewren/lib/media.gd")
const Export = preload("res://addons/cluewren/lib/export.gd")

const KNOWN_MAC := "b0e5b16410e69ae2e7ab99cb08045c3b0f6ac8cf250e9898d151a05c964a31a3"
const NO_PROCESS := 2147483646
const ADVERT_FIELDS := ["engine", "engine_version", "host", "id", "pid", "plugin_version", "project_dir", "protocol_max",
	"protocol_min"]


static func run(t) -> void:
	hello_mac(t)
	urls(t)
	refusal_texts(t)
	a_project_at_a_drive_root(t)
	var saved := OS.get_environment("LOCALAPPDATA")
	var root := OS.get_environment("TEMP").path_join(_own_name("cluewren-godot-test"))
	if DirAccess.dir_exists_absolute(root) or FileAccess.file_exists(root):
		t.expect("binding", "a folder of the test's own", false, root)
		return
	OS.set_environment("LOCALAPPDATA", root)
	if Discovery.folder() == root.path_join("Cluewren"):
		the_advert(t)
		the_editor_attaches_only_for_a_proved_brain(t)
		a_new_session_gets_no_answer_owed_to_the_one_before(t)
		a_new_session_drops_the_games_work(t)
		a_map_rooted_elsewhere_is_no_scene(t)
		the_invite_is_checked_in_order(t)
		the_handshake(t)
	else:
		t.expect("binding", "the rendezvous moved for the test", false, Discovery.folder())
	OS.set_environment("LOCALAPPDATA", saved)
	_remove(root)


static func _range(from: int) -> PackedByteArray:
	var b := PackedByteArray()
	for i in 32:
		b.append(from + i)
	return b


static func hello_mac(t) -> void:
	var mac := Handshake.hello_mac(_range(0), _range(32), "C:\\Users\\Dev\\Projects\\Gr\u00f6\u00dfe")
	t.expect("binding", "the hello's MAC", mac.hex_encode() == KNOWN_MAC, mac.hex_encode())


static func urls(t) -> void:
	for url in ["ws://127.0.0.1:5000/v1/bridge", "ws://[::1]:5000/v1/bridge"]:
		t.expect("binding", "%s is this machine" % url, Discovery.check_url(url) == "", Discovery.check_url(url))
	for url in ["wss://127.0.0.1:5000/v1/bridge", "ws://localhost:5000/v1/bridge", "ws://127.0.0.1.example.com:5000/",
			"ws://user@127.0.0.1:5000/v1/bridge", "ws://example.com:80/v1/bridge", "http://127.0.0.1:5000/", ""]:
		t.expect("binding", "\"%s\" is refused" % url, Discovery.check_url(url) != "")


static func refusal_texts(t) -> void:
	for row in [
			["protocol", "The cluewren command and this plugin are of different releases: install the same release of both"],
			["not_authenticated", "The brain did not accept this editor's proof of the invite"],
			["project", "The run is for another project than this editor's"],
			["resume", "The session this editor tried to resume belongs to another project or engine"],
			["hello", "The brain refused the connection (hello)"],
			["proof", "The brain refused the connection (proof)"],
			["anything_else", "The brain refused the connection (anything_else)"]]:
		t.expect("binding", "the text for refusal %s" % row[0], Link.refusal_text(row[0]) == row[1], Link.refusal_text(row[0]))
	var link = Link.new()
	link.ws = WebSocketPeer.new()
	link.url = "ws://127.0.0.1:1/v1/bridge"
	link._take(JSON.stringify({"type": "error", "code": "<b>not a code</b>", "message": "words"}))
	t.expect("binding", "an error frame without a plain code is no refusal by code",
		link.problem.contains("it didn't prove it is this machine's brain") and not link.problem.contains("not a code"), link.problem)
	link.ws = WebSocketPeer.new()
	link._take(JSON.stringify({"type": "error", "code": "busy_2", "message": "words"}))
	t.expect("binding", "a code with digits is a code", link.problem == "Refused: The brain refused the connection (busy_2)", link.problem)


static func a_project_at_a_drive_root(t) -> void:
	t.expect("binding", "a drive root keeps its separator", Discovery._dir_of("C:/") == "C:/", Discovery._dir_of("C:/"))
	t.expect("binding", "a folder loses its trailing separator", Discovery._dir_of("C:/Games/Foo/") == "C:/Games/Foo")


class FakeDebugger extends RefCounted:
	signal game_message(message: String, data: Array, session_id: int)
	signal session_started(session_id: int)
	signal session_stopped(session_id: int)
	signal session_breaked(session_id: int)
	var sent := []

	func send(_session_id: int, message: String, _data: Array) -> bool:
		sent.append(message)
		return true

	func resume(_session_id: int) -> void:
		pass


static func _bridge(t, fake: FakeDebugger):
	var bridge = EditorBridge.new()
	bridge.debugger = fake
	t.root.add_child(bridge)
	return bridge


static func _read(path: String):
	var f := FileAccess.open(path, FileAccess.READ) if FileAccess.file_exists(path) else null
	return JSON.parse_string(f.get_as_text()) if f != null else null


static func the_advert(t) -> void:
	var path := Discovery.advert_path()
	var fake := FakeDebugger.new()
	var bridge = _bridge(t, fake)
	var first = _read(path)
	if typeof(first) != TYPE_DICTIONARY:
		t.expect("advert", "written when the plugin enters the tree", false, path)
		bridge.free()
		return
	var fields: Array = first.keys()
	fields.sort()
	t.expect("advert", "its fields and no other", fields == ADVERT_FIELDS, fields)
	t.expect("advert", "pid", first.get("pid") == OS.get_process_id(), first)
	t.expect("advert", "id is 32 lowercase hex characters",
		RegEx.create_from_string("^[0-9a-f]{32}$").search(str(first.get("id"))) != null, first)
	t.expect("advert", "engine", first.get("engine") == "godot" and first.get("engine_version") == Engine.get_version_info().string, first)
	t.expect("advert", "plugin_version", first.get("plugin_version") == Discovery.plugin_version() and Discovery.plugin_version() != "", first)
	t.expect("advert", "protocol", first.get("protocol_min") == 2 and first.get("protocol_max") == 2, first)
	t.expect("advert", "project_dir is the hello's", first.get("project_dir") == Discovery.project_dir(), first)
	t.expect("advert", "host", first.get("host") == "editor", first)
	bridge.free()
	t.expect("advert", "stays when the plugin leaves to be loaded again", FileAccess.file_exists(path))
	DirAccess.remove_absolute(path)
	bridge = _bridge(t, fake)
	var second = _read(path)
	t.expect("advert", "written again with the same id by the plugin loaded again",
		typeof(second) == TYPE_DICTIONARY and second.get("id") == first.get("id"), second)
	bridge.notification(Node.NOTIFICATION_WM_CLOSE_REQUEST)
	bridge.free()
	t.expect("advert", "removed when the editor closes", not FileAccess.file_exists(path))
	t.expect("advert", "a game's is written", Discovery.write_advert("game") == "")
	var game = _read(path)
	t.expect("advert", "a game says so", typeof(game) == TYPE_DICTIONARY and game.get("host") == "game" and
		game.get("id") == first.get("id"), game)
	Discovery.remove_advert()


## A game played in an editor no brain has proved itself to is left alone, as on Unity and Unreal.
static func the_editor_attaches_only_for_a_proved_brain(t) -> void:
	var fake := FakeDebugger.new()
	var bridge = _bridge(t, fake)
	bridge.link.close()  # (it stands still: this is about the attach alone)
	bridge._game_session = 1
	bridge._attach_until = Time.get_ticks_msec() / 1000.0 + 60.0
	bridge._process(0.0)
	t.expect("binding", "no attach while no brain has proved itself", not "attach" in fake.sent, fake.sent)
	bridge.link.connected = true
	bridge._on_welcomed(false)
	bridge._process(0.0)
	t.expect("binding", "an attach once a brain has proved itself", "attach" in fake.sent, fake.sent)
	bridge.link.connected = false
	bridge.notification(Node.NOTIFICATION_WM_CLOSE_REQUEST)
	bridge.free()


## Request ids count from 1 in each session: what the game owed the session before must not answer this one's.
static func a_new_session_gets_no_answer_owed_to_the_one_before(t) -> void:
	var fake := FakeDebugger.new()
	var bridge = _bridge(t, fake)
	bridge.link.close()
	bridge.link.want = true  # (answers are held for the session, the socket standing still)
	bridge._game_session = 1
	bridge._game_ready = true
	bridge._forward(5, JSON.stringify({"type": "request", "id": 5, "op": "watch", "args": {}}))
	bridge.link.session = "s2"
	bridge._on_welcomed(false)
	t.expect("binding", "a new session detaches the game from the one before", "detach" in fake.sent, fake.sent)
	bridge._game_ready = true
	bridge._on_game_message("frame", [5, true, JSON.stringify({"type": "response", "id": 5, "ok": true})], 1)
	bridge._on_game_message("media", [JSON.stringify({"kind": "still", "id": 5}), PackedByteArray([1])], 1)
	t.expect("binding", "the game's answers owed to the session before are not passed on", bridge.link._held.is_empty(),
		bridge.link._held)
	bridge._forward(6, JSON.stringify({"type": "request", "id": 6, "op": "ping", "args": {}}))
	bridge._on_game_message("frame", [6, true, JSON.stringify({"type": "response", "id": 6, "ok": true})], 1)
	t.expect("binding", "an answer to this session's request is", bridge.link._held.size() == 1 and
		bridge.link._held[0].contains("\"id\":6"), bridge.link._held)
	bridge.link.close()
	bridge.notification(Node.NOTIFICATION_WM_CLOSE_REQUEST)
	bridge.free()


class Recorder extends Node:
	var seen := []

	func _input(event: InputEvent) -> void:
		if event is InputEventKey:
			seen.append("key %s %s" % [OS.get_keycode_string(event.keycode), "down" if event.pressed else "up"])
		elif event is InputEventAction:
			seen.append("action %s %s" % [event.action, "down" if event.pressed else "up"])


static func a_new_session_drops_the_games_work(t) -> void:
	var had_home := DirAccess.dir_exists_absolute(Export.HOME)
	var game = Game.new()
	t.root.add_child(game)
	game._begin()
	game.relay = true
	game.active = true
	var recorder := Recorder.new()
	t.root.add_child(recorder)
	game.dispatch(3, "watch", {"objects": [], "seconds": 30.0})
	game.dispatch(4, "key", {"key": "W", "mode": "tap"})
	game.dispatch(5, "input", {"action": "ui_accept", "mode": "tap"})
	game.dispatch(6, "input", {"action": "ui_cancel", "mode": "hold"})
	var down := [Input.is_key_pressed(KEY_W), Input.is_action_pressed("ui_accept"), Input.is_action_pressed("ui_cancel")]
	t.expect("binding", "a tap and a hold are down while the session goes on", down == [true, true, true], down)
	t.expect("binding", "the game's _input sees them go down", recorder.seen == ["key W down", "action ui_accept down",
		"action ui_cancel down"], recorder.seen)
	recorder.seen.clear()
	game._on_editor_message("detach", [])
	t.expect("binding", "a game detached drops what it owed the session", game._pending.is_empty(), game._pending)
	down = [Input.is_key_pressed(KEY_W), Input.is_action_pressed("ui_accept"), Input.is_action_pressed("ui_cancel")]
	t.expect("binding", "and lets go of what it held down, a tap cut short included", down == [false, false, false], down)
	var released := recorder.seen.duplicate()
	released.sort()
	t.expect("binding", "the game's _input sees each of them go up", released == ["action ui_accept up",
		"action ui_cancel up", "key W up"], recorder.seen)
	recorder.free()
	game.relay = false
	game.link = Link.new()
	game.link.want = true
	game.link.session = "s1"
	game.media._stills.append(_still(4))
	game.link.session = "s2"
	game._on_welcomed(false)
	game.media._stills.append(_still(9))
	game.media.tick(Time.get_ticks_msec() / 1000.0)
	var held: PackedStringArray = game.link._held
	t.expect("binding", "a capture of the session before answers nobody in the next", held.size() == 1 and
		held[0].contains("\"id\":9") and not held[0].contains("\"id\":4"), held)
	# (a node is locked while its export runs on a worker, and can't be freed until it is over)
	while not WorkerThreadPool.is_task_completed(game._summary_task):
		OS.delay_msec(10)
	game.free()
	if not had_home:
		for p in [Export.SUMMARY, Export.SUMMARY.get_base_dir(), Export.HOME + "/.gitignore", Export.HOME]:
			DirAccess.remove_absolute(p)


## A name of the test's own: it never writes over, or removes, a file it didn't make.
static func _own_name(stem: String) -> String:
	return "%s-%d-%08x" % [stem, OS.get_process_id(), randi()]


## Writes a scene where none is, or says why not.
static func _new_scene(path: String) -> String:
	if FileAccess.file_exists(path) or DirAccess.dir_exists_absolute(path):
		return "%s is there already" % path
	var f := FileAccess.open(path, FileAccess.WRITE)
	if f == null:
		return "%s can't be written (error %d)" % [path, FileAccess.get_open_error()]
	f.store_string("[gd_scene format=3]\n\n[node name=\"Level\" type=\"Node\"]\n")
	f.close()
	return ""


## A map comes from harness code: a path rooted elsewhere, or with "." or ".." in it, is no scene, whatever its file is
## called, and is not opened.
static func a_map_rooted_elsewhere_is_no_scene(t) -> void:
	var level := _own_name("rc_level")
	var file := level + ".tscn"
	var scene := "res://tests/" + file
	var outside := ProjectSettings.globalize_path("res://").get_base_dir().get_base_dir().path_join(file)
	var made := []
	for path in [scene, outside]:
		var why := _new_scene(path)
		t.expect("binding", "a scene of the test's own at %s" % path.get_file(), why == "", why)
		if why == "":
			made.append(path)
	if made.size() < 2:
		for path in made:
			DirAccess.remove_absolute(path)
		return
	t.expect("binding", "a scene is found by its name", Export.find_scene(level) == scene, Export.find_scene(level))
	var game = Game.new()
	t.root.add_child(game)
	game._begin()
	var maps := ["\\\\server\\share\\" + file, "//server/share/" + file, "C:\\elsewhere\\" + file, "C:" + file,
		"\\\\.\\C:\\elsewhere\\" + file, "res://../" + file, "res://tests/../../" + file, "res://./tests/" + file,
		"res://tests//" + file]
	for map in maps:
		var r = game.session(1, {"action": "restart", "map": map})
		t.expect("binding", "a restart's map %s is no scene" % map, r is Array and not r[0] and r.size() > 2 and r[2] == 404, r)
	game.free()
	var fake := FakeDebugger.new()
	var bridge = _bridge(t, fake)
	bridge.link.close()
	bridge.link.want = true
	bridge.link.session = "s1"
	bridge._session(2, {"action": "start", "map": maps[0]}, "")
	t.expect("binding", "an editor's start with a map %s is no scene" % maps[0], bridge.link._held.size() == 1 and
		bridge.link._held[0].contains("\"status\":404"), bridge.link._held)
	bridge.link.close()
	bridge.notification(Node.NOTIFICATION_WM_CLOSE_REQUEST)
	bridge.free()
	t.expect("binding", "nothing is loaded for such a map", not ResourceLoader.has_cached(scene) and
		not ResourceLoader.has_cached("res://../" + file))
	for path in made:
		DirAccess.remove_absolute(path)


## A capture that is over: its picture failed, so it answers without one.
static func _still(id: int):
	var job = Media.Job.new()
	job.still = true
	job.id = id
	job.set_failed("no picture in the test")
	return job


## "" once this user owns each path, as discovery.gd checks it, else why not. What an elevated administrator writes is
## owned by the Administrators group (the hosted CI runner), which the check rightly refuses.
static func _own(paths: Array) -> String:
	var quoted := PackedStringArray()
	for p in paths:
		quoted.append("'%s'" % str(p).replace("/", "\\").replace("'", "''"))
	var script := ("$ErrorActionPreference='Stop';$me=[Security.Principal.WindowsIdentity]::GetCurrent().User;" +
		"foreach($p in @(%s)){" % ",".join(quoted) +
		"if((Get-Item -LiteralPath $p -Force).GetAccessControl('Owner').GetOwner([Security.Principal.SecurityIdentifier]) -ne $me){" +
		"& \"$env:SystemRoot\\System32\\icacls.exe\" $p /setowner ('*'+$me.Value) /q | Out-Null;" +
		"if((Get-Item -LiteralPath $p -Force).GetAccessControl('Owner').GetOwner([Security.Principal.SecurityIdentifier]) -ne $me){" +
		"\"icacls /setowner left $p owned by another (exit $LASTEXITCODE)\";exit}}};'ok'")
	var shell := OS.get_environment("SystemRoot").path_join("System32/WindowsPowerShell/v1.0/powershell.exe")
	var out := []
	var code := OS.execute(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Marshalls.raw_to_base64(script.to_utf16_buffer())], out)
	var said := str(out[0]).strip_edges() if out.size() > 0 else ""
	return "" if code == 0 and said == "ok" else "%s (exit %d)" % [said, code]


static func _invite(over := {}) -> Dictionary:
	var said := {"url": "ws://127.0.0.1:1/v1/bridge", "secret": Marshalls.raw_to_base64(_range(0)), "pid": OS.get_process_id(),
		"born": 1, "started": "2026-01-01T00:00:00+00:00", "protocol": 2, "editor_id": Discovery.process_id()}
	said.merge(over, true)
	return said


## Puts an invite (a dictionary, or a file's text) where this process looks for its own. changed: its time differs from
## the one before, for the same bytes again, which only their time (in whole seconds) tells from the ones before.
static func _place(t, said, changed := false) -> bool:
	var path := Discovery.invite_path()
	var before := FileAccess.get_modified_time(path) if FileAccess.file_exists(path) else -1
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	while true:
		var f := FileAccess.open(path, FileAccess.WRITE)
		f.store_string(said if typeof(said) == TYPE_STRING else JSON.stringify(said))
		f.close()
		if not changed or FileAccess.get_modified_time(path) != before:
			break
		OS.delay_msec(100)
	if before >= 0:
		return true
	var owned := _own([path.get_base_dir(), path])
	t.expect("invite", "the test's invite is owned by this user", owned == "", owned)
	return owned == ""


## Sets a file's time to a whole second, as FileAccess counts it.
static func _dated(path: String, seconds: int) -> String:
	var script := ("$ErrorActionPreference='Stop';(Get-Item -LiteralPath '%s' -Force).LastWriteTimeUtc=" % path.replace("/", "\\").replace("'", "''") +
		"[DateTimeOffset]::FromUnixTimeSeconds(%d).UtcDateTime;'ok'" % seconds)
	var shell := OS.get_environment("SystemRoot").path_join("System32/WindowsPowerShell/v1.0/powershell.exe")
	var out := []
	var code := OS.execute(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Marshalls.raw_to_base64(script.to_utf16_buffer())], out)
	var said := str(out[0]).strip_edges() if out.size() > 0 else ""
	return "" if code == 0 and said == "ok" and FileAccess.get_modified_time(path) == seconds else "%s (exit %d)" % [said, code]


static func _looked(discovery) -> Dictionary:
	var deadline := Time.get_ticks_msec() + 30000
	var found: Dictionary = discovery.look()
	while found.has("pending") and Time.get_ticks_msec() < deadline:
		OS.delay_msec(20)
		found = discovery.look()
	return found


## Each invite fails at more than one check: what it is answered with tells which check came first.
static func the_invite_is_checked_in_order(t) -> void:
	var path := Discovery.invite_path()
	t.expect("invite", "none is none", Discovery.new().look().has("absent"))
	if not _place(t, "[]"):
		return
	var found := _looked(Discovery.new())
	t.expect("invite", "what isn't a JSON object is no invite", str(found.get("problem", "")).ends_with("isn't an invite"), found)
	_place(t, JSON.stringify(_invite()) + " ".repeat(65536))
	found = _looked(Discovery.new())
	t.expect("invite", "one larger than an invite can be is refused unread",
		str(found.get("problem", "")).ends_with("is larger than an invite can be: refused"), found)
	_place(t, _invite({"editor_id": "0123456789abcdef0123456789abcdef", "protocol": 3}))
	found = _looked(Discovery.new())
	t.expect("invite", "one for another editor_id is left alone without a word", found.is_empty(), found)
	_place(t, _invite({"protocol": 3, "secret": "AAAA"}))
	found = _looked(Discovery.new())
	t.expect("invite", "another protocol gives the release text", found.get("problem") == Discovery.OTHER_RELEASE, found)
	_place(t, _invite({"secret": "AAAA", "pid": NO_PROCESS}))
	found = _looked(Discovery.new())
	t.expect("invite", "a short secret", str(found.get("problem", "")).ends_with("has no usable secret"), found)
	_place(t, _invite({"pid": NO_PROCESS, "url": "ws://example.com:80/v1/bridge"}))
	found = _looked(Discovery.new())
	t.expect("invite", "a brain that isn't running", found.get("problem") == Discovery.RUN_ENDED, found)
	_place(t, _invite({"url": "ws://example.com:80/v1/bridge"}))
	found = _looked(Discovery.new())
	t.expect("invite", "a URL elsewhere", str(found.get("problem", "")).contains("isn't ws:// on this machine"), found)
	_place(t, _invite())
	var discovery = Discovery.new()
	found = _looked(discovery)
	t.expect("invite", "one that passes gives its URL and secret", found.get("url") == "ws://127.0.0.1:1/v1/bridge" and
		found.get("secret") == _range(0), found)
	discovery.refuse()
	t.expect("invite", "a refused one is not given again", discovery.look().is_empty(), discovery.look())
	found = Discovery.new().look()
	t.expect("invite", "nor by the plugin loaded again, which knows it by its time and size alone", found.is_empty(), found)
	_place(t, _invite(), true)
	found = _looked(discovery)
	t.expect("invite", "one that changed after a refusal is given", found.has("url"), found)
	# The brain writes an invite that replaces another in a later second: one of the same size is told apart by that.
	var second := FileAccess.get_modified_time(path)
	discovery.refuse()
	_place(t, _invite({"url": "ws://127.0.0.1:2/v1/bridge"}))
	var dated := _dated(path, second + 1)
	found = _looked(discovery)
	t.expect("invite", "one of the same size a second after a refused one is given",
		dated == "" and found.get("url") == "ws://127.0.0.1:2/v1/bridge", [dated, found])
	_place(t, _invite({"url": "ws://127.0.0.1:3/v1/bridge"}))
	dated = _dated(path, second + 2)
	found = _looked(discovery)
	t.expect("invite", "one of the same size a second after the one given is given in its place",
		dated == "" and found.get("url") == "ws://127.0.0.1:3/v1/bridge", [dated, found])
	discovery.finish()

	# The folder's owner and kind come before what the file says: through a junction, not even "no invite" is answered.
	var invites := path.get_base_dir()
	var elsewhere := invites.get_base_dir().path_join("elsewhere")
	DirAccess.make_dir_recursive_absolute(elsewhere)
	DirAccess.rename_absolute(path, elsewhere.path_join(path.get_file()))
	DirAccess.remove_absolute(invites)
	var out := []
	OS.execute(OS.get_environment("SystemRoot").path_join("System32/cmd.exe"),
		["/c", "mklink", "/J", invites.replace("/", "\\"), elsewhere.replace("/", "\\")], out, true)
	if FileAccess.file_exists(path):
		found = _looked(Discovery.new())
		t.expect("invite", "one reached through a link is refused before it is read",
			str(found.get("problem", "")).ends_with("isn't owned by this user: refused"), found)
	else:
		t.expect("invite", "a junction for the test", false, out)
	DirAccess.remove_absolute(invites)
	DirAccess.remove_absolute(elsewhere.path_join(path.get_file()))
	DirAccess.remove_absolute(elsewhere)


class Brain extends RefCounted:
	var secret: PackedByteArray
	var server := TCPServer.new()
	var peer: WebSocketPeer = null
	var connections := 0
	var frames := []

	func listen() -> int:
		return server.get_local_port() if server.listen(0, "127.0.0.1") == OK else 0

	func poll() -> void:
		if server.is_connection_available():
			connections += 1
			peer = WebSocketPeer.new()
			peer.accept_stream(server.take_connection())
		if peer != null:
			peer.poll()
			while peer.get_available_packet_count() > 0:
				frames.append(JSON.parse_string(peer.get_packet().get_string_from_utf8()))

	func say(frame: Dictionary) -> void:
		peer.send_text(JSON.stringify(frame))

	func welcome(hello: Dictionary, session: String) -> String:
		var theirs := Marshalls.base64_to_raw(hello.nonce)
		var mine := Handshake.new_nonce()
		say({"type": "welcome", "role": "service", "session": session, "brain_version": "test",
			"nonce": Marshalls.raw_to_base64(mine),
			"proof": Marshalls.raw_to_base64(Handshake.prove(secret, Handshake.BRAIN_ROLE, theirs, mine))})
		return Marshalls.raw_to_base64(Handshake.prove(secret, Handshake.PLUGIN_ROLE, theirs, mine))

	func last() -> Dictionary:
		return frames[-1] if frames.size() > 0 and typeof(frames[-1]) == TYPE_DICTIONARY else {}


static func _until(link, brain: Brain, done: Callable, seconds := 30.0) -> bool:
	var deadline := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < deadline:
		link.poll()
		brain.poll()
		if done.call():
			return true
		OS.delay_msec(5)
	return false


static func _hello_after(link, brain: Brain, count: int) -> Dictionary:
	var came := _until(link, brain, func(): return brain.frames.size() > count and brain.last().get("type") == "hello")
	return brain.last() if came else {}


static func _hello_for(t, link, brain: Brain, invite: Dictionary) -> Dictionary:
	_place(t, invite, true)
	return _hello_after(link, brain, brain.frames.size())


static func the_handshake(t) -> void:
	var brain := Brain.new()
	brain.secret = _range(0)
	var port := brain.listen()
	var invite := _invite({"url": "ws://127.0.0.1:%d/v1/bridge" % port})
	if port <= 0 or not _place(t, invite):
		t.expect("handshake", "a brain to talk to", port > 0, port)
		return
	var seen := {"welcomed": [], "refused": 0}
	var link = Link.new()
	link.hello_fields = func(): return {"engine": "godot", "host": "editor"}
	link.on_frame = func(_frame, _text): pass
	link.on_welcomed = func(resumed): seen.welcomed.append(resumed)
	link.on_refused = func(): seen.refused += 1
	link.open()

	var hello := _hello_after(link, brain, 0)
	t.expect("handshake", "the hello speaks protocol 2", hello.get("protocol") == 2, hello)
	t.expect("handshake", "the hello names the project", hello.get("project_dir") == Discovery.project_dir(), hello)
	t.expect("handshake", "the hello's MAC is the invite's secret over its nonce and project",
		hello.has("nonce") and hello.has("mac") and Marshalls.base64_to_raw(str(hello.get("mac"))) ==
		Handshake.hello_mac(brain.secret, Marshalls.base64_to_raw(str(hello.get("nonce"))), Discovery.project_dir()), hello)
	t.expect("handshake", "a first hello resumes nothing", not hello.has("resume") and not hello.has("received"), hello)
	if hello.is_empty():
		link.close()
		return
	var words := "words of a brain that has proved nothing"
	brain.say({"type": "error", "code": "project", "message": words})
	_until(link, brain, func(): return seen.refused == 1)
	brain.peer.close(1008, words)
	t.expect("handshake", "a refusal by code shows the plugin's text for it",
		link.problem == "Refused: The run is for another project than this editor's", link.problem)
	var connections := brain.connections
	_until(link, brain, func(): return false, 1.5)
	t.expect("handshake", "a refused invite is not tried again", brain.connections == connections and link.ws == null,
		brain.connections)

	# The error frame and the close at once, as the brain sends them: Godot drops the frame more often than not.
	hello = _hello_for(t, link, brain, invite)
	t.expect("handshake", "an invite that changed is taken up", hello.get("type") == "hello", link.problem)
	if not hello.is_empty():
		brain.say({"type": "error", "code": "resume", "message": words})
		brain.peer.close(1008, "resume")
	_until(link, brain, func(): return seen.refused == 2)
	t.expect("handshake", "a refusal whose frame is lost is known by the close's reason",
		link.problem == "Refused: The session this editor tried to resume belongs to another project or engine", link.problem)
	hello = _hello_for(t, link, brain, invite)
	if not hello.is_empty():
		brain.peer.close(1008, words)
	_until(link, brain, func(): return seen.refused == 3)
	t.expect("handshake", "a close that refuses in words of its own is a refusal without them",
		link.problem == "Refused: The brain refused the connection", link.problem)

	hello = _hello_for(t, link, brain, invite)
	if not hello.is_empty():
		brain.say({"type": "welcome", "role": "service", "session": "s0", "nonce": Marshalls.raw_to_base64(_range(1)),
			"proof": Marshalls.raw_to_base64(_range(2))})
	_until(link, brain, func(): return seen.refused == 4)
	t.expect("handshake", "a welcome without the proof is no brain", not link.connected and
		link.problem.contains("it didn't prove it is this machine's brain"), link.problem)

	hello = _hello_for(t, link, brain, invite)
	var owed := brain.welcome(hello, "s1") if not hello.is_empty() else ""
	_until(link, brain, func(): return link.connected and brain.last().get("type") == "proof")
	t.expect("handshake", "a proved brain is connected to", link.connected and link.session == "s1" and
		seen.welcomed == [false], [link.problem, seen])
	t.expect("handshake", "the plugin proves itself back", brain.last().get("proof") == owed and owed != "", brain.last())
	if not link.connected:
		link.close()
		return
	brain.say({"type": "request", "id": 7, "op": "ping", "args": {}})
	_until(link, brain, func(): return link.received == 7)
	t.expect("handshake", "the newest request is counted", link.received == 7, link.received)

	brain.peer.close(1011, "dropped")
	_until(link, brain, func(): return not link.connected)
	link.send_answer(JSON.stringify({"type": "response", "id": 7, "ok": true}))
	hello = _hello_after(link, brain, brain.frames.size())
	t.expect("handshake", "the hello asks for its session back", hello.get("resume") == "s1" and hello.get("received") == 7, hello)
	if not hello.is_empty():
		brain.welcome(hello, "s2")
	_until(link, brain, func(): return link.connected and brain.last().get("type") == "proof")
	_until(link, brain, func(): return false, 0.3)
	t.expect("handshake", "a new session starts counting again", link.connected and link.session == "s2" and
		link.received == 0 and seen.welcomed == [false, false], [link.session, link.received, seen])
	t.expect("handshake", "a new session gets no answer held for the one before", brain.last().get("type") == "proof",
		brain.last())

	if link.connected:
		brain.peer.close(1011, "dropped")
		_until(link, brain, func(): return not link.connected)
		link.send_answer(JSON.stringify({"type": "response", "id": 1, "ok": true}))
		hello = _hello_after(link, brain, brain.frames.size())
		if not hello.is_empty():
			brain.welcome(hello, "s2")
		_until(link, brain, func(): return brain.last().get("type") == "response")
		t.expect("handshake", "a resumed session gets the answer held for it", link.connected and
			seen.welcomed == [false, false, true] and brain.last().get("id") == 1, [seen, brain.last()])
	link.close()
	brain.server.stop()


static func _remove(dir: String) -> void:
	var d := DirAccess.open(dir)
	if d == null:
		return
	for f in d.get_files():
		d.remove(f)
	for sub in d.get_directories():
		_remove(dir.path_join(sub))
	DirAccess.remove_absolute(dir)
