# Modified by Crane Valley: a still is sent as an answer, so it can wait out a dropped connection, and pictures skip the
# text scan other messages get.
## The game's picture for the brain: `record` (frames at most `fps` a
## second while the game runs and isn't paused) and `capture` (one still), of the root viewport, UI included: what the
## player sees.
##
## The game thread never waits on the GPU or on encoding:
##  - the picture is read back with RenderingDevice.texture_get_data_async (Forward+ and Mobile; queued on the render
##    thread), its callback only keeping the bytes; the Compatibility renderer has no asynchronous readback, so there
##    the viewport's get_image() is taken, at the frame rate asked and no more often;
##  - scaling, the JPEG and, when the game holds the connection itself, the base64 and the JSON text are made on the
##    WorkerThreadPool; the game thread only hands over the finished text (or, through the editor, the JPEG's bytes);
##  - a frame whose time comes while the one before is still being read or encoded is skipped (a gap in `n`).
## Without a picture (--headless, the dummy renderer) both answer 409 "no picture: ...".
extends RefCounted

const DEBUGGER_MAX_BYTES := 7 * 1024 * 1024  # a debugger message is at most 8 MiB (RemoteDebuggerPeerTCP)
const STALE_SECONDS := 3.0  # a readback or encode not done by then is dropped (a resize may lose a readback)

## One picture on its way: read back, then scaled and encoded on a worker. Fields written off the game thread are
## guarded by `lock`.
class Job:
	extends RefCounted
	var lock := Mutex.new()
	var still := false  # a capture (answered as a response), else a recording frame
	var id = null  # the capture's request id
	var n := 0  # the frame's number
	var t = null  # game time it shows
	var wall := 0.0  # unix seconds when taken
	var max_dim := 960
	var quality := 75
	var as_text := false  # make the whole message's JSON text (the game sends it itself), else the JPEG's bytes
	var gen := 0
	var started := 0.0
	# the readback (written on the render thread)
	var read := false  # the bytes (or image) are here
	var failed := ""  # why there is no picture
	var fallback := false  # no asynchronous readback for this texture: take get_image() on the game thread
	var image: Image = null
	var data := PackedByteArray()
	var w := 0
	var h := 0
	var image_format := -1
	var linear := false
	# the encode (written on a worker)
	var task := -1
	var done := false
	var width := 0
	var height := 0
	var jpeg := PackedByteArray()
	var text := ""

	func set_read(bytes: PackedByteArray) -> void:
		lock.lock()
		data = bytes
		read = true
		lock.unlock()

	func set_failed(why: String, try_image := false) -> void:
		lock.lock()
		if try_image:
			fallback = true
		else:
			failed = why
		read = true
		lock.unlock()


var host  # playtest.gd: the viewport, game time, and sending
var recording := false
var fps := 5.0
var max_dim := 960
var quality := 75
var n := 0  # the next frame's number
var sent := 0  # frames sent since the recording started
var _gen := 0
var _next_at := 0.0
var _frame: Job = null  # the recording's frame in flight
var _stills := []  # captures in flight


func _init(owner_) -> void:
	host = owner_


## Why there is no picture to capture, or "" when there is one.
static func no_picture() -> String:
	if DisplayServer.get_name() == "headless":
		return "the game runs headless (--headless): nothing is drawn"
	if RenderingServer.get_current_rendering_driver_name() == "dummy":
		return "the game runs without rendering (the dummy renderer)"
	return ""


static func can_capture() -> bool:
	return no_picture() == ""


## A size scaled so its longer side is at most max_dim (never up).
static func fit(size: Vector2i, limit: int) -> Vector2i:
	var longer := maxi(size.x, size.y)
	if longer <= limit or longer <= 0:
		return size
	var f := float(limit) / float(longer)
	return Vector2i(maxi(1, roundi(size.x * f)), maxi(1, roundi(size.y * f)))


static func _clamp_dim(v) -> int:
	return clampi(int(v), 16, 8192) if typeof(v) in [TYPE_INT, TYPE_FLOAT] else 0


static func _clamp_quality(v, default: int) -> int:
	return clampi(int(v), 1, 100) if typeof(v) in [TYPE_INT, TYPE_FLOAT] else default


func _view_size() -> Vector2i:
	var tex = host.get_viewport().get_texture()
	return Vector2i(tex.get_size()) if tex else Vector2i.ZERO


## `record` start: [true, result] or [false, error, status]. n0: the first frame's number (a recording the editor
## starts again in a new play session goes on with its numbers).
func start(args: Dictionary, n0 := 0) -> Array:
	var why := no_picture()
	if why != "":
		return [false, "no picture: " + why, 409]
	var f = args.get("fps", 5)
	fps = clampf(float(f), 0.1, 60.0) if typeof(f) in [TYPE_INT, TYPE_FLOAT] and float(f) > 0.0 else 5.0
	var d := _clamp_dim(args.get("max_dim", 960))
	max_dim = d if d > 0 else 960
	quality = _clamp_quality(args.get("quality", 75), 75)
	recording = true
	_gen += 1
	n = maxi(0, n0)
	sent = 0
	_next_at = 0.0
	_frame = null
	var size := fit(_view_size(), max_dim)
	return [true, {"recording": true, "width": size.x, "height": size.y}]


