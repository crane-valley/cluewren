# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-11-16

Cluewren 0.2.0 is a protocol 2 redesign with per-editor adverts and invites, a unified `.cluewren` project folder, tightened harness rights, the `stop` tool and call cancellation, cross-process frame access, and updated decision model kit names.

### Security

- Editor rendezvous and refusals ([ADR 0017](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0017-editors-advertise-and-the-brain-invites.md)): Open editors write advert files (`%LOCALAPPDATA%\Cluewren\editors\<pid>.json`) with no secrets. The brain selects an editor matching the run's project by disk identity, binds the run to that folder, and writes a private invite file (`%LOCALAPPDATA%\Cluewren\invites\<editor pid>.json`) readable only by the current user (retrying invite deletion if open, and leaving admin adverts alone). The editor connects out to loopback with a per-run secret handshake; processes with a connect flag prepare themselves before validation (scene, capture, advert) but open no socket or input devices before a verified welcome. A close with status 1008 alone is read as the refusal in all three plugins (unknown codes shown only if matching `[a-z0-9_]{1,32}`). In Godot, invites are told apart by modified time and size without being read; the brain gives a replacing invite a later second. Unity resumes only across its own script reload; any other lost socket ends the session. In all plugins, a new session drops unfinished work and captures in flight.
- Harness rights and evidence ([ADR 0018](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0018-what-harness-code-may-touch.md)): In the AppContainer, the harness child can read its run folder and load what is there, and write only under `.cluewren\runs\<id>\work`. The container is kept once it has worked (cannot fall back to a plain child after a contained child has answered), and `carry.json` left in work is read only after the first answer and ignored if invalid. Which pictures belong to an episode is decided by the brain (from level restart or call start to return; child marks cannot alter the boundary). Decisions count toward the report's gate only when the decision model's answer gives an offered option probability above zero; the gate uses the smaller of the brain's count and the harness's count.
- Op forwarding filter ([ADR 0018](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0018-what-harness-code-may-touch.md)): The brain forwards to the game only allowed operations (rc, find, transforms, pose, movement, view, watch, screen, ui, ui_act, key, input, move, pointer, pause, context, levels, ping, and session with action restart). Recording, capture, detectors, timestep, and session start, stop, and status are the brain's own. Disallowed requests are answered with status 403 ("<op> is refused: harness code may not send it") while the session continues; disallowed messages expecting no reply are dropped.
- Stopping and call cancellation ([ADR 0018](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0018-what-harness-code-may-touch.md), [ADR 0021](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0021-a-second-process-reaches-frames-and-a-call-can-be-stopped.md)): The `stop` tool and client cancellation act on calls that play (`probe`, `check_harness`, `run_harness`). Calls still going that cannot be stopped receive "<tool> is still going and can't be stopped: call wait."; calls already ended receive "<tool> has ended: call wait for its result."; stops arriving during compile or startup answer "Stopped before it began."; cancelling `wait` stops the play it waits for. Stopping does not rest on the child: the brain sets a 15 s limit before asking the child on another thread, then ends the session, releases keys, and pauses the game.
- Remote call folder and addon protection ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md), [ADR 0019](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0019-one-folder-in-the-project.md), [ADR 0023](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0023-the-data-folder-is-refused-by-its-name.md)): Remote calls (rc) and restart levels are refused with status 403 on all engines when a path segment names the data folder (".cluewren", stream notation, or a name shaped like its 8.3 short name, such as CLUEWR~1); every string is checked without pre-filtering, nothing the string names is opened or resolved, and links into the folder from outside are left to the user. Harness-chosen maps must be one of the project's levels (Unreal: an allow-list checked against the project's levels in the asset registry, the engine given the name found there; anything else, such as options like "?listen", is answered 404 without file or network access). Godot also refuses `res://.godot/` and its own addon scripts by path text under every spelling and scripts by source in memory.
- Network and endpoint validation ([ADR 0020](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0020-decisions-behind-names-of-their-own.md)): `TYPESAFE_BASE_URL` must be an https address of a host alone (no user, password, path, query or fragment); the decision client follows no redirects, so the key goes only to the configured address. No plugin's WebSocket client follows an HTTP redirect; Unreal's socket also disables credentials and cookies.
- Systems other than Windows: The Linux bubblewrap sandbox has been removed. On non-Windows systems, harness code runs in a plain child process with a note under `CLUEWREN_SANDBOX=auto` (or unset), and is refused under `CLUEWREN_SANDBOX=required`.

