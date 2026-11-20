## rc's refusals by category (docs/adr/0014), the export, and how an editor meets the brain (tests/binding.gd), on this
## Godot:
## godot --headless --path plugin/godot -s res://tests/rc_test.gd
## Any error logged counts as a failure, and every way out prints the summary: a script error stops only the function
## it is in, and one in _initialize or _run would otherwise leave the tree running with nothing to quit it.
extends SceneTree

const TIMEOUT_SECONDS := 120

class Errors extends Logger:
	var _lock := Mutex.new()
	var _count := 0

	func _log_error(_function: String, _file: String, _line: int, _code: String, _rationale: String, _editor_notify: bool,
			error_type: int, _script_backtraces: Array) -> void:
		if error_type != ERROR_TYPE_WARNING:
			_lock.lock()
			_count += 1
			_lock.unlock()

	func count() -> int:
		_lock.lock()
		var n := _count
		_lock.unlock()
		return n

const REVIEWED := {
	"GLTFState.set_base_path": "a field of the state; GLTFDocument, which reads and writes files, is refused",
	"GLTFState.set_filename": "a field of the state; GLTFDocument, which reads and writes files, is refused",
	"Node.set_scene_file_path": "a name only; what a restart opens is checked against res://.godot/ and res://.cluewren/",
	"MultiplayerSpawner.add_spawnable_scene": "a path argument under res://.godot/ or res://.cluewren/ is refused",
	"Resource.get_id_for_path": "reads an id",
	"RenderingServer.texture_set_path": "a debug label",
	"RenderingServer.shader_set_path_hint": "a debug label",
	"ShaderIncludeDB.has_built_in_include_file": "the engine's built-in shader includes",
	"ShaderIncludeDB.get_built_in_include_file": "the engine's built-in shader includes",
	"Logger._log_error": "a virtual told the file an error came from",
}
const REVIEWED_PREFIXES := ["OpenXR"]  # OpenXR action and interaction paths, not files

var Rc
var Export
var Binding
var rc
var host: Node
var failures := 0
var passes := 0
var _errors := Errors.new()
var _watchdog := Thread.new()
var _lock := Mutex.new()
var _over := false
var _started := false
var _ran := false


func _initialize() -> void:
	# The tests make, write and remove res://.cluewren: they run in a copy of the plugin that has none, as
	# eng/test-plugins.ps1 makes, never over a folder or a file somebody keeps under that name.
	if FileAccess.file_exists("res://.cluewren") or DirAccess.dir_exists_absolute("res://.cluewren"):
		printerr("res://.cluewren is there already: run the tests in a copy of plugin/godot that has none")
		quit(1)
		return
	OS.add_logger(_errors)
	_watchdog.start(_time_out)
	_main.call_deferred()
	# (loaded here, not preloaded: an addon script that doesn't compile is a failure with a summary, not a silent exit)
	Rc = load("res://addons/cluewren/lib/rc.gd")
	Export = load("res://addons/cluewren/lib/export.gd")
	Binding = load("res://tests/binding.gd")
	host = Node.new()
	host.name = "Host"
	root.add_child(host)
	rc = Rc.new(host)
	var probe: Node3D = load("res://tests/fixture.gd").new()
	probe.name = "Probe"
	root.add_child(probe)
	for n in [HTTPRequest.new(), LinkButton.new(), FileDialog.new()]:
		n.name = n.get_class()
		root.add_child(n)
	_started = true


func _main() -> void:
	if not _started:
		print("FAIL start-up stopped on an error")
	else:
		_run()
		if not _ran:
			print("FAIL _run stopped on an error")
	_lock.lock()
	_over = true
	_lock.unlock()
	_watchdog.wait_to_finish()
	OS.remove_logger(_errors)
	quit(1 if _report_failed() else 0)


func _report_failed() -> bool:
	var errors := _errors.count()
	if errors > 0:
		print("FAIL %d errors logged" % errors)
	var failed := failures + errors + (0 if _started and _ran else 1)
	print("%d passed, %d failed" % [passes, failed])
	return failed > 0 or passes == 0


