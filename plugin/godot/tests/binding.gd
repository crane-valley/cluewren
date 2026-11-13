## Which brain an editor takes (docs/adr/0015), with the same vectors and known answer as the Unity and Unreal tests.
extends RefCounted

const Discovery = preload("res://addons/cluewren/lib/discovery.gd")
const Handshake = preload("res://addons/cluewren/lib/handshake.gd")
const Link = preload("res://addons/cluewren/lib/link.gd")
const EditorBridge = preload("res://addons/cluewren/editor_bridge.gd")

const ABSENT := "absent"

# [vector, match, folders (ABSENT: no such field), this project's folder, taken]
const VECTORS := [
	["1", "related", [r"C:\Games\Foo"], r"C:\Games\Foo", true],
	["2", "related", [r"C:\Games\Foo"], "C:\\Games\\Foo\\", true],
	["3", "related", [r"C:\Games\Foo"], "c:/games/foo", true],
	["4", "related", [r"C:\Games"], r"C:\Games\Foo", true],
	["5", "related", [r"C:\Games\Foo\Assets\Scripts"], r"C:\Games\Foo", true],
	["6", "related", [r"C:\Games\Foo"], r"C:\Games\FooBar", false],
	["7", "related", [r"C:\Games\FooBar"], r"C:\Games\Foo", false],
	["8", "related", ["C:\\"], r"C:\Games\Foo", true],
	["9", "related", [r"C:\Games\Foo"], r"D:\Games\Foo", false],
	["10", "exact", [r"C:\Games"], r"C:\Games\Foo", false],
	["11", "exact", [r"C:\Games\Foo"], "C:\\Games\\Foo\\", true],
	["12", "exact", [r"C:\Games\Foo\Assets"], r"C:\Games\Foo", false],
	["13", "related", [r"C:\Link\Foo", r"C:\Games\Foo"], r"C:\Games\Foo", true],
	["14", "related", [r"C:\Games\Foo"], r"S:\Foo", false],
	["15", "related", [r"C:\Games\LongName"], r"C:\Games\LONGNA~1", false],
	["16", "exact", [r"S:\Foo", r"C:\Games\Foo"], r"S:\Foo", true],
	["17 absent", "related", ABSENT, r"C:\Games\Foo", true],
	["17 empty", "related", [], r"C:\Games\Foo", true],
	["18 empty", "related", [r"C:\Games\Foo"], "", false],
	["18 relative", "related", [r"C:\Games\Foo"], r"Games\Foo", false],
	["19", "nearby", [r"C:\Games\Foo"], r"C:\Games\Foo", false],
]

const KNOWN_MAC := "b0e5b16410e69ae2e7ab99cb08045c3b0f6ac8cf250e9898d151a05c964a31a3"


static func run(t) -> void:
	vectors(t)
	the_verdict_names_both_folders(t)
	hello_mac(t)
	the_link_opens_no_socket_for_another_project(t)
	the_editor_attaches_only_for_a_proved_brain(t)
	a_project_at_a_drive_root(t)


static func a_project_at_a_drive_root(t) -> void:
	var root := Discovery._dir_of("C:/")
	t.expect("binding", "a drive root keeps its separator", root == "C:/", root)
	t.expect("binding", "a folder loses its trailing separator", Discovery._dir_of("C:/Games/Foo/") == "C:/Games/Foo")
	for mode in ["related", "exact"]:
		var why := Discovery.for_another_project({"folders": ["C:\\"], "match": mode}, root)
		t.expect("binding", "a project at C:\\ takes a run for C:\\ (%s)" % mode, why == "", why)


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