### Changed

- Compatibility and upgrade: Protocol 2 redesign with no backward compatibility with 0.1.x in either direction. The `cluewren` command and engine plugins must be of the same release (0.2.0 with 0.2.0); plugins of a different release are refused.
- Advert and invite rendezvous ([ADR 0017](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0017-editors-advertise-and-the-brain-invites.md)): Replaced `brain.json` with per-editor adverts and per-run invites. Multiple editors can remain open at once, and concurrent runs on different projects can execute simultaneously. An editor already in a run of another agent session is refused with a message that says so. Folders are matched by disk identity (volume serial and file id), allowing junctions, subst drives, and 8.3 aliases to match.
- Unified project folder ([ADR 0019](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0019-one-folder-in-the-project.md)): All three engines use `<project>\.cluewren` containing `memory\`, `runs\`, `export\`, and a `.gitignore` holding `*`. Replaces `UserSettings/Cluewren`, `Library/CluewrenCodex`, `.godot/cluewren`, `.godot/cluewren_codex`, `user://cluewren_codex`, `Saved/Cluewren`, `Saved/CluewrenCodex`, and `Saved/Logs/CluewrenCodexExport.log`.
- Stop tool and call cancellation: Added the `stop` MCP tool: acts on probe, check_harness, and run_harness, releases keys, and pauses the game; wait then gives what the call produced until then. For another call still going, stop answers "<tool> is still going and can't be stopped: call wait."; for one already ended, "<tool> has ended: call wait for its result."; stops arriving during compilation or child startup answer "Stopped before it began." Client call cancellation (notifications/cancelled), including cancelling wait, stops active play the same way ([ADR 0021](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0021-a-second-process-reaches-frames-and-a-call-can-be-stopped.md)).
- Frames from a second process ([ADR 0021](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0021-a-second-process-reaches-frames-and-a-call-can-be-stopped.md)): When an agent's subagent starts its own `cluewren mcp` in the same working folder (as Codex does for the frame describer), its `frames` requests are answered by the process holding the run over a user-only named pipe named after the folder's identity on disk. Frames remain in memory and are never written to disk.
- Decision model configuration ([ADR 0020](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0020-decisions-behind-names-of-their-own.md)): Added `TYPESAFE_MODEL` (default `jev-1.13.0`) and `TYPESAFE_BASE_URL` (default `https://api.typesafe.ai`; must be an https address of a host alone: no user, password, path, query, or fragment), configurable via environment variables or `%LOCALAPPDATA%\Cluewren\keys.env`.
- Harness kit names ([ADR 0020](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0020-decisions-behind-names-of-their-own.md)): Renamed public kit types to `Decision`, `DecisionRequest`, `YesNo`, and `IDecider` (from `JevDecision`, `JevRequest`, and `Noul`). The kit version is recorded in `.cluewren\memory\kit.txt`.
- Engine plugin flags and autoload: Connect command-line flags renamed to `-CluewrenConnect`, `-CluewrenConnectTimeout=<s>`, `-CluewrenMap=<level>` (Unity, Unreal), `-CluewrenEditMode` (Unity), and `--cluewren-connect`, `--cluewren-connect-timeout=<s>`, `--cluewren-map=<level>` (Godot), renamed from `-PlaytestConnect` and `--playtest-connect`. The brain's address never comes from a flag. Godot autoload script renamed from `playtest.gd` to `addons/cluewren/game.gd`.
- Engine builds ([ADR 0022](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0022-a-build-has-the-bridge-only-when-asked.md)): Unity player builds include the plugin runtime only when the project sets the scripting define `CLUEWREN_PLAYTEST`, Development build or not (a Development build without it no longer carries the bridge); Unreal Shipping builds exclude the runtime module, Development and Test builds carry it; Godot exports carry the addon and a release export does nothing with it; a built game with the bridge connects only when started with the connect flag.
- Refusal reporting: Plugins show a fixed text of their own for each refusal code the brain sends before the handshake completes ("Refused: <text>"), so a release mismatch or another project's run reaches the user, instead of one generic text.

### Tested

