## The rendezvous with the brain (src/Cluewren/Bridge/Rendezvous.cs): this process advertises itself in
## %LOCALAPPDATA%\Cluewren\editors\<pid>.json, and the brain answers with invites\<pid>.json. The advert holds no secret
## and proves nothing. Nothing is decided on the invite's word alone: its owner is checked before it is read, it must
## answer this process's advert, and its URL must name this machine.
@tool
extends RefCounted

const PROTOCOL := 2
const SECRET_BYTES := 32
const MAX_BYTES := 65536  # the bound the brain reads adverts with: no invite comes near it
const ID_META := &"cluewren_id"
const REFUSED_META := &"cluewren_refused"  # in the engine as the id is: a refusal outlives the plugin loaded again
const WINDOWS_ONLY := "The bridge is Windows only: an invite's owner can't be checked here"
const OTHER_RELEASE := "The cluewren command and this plugin are of different releases: install the same release of both"
const RUN_ENDED := "The run this editor was invited to has ended"

static var _url_shape := RegEx.create_from_string("^ws://(127\\.0\\.0\\.1|\\[::1\\]):[0-9]{1,5}(/[A-Za-z0-9/_-]*)?$")

var _task := -1
var _task_key := ""
var _task_said := ""
var _key := ""  # the time and size of the invite last validated
var _found := {}  # what that validation gave
var _given := ""  # the key of the invite last given out to connect to


static func folder() -> String:
	var root := OS.get_environment("LOCALAPPDATA")
	return "" if root == "" else root.path_join("Cluewren")


static func _file(kind: String) -> String:
	var root := folder()
	return "" if root == "" else root.path_join(kind).path_join("%d.json" % OS.get_process_id())


static func advert_path() -> String:
	return _file("editors")


static func invite_path() -> String:
	return _file("invites")


## Made once for this process and kept in the engine, so a plugin loaded again has the same one: the invite names it,
## and an editor with a new id would ignore the invite of the run it is in.
static func process_id() -> String:
	if not Engine.has_meta(ID_META):
		Engine.set_meta(ID_META, Crypto.new().generate_random_bytes(16).hex_encode())
	return Engine.get_meta(ID_META)


static func plugin_version() -> String:
	var cfg := ConfigFile.new()
	var version := "0.2.0"
	if cfg.load("res://addons/cluewren/plugin.cfg") == OK:
		version = str(cfg.get_value("plugin", "version", version))
	return version


static func project_dir() -> String:
	return _dir_of(ProjectSettings.globalize_path("res://"))


## A drive root keeps its separator: "C:" alone is no folder, and the brain refuses it.
static func _dir_of(globalized: String) -> String:
	var trimmed := globalized.trim_suffix("/")
	return globalized if trimmed == "" or trimmed.ends_with(":") else trimmed


static func write_advert(host: String) -> String:
	if OS.get_name() != "Windows":
		return WINDOWS_ONLY
	var path := advert_path()
	if path == "":
		return "LOCALAPPDATA isn't set: the brain can't find this %s" % host
	var dir := path.get_base_dir()
	if DirAccess.make_dir_recursive_absolute(dir) != OK and not DirAccess.dir_exists_absolute(dir):
		return "%s can't be made: the brain can't find this %s" % [dir, host]
	var temp := path + ".tmp"
	var f := FileAccess.open(temp, FileAccess.WRITE)
	if f == null:
		return "%s can't be written (error %d): the brain can't find this %s" % [temp, FileAccess.get_open_error(), host]
	f.store_string(JSON.stringify({"pid": OS.get_process_id(), "id": process_id(), "engine": "godot",
		"engine_version": Engine.get_version_info().string, "plugin_version": plugin_version(),
		"protocol_min": PROTOCOL, "protocol_max": PROTOCOL, "project_dir": project_dir(), "host": host}))
	f.close()
	# Renamed into place: the brain reads the folder at any moment and must never find half an advert.
	if DirAccess.rename_absolute(temp, path) != OK:
		DirAccess.remove_absolute(temp)
		return "%s can't be written: the brain can't find this %s" % [path, host]
	return ""


static func remove_advert() -> void:
	var path := advert_path()
	if path != "" and FileAccess.file_exists(path):
		DirAccess.remove_absolute(path)


## GDScript has no API for a file's owner or another process, so PowerShell answers, off the caller's thread and once per
## invite: what it said stands until the file changes or forget() is called. Its time and size tell one invite from
## another, with nothing in it read: FileAccess counts the time in whole seconds, and the brain writes an invite that
## replaces another in a later second.
func look() -> Dictionary:
	if OS.get_name() != "Windows":
		return {"problem": WINDOWS_ONLY}
	var path := invite_path()
	if path == "" or not FileAccess.file_exists(path):
		_key = ""
		if Engine.has_meta(REFUSED_META):
			Engine.remove_meta(REFUSED_META)
		return {"absent": true}
	var size := FileAccess.get_size(path)
	if size > MAX_BYTES:
		return {"problem": "%s is larger than an invite can be: refused" % path}
	var key := "%d %d" % [FileAccess.get_modified_time(path), size]
	if key == Engine.get_meta(REFUSED_META, ""):
		return {}
	if key == _key:
		return _found
	if _task >= 0:
		if not WorkerThreadPool.is_task_completed(_task):
			return {"pending": true}
		WorkerThreadPool.wait_for_task_completion(_task)
		_task = -1
		if _task_key == key:
			var found := _validate(path, _task_said)
			if not found.has("pending"):
				_key = key
				_found = found
				if found.has("url"):
					_given = key
				return found
	_task_key = key
	_task = WorkerThreadPool.add_task(_ask_windows.bind(path), false, "Cluewren invite check")
	return {"pending": true}