## A game played in an editor with no brain, or one for another project, is left alone as on Unity and Unreal.
static func the_editor_attaches_only_for_a_proved_brain(t) -> void:
	var fake := FakeDebugger.new()
	var bridge = EditorBridge.new()
	bridge.debugger = fake
	bridge.disconnected_by_user = true  # (this machine's brain.json isn't looked at)
	t.root.add_child(bridge)
	bridge._game_session = 1
	bridge._attach_until = Time.get_ticks_msec() / 1000.0 + 60.0
	bridge._process(0.0)
	t.expect("binding", "no attach while no brain has proved itself", not "attach" in fake.sent, fake.sent)
	bridge.link.connected = true
	bridge._on_welcomed(false)
	bridge._process(0.0)
	t.expect("binding", "an attach once a brain has proved itself", "attach" in fake.sent, fake.sent)
	bridge.link.connected = false
	bridge.free()


static func vectors(t) -> void:
	for v in VECTORS:
		var said := {"match": v[1]}
		if typeof(v[2]) == TYPE_ARRAY:
			said.folders = v[2]
		var why := Discovery.for_another_project(said, v[3])
		t.expect("binding", "vector %s" % v[0], (why == "") == v[4], why)


static func the_verdict_names_both_folders(t) -> void:
	var why := Discovery.for_another_project({"folders": [r"C:\Link\Foo", r"C:\Games\Foo"], "match": "related"}, r"C:\Games\Bar")
	t.expect("binding", "the verdict's words", why == "For another project: the run is for \"C:\\Link\\Foo\" or " +
		"\"C:\\Games\\Foo\" (match \"related\"), this project is \"C:\\Games\\Bar\"", why)


static func _range(from: int) -> PackedByteArray:
	var b := PackedByteArray()
	for i in 32:
		b.append(from + i)
	return b


static func hello_mac(t) -> void:
	var mac := Handshake.hello_mac(_range(0), _range(32), "C:\\Users\\Dev\\Projects\\Gr\u00f6\u00dfe")
	t.expect("binding", "the hello's MAC", mac.hex_encode() == KNOWN_MAC, mac.hex_encode())


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


## The editor's and the game's link, on a brain.json of its own (LOCALAPPDATA pointed at a folder of the test's): for
## another project it opens no socket; for this one it does.
static func the_link_opens_no_socket_for_another_project(t) -> void:
	var saved := OS.get_environment("LOCALAPPDATA")
	var root := OS.get_environment("TEMP").path_join("cluewren-godot-test-%d" % OS.get_process_id())
	var path := root.path_join("Cluewren").path_join("brain.json")
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	OS.set_environment("LOCALAPPDATA", root)
	if Discovery.file_path() != path:
		t.expect("binding", "brain.json moved for the test", false, Discovery.file_path())
		OS.set_environment("LOCALAPPDATA", saved)
		return
	var other := r"C:\Cluewren\Another\Project"
	for case in [[other, false], [Discovery.project_dir(), true]]:
		var f := FileAccess.open(path, FileAccess.WRITE)
		f.store_string(JSON.stringify({"url": "ws://127.0.0.1:1/v1/bridge", "secret": Marshalls.raw_to_base64(_range(0)),
			"pid": OS.get_process_id(), "folders": [case[0]], "match": "related"}))
		f.close()
		var owned := _own([path.get_base_dir(), path])
		if owned != "":
			t.expect("binding", "the test's brain.json is owned by this user", false, owned)
			break
		var link = Link.new()
		link.hello_fields = func(): return {}
		link.open()
		var deadline := Time.get_ticks_msec() + 30000
		while link.ws == null and link.problem == "" and Time.get_ticks_msec() < deadline:
			OS.delay_msec(20)
			link.poll()
		if case[1]:
			t.expect("binding", "a socket for this project", link.ws != null, link.problem)
		else:
			t.expect("binding", "no socket for another project", link.ws == null, link.problem)
			t.expect("binding", "the problem names both folders", link.problem.contains(other) and
				link.problem.contains(Discovery.project_dir()), link.problem)
		link.close()
	OS.set_environment("LOCALAPPDATA", saved)
	DirAccess.remove_absolute(path)
	DirAccess.remove_absolute(path.get_base_dir())
	DirAccess.remove_absolute(root)