- Test suites pass: .NET 559, Godot plugin 625, Unity plugin 235, Unreal plugin 10.
- `eng/package.ps1` builds the five release assets for 0.2.0 (the app zip, three plugin zips, the SBOM, plus `SHA256SUMS.txt`).
- Godot 4.7.2 and Unity 6000.6.7f1: runs started from `cluewren run`, from Claude Code and from Codex reached their goals or verdicts; a Claude Code run on a Unity project and a Codex run on a Godot project went at the same time.
- Unreal 5.8.4: the plugin compiled for a Blueprint-only project; `cluewren run` played a minimal harness in play-in-editor; a Claude Code run reached its goal (eliminate two enemies, 147 s) and a Codex run gave its verdict (the player can jump, CONFIRMED, 112 s), both through the server registered by `cluewren connect`.
- With Codex, the frame describer subagent's `frames` call reached the run from its own `cluewren mcp` process (a verify request on Unity reported with pictures described).
- `cluewren connect claude` and `cluewren connect codex` on a real configuration, and `connect codex --approve-tools` followed by `--remove`, which left `config.toml` byte for byte as before.
- Not verified: a built game of any engine started with its connect flag against the real `cluewren` command (Unreal only against a stand-in brain in the plugin's own tests); Unity's dropping of captures across sessions with a game that renders; a Unity dedicated server build (no server module on the test machine) and Build Profile defines.
- Not measured in this release: frame rates (the Performance numbers of 0.1.1 and 0.1.2 were not taken again).

### Known limitations

- Windows 11 x64 only. On other systems there is no container for harness code: with `CLUEWREN_SANDBOX` unset or `auto`, harness code runs in a plain child process with a note that it can read the user's files and reach the network (no CPU or memory limit); with `CLUEWREN_SANDBOX=required` it is refused.
- The Unreal plugin is beta and source-only.
- Invited editors connect by themselves without asking the user.
- At most 8 handshakes are in progress at a time; another user's process on the same machine can hold them for up to 10 s each and delay an editor's connection (it cannot authenticate, learn the run's folder, or make the brain open a file).
- Godot starts `powershell.exe` once per invite file to check ownership.
- Link limits ([ADR 0016](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0016-the-link-bounds-what-a-harness-child-queues.md) bounds what the brain holds for the game per child at 16 MiB waiting plus 32 MiB kept for replay):
  - Messages that expect no reply, queued by a child before its session failed, are still delivered, and replayed on a reconnect, within those limits.
  - A status is lost while the game is 16 MiB behind.
  - The one message being written is buffered outside these counters.
- For `connect claude`, ownership is judged from what `claude mcp get` displays, and that display drops trailing white space. So an entry that runs a file named cluewren or cluewren.exe with arguments that differ from `mcp` only by invisible trailing white-space arguments cannot be told apart from Cluewren's own and is treated as Cluewren's.
- `cluewren connect codex`: the `config.toml` write is not atomic and there is no copy to go back to; on macOS/Linux (unsupported) another program is not kept from writing the file between connect's read and write.
- rc refusals are defense in depth, not a boundary ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md)).
- rc refusals add a few microseconds per remote call on Godot ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md)).
- If two agent sessions in the same folder each have a run, the second run's frames are reachable only inside its own process.
- Unmeasured as of release ([ADR 0006](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0006-autopilot-is-the-floor.md)): a comparison on an untuned Unity game; decision-model token and subscription usage.
- Issue 27: on Unreal a run started from the brain creates the play-in-editor world twice, 0.6 s apart; not re-checked in 0.2.0.
- On a Unity script reload, responses still queued or held and requests still being worked on (a move, a watch, a capture) are lost; the brain does not send them again and they end by its timeout (30 s by default). Only the waits of a session start, stop or restart are carried across the reload.
- A session is not resumed after a dropped socket: that would need the brain to acknowledge responses, which this protocol does not have.

## [0.1.2] - 2026-11-14

Cluewren 0.1.2 bounds what the brain holds for the game on behalf of a harness child, drops unread game messages past 32, reports file errors in `cluewren run` with exit code 1, measures frame rates through the editor, and checks Unreal Shipping packages.

### Security

