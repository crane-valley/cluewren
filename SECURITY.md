# Security Policy

## Supported versions

Security fixes are made for the latest 0.1.x release.

## Reporting a vulnerability

Report security vulnerabilities through GitHub private vulnerability reporting via the repository's Security tab ("Report a vulnerability"). Do not report security issues through public issues.

There is no service level agreement. The report is acknowledged and handled through the private advisory, and fixes ship as 0.1.x releases with a changelog entry.

## Threat model

### What runs with what rights

- **Coding agent**: Runs with the user's rights and communicates with its own model provider. Cluewren does not limit or control the agent.
- **Cluewren process (`cluewren mcp` / `cluewren run`)**: Runs with the user's rights. It holds the TypeSafe API key and is the only process that connects to https://api.typesafe.ai (port 443). It never starts or drives a language model. It compiles model-written C# harnesses with Roslyn in its own process without running model code during compilation.
- **Harness child**: Runs the compiled C# harness in a child process inside a Windows AppContainer with no network access and write access only to its run folder. It is held by a Job Object (one process, memory limit, CPU ceiling of two logical processors' worth of the whole machine, killed when the cluewren process exits). It reaches the game and the decision model only through the cluewren process over stdio.
- **Plain-child fallback and `CLUEWREN_SANDBOX=required`**: If the container cannot be created, the default setting (`CLUEWREN_SANDBOX=auto` or unset) runs harness code in a plain child process created directly inside its Job Object with no keys in its environment and notes the downgrade. That child process can read the user's files and reach the network. Setting `CLUEWREN_SANDBOX=required` makes the container mandatory: if the container cannot be created, Cluewren refuses to run harness code, and the tool call fails with the reason. If the CPU ceiling cannot be set, `CLUEWREN_SANDBOX=required` fails before any agent code runs.
- **Editor and plugin**: The editor runs with the editor's rights (the user's rights). The plugin connects to the cluewren bridge over loopback (127.0.0.1) on an OS-assigned port with a per-start secret handshake. It connects only when its project is the agent's working folder, inside it, or contains it (or the exact `project` given to `start_run`). It accepts no connections from other machines and rejects connections carrying an `Origin` header.
- **Game code**: The game's own scripts and Blueprints run inside the editor with the editor's rights.

### Trust boundaries

- **Agent to cluewren**: MCP over stdio. The agent's own permission prompts do not apply to harness code, which is why the container exists. Tool annotations declare destructive and open-world operations.
- **Cluewren to editor plugin**: Loopback WebSocket (127.0.0.1) with a per-start secret handshake, project folder match verification, and a MAC-authenticated first message.
- **Harness child to cluewren**: Untrusted input over stdio inside a Windows AppContainer held by a Job Object.
- **Harness to editor through rc**: No boundary. Harness code runs with the editor's rights; refusals are defense in depth.

### Network and data handling

- The cluewren process connects out only to https://api.typesafe.ai (port 443) using the user's TypeSafe key. Requests carry text about the game state (the goal, the situation, the options and recent actions), never pictures. While a run is active, and only then, it sends a keep-warm request about every 15 seconds when idle.
- The bridge listens on 127.0.0.1 on an OS-assigned port. It refuses non-loopback connections and connections carrying an Origin header.
- The coding agent talks to its own provider directly. Game frames reach the agent's model through the frame-describer subagent when checking visual appearance.
- Cluewren contains no upload, telemetry, account, sign-in, or billing code.
- Frames are kept only in the cluewren process memory (the last 160 MiB) and are never written to disk by Cluewren.
- API keys are never sent to the engine plugin, logs, or run records. The plain child fallback receives no keys in its environment.

### Which editor a run takes and how it is decided (ADR 0015)

`start_run` announces the agent's working folder (the folder `cluewren mcp` was started in). An editor connects only if its project is that folder, inside it, or contains it. `start_run` takes an optional `project` (absolute path of the game project) for an exact match; `cluewren run` takes `--project <folder>`.

- The plugin decides first, from `brain.json` (`folders`, `match`), and does not connect at all for another project: no socket and none of its on-connect effects (Unreal asset export, Godot attach to the running game, Unity virtual input devices). Its Cluewren window/dock shows:
  `For another project: the run is for "..." (match "..."), this project is "...".`
