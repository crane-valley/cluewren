# Modified by Crane Valley: method, property and class lookups are cached by type, reads being on the harness's tick path; every receiver, object argument and property-path owner passes refused() (docs/adr/0014).
## `rc`: Remote Control compatible calls over Godot's reflection.
##
## objectPath: a node path in the tree (/root/Main/Player; a path relative to /root, e.g. Main/Player, works too), an
## autoload or engine singleton by name (GameState, Input, Engine, Time...), a script class by its class_name or a
## resource by its res:// path (for static functions and resource properties). Values are native (metres,
## {"x","y","z"}): see values.gd.
extends RefCounted

const Values = preload("values.gd")
const Codex = preload("codex.gd")

const FULL_SINGLETONS := ["Input", "InputMap", "Engine", "Time", "Performance", "PhysicsServer2D", "PhysicsServer3D",
	"NavigationServer2D", "NavigationServer3D", "AudioServer", "TranslationServer", "RenderingServer", "Geometry2D",
	"Geometry3D"]
const LISTED_SINGLETONS := {
	"OS": ["get_name", "get_version", "get_locale", "get_locale_language", "is_debug_build", "has_feature",
		"get_processor_count", "get_static_memory_usage", "get_static_memory_peak_usage"],
	"ProjectSettings": ["get_setting", "get_setting_with_override", "has_setting", "get_global_class_list"],
	"DisplayServer": ["get_name", "window_get_size", "window_get_position", "window_get_mode", "window_is_focused",
		"screen_get_size", "screen_get_dpi", "screen_get_scale", "screen_get_refresh_rate", "get_screen_count",
		"mouse_get_position"],
}
const REFUSED_CLASSES := ["FileAccess", "DirAccess", "ConfigFile", "ZIPPacker", "ZIPReader", "PCKPacker", "XMLParser",
	"FileDialog", "CryptoKey", "X509Certificate", "GLTFDocument", "GLTFDocumentExtension", "ResourceFormatLoader",
	"ResourceFormatSaver", "TextServer", "HTTPRequest", "HTTPClient", "StreamPeer", "PacketPeer", "TCPServer",
	"UDPServer", "UDSServer", "DTLSServer", "ENetConnection", "WebRTCPeerConnection", "UPNP", "UPNPDevice",
	"MultiplayerAPI", "MovieWriter", "ResourceImporter", "Expression", "GDExtension", "Thread", "ScriptLanguage",
	"JavaClass", "JavaObject", "JavaScriptObject", "AudioEffectCapture", "AudioEffectRecord", "AudioStreamMicrophone",
	"CameraFeed"]
# A property is refused when its setter is.
const REFUSED_MEMBERS := {
	"Object": ["call", "callv", "call_deferred", "set", "set_deferred", "set_indexed", "get", "get_indexed",
		"set_script"],
	"Node": ["propagate_call", "call_deferred_thread_group", "call_thread_safe", "set_deferred_thread_group",
		"set_thread_safe", "rpc", "rpc_id"],
	"Engine": ["get_singleton", "register_singleton", "unregister_singleton", "register_script_language",
		"unregister_script_language"],
	"RenderingServer": ["shader_set_code"],
	"AudioServer": ["get_input_device_list", "set_input_device"],
	"Resource": ["set_path", "take_over_path", "set_path_cache", "_set_path_cache", "set_id_for_path"],
	"SceneTree": ["change_scene_to_file"],
	"Image": ["load", "load_from_file", "save_png", "save_jpg", "save_exr", "save_dds", "save_webp"],
	"CompressedTexture2D": ["load"],
	"CompressedTexture3D": ["load"],
	"CompressedTextureLayered": ["load"],
	"FontFile": ["load_bitmap_font", "load_dynamic_font"],
	"AudioStreamMP3": ["load_from_file"],
	"AudioStreamOggVorbis": ["load_from_file"],
	"AudioStreamWAV": ["load_from_file", "save_to_wav"],
	"VideoStream": ["set_file"],
	"LinkButton": ["set_uri"],
	"Shader": ["set_code"],
	"ShaderInclude": ["set_code"],
}

var host: Node  # the autoload: for the tree
var _methods := {}  # script or class name -> {method name: info}
var _properties := {}  # script or class name -> {property name: info}
var _classes := {}  # class_name -> script path
var _singletons := {}  # instance id -> singleton name
var _declared := {}  # script -> {member: true}
var _verdicts := {}  # "class|member|op" -> why, "" when allowed
var _class_verdicts := {}  # class -> why the class is refused whole, "" when it isn't
var _class_properties := {}  # class -> {property name: true}