- The link to the game is bounded ([ADR 0016](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0016-the-link-bounds-what-a-harness-child-queues.md), closes issue 19): Frames a harness child has the brain send to the game (a post, a request, a status) are taken only while less than 16 MiB waits to be written to the game. A request that is not taken fails at once, a status is dropped, and a post that is not taken ends the child's session (and kills the child, as every other violation does). The brain's own frames (releasing held input, pausing, ending a run) are always sent. The frames kept for replay after a reconnect are at most 1024 frames and 32 MiB. So what the brain holds for the game on behalf of a child is at most 16 MiB waiting plus 32 MiB kept for replay. A reconnect (an editor coming back after a script reload) that needs a request no longer kept is not resumed with a gap any more: the brain ends the connection and the run's link ("the game came back after requests it missed were no longer kept"). The editor sees a normal disconnect.
- Episode mark verification: An episode mark from the child for another episode than the one being played ends the session.
- Unread game messages: Messages from the game that nobody reads (for example a recording a harness starts under `cluewren run`) are kept to the newest 32; older ones are dropped.
- Link repairs: Requests reach the game in the order of their ids; a reconnect interrupted a second time keeps that order and does not send again a request the game says it has; a reply arriving just as its request gave up waiting is released.

### Changed

- Compatibility and upgrade: No protocol change. Brain 0.1.2 works with plugin 0.1.1 and 0.1.2; plugin 0.1.2 differs from 0.1.1 only in its version number. Upgrading the brain alone is enough; upgrading both is fine. Nothing to run again after upgrading from 0.1.1 unless the folder moved (then `connect` as before).

### Fixed

- Issue 23: `cluewren run` reports a file error while compiling or writing the harness (the run folder's bin not writable, the disk full, a file in use) with "The run couldn't use its files: ..." and exit code 1, instead of ending with an unhandled exception.
- On macOS and Linux (unsupported) the brain waits for a killed harness child to exit, as on Windows since 0.1.1. Not run: the build and test machines are Windows.

### Performance

- Unreal packaged Shipping build (Unreal 5.8, BuildCookRun, Win64 Shipping): the package contains none of the plugin's code, binaries or content. What it does contain from the plugin: two small text files inside the .pak (the descriptor `Cluewren.uplugin`, 932 bytes, and `Config/FilterPlugin.ini`, 40 bytes) and the plugin's class names as strings in the cooked script-object table. The executable holds none of the plugin's names.
- Frame rate through the editor (the way a user's playtest runs), same 900-object scene as in 0.1.1, three 22 s repetitions each, machine otherwise idle (total CPU 16-26%):
  - Godot 4.7.2, game started by the editor: the mean frame rate during a run was 6% lower in one set (550 fps without a run, 518 with) and 11% lower in the other (469 without, 418 with); median frame time +0.09 ms and +0.24 ms; the longest frame 14-19 ms against 5-7 ms.
  - Unreal 5.8, play-in-editor: 92 fps without a run, 90 with (2% lower, the same size as the drift between repetitions).
  - Together with 0.1.1's measurements (games started directly: no difference beyond noise on Godot and Unreal; Unity in the editor's play mode 164 against 159 fps).
  - Still not measured: the first seconds after connecting on Unreal while its asset export runs; a harness that records frames continuously.

### Known limitations

- Windows 11 x64 only. Linux and macOS remain untested and unsupported. On Linux, the whole file system is bound read-only so the harness can read every file the user can read, and there is no CPU or memory limit there.
- The Unreal plugin is beta and source-only.
- Project-matching limits:
  - An editor that opened its project through another alias than the agent's folder (a second junction, a subst drive, an 8.3 name) does not match textually; pass `project` with the path the editor shows.
  - An agent started in a folder that holds several game projects matches all of them; the first matching editor is played unless `project` is given.
  - One brain.json per user: a later start_run from another agent session takes the announcement over (as in 0.1.0).
  - At most 8 handshakes are in progress at a time; another user's process on the same machine can hold them for up to 10 s each and delay an editor's connection (it cannot authenticate, learn the run's folder, or make the brain open a file).
- Editors of matching projects still connect by themselves.
- Development and debug builds contain the plugin and activate only with the connect flag.
- Unreal Shipping builds contain none of the plugin's code (checked on a packaged build; the package carries the plugin's descriptor and one small config file).
- Godot starts powershell.exe once per brain.json to check ownership.
- Link limits ([ADR 0016](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0016-the-link-bounds-what-a-harness-child-queues.md) bounds what the brain holds for the game per child at 16 MiB waiting plus 32 MiB kept for replay):
  - Posts a child queued before its session failed are still delivered, and replayed on a reconnect, within those limits.
  - A status is lost while the game is 16 MiB behind.
  - The one message being written is buffered outside these counters.
- For `connect claude`, ownership is judged from what `claude mcp get` displays, and that display drops trailing white space. So an entry that runs a file named cluewren or cluewren.exe with arguments that differ from `mcp` only by invisible trailing white-space arguments cannot be told apart from Cluewren's own and is treated as Cluewren's.
- `cluewren connect codex`: the `config.toml` write is not atomic and there is no copy to go back to; on macOS/Linux (unsupported) another program is not kept from writing the file between connect's read and write.
- Unmeasured as of release ([ADR 0006](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0006-autopilot-is-the-floor.md)): a comparison on an untuned Unity game; decision-model token and subscription usage. Frame rate during a run was measured on each engine (see Performance): on Godot through the editor it was 6-11% lower; Unreal's first seconds after connecting and a harness that records frames continuously are not measured.
- rc refusals add a few microseconds per remote call on Godot ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md)).
- rc refusals are defense in depth, not a boundary ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md)).
- Issue 27: on Unreal a run started from the brain creates the play-in-editor world twice, 0.6 s apart; play then goes on normally. Not investigated.

