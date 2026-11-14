# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.1.1]: https://github.com/crane-valley/cluewren/releases/tag/v0.1.1
[0.1.0]: https://github.com/crane-valley/cluewren/releases/tag/v0.1.0
