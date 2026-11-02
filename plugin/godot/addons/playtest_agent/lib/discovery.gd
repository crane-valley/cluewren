## Finding the brain: brain.json, written by src/PlaytestAgent/Bridge/DiscoveryFile.cs. Nothing here decides anything on
## the file's word alone: its owner is checked before it is read, and any URL must name this machine.
@tool
extends RefCounted

const PENDING := "pending"
const SECRET_BYTES := 32
const NOT_RUNNING := "No brain is running on this machine"

static var _url_shape := RegEx.create_from_string("^ws://(127\\.0\\.0\\.1|\\[::1\\]):[0-9]{1,5}(/[A-Za-z0-9/_-]*)?$")

var _task := -1
var _task_stamp := -1
var _task_said := ""
var _stamp := -1
var _verdict := ""


static func file_path() -> String:
	var root := OS.get_environment("LOCALAPPDATA")
	return "" if root == "" else root.path_join("PlaytestAgent").path_join("brain.json")


## "" when brain.json is this user's and its brain is alive, PENDING while that is being checked, else why it can't be
## used. GDScript has no API for a file's owner or another process, so PowerShell answers, off the game's thread and
## once per brain: a verdict stands until the file changes, and every brain writes a new file.
func check(path: String) -> String:
	if OS.get_name() != "Windows":
		return "The bridge is Windows only: brain.json's owner can't be checked here"
	if path == "" or not FileAccess.file_exists(path):
		return NOT_RUNNING
	if _task >= 0:
		if not WorkerThreadPool.is_task_completed(_task):
			return PENDING
		WorkerThreadPool.wait_for_task_completion(_task)
		_task = -1
		_stamp = _task_stamp
		_verdict = _task_said
	var stamp := FileAccess.get_modified_time(path)
	if stamp == _stamp:
		return _verdict
	var said := _parse(path)
	if said.has("error"):
		return said.error
	_task_stamp = stamp
	_task = WorkerThreadPool.add_task(_ask_windows.bind(path, int(said.get("pid", 0))), false, "Playtest Agent brain check")
	return PENDING


## Wait for a check still running: its task holds this object.
func finish() -> void:
	if _task >= 0:
		WorkerThreadPool.wait_for_task_completion(_task)
		_task = -1


func _ask_windows(path: String, pid: int) -> void:
	if pid <= 0:
		_task_said = "%s names no process" % path
		return
	var folder := path.get_base_dir().replace("'", "''")
	var file := path.replace("'", "''")
	var script := ("$ErrorActionPreference='Stop';$me=[Security.Principal.WindowsIdentity]::GetCurrent().User;" +
		"foreach($p in @('%s','%s')){$i=Get-Item -LiteralPath $p -Force;" % [folder, file] +
		"if($i.Attributes -band [IO.FileAttributes]::ReparsePoint){'link';exit}" +
		"if($i.GetAccessControl('Owner').GetOwner([Security.Principal.SecurityIdentifier]) -ne $me){'other';exit}}" +
		"if(-not(Get-Process -Id %d -ErrorAction SilentlyContinue)){'dead';exit}'ok'" % pid)
	var shell := OS.get_environment("SystemRoot").path_join("System32/WindowsPowerShell/v1.0/powershell.exe")
	var out := []
	var code := OS.execute(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Marshalls.raw_to_base64(script.to_utf16_buffer())], out)
	var word: String = str(out[0]).strip_edges() if out.size() > 0 else ""
	if code == 0 and word == "ok":
		_task_said = ""
	elif word == "dead":
		_task_said = "The brain in brain.json isn't running"
	else:
		_task_said = "%s isn't owned by this user: refused" % path


static func _parse(path: String) -> Dictionary:
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		return {"error": "%s can't be read (error %d)" % [path, FileAccess.get_open_error()]}
	var said = JSON.parse_string(f.get_as_text())
	if typeof(said) != TYPE_DICTIONARY:
		return {"error": "%s isn't a brain announcement" % path}
	return said


## {"url", "secret"} of a brain check() passed, or {"error"}.
static func read(path: String) -> Dictionary:
	var said := _parse(path)
	if said.has("error"):
		return said
	var secret := Marshalls.base64_to_raw(str(said.get("secret", "")))
	if secret.size() < SECRET_BYTES:
		return {"error": "%s has no usable secret" % path}
	return {"url": str(said.get("url", "")), "secret": secret}


## Why a URL is refused, or "". The only URL the addon connects to: ws:// to 127.0.0.1 or [::1], with no userinfo.
static func check_url(url: String) -> String:
	return "" if _url_shape.search(url) else "%s isn't ws:// on this machine: refused" % url