## [0.1.1] - 2026-11-14

Cluewren 0.1.1 binds playtest runs to an editor whose project matches the agent's working folder, treats harness child input as untrusted, sets a CPU ceiling on the harness child process, removes automatic tool approval from `cluewren connect`, adds MCP tool annotations, and improves capture and path-planning performance.

### Security

- Project-bound editor connection ([ADR 0015](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0015-a-run-takes-only-its-projects-editor.md)): `start_run` announces the agent's working folder (the folder `cluewren mcp` was started in). An editor connects only if its project is that folder, inside it, or contains it. `start_run` takes an optional `project` (absolute path of the game project) for an exact match; `cluewren run` takes `--project <folder>`. The plugin decides first, from `brain.json` (`folders`, `match`), and does not connect at all for another project (no socket and none of its on-connect effects: Unreal asset export, Godot attach to the running game, Unity virtual input devices), showing in its Cluewren window: `For another project: the run is for "..." (match "..."), this project is "...".` The brain decides with authority: the editor's first message carries its project folder and a MAC over it; the brain checks the message size, then the MAC, then that the path is a rooted local path, and only then touches the file system. An unauthenticated peer gets a fixed text that names no folder. Folders are compared by file identity (volume serial and file id), keeping handles open while it relies on that identity. The verified project is bound to the run: the memory folder is created there; once the game has started, the game's own report of its project must be the same folder; a reconnect must carry the same project and engine. Unity sets up its virtual input devices only after the brain has proved itself; the Godot editor attaches to its running game only while connected.
- Harness child input treated as untrusted: The brain guarantees: a line from the child is at most 4 MiB; a line that is not JSON, a message of the wrong shape, a reply without value or error, each end the session and kill the child (the brain itself keeps running); per child the brain holds at most 512 items or 16 MiB across game requests and Jev requests being served and statuses waiting to be shown, past which the session ends; after a session ends nothing already read is delivered or forwarded and requests being served are cancelled; the child cannot send raw frames to the game (a `send` from the child ends the session). Link-side limits are not guaranteed and are tracked in issue 19.
- CPU ceiling: The harness child's job has a hard ceiling of two logical processors' worth of the whole machine. It is a ceiling, not a reservation: measured about 2.0-2.2 processors' worth alone and about 1.6 while every processor was busy; lower again when the brain itself runs in a CPU-limited job. Not set on machines with two or fewer logical processors. If it cannot be set, `CLUEWREN_SANDBOX=required` fails before any agent code runs; otherwise the run goes on and its notes say so.
- Fallback plain child created inside its job: When the AppContainer cannot be used and `CLUEWREN_SANDBOX` is not `required`, the fallback plain child is now created inside its job object; before, it started and was put in the job a moment later.
- Failed child start: A child start that fails part way releases everything it had acquired (handles, memory).
- Connect registrations and tool approvals: `cluewren connect` registers no automatic approval. `cluewren connect codex --approve-tools` writes `default_tools_approval_mode = "approve"`. An entry is Cluewren's only when it is a stdio server that runs a file named cluewren or cluewren.exe with the single argument mcp (in Codex's config.toml also dotnet with Cluewren.dll and mcp). connect never removes or rewrites anything else, never reads an entry's environment for reuse, and never prints another entry's command, arguments or environment values.
- MCP tool annotations: Tool declarations specify operational characteristics: only `wait` and `frames` are read-only; all others act on the live game or editor or write files (destructive); `start_run`, `probe`, `check_harness`, `run_harness` can reach the TypeSafe API (open world). Each tool has a title. Clients that ask before destructive tools will ask.

