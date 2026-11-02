# Modified by Crane Valley: the player is cached across frames and script lookups by script, a named body is preferred over
# the first one, a freed player isn't replaced by another body, and bounds reuse one stack. A fall-acceleration property
# counts as gravity, as C# demos name it.
## The played world in the kit frame (frame.gd): the player, things' transforms and bounds, what the player sees.
## Ops: find, transforms, pose, movement, view (and the player detection the others use).
extends RefCounted

const Frame = preload("frame.gd")
const Values = preload("values.gd")

var host: Node
var found_by := ""
var _player_cache: Node = null
var _player_frame := -1
var _player_camera: Node = null
var _player_scene: Node = null
var _player_had := false
var _seek_at := 0.0
static var _walk_stack: Array[Node] = []
static var _numbers := {}  # script -> {pattern: [name, constant value or null]}
static var _patterns := {}  # pattern -> RegEx


func _init(owner_node: Node) -> void:
	host = owner_node


func tree() -> SceneTree:
	return host.get_tree()


func scene() -> Node:
	var t := tree()
	return t.current_scene if t else null


func camera3d() -> Camera3D:
	var vp := host.get_viewport()
	return vp.get_camera_3d() if vp else null


func camera2d() -> Camera2D:
	var vp := host.get_viewport()
	return vp.get_camera_2d() if vp else null


func is_ours(n: Node) -> bool:
	return n == host or host.is_ancestor_of(n)


## The player: the body the current camera hangs from (a CharacterBody3D/2D or other physics body), else the first
## node in the group "player"/"Player", else the scene's first CharacterBody3D (or 2D) with "player" in its name, else its
## first one. null in a menu, and once the player has been freed, rather than handing its role to an enemy.
## The detectors ask every frame, so it is looked for only when the camera or the scene changed or the player went.
func player() -> Node:
	var f := Engine.get_process_frames()
	if f == _player_frame:
		return _player_cache if is_instance_valid(_player_cache) else null
	_player_frame = f
	var s := scene()
	var cam: Node = camera3d()
	if cam == null:
		cam = camera2d()
	if s != _player_scene:
		_player_scene = s
		_player_had = false
		_player_cache = null
		_seek_at = 0.0
	elif is_instance_valid(_player_cache) and _player_cache.is_inside_tree():
		if cam == _player_camera:
			return _player_cache
		_seek_at = 0.0
	else:
		_player_cache = null
	var now := Time.get_ticks_msec() / 1000.0
	if now < _seek_at:
		return null
	_seek_at = now + 0.25
	_player_camera = cam
	_player_cache = _find_player()
	_player_had = _player_had or _player_cache != null
	return _player_cache


func _find_player() -> Node:
	var cam3 := camera3d()
	if cam3:
		var n: Node = cam3
		while n:
			if n is PhysicsBody3D:
				found_by = "camera"
				return n
			n = n.get_parent()
	var cam2 := camera2d()
	if cam2:
		var n: Node = cam2
		while n:
			if n is PhysicsBody2D:
				found_by = "camera"
				return n
			n = n.get_parent()
	for g in ["player", "Player", "players"]:
		for n in tree().get_nodes_in_group(g):
			if n is Node3D or n is Node2D:
				found_by = "group"
				return n
	var s := scene()
	if s == null:
		return null
	for type in ["CharacterBody3D", "CharacterBody2D"]:
		var found := s.find_children("*", type, true, false)
		for n in found:
			if "player" in str(n.name).to_lower():
				found_by = "name"
				return n
		if found.size() > 0 and not _player_had:
			found_by = "first body"
			return found[0]
	return null


func dimension() -> String:
	var p := player()
	if p is Node3D or camera3d() != null:
		return "3d"
	if p is Node2D or camera2d() != null:
		return "2d"
	var s := scene()
	if s is Node3D:
		return "3d"
	if s is Node2D:
		return "2d"
	return "ui"


# --- find -------------------------------------------------------------------------------------------

## Whether a node is of a class: a Godot class (subclasses included) or a script class_name (inherited ones too).
static func is_of(n: Node, cls: String) -> bool:
	if n.is_class(cls):
		return true
	var s = n.get_script()
	while s is Script:
		if str(s.get_global_name()) == cls or s.resource_path.get_file().get_basename() == cls:
			return true
		s = s.get_base_script()
	return false