func _init(owner_node: Node) -> void:
	host = owner_node
	for s in Engine.get_singleton_list():
		var o = Engine.get_singleton(s)
		if o:
			_singletons[o.get_instance_id()] = s


func missing(path: String) -> String:
	return "Object %s does not exist" % path


# --- what rc refuses ---------------------------------------------------------------------------------

func refused(obj: Object, member: String, op: String) -> String:
	if obj == null:
		return ""
	var singleton: String = _singletons.get(obj.get_instance_id(), "")
	if singleton != "" and singleton not in FULL_SINGLETONS:
		if not LISTED_SINGLETONS.has(singleton):
			return "%s is refused: rc reaches the engine singletons %s, and some getters of %s" % [singleton,
				", ".join(FULL_SINGLETONS), ", ".join(LISTED_SINGLETONS.keys())]
		if op == "argument" or (member != "" and member not in LISTED_SINGLETONS[singleton]):
			return "%s.%s is refused: of %s rc reaches %s" % [singleton, member, singleton,
				", ".join(LISTED_SINGLETONS[singleton])]
		return ""
	if obj is Script and member != "" and declares(obj, member):
		return ""
	var cls := obj.get_class()
	var whole = _class_verdicts.get(cls)
	if whole == null:
		whole = _decide(cls, "", "argument")
		_class_verdicts[cls] = whole
	# Reading a member is refused only where the whole class is, but for a script's own members and the singletons above.
	if whole != "" or member == "" or ((op == "read" or op == "argument") and not obj is Script):
		return whole
	return refused_in_class(cls, member, op)


# Cached: rc reads are on the harness's tick path.
func refused_in_class(cls: String, member: String, op: String) -> String:
	var key := cls + "|" + member + "|" + op
	var known = _verdicts.get(key)
	if known == null:
		known = _decide(cls, member, op)
		_verdicts[key] = known
	return known


func _decide(cls: String, member: String, op: String) -> String:
	if _editor_class(cls):
		return "%s is refused: the editor's classes are out of rc's reach" % cls
	for c in REFUSED_CLASSES:
		if cls == c or ClassDB.is_parent_class(cls, c):
			return "%s is refused: rc doesn't reach files, the network, threads or code loading (%s)" % [cls, c]
	if member == "" or op == "argument":
		return ""
	if ClassDB.is_parent_class(cls, "Script") and (ClassDB.class_has_method(cls, member) or _class_property(cls, member)):
		return "%s.%s is refused: of a script rc reaches only what it declares" % [cls, member]
	var method := member
	if op == "write":
		if member == "script":
			return "Writing script is refused: rc doesn't change an object's code"
		method = ClassDB.class_get_property_setter(cls, member) if _class_property(cls, member) else ""
	elif op != "call":
		return ""
	if method == "":
		return ""
	for c in REFUSED_MEMBERS:
		if (cls == c or ClassDB.is_parent_class(cls, c)) and method in REFUSED_MEMBERS[c]:
			return "%s.%s is refused: rc doesn't reach files, URLs, code or calls by name through it" % [cls, member]
	return ""


func _editor_class(cls: String) -> bool:
	var api := ClassDB.class_get_api_type(cls)
	return api == ClassDB.API_EDITOR or api == ClassDB.API_EDITOR_EXTENSION


func _class_property(cls: String, name: String) -> bool:
	if not _class_properties.has(cls):
		var names := {}
		for p in ClassDB.class_get_property_list(cls):
			names[p.name] = true
		_class_properties[cls] = names
	return _class_properties[cls].has(name)


func declares(script: Script, member: String) -> bool:
	if not _declared.has(script):
		var known := {}
		for m in script.get_script_method_list():
			known[m.name] = true
		for p in script.get_script_property_list():
			known[p.name] = true
		for k in script.get_script_constant_map():
			known[k] = true
		_declared[script] = known
	return _declared[script].has(member)


func refused_arguments(json, values) -> String:
	var flat := str(json)
	var why := _refused_paths(json, 0) if "/" in flat or "\\" in flat else ""
	return why if why != "" else _refused_values(values, 0)


const TOO_DEEP := "A value nested more than %d deep is refused: rc can't check it" % Values.MAX_DEPTH


