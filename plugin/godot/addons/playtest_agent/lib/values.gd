## Godot values as JSON for `rc` (the native frame: metres, lowercase vector keys) and JSON back into values.
##
## To JSON: numbers, booleans and strings as they are (NaN and infinities as null); StringName and NodePath as
## strings; Vector2/3/4 (and their int forms) as {"x","y"[,"z"[,"w"]]}; Color {"r","g","b","a"}; Quaternion
## {"x","y","z","w"}; Rect2/AABB {"position","size"}; Basis {"x","y","z"} (its columns); Transform3D {"basis",
## "origin"}; Transform2D {"x","y","origin"}; Plane {"normal","d"}; a node as its path in the tree, a resource as its
## res:// path ("<Class>" for one built into a scene without a path of its own); arrays and packed arrays as lists;
## dictionaries as objects keyed by the key's text; an int property with an enum hint as the enum's name.
## From JSON: by the type the target declares (a property's or a parameter's), else by the current value's type, else by
## the value's shape ({"x","y","z"} is a Vector3).
extends RefCounted

const MAX_DEPTH := 8
const MAX_ITEMS := 4000


static func num(x):
	if typeof(x) == TYPE_FLOAT and not is_finite(x):
		return null
	return x


static func obj_ref(o) -> String:
	if not is_instance_valid(o):
		return ""
	if o is Node:
		return str(o.get_path()) if o.is_inside_tree() else "<%s>" % o.get_class()
	if o is Resource:
		if o.resource_path != "":
			return o.resource_path
		return "<%s>" % o.get_class()
	return "<%s>" % o.get_class()


static func vec(v) -> Dictionary:
	match typeof(v):
		TYPE_VECTOR2, TYPE_VECTOR2I:
			return {"x": num(v.x), "y": num(v.y)}
		TYPE_VECTOR3, TYPE_VECTOR3I:
			return {"x": num(v.x), "y": num(v.y), "z": num(v.z)}
		TYPE_VECTOR4, TYPE_VECTOR4I, TYPE_QUATERNION:
			return {"x": num(v.x), "y": num(v.y), "z": num(v.z), "w": num(v.w)}
	return {}


static func to_json(v, depth := 0):
	if depth > MAX_DEPTH:
		return null
	match typeof(v):
		TYPE_NIL:
			return null
		TYPE_BOOL, TYPE_INT, TYPE_STRING:
			return v
		TYPE_FLOAT:
			return num(v)
		TYPE_STRING_NAME, TYPE_NODE_PATH:
			return str(v)
		TYPE_VECTOR2, TYPE_VECTOR2I, TYPE_VECTOR3, TYPE_VECTOR3I, TYPE_VECTOR4, TYPE_VECTOR4I, TYPE_QUATERNION:
			return vec(v)
		TYPE_COLOR:
			return {"r": v.r, "g": v.g, "b": v.b, "a": v.a}
		TYPE_RECT2, TYPE_RECT2I, TYPE_AABB:
			return {"position": vec(v.position), "size": vec(v.size)}
		TYPE_BASIS:
			return {"x": vec(v.x), "y": vec(v.y), "z": vec(v.z)}
		TYPE_TRANSFORM3D:
			return {"basis": to_json(v.basis, depth + 1), "origin": vec(v.origin)}
		TYPE_TRANSFORM2D:
			return {"x": vec(v.x), "y": vec(v.y), "origin": vec(v.origin)}
		TYPE_PLANE:
			return {"normal": vec(v.normal), "d": num(v.d)}
		TYPE_PROJECTION:
			return [vec(v.x), vec(v.y), vec(v.z), vec(v.w)]
		TYPE_RID:
			return v.get_id()
		TYPE_OBJECT:
			return obj_ref(v)
		TYPE_CALLABLE, TYPE_SIGNAL:
			return str(v)
		TYPE_DICTIONARY:
			var out := {}
			var n := 0
			for k in v:
				n += 1
				if n > MAX_ITEMS:
					break
				var key = to_json(k, depth + 1)
				out[key if typeof(key) == TYPE_STRING else JSON.stringify(key)] = to_json(v[k], depth + 1)
			return out
		TYPE_ARRAY, TYPE_PACKED_BYTE_ARRAY, TYPE_PACKED_INT32_ARRAY, TYPE_PACKED_INT64_ARRAY, TYPE_PACKED_FLOAT32_ARRAY, \
		TYPE_PACKED_FLOAT64_ARRAY, TYPE_PACKED_STRING_ARRAY, TYPE_PACKED_VECTOR2_ARRAY, TYPE_PACKED_VECTOR3_ARRAY, \
		TYPE_PACKED_COLOR_ARRAY, TYPE_PACKED_VECTOR4_ARRAY:
			var out := []
			for i in mini(v.size(), MAX_ITEMS):
				out.append(to_json(v[i], depth + 1))
			return out
	return str(v)


