# Modified by Crane Valley: renamed from codex.gd; only the project summary stays, written to .cluewren/export/; the
# manifest and read paths are removed, the agent reading the project with its own tools; nothing under res://.godot/ or
# res://.cluewren/ is found as a scene.
## The addon's export of the project for the agent: a summary (main scene, autoloads, input actions, global classes, each
## text scene's nodes) written to res://.cluewren/export/summary.txt. The project's files are its .gd .cs .tscn .scn .tres
## .gdshader files, skipping directories whose name starts with "." (.godot, .cluewren, .git, .import...), node_modules,
## addons/cluewren and any directory holding a .gdignore.
extends RefCounted

const EXTENSIONS := ["gd", "cs", "tscn", "scn", "tres", "gdshader"]
const SCENE_EXTENSIONS := ["tscn", "scn"]
const HOME := "res://.cluewren"
const SUMMARY := HOME + "/export/summary.txt"
const Keys = preload("keys.gd")

const DATA_FOLDERS := [".cluewren", ".godot"]
const KEPT := 4096  # (the longest string a verdict is kept for, and how many are kept)

static var _known := {}  # the same paths come back tick after tick
static var _spelled := {}
# The 8.3 names Windows can give .cluewren and .godot: CLUEWR~1 to ~4, then two letters, four hex digits and ~N.
static var _short_shape := RegEx.create_from_string("^(cl|go)[^.]*~[0-9]+$")


static func skipped_dir(name: String) -> bool:
	return name.begins_with(".") or name == "node_modules"


static func walk(dir: String, rel: String, out: Array) -> void:
	var d := DirAccess.open(dir)
	if d == null:
		return
	d.include_hidden = false
	var files := d.get_files()
	if files.has(".gdignore"):
		return
	for f in files:
		if f.get_extension().to_lower() in EXTENSIONS:
			out.append(rel + f)
	for sub in d.get_directories():
		if skipped_dir(sub):
			continue
		if rel == "addons/" and sub == "cluewren":
			continue
		walk(dir.path_join(sub), rel + sub + "/", out)


## The project's scenes, as res:// paths, sorted.
static func scenes() -> Array:
	var found := []
	walk("res://", "", found)
	var out := []
	for f in found:
		var path := "res://" + str(f)
		if path.get_extension().to_lower() in SCENE_EXTENSIONS:
			out.append(path)
	out.sort()
	return out


# res://.godot/ is the engine's cache and res://.cluewren/ holds Cluewren's data, what the harness wrote included: rc and
# a scene change never load from either. Decided by the text alone: what a path names is never opened to find out, so it
# is refused under any root, drive or share it is reached by.
static func data_path(path: String) -> bool:
	if _known.has(path):
		return _known[path]
	if _known.size() > KEPT:
		_known.clear()
	var found := _names_data_folder(path)
	if not found and "%" in path:
		found = _names_data_folder(path.uri_decode())
	if not found and path.strip_edges().begins_with("uid://"):
		var id := ResourceUID.text_to_id(path.strip_edges())
		found = ResourceUID.has_id(id) and _names_data_folder(ResourceUID.get_id_path(id))
	if path.length() <= KEPT:
		_known[path] = found
	return found


## A segment names a data folder cut at its first ":" (an NTFS stream) and without the dots and spaces Windows drops
## from a name's end. A drive's letter comes off first: C:.cluewren is .cluewren in that drive's current folder. Which
## 8.3 name the folder got is not asked: any of the shape Windows could give it counts.
static func _names_data_folder(text: String) -> bool:
	var p := text.replace("\\", "/")
	if p.length() >= 2 and p[1] == ":" and (p.unicode_at(0) | 0x20) >= 0x61 and (p.unicode_at(0) | 0x20) <= 0x7a:
		p = p.substr(2)
	for part in p.split("/", false):
		var name := part.get_slice(":", 0).rstrip(". ").to_lower()
		if name in DATA_FOLDERS or (name.length() <= 8 and _short_shape.search(name) != null):
			return true
	return false


