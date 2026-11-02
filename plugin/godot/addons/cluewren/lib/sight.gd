# Modified by Crane Valley: the ray query, the player's excluded bodies and mesh names are reused between calls, and the
# scene walks reuse one stack, `view` being on the harness's tick path. A mesh in a model instanced under a body belongs
# to the body, as the note on thing_of always said.
## What the player sees and feels (`view`), and things watched over time (`watch`), in the kit frame.
##
## view (3D): things with a drawn body (VisualInstance3D) in the camera's frustum that a line of sight from the camera
## reaches (the box's center, top or nearest point), nearest first, grouped into things (a node's nearest physics body
## or area, else the root of the scene instance it belongs to, else the mesh itself); the player and the large floor it
## stands on left out. Plus the camera, the body (on_ground, falling, standing_on, touching, floor slope) and, with
## `sonar`, how far the body could walk each way (test_move sweeps of its own shape, rays down for drops). Not told
## (Unreal's richer view): held, scene.sun, openings, ways_up, lane, slope ends, body.under/overhead, highlighted,
## limp, effects (effects_play is false headless).
## view (2D): drawn CanvasItems in the camera's rect, by thing, nearest the player first; 1 px = 1 unit (frame.gd).
extends RefCounted

const Frame = preload("frame.gd")
const World = preload("world.gd")

var host: Node
var world
var _ray := PhysicsRayQueryParameters3D.new()
var _stack: Array[Node] = []
var _rids_of: Node = null
var _rids := []
var _mesh_names := {}  # Mesh -> the name a look gives it


func _init(owner_node: Node, world_module) -> void:
	host = owner_node
	world = world_module


const OMITTED := ["held", "scene.sun", "openings", "ways_up", "lane", "top_slope_degrees", "low_end", "high_end",
	"uphill", "body.under", "body.overhead", "look.highlighted", "look.limp", "look.effects", "blocks_player"]


## The thing a node is part of: its nearest physics body or area (itself included), else the nearest root of a scene
## instance, else itself. Never above the current scene's root.
func thing_of(n: Node) -> Node:
	var s: Node = world.scene()
	var instance: Node = null
	var x := n
	while x and x != s and x != host.get_tree().root:
		if x is CollisionObject3D or x is CollisionObject2D:
			return x
		if instance == null and x.scene_file_path != "" and x != n:
			instance = x
		x = x.get_parent()
	return instance if instance else n


static func class_of(n: Node) -> String:
	var s = n.get_script()
	if s is Script:
		var g := str(s.get_global_name())
		if g != "":
			return g
	return n.get_class()


func view(args: Dictionary) -> Array:
	match world.dimension():
		"3d":
			return view3d(args)
		"2d":
			return view2d(args)
	return [true, {"visible": [], "camera": null, "body": null, "effects_play": false, "omitted": OMITTED}]


func _player_rids(p: Node) -> Array:
	if is_instance_valid(_rids_of) and p == _rids_of:
		return _rids
	_rids_of = p
	_rids = []
	if p == null:
		return _rids
	_stack.clear()
	_stack.append(p)
	while _stack.size() > 0:
		var c: Node = _stack.pop_back()
		if c is CollisionObject3D:
			_rids.append(c.get_rid())
		for i in c.get_child_count():
			_stack.append(c.get_child(i))
	return _rids


func _intersect(space: PhysicsDirectSpaceState3D, from: Vector3, to: Vector3, exclude: Array, areas := false) -> Dictionary:
	_ray.from = from
	_ray.to = to
	_ray.collision_mask = 0xFFFFFFFF
	_ray.exclude = exclude
	_ray.collide_with_areas = areas
	return space.intersect_ray(_ray)


func _belongs(collider: Object, thing: Node) -> bool:
	return collider == thing or (collider is Node and thing.is_ancestor_of(collider))


