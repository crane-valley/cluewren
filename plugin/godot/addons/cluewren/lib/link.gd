# Modified by Crane Valley: the brain is found through its invite and must prove itself with a mutual HMAC handshake, a
# dropped connection resumes its session, and the bearer token, default URL and hosted-service paths are removed.
## The connection out to the brain: one WebSocket (WebSocketPeer) carrying JSON text frames. Polled every frame by its
## owner. While wanted and not connected it looks for the brain's invite once a second, and after a connection that
## failed it tries again with a backoff (1 s doubling to 15 s). The address and the secret are the invite's at every
## attempt: each run has its own.
@tool
extends RefCounted

const Discovery = preload("discovery.gd")
const Handshake = preload("handshake.gd")
const MAX_BACKOFF := 15.0
const LOOK_SECONDS := 1.0
const CONNECT_SECONDS := 10.0
const MAX_HELD := 200
const CLOSE_NORMAL := 1000
const CLOSE_POLICY := 1008
const CLOSE_RELOAD := 1012
const REFUSED := "The brain refused the connection"
# A brain that refuses a hello has not proved itself: its code picks one of these texts, and its own words are not shown.
const REFUSALS := {
	"protocol": Discovery.OTHER_RELEASE,
	"not_authenticated": "The brain did not accept this editor's proof of the invite",
	"project": "The run is for another project than this editor's",
	"resume": "The session this editor tried to resume belongs to another project or engine",
}

var url := ""
var ws: WebSocketPeer = null
var want := false
var connected := false  # both sides have proved themselves: nothing but the handshake crosses before this
var role := ""  # the brain's role from its welcome ("" until welcomed)
var session := ""  # the brain's session, kept across a drop to ask for it back
var received := 0  # the newest request taken: on a resume, the brain sends again what came after it
var problem := ""  # what went wrong last, in words
var backoff := 1.0
var next_try := 0.0
var hello_fields: Callable  # () -> Dictionary: what the host says in its hello
var on_frame: Callable  # (frame: Dictionary, text: String)
var on_welcomed: Callable  # (resumed: bool)
var on_closed: Callable  # (was_connected: bool)
var on_refused: Callable  # ()

var _discovery := Discovery.new()
var _json := JSON.new()
var _secret := PackedByteArray()
var _nonce := PackedByteArray()
var _said_hello := false
var _asked_resume := ""
var _deadline := 0.0
var _held := PackedStringArray()
var _refused := false  # the problem is a brain's refusal: it stays shown when that brain takes its invite away
static var _control_chars := RegEx.create_from_string("[\\x00-\\x1f]")
static var _code_shape := RegEx.create_from_string("^[a-z0-9_]{1,32}$")


func _now() -> float:
	return Time.get_ticks_msec() / 1000.0


static func refusal_text(code: String) -> String:
	return REFUSALS.get(code, "%s (%s)" % [REFUSED, code])


func open() -> void:
	want = true
	backoff = 1.0
	problem = ""
	_refused = false
	_connect()


func _connect() -> void:
	close_socket()
	var invite := _discovery.look()
	if invite.has("pending"):
		next_try = _now() + 0.05
		return
	if not invite.has("url"):
		if invite.has("problem"):
			problem = invite.problem
		elif invite.has("absent") and not _refused:
			problem = ""
		next_try = _now() + LOOK_SECONDS
		return
	_refused = false
	url = invite.url
	_secret = invite.secret
	ws = WebSocketPeer.new()
	ws.inbound_buffer_size = 16 * 1024 * 1024
	ws.outbound_buffer_size = 64 * 1024 * 1024
	ws.max_queued_packets = 16384
	_said_hello = false
	_deadline = _now() + CONNECT_SECONDS
	next_try = INF
	var err := ws.connect_to_url(url)
	if err != OK:
		problem = "Can't connect to %s (error %d)" % [url, err]
		_schedule()


func close_socket() -> void:
	if ws:
		ws.close()
	_forget_socket()


func close() -> void:
	want = false
	session = ""
	received = 0
	_held.clear()
	if ws:
		ws.close(CLOSE_NORMAL, "disconnect")
	_forget_socket()
	_discovery.finish()


## The brain keeps the session for a plugin that closes with 1012 and comes back with it.
func close_for_reload() -> void:
	want = false
	if ws:
		ws.close(CLOSE_RELOAD, "reload")
	_forget_socket()
	_discovery.finish()


func _forget_socket() -> void:
	ws = null
	connected = false
	role = ""
	_secret = PackedByteArray()
	_nonce = PackedByteArray()
	_deadline = 0.0


## After a connection that failed or ended. The invite is validated again before the next one: its brain may be gone.
func _schedule() -> void:
	_forget_socket()
	_discovery.forget()
	if want:
		next_try = _now() + backoff
		backoff = minf(backoff * 2.0, MAX_BACKOFF)


func _send_hello() -> void:
	_nonce = Handshake.new_nonce()
	var h: Dictionary = hello_fields.call()
	h.type = "hello"
	h.protocol = Discovery.PROTOCOL
	h.nonce = Marshalls.raw_to_base64(_nonce)
	h.project_dir = Discovery.project_dir()
	h.mac = Marshalls.raw_to_base64(Handshake.hello_mac(_secret, _nonce, h.project_dir))
	_asked_resume = session
	if session != "":
		h.resume = session
		h.received = received
	ws.send_text(JSON.stringify(h))