func find(args: Dictionary) -> Dictionary:
	var cls := str(args.get("class", ""))
	var name := str(args.get("name", "")).to_lower()
	var limit := int(args.get("limit", 1000))
	var out := []
	var candidates: Array
	if cls.begins_with("group:"):
		candidates = tree().get_nodes_in_group(cls.substr(6))
		cls = ""
	elif args.get("things", false):
		# the scene's things, as Unreal's actors: its direct children, physics bodies and areas, and the roots of the
		# scenes instanced in it (not their meshes, shapes and other parts)
		candidates = []
		var s := scene()
		if s:
			candidates.append(s)
			for n in s.find_children("*", "", true, false):
				if n.get_parent() == s or n is CollisionObject3D or n is CollisionObject2D or n.scene_file_path != "":
					candidates.append(n)
	else:
		candidates = []
		_walk(tree().root, candidates)
	for n in candidates:
		if out.size() >= limit:
			break
		if not is_instance_valid(n) or is_ours(n):
			continue
		if cls != "" and not is_of(n, cls):
			continue
		if name != "" and not (name in str(n.name).to_lower()):
			continue
		out.append(str(n.get_path()))
	return {"objects": out}


func _walk(n: Node, out: Array) -> void:
	if n == host:
		return
	out.append(n)
	for i in n.get_child_count():
		_walk(n.get_child(i), out)


# --- bounds and transforms --------------------------------------------------------------------------

## The world box of a 3D node and what hangs under it: its drawn parts' AABBs, else its collision shapes'. Empty
## (size 0, at its origin) when it has neither.
static func bounds3d(n: Node3D, skip: Node = null) -> AABB:
	var box := AABB()
	var have := false
	var stack := _walk_stack
	stack.clear()
	stack.append(n)
	while stack.size() > 0:
		var c: Node = stack.pop_back()
		if c != n and skip != null and c == skip:
			continue
		if c is VisualInstance3D and c.is_visible_in_tree() and not (c is Light3D) and not (c is Camera3D):
			var b: AABB = c.global_transform * c.get_aabb()
			if b.size.length() > 0.0:
				box = b if not have else box.merge(b)
				have = true
		for i in c.get_child_count():
			stack.append(c.get_child(i))
	if have:
		return box
	stack.append(n)
	while stack.size() > 0:
		var c: Node = stack.pop_back()
		if c is CollisionShape3D and c.shape != null and not c.disabled:
			var m: Mesh = c.shape.get_debug_mesh()
			if m:
				var b: AABB = c.global_transform * m.get_aabb()
				box = b if not have else box.merge(b)
				have = true
		for i in c.get_child_count():
			stack.append(c.get_child(i))
	if have:
		return box
	return AABB(n.global_position, Vector3.ZERO)


static func bounds2d(n: Node) -> Rect2:
	var rect := Rect2()
	var have := false
	var stack: Array = [n]
	while stack.size() > 0:
		var c: Node = stack.pop_back()
		var r := Rect2()
		var ok := false
		if c is Control and c.is_visible_in_tree():
			r = c.get_global_rect()
			ok = true
		elif c is Sprite2D and c.is_visible_in_tree():
			r = c.global_transform * c.get_rect()
			ok = true
		elif c is AnimatedSprite2D and c.is_visible_in_tree() and c.sprite_frames:
			var tex: Texture2D = c.sprite_frames.get_frame_texture(c.animation, c.frame)
			if tex:
				var sz := tex.get_size()
				var local := Rect2(-sz / 2 if c.centered else Vector2.ZERO, sz)
				r = c.global_transform * local
				ok = true
		elif c is CollisionShape2D and c.shape != null and not c.disabled:
			r = c.global_transform * c.shape.get_rect()
			ok = true
		if ok:
			rect = r if not have else rect.merge(r)
			have = true
		stack.append_array(c.get_children())
	if have:
		return rect
	if n is Node2D:
		return Rect2(n.global_position, Vector2.ZERO)
	return Rect2()