func view3d(args: Dictionary) -> Array:
	var cam: Camera3D = world.camera3d()
	if cam == null:
		return [false, "No camera"]
	var p: Node = world.player()
	var max_m := float(args.get("max_distance", 5000.0)) / Frame.SCALE
	var max_count := int(args.get("max_count", 24))
	var only = args.get("only", null)
	var only_set := {}
	if typeof(only) == TYPE_ARRAY:
		for x in only:
			only_set[str(x)] = true
	var space := cam.get_world_3d().direct_space_state
	var exclude := _player_rids(p)
	var cam_pos := cam.global_position
	var vp_size := cam.get_viewport().get_visible_rect().size
	var feet_y := cam_pos.y
	var standing_on: Node = null
	var touching := []
	var body := {}
	var view_yaw := Frame.yaw_of(cam.global_basis)
	if p is Node3D:
		var bc: Array = world.body_center(p)
		feet_y = bc[0].y - bc[1]
		var vel := World.velocity_of(p)
		body = {"velocity": Frame.kit(vel), "feet_z": Frame.r2(feet_y * Frame.SCALE), "crouched": false}
		if p is CharacterBody3D:
			body.on_ground = p.is_on_floor()
			body.falling = not p.is_on_floor()
			if p.is_on_floor():
				body.floor_slope_degrees = Frame.r2(rad_to_deg(p.get_floor_angle()))
			var seen := {}
			for i in p.get_slide_collision_count():
				var c: KinematicCollision3D = p.get_slide_collision(i)
				var col = c.get_collider()
				if not (col is Node):
					continue
				var t := thing_of(col)
				var nrm := c.get_normal()
				if nrm.angle_to(Vector3.UP) <= p.floor_max_angle + 0.01:
					standing_on = t
					continue
				if seen.has(t):
					continue
				seen[t] = true
				var way := Frame.yaw_of_dir(-nrm)
				touching.append({"thing": str(t.name), "path": str(t.get_path()), "yaw": Frame.r2(way),
					"turn": Frame.r2(Frame.wrap180(way - view_yaw)), "normal": Frame.kit_dir(nrm)})
			if standing_on:
				body.standing_on = str(standing_on.name)
			if touching.size() > 0:
				body.touching = touching
		else:
			body.on_ground = null
			body.falling = null
	# Things: drawn parts grouped by thing
	var things := {}
	var s: Node = world.scene()
	if s == null:
		return [false, "No scene"]
	_stack.clear()
	_stack.append(s)
	while _stack.size() > 0:
		var n: Node = _stack.pop_back()
		if n == p or world.is_ours(n):
			continue
		for i in n.get_child_count():
			_stack.append(n.get_child(i))
		if not (n is GeometryInstance3D) or not n.is_visible_in_tree():
			continue
		var b: AABB = n.global_transform * n.get_aabb()
		if b.size.length() <= 0.0:
			continue
		var t := thing_of(n)
		if p != null and (t == p or p.is_ancestor_of(t)):
			continue
		if not only_set.is_empty() and not (only_set.has(str(t.name)) or only_set.has(str(t.get_path()))):
			continue
		if things.has(t):
			things[t].box = things[t].box.merge(b)
			things[t].parts.append(n)
		else:
			things[t] = {"box": b, "parts": [n]}
	# What the crosshair is on
	var crosshair: Node = null
	var fwd := -cam.global_basis.z
	var hit := _intersect(space, cam_pos, cam_pos + fwd * max_m, exclude, true)
	if hit and hit.collider is Node:
		crosshair = thing_of(hit.collider)
	var out := []
	for t in things:
		var box: AABB = things[t].box
		if t == standing_on and box.size.x > 10.0 and box.size.z > 10.0:
			continue  # the ground
		var center := box.get_center()
		var nearest := Vector3(clampf(cam_pos.x, box.position.x, box.end.x), clampf(cam_pos.y, box.position.y, box.end.y), clampf(cam_pos.z, box.position.z, box.end.z))
		var dist := cam_pos.distance_to(nearest)
		if dist > max_m:
			continue
		var center_in := cam.is_position_in_frustum(center)
		var any_in := center_in
		if not any_in:
			for i in 8:
				if cam.is_position_in_frustum(box.get_endpoint(i)):
					any_in = true
					break
		if not any_in:
			continue
		var seen := false
		for target in [center, Vector3(center.x, box.end.y - 0.01, center.z), nearest]:
			var r := _intersect(space, cam_pos, target, exclude)
			if r.is_empty() or _belongs(r.collider, t) or (r.position.distance_to(target) < 0.05):
				seen = true
				break
		if not seen:
			continue
		var item := {"name": str(t.name), "path": str(t.get_path()), "class": class_of(t), "center": Frame.kit(center),
			"size": {"X": Frame.r2(box.size.z * Frame.SCALE), "Y": Frame.r2(box.size.x * Frame.SCALE), "Z": Frame.r2(box.size.y * Frame.SCALE)},
			"yaw": Frame.r2(Frame.yaw_of(t.global_basis)) if t is Node3D else 0.0, "distance": Frame.r2(dist * Frame.SCALE),
			"top_above_feet": Frame.r2((box.end.y - feet_y) * Frame.SCALE), "bottom_above_feet": Frame.r2((box.position.y - feet_y) * Frame.SCALE),
			"crosshair_on": t == crosshair}
		if not cam.is_position_behind(center):
			var sp := cam.unproject_position(center)
			item.screen_x = Frame.r2(sp.x / vp_size.x * 2.0 - 1.0)
			item.screen_y = Frame.r2(1.0 - sp.y / vp_size.y * 2.0)
		if not center_in:
			item.partly_in_view = true
		var v := World.velocity_of(t)
		if v.length() > 0.01:
			item.speed = Frame.r2(v.length() * Frame.SCALE)
		var look := look_of(t)
		if not look.is_empty():
			item.look = look
			if look.has("texts"):
				item.label = look.texts[0]
		out.append(item)
	out.sort_custom(func(a, b): return a.distance < b.distance)
	if out.size() > max_count:
		out.resize(max_count)
	var hfov := cam.fov
	if cam.keep_aspect == Camera3D.KEEP_HEIGHT and vp_size.y > 0:
		hfov = rad_to_deg(2.0 * atan(tan(deg_to_rad(cam.fov) / 2.0) * vp_size.x / vp_size.y))
	var result := {"visible": out, "camera": {"location": Frame.kit(cam_pos), "yaw": Frame.r2(view_yaw),
		"pitch": Frame.r2(Frame.pitch_of(cam.global_basis)), "fov": Frame.r2(hfov), "vfov": Frame.r2(cam.fov)},
		"crosshair_tested": true, "oriented": true, "effects_play": DisplayServer.get_name() != "headless",
		"body": body, "omitted": OMITTED}
	if typeof(only) == TYPE_ARRAY:
		result.only = only
	if args.get("sonar", false) and p is CharacterBody3D:
		body.sonar = sonar(p, args, view_yaw, space, exclude)
	return [true, result]