## `record` stop: the frames sent since the start.
func stop() -> Dictionary:
	var frames := sent if recording else 0
	recording = false
	_gen += 1
	_frame = null
	return {"recording": false, "frames": frames}


## `capture`: answered later (null) with the still, or now with an error.
func capture(id, args: Dictionary):
	var why := no_picture()
	if why != "":
		return [false, "no picture: " + why, 409]
	if host.get_tree().current_scene == null:
		return [false, "no picture: no scene is running", 409]
	var job := Job.new()
	job.still = true
	job.id = id
	var d := _clamp_dim(args.get("max_dim", 1920))
	job.max_dim = d if d > 0 else 1920
	job.quality = _clamp_quality(args.get("quality", 90), 90)
	_begin(job)
	_stills.append(job)
	return null


## Every frame (the game thread): hand over what is done, and take the recording's next frame when its time comes.
func tick(now: float) -> void:
	_collect(now)
	if not recording:
		return
	var tree: SceneTree = host.get_tree()
	if tree.paused or tree.current_scene == null:
		return  # (no frames, and no gaps: a paused picture doesn't change)
	if now < _next_at:
		return
	var period := 1.0 / fps
	_next_at = _next_at + period if _next_at > 0.0 and now - _next_at < period else now + period
	var num := n
	n += 1
	if _frame != null:
		return  # the one before is still on its way: this one is skipped (a gap in n)
	var job := Job.new()
	job.n = num
	job.max_dim = max_dim
	job.quality = quality
	job.gen = _gen
	_begin(job)
	_frame = job


func _begin(job: Job) -> void:
	job.t = host.t_value()
	job.wall = Time.get_unix_time_from_system()
	job.started = Time.get_ticks_msec() / 1000.0
	job.as_text = not host.relay
	var tex = host.get_viewport().get_texture()
	if tex == null:
		job.set_failed("the game's viewport has no picture")
		return
	var rd := RenderingServer.get_rendering_device()
	if rd == null:
		job.set_failed("", true)  # (Compatibility renderer: get_image() when collected)
		return
	var rid: RID = tex.get_rid()
	RenderingServer.call_on_render_thread(_read_on_render_thread.bind(rd, rid, job))


## On the render thread: ask the GPU for the texture's bytes; the callback only keeps them.
static func _read_on_render_thread(rd: RenderingDevice, rid: RID, job: Job) -> void:
	var rd_rid := RenderingServer.texture_get_rd_texture(rid)
	if not rd_rid.is_valid() or not rd.texture_is_valid(rd_rid):
		job.set_failed("", true)
		return
	var f: RDTextureFormat = rd.texture_get_format(rd_rid)
	var fmt := -1
	var linear := false
	match f.format:
		RenderingDevice.DATA_FORMAT_R8G8B8A8_UNORM, RenderingDevice.DATA_FORMAT_R8G8B8A8_SRGB:
			fmt = Image.FORMAT_RGBA8
		RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT:
			fmt = Image.FORMAT_RGBAH
			linear = true
		RenderingDevice.DATA_FORMAT_R32G32B32A32_SFLOAT:
			fmt = Image.FORMAT_RGBAF
			linear = true
	if fmt < 0:
		job.set_failed("", true)  # (a format this doesn't read: get_image() converts it)
		return
	job.lock.lock()
	job.w = f.width
	job.h = f.height
	job.image_format = fmt
	job.linear = linear
	job.lock.unlock()
	var err := rd.texture_get_data_async(rd_rid, 0, job.set_read)
	if err != OK:
		job.set_failed("", true)


## Hand over the jobs done, start the encodes of those read back, and drop the stale ones.
func _collect(now: float) -> void:
	if _frame != null:
		if _frame.gen != _gen:
			_frame = null
		elif _advance(_frame, now):
			var job := _frame
			_frame = null
			if job.failed == "" and recording and job.gen == _gen:
				_send_frame(job)
	var i := 0
	while i < _stills.size():
		var job: Job = _stills[i]
		if _advance(job, now):
			_stills.remove_at(i)
			if job.failed != "":
				host.respond_error(job.id, "no picture: " + job.failed, 409 if job.failed != "it took too long" else 500)
			else:
				_send_still(job)
		else:
			i += 1