- The brain decides with authority: the editor's first message carries its project folder and a MAC over it; the brain checks the message size, then the MAC, then that the path is a rooted local path, and only then touches the file system.
- An unauthenticated peer gets a fixed text that names no folder.
- Folders are compared by file identity (volume serial and file id), so junctions, 8.3 names and case-sensitive folders are judged correctly; the brain keeps the folders open while it relies on that identity (the user can still create, delete and rename files inside; renaming a folder above the project waits until the run or the editor's connection ends).
- The verified project is bound to the run: the memory folder is created there; once the game has started, the game's own report of its project must be the same folder; a reconnect must carry the same project and engine.
- `cluewren run` goes through the same checks; it reads the harness's StartMap in a sandbox child that cannot reach the game, then starts that map and verifies.
- Unity now sets up its virtual input devices only after the brain has proved itself; the Godot editor attaches to its running game only while connected.
- What an unauthenticated local peer can and cannot do: another user's process on the same machine can hold up to 8 handshakes for up to 10 s each and delay an editor's connection. It cannot authenticate, learn the run's folder, or make the brain open a file.
- Limits:
  - An editor that opened its project through another alias than the agent's folder (a second junction, a subst drive, an 8.3 name) does not match textually; pass `project` with the path the editor shows.
  - An agent started in a folder that holds several game projects matches all of them; the first matching editor is played unless `project` is given.
  - One brain.json per user: a later start_run from another agent session takes the announcement over (as in 0.1.0).
  - A process started with the connect flag on its command line (Unity batch mode, a Godot game, an Unreal game) prepares itself before it reads brain.json: that is the user's own command.

### Harness child input handling and resource limits

- CPU ceiling: The child's job has a hard ceiling of two logical processors' worth of the whole machine. It is a ceiling, not a reservation: measured about 2.0-2.2 processors' worth alone and about 1.6 while every processor was busy; lower again when the brain itself runs in a CPU-limited job. Not set on machines with two or fewer logical processors. If it cannot be set, `CLUEWREN_SANDBOX=required` fails before any agent code runs; otherwise the run goes on and its notes say so. Measured cost on a 20-processor machine: child start and probe latency within 10% of 0.1.0 (first probe +0.8% to +4.5%).
- Child creation and cleanup: The fallback plain child (when the AppContainer cannot be used and `CLUEWREN_SANDBOX` is not `required`) is now created inside its job object; before, it started and was put in the job a moment later. A child start that fails part way releases everything it had acquired (handles, memory).
- Child program folder: The child's program folder is now `%LOCALAPPDATA%\Cluewren-child\v2\<version>`: built aside and renamed into place, held open by the running brain, and other versions under `v2` are removed at start (never one in use; leftovers younger than ten minutes are kept). Folders 0.1.0 created directly under Cluewren-child are not touched and stay until the user deletes the `Cluewren-child` folder.
- Guarantees on child input:
  - Input from the child is treated as untrusted.
  - A line from the child is at most 4 MiB.
  - A line that is not JSON, a message of the wrong shape, a reply without value or error, each end the session and kill the child (the brain itself keeps running).
  - Per child the brain holds at most 512 items or 16 MiB across game requests and Jev requests being served and statuses waiting to be shown, past which the session ends.
  - After a session ends nothing already read is delivered or forwarded and requests being served are cancelled.
  - The child cannot send raw frames to the game (a `send` from the child ends the session).

#### What the brain does not guarantee (issue 19)

- Frames the child has the brain forward to the game (`post`) are not counted or limited, and the link keeps them for replay.
- After a child fails, frames it had already queued are still sent and are replayed on a reconnect.
- A reconnect that needs frames no longer held is not refused.
- Episode notifications from the child are not checked against the brain's episode.

### Agent setup and `cluewren connect`

- `cluewren connect` registers no automatic approval. `cluewren connect codex --approve-tools` writes `default_tools_approval_mode = "approve"` (Codex then calls Cluewren's tools without asking). A plain re-run removes the line 0.1.0 wrote and leaves an approval line the user wrote.
- One ownership rule: an entry is Cluewren's only when it is a stdio server that runs a file named `cluewren` or `cluewren.exe` with the single argument `mcp` (in Codex's `config.toml` also `dotnet` with `Cluewren.dll` and `mcp`). connect never removes or rewrites anything else, never reads an entry's environment for reuse, and never prints another entry's command, arguments or environment values.
- `cluewren connect claude` registers Cluewren with Claude Code (user scope) only when Claude Code has no server named cluewren. If that entry already runs this Cluewren, nothing changes. Any other entry named cluewren is left as it is, and connect says what to do by hand. connect never moves or copies the settings of an existing entry. If connect cannot tell what is registered (an error, a timeout, an answer it does not recognise), it changes nothing. For `connect claude`, ownership is judged from what `claude mcp get` displays, and that display drops trailing white space. So an entry that runs a file named cluewren or cluewren.exe with arguments that differ from `mcp` only by invisible trailing white-space arguments cannot be told apart from Cluewren's own and is treated as Cluewren's.
- `cluewren connect codex` changes only the command and args of a cluewren table that already runs Cluewren; every other line and sub-table stays byte for byte (env such as `CLUEWREN_SANDBOX = "required"`, cwd, tool lists). A table that runs something else, an HTTP server, or a config.toml that connect cannot read exactly is left unchanged, and connect prints the lines to set by hand. `connect codex --remove`, and an add followed by a remove, leave every byte outside Cluewren's own table as it was (line endings included).
- Save sequence for `config.toml`: connect edits the file in place. It reads the file and computes the change; then it opens the file once so that no other program can open it meanwhile (a Windows guarantee), reads it again through that handle, and writes only if it still holds what was read. It makes no other file with the config's content: no temporary file and no backup. 0.1.1 does not write `config.toml.before-cluewren`, and one that 0.1.0 left is not touched (the user may delete it).
- Why `config.toml` is edited in place: the config can hold keys in MCP env settings, and a new file does not reliably keep the old one's access rules, owner, mode, encryption or links; editing in place leaves all of those as they were. A `config.toml` that is a symbolic link stays a link and its target is edited. A hard link to the file under another name shares the file, so that name changes too.
- What is given up: the write is not atomic and there is no copy to go back to. If the write fails part way (disk error, power loss during a write of a few kilobytes), the file can be left damaged; connect then prints "Writing <path> failed after it began: ... What the file holds now is not known; it may be damaged. Check it." and the lines to set by hand. A user who wants a copy makes one before running connect.
- A `config.toml` that cannot be opened that way (open in another program, read-only, access denied) is left as it is: "Left <path> as it is: <reason>. Set the cluewren server up there by hand: command = ..., args = [...]" and connect ends with exit code 3. If the file changed between the two reads: "Left <path> as it is: it changed while connect was reading it; run connect again".
- A new `config.toml` (none existed) takes the Codex home folder's access rules like any file created there (Windows); on macOS/Linux (unsupported) it is created readable by the owner only. On macOS/Linux another program is not kept from writing the file between connect's read and write.

### MCP tool annotations

MCP tool annotations now say what the tools do: only `wait` and `frames` are read-only; all others act on the live game or editor or write files (destructive); `start_run`, `probe`, `check_harness`, `run_harness` can reach the TypeSafe API (open world). Each tool has a title. Clients that ask before destructive tools will ask.

### Linux and macOS platform status

Linux and macOS are unsupported (untested), not disabled. Bubblewrap is used only from an absolute PATH entry. On Linux the harness can read every file the user can read (the whole file system is bound read-only), and there is no CPU or memory limit there.

### Remote call (rc) refusals and defense in depth

Through the engine plugin's remote calls (rc), harness code acts inside the editor with the editor's rights. The container does not limit that. Treat harness code as running with the editor's rights.

As defense in depth, rather than a security boundary, the engine plugins refuse nine categories of remote calls on all three engines (status 403):

1. C1 processes and the shell: Process execution, shell launches, and URL opening.
2. C2 files by path: File and directory access, resource loading and saving by path, preferences, and file-based captures.
3. C3 the network: Sockets, HTTP clients, servers, networking subsystems, and multiplayer APIs.
4. C4 code: Reflection, interop, calls and property writes by name, script source modification, expressions, threads, and shader code.
5. C5 editor scripting: Editor-only assemblies, editor APIs, and editor module classes.
6. C6 host data: Host environment, clipboard, device identifiers, camera, and audio input.
7. C7 consoles and command strings: Console execution, console commands, and cheat scripts.
8. C8 the user's settings: Editor and project settings beyond their getters, and config defaults.
9. C9 what the harness wrote: The harness run data folder in the project (`UserSettings/Cluewren`, `res://.godot/`, `Saved/Cluewren`).

Each engine includes a census test that fails if an engine update introduces an unreviewed member taking a path-, URL-, address-, or command-like string. The refusals cost a few microseconds per remote call on Godot and are within noise on Unity and about 0.6 us per call on Unreal ([ADR 0014](docs/adr/0014-rc-refusals-on-every-engine.md)).

For design details, see [ADR 0004](docs/adr/0004-model-written-csharp-harness.md) and [ADR 0014](docs/adr/0014-rc-refusals-on-every-engine.md).

### Residual risks

As recorded in [ADR 0014](docs/adr/0014-rc-refusals-on-every-engine.md), the following routes are not refused:

- All engines: The game's own code, scripts, and Blueprints, which stay reachable and may perform any action; engine members not named by the census because they take no path-, URL-, or command-like string; and the game's own scenes and levels opened by restarts.
- Unity: `Resources` loading and the build's scenes; `UnityEvent.Invoke` reaching the game's registered listeners.
- Godot: Members of allowed singletons (`AudioServer`, `RenderingServer`, `Engine`); `PackedScene` data; and `emit_signal` reaching the game's connections.
- Unreal: Native functions in plugins that the census did not load, outside the refused packages.

Additional residual risks:

- The link-side limits of issue 19: frames the child has the brain forward to the game (`post`) are not counted or limited, and the link keeps them for replay; after a child fails, frames it had already queued are still sent and are replayed on a reconnect; a reconnect that needs frames no longer held is not refused; episode notifications from the child are not checked against the brain's episode.
- Project-matching limits: an editor that opened its project through another alias than the agent's folder (a second junction, a subst drive, an 8.3 name) does not match textually; pass `project` with the path the editor shows. An agent started in a folder that holds several game projects matches all of them; the first matching editor is played unless `project` is given. One brain.json per user: a later start_run from another agent session takes the announcement over (as in 0.1.0).
- Local peer delay: another user's process on the same machine can hold up to 8 handshakes for up to 10 s each and delay an editor's connection; it cannot authenticate, learn the run's folder, or make the brain open a file.
- For `connect claude`, ownership is judged from what `claude mcp get` displays, and that display drops trailing white space. So an entry that runs a file named cluewren or cluewren.exe with arguments that differ from `mcp` only by invisible trailing white-space arguments cannot be told apart from Cluewren's own and is treated as Cluewren's.
- `cluewren connect codex`: the write is not atomic and there is no copy to go back to (a write that fails part way can leave `config.toml` damaged); on macOS/Linux (unsupported) another program is not kept from writing the file between connect's read and write.
- Linux file system and resource exposure: on unsupported Linux systems, the harness can read every file the user can read (the whole file system is bound read-only), and there is no CPU or memory limit there.
- Editors of matching projects still connect by themselves.
- Development and debug builds contain the plugin and activate only with the connect flag.
- Godot starts `powershell.exe` once per `brain.json` to check ownership.

## Verifying a download

Downloads are available on the [release page](https://github.com/crane-valley/cluewren/releases/tag/v0.1.1).

1. Check the hash of `cluewren-0.1.1-win-x64.zip` against `SHA256SUMS.txt`:
   ```powershell
   (Get-FileHash -Algorithm SHA256 .\cluewren-0.1.1-win-x64.zip).Hash.ToLowerInvariant()
   ```
   Compare the result with the line for `cluewren-0.1.1-win-x64.zip` in `SHA256SUMS.txt`.

2. Verify the GitHub build provenance attestation for any release asset:
   ```bash
   gh attestation verify <file> --repo crane-valley/cluewren
   ```

3. Review `cluewren-0.1.1.cdx.json`, the CycloneDX SBOM provided with the release.

## Hardening for a managed environment

For deployments in managed environments:

- Set `CLUEWREN_SANDBOX=required` in the user or machine environment to prevent harness code from running without an AppContainer.
- Restrict outbound network access for `cluewren.exe` to `api.typesafe.ai:443`. The bridge requires loopback access only.
- Allow the Godot addon to run `powershell.exe -NoProfile -NonInteractive` once per `brain.json`, which verifies that `brain.json` and its folder are owned by the current user and that the announced process is running.
- Store the TypeSafe API key in the environment or in `%LOCALAPPDATA%\Cluewren\keys.env` (protected by folder ACLs set to the current user only). Never store keys in the project or paste them into agent conversations.
- Start the agent in the game's folder or pass `project` to `start_run` (or `--project` to `cluewren run`) for an exact match.
- Do not pass `--approve-tools` to `cluewren connect codex` if Codex should ask before it calls Cluewren's tools; with it, Codex calls them without asking.
- Verify download integrity with `SHA256SUMS.txt`, GitHub attestations, and the CycloneDX SBOM before deployment.
