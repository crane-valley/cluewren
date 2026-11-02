# Modified by Crane Valley: the controls with text are tracked through the tree's signals, for the HUD detector, and the
# tree walks reuse one stack.
## The screen's texts (`screen`) and its interactive controls (`ui`, `ui_act`).
##
## Keys and ids are "<Owner>.<Name>": Owner is the root of the scene the control was saved in (its `owner`), else
## the nearest scene instance above it, else "Screen" (controls built in code); repeated keys get "#2", "#3".
## A text or control counts when it is visible in the tree, on the viewport and not fully transparent.
## ui_act acts as the player's mouse does: a click is a mouse press and release pushed into the viewport at the
## control's middle (so an overlay that stops the mouse stops it too); when that doesn't press it (nothing took the
## click), the control's `pressed` signal is emitted instead and the answer says "via": "signal".
extends RefCounted

var host: Node
var _last := {}  # id -> Control, from the latest listing
var _stack: Array[Node] = []
var tracked := {}  # Control -> true: the ones that can hold text


func _init(owner_node: Node) -> void:
	host = owner_node


## Keep `tracked` as the tree changes, so a check several times a second needn't walk the tree.
func track() -> void:
	var tree := host.get_tree()
	tree.node_added.connect(_on_node_added)
	tree.node_removed.connect(_on_node_removed)
	_stack.clear()
	_stack.append(tree.root)
	while _stack.size() > 0:
		var n: Node = _stack.pop_back()
		for i in n.get_child_count():
			_stack.append(n.get_child(i))
		_on_node_added(n)


func _on_node_added(n: Node) -> void:
	if n is Label or n is RichTextLabel or n is Button or n is LineEdit or n is TextEdit:
		tracked[n] = true


func _on_node_removed(n: Node) -> void:
	tracked.erase(n)


func key_of(c: Control) -> String:
	return _owner_name(c) + "." + str(c.name)


## The UI a control belongs to: its nearest CanvasLayer, scene instance root or owner (the root of the scene it was
## saved in), as a player tells "the HUD" from "the menu".
func _owner_name(n: Node) -> String:
	var x := n.get_parent()
	while x and x != host.get_tree().root:
		if x is CanvasLayer or x.scene_file_path != "" or x == n.owner:
			return str(x.name)
		x = x.get_parent()
	return "Screen"


func on_screen(c: Control) -> bool:
	if not c.is_visible_in_tree():
		return false
	var a := 1.0
	var x: Node = c
	while x:
		if x is CanvasItem:
			a *= x.modulate.a
			if x == c:
				a *= x.self_modulate.a
		x = x.get_parent()
	if a <= 0.01:
		return false
	var vp := c.get_viewport()
	var rect := Rect2(c.get_global_transform_with_canvas().origin, c.size * c.get_global_transform_with_canvas().get_scale())
	return vp.get_visible_rect().intersects(rect, true) or c.size == Vector2.ZERO


func _controls() -> Array:
	var out := []
	_stack.clear()
	_stack.append(host.get_tree().root)
	while _stack.size() > 0:
		var n: Node = _stack.pop_back()
		if n == host:
			continue
		if n is CanvasItem and not n.visible:
			continue
		if n is CanvasLayer and not n.visible:
			continue
		for i in range(n.get_child_count() - 1, -1, -1):
			_stack.append(n.get_child(i))
		if n is Control and on_screen(n):
			out.append(n)
	return out


static func text_of(c: Control) -> String:
	if c is Label:
		return c.text
	if c is RichTextLabel:
		return c.get_parsed_text()
	if c is Button:
		return c.text
	if c is LineEdit:
		return c.text if c.text != "" else ""
	if c is TextEdit:
		return c.text
	return ""


func _keyed(nodes: Array) -> Array:
	var counts := {}
	var out := []
	for n in nodes:
		var base := _owner_name(n) + "." + str(n.name)
		counts[base] = counts.get(base, 0) + 1
		var key := base if counts[base] == 1 else "%s#%d" % [base, counts[base]]
		out.append([key, n])
	return out


func screen(args: Dictionary) -> Dictionary:
	var texts := {}
	var with_text := []
	for c in _controls():
		if text_of(c).strip_edges() != "":
			with_text.append(c)
	for kn in _keyed(with_text):
		texts[kn[0]] = text_of(kn[1])
	var out := {"texts": texts}
	if args.get("ui", false):
		out.ui = list()
	return out


static func kind_of(c: Control) -> String:
	if c is OptionButton:
		return "combo"
	if c is CheckBox or c is CheckButton:
		return "checkbox"
	if c is BaseButton:
		return "checkbox" if c.toggle_mode and not (c is MenuButton) else "button"
	if c is Slider or c is SpinBox:
		return "slider"
	if c is LineEdit or c is TextEdit:
		return "text_input"
	return ""