# A hang the tree can't end (a check that never returns): the summary, then out with code 1. taskkill /F exits
# with 1 where OS.kill would exit with 0.
func _time_out() -> void:
	var deadline := Time.get_ticks_msec() + TIMEOUT_SECONDS * 1000
	while Time.get_ticks_msec() < deadline:
		_lock.lock()
		var over := _over
		_lock.unlock()
		if over:
			return
		OS.delay_msec(100)
	print("FAIL no end within %d s" % TIMEOUT_SECONDS)
	_report_failed()
	OS.execute(OS.get_environment("SystemRoot").path_join("System32/taskkill.exe"), ["/F", "/PID", str(OS.get_process_id())])


func _run() -> void:
	tables()
	c1_process()
	c2_files()
	c3_network()
	c4_code()
	c5_editor()
	c6_host_data()
	c7_console()
	c8_settings()
	c9_data_folder()
	the_addon_itself()
	the_export()
	load("res://tests/scenes_test.gd").run(self)
	still_reachable()
	census()
	Binding.run(self)
	_ran = true


func expect(cell: String, what: String, ok: bool, detail = "") -> void:
	if ok:
		passes += 1
	else:
		failures += 1
		print("FAIL %s %s %s" % [cell, what, str(detail)])


func refuses(cell: String, what: String, r: Array) -> void:
	expect(cell, what, not r[0] and r.size() > 2 and r[2] == 403, r)


func allows(cell: String, what: String, r: Array) -> void:
	expect(cell, what, r[0], r)


func gone(cell: String, what: String, r: Array) -> void:
	expect(cell, what, not r[0] and (str(r[1]).contains("does not exist") or r.size() > 2 and r[2] == 403), r)


func call_rc(path: String, fname: String, params = {}) -> Array:
	return rc.handle("call", {"objectPath": path, "functionName": fname, "parameters": params})


func read(path: String, prop := "") -> Array:
	return rc.handle("property", {"objectPath": path, "propertyName": prop})


func write(path: String, prop: String, value) -> Array:
	return rc.handle("property", {"objectPath": path, "propertyName": prop, "propertyValue": {prop: value}})


func instance(cell: String, obj: Object, member := "", op := "argument") -> void:
	expect(cell, "%s %s %s" % [obj.get_class(), member, op], rc.refused(obj, member, op) != "")


func tables() -> void:
	for s in Rc.FULL_SINGLETONS + Rc.LISTED_SINGLETONS.keys():
		expect("table", "singleton %s exists" % s, Engine.has_singleton(s))
	for s in Rc.LISTED_SINGLETONS:
		for m in Rc.LISTED_SINGLETONS[s]:
			expect("table", "%s.%s exists" % [s, m], ClassDB.class_has_method(Engine.get_singleton(s).get_class(), m))
	for c in Rc.REFUSED_CLASSES:
		expect("table", "class %s exists" % c, ClassDB.class_exists(c))
	for c in Rc.REFUSED_MEMBERS:
		var methods := ClassDB.class_get_method_list(c, true).map(func(m): return m.name)
		for m in Rc.REFUSED_MEMBERS[c]:
			expect("table", "%s.%s exists" % [c, m], m in methods)


func c1_process() -> void:
	refuses("C1", "OS.execute", call_rc("OS", "execute", {"path": "cmd.exe", "arguments": ["/c", "exit"]}))
	refuses("C1", "OS.create_process", call_rc("OS", "create_process", {"path": "cmd.exe", "arguments": []}))
	refuses("C1", "OS.shell_open", call_rc("OS", "shell_open", {"uri": "https://example.com"}))
	refuses("C1", "OS.kill", call_rc("OS", "kill", {"pid": 1}))
	refuses("C1", "LinkButton uri write", write("/root/LinkButton", "uri", "https://example.com"))
	refuses("C1", "LinkButton.set_uri", call_rc("/root/LinkButton", "set_uri", {"uri": "https://example.com"}))