static func _sha256(bytes: PackedByteArray) -> String:
	var h := HashingContext.new()
	h.start(HashingContext.HASH_SHA256)
	if not bytes.is_empty():
		h.update(bytes)
	return h.finish().hex_encode()


## The brain behind the invite may be gone since it was validated: it is validated again at the next look.
func forget() -> void:
	_key = ""


## The brain behind the invite last given out turned this process away: asking it again won't change its answer.
func refuse() -> void:
	if _given != "":
		Engine.set_meta(REFUSED_META, _given)


## Wait for a check still running: its task holds this object.
func finish() -> void:
	if _task >= 0:
		WorkerThreadPool.wait_for_task_completion(_task)
		_task = -1


## _task_said, when the invite and its folder are this user's: the pid the invite names if that process is running, else
## 0, then the sha256 of the bytes that pid was read from; anything else when they aren't. The file is read only after
## its owner passed.
func _ask_windows(path: String) -> void:
	var folder := path.get_base_dir().replace("'", "''")
	var file := path.replace("'", "''")
	var script := ("$ErrorActionPreference='Stop';$me=[Security.Principal.WindowsIdentity]::GetCurrent().User;" +
		"foreach($p in @('%s','%s')){$i=Get-Item -LiteralPath $p -Force;" % [folder, file] +
		"if($i.Attributes -band [IO.FileAttributes]::ReparsePoint){'link';exit}" +
		"if($i.GetAccessControl('Owner').GetOwner([Security.Principal.SecurityIdentifier]) -ne $me){'other';exit}}" +
		"if($i.Length -gt %d){'large';exit}" % MAX_BYTES +
		"$b=[IO.File]::ReadAllBytes('%s');" % file +
		"$h=-join([Security.Cryptography.SHA256]::Create().ComputeHash($b)|ForEach-Object{$_.ToString('x2')});" +
		"$n=0;try{$n=[int]([Text.Encoding]::UTF8.GetString($b)|ConvertFrom-Json).pid}catch{}" +
		"if(-not($n -gt 0 -and (Get-Process -Id $n -ErrorAction SilentlyContinue))){$n=0};\"$n $h\"")
	var shell := OS.get_environment("SystemRoot").path_join("System32/WindowsPowerShell/v1.0/powershell.exe")
	var out := []
	var code := OS.execute(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Marshalls.raw_to_base64(script.to_utf16_buffer())], out)
	_task_said = str(out[0]).strip_edges() if code == 0 and out.size() > 0 else ""


static func _number(value) -> bool:
	return typeof(value) == TYPE_FLOAT or typeof(value) == TYPE_INT


## The three engines' plugins check in this one order, so the same invite is answered the same way in each. said: what
## the owner check said; {"pending": true} when the file is no longer the one it read.
static func _validate(path: String, said: String) -> Dictionary:
	var checked := said.split(" ")
	if checked.size() != 2 or not checked[0].is_valid_int() or checked[1].length() != 64:
		return {"problem": "%s isn't owned by this user: refused" % path}
	var running := checked[0]
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		return {"problem": "%s can't be read (error %d)" % [path, FileAccess.get_open_error()]}
	var bytes := f.get_buffer(mini(f.get_length(), MAX_BYTES + 1))
	if _sha256(bytes) != checked[1]:
		return {"pending": true}
	var invite = JSON.parse_string(bytes.get_string_from_utf8())
	if typeof(invite) != TYPE_DICTIONARY:
		return {"problem": "%s isn't an invite" % path}
	var id = invite.get("editor_id")
	# Written for a process that had this pid before: left alone, without a word.
	if typeof(id) != TYPE_STRING or id != process_id():
		return {}
	var protocol = invite.get("protocol")
	if not _number(protocol) or protocol != PROTOCOL:
		return {"problem": OTHER_RELEASE}
	var given = invite.get("secret")
	var secret := Marshalls.base64_to_raw(given) if typeof(given) == TYPE_STRING else PackedByteArray()
	if secret.size() < SECRET_BYTES:
		return {"problem": "%s has no usable secret" % path}
	var pid = invite.get("pid")
	if not _number(pid) or pid < 1 or str(int(pid)) != running:
		return {"problem": RUN_ENDED}
	var url := str(invite.get("url", ""))
	var why := check_url(url)
	if why != "":
		return {"problem": why}
	return {"url": url, "secret": secret}


## Why a URL is refused, or "". The only URL the addon connects to: ws:// to 127.0.0.1 or [::1], with no userinfo.
static func check_url(url: String) -> String:
	return "" if _url_shape.search(url) else "%s isn't ws:// on this machine: refused" % url