### Changed

- Compatibility and upgrade: Brain 0.1.1 needs plugin 0.1.1 in the game project: a 0.1.0 plugin is refused with the text "update the Cluewren plugin in this project". Plugin 0.1.1 still works with a 0.1.0 brain. Upgrade both. After upgrading, run `.\cluewren.exe connect codex` once to remove the automatic approval line written by 0.1.0. For Claude Code after moving or reinstalling the executable, run `.\cluewren.exe connect claude --remove` and then `.\cluewren.exe connect claude`; settings the user added to the old entry (for example `-e CLUEWREN_SANDBOX=required`) are theirs to add again, and an entry with such settings is removed with `claude mcp remove --scope user cluewren`.
- Connect registration: `cluewren connect` writes no automatic approvals. `cluewren connect codex --approve-tools` writes `default_tools_approval_mode = "approve"`. A plain re-run removes the line 0.1.0 wrote and leaves an approval line the user wrote. For Claude Code, registration occurs only when Claude Code has no server named cluewren; an existing entry running this Cluewren is unchanged, any other entry named cluewren is left as it is and connect says what to do by hand, and connect never moves or copies settings of an existing entry. For `connect claude`, ownership is judged from what `claude mcp get` displays, and that display drops trailing white space: an entry that runs a file named cluewren or cluewren.exe with arguments that differ from `mcp` only by invisible trailing white-space arguments cannot be told apart from Cluewren's own and is treated as Cluewren's. For Codex, connect changes only the command and args of a cluewren table that already runs Cluewren, keeping every other line byte for byte; `connect codex --remove`, and an add followed by a remove, leave every byte outside Cluewren's own table as it was (line endings included).
- `cluewren connect codex` now edits `config.toml` in place and no longer writes `config.toml.before-cluewren`: the config can hold keys in MCP env settings, and a new file does not reliably keep the old one's access rules, owner, mode, encryption or links. A backup left by 0.1.0 is not touched.
- Run project parameters: `start_run` takes an optional `project` (absolute path of the game project) for an exact match; `cluewren run` takes `--project <folder>`.
- MCP tool annotations: Tools declare titles, destructive flags, and open-world flags. Clients configured to prompt before destructive tools will prompt.
- Child runtime path: The child program folder is now `%LOCALAPPDATA%\Cluewren-child\v2\<version>`: built aside and renamed into place, held open by the running brain, and other versions under `v2` are removed at start (never one in use; leftovers younger than ten minutes are kept). Folders 0.1.0 created directly under `Cluewren-child` are not touched and stay until the user deletes the `Cluewren-child` folder.
- Child input limits: A line from the child over 4 MiB ends the session. A line from the harness child that is not JSON ends the session and kills the child (the brain itself keeps running).

### Fixed

