# Modified by Crane Valley: the per-frame path allocates nothing while nothing is found: the HUD check reads the tracked
# text controls instead of walking the tree, and the scan reuses one stack.
## In-game detectors, frame accurate: log_error, nan, out_of_world, long_fall, hitch,
## hud_fraction. Errors come from a Logger (push_error, script errors, engine errors), which may be called on any thread.
extends RefCounted

const Frame = preload("frame.gd")


class ErrorLog extends Logger:
	const NOTHING := []
	var mutex := Mutex.new()
	var lines := []

	func _log_error(function: String, file: String, line: int, code: String, rationale: String, _editor_notify: bool, error_type: int, script_backtraces: Array) -> void:
		var message := rationale if rationale != "" else code
		var where := "%s:%d" % [file, line]
		# push_error's own place is in the engine: the script's place is the backtrace's first frame
		for bt in script_backtraces:
			if bt != null and not bt.is_empty() and bt.get_frame_count() > 0:
				where = "%s:%d" % [bt.get_frame_file(0), bt.get_frame_line(0)]
				break
		mutex.lock()
		if lines.size() < 1000:
			lines.append({"type": error_type, "message": message, "where": where, "function": function})
		mutex.unlock()

	func drain() -> Array:
		mutex.lock()
		var out: Array = NOTHING if lines.is_empty() else lines
		if not lines.is_empty():
			lines = []
		mutex.unlock()
		return out


var host: Node
var world
var ui
var errors := ErrorLog.new()
var enabled := true
var counts := {}  # signature -> times seen
var config := {"long_fall_seconds": 8.0, "hitch_seconds": 0.75, "hud_every": 0.25, "scan_every": 0.5, "out_of_world_margin": 50.0}
var _falling := 0.0
var _hud_at := 0.0
var _scan_at := 0.0
var _kill_y = null
var _scene: Node = null
var _scene_at := 0
var _flagged := {}
var _stack: Array[Node] = []
static var _digits := RegEx.create_from_string("[0-9]+")


func _init(owner_node: Node, world_module, ui_module) -> void:
	host = owner_node
	world = world_module
	ui = ui_module
	OS.add_logger(errors)
	ui.track()


func shutdown() -> void:
	OS.remove_logger(errors)


func configure(args: Dictionary) -> void:
	if args.has("enabled"):
		enabled = bool(args.enabled)
	for k in config:
		if args.has(k):
			config[k] = float(args[k])


## Log events and findings for this frame. emit(event_dict) sends an event; t: game time; real_dt: the frame's wall
## time; paused: whether the game is paused.
func tick(emit: Callable, t: float, real_dt: float, paused: bool) -> void:
	for e in errors.drain():
		var error: bool = e.type != Logger.ERROR_TYPE_WARNING
		var category := "Script" if e.type == Logger.ERROR_TYPE_SCRIPT else ("Shader" if e.type == Logger.ERROR_TYPE_SHADER else "Engine")
		var msg := "%s (%s)" % [e.message, e.where]
		emit.call({"kind": "log", "t": t, "verbosity": "Error" if error else "Warning", "category": category, "message": msg.substr(0, 2000)})
		if error and enabled:
			var sig: String = "log_error:" + _normalize(e.message) + "@" + e.where
			_finding(emit, t, {"detector": "log_error", "severity": "error", "summary": "%s error: %s" % [category, e.message.substr(0, 300)],
				"signature": sig, "details": {"message": e.message, "where": e.where, "function": e.function, "category": category}})
	if not enabled:
		return
	var s: Node = world.scene()
	if s != _scene:
		_scene = s
		_scene_at = Time.get_ticks_msec()
		_kill_y = null
		_flagged.clear()
		_falling = 0.0
	var loading := Time.get_ticks_msec() - _scene_at < 3000
	if not paused and not loading and real_dt > config.hitch_seconds:
		_finding(emit, t, {"detector": "hitch", "severity": "warning", "summary": "A frame took %.2f s" % real_dt,
			"signature": "hitch", "details": {"seconds": snappedf(real_dt, 0.01)}})
	var p: Node = world.player()
	if p is CharacterBody3D or p is CharacterBody2D:
		var falling_now: bool = not p.is_on_floor() and (p.velocity.y < 0.0 if p is CharacterBody3D else p.velocity.y > 0.0)
		if paused:
			pass
		elif falling_now:
			_falling += real_dt * Engine.time_scale
		else:
			_falling = 0.0
		if _falling > config.long_fall_seconds:
			_falling = -1e9  # once per fall
			_finding(emit, t, {"detector": "long_fall", "severity": "error", "summary": "%s kept falling for %d s" % [p.name, int(config.long_fall_seconds)],
				"signature": "long_fall:" + str(p.name), "actor": str(p.name), "where": _where(p)})
	var now := Time.get_ticks_msec() / 1000.0
	if now - _hud_at >= config.hud_every:
		_hud_at = now
		_hud_fraction(emit, t)
	if now - _scan_at >= config.scan_every and s != null and not loading:
		_scan_at = now
		_scan(emit, t, s)