func _refused_paths(j, depth: int) -> String:
	if depth > Values.MAX_DEPTH:
		return TOO_DEEP
	match typeof(j):
		TYPE_STRING:
			if ("/" in j or "\\" in j) and Codex.data_path(j):
				return "%s is refused: nothing under res://.godot/ is loaded for rc" % j
		TYPE_ARRAY:
			for x in j:
				var why := _refused_paths(x, depth + 1)
				if why != "":
					return why
		TYPE_DICTIONARY:
			for k in j:
				var why := _refused_paths(j[k], depth + 1)
				if why != "":
					return why
	return ""


# After conversion too: a default argument or an inferred value never came through the JSON.
func _refused_values(v, depth: int) -> String:
	if depth > Values.MAX_DEPTH:
		return TOO_DEEP
	match typeof(v):
		TYPE_OBJECT:
			return refused(v, "", "argument") if is_instance_valid(v) else ""
		TYPE_STRING, TYPE_STRING_NAME, TYPE_NODE_PATH:
			var text := str(v)
			if ("/" in text or "\\" in text) and Codex.data_path(text):
				return "%s is refused: nothing under res://.godot/ is loaded for rc" % text
		TYPE_ARRAY, TYPE_PACKED_STRING_ARRAY:
			for x in v:
				var t := typeof(x)
				if t == TYPE_BOOL or t == TYPE_INT or t == TYPE_FLOAT:
					continue
				var why := _refused_values(x, depth + 1)
				if why != "":
					return why
		TYPE_DICTIONARY:
			for k in v:
				var why := _refused_values(k, depth + 1)
				if why == "":
					why = _refused_values(v[k], depth + 1)
				if why != "":
					return why
	return ""


# --- targets -------------------------------------------------------------------------------------------

## The object a path names, or null.
func resolve(path: String) -> Object:
	if path == "":
		return null
	var tree := host.get_tree()
	if tree == null:
		return null
	var root := tree.root
	if path.begins_with("/"):
		return root.get_node_or_null(NodePath(path))
	if path.begins_with("res://") or path.begins_with("uid://"):
		if not Codex.data_path(path) and ResourceLoader.exists(path):
			return load(path)
		return null
	if Engine.has_singleton(path):
		return null if _editor_class(path) else Engine.get_singleton(path)
	var node := root.get_node_or_null(NodePath(path))
	if node:
		return node
	if tree.current_scene:
		node = tree.current_scene.get_node_or_null(NodePath(path))
		if node:
			return node
	if _classes.is_empty():
		for c in ProjectSettings.get_global_class_list():
			_classes[str(c.get("class", ""))] = str(c.get("path", ""))
	return load(_classes[path]) if _classes.has(path) and not Codex.data_path(_classes[path]) else null


func _target(path: String, op: String) -> Array:
	if (path.begins_with("res://") or path.begins_with("uid://")) and Codex.data_path(path):
		return [null, "%s is refused: nothing under res://.godot/ is loaded for rc" % path, 403]
	if Engine.has_singleton(path) and _editor_class(path):
		return [null, "%s is refused: the editor's classes are out of rc's reach" % path, 403]
	var obj := resolve(path)
	if obj == null:
		return [null, missing(path)]
	var why := refused(obj, "", op)
	return [obj] if why == "" else [null, why, 403]


func handle(verb: String, body) -> Array:
	if typeof(body) != TYPE_DICTIONARY:
		return [false, "rc needs a body"]
	match verb:
		"call":
			return _call(body)
		"property":
			return _property(body)
		"describe":
			return _describe(body)
	return [false, "unknown rc verb %s" % verb]


# --- call -------------------------------------------------------------------------------------------

## What the lists are cached under: instances of one script or class have the same members.
func _type_key(obj: Object):
	var script = obj.get_script()
	return obj if obj is Script else (script if script is Script else obj.get_class())


func method_info(obj: Object, name: String) -> Dictionary:
	var key = _type_key(obj)
	if not _methods.has(key):
		var known := {}
		for m in obj.get_method_list():
			known[m.name] = m
		var script = obj.get_script() if not (obj is Script) else obj
		if script is Script:
			for m in script.get_script_method_list():
				known[m.name] = m
		_methods[key] = known
	return _methods[key].get(name, {})