- A tool call that raced the end of a run could act on the ended run; stopping the server waits (up to 15 s) for the call in flight; a call arriving after the stop gets a tool error.
- A way planned over several ticks is checked against obstacles seen meanwhile.
- Issue 12: The four Unity isolation tests failed in projects whose Active Input Handling is the Input Manager alone (not because of batch mode, as 0.1.0 stated), and are now reported as ignored there with the reason.
- Issue 11 and other tests no longer depend on machine speed (the kit's test fakes run on a virtual clock).
- The Godot editor dock now shows a problem as soon as it changes (it stayed on "Connecting").
- A Godot project at a drive root can connect.
- `connect claude` treated any server named cluewren as Cluewren's and replaced it.
- `connect codex --remove` could delete the last line of an unrelated multi-line string that looked like its marker comment.
- A harness child is gone before its end is reported (the brain waits for the killed child to exit, at most 30 s): before, the child could still hold the compiled harness in the run folder for a moment, so compiling again into the same run folder or deleting it could fail. Seen with `cluewren run`.
- The session's `sandbox.log` is closed as soon as the session ends; before, it could stay open for a moment (measured under load: in about 5% of sessions), so deleting or reopening it right away could fail.
- `cluewren connect` ends with a one-line reason and exit code 3 instead of an exception when it cannot write one of its files.

### Performance

- Unity frame capture: Scaling and flipping moved to the GPU; a 3840x2160 recording frame costs about 6 ms instead of 65 (main thread 6.0 -> 1.0 ms); a 4K still 110 -> 23 ms and 40 MB -> 8 MB of heap. Verified equal to the old pictures (difference at most 1 level) on Direct3D 11, Direct3D 12, Vulkan and OpenGL Core.
- Unity compile time: The IL post-processor passes over assemblies that never use UnityEngine.Input (3.0 s -> 0.5 s over 74 assemblies in the measurement).
- Godot Compatibility renderer: Frames are read back from a GPU copy at the size sent; game thread cost per recorded frame at 3840x2160 about 16 ms -> 1.5 ms. Forward+ and Mobile unchanged (already asynchronous).
- Path planning: A goal that cannot be reached no longer stalls a harness tick (worst step in the bench 462 ms -> under 40 ms); long plans continue over the next ticks; a wide arrival radius no longer enumerates its whole region (562 ms -> 0.2 ms in the measured case); walks are unchanged.
- Unreal asset export: Not changed. The Unreal asset export stays a separate editor process (moving it in-process would load assets on the running editor's game thread during the playtest).
- Frame rate with and without a run: Measured once, on one machine (Core i5-13500, RTX 4070 Ti), in a scene of 900 moving objects with a shadowed light, vsync off, three 22 s repetitions each, while other work was running on the machine, so differences of a few percent cannot be told from noise. The harness observed the pose 15 times a second, took a view every second and held movement keys; it made no decision-model call. Godot 4.7.2 (game started directly, 1280x720, Forward+): mean 307 fps without a run, 313 with. The slowest frames are longer during a run (longest frame 18-25 ms against 6-11 ms; 1% low 128-130 fps against 134-207). Unity 6000.6 (play mode in the editor, 2560x1440, Direct3D 12): mean 164 fps without a run (two repetitions), 159 with (three). Unreal 5.8 (game started with the editor executable, 1280x720): mean 112 fps without a run, 113 with. In all three the mean frame rate with a run is within the spread of the runs without one. Not measured: Godot and Unreal through play-in-editor; the first seconds after connecting on Unreal, when the plugin's asset export runs in a separate process (about 32 s in the trial); a quiet machine; a harness that records frames continuously.

### Known limitations

- Windows 11 x64 only. Linux and macOS remain untested and unsupported. On Linux, the whole file system is bound read-only so the harness can read every file the user can read, and there is no CPU or memory limit there.
- The Unreal plugin is beta and source-only.
- Project-matching limits:
  - An editor that opened its project through another alias than the agent's folder (a second junction, a subst drive, an 8.3 name) does not match textually; pass `project` with the path the editor shows.
  - An agent started in a folder that holds several game projects matches all of them; the first matching editor is played unless `project` is given.
  - One brain.json per user: a later start_run from another agent session takes the announcement over (as in 0.1.0).
  - At most 8 handshakes are in progress at a time; another user's process on the same machine can hold them for up to 10 s each and delay an editor's connection (it cannot authenticate, learn the run's folder, or make the brain open a file).
- Editors of matching projects still connect by themselves.
- Development and debug builds contain the plugin and activate only with the connect flag.
- Unreal Shipping builds contain none of the plugin's code (checked on a build; the plugin's descriptor file may still be packaged).
- Godot starts powershell.exe once per brain.json to check ownership.
- The link-side limits of issue 19: frames the child has the brain forward to the game (`post`) are not counted or limited, and the link keeps them for replay; after a child fails, frames it had already queued are still sent and are replayed on a reconnect; a reconnect that needs frames no longer held is not refused; episode notifications from the child are not checked against the brain's episode.
- For `connect claude`, ownership is judged from what `claude mcp get` displays, and that display drops trailing white space. So an entry that runs a file named cluewren or cluewren.exe with arguments that differ from `mcp` only by invisible trailing white-space arguments cannot be told apart from Cluewren's own and is treated as Cluewren's.
- `cluewren connect codex`: the `config.toml` write is not atomic and there is no copy to go back to; on macOS/Linux (unsupported) another program is not kept from writing the file between connect's read and write.
- Issue 23: `cluewren run` still ends with an unhandled exception on a file error while compiling or writing the harness (disk full, file in use).
- Unmeasured as of release ([ADR 0006](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0006-autopilot-is-the-floor.md)): a comparison on an untuned Unity game; decision-model token and subscription usage. Frame rate was measured once per engine under load (see Performance); an effect below a few percent, play-in-editor on Godot and Unreal, and Unreal's first seconds after connecting are not measured.
- rc refusals add a few microseconds per remote call on Godot ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md)).
- rc refusals are defense in depth, not a boundary ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md)).