## An int's enum name from a property's hint ("A,B,C" or "A:0,B:5"), or the int when it has none.
static func enum_name(value: int, hint_string: String):
	var next := 0
	for part in hint_string.split(","):
		var name := part
		var at := next
		if ":" in part:
			name = part.get_slice(":", 0)
			at = int(part.get_slice(":", 1))
		if at == value:
			return name.strip_edges()
		next = at + 1
	return value


static func enum_value(name: String, hint_string: String):
	var next := 0
	for part in hint_string.split(","):
		var n := part
		var at := next
		if ":" in part:
			n = part.get_slice(":", 0)
			at = int(part.get_slice(":", 1))
		if n.strip_edges().to_lower() == name.strip_edges().to_lower():
			return at
		next = at + 1
	return null


## A property's value as JSON, its enum name when it is an int with an enum hint.
static func prop_to_json(value, info: Dictionary):
	if typeof(value) == TYPE_INT and info.get("hint", 0) == PROPERTY_HINT_ENUM and info.get("hint_string", "") != "":
		return enum_name(value, info.hint_string)
	return to_json(value)


static func _field(j: Dictionary, k: String, default = 0.0):
	if j.has(k):
		return j[k]
	if j.has(k.to_upper()):
		return j[k.to_upper()]
	return default


static func _floats(j, keys: Array) -> Array:
	var out := []
	if typeof(j) == TYPE_ARRAY:
		for i in keys.size():
			out.append(float(j[i]) if i < j.size() and j[i] != null else 0.0)
	elif typeof(j) == TYPE_DICTIONARY:
		for k in keys:
			var x = _field(j, k)
			out.append(float(x) if x != null else 0.0)
	else:
		for k in keys:
			out.append(0.0)
	return out


static func to_vector2(j) -> Vector2:
	var f := _floats(j, ["x", "y"])
	return Vector2(f[0], f[1])


static func to_vector3(j) -> Vector3:
	var f := _floats(j, ["x", "y", "z"])
	return Vector3(f[0], f[1], f[2])


static func to_vector4(j) -> Vector4:
	var f := _floats(j, ["x", "y", "z", "w"])
	return Vector4(f[0], f[1], f[2], f[3])


static func to_color(j) -> Color:
	if typeof(j) == TYPE_STRING:
		return Color.from_string(j, Color.WHITE)
	if typeof(j) == TYPE_DICTIONARY:
		return Color(float(_field(j, "r")), float(_field(j, "g")), float(_field(j, "b")), float(_field(j, "a", 1.0)))
	if typeof(j) == TYPE_ARRAY:
		var f := _floats(j, ["r", "g", "b", "a"])
		return Color(f[0], f[1], f[2], f[3] if j.size() > 3 else 1.0)
	return Color.WHITE


## What a value of the JSON shape stands for, with no declared type: {"x","y","z"} a Vector3, and so on.
static func infer(j):
	if typeof(j) == TYPE_DICTIONARY:
		var keys: Array = []
		for k in j:
			keys.append(str(k).to_lower())
		keys.sort()
		if keys == ["x", "y"]:
			return to_vector2(j)
		if keys == ["x", "y", "z"]:
			return to_vector3(j)
		if keys == ["w", "x", "y", "z"]:
			return to_vector4(j)
		if keys == ["a", "b", "g", "r"] or keys == ["b", "g", "r"]:
			return to_color(j)
	return j