func _call(body: Dictionary) -> Array:
	var path := str(body.get("objectPath", ""))
	var target := _target(path, "call")
	if target[0] == null:
		return [false] + target.slice(1)
	var obj: Object = target[0]
	var fname := str(body.get("functionName", ""))
	var why := refused(obj, fname, "call")
	if why != "":
		return [false, why, 403]
	var info := method_info(obj, fname)
	if not obj.has_method(fname) and info.is_empty():
		return [false, "Function: %s does not exist on object: %s" % [fname, path]]
	var params = body.get("parameters", {})
	if params == null:
		params = {}
	var args: Array = info.get("args", [])
	var defaults: Array = info.get("default_args", [])
	var first_default := args.size() - defaults.size()
	var argv := []
	if typeof(params) == TYPE_ARRAY:
		for i in params.size():
			var a: Dictionary = args[i] if i < args.size() else {}
			argv.append(Values.from_json(params[i], a.get("type", TYPE_NIL), null, a, resolve))
		# The defaults callv would fill in, here: what is checked is what is called with.
		for i in range(params.size(), args.size()):
			if i < first_default:
				break
			argv.append(defaults[i - first_default])
	else:
		var used := {}
		for i in args.size():
			var a: Dictionary = args[i]
			if params.has(a.name):
				argv.append(Values.from_json(params[a.name], a.get("type", TYPE_NIL), null, a, resolve))
				used[a.name] = true
			elif i >= first_default:
				argv.append(defaults[i - first_default])
			else:
				return [false, "%s on %s: missing parameter %s" % [fname, path, a.name]]
		if info.get("flags", 0) & METHOD_FLAG_VARARG:
			for k in params:
				if not used.has(k):
					argv.append(Values.infer(params[k]))
	why = refused_arguments(params, argv)
	if why != "":
		return [false, why, 403]
	var ret = obj.callv(fname, argv)
	return [true, {"ReturnValue": Values.to_json(ret)}]


# --- property ---------------------------------------------------------------------------------------

func property_info(obj: Object, name: String) -> Dictionary:
	var key = _type_key(obj)
	if not _properties.has(key):
		var known := {}
		for p in obj.get_property_list():
			if not known.has(p.name):
				known[p.name] = p
		_properties[key] = known
	var info: Dictionary = _properties[key].get(name, {})
	if info.is_empty() and name in obj:
		# (a property of this instance alone: _get_property_list can differ between instances)
		for p in obj.get_property_list():
			if p.name == name:
				return p
	return info


func script_constants(obj: Object) -> Dictionary:
	var script = obj if obj is Script else obj.get_script()
	if script is Script:
		return script.get_script_constant_map()
	return {}


func _readable(p: Dictionary) -> bool:
	var usage: int = p.get("usage", 0)
	if usage & (PROPERTY_USAGE_CATEGORY | PROPERTY_USAGE_GROUP | PROPERTY_USAGE_SUBGROUP):
		return false
	return usage & (PROPERTY_USAGE_EDITOR | PROPERTY_USAGE_STORAGE | PROPERTY_USAGE_SCRIPT_VARIABLE) != 0


func _refused_path(obj: Object, name: String, write: bool) -> String:
	if ":" not in name:
		return refused(obj, name, "write" if write else "read")
	var parts := name.split(":")
	var owner = obj
	# get_indexed and set_indexed step through dictionaries and arrays as well as objects: so does this.
	for i in parts.size():
		var last := i == parts.size() - 1
		match typeof(owner):
			TYPE_OBJECT:
				if not is_instance_valid(owner):
					return ""
				var why := refused(owner, parts[i], "write" if write and last else "read")
				if why != "" or last:
					return why
				owner = owner.get(parts[i])
			TYPE_DICTIONARY:
				owner = owner.get(parts[i], owner.get(StringName(parts[i])))
			TYPE_ARRAY:
				var at := int(parts[i]) if parts[i].is_valid_int() else -1
				owner = owner[at] if at >= 0 and at < owner.size() else null
			_:
				return ""
	return ""