func c2_files() -> void:
	instance("C2", FileAccess.open("res://project.godot", FileAccess.READ))
	instance("C2", DirAccess.open("res://"))
	instance("C2", ConfigFile.new())
	instance("C2", ZIPReader.new())
	instance("C2", PCKPacker.new())
	instance("C2", XMLParser.new())
	instance("C2", GLTFDocument.new())
	var image := Image.create_empty(2, 2, false, Image.FORMAT_RGBA8)
	for m in ["save_png", "save_jpg", "save_webp", "load"]:
		instance("C2", image, m, "call")
	var wav := AudioStreamWAV.new()
	instance("C2", wav, "save_to_wav", "call")
	instance("C2", wav, "load_from_file", "call")
	instance("C2", FontFile.new(), "load_dynamic_font", "call")
	instance("C2", CompressedTexture2D.new(), "load", "call")
	if ClassDB.can_instantiate("VideoStreamTheora"):
		instance("C2", ClassDB.instantiate("VideoStreamTheora"), "file", "write")
	refuses("C2", "FileDialog.set_current_dir", call_rc("/root/FileDialog", "set_current_dir", {"dir": "C:/"}))
	refuses("C2", "ResourceSaver.save", call_rc("ResourceSaver", "save", {"resource": "", "path": "res://x.tres"}))
	refuses("C2", "ResourceLoader.load", call_rc("ResourceLoader", "load", {"path": "res://project.godot"}))
	refuses("C2", "ProjectSettings.save_custom", call_rc("ProjectSettings", "save_custom", {"file": "res://x.godot"}))


func c3_network() -> void:
	refuses("C3", "HTTPRequest.request", call_rc("/root/HTTPRequest", "request", {"url": "https://example.com"}))
	refuses("C3", "HTTPRequest read", read("/root/HTTPRequest", "download_file"))
	refuses("C3", "IP.resolve_hostname", call_rc("IP", "resolve_hostname", {"host": "example.com"}))
	for o in [HTTPClient.new(), StreamPeerTCP.new(), PacketPeerUDP.new(), TCPServer.new(), UDPServer.new(),
			ENetMultiplayerPeer.new(), WebSocketPeer.new(), UPNP.new()]:
		instance("C3", o)


func c4_code() -> void:
	refuses("C4", "script:source_code read", read("/root/Probe", "script:source_code"))
	refuses("C4", "script write", write("/root/Probe", "script", "res://tests/fixture.gd"))
	for m in ["call", "callv", "call_deferred", "set", "get", "set_indexed", "set_script", "propagate_call", "rpc",
			"call_thread_safe"]:
		refuses("C4", "Node.%s" % m, call_rc("/root/Probe", m, ["score"]))
	refuses("C4", "script object source_code", read("res://tests/fixture.gd", "source_code"))
	refuses("C4", "script object reload", call_rc("res://tests/fixture.gd", "reload"))
	refuses("C4", "script object new", call_rc("res://tests/fixture.gd", "new"))
	refuses("C4", "script object set_source_code", call_rc("res://tests/fixture.gd", "set_source_code", {"source": ""}))
	refuses("C4", "ClassDB.instantiate", call_rc("ClassDB", "instantiate", {"class": "Node"}))
	refuses("C4", "GDExtensionManager", call_rc("GDExtensionManager", "load_extension", {"path": "res://x.gdextension"}))
	refuses("C4", "Marshalls", call_rc("Marshalls", "base64_to_variant", {"base64_str": "", "allow_objects": true}))
	refuses("C4", "Engine.get_singleton", call_rc("Engine", "get_singleton", {"name": "OS"}))
	refuses("C4", "Engine.register_singleton", call_rc("Engine", "register_singleton", {"name": "X", "instance": "/root/Probe"}))
	instance("C4", Expression.new())
	instance("C4", Thread.new())
	instance("C4", Shader.new(), "code", "write")
	instance("C4", Resource.new(), "resource_path", "write")
	instance("C4", Resource.new(), "take_over_path", "call")
	refuses("C4", "an OS argument", call_rc("/root/Probe", "add_child", {"node": "OS"}))
	var probe = root.get_node("Probe")
	probe.bag = {"script_resource": load("res://tests/fixture.gd")}
	probe.list = [load("res://tests/fixture.gd")]
	refuses("C4", "a script's source through a dictionary", read("/root/Probe", "bag:script_resource:source_code"))
	refuses("C4", "a script's source written through a dictionary",
		write("/root/Probe", "bag:script_resource:source_code", "extends Node"))
	var listed := read("/root/Probe", "list:0:source_code")
	expect("C4", "a script's source through an array", not listed[0] or not str(listed[1]).contains("extends"), listed)


func c5_editor() -> void:
	refuses("C5", "EditorInterface", call_rc("EditorInterface", "get_editor_settings"))
	for c in ClassDB.get_class_list():
		var api := ClassDB.class_get_api_type(c)
		if (api == ClassDB.API_EDITOR or api == ClassDB.API_EDITOR_EXTENSION) and ClassDB.can_instantiate(c) \
				and ClassDB.is_parent_class(c, "RefCounted"):
			instance("C5", ClassDB.instantiate(c))
			return