## [0.1.0] - 2026-11-11

Cluewren is an MCP server (`cluewren mcp`), a skill and a frame-describer subagent that let your coding agent (Claude Code or Codex) playtest the game open in a Unity, Godot or Unreal editor through engine plugins over a loopback WebSocket. First public release.

### Added

Release assets:
- `cluewren-0.1.0-win-x64.zip`: the application binary (`cluewren.exe` and its runtime files), `LICENSE`, `NOTICE`, and `THIRD-PARTY-NOTICES.txt`.
- `cluewren-unity-0.1.0.zip`: the Unity package (unzips to `com.cranevalley.cluewren/`).
- `cluewren-godot-0.1.0.zip`: the Godot addon (unzips to `addons/cluewren` in the project root).
- `cluewren-unreal-0.1.0.zip`: the Unreal Engine plugin source (unzips to `Cluewren/`; built by the user with their engine, Win64 only).
- `cluewren-0.1.0.cdx.json`: CycloneDX SBOM of the application.
- `SHA256SUMS.txt`: SHA-256 checksums of the other release assets.

Install instructions: https://github.com/crane-valley/cluewren#quick-start

### Requirements

- Windows 11 x64 only (Linux and macOS are not supported).
- ASP.NET Core Runtime 10 (x64), which includes the .NET runtime. The .NET 10 SDK is needed only when building from source.
- Claude Code or Codex CLI.
- A TypeSafe API key for the decision model (TypeSafe's Jev). TypeSafe is a separate service with its own account, pricing and terms; Cluewren grants no rights to it.
- One supported engine: Unity 6000.0 or later, Godot 4 (tested with 4.7.2), or Unreal Engine 5 (tested with 5.8, Win64 only, needs a C++ toolchain to build the plugin, marked beta).
- No administrator rights are needed.

### Security

- Remote call (rc) refusals on every engine: The plugins refuse the same nine categories on Unity, Godot and Unreal as defense in depth. A game's own code stays reachable. Treat harness code as running with the editor's rights. See [ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md) and [SECURITY.md](https://github.com/crane-valley/cluewren/blob/main/SECURITY.md).
- Model-written C# harnesses run in a child process inside a Windows AppContainer with no network access and write access limited to their run folder.
- `CLUEWREN_SANDBOX`: Unset or `auto` runs in the container and falls back to a plain child process with no keys in its environment if the container cannot be created. Setting `CLUEWREN_SANDBOX=required` refuses to run harness code without the container (the tool call fails with the reason).

### Known limitations

- Windows 11 x64 only. Linux bubblewrap and macOS code paths exist in code but are untested and unsupported.
- The Unreal plugin is beta and source-only.
- With two editors open, either may attach to the announced brain, and a second agent session takes over the announcement (see [ADR 0010](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0010-unreal-plugin-owns-its-socket.md) and [ADR 0008](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0008-mcp-process-is-the-brain.md)).
- Unity development builds include the plugin runtime (inactive unless started for a playtest); release builds do not. Unreal Shipping builds never include it. Godot release exports carry the addon files but never activate them.
- Unmeasured as of release ([ADR 0006](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0006-autopilot-is-the-floor.md)): a comparison on an untuned Unity game; decision-model token and subscription usage; frame rate on Godot and Unreal.
- rc refusals add a few microseconds per remote call on Godot ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md)).
- Four Unity input-isolation tests fail in batch mode on the tested editors; the cause is not yet found.

### Verification

Check the SHA-256 hash against `SHA256SUMS.txt`:
```powershell
(Get-FileHash -Algorithm SHA256 .\cluewren-0.1.0-win-x64.zip).Hash.ToLowerInvariant()
```
Compare the hash with the line for that file in `SHA256SUMS.txt`.

Verify GitHub build provenance attestations:
```bash
gh attestation verify <file> --repo crane-valley/cluewren
```

Review the CycloneDX SBOM: `cluewren-0.1.0.cdx.json`.

[0.2.0]: https://github.com/crane-valley/cluewren/releases/tag/v0.2.0
[0.1.2]: https://github.com/crane-valley/cluewren/releases/tag/v0.1.2
[0.1.1]: https://github.com/crane-valley/cluewren/releases/tag/v0.1.1
[0.1.0]: https://github.com/crane-valley/cluewren/releases/tag/v0.1.0
