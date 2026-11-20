# The bridge protocol (protocol 2)

## What this is

One WebSocket on loopback per run. The plugin connects out to the brain (the `cluewren` .NET command);
the brain listens.

Protocol number 2. A brain and a plugin of the same release only (protocol_min/protocol_max in the advert, protocol in
the hello and invite must be 2).

Trust: processes of the same Windows user are trusted; processes of other users and pages in a browser are not.
Harness code (the C# the agent writes, run in a sandboxed child process) sits behind the brain and never speaks this
protocol itself: the brain forwards only some of its ops (see Ops).

## Finding each other: the advert file and the invite file

Folder R = %LOCALAPPDATA%\Cluewren.

### The advert file

R\editors\<pid>.json, written by the plugin, <pid> = decimal plugin process id. UTF-8 JSON, no BOM, snake_case fields:

```json
{
  "pid": 1234, "id": "<32 lowercase hex chars>", "engine": "unity", "engine_version": "<engine version>",
  "plugin_version": "<plugin version>", "protocol_min": 2, "protocol_max": 2,
  "project_dir": "<absolute project folder>", "host": "editor"
}
```

- pid: advertising process. id: made once per process from engine cryptographic random source, kept across reloads
  (a new id after a reload makes the editor ignore the invite of its run).
- engine, engine_version, plugin_version: what it is; engine is lowercased. protocol_min/protocol_max: range it speaks
  (both 2). project_dir: absolute project folder, matching hello. host: whether it is an editor or game process.

Written when the plugin loads in an editor; in a game or batch process only when started with the connect flag.
Written again after a reload, with the same id (Unity withdraws its advert before a script reload and writes it again
after, with the same id). The plugin creates R and R\editors if missing, writes to a temporary name in the same folder
and renames over the file where the engine can.

Deleted by the plugin when the editor quits, the plugin is disabled or the subsystem shuts down. A crash leaves it;
the brain removes it. The advert holds no secret and proves nothing: it only decides who is invited; hello decides
who is admitted. Nothing but these fields goes in it.

### The invite file

R\invites\<pid>.json, <pid> = the editor's (plugin process's) pid. Written by the brain, read by the plugin with that
pid; the plugin reads nothing else in R\invites. snake_case JSON:

```json
{
  "url": "ws://127.0.0.1:<port>/v1/bridge", "secret": "<base64 of 32 random bytes>", "pid": 1234,
  "born": <ms>, "started": "<ISO 8601 time>", "protocol": 2, "editor_id": "<the advert id it answers>"
}
```

- url: where the brain listens (127.0.0.1 on an OS-picked port, path /v1/bridge).
  secret: shared key of the handshake, 32 random bytes, base64.
- pid and born: the brain process; born is process start time in Unix milliseconds, so pid plus born name one process
  where a pid alone names whichever process currently holds it. The brain treats an invite as alive only when born is
  not 0 and equals the start time of the process currently holding that pid.
- started: when written (UTC). protocol: 2. editor_id: advert id answered; another editor with that pid ignores it.

The brain creates R and R\invites with an access list giving only the current user full control (inheritance off);
the invite file is created with the same owner-only access list, written to `<invite>.<brain pid>.tmp` and moved to
the final name.

Lock: every look at an invite, removal of a dead brain's invite, publishing and withdrawing happens while holding
`<invite path>.lock` (R\invites\<pid>.json.lock), opened with no sharing and deleted on close. It excludes every brain
that can reach the invite, in any logon session of the user. A brain waits up to 5 seconds for it, then fails with
"That editor is being invited by another agent session".