func c6_host_data() -> void:
	for m in ["get_environment", "set_environment", "has_environment", "get_cmdline_args", "get_cmdline_user_args",
			"get_unique_id", "get_user_data_dir"]:
		refuses("C6", "OS.%s" % m, call_rc("OS", m, {"variable": "PATH", "value": ""}))
	refuses("C6", "DisplayServer.clipboard_get", call_rc("DisplayServer", "clipboard_get"))
	refuses("C6", "DisplayServer.clipboard_set", call_rc("DisplayServer", "clipboard_set", {"clipboard": "x"}))
	refuses("C6", "AudioServer.get_input_device_list", call_rc("AudioServer", "get_input_device_list"))
	refuses("C6", "AudioServer.set_input_device", call_rc("AudioServer", "set_input_device", {"name": "Default"}))
	refuses("C6", "CameraServer", call_rc("CameraServer", "feeds"))
	for o in [AudioEffectCapture.new(), AudioEffectRecord.new(), AudioStreamMicrophone.new(), CameraFeed.new()]:
		instance("C6", o)


func c7_console() -> void:
	refuses("C7", "EngineDebugger.send_message", call_rc("EngineDebugger", "send_message", {"message": "x", "data": []}))


func c8_settings() -> void:
	refuses("C8", "ProjectSettings.set_setting", call_rc("ProjectSettings", "set_setting", {"name": "a/b", "value": 1}))
	refuses("C8", "ProjectSettings.save", call_rc("ProjectSettings", "save"))
	refuses("C8", "ProjectSettings property write", write("ProjectSettings", "application/config/name", "x"))


func c9_data_folder() -> void:
	for scene in ["res://.cluewren/runs/x/work/evil.tscn", "res://.godot/imported/evil.tscn"]:
		var folder: String = scene.get_slice("/", 2)
		refuses("C9", "a res://%s/ target" % folder, read(scene))
		refuses("C9", "a res://%s/ describe" % folder, rc.handle("describe", {"objectPath": scene}))
		refuses("C9", "a res://%s/ string argument" % folder,
			call_rc("/root/Probe", "set_scene_file_path", {"scene_file_path": scene}))
		refuses("C9", "a relative %s/ argument" % folder,
			call_rc("/root/Probe", "set_scene_file_path", {"scene_file_path": scene.trim_prefix("res://")}))
		for p in ["res://%s/x.tscn" % folder.to_upper(), "res://a/../%s/x.tscn" % folder,
				ProjectSettings.globalize_path(scene), ProjectSettings.globalize_path(scene).replace("/", "\\")]:
			expect("C9", "data_path %s" % p, Export.data_path(p))
		var id := ResourceUID.create_id()
		ResourceUID.add_id(id, scene)
		expect("C9", "uid to res://%s/" % folder, Export.data_path(ResourceUID.id_to_text(id)))
		ResourceUID.remove_id(id)
		expect("C9", "find_scene refuses res://%s/" % folder, Export.find_scene(scene) == "")
		var why: String = Export.refused_scene(scene)
		expect("C9", "a scene under res://%s/ is refused by name" % folder, why.begins_with(scene + " is refused: "), why)
	# By the text alone: nothing a path names is opened, a share's included.
	for p in [".cluewren/x", "a/.CLUEWREN/x", "C:\\other\\.cluewren\\x", "\\\\server\\share\\.cluewren\\x", ".cluewren.",
			".cluewren ", ".cluewren::$INDEX_ALLOCATION", ".cluewren:x", "file:///C:/p/%2Ecluewren/x", "a/.godot/x",
			"C:.cluewren/x", "c:.CLUEWREN", ".cluewren", "%2Eclue%77ren%2Fruns%2Fx%2Fwork%2Fevil.tscn", "C:/p/CLUEWR~1/x",
			"cl12ab~1", "GODOT~1/imported/x.tscn"]:
		expect("C9", "data_path %s" % p, Export.data_path(p))
	for p in [".cluewrens/x", "x.cluewren/y", "my.cluewren", "a/cluewren/b", "C:/PROGRA~1/x/y.tscn", "Assets/NUL",
			"CLUEWREN~1/x", "CLUEWR~1.txt"]:
		expect("C9", "not data_path %s" % p, not Export.data_path(p))
	refuses("C9", "a percent-encoded .cluewren as a string argument", call_rc("/root/Probe", "echo",
		{"value": "%2Eclue%77ren%2Fruns%2Fx%2Fwork%2Fevil.tscn"}))
	refuses("C9", "a share's .cluewren as a string argument", call_rc("/root/Probe", "echo",
		{"value": "\\\\server\\share\\.cluewren\\x"}))
	refuses("C9", "a stream of .cluewren as a string argument", call_rc("/root/Probe", "echo", {"value": ".cluewren:x"}))
	var had_home := DirAccess.dir_exists_absolute("res://.cluewren")
	if not had_home:
		DirAccess.make_dir_absolute("res://.cluewren")
	var short := _short_name(ProjectSettings.globalize_path("res://.cluewren"))
	if short != "":
		var through := ProjectSettings.globalize_path("res://").path_join(short).path_join("runs/x.tscn")
		expect("C9", "data_path through the folder's own 8.3 name", Export.data_path(through), through)
	if not had_home:
		DirAccess.remove_absolute("res://.cluewren")
	expect("C9", "an ordinary scene is not refused", Export.refused_scene("res://levels/one.tscn") == "")
	expect("C9", "an ordinary path is not data", not Export.data_path("res://tests/fixture.gd"))
	expect("C9", "a node path is not data", not Export.data_path("/root/Probe"))
	refuses("C9", "a String default argument into res://.cluewren/", call_rc("/root/Probe", "pick"))
	refuses("C9", "a NodePath default argument into res://.cluewren/", call_rc("/root/Probe", "pick_node"))
	allows("ok", "an ordinary default argument", call_rc("/root/Probe", "pick_safe"))
	refuses("C9", "a String default argument after no positional ones", call_rc("/root/Probe", "pick", []))
	refuses("C9", "a NodePath default argument after no positional ones", call_rc("/root/Probe", "pick_node", []))
	allows("ok", "an ordinary default argument after no positional ones", call_rc("/root/Probe", "pick_safe", []))
	var deep = "res://.cluewren/runs/x.tscn"
	var flat = 1
	for i in 12:
		deep = [deep]
		flat = [flat]
	refuses("C9", "a path nested past the depth checked", call_rc("/root/Probe", "echo", {"value": deep}))
	refuses("C9", "a value nested past the depth checked", call_rc("/root/Probe", "echo", {"value": flat}))
	allows("ok", "a shallow value", call_rc("/root/Probe", "echo", {"value": [[1]]}))