## The one spelling rc compares a path by, made from its text alone: a uid:// path as its file's, a relative one under
## res://, globalized, simplified, lowercase.
static func resolved(path: String) -> String:
	if _spelled.has(path):
		return _spelled[path]
	if _spelled.size() > KEPT:
		_spelled.clear()
	var p := path.strip_edges().replace("\\", "/")
	if p.begins_with("uid://"):
		var id := ResourceUID.text_to_id(p)
		if ResourceUID.has_id(id):
			p = ResourceUID.get_id_path(id)
	if p.is_relative_path():
		p = "res://" + p
	var full := ProjectSettings.globalize_path(p).simplify_path().to_lower()
	if path.length() <= KEPT:
		_spelled[path] = full
	return full


static func refused_scene(path: String) -> String:
	return "%s is refused: nothing under res://.godot/ or res://.cluewren/ is opened as a scene" % path if data_path(path) else ""


## A scene by res:// path or by its file name, with or without .tscn or .scn. A map comes from
## harness code: any other path (a drive's, a share's, a device's, one with "." or ".." in it, which Godot keeps at the
## start of a res:// path) is no scene, and nothing is asked about it.
static func find_scene(name: String) -> String:
	var res := name
	if name.begins_with("uid://"):
		var id := ResourceUID.text_to_id(name)
		res = ResourceUID.get_id_path(id) if ResourceUID.has_id(id) else ""
	if name.begins_with("res://") or name.begins_with("uid://"):
		return res if _plain_res(res) and not data_path(res) and ResourceLoader.exists(res) else ""
	if "/" in name or "\\" in name or ":" in name:
		return ""
	var want := name.to_lower()
	var has_extension := want.get_extension() in SCENE_EXTENSIONS
	var preferred := want if has_extension else want + ".tscn"
	var binary := ""
	for path in scenes():
		var file := str(path).get_file().to_lower()
		if file == preferred:
			return path
		if not has_extension and binary == "" and file == want + ".scn":
			binary = path
	return binary


static func _plain_res(path: String) -> bool:
	if not path.begins_with("res://"):
		return false
	for part in path.substr(6).split("/"):
		if part == "" or part == "." or part == ".." or "\\" in part or ":" in part:
			return false
	return true


## Write the project summary; returns why it couldn't be written, or "".
static func write_summary() -> String:
	var text := summary_text()
	var made := not DirAccess.dir_exists_absolute(HOME)
	var dir := SUMMARY.get_base_dir()
	if DirAccess.make_dir_recursive_absolute(dir) != OK and not DirAccess.dir_exists_absolute(dir):
		return "The project's export wasn't written: %s can't be made" % dir
	# Whoever makes .cluewren keeps it out of the user's repository; one the brain made has its .gitignore already.
	if made:
		var ignore := FileAccess.open(HOME + "/.gitignore", FileAccess.WRITE)
		if ignore != null:
			ignore.store_string("*\n")
			ignore.close()
	var f := FileAccess.open(SUMMARY, FileAccess.WRITE)
	if f == null:
		return "The project's export wasn't written: %s can't be written (error %d)" % [SUMMARY, FileAccess.get_open_error()]
	f.store_string(text)
	f.close()
	return ""