## How a thing is drawn: its visible meshes' names, its lights on, the animations playing, its 3D texts.
func look_of(t: Node) -> Dictionary:
	var parts := {}
	var lights := 0
	var brightest := 0.0
	var anims := []
	var texts := []
	_stack.clear()
	_stack.append(t)
	while _stack.size() > 0:
		var n: Node = _stack.pop_back()
		for i in n.get_child_count():
			_stack.append(n.get_child(i))
		if n is MeshInstance3D and n.is_visible_in_tree() and n.mesh:
			var m: Mesh = n.mesh
			if not _mesh_names.has(m):
				_mesh_names[m] = m.resource_path.get_file().get_basename() if m.resource_path != "" and not ("::" in m.resource_path) else (m.resource_name if m.resource_name != "" else m.get_class())
			parts[_mesh_names[m]] = true
		elif n is Light3D and n.is_visible_in_tree() and n.light_energy > 0.0:
			lights += 1
			brightest = maxf(brightest, n.light_energy)
		elif n is AnimationPlayer and n.is_playing():
			anims.append(str(n.current_animation))
		elif n is Label3D and n.is_visible_in_tree() and n.text != "" and texts.size() < 4:
			texts.append(n.text.substr(0, 40))
	var look := {}
	if parts.size() > 0:
		var names := parts.keys()
		names.sort()
		look.parts = names.slice(0, 8)
	if lights > 0:
		look.lights = {"on": lights, "brightness": Frame.r2(brightest)}
	if anims.size() > 0:
		look.animation = anims[0]
	if texts.size() > 0:
		look.texts = texts
	return look