## The 8.3 name Windows gave a folder, "" when it gave none.
func _short_name(folder: String) -> String:
	var script := "(New-Object -ComObject Scripting.FileSystemObject).GetFolder('%s').ShortName" % folder.replace("/", "\\").replace("'", "''")
	var shell := OS.get_environment("SystemRoot").path_join("System32/WindowsPowerShell/v1.0/powershell.exe")
	var out := []
	var code := OS.execute(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Marshalls.raw_to_base64(script.to_utf16_buffer())], out)
	var said := str(out[0]).strip_edges() if code == 0 and out.size() > 0 else ""
	return "" if said.to_lower() == folder.get_file().to_lower() else said


## The autoload holds the link to the brain and the addon's scripts read the invite: rc (here with /root/Host as its
## host) reaches neither.
func the_addon_itself() -> void:
	refuses("addon", "its node as a target", read("/root/Host"))
	refuses("addon", "its node described", rc.handle("describe", {"objectPath": "/root/Host"}))
	refuses("addon", "a call on its node", call_rc("/root/Host", "get_name"))
	refuses("addon", "a write to its node", write("/root/Host", "name", "Other"))
	refuses("addon", "its node as an argument", call_rc("/root/Probe", "is_ancestor_of", {"node": "/root/Host"}))
	var said := read("/root/Host")
	expect("addon", "the refusal names the node and why", str(said[1]).begins_with("/root/Host is refused: "), said)
	for script in ["res://addons/cluewren/lib/discovery.gd", "res://addons/cluewren/game.gd"]:
		refuses("addon", "%s as a target" % script, read(script))
	refuses("addon", "a static function of its scripts", call_rc("res://addons/cluewren/lib/discovery.gd", "invite_path"))
	refuses("addon", "a constant of its scripts", read("res://addons/cluewren/lib/discovery.gd", "PROTOCOL"))
	# Windows opens a file by any case of its name, and Godot keeps the spelling it was asked for as the script's path.
	var id := ResourceUID.create_id()
	ResourceUID.add_id(id, "res://addons/CLUEWREN/lib/discovery.gd")
	for script in ["res://ADDONS/cluewren/lib/discovery.gd", "res://addons/Cluewren/lib/discovery.gd",
			"res://addons/x/../cluewren/lib/discovery.gd", "res://addons//cluewren/lib/discovery.gd", ResourceUID.id_to_text(id)]:
		refuses("addon", "a static function of its scripts by %s" % script, call_rc(script, "invite_path"))
	ResourceUID.remove_id(id)
	refuses("addon", "its script as an object argument by another spelling",
		call_rc("/root/Probe", "is_ancestor_of", {"node": "res://ADDONS/cluewren/lib/discovery.gd"}))
	for p in ["res://addons/CLUEWREN/lib/discovery.gd", ProjectSettings.globalize_path("res://addons/cluewren/lib/discovery.gd")]:
		refuses("addon", "its script's path %s as a string argument" % p, call_rc("/root/Probe", "echo", {"value": p}))
	expect("addon", "no other spelling of its scripts is loaded for rc", not ResourceLoader.has_cached("res://ADDONS/cluewren/lib/discovery.gd")
		and not ResourceLoader.has_cached("res://addons/Cluewren/lib/discovery.gd")
		and not ResourceLoader.has_cached("res://addons/CLUEWREN/lib/discovery.gd"))
	var loaded: Script = load("res://Addons/cluewren/lib/discovery.gd")
	expect("addon", "its script loaded under another spelling", rc.refused(loaded, "invite_path", "call") != "", loaded.resource_path)
	the_addon_reached_elsewhere()


