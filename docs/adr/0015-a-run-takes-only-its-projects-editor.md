# 0015 A run takes only an editor whose project the agent is working in

Status: accepted
Date: 2026-11-12
Amends: 0008

## Context

ADR 0008 left open that the brain welcomed every authenticated editor and played the first, whatever its project.
Editors connect by themselves to any valid brain.json, and connecting has effects before an op is sent: Unreal exports
assets on welcome, Godot turns busy and attaches, Unity added virtual input devices before it read the discovery file.
The owner asked for no security difference between the three engines.

Trust is as before: processes of the same Windows user are trusted (they can read keys.env and brain.json and control
the editor). Not trusted: the harness child, and other users' processes, which reach the loopback port but cannot read
brain.json.

## Decision

- The run's folder. `cluewren mcp` records its working folder at start. `start_run` takes an optional `project`, a full
  local path (`X:\...`, or `/...` on macOS and Linux) of an existing folder; `cluewren run` takes `--project <folder>` and otherwise uses its current
  folder, and waits without a deadline as before. With `project` the match is `exact` (the editor's project is that
  folder); without it `related` (the editor's project is the folder, inside it, or contains it).
- brain.json gains `folders`, one or two entries: the folder as given (separators unified, trailing separator removed, a
  drive root keeps it; not expanded, so an 8.3 or subst form stays as given), then its canonical final path when that
  differs. Never with a `\\?\` prefix. It also gains `match`: `related` or `exact`.
- Every plugin filters in the function that already returns a verdict for brain.json, before any socket or connect
  preparation: `folders` absent or empty is no filter (a 0.1.0 brain); a `match` other than the two is "for another
  project"; otherwise the editor's project folder must match an entry. The comparison is textual: absolute path,
  separators unified, trailing separator removed (a drive root keeps it), containment on separator boundaries; on
  Windows case is folded for ASCII letters only, because each engine folds other letters differently and a mismatch there
  fails closed (`project` recovers it). On macOS and Linux a path is rooted at `/` and case is kept. The verdict text is
  the same on the three engines: `For another project: the run is for "F1" or "F2" (match "m"), this project is "E"`.
  Unity sets up its virtual input devices and editor input passthrough only once the welcome is verified, as Godot and
  Unreal act at that point, and the Godot editor attaches to its running game only while connected.
- The hello is authenticated. It gains `project_dir` (absolute) and `mac`, base64 like `proof`:
  HMAC-SHA256(secret, ASCII "hello" 0x00 plugin_nonce 0x00 UTF-8 of project_dir exactly as sent). The brain checks, in
  this order and before any file system call: the hello is at most 64 KiB; the mac, in constant time; project_dir is a
  rooted local drive path, `X:\` or `X:/` (Godot and Unreal send forward slashes), with no UNC, device, `\\?\`, POSIX or
  relative form. On macOS and Linux the same check is that project_dir starts with `/`, and brain.json's `folders` are
  in that platform's form. A hello without `mac` is refused with "update the
  Cluewren plugin in this project" and a wrong one with "not authenticated"; neither names a folder. The welcome and
  proof exchange is unchanged.
- The brain then compares by file identity on Windows: volume serial and 128-bit file id from
  GetFileInformationByHandleEx(FileIdInfo) on handles opened with FILE_FLAG_BACKUP_SEMANTICS; containment walks the
  parents of each folder's final path looking for the other's identity. A folder that cannot be opened, or is not a
  directory, does not match. On Linux and macOS (unsupported) the comparison is ordinal on full paths after resolving the
  final link. No match is refused with a text naming the run's folder, since the peer is authenticated by then.
- A refusal by the brain uses the existing path, an error frame and a close, so each engine shows it as it shows a
  refused brain today. A refused editor is not attached and runs none of its on-welcome effects.
- Binding. The identity (volume serial and file id) is taken once: of the run's folder when the run starts, of the
  editor's folder when its hello is verified. A welcomed link carries its folder and that identity, and every later
  look compares a newly opened folder with the stored identity, never with a path opened again, so a junction re-pointed
  meanwhile does not carry a session or a run to another folder. A hello with `resume` must carry the project and the
  engine of the session it resumes, or it is refused.
- Each stored identity keeps its folder's handle open, because Windows may give a deleted folder's file id to another
  folder once no handle is open. The handle is opened as for the comparison (read attributes, backup semantics,
  sharing read, write and delete), so the user can still delete the folder or rename it; renaming a folder above it
  is refused while the handle is open. The run's folder is held from the start of the run until its bridge closes; the
  bridge owns it from the moment it is asked to start, so a bridge that fails to start or to stop still lets it go. An
  editor's folder is held from its verified hello until its link ends: a normal close, the resume window running out
  or the bridge closing. A hello refused after its folder was opened, or one whose hand-over to the run doesn't finish,
  lets it go at once.
- The game is started and its project confirmed before anything else. The game's `context.project_dir` must be the
  verified folder before the game starts and again once it runs (Godot answers from the editor until then), and the
  verified path must still lead to it. Only then is the memory folder made, recording started and run_started sent.
  start_run and `cluewren run` share this step, and `cluewren run` takes it before any harness starts. start_run
  starts its `level`. `cluewren run` starts its harness's StartMap, read in the sandbox child so that the harness is
  never made in the brain's process. That child's session reaches no game: every request, post, send or status it
  makes is refused, and a harness that tries is reported as not loading. A harness that doesn't load stops the run
  before the game starts. A later restart,
  with another map too, happens in the game the verified editor started, from that editor's project, and is not
  checked again.
- Limits: at most 8 handshakes in progress, further connections are answered 503 before the upgrade. A hello of exactly
  64 KiB is taken however it is fragmented. Every explicit refusal at the handshake is kept for the agent, capped at 8
  distinct entries and a folder at 260 characters; a refusal before the mac is verified (too large, not a hello of
  protocol 1, no mac, a wrong mac) is kept only as a fixed text, so nothing such a peer sends reaches the agent.
- When no editor connects, start_run's error names the run's folder, each refusal at the handshake, and says that an
  editor of another project does not connect and shows the reason in its own Cluewren window. `cluewren run` prints one
  line for each new refusal.
- Unchanged: every editor whose project matches is welcomed and the first is played (`project` makes it exact); one
  brain.json per user. Protocol stays 1. Brain 0.1.1 needs plugin 0.1.1; plugin 0.1.1 with a 0.1.0 brain behaves as
  0.1.0 (no `folders`, the extra hello fields are ignored).

Textual vectors, asserted by each engine's tests (mode; announced folders; editor project; expected):

```
 1 related; C:\Games\Foo; C:\Games\Foo; match
 2 related; C:\Games\Foo; C:\Games\Foo\; match
 3 related; C:\Games\Foo; c:/games/foo; match
 4 related; C:\Games; C:\Games\Foo; match
 5 related; C:\Games\Foo\Assets\Scripts; C:\Games\Foo; match
 6 related; C:\Games\Foo; C:\Games\FooBar; no
 7 related; C:\Games\FooBar; C:\Games\Foo; no
 8 related; C:\; C:\Games\Foo; match
 9 related; C:\Games\Foo; D:\Games\Foo; no
10 exact; C:\Games; C:\Games\Foo; no
11 exact; C:\Games\Foo; C:\Games\Foo\; match
12 exact; C:\Games\Foo\Assets; C:\Games\Foo; no
13 related; C:\Link\Foo and C:\Games\Foo; C:\Games\Foo; match
14 related; C:\Games\Foo; S:\Foo (a subst alias of it); no (documented; `project` recovers)
15 related; C:\Games\LongName; C:\Games\LONGNA~1; no (documented; `project` recovers)
16 exact; S:\Foo and C:\Games\Foo; S:\Foo; match
17 related; (folders absent or empty); C:\Games\Foo; match (no filter)
18 related; C:\Games\Foo; (empty or relative path); no
19 unknown mode "nearby"; C:\Games\Foo; C:\Games\Foo; no
```

The brain's tests assert what it announces for these (trailing separator, slashes, drive root, junction, 8.3 name) and
the identity comparison behind them.

## Consequences

- An editor that opened its project through an alias the brain does not announce (a second junction, a subst drive, an
  8.3 name) does not match textually and does not connect; its Cluewren window shows both folders. Passing `project`
  with the path the editor shows makes it match, because the folder as given is announced.
- A brain-side refusal is rare by construction, since the plugin filters first: a 0.1.0 plugin, or a textual match that
  identity denies (a case-sensitive sibling folder, a folder that cannot be opened).
- `cluewren mcp` started in a broad folder (a home folder, a drive root) takes any editor below it; `project` narrows it.
- Another user's process can still hold the 8 handshake slots for their 10 s deadline, delaying an editor's connect; it
  learns nothing and touches no file.
- A process started with the connect flag on its command line prepares itself before brain.json is read: a Unity batch
  start, a Godot game's logger and UI tracking, Unreal's log device. That is the user's own command, and this decision
  does not cover it.
- An editor whose own project is at a drive root (`C:\`) is supported, and each engine's tests cover it.
- Platforms: the brain and the Unity plugin keep working on macOS and Linux as before, unsupported, with the comparison
  above tested by review only. The Godot plugin refuses every platform but Windows (it can't check brain.json's owner
  there, lib/discovery.gd) and the Unreal plugin is Win64 only (ADR 0010); this decision does not change that.
- Tests: tests/Cluewren.Tests/ProjectBindingTests.cs for the brain over real loopback sockets; each engine's plugin tests
  for the vector table and for a "for another project" verdict that leaves no socket and, on Unity, no input change.

## Alternatives rejected

- Per-project discovery files or announcement generations: larger than this release; refused editors are no longer held,
  and a refusal by the brain is the rare path.
- Canonicalizing the editor's own path inside each plugin: GDScript has no API for it, so the three engines could not do
  the same; the textual rule plus `project` is the same everywhere.
- Comparing folders by text in the brain too: a case-sensitive sibling or a junction would be judged wrongly, and the
  brain can afford the file system once the peer is authenticated.