func sonar(p: CharacterBody3D, args: Dictionary, view_yaw: float, space: PhysicsDirectSpaceState3D, exclude: Array) -> Dictionary:
	var reach := clampf(float(args.get("sonar_reach", 1500.0)), 50.0, 5000.0) / Frame.SCALE
	var turns: Array = args.get("sonar_turns", [0, -30, 30, -60, 60, -90, 90, -135, 135, 180])
	var bc: Array = world.body_center(p)
	var hh: float = bc[1]
	var shape_offset: Vector3 = bc[0] - p.global_position
	var ways := []
	var step := 0.5
	for turn in turns.slice(0, 16):
		var yaw := view_yaw + float(turn)
		var dir := Frame.dir_of_yaw(yaw)
		var t := p.global_transform
		t.origin += Vector3(0, 0.1, 0)
		var travelled := 0.0
		var way := {"turn": float(turn), "by": "reach"}
		var rise := 0.0
		var start_y := t.origin.y
		var guard := 0
		while travelled < reach and guard < 64:
			guard += 1
			var col := KinematicCollision3D.new()
			var move := dir * minf(step, reach - travelled)
			if p.test_move(t, move, col, 0.001, false):
				var n := col.get_normal()
				if n.angle_to(Vector3.UP) <= p.floor_max_angle and guard < 63:
					t.origin += Vector3(0, 0.25, 0)  # walkable rise: go up it
					continue
				travelled += col.get_travel().length()
				way.by = "wall"
				var c = col.get_collider()
				if c is Node:
					way.thing = str(thing_of(c).name)
				way.normal = {"X": Frame.r2(-n.z), "Y": Frame.r2(n.x)}
				break
			t.origin += move
			travelled += move.length()
			var center := t.origin + shape_offset
			var r := _intersect(space, center, center - Vector3(0, hh + 30.0, 0), exclude)
			var below: float = (center.y - hh) - r.position.y if r else 31.0
			if below > 0.6:
				way.by = "drop"
				way.drop = Frame.r2(below * Frame.SCALE) if below < 30.0 else null
				# the edge, to within a 32nd of a step: where the floor under the body's middle ends
				var lo := 0.0
				var hi := move.length()
				var back := center - move
				for _i in 5:
					var mid := (lo + hi) / 2.0
					var at := back + dir * mid
					if _intersect(space, at, at - Vector3(0, hh + 0.6, 0), exclude):
						lo = mid
					else:
						hi = mid
				travelled -= move.length() - lo
				break
			t.origin.y = r.position.y + hh - shape_offset.y + 0.1
		way.clear = Frame.r2(travelled * Frame.SCALE)
		rise = t.origin.y - start_y
		if absf(rise) > 0.05:
			way.rise = Frame.r2(rise * Frame.SCALE)
		ways.append(way)
	return {"ways": ways, "reach": Frame.r2(reach * Frame.SCALE)}