func _property(body: Dictionary) -> Array:
	var path := str(body.get("objectPath", ""))
	var target := _target(path, "read")
	if target[0] == null:
		return [false] + target.slice(1)
	var obj: Object = target[0]
	var name := str(body.get("propertyName", ""))
	var new_value = body.get("propertyValue", null)
	var write: bool = typeof(new_value) == TYPE_DICTIONARY or str(body.get("access", "")).begins_with("WRITE")
	if name == "":
		if write:
			return [false, "A write needs propertyName"]
		var all := {}
		for p in obj.get_property_list():
			if _readable(p) and not all.has(p.name) and refused(obj, p.name, "read") == "":
				all[p.name] = Values.prop_to_json(obj.get(p.name), p)
		var consts := script_constants(obj)
		for k in consts:
			if not all.has(k) and refused(obj, k, "read") == "":
				all[k] = Values.to_json(consts[k])
		return [true, all]
	# A plain read of an ordinary object was settled by _target; only scripts, singletons and paths have more to ask.
	var why := ""
	if write or ":" in name or obj is Script or _singletons.has(obj.get_instance_id()):
		why = _refused_path(obj, name, write)
	if why != "":
		return [false, why, 403]
	var base := name.get_slice(":", 0)
	var info := property_info(obj, base)
	var consts := script_constants(obj)
	if info.is_empty() and not (base in obj):
		if consts.has(base) and not write:
			return [true, {name: Values.to_json(consts[base])}]
		return [false, "Property %s does not exist on object %s" % [name, path]]
	if write:
		if typeof(new_value) != TYPE_DICTIONARY:
			return [false, "No value given for %s" % name]
		var value = null
		if new_value.has(name):
			value = new_value[name]
		elif new_value.size() == 1:
			value = new_value.values()[0]
		else:
			return [false, "No value given for %s" % name]
		var current = obj.get_indexed(NodePath(name)) if ":" in name else obj.get(name)
		var type: int = TYPE_NIL if ":" in name else info.get("type", TYPE_NIL)
		var v = Values.from_json(value, type, current, {} if ":" in name else info, resolve)
		why = refused_arguments(value, v)
		if why != "":
			return [false, why, 403]
		if ":" in name:
			obj.set_indexed(NodePath(name), v)
		else:
			obj.set(name, v)
		return [true, {}]
	if ":" in name:
		return [true, {name: Values.to_json(obj.get_indexed(NodePath(name)))}]
	return [true, {name: Values.prop_to_json(obj.get(name), info)}]


# --- describe ---------------------------------------------------------------------------------------

func class_label(obj: Object) -> String:
	var script = obj.get_script()
	if script is Script:
		var g := str(script.get_global_name())
		if g != "":
			return "%s (%s)" % [g, obj.get_class()]
		if script.resource_path != "":
			return "%s (%s)" % [script.resource_path, obj.get_class()]
	return obj.get_class()


func _describe(body: Dictionary) -> Array:
	var path := str(body.get("objectPath", ""))
	var target := _target(path, "read")
	if target[0] == null:
		return [false] + target.slice(1)
	var obj: Object = target[0]
	var props := []
	var seen := {}
	var script = obj if obj is Script else obj.get_script()
	if script is Script:
		for p in script.get_script_property_list():
			if _readable(p) and not seen.has(p.name):
				seen[p.name] = true
				props.append({"Name": p.name, "Description": "script variable", "Type": Values.type_label(p)})
		var consts: Dictionary = script.get_script_constant_map()
		for k in consts:
			if not seen.has(k) and typeof(consts[k]) != TYPE_OBJECT:
				seen[k] = true
				props.append({"Name": k, "Description": "script constant (read only) = %s" % str(consts[k]), "Type": type_string(typeof(consts[k]))})
	for p in obj.get_property_list():
		if _readable(p) and not seen.has(p.name) and refused(obj, p.name, "read") == "":
			seen[p.name] = true
			props.append({"Name": p.name, "Description": "", "Type": Values.type_label(p)})
	var funcs := []
	var fseen := {}
	if script is Script:
		for m in script.get_script_method_list():
			if fseen.has(m.name):
				continue
			fseen[m.name] = true
			funcs.append(_function(m, "script function"))
	for m in obj.get_method_list():
		if fseen.has(m.name) or str(m.name).begins_with("_") or refused(obj, m.name, "call") != "":
			continue
		fseen[m.name] = true
		funcs.append(_function(m, ""))
	var name := ""
	if obj is Node:
		name = str(obj.name)
	elif obj is Resource:
		name = obj.resource_path.get_file() if obj.resource_path != "" else obj.resource_name
	else:
		name = obj.get_class()
	return [true, {"Name": name, "Class": class_label(obj), "Properties": props, "Functions": funcs}]


func _function(m: Dictionary, description: String) -> Dictionary:
	var arguments := []
	for a in m.get("args", []):
		arguments.append({"Name": a.name, "Type": Values.type_label(a)})
	var ret: Dictionary = m.get("return", {})
	var out := {"Name": m.name, "Description": description, "Arguments": arguments}
	if not ret.is_empty() and (ret.get("type", TYPE_NIL) != TYPE_NIL or ret.get("usage", 0) & PROPERTY_USAGE_NIL_IS_VARIANT):
		out["Returns"] = Values.type_label(ret)
	return out