## A junction in the project and a copy of a script reach the addon's scripts under paths of their own. Once loaded as a
## target they are known by what they hold; a string naming them is not followed (it is left to the project's own code).
func the_addon_reached_elsewhere() -> void:
	# (names of the test's own: it never writes over, or removes, what it didn't make)
	var own := "%d-%08x" % [OS.get_process_id(), randi()]
	var alias_res := "res://rc_alias-" + own
	var alias := ProjectSettings.globalize_path(alias_res)
	var copy := "res://tests/rc_copy-%s.gd" % own
	if DirAccess.dir_exists_absolute(alias) or FileAccess.file_exists(alias) or FileAccess.file_exists(copy):
		expect("addon", "names of the test's own", false, [alias, copy])
		return
	var out := []
	OS.execute(OS.get_environment("SystemRoot").path_join("System32/cmd.exe"), ["/c", "mklink", "/J", alias.replace("/", "\\"),
		ProjectSettings.globalize_path("res://addons/cluewren").replace("/", "\\")], out, true)
	expect("addon", "a junction for the test", DirAccess.dir_exists_absolute(alias), out)
	var f := FileAccess.open(copy, FileAccess.WRITE)
	f.store_string(FileAccess.get_file_as_string("res://addons/cluewren/lib/discovery.gd"))
	f.close()
	for script in [alias_res + "/lib/discovery.gd", copy]:
		refuses("addon", "a static function of its script as %s" % script, call_rc(script, "invite_path"))
		refuses("addon", "its script as %s as an object argument" % script,
			call_rc("/root/Probe", "is_ancestor_of", {"node": script}))
	var copied: Script = load(copy)
	expect("addon", "a copy of its script loaded", rc.refused(copied, "invite_path", "call") != "", copy)
	DirAccess.remove_absolute(copy)
	# (the junction alone: a junction is removed as an empty folder, what it points at staying)
	DirAccess.remove_absolute(alias)
	expect("addon", "the junction is gone and the addon is not", not DirAccess.dir_exists_absolute(alias) and
		FileAccess.file_exists("res://addons/cluewren/lib/discovery.gd"))