func view2d(args: Dictionary) -> Array:
	var p: Node = world.player()
	var vp: Viewport = host.get_viewport()
	var canvas := vp.get_canvas_transform()
	var screen := vp.get_visible_rect()
	var world_rect := canvas.affine_inverse() * screen
	var max_count := int(args.get("max_count", 24))
	var max_d := float(args.get("max_distance", 5000.0))
	var only = args.get("only", null)
	var only_set := {}
	if typeof(only) == TYPE_ARRAY:
		for x in only:
			only_set[str(x)] = true
	var origin: Vector2 = world.body_center(p)[0] if p is Node2D else world_rect.get_center()
	var things := {}
	var s: Node = world.scene()
	_stack.clear()
	_stack.append(s)
	while _stack.size() > 0:
		var n: Node = _stack.pop_back()
		if n == p or world.is_ours(n) or n is CanvasLayer:
			continue
		for i in n.get_child_count():
			_stack.append(n.get_child(i))
		if not (n is Node2D) or not n.is_visible_in_tree():
			continue
		if not (n is Sprite2D or n is AnimatedSprite2D or n is Polygon2D or n is MeshInstance2D or n is Line2D):
			continue
		var t := thing_of(n)
		if p != null and (t == p or p.is_ancestor_of(t)):
			continue
		if not only_set.is_empty() and not (only_set.has(str(t.name)) or only_set.has(str(t.get_path()))):
			continue
		things[t] = true
	var out := []
	for t in things:
		var r: Rect2 = World.bounds2d(t)
		if not world_rect.intersects(r, true) and not world_rect.has_point(r.position):
			continue
		var c := r.get_center()
		var d := origin.distance_to(c)
		if d > max_d:
			continue
		var sp := canvas * c
		var item := {"name": str(t.name), "path": str(t.get_path()), "class": class_of(t), "center": Frame.kit2d(c),
			"size": {"X": Frame.r2(r.size.x), "Y": 0.0, "Z": Frame.r2(r.size.y)}, "distance": Frame.r2(d),
			"screen_x": Frame.r2(sp.x / screen.size.x * 2.0 - 1.0), "screen_y": Frame.r2(1.0 - sp.y / screen.size.y * 2.0)}
		if not world_rect.encloses(r):
			item.partly_in_view = true
		out.append(item)
	out.sort_custom(func(a, b): return a.distance < b.distance)
	if out.size() > max_count:
		out.resize(max_count)
	var body := {}
	if p is Node2D:
		body = {"velocity": Frame.kit2d(World.velocity2d_of(p)), "crouched": false}
		if p is CharacterBody2D:
			body.on_ground = p.is_on_floor()
			body.falling = not p.is_on_floor()
	var result := {"visible": out, "camera": {"location": Frame.kit2d(world_rect.get_center()), "yaw": 0.0, "pitch": 0.0, "fov": null},
		"effects_play": DisplayServer.get_name() != "headless", "body": body, "omitted": OMITTED + ["sonar"]}
	if typeof(only) == TYPE_ARRAY:
		result.only = only
	return [true, result]


# --- watch ------------------------------------------------------------------------------------------

## The state a watch starts from: per object its local transform (and with `drawn`, its parts' transforms in its own
## frame and how it is drawn). tick() samples; finish() gives the changes.
func watch_start(args: Dictionary, resolve: Callable) -> Dictionary:
	var drawn: bool = args.get("drawn", false)
	var parts_count := clampi(int(args.get("parts", 3)), 0, 16)
	var entries := []
	var looks := []
	for x in args.get("objects", []):
		var path := str(x)
		var n = resolve.call(path)
		if not (n is Node3D or n is Node2D or n is Control):
			continue
		entries.append({"node": n, "thing": null, "path": path, "first": null, "found": {}})
		if drawn and n is Node3D:
			var meshes := []
			for m in n.find_children("*", "GeometryInstance3D", true, false):
				if m.is_visible_in_tree():
					meshes.append(m)
			meshes.sort_custom(func(a, b): return a.get_aabb().size.length() > b.get_aabb().size.length())
			for m in meshes.slice(0, parts_count):
				entries.append({"node": m, "thing": n, "path": path + "/" + str(n.get_path_to(m)), "first": null, "found": {}})
			looks.append({"node": n, "path": path, "last": null, "found": []})
	return {"entries": entries, "looks": looks, "drawn": drawn, "turned": float(args.get("turned", 1.0)),
		"moved": float(args.get("moved", 2.0)), "resized": float(args.get("resized", 0.02))}


