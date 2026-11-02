# Modified by Crane Valley: method, property and class lookups are cached by type, reads being on the harness's tick path.
## `rc`: Remote Control compatible calls over Godot's reflection.
##
## objectPath: a node path in the tree (/root/Main/Player; a path relative to /root, e.g. Main/Player, works too), an
## autoload or engine singleton by name (GameState, Input, Engine, ProjectSettings, OS, Time...), a script class by its
## class_name or a resource by its res:// path (for static functions and resource properties). Values are native
## (metres, {"x","y","z"}): see values.gd.
extends RefCounted

const Values = preload("values.gd")

var host: Node  # the autoload: for the tree
var _methods := {}  # script or class name -> {method name: info}
var _properties := {}  # script or class name -> {property name: info}
var _classes := {}  # class_name -> script path


func _init(owner_node: Node) -> void:
	host = owner_node


func missing(path: String) -> String:
	return "Object %s does not exist" % path


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
		if ResourceLoader.exists(path):
			return load(path)
		return null
	if Engine.has_singleton(path):
		return Engine.get_singleton(path)
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
	return load(_classes[path]) if _classes.has(path) else null


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
	var obj := resolve(path)
	if obj == null:
		return [false, missing(path)]
	var fname := str(body.get("functionName", ""))
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


func _property(body: Dictionary) -> Array:
	var path := str(body.get("objectPath", ""))
	var obj := resolve(path)
	if obj == null:
		return [false, missing(path)]
	var name := str(body.get("propertyName", ""))
	var new_value = body.get("propertyValue", null)
	var write: bool = typeof(new_value) == TYPE_DICTIONARY or str(body.get("access", "")).begins_with("WRITE")
	if name == "":
		if write:
			return [false, "A write needs propertyName"]
		var all := {}
		for p in obj.get_property_list():
			if _readable(p) and not all.has(p.name):
				all[p.name] = Values.prop_to_json(obj.get(p.name), p)
		var consts := script_constants(obj)
		for k in consts:
			if not all.has(k):
				all[k] = Values.to_json(consts[k])
		return [true, all]
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
	var obj := resolve(path)
	if obj == null:
		return [false, missing(path)]
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
		if _readable(p) and not seen.has(p.name):
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
		if fseen.has(m.name) or str(m.name).begins_with("_"):
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