func list() -> Array:
	var interactive := []
	for c in _controls():
		if kind_of(c) != "":
			interactive.append(c)
	_last.clear()
	var out := []
	for kn in _keyed(interactive):
		var c: Control = kn[1]
		var kind := kind_of(c)
		_last[kn[0]] = c
		var label := text_of(c)
		if label == "" and c is BaseButton:
			label = c.tooltip_text
		if label == "":
			for l in c.find_children("*", "Label", true, false):
				label = l.text
				break
		var enabled := true
		if c is BaseButton:
			enabled = not c.disabled
		elif c is LineEdit:
			enabled = c.editable
		elif c is TextEdit:
			enabled = c.editable
		elif c is Range:
			enabled = c.editable if "editable" in c else true
		var center := c.get_global_transform_with_canvas() * (c.size / 2.0)
		var item := {"id": kn[0], "kind": kind, "name": str(c.name), "owner": _owner_name(c), "label": label,
			"enabled": enabled, "path": str(c.get_path()), "class": c.get_class(), "screen": {"x": roundf(center.x), "y": roundf(center.y)}}
		if kind == "checkbox":
			item.checked = c.button_pressed
		if c is OptionButton:
			var options := []
			for i in c.item_count:
				options.append(c.get_item_text(i))
			item.options = options
			item.selected = c.get_item_text(c.selected) if c.selected >= 0 else ""
		if c is Range:
			item.value = c.value
			item.min = c.min_value
			item.max = c.max_value
		if c is LineEdit and c.placeholder_text != "":
			item.placeholder = c.placeholder_text
		out.append(item)
	return out


func _find(id: String) -> Control:
	if id.begins_with("/"):
		var n := host.get_tree().root.get_node_or_null(NodePath(id))
		return n if n is Control else null
	if not _last.has(id) or not is_instance_valid(_last[id]):
		list()
	var c = _last.get(id, null)
	return c if is_instance_valid(c) else null


func _push_mouse(c: Control, button: int, double := false) -> void:
	var vp := c.get_viewport()
	var pos := c.get_global_transform_with_canvas() * (c.size / 2.0)
	var motion := InputEventMouseMotion.new()
	motion.position = pos
	motion.global_position = pos
	vp.push_input(motion, true)
	for pressed in [true, false]:
		var ev := InputEventMouseButton.new()
		ev.button_index = button
		ev.pressed = pressed
		ev.double_click = double and pressed
		ev.position = pos
		ev.global_position = pos
		ev.button_mask = (1 << (button - 1)) if pressed else 0
		vp.push_input(ev, true)


func act(args: Dictionary) -> Array:
	var id := str(args.get("id", ""))
	var action := str(args.get("action", "click"))
	var c := _find(id)
	if c == null:
		return [false, "no element %s on screen" % id]
	if c is BaseButton and c.disabled:
		return [false, "%s is disabled" % id]
	match action:
		"click":
			if c is OptionButton:
				var option = args.get("option", null)
				var idx := -1
				if option == null:
					idx = (c.selected + 1) % maxi(c.item_count, 1)
				else:
					for i in c.item_count:
						if c.get_item_text(i) == str(option):
							idx = i
					if idx < 0:
						return [false, "%s has no option %s" % [id, str(option)]]
				c.select(idx)
				c.item_selected.emit(idx)
				return [true, {"via": "select"}]
			if c is BaseButton:
				var state := {"pressed": false, "toggled": false}
				var on_pressed := func(): state.pressed = true
				var on_toggled := func(_on): state.toggled = true
				c.pressed.connect(on_pressed)
				c.toggled.connect(on_toggled)
				_push_mouse(c, MOUSE_BUTTON_LEFT)
				if c.pressed.is_connected(on_pressed):
					c.pressed.disconnect(on_pressed)
				if c.toggled.is_connected(on_toggled):
					c.toggled.disconnect(on_toggled)
				if state.pressed or state.toggled:
					return [true, {"via": "mouse"}]
				if not is_instance_valid(c):
					return [true, {"via": "mouse"}]
				if c.toggle_mode:
					c.button_pressed = not c.button_pressed
				c.pressed.emit()
				return [true, {"via": "signal"}]
			_push_mouse(c, MOUSE_BUTTON_LEFT)
			return [true, {"via": "mouse"}]
		"right_click":
			_push_mouse(c, MOUSE_BUTTON_RIGHT)
			return [true, {"via": "mouse"}]
		"double_click":
			_push_mouse(c, MOUSE_BUTTON_LEFT, true)
			return [true, {"via": "mouse"}]
		"set":
			if c is Range:
				c.value = float(args.get("value", c.value))
				return [true, {}]
			if c is LineEdit:
				var text := str(args.get("text", args.get("value", "")))
				c.text = text
				c.text_changed.emit(text)
				c.text_submitted.emit(text)
				return [true, {}]
			if c is TextEdit:
				c.text = str(args.get("text", args.get("value", "")))
				c.text_changed.emit()
				return [true, {}]
			if c is BaseButton and c.toggle_mode:
				var on := bool(args.get("value", true))
				if c.button_pressed != on:
					c.button_pressed = on
				return [true, {}]
			return [false, "%s takes no value" % id]
		"drag":
			var to := _find(str(args.get("to", "")))
			if to == null:
				return [false, "no element %s on screen to drop on" % str(args.get("to", ""))]
			var from_pos := c.size / 2.0
			var data = c.call("_get_drag_data", from_pos) if c.has_method("_get_drag_data") else null
			if data == null:
				return [false, "%s can't be dragged" % id]
			var at := to.size / 2.0
			if not (to.has_method("_can_drop_data") and to.call("_can_drop_data", at, data)):
				return [false, "%s didn't take the drop" % str(args.get("to", ""))]
			to.call("_drop_data", at, data)
			return [true, {}]
	return [false, "unknown action %s" % action]