## Poll the socket; each frame that came in goes to on_frame.
func poll() -> void:
	if ws == null:
		if want and _now() >= next_try:
			_connect()
		return
	ws.poll()
	var state := ws.get_ready_state()
	if state == WebSocketPeer.STATE_OPEN and not _said_hello:
		_said_hello = true
		_deadline = _now() + CONNECT_SECONDS
		_send_hello()
	# Every frame that came in, whatever the state now: a brain that sends its last frame and closes at once has both
	# arrive in one poll, and the frame must not be lost with the connection
	while ws != null and ws.get_available_packet_count() > 0:
		_take(ws.get_packet().get_string_from_utf8())
	if ws == null:
		return
	if state == WebSocketPeer.STATE_CLOSED:
		_closed()
	elif _deadline > 0.0 and _now() >= _deadline:
		# A peer that accepts the socket and never welcomes must not hold the next attempt back for ever.
		problem = "No welcome from %s within %d s" % [url, int(CONNECT_SECONDS)]
		close_socket()
		_schedule()


func _closed() -> void:
	var was := connected
	var code := ws.get_close_code()
	if not was and code == CLOSE_POLICY:
		# The brain refuses with an error frame and then this close, and WebSocketPeer drops a frame that comes in the
		# same poll as the close. The refusal counts all the same; its code is known when the close's reason carries it.
		var reason := ws.get_close_reason()
		_refuse("Refused: " + (refusal_text(reason) if _code_shape.search(reason) else REFUSED), CLOSE_POLICY)
		return
	if was:
		problem = "Connection closed (%d %s)" % [code, ws.get_close_reason()]
		if code == CLOSE_NORMAL:
			session = ""
			received = 0
			_held.clear()
	else:
		problem = "Can't reach the brain at %s. Trying again..." % url
	_schedule()
	if on_closed.is_valid():
		on_closed.call(was)


func _take(text: String) -> void:
	var frame = _json.data if _json.parse(text) == OK else null
	var type := str(frame.get("type", "")) if typeof(frame) == TYPE_DICTIONARY else ""
	if not connected:
		if type == "welcome" and _proved(frame):
			_welcomed(frame)
		elif type == "error" and typeof(frame.get("code")) == TYPE_STRING and _code_shape.search(frame.code):
			_refuse("Refused: " + refusal_text(frame.code), CLOSE_NORMAL)
		else:
			_refuse("Refused %s: it didn't prove it is this machine's brain" % url, CLOSE_POLICY)
		return
	if typeof(frame) != TYPE_DICTIONARY:
		push_warning("Cluewren: unreadable message from the brain")
		return
	match type:
		"request":
			received = maxi(received, int(frame.get("id", 0)))
		"error":
			_refuse(str(frame.get("message", "the brain gave up")), CLOSE_NORMAL)
			return
	on_frame.call(frame, text)


func _proved(welcome: Dictionary) -> bool:
	var nonce := Marshalls.base64_to_raw(str(welcome.get("nonce", "")))
	var proof := Marshalls.base64_to_raw(str(welcome.get("proof", "")))
	if nonce.size() != Handshake.NONCE_BYTES or proof.is_empty() or _secret.is_empty():
		return false
	if not Handshake.verify(_secret, Handshake.BRAIN_ROLE, _nonce, nonce, proof):
		return false
	# Here, before anything else can be sent: the brain takes the next frame as this proof.
	var mine := Handshake.prove(_secret, Handshake.PLUGIN_ROLE, _nonce, nonce)
	ws.send_text(JSON.stringify({"type": "proof", "proof": Marshalls.raw_to_base64(mine)}))
	return true


func _welcomed(frame: Dictionary) -> void:
	_secret = PackedByteArray()
	_nonce = PackedByteArray()
	_deadline = 0.0
	role = str(frame.get("role", ""))
	var resumed := _asked_resume != "" and str(frame.get("session", "")) == _asked_resume
	_asked_resume = ""
	session = str(frame.get("session", ""))
	if not resumed:
		received = 0
		_held.clear()
	backoff = 1.0
	problem = ""
	connected = true
	for held in _held:
		ws.send_text(held)
	_held.clear()
	on_welcomed.call(resumed)


## No reconnect: asking the same brain again won't change its answer. Only an invite that changed is taken up.
func _refuse(why: String, code: int) -> void:
	session = ""
	received = 0
	_held.clear()
	problem = why
	_refused = true
	_discovery.refuse()
	ws.close(code, "refused")
	_forget_socket()
	next_try = _now() + LOOK_SECONDS
	if on_refused.is_valid():
		on_refused.call()


## JSON.stringify leaves most control characters raw inside strings, and game text can hold them (a stale keycode's
## as_text() does): the brain's strict parser takes that for a broken connection. Newlines and tabs are escaped already.
static func _clean(text: String) -> String:
	return _control_chars.sub(text, "", true) if _control_chars.search(text) else text


## raw: a picture's message, all base64 and far too long to scan on the game's thread.
func send_text(text: String, raw := false) -> bool:
	if ws == null or not connected:
		return false
	return ws.send_text(text if raw else _clean(text)) == OK


## A response goes out now, or waits for the session to resume.
func send_answer(text: String, raw := false) -> void:
	if not send_text(text, raw) and want and session != "" and _held.size() < MAX_HELD:
		_held.append(text if raw else _clean(text))