func _sample(e: Dictionary) -> Array:
	var n = e.node
	if not is_instance_valid(n):
		return []
	if n is Node3D:
		var tr: Transform3D = n.transform
		if e.thing != null:
			if not is_instance_valid(e.thing):
				return []
			tr = e.thing.global_transform.affine_inverse() * n.global_transform
		return [tr.basis.get_rotation_quaternion(), tr.origin * Frame.SCALE, tr.basis.get_scale()]
	if n is Node2D:
		return [Quaternion(Vector3.FORWARD, n.rotation), Vector3(n.position.x, n.position.y, 0), Vector3(n.scale.x, n.scale.y, 1)]
	if n is Control:
		return [Quaternion(Vector3.FORWARD, n.rotation), Vector3(n.position.x, n.position.y, 0), Vector3(n.scale.x, n.scale.y, 1)]
	return []


func watch_tick(st: Dictionary, t: float) -> void:
	for e in st.entries:
		var s: Array = _sample(e)
		if s.is_empty():
			continue
		if e.first == null:
			e.first = s
			continue
		var f: Array = e.first
		if not e.found.has("turned") and rad_to_deg(f[0].angle_to(s[0])) > st.turned:
			e.found["turned"] = t
		if not e.found.has("moved") and f[1].distance_to(s[1]) > st.moved:
			e.found["moved"] = t
		if not e.found.has("changed size"):
			var base: Vector3 = f[2].abs().max(Vector3(0.001, 0.001, 0.001))
			var rel: Vector3 = (s[2] - f[2]).abs() / base
			if maxf(rel.x, maxf(rel.y, rel.z)) > st.resized:
				e.found["changed size"] = t
	for l in st.looks:
		if not is_instance_valid(l.node):
			continue
		var now := look_of(l.node)
		var cur := {"parts": now.get("parts", []), "lights": now.get("lights", {}).get("on", 0), "anims": [now.get("animation")] if now.has("animation") else []}
		if l.last != null:
			for x in cur.parts:
				if not l.last.parts.has(x):
					l.found.append({"t": t, "what": "part shown", "asset": x})
			for x in l.last.parts:
				if not cur.parts.has(x):
					l.found.append({"t": t, "what": "part hidden", "asset": x})
			if cur.lights != l.last.lights:
				l.found.append({"t": t, "what": "light on" if cur.lights > l.last.lights else "light off", "asset": ""})
			for x in cur.anims:
				if not l.last.anims.has(x):
					l.found.append({"t": t, "what": "animation started", "asset": x})
			for x in l.last.anims:
				if not cur.anims.has(x):
					l.found.append({"t": t, "what": "animation ended", "asset": x})
		l.last = cur


func watch_finish(st: Dictionary) -> Dictionary:
	var changes := []
	var seen := {}
	for e in st.entries:
		for what in e.found:
			var key: String = e.path + "|" + what
			if seen.has(key):
				continue
			seen[key] = true
			changes.append({"t": Frame.r2(e.found[what]), "object": e.path, "what": what})
	for l in st.looks:
		for f in l.found:
			changes.append({"t": Frame.r2(f.t), "object": l.path, "what": f.what, "asset": f.asset})
	changes.sort_custom(func(a, b): return a.t < b.t)
	var out := {"changes": changes}
	if st.drawn:
		out.drawn = true
	return out