static func velocity_of(n: Node) -> Vector3:
	if n is CharacterBody3D:
		return n.velocity
	if n is RigidBody3D:
		return n.linear_velocity
	return Vector3.ZERO


static func velocity2d_of(n: Node) -> Vector2:
	if n is CharacterBody2D:
		return n.velocity
	if n is RigidBody2D:
		return n.linear_velocity
	return Vector2.ZERO


static func transform_of(n: Node):
	if n is Node3D:
		var b := bounds3d(n)
		return {"location": Frame.kit(n.global_position), "rotation": Frame.kit_rot(n.global_basis),
			"velocity": Frame.kit(velocity_of(n)), "center": Frame.kit(b.get_center()), "extent": Frame.kit_extent(b.size)}
	if n is Node2D or n is Control:
		var r := bounds2d(n)
		var at: Vector2 = n.global_position
		var rot: float = n.global_rotation if n is Node2D else n.rotation
		return {"location": Frame.kit2d(at), "rotation": Frame.kit_rot2d(rot), "velocity": Frame.kit2d(velocity2d_of(n)),
			"center": Frame.kit2d(r.get_center()), "extent": {"X": Frame.r2(r.size.x / 2), "Y": 0.0, "Z": Frame.r2(r.size.y / 2)}}
	return null


func transforms(args: Dictionary, resolve: Callable) -> Dictionary:
	var out := {}
	for p in args.get("objects", []):
		var n = resolve.call(str(p))
		out[str(p)] = transform_of(n) if n is Node else null
	return {"objects": out}


# --- the player's body ------------------------------------------------------------------------------

## The player's collision shape node (the first CollisionShape3D/2D under it), or null.
static func body_shape(p: Node) -> Node:
	for c in p.get_children():
		if (c is CollisionShape3D or c is CollisionShape2D) and c.shape != null and not c.disabled:
			return c
	return null


## [half height, radius] of a shape in its own units (metres or pixels), or [null, null].
static func shape_size(shape) -> Array:
	if shape is CapsuleShape3D or shape is CapsuleShape2D:
		return [shape.height / 2.0, shape.radius]
	if shape is CylinderShape3D:
		return [shape.height / 2.0, shape.radius]
	if shape is SphereShape3D or shape is CircleShape2D:
		return [shape.radius, shape.radius]
	if shape is BoxShape3D:
		return [shape.size.y / 2.0, maxf(shape.size.x, shape.size.z) / 2.0]
	if shape is RectangleShape2D:
		return [shape.size.y / 2.0, shape.size.x / 2.0]
	return [null, null]


## The middle of the player's body (its collision shape's center) and its half height (Godot units).
func body_center(p: Node) -> Array:
	var cs := body_shape(p)
	if cs and p is Node3D:
		var hh = shape_size(cs.shape)[0]
		return [cs.global_position, hh if hh != null else 0.0]
	if cs and p is Node2D:
		var hh = shape_size(cs.shape)[0]
		return [cs.global_position, hh if hh != null else 0.0]
	if p is Node3D:
		return [p.global_position, 0.0]
	return [p.global_position, 0.0]


func pose(_args := {}) -> Array:
	var p := player()
	if p == null:
		return [false, "No player"]
	if p is Node3D:
		var bc := body_center(p)
		var center: Vector3 = bc[0]
		var hh: float = bc[1]
		var cam := camera3d()
		var basis: Basis = cam.global_basis if cam else p.global_basis
		var eye: Vector3 = cam.global_position if cam else center
		return [true, {"path": str(p.get_path()), "at": Frame.kit(center), "feet": Frame.kit(center - Vector3(0, hh, 0)),
			"eye": Frame.kit(eye), "yaw": Frame.r2(Frame.yaw_of(basis)), "pitch": Frame.r2(Frame.pitch_of(basis)),
			"velocity": Frame.kit(velocity_of(p)), "on_ground": p.is_on_floor() if p is CharacterBody3D else null}]
	var bc2 := body_center(p)
	var c2: Vector2 = bc2[0]
	var cam2 := camera2d()
	var eye2: Vector2 = cam2.get_screen_center_position() if cam2 else c2
	return [true, {"path": str(p.get_path()), "at": Frame.kit2d(c2), "feet": Frame.kit2d(c2 + Vector2(0, bc2[1])),
		"eye": Frame.kit2d(eye2), "yaw": 0.0, "pitch": 0.0, "velocity": Frame.kit2d(velocity2d_of(p)),
		"on_ground": p.is_on_floor() if p is CharacterBody2D else null}]


