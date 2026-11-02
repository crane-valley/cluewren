# Modified by Crane Valley: replaces the dock with connection state and Connect and Disconnect; the theme, badge and the
# project id written to project.godot are removed.
## The editor plugin: registers the game-side autoload (playtest.gd) and adds the editor's link to the brain
## (editor_bridge.gd), which reaches the running game over the debugger connection (debugger.gd).
@tool
extends EditorPlugin

const AUTOLOAD := "Cluewren"
const AUTOLOAD_PATH := "res://addons/cluewren/playtest.gd"
const Debugger = preload("debugger.gd")
const EditorBridge = preload("editor_bridge.gd")

var debugger
var bridge
var holder: EditorDock
var state: Label
var connect_button: Button
var disconnect_button: Button


func _enable_plugin() -> void:
	add_autoload_singleton(AUTOLOAD, AUTOLOAD_PATH)


func _disable_plugin() -> void:
	remove_autoload_singleton(AUTOLOAD)


func _enter_tree() -> void:
	debugger = Debugger.new()
	add_debugger_plugin(debugger)
	bridge = EditorBridge.new()
	bridge.name = "CluewrenBridge"
	bridge.debugger = debugger
	add_child(bridge)
	var column := VBoxContainer.new()
	state = Label.new()
	state.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	connect_button = Button.new()
	connect_button.text = "Connect"
	connect_button.pressed.connect(bridge.connect_to_brain)
	disconnect_button = Button.new()
	disconnect_button.text = "Disconnect"
	disconnect_button.pressed.connect(bridge.disconnect_from_brain)
	column.add_child(state)
	column.add_child(connect_button)
	column.add_child(disconnect_button)
	holder = EditorDock.new()
	holder.name = "Cluewren"
	holder.title = "Cluewren"
	holder.layout_key = "Cluewren"
	holder.default_slot = EditorDock.DOCK_SLOT_RIGHT_UL
	holder.available_layouts = EditorDock.DOCK_LAYOUT_ALL
	holder.add_child(column)
	add_dock(holder)
	bridge.state_changed.connect(_show_state)
	_show_state()


func _exit_tree() -> void:
	if holder:
		remove_dock(holder)
		holder.queue_free()
		holder = null
	if bridge:
		bridge.queue_free()
	if debugger:
		remove_debugger_plugin(debugger)


func _show_state() -> void:
	state.text = bridge.state_text()
	connect_button.disabled = bridge.wants_connection()
	disconnect_button.disabled = not bridge.wants_connection()