func the_export() -> void:
	var home := "res://.cluewren"
	if DirAccess.dir_exists_absolute(home):
		expect("export", "no res://.cluewren before the test", false, ProjectSettings.globalize_path(home))
		return
	var why: String = Export.write_summary()
	expect("export", "the summary is written", why == "", why)
	var summary := FileAccess.get_file_as_string(home + "/export/summary.txt")
	expect("export", "to res://.cluewren/export/summary.txt", summary.begins_with("Godot project: "), summary.left(80))
	expect("export", "whoever makes .cluewren ignores it for git", FileAccess.get_file_as_string(home + "/.gitignore") == "*\n")
	expect("export", "nothing where the summary used to be", not DirAccess.dir_exists_absolute("res://.godot/cluewren_codex"))
	DirAccess.remove_absolute(home + "/.gitignore")
	expect("export", "written again", Export.write_summary() == "")
	expect("export", "a .cluewren that was there gets no .gitignore", not FileAccess.file_exists(home + "/.gitignore"))
	for path in [home + "/export/summary.txt", home + "/export", home]:
		DirAccess.remove_absolute(path)


func still_reachable() -> void:
	allows("ok", "Input.is_action_pressed", call_rc("Input", "is_action_pressed", {"action": "ui_accept"}))
	allows("ok", "Time.get_ticks_msec", call_rc("Time", "get_ticks_msec"))
	allows("ok", "Engine.get_frames_drawn", call_rc("Engine", "get_frames_drawn"))
	allows("ok", "Engine time_scale", read("Engine", "time_scale"))
	allows("ok", "OS.get_name", call_rc("OS", "get_name"))
	allows("ok", "ProjectSettings.get_setting", call_rc("ProjectSettings", "get_setting", {"name": "application/config/name"}))
	allows("ok", "DisplayServer.window_get_size", call_rc("DisplayServer", "window_get_size"))
	allows("ok", "script variable", read("/root/Probe", "score"))
	allows("ok", "script variable write", write("/root/Probe", "score", 7))
	expect("ok", "written", root.get_node("Probe").score == 7)
	allows("ok", "position:y", read("/root/Probe", "position:y"))
	allows("ok", "position:y write", write("/root/Probe", "position:y", 2.0))
	allows("ok", "script read", read("/root/Probe", "script"))
	var sum := call_rc("/root/Probe", "add", {"a": 1, "b": 2})
	expect("ok", "script function", sum[0] and sum[1].ReturnValue == 3, sum)
	allows("ok", "a node argument", call_rc("/root/Probe", "is_ancestor_of", {"node": "/root/LinkButton"}))
	allows("ok", "script object static function", call_rc("res://tests/fixture.gd", "twice", {"x": 2}))
	allows("ok", "script object constant", read("res://tests/fixture.gd", "LIMIT"))
	var props := read("OS")
	expect("ok", "OS props list nothing refused", props[0] and props[1].is_empty(), props)
	var described: Array = rc.handle("describe", {"objectPath": "OS"})
	var names := []
	if described[0]:
		for f in described[1].Functions:
			names.append(f.Name)
	expect("ok", "OS describe lists its getters alone", "get_name" in names and not "execute" in names, names)
	var node: Array = rc.handle("describe", {"objectPath": "/root/Probe"})
	var listed := []
	if node[0]:
		for f in node[1].Functions:
			listed.append(f.Name)
	expect("ok", "node describe", "add" in listed and "get_position" in listed and not "callv" in listed, listed)
	var all := read("/root/Probe")
	expect("ok", "node props", all[0] and all[1].has("score") and all[1].has("position"), all)


func census() -> void:
	var words := ["path", "paths", "file", "files", "filename", "dir", "directory", "url", "uri", "host", "hostname",
		"address", "program", "pck"]
	var singletons := {}
	for s in Engine.get_singleton_list():
		var o = Engine.get_singleton(s)
		if o:
			singletons[o.get_class()] = o
			singletons[s] = o
	for c in ClassDB.get_class_list():
		var api := ClassDB.class_get_api_type(c)
		if api == ClassDB.API_EDITOR or api == ClassDB.API_EDITOR_EXTENSION:
			continue
		for m in ClassDB.class_get_method_list(c, true):
			var hit := false
			for a in m.args:
				if a.type in [TYPE_STRING, TYPE_STRING_NAME, TYPE_PACKED_STRING_ARRAY]:
					for w in str(a.name).split("_"):
						hit = hit or w in words
			var key := "%s.%s" % [c, m.name]
			if not hit or REVIEWED.has(key) or REVIEWED_PREFIXES.any(func(p): return c.begins_with(p)):
				continue
			var why: String = rc.refused(singletons[c], m.name, "call") if singletons.has(c) \
				else rc.refused_in_class(c, m.name, "call")
			expect("census", "%s is refused or reviewed" % key, why != "")
