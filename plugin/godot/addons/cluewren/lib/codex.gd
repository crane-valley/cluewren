# Modified by Crane Valley: only the project summary stays, written to .godot/cluewren_codex/; the manifest and read
# paths are removed, the agent reading the project with its own tools.
## The addon's summary of the project for the agent (main scene, autoloads, input actions, global classes, each scene's
## nodes), written to .godot/cluewren_codex/summary.txt, or to user://cluewren_codex/ when res:// is
## read-only. The project's files are its .gd .cs .tscn .tres .gdshader files, skipping directories whose name starts
## with "." (.godot, .git, .import...), node_modules, addons/cluewren and any directory holding a .gdignore.
extends RefCounted

const EXTENSIONS := ["gd", "cs", "tscn", "tres", "gdshader"]
const Keys = preload("keys.gd")


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
		if path.get_extension().to_lower() in ["tscn", "scn"]:
			out.append(path)
	out.sort()
	return out


## A scene by res:// path, or by its file's name ("level" or "level.tscn"), searched in the project.
static func find_scene(name: String) -> String:
	if name.begins_with("res://") or name.begins_with("uid://"):
		return name if ResourceLoader.exists(name) else ""
	var want := name.get_file()
	if not want.ends_with(".tscn") and not want.ends_with(".scn"):
		want += ".tscn"
	for path in scenes():
		if str(path).get_file().to_lower() == want.to_lower():
			return path
	return ""


## Write the project summary; returns where ("" when nowhere was writable).
static func write_summary() -> String:
	var text := summary_text()
	for dir in ["res://.godot/cluewren_codex", "user://cluewren_codex"]:
		if DirAccess.make_dir_recursive_absolute(dir) != OK and not DirAccess.dir_exists_absolute(dir):
			continue
		var path: String = dir + "/summary.txt"
		var f := FileAccess.open(path, FileAccess.WRITE)
		if f == null:
			continue
		f.store_string(text)
		f.close()
		return path
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
		if s.get_extension() != "tscn":
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