func _where(n: Node):
	if n is Node3D:
		return Frame.kit(n.global_position)
	if n is Node2D:
		return Frame.kit2d(n.global_position)
	return null


static func _normalize(s: String) -> String:
	return _digits.sub(s, "#", true).substr(0, 160)


func _finding(emit: Callable, t: float, f: Dictionary) -> void:
	var sig: String = f.signature
	var n: int = counts.get(sig, 0) + 1
	counts[sig] = n
	if not (n == 1 or n == 10 or n == 100 or n == 1000):
		return
	f.t = t
	f.count = n
	f.source = "engine"
	emit.call({"kind": "finding", "t": t, "finding": f})


var _fraction := RegEx.create_from_string("(\\d+)\\s*/\\s*(\\d+)")


func _hud_fraction(emit: Callable, t: float) -> void:
	for c in ui.tracked:
		var text: String = ui.text_of(c)
		if not "/" in text or not ui.on_screen(c):
			continue
		for m in _fraction.search_all(text):
			var a := int(m.get_string(1))
			var b := int(m.get_string(2))
			if b > 0 and a > b:
				var key: String = ui.key_of(c)
				_finding(emit, t, {"detector": "hud_fraction", "severity": "error",
					"summary": "HUD text %s shows %d/%d: more than its stated maximum" % [key, a, b],
					"signature": "hud_fraction:" + key, "details": {"key": key, "text": text, "value": a, "max": b}})


## NaN transforms and bodies below the level (the lowest drawn point at the scene's start, less a margin).
func _scan(emit: Callable, t: float, s: Node) -> void:
	if _kill_y == null and s is Node3D:
		var low := INF
		for v in s.find_children("*", "VisualInstance3D", true, false):
			if v is GeometryInstance3D:
				low = minf(low, (v.global_transform * v.get_aabb()).position.y)
		_kill_y = (low if low != INF else 0.0) - config.out_of_world_margin
	var checked := 0
	_stack.clear()
	_stack.append(s)
	while _stack.size() > 0 and checked < 5000:
		var n: Node = _stack.pop_back()
		for i in n.get_child_count():
			_stack.append(n.get_child(i))
		if n is Node3D:
			checked += 1
			var pos: Vector3 = n.global_position
			if not (is_finite(pos.x) and is_finite(pos.y) and is_finite(pos.z)) or not n.global_basis.is_finite():
				if not _flagged.has(n):
					_flagged[n] = true
					_finding(emit, t, {"detector": "nan", "severity": "error", "summary": "%s has a NaN or infinite transform" % n.name,
						"signature": "nan:" + str(n.name), "actor": str(n.name)})
			elif _kill_y != null and (n is PhysicsBody3D) and pos.y < _kill_y and not _flagged.has(n):
				_flagged[n] = true
				_finding(emit, t, {"detector": "out_of_world", "severity": "error",
					"summary": "%s fell out of the world (below %.0f m)" % [n.name, _kill_y],
					"signature": "out_of_world:" + str(n.name), "actor": str(n.name), "where": Frame.kit(pos)})
		elif n is Node2D:
			checked += 1
			var p2: Vector2 = n.global_position
			if not (is_finite(p2.x) and is_finite(p2.y)) and not _flagged.has(n):
				_flagged[n] = true
				_finding(emit, t, {"detector": "nan", "severity": "error", "summary": "%s has a NaN or infinite transform" % n.name,
					"signature": "nan:" + str(n.name), "actor": str(n.name)})
