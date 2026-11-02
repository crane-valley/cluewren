# Modified by Crane Valley: the capture is named playtest_agent, and a game already running is told as started.
## The editor's end of the debugger channel to the running game (EditorDebuggerPlugin): messages the game's
## autoload sends as "playtest_agent:<message>" come out as game_message; sessions starting, stopping and breaking
## come out as signals.
@tool
extends EditorDebuggerPlugin

signal game_message(message: String, data: Array, session_id: int)
signal session_started(session_id: int)
signal session_stopped(session_id: int)
signal session_breaked(session_id: int)

const CAPTURE := "playtest_agent"


func _has_capture(capture: String) -> bool:
	return capture == CAPTURE


func _capture(message: String, data: Array, session_id: int) -> bool:
	game_message.emit(message.trim_prefix(CAPTURE + ":"), data, session_id)
	return true


func _setup_session(session_id: int) -> void:
	var s := get_session(session_id)
	s.started.connect(func(): session_started.emit(session_id))
	s.stopped.connect(func(): session_stopped.emit(session_id))
	s.breaked.connect(func(_can_debug): session_breaked.emit(session_id))
	# A plugin enabled again while a game runs is set up after that game's `started` went by.
	if s.is_active():
		session_started.emit.call_deferred(session_id)


func send(session_id: int, message: String, data: Array) -> bool:
	var s := get_session(session_id)
	if s == null or not s.is_active():
		return false
	s.send_message(CAPTURE + ":" + message, data)
	return true


## Let a game that stopped at a script error go on (a playtest reports the error instead: log_error).
func resume(session_id: int) -> void:
	var s := get_session(session_id)
	if s and s.is_active() and s.is_breaked():
		s.send_message("continue", [])