static func summary_text() -> String:
	var lines := PackedStringArray()
	lines.append("Godot project: %s (Godot %s)" % [ProjectSettings.get_setting("application/config/name", ""), Engine.get_version_info().string])
	lines.append("Main scene: %s" % ProjectSettings.get_setting("application/run/main_scene", ""))
	lines.append("")
	lines.append("Autoloads (nodes under /root, created before the main scene, in this order):")
	for p in ProjectSettings.get_property_list():
		var n: String = p.name
		if n.begins_with("autoload/"):
			var name := n.substr(9)
			if name == "Cluewren":
				continue
			lines.append("  /root/%s = %s" % [name, str(ProjectSettings.get_setting(n)).trim_prefix("*")])
	lines.append("")
	lines.append("Input actions (InputMap; project.godot [input]):")
	for p in ProjectSettings.get_property_list():
		var n: String = p.name
		if n.begins_with("input/") and not Keys.is_default_ui(n.substr(6)):
			var v = ProjectSettings.get_setting(n)
			var keys := []
			if typeof(v) == TYPE_DICTIONARY:
				for ev in v.get("events", []):
					if ev is InputEvent:
						keys.append(ev.as_text())
			lines.append("  %s: %s" % [n.substr(6), ", ".join(keys)])
	lines.append("")
	lines.append("Global script classes (class_name):")
	for c in ProjectSettings.get_global_class_list():
		lines.append("  %s extends %s: %s" % [c.get("class", ""), c.get("base", ""), c.get("path", "")])
	lines.append("")
	lines.append("Physics: 3D gravity %s m/s^2, 2D gravity %s px/s^2, %s ticks/s" % [ProjectSettings.get_setting("physics/3d/default_gravity", 9.8), ProjectSettings.get_setting("physics/2d/default_gravity", 980), ProjectSettings.get_setting("physics/common/physics_ticks_per_second", 60)])
	lines.append("")
	lines.append("Scenes (node tree: name, type, script, instanced scene, groups):")
	var scenes := []
	walk("res://", "", scenes)
	for s in scenes:
		if s.get_extension().to_lower() != "tscn":
			continue
		lines.append("  res://%s" % s)
		lines.append_array(_scene_outline("res://" + s))
	return "\n".join(lines) + "\n"


## A .tscn file's node tree from its text (no loading).
static func _scene_outline(path: String) -> PackedStringArray:
	var out := PackedStringArray()
	var f := FileAccess.open(path, FileAccess.READ)
	if f == null:
		return out
	var ext := {}  # id -> path
	var re_ext := RegEx.create_from_string('^\\[ext_resource [^\\]]*path="([^"]+)"[^\\]]*id="([^"]+)"')
	var re_ext2 := RegEx.create_from_string('^\\[ext_resource [^\\]]*id="([^"]+)"[^\\]]*path="([^"]+)"')
	var re_node := RegEx.create_from_string('^\\[node name="([^"]+)"(.*)\\]')
	var re_attr := RegEx.create_from_string('(\\w+)=("[^"]*"|ExtResource\\("[^"]+"\\)|\\[[^\\]]*\\])')
	var current := -1
	var count := 0
	while not f.eof_reached():
		var line := f.get_line()
		var m := re_ext.search(line)
		if m:
			ext[m.get_string(2)] = m.get_string(1)
			continue
		m = re_ext2.search(line)
		if m:
			ext[m.get_string(1)] = m.get_string(2)
			continue
		m = re_node.search(line)
		if m:
			count += 1
			if count > 400:
				out.append("    ... (more nodes)")
				break
			var attrs := {}
			for a in re_attr.search_all(m.get_string(2)):
				attrs[a.get_string(1)] = a.get_string(2).trim_prefix('"').trim_suffix('"')
			var name := m.get_string(1)
			var parent: String = attrs.get("parent", "")
			var where := name if parent == "" else (name if parent == "." else parent + "/" + name)
			var desc := "    %s" % where
			if attrs.has("type"):
				desc += " (%s)" % attrs.type
			if attrs.has("instance"):
				desc += " instance of %s" % ext.get(_ext_id(attrs.instance), attrs.instance)
			if attrs.has("groups"):
				desc += " groups %s" % attrs.groups
			out.append(desc)
			current = out.size() - 1
			continue
		if current >= 0 and line.begins_with("script = ExtResource("):
			out[current] = out[current] + " script " + ext.get(_ext_id(line.substr(9)), "")
	return out


static func _ext_id(s: String) -> String:
	var a := s.find('"')
	var b := s.find('"', a + 1)
	return s.substr(a + 1, b - a - 1) if a >= 0 and b > a else s