Publishing and withdrawal: if the existing file is a different invite and its brain is alive, the brain refuses ("That
editor is already in a run of another agent session (cluewren process <pid>)"); otherwise it deletes it and writes its
own. When the brain writes an invite that replaces one still there, it gives the new file a modified time in a later
whole second, so a plugin that reads file times in whole seconds tells the two apart by that time alone. At the end of
a run the brain deletes the invite, but only if the file still holds its own invite.

### Plugin invite validation

While the plugin wants a connection and has none, it looks for its invite about once a second and validates it again
only when it changed. Unity and Unreal tell an invite from the one before by its modification time (Unreal reads the
100 ns write time, as the engine's own file time counts whole seconds). In Godot, a key of modified time and size
decides when the owner check runs, a file larger than 64 KiB is refused unread, and nothing is read to tell invites
apart (the brain's later-second guarantee makes the time enough). When the file is there and is not the invite last
refused (Godot remembers the refused invite across an addon reload), it validates in order and stops at the first failure:

1. The folder R\invites and the file are owned by the current user and are not reparse points.
2. It parses as a JSON object.
3. editor_id equals this process's advert id. If not: ignored silently, no status change; look again on file change.
4. protocol is 2. If not, status: "The cluewren command and this plugin are of different releases: install the same
   release of both".
5. secret decodes from base64 to at least 32 bytes.
6. A process with the invite's pid is running. If not, status "The run this editor was invited to has ended" (quiet:
   no error log, no popup); look again when the file changes.
7. url is ws://127.0.0.1:<port>/... or ws://[::1]:<port>/..., with no userinfo.

Then it connects and does the handshake. The connect flag is the one exception where a process prepares itself before
an invite passes validation, because the flag is the user's own request:
- Unity batch-mode editor: writes the advert, writes the project export when missing or stale, opens the scene (the map
  flag's or the build's first), and enters play mode unless -CluewrenEditMode.
- Unity built game: makes its client, writes the advert, loads the map flag's scene.
- Godot game: sets up its log capture and UI tracking, sizes a headless viewport and caps the frame rate, writes the
  advert, changes to the map flag's scene. Godot acts on --cluewren-map only together with --cluewren-connect.
- Unreal: writes the advert, attaches its log capture, opens the map flag's level once the first world plays.
Each opens its socket only after its invite has passed its checks, and makes no input devices before a verified welcome;
each exits with code 1 when no welcome comes within the connect timeout (default 120 s). What waits for a verified
welcome keeps waiting for it. After a refusal the plugin does not connect again until the invite file changes. After a
normal end of a run the brain deletes the invite and the plugin returns to looking. The editor's Connect/Disconnect
control switches whether it wants a connection.

### Brain advert polling

The brain reads adverts (Rendezvous.Editors, Rendezvous.Choose; polled every 500 ms while waiting):
- Reads at most 256 *.json files in R\editors; skips files whose name is not a decimal pid.
- Deletes adverts whose process is gone, or whose file was written more than 5 seconds before the start of the process
  currently holding its pid (stale: pid belongs to another program). An advert whose process the brain is not allowed
  to ask about (an editor run as administrator) is left alone, not deleted.
- Skips files larger than 64 KiB. One that is half written or being replaced (read error, bad JSON) is skipped and
  read again on the next look. Valid adverts require non-empty engine, project_dir and id, and pid matching the
  file name; engine is lowercased.
- Project match: project_dir must be a full local path (drive letter, colon, slash) and name the same folder as the
  run's project by file identity (volume serial number plus file id of opened folder, not by path text). The folder is
  opened and the handle kept, binding the run to that identity.
- No match: brain keeps waiting, reporting "No editor with the Cluewren plugin is open" or "No open editor has that
  project. Open: <engine> <project_dir>; ...".
- Protocol range: matching adverts must have protocol_min <= 2 <= protocol_max. If none, the run fails with a message
  telling the user to install matching releases.
- Several editors: if remaining ones belong to more than one project folder, the run fails and asks for `project` to
  name the one to play. For the same project, the one whose advert was written last is chosen.
- Already invited: if its invite file holds an invite from another alive brain, the run fails with "The editor of
  <project_dir> is already in a run of another agent session (cluewren process <pid>)".
- The brain retries deleting an invite file that an editor has open for a moment (up to 20 tries 10 ms apart).
- Once chosen, the brain's server accepts only that project's folder (by identity) in a hello.

## The handshake

Sequence: plugin sends hello; brain answers welcome (or a refusal, see Refusals); plugin verifies the welcome's proof
and sends proof; brain verifies it. Then the link is up.

hello (plugin to brain):

```json
{
  "type": "hello", "protocol": 2, "plugin_version": "<version>", "engine": "unity", "engine_version": "<version>",
  "project": "<project name>", "host": "editor", "features": ["timed_hold", "pointer", "levels", "media"],
  "nonce": "<base64 of 32 random bytes>", "project_dir": "<absolute project folder>", "mac": "<base64>",
  "resume": "<session id>", "received": 17
}
```

- resume and received only when the plugin kept a session (see Resume). features: see Features.
- engine is lowercased by the brain (missing engine counts as "unity"). nonce must decode to exactly 32 bytes.
- mac = HMAC-SHA256(secret, input) byte for byte (Handshake.ProveHello): ASCII "hello", 0x00, 32 nonce bytes, 0x00,
  UTF-8 project_dir.

welcome (brain to plugin):

```json
{
  "type": "welcome", "role": "service", "session": "local-<32 hex chars>", "brain_version": "<version>",
  "nonce": "<base64 of 32 random bytes>", "proof": "<base64>"
}
```

- role: always "service". session: "local-" + 32 hex chars, or the resumed session's id.
- nonce: brain's nonce, 32 random bytes. proof: brain's proof.

proof (plugin to brain):

```json
{ "type": "proof", "proof": "<base64>" }
```

Proofs: HMAC-SHA256(secret, input) where input is, byte for byte (Handshake.Prove):
the ASCII bytes of the role label, one 0x00 byte, the 32 plugin nonce bytes (from hello), the 32 brain nonce bytes
(from welcome), with no separator between the two nonces. Role label "brain-proof" for welcome, "plugin-proof" for
proof. Both sides compare MACs in constant time.

Limits:
- Brain listens on 127.0.0.1 only, path /v1/bridge. Non-loopback addresses or requests with an Origin header receive
  HTTP 403. Non-WebSocket requests receive HTTP 400. More than 8 handshakes in progress at once: HTTP 503.
- hello is at most 64 KiB (65536 bytes); larger is refused with code "hello".
- Handshake (hello to proof) must finish within 10 seconds or the connection is dropped.
- No plugin's WebSocket client follows an HTTP redirect. Unreal's socket also sends no credentials or cookies
  (redirects, authentication and cookies are disabled for the request).

Brain checks, in order (BridgeServer.HandleAsync and AuthenticateAsync):
1. No Origin header and a loopback peer, else HTTP 403.
2. A WebSocket upgrade, else HTTP 400.
3. At most 8 handshakes in progress, else HTTP 503.
4. hello fits in 64 KiB, else refusal "hello".
5. JSON object with type "hello" and 32-byte nonce, else refusal "hello".
6. protocol is 2, else refusal "protocol".
7. project_dir is a string, mac is base64 and verifies, else refusal "not_authenticated". Until mac is checked the
   peer may be another user's process (cannot read invite): nothing sent touches files or messages before this.
8. project_dir is a full local path, else refusal "project".
9. project_dir is the run's project folder by file identity, else refusal "project".
10. If resume names a suspended session of this brain: hello's engine and project folder (by identity) must match that
    session, else refusal "resume". A resume naming no suspended session is a new session.
11. Send welcome; receive plugin's proof and verify it, else refusal "proof".
12. For resume, check replay window (see Resume); a gap ends the link with a plain close.

## Refusals

Before the welcome a refusal is one text frame:

```json
{ "type": "error", "code": "<code>", "message": "<text>" }
```

followed by a WebSocket close with status 1008 (policy violation) whose reason is the same code. A WebSocket close
with status 1008 alone before the welcome, without the error frame, is read as the refusal its reason names, in all
three plugins. The reason carries the code because Godot's socket can drop a text frame that arrives together with the
close; all three plugins read the reason from the close alone or from the frame.

The plugin shows a text of its own for the code, never the brain's message: the brain has not proved itself yet.
Shown as "Refused: <text>" in the plugin status (window, dock or log). An unknown code is shown only if it matches
[a-z0-9_]{1,32}, otherwise a fixed text.

Codes and the text a plugin shows:

```text
protocol           The cluewren command and this plugin are of different releases: install the same release of both
not_authenticated  The brain did not accept this editor's proof of the invite
project            The run is for another project than this editor's
resume             The session this editor tried to resume belongs to another project or engine
hello, proof, any other code
                   The brain refused the connection (<code>)
```

A first frame that is neither a welcome whose proof verifies nor such an error frame: the plugin treats the peer
as not this machine's brain and does not go on.

Every refusal means the plugin does not retry until the invite file changes. A process started with the connect flag
exits with code 1.

## After the welcome: frame types in each direction

All frames are WebSocket text frames holding one JSON object each. The brain reads frames of at most 16 MiB; a larger
one drops the connection (see Resume).

Brain to plugin:
- request: `{ "type": "request", "id": 1, "op": "<op>", "args": {} }`. args is always an object.
- status: `{ "type": "status", "text": "<text>" }`: a line for the plugin to show.
- run_started: `{ "type": "run_started" }`: a run began.
- result: `{ "type": "result" }`: the run ended. Every plugin keeps a run_active flag from these two.
- error: `{ "type": "error", "code": "<code>", "message": "<text>" }`; code is optional. Handled after welcome too.

Plugin to brain:
- response, success: `{ "type": "response", "id": 1, "ok": true, "result": {}, "t": 12.5 }`
- response, failure: `{ "type": "response", "id": 1, "ok": false, "status": 403, "error": "<text>", "t": 12.5 }`
  (failure without status counts as 400).
- t on any frame is game time in seconds; the brain keeps the latest one.
- events: `{ "type": "events", "events": [{ "kind": "log" }], "t": 12.5 }`: kinds session, log, finding, heartbeat.
- frame (one picture of a recording, record op):
```json
{ "type": "frame", "n": 1, "t": 12.5, "wall": <wall time>, "width": 640, "height": 360, "jpeg": "<base64 JPEG>" }
```

A request response waits 30 seconds by default in the brain; then it reports that the game didn't answer in time.
Frames other than responses and events (e.g. frame) go to a queue of at most 32 that drops the oldest.

## Resume

Request ids count from 1 per session, in the order the brain sends them.

A connection ending without a normal close (status 1000) suspends the session for 90 seconds (Unity closes with 1012
when reloading scripts on entering play mode and comes back with the same session; a reload can also tear down the
socket before close gets out). The Unity plugin asks to resume only across its own script reload; any other loss of
the socket ends the session, and the reconnect is a new session. A normal close ends the session. If the plugin does
not return within 90 seconds the session ends.

hello carries resume = kept session id and received = highest request id handled in that session.

If welcome's session equals the one asked for, it is a resume: plugin sends held responses and keeps pending work. The
brain re-sends every request with id greater than received, then what was queued after them; requests the plugin has
are not sent twice. Responses still queued or held and requests still being worked on (a move, a watch, a capture) at
the moment of a reload are lost with the script domain; the resuming hello counts those requests as received, so the
brain does not send them again and they end by its timeout (30 s by default); only the waits of a session start, stop
or restart are carried across the reload and answered after it.

If it does not (or none was asked for), it is a new session: plugin resets received to 0, drops held responses without
sending, and drops pending work. A new session drops what the one before left unfinished, in every plugin: pending
work and captures in flight (Unity: by a session serial; Godot: stills are dropped, every input the session pressed
(taps and holds) is let go with the event its press was made with, so a game that follows input events sees
the release, and the editor forwards only answers to requests of this session and detaches the game; Unreal: by
a session serial on session ops and captures). A Unity editor's waits for play mode are not advanced while it is
not connected and are dropped when their session ends (a new session, a normal close, a refusal, and also an
abnormal close). In an ordinary Unreal editor (no connect flag) the log capture is attached only after the
brain's proof verifies.

What the brain keeps for replay: frames sent on the socket, at most 1024 frames and 32 MiB; older ones are dropped.
If resume's received is below the id of a dropped request (plugin missed a request that is not kept in the buffer),
the brain ends the session: after welcome and proof it closes with a plain normal close (status 1000, reason
"resume refused"), not an error frame, and the plugin ends as for any lost brain.

What the brain queues: frames a harness child had it send (requests and statuses from harness code) are taken only
while less than 16 MiB waits to be sent; past that they are refused. The brain's own frames (releasing input, pausing,
ending a run) are always taken.

## Ops

Operations on the bridge. Operations marked `(harness)` may be sent by harness code in the sandboxed child process;
unmarked operations are sent by the brain only.

Harness code may send `session` only with action "restart" and with no map, or a map that is a string not containing
".cluewren" (case-insensitive; a map must be one of the project's, see Folders in the project). A harness request for
any other op gets 403 with error "<op> is refused: harness code may not send it" from the brain without reaching the
plugin. Brain-only: session (other actions), timestep, detectors, record, capture.

```text
key        (harness)  {key, mode: tap|hold|release|release_all, value?, rate?, lease?, seconds?} -> {}
input      (harness)  {action, mode: hold|release|tap, value?} -> {}
move       (harness)  {direction, scale?, seconds?} -> {moved}
pointer    (harness)  {screen_x, screen_y} or {world} or {object} -> {screen_x, screen_y, on_screen}
view       (harness)  {max_distance?, max_count?, only?, sonar?, sonar_reach?, sonar_turns?}
                      -> {visible[], camera, body, ...}
watch      (harness)  {objects[], seconds?, ...} -> {changes[]}
rc         (harness)  {verb: call|property|describe, body}
                      -> the call's return values, the property values, or the description
context    (harness)  {} -> {world, character, controller, engine, engine_version, project_dir, session_running,
                      paused, game_time, controls[], ...}
session    (harness: restart only)
                      {action: status|start|stop|restart, map?} -> {running, export_state?, ...}
levels     (harness)  {} -> {levels[], open, playing}
pause      (harness)  {paused} -> {paused}
timestep              {fixed?, fps?} -> {}
ping       (harness)  {} -> {game_time, wall_time}
screen     (harness)  {} -> {texts{}}
ui         (harness)  {} -> {elements[]}
ui_act     (harness)  {id, action: click|right_click|double_click|set|drag, to?, option?, value?, text?} -> {}
detectors             {enabled?, thresholds...} -> {}
record                {action: start|stop, fps?, max_dim?, quality?} -> {recording, width, height} or {frames}
capture               {max_dim?, quality?} -> {width, height, t, jpeg}
find       (harness)  {class?, name?, limit?, things?} -> {objects[]}
transforms (harness)  {objects[]} -> {objects{path: {location, rotation, velocity, center, extent} or null}}
pose       (harness)  {} -> the player's position, eye, rotation, velocity, on-ground state
movement   (harness)  {} -> the character's movement numbers (walk speed, jump height, gravity, capsule, ...)
```

A successful session start with an explicit map means that map is playing. A host that is already playing a
different map may refuse the start; the caller can stop and start again with the requested map.

Unreal reports `export_state` on session start and status. The brain polls while the state is `pending`. It uses the
export when the editor reports `ready` and the summary exists. On `unavailable` or timeout it proceeds without the
export. A standalone Unreal game reports `unavailable`.

Failure statuses:

```text
400  unknown op
403  a refusal (rc's refusal categories, a map or path under .cluewren), error text "<what> is refused: <why>"
404  something that is not there
409  no picture to record or capture, or a conflicting play session
500  a failure inside the plugin
```

## Features

The hello's features array lists strings. The brain acts on two of them; the other two only say what the plugin has:

```text
timed_hold  key holds accept `seconds` (the plugin lets go in-game); without it the kit times the hold itself
media       record and capture are available; without it the brain films nothing
pointer     the pointer op is available (Unity and Unreal send it; the brain does not read it)
levels      the levels op is available (the brain does not read it: it sends the op and takes a failure as none)
```

## Folders in the project and command-line flags

Everything Cluewren keeps in a game project is under `<project>\.cluewren`, for all three engines:

```text
.cluewren\memory\     the agent's notes.md and lib.cs (brain and agent write)
.cluewren\runs\       one folder per run (brain, agent, and harness code under runs\<id>\work)
.cluewren\export\     the project as text, written by the plugin; summary.txt is written last
.cluewren\.gitignore  contains "*" (whoever creates .cluewren first writes it if missing)
```

Plugins refuse, with 403, rc targets, rc string arguments and values, and a restart's map that name the data folder (rc
refusal category 9). Every string the plugin checks goes through the rule whether or not it holds a separator, a ':' or
a '%'; ".cluewren" alone and "%2Ecluewren%2Fruns%2Fx" are refused. A string is refused when, read as a path, one of its
segments names the data folder: split at every '/' and '\', with a leading drive spec taken off first (refusing
drive-relative paths such as "C:.cluewren\x"; Unreal takes it off any segment), cut at the first ':' (NTFS stream or
attribute notation), and with trailing dots and spaces trimmed, it equals ".cluewren" (case-insensitive) or has the
shape of an 8.3 short name Windows could give the folder: no '.', at most 8 characters, begins with the first two
letters of the folder's name without its leading dot ("CL" for .cluewren; Godot also "GO" for .godot), ignoring case,
and ends with '~' and one or more digits. That covers what Windows generates (CLUEWR~1 to ~4, then two letters, four
hexadecimal digits and ~N) without knowing which one it gave; it refuses a few other names of that shape (CLIENT~1),
which is accepted. Godot applies the same rule to .godot as well as .cluewren. The check is made on the string as given
and, if it holds '%', percent-decoded once; an engine path is turned into text from the engine's internal tables first
(Godot uid://, Unreal package names) and the rule checked on both. No path is opened, listed or resolved, and no root
is compared. Links into the folder from outside (junctions, symbolic links, subst drives, hard links) are not followed.
A session start or restart with a map under .cluewren is answered 403 before anything changes. Godot also refuses
res://.godot/, and rc refuses the addon's own scripts by path text under every spelling and a Script object by the source
it already holds in memory, reading no file to judge a string argument (with "<what> is refused: the Cluewren addon's
own scripts are out of rc's reach").

A session's map is harness-chosen text too: a map is a level's name or a path inside the project as the engine names
it; anything else is answered 404 without any file system or network call. In Unity, a ':' anywhere, a leading
separator, or a ".", ".." or empty segment makes a map none of the project's; the command-line map (-CluewrenMap) is
the user's own and is not restricted. In Godot, a map is a res:// or uid:// path, or a scene's file name without '/',
'\', or ':'; a res:// map is a scene only when every segment after res:// is a plain name (no "." or "..", no empty
segment, no '\' or ':'), and uid:// is turned into its res:// path through the engine's table first; an unknown map is
answered 404. In Unreal, an allow-list is used: a map is taken only when its text is made of letters, digits, '_', '-',
'/' and at most one '.' (an object part, or .umap on a short name) and it is found among the project's levels in the
engine's asset registry (levels under /Game and the project's plugins); the engine is given the package name found
there, never the string as sent (keeping out URL options such as "Arena?listen" or hosts to travel to); an unknown
level answers 404 in the editor as in a built game; -CluewrenMap on a built game's command line goes through the same
check.

Command-line flags that make a game or batch process advertise and connect:

```text
Unity, Unreal   -CluewrenConnect, -CluewrenConnectTimeout=<s>, -CluewrenMap=<level>
Unity           -CluewrenEditMode (with -CluewrenConnect: stay in edit mode, do not enter play mode)
Godot           --cluewren-connect, --cluewren-connect-timeout=<s>, --cluewren-map=<level> (after "--")
```

The URL never comes from a flag: only from the invite.