## A JSON value as the given Variant type. `current`: the target's value now (its type is used when `type` is NIL;
## a typed array keeps its element type). `resolve`: path -> Object, for object references.
static func from_json(j, type: int, current = null, info := {}, resolve := Callable()):
	if type == TYPE_NIL:
		if current == null:
			return infer(j)
		type = typeof(current)
		if type == TYPE_NIL:
			return infer(j)
	if j == null:
		match type:
			TYPE_OBJECT, TYPE_NIL:
				return null
	match type:
		TYPE_BOOL:
			if typeof(j) == TYPE_STRING:
				return j.to_lower() in ["true", "1", "yes", "on"]
			return bool(j)
		TYPE_INT:
			if typeof(j) == TYPE_STRING:
				if info.get("hint", 0) == PROPERTY_HINT_ENUM:
					var e = enum_value(j, info.get("hint_string", ""))
					if e != null:
						return e
				return int(j)
			if typeof(j) == TYPE_BOOL:
				return 1 if j else 0
			return int(round(float(j)))
		TYPE_FLOAT:
			return float(j)
		TYPE_STRING:
			return j if typeof(j) == TYPE_STRING else str(j)
		TYPE_STRING_NAME:
			return StringName(str(j))
		TYPE_NODE_PATH:
			return NodePath(str(j))
		TYPE_VECTOR2:
			return to_vector2(j)
		TYPE_VECTOR2I:
			return Vector2i(to_vector2(j).round())
		TYPE_VECTOR3:
			return to_vector3(j)
		TYPE_VECTOR3I:
			return Vector3i(to_vector3(j).round())
		TYPE_VECTOR4:
			return to_vector4(j)
		TYPE_VECTOR4I:
			return Vector4i(to_vector4(j).round())
		TYPE_QUATERNION:
			var q := to_vector4(j)
			return Quaternion(q.x, q.y, q.z, q.w)
		TYPE_COLOR:
			return to_color(j)
		TYPE_RECT2:
			return Rect2(to_vector2(_field(j, "position", {})), to_vector2(_field(j, "size", {})))
		TYPE_RECT2I:
			return Rect2i(Vector2i(to_vector2(_field(j, "position", {}))), Vector2i(to_vector2(_field(j, "size", {}))))
		TYPE_AABB:
			return AABB(to_vector3(_field(j, "position", {})), to_vector3(_field(j, "size", {})))
		TYPE_PLANE:
			return Plane(to_vector3(_field(j, "normal", {})), float(_field(j, "d", 0.0)))
		TYPE_BASIS:
			return Basis(to_vector3(_field(j, "x", {})), to_vector3(_field(j, "y", {})), to_vector3(_field(j, "z", {})))
		TYPE_TRANSFORM3D:
			var b = _field(j, "basis", null)
			var basis: Basis = Basis.IDENTITY if b == null else from_json(b, TYPE_BASIS)
			return Transform3D(basis, to_vector3(_field(j, "origin", {})))
		TYPE_TRANSFORM2D:
			return Transform2D(to_vector2(_field(j, "x", {"x": 1, "y": 0})), to_vector2(_field(j, "y", {"x": 0, "y": 1})), to_vector2(_field(j, "origin", {})))
		TYPE_OBJECT:
			if typeof(j) == TYPE_STRING:
				if j == "":
					return null
				if resolve.is_valid():
					return resolve.call(j)
			return null
		TYPE_DICTIONARY:
			if typeof(j) != TYPE_DICTIONARY:
				return {}
			var out := {}
			for k in j:
				out[k] = infer(j[k])
			return out
		TYPE_ARRAY:
			var list: Array = j if typeof(j) == TYPE_ARRAY else [j]
			if typeof(current) == TYPE_ARRAY and current.is_typed():
				var typed: Array = current.duplicate()
				typed.clear()
				for x in list:
					typed.append(from_json(x, current.get_typed_builtin(), null, {}, resolve))
				return typed
			var out := []
			for x in list:
				out.append(infer(x))
			return out
		TYPE_PACKED_STRING_ARRAY:
			return PackedStringArray(j if typeof(j) == TYPE_ARRAY else [j])
		TYPE_PACKED_INT32_ARRAY:
			return PackedInt32Array(j if typeof(j) == TYPE_ARRAY else [j])
		TYPE_PACKED_INT64_ARRAY:
			return PackedInt64Array(j if typeof(j) == TYPE_ARRAY else [j])
		TYPE_PACKED_FLOAT32_ARRAY:
			return PackedFloat32Array(j if typeof(j) == TYPE_ARRAY else [j])
		TYPE_PACKED_FLOAT64_ARRAY:
			return PackedFloat64Array(j if typeof(j) == TYPE_ARRAY else [j])
		TYPE_PACKED_VECTOR2_ARRAY:
			var out := PackedVector2Array()
			for x in (j if typeof(j) == TYPE_ARRAY else []):
				out.append(to_vector2(x))
			return out
		TYPE_PACKED_VECTOR3_ARRAY:
			var out := PackedVector3Array()
			for x in (j if typeof(j) == TYPE_ARRAY else []):
				out.append(to_vector3(x))
			return out
		TYPE_PACKED_COLOR_ARRAY:
			var out := PackedColorArray()
			for x in (j if typeof(j) == TYPE_ARRAY else []):
				out.append(to_color(x))
			return out
	return j


static func type_label(info: Dictionary) -> String:
	var t: int = info.get("type", TYPE_NIL)
	if t == TYPE_OBJECT:
		var cn: String = str(info.get("class_name", ""))
		return cn if cn != "" else "Object"
	if t == TYPE_INT and info.get("hint", 0) == PROPERTY_HINT_ENUM:
		var cn: String = str(info.get("class_name", ""))
		return "enum " + (cn if cn != "" else "{" + str(info.get("hint_string", "")) + "}")
	if t == TYPE_NIL:
		return "Variant"
	if t == TYPE_ARRAY and info.get("hint", 0) == PROPERTY_HINT_ARRAY_TYPE:
		return "Array[%s]" % info.get("hint_string", "")
	return type_string(t)