const SPEED_RE := "^(walk_?|move_?|movement_?|max_?|run_?|player_?|base_?)?speed$"
const JUMP_RE := "^(jump_?)(velocity|speed|force|impulse|strength|power)$"
const JUMP_HEIGHT_RE := "^(jump_?)(height)$"
const GRAVITY_RE := "^(gravity|fall_?(acceleration|gravity))$"


## A number the player's script holds under a name matching the pattern (its variables, then its constants):
## [value, name] or [null, ""].
static func script_number(p: Node, pattern: String) -> Array:
	var script = p.get_script()
	if not (script is Script):
		return [null, ""]
	var known: Dictionary = _numbers.get_or_add(script, {})
	if not known.has(pattern):
		known[pattern] = _find_number(p, script, pattern)
	var found: Array = known[pattern]
	if found[1] != null:
		return [float(found[1]), found[0]]
	if found[0] == "":
		return [null, ""]
	var v = p.get(found[0])
	return [float(v), found[0]] if typeof(v) in [TYPE_INT, TYPE_FLOAT] else [null, ""]


static func _find_number(p: Node, script: Script, pattern: String) -> Array:
	if not _patterns.has(pattern):
		_patterns[pattern] = RegEx.create_from_string(pattern)
	var re: RegEx = _patterns[pattern]
	for prop in script.get_script_property_list():
		if re.search(str(prop.name).to_lower()) and typeof(p.get(prop.name)) in [TYPE_INT, TYPE_FLOAT]:
			return [str(prop.name), null]
	var consts: Dictionary = script.get_script_constant_map()
	for k in consts:
		if re.search(str(k).to_lower()) and typeof(consts[k]) in [TYPE_INT, TYPE_FLOAT]:
			return [str(k), consts[k]]
	return ["", null]


func movement(_args := {}) -> Array:
	var p := player()
	if p == null:
		return [false, "No player"]
	var is3d := p is Node3D
	var scale := Frame.SCALE if is3d else 1.0
	var out := {"walk_speed": null, "jump_z": null, "gravity": null, "half_height": null, "radius": null, "step": null,
		"max_slope": null, "sources": {}}
	var speed := script_number(p, SPEED_RE)
	if speed[0] != null:
		out.walk_speed = Frame.r2(absf(speed[0]) * scale)
		out.sources.walk_speed = "script " + speed[1]
	var gravity := script_number(p, GRAVITY_RE)
	var g: float
	if gravity[0] != null:
		g = absf(gravity[0])
		out.sources.gravity = "script " + gravity[1]
	else:
		g = float(ProjectSettings.get_setting("physics/3d/default_gravity" if is3d else "physics/2d/default_gravity", 9.8 if is3d else 980.0))
		out.sources.gravity = "project setting"
	out.gravity = Frame.r2(g * scale)
	var jump := script_number(p, JUMP_RE)
	if jump[0] != null:
		out.jump_z = Frame.r2(absf(jump[0]) * scale)
		out.sources.jump_z = "script " + jump[1]
	else:
		var jh := script_number(p, JUMP_HEIGHT_RE)
		if jh[0] != null:
			out.jump_z = Frame.r2(sqrt(2.0 * g * absf(jh[0])) * scale)
			out.sources.jump_z = "script %s (as sqrt(2 g h))" % jh[1]
	var cs := body_shape(p)
	if cs:
		var sz := shape_size(cs.shape)
		if sz[0] != null:
			out.half_height = Frame.r2(sz[0] * scale)
			out.radius = Frame.r2(sz[1] * scale)
			out.sources.half_height = cs.shape.get_class()
	if p is CharacterBody3D or p is CharacterBody2D:
		out.max_slope = Frame.r2(rad_to_deg(p.floor_max_angle))
		out.sources.step = "CharacterBody has no step-up (step is null); floor_snap_length=%s" % str(p.floor_snap_length)
	return [true, out]