## Move a job on: start its encode once it is read back (taking get_image() here for a texture with no asynchronous
## readback). true when it is over (done, failed or stale).
func _advance(job: Job, now: float) -> bool:
	if job.task >= 0:
		if not WorkerThreadPool.is_task_completed(job.task):
			if now - job.started > STALE_SECONDS * 3.0:
				job.failed = "it took too long"  # (the task finishes later; its result is dropped)
				return true
			return false
		WorkerThreadPool.wait_for_task_completion(job.task)
		job.task = -1
		return true
	job.lock.lock()
	var read := job.read
	var fallback := job.fallback
	job.lock.unlock()
	if not read:
		if now - job.started > STALE_SECONDS:
			job.failed = "it took too long"
			return true
		return false
	if job.failed != "":
		return true
	if fallback and job.image == null:
		var tex = host.get_viewport().get_texture()
		job.image = tex.get_image() if tex else null
		if job.image == null or job.image.is_empty():
			job.failed = "the game's viewport gave no picture"
			return true
		job.linear = job.image.get_format() in [Image.FORMAT_RGBAH, Image.FORMAT_RGBAF, Image.FORMAT_RGBH, Image.FORMAT_RGBF]
	job.task = WorkerThreadPool.add_task(_encode.bind(job), false, "Playtest Agent picture")
	return false


## On a worker: the picture scaled, as a JPEG, and (as_text) the message's JSON text.
static func _encode(job: Job) -> void:
	var img: Image = job.image
	if img == null:
		var bpp: int = {Image.FORMAT_RGBA8: 4, Image.FORMAT_RGBAH: 8, Image.FORMAT_RGBAF: 16}.get(job.image_format, 0)
		var need := job.w * job.h * bpp
		if need <= 0 or job.data.size() < need:
			job.failed = "the picture read back is incomplete"
			return
		var bytes := job.data if job.data.size() == need else job.data.slice(0, need)
		img = Image.create_from_data(job.w, job.h, false, job.image_format, bytes)
	if img == null or img.is_empty():
		job.failed = "the picture read back is empty"
		return
	if img.get_format() != Image.FORMAT_RGBA8 and img.get_format() != Image.FORMAT_RGB8:
		img.convert(Image.FORMAT_RGBA8)
	var size := fit(img.get_size(), job.max_dim)
	if size != img.get_size():
		img.resize(size.x, size.y, Image.INTERPOLATE_BILINEAR)
	if job.linear:
		img.linear_to_srgb()
	if img.get_format() != Image.FORMAT_RGB8:
		img.convert(Image.FORMAT_RGB8)  # (JPEG has no alpha)
	job.jpeg = img.save_jpg_to_buffer(job.quality / 100.0)
	job.width = img.get_width()
	job.height = img.get_height()
	if job.jpeg.is_empty():
		job.failed = "the JPEG encoder failed"
		return
	if job.as_text:
		var b64 := Marshalls.raw_to_base64(job.jpeg)
		if job.still:
			job.text = response_text(job.id, {"width": job.width, "height": job.height, "t": job.t}, b64, job.t)
		else:
			job.text = frame_text({"n": job.n, "t": job.t, "wall": job.wall, "width": job.width, "height": job.height}, b64)
	job.done = true


## The text of a `frame` message: head's fields and the JPEG (base64), without parsing or re-encoding the picture.
static func frame_text(head: Dictionary, b64: String) -> String:
	var h := {"type": "frame"}
	h.merge(head)
	return _with_jpeg(JSON.stringify(h), b64)


## The text of a capture's `response`: result's fields and the JPEG (base64).
static func response_text(id, result: Dictionary, b64: String, t) -> String:
	return '{"type":"response","id":%s,"ok":true,"result":%s,"t":%s}' % [JSON.stringify(id),
		_with_jpeg(JSON.stringify(result), b64), JSON.stringify(t)]


static func _with_jpeg(object_text: String, b64: String) -> String:
	return object_text.substr(0, object_text.length() - 1) + ',"jpeg":"' + b64 + '"}'


func _send_frame(job: Job) -> void:
	if not job.as_text and job.jpeg.size() > DEBUGGER_MAX_BYTES:
		return  # (too big for the debugger connection: dropped, a gap in n)
	sent += 1
	if job.as_text:
		host.send_picture(job.text, false)
	else:
		host.send_media({"kind": "frame", "n": job.n, "t": job.t, "wall": job.wall, "width": job.width, "height": job.height}, job.jpeg)


func _send_still(job: Job) -> void:
	if not job.as_text and job.jpeg.size() > DEBUGGER_MAX_BYTES:
		host.respond_error(job.id, "the still is too big to pass through the editor (%d bytes): ask for a smaller max_dim or quality" % job.jpeg.size(), 413)
		return
	if job.as_text:
		host.send_picture(job.text, true)
	else:
		host.send_media({"kind": "still", "id": job.id, "t": job.t, "width": job.width, "height": job.height}, job.jpeg)
