# Security Policy

## Supported versions

Security fixes are made for the latest 0.2.x release.

## Reporting a vulnerability

Report security vulnerabilities through GitHub private vulnerability reporting via the repository's Security tab ("Report a vulnerability"). Do not report security issues through public issues.

There is no service level agreement. The report is acknowledged and handled through the private advisory, and fixes ship as 0.2.x releases with a changelog entry.

## Threat model

### What runs with what rights

- **Coding agent**: Runs with the user's rights and communicates with its own model provider. Cluewren does not limit or control the agent.
- **Cluewren process, the brain (`cluewren mcp` / `cluewren run`)**: Runs with the user's rights. It holds the TypeSafe API key and is the only process that connects to https://api.typesafe.ai (port 443), or to the address set in `TYPESAFE_BASE_URL`. It never starts or drives a language model. It compiles model-written C# harnesses with Roslyn in its own process without running model code during compilation.
- **Harness child**: Runs the compiled C# harness in a child process inside a Windows AppContainer with no network access, read access to its run folder, and write access only to the run's work folder (`.cluewren\runs\<id>\work`). It is held by a Job Object (one process, memory limit, CPU ceiling of two logical processors' worth of the whole machine, killed when the cluewren process exits). It reaches the game and the decision model only through the cluewren process over stdio.
- **Plain-child fallback and `CLUEWREN_SANDBOX=required`**: If the container cannot be created, the default setting (`CLUEWREN_SANDBOX=auto` or unset) runs harness code in a plain child process created directly inside its Job Object with no keys in its environment and notes the downgrade. That child process can read the user's files and reach the network. Setting `CLUEWREN_SANDBOX=required` makes the container mandatory: if the container cannot be created, Cluewren refuses to run harness code, and the tool call fails with the reason.
- **Editor and plugin**: The editor runs with the editor's rights (the user's rights). The plugin connects to the cluewren bridge over loopback (127.0.0.1) on an OS-assigned port with a per-run secret handshake after being invited by the brain. The brain invites it only when its project is the agent's working folder, inside it, or contains it (or the exact `project` given to `start_run`).
- **Game code**: The game's own scripts and Blueprints run inside the editor with the editor's rights.

### Trust boundaries

- **Agent to cluewren**: MCP over stdio. The agent's own permission prompts do not apply to harness code, which is why the container exists. Tool annotations declare destructive and open-world operations.
- **Cluewren to editor plugin**: Loopback WebSocket (127.0.0.1) with a per-run secret handshake, project folder match verification, and a MAC-authenticated first message.
- **Harness child to cluewren**: Untrusted input over stdio inside a Windows AppContainer held by a Job Object.
- **Harness to editor through rc**: No boundary. Harness code runs with the editor's rights; refusals are defense in depth.

### Network and data handling

- The cluewren process connects out only to https://api.typesafe.ai (port 443), or to the TYPESAFE_BASE_URL address the user set (which must be an https address of a host alone: no user, password, path, query or fragment), using the user's TypeSafe key. Requests carry text about the game state (the goal, the situation, the options and recent actions), never pictures. While a run is active, and only then, it sends a keep-warm request about every 15 seconds when idle.
- The bridge listens on 127.0.0.1 on an OS-assigned port. It refuses non-loopback connections and connections carrying an Origin header. No plugin's WebSocket client follows an HTTP redirect; Unreal's socket also sends no credentials or cookies.
- The coding agent talks to its own provider directly. Game frames reach the agent's model through the frame-describer subagent when checking visual appearance.
- Cluewren contains no upload, telemetry, account, sign-in, or billing code.
- Frames are kept only in the cluewren process memory (the last 160 MiB) and are never written to disk by Cluewren.
- API keys are never sent to the engine plugin, logs, or run records. The plain child fallback receives no keys in its environment.

### Which editor a run takes and how it is decided

Each open editor with the plugin writes a small advert file `%LOCALAPPDATA%\Cluewren\editors\<pid>.json` (its engine, versions and project folder; no secret in it). It is deleted when the editor quits; one left by a crash is removed by the next run. `start_run` (or `cluewren run`) picks the editor whose project is the agent's working folder, a folder inside it, or one that contains it; with `project` (`start_run`) or `--project <folder>` (`cluewren run`), only an editor of exactly that project. If no such editor is open, `start_run` waits up to 60 s for one, then says why and lists the editors that are open. If editors of more than one project match, the run fails and asks for `project`. With several editors of the same project, the one whose advert was written most recently is taken.

- The brain then writes an invite only that editor reads: `%LOCALAPPDATA%\Cluewren\invites\<editor pid>.json`, readable by the current user only, holding the address and the secret of the handshake. The editor connects out to 127.0.0.1 with the per-run secret handshake. An editor that was not invited opens no socket and does nothing but look for its invite file about once a second; nothing with an effect happens before its invite has passed its checks. With a connect flag a process prepares itself before an invite is validated, because the flag is the user's own request; it opens its socket only after its invite has passed its checks, and makes no input devices before a verified welcome and exits with code 1 after the connect timeout (120 s by default); see [docs/PROTOCOL.md](docs/PROTOCOL.md) for the per-engine list.
- Several editors can be open at once, and runs on different projects can go on at the same time, one per agent session.
- An editor already in a run of another agent session is refused: "The editor of <project_dir> is already in a run of another agent session (cluewren process <pid>)".
- The brain decides with authority: the editor's first message carries its project folder and a MAC over it; the brain checks the message size, then the MAC, then that the path is a full local path, and only then touches the file system; then that it is the chosen project folder by identity. A first message from any other project folder is refused.
- An unauthenticated peer gets a fixed text that names no folder.
- Refusals before the handshake completes: the plugin shows its own text, "Refused: <text>", in its Cluewren window, dock or log, never the brain's words (a close alone is read as the refusal; unknown codes are shown only when they look like a code). Texts: "The cluewren command and this plugin are of different releases: install the same release of both"; "The brain did not accept this editor's proof of the invite"; "The run is for another project than this editor's"; "The session this editor tried to resume belongs to another project or engine". After a refusal the plugin does not try again until a new invite is written.
- Folders are compared by their identity on disk (volume serial and file id), not by path text, so a junction, a subst drive or an 8.3 name of the same folder matches. The brain keeps the folder open while the run relies on that identity (the user can still create, delete and rename files inside; renaming a folder above the project waits until the run ends).
- The verified project is bound to the run: the `.cluewren` folder is created there; once the game has started, the game's own report of its project must be the same folder; a reconnect must carry the same project and engine (a Unity editor resumes its session only across its own script reload).
- `cluewren run` goes through the same checks; it reads the harness's StartMap in a sandbox child that cannot reach the game, then starts that map and verifies.
- Unity sets up its virtual input devices only after the brain has proved itself; the Godot editor attaches to its running game only while connected.
- At most 8 handshakes are in progress at a time and a handshake not finished within 10 s is dropped. Another user's process on the same machine can hold those 8 handshakes and delay an editor's connection (it cannot authenticate, learn the run's folder, or make the brain open a file).
- Processes of the same Windows user are trusted: they can write adverts and read invites.
- Godot: the addon starts `powershell.exe -NoProfile -NonInteractive` once per invite file (only when that file changes) to check that the invite and its folder are owned by the current user, are not links, and that the inviting process is running.

### Harness child input handling and resource limits

- CPU ceiling: The child's job has a hard ceiling of two logical processors' worth of the whole machine. It is a ceiling, not a reservation: the child gets less when the machine is busy or when the brain itself runs in a CPU-limited job. Not set on machines with two or fewer logical processors. If it cannot be set, `CLUEWREN_SANDBOX=required` fails before any agent code runs; otherwise the run goes on and its notes say so.
- Child creation and cleanup: The fallback plain child (when the AppContainer cannot be used and `CLUEWREN_SANDBOX` is not `required`) is created inside its job object, so it never runs outside it. A child start that fails part way releases everything it had acquired (handles, memory).
- Child program folder: The child's program folder is `%LOCALAPPDATA%\Cluewren-child\v2\<version>`: built aside and renamed into place, held open by the running brain, and other versions under `v2` are removed at start (never one in use; leftovers younger than ten minutes are kept).
- Guarantees on child input:
  - Input from the child is treated as untrusted.
  - In the AppContainer the harness child can read its run folder and load what is there, and write only to the run's work folder (`.cluewren\runs\<id>\work`). The plain-child fallback has none of the container's file limits.
  - The container is kept once it worked: in automatic sandbox mode (CLUEWREN_SANDBOX unset or auto) a run falls back to a plain child only while no contained child of that run has answered; after one has, a container that cannot be made again fails the call instead of falling back to a plain child. What an earlier child left in the work folder (carry.json) is read by the next child only after its first answer and is ignored whatever is wrong with it.
  - The brain passes to the game only the operations harness code may send: rc, find, transforms, pose, movement, view, watch, screen, ui, ui_act, key, input, move, pointer, pause, context, levels, ping, and session with action restart. Recording, capture, detectors, the timestep, and starting, stopping or asking the status of the game session are the brain's own. A request for another operation is answered with status 403 ("<op> is refused: harness code may not send it") while the session goes on; a message of another operation that expects no reply is dropped.
  - Decisions are counted by the brain: a decision counts toward the report's minimum of 3 decisions in an episode only when the decision model's answer, read as the kit reads it, gives an offered option a probability above zero (and the request offered two or more options by different names). The report uses the smaller of the brain's count and the harness's own number.
  - Which pictures belong to an episode is decided by the brain: an episode begins where the brain saw the level start over (the harness's restart answered) or, for play that goes on from where it is, just before the call is sent; it ends where run_harness returned. A harness's own marks cannot move the beginning later or the end earlier; a probe or a check cannot mark an episode at all (a mark outside an episode ends the session); an episode that was numbered and not played leaves no pictures to the one that gets its number next.
  - Stopping an episode (the `stop` tool, a cancelled call, the run's time limits) does not rest on the harness child: the brain sets the end after 15 seconds before it asks the child to stop, and asks it on another thread, so a child that does not read its input cannot hold the stop up. If the child has not stopped by then, the brain ends the session itself, lets go of the keys and pauses the game.
  - A line from the child is at most 4 MiB.
  - A line that is not JSON, a message of the wrong shape, a reply without value or error, each end the session and kill the child (the brain itself keeps running).
  - Per child the brain holds at most 512 items or 16 MiB across game requests and Jev requests being served and statuses waiting to be shown, past which the session ends.
  - After a session ends nothing already read is delivered or forwarded and requests being served are cancelled.
  - The child cannot send raw messages to the game (a `send` from the child ends the session).
  - A notification from the child that an episode began or ended, for another episode than the one being played, ends the session.
  - Messages from the game that nobody reads (for example a recording a harness starts under `cluewren run`) are kept to the newest 32; older ones are dropped.

#### The link to the game is bounded

- Messages the harness has the brain send to the game (a message that expects no reply, such as input; a request; a status) are taken only while less than 16 MiB waits to be written to the game. A request that is not taken fails at once, a status is dropped, and a message that expects no reply and is not taken ends the child's session (and kills the child, as every other violation does). The brain's own messages to the game (releasing held input, pausing, ending a run) are always sent.
- The messages kept for replay after a reconnect are at most 1024 messages and 32 MiB.
- So what the brain holds for the game on behalf of a child is at most 16 MiB waiting plus 32 MiB kept for replay.
- A reconnect (an editor coming back after a script reload) that needs a request dropped from the messages kept for replay is not resumed: the brain ends the connection and the run ("the game came back after requests it missed were no longer kept"). The editor sees a normal disconnect.
- Requests reach the game in the order they were made, also across reconnects, and a reconnect does not repeat a request the game already has.

#### What is not guaranteed

- Messages that expect no reply, queued by a child before its session failed, are delivered, and replayed on a reconnect, within the limits above.
- A status is lost while the game is 16 MiB behind.
- The one message being written is buffered outside these counters.

### Agent setup and `cluewren connect`

- `cluewren connect` registers no automatic approval. `cluewren connect codex --approve-tools` writes `default_tools_approval_mode = "approve"` (Codex then calls Cluewren's tools without asking). A plain re-run removes the line 0.1.0 wrote and leaves an approval line the user wrote.
- One ownership rule: an entry is Cluewren's only when it is a stdio server that runs a file named `cluewren` or `cluewren.exe` with the single argument `mcp` (in Codex's `config.toml` also `dotnet` with `Cluewren.dll` and `mcp`). connect never removes or rewrites anything else, never reads an entry's environment for reuse, and never prints another entry's command, arguments or environment values.
- `cluewren connect claude` registers Cluewren with Claude Code (user scope) only when Claude Code has no server named cluewren. If that entry already runs this Cluewren, nothing changes. Any other entry named cluewren is left as it is, and connect says what to do by hand. connect never moves or copies the settings of an existing entry. If connect cannot tell what is registered (an error, a timeout, an answer it does not recognise), it changes nothing. For `connect claude`, ownership is judged from what `claude mcp get` displays, and that display drops trailing white space. So an entry that runs a file named cluewren or cluewren.exe with arguments that differ from `mcp` only by invisible trailing white-space arguments cannot be told apart from Cluewren's own and is treated as Cluewren's.
- `cluewren connect codex` changes only the command and args of a cluewren table that already runs Cluewren; every other line and sub-table stays byte for byte (env such as `CLUEWREN_SANDBOX = "required"`, cwd, tool lists). A table that runs something else, an HTTP server, or a config.toml that connect cannot read exactly is left unchanged, and connect prints the lines to set by hand. `connect codex --remove`, and an add followed by a remove, leave every byte outside Cluewren's own table as it was (line endings included).
- Save sequence for `config.toml`: connect edits the file in place. It reads the file and computes the change; then it opens the file once so that no other program can open it meanwhile (a Windows guarantee), reads it again through that handle, and writes only if it still holds what was read. It makes no other file with the config's content: no temporary file and no backup. A `config.toml.before-cluewren` left by 0.1.0 is not touched; you may delete it.
- Why `config.toml` is edited in place: the config can hold keys in MCP env settings, and a new file does not reliably keep the old one's access rules, owner, mode, encryption or links; editing in place leaves all of those as they were. A `config.toml` that is a symbolic link stays a link and its target is edited. A hard link to the file under another name shares the file, so that name changes too.
- What is given up: the write is not atomic and there is no copy to go back to. If the write fails part way (disk error, power loss during a write of a few kilobytes), the file can be left damaged; connect then prints "Writing <path> failed after it began: ... What the file holds now is not known; it may be damaged. Check it." and the lines to set by hand. A user who wants a copy makes one before running connect.
- A `config.toml` that cannot be opened that way (open in another program, read-only, access denied) is left as it is: "Left <path> as it is: <reason>. Set the cluewren server up there by hand: command = ..., args = [...]" and connect ends with exit code 3. If the file changed between the two reads: "Left <path> as it is: it changed while connect was reading it; run connect again".
- A new `config.toml` (none existed) takes the Codex home folder's access rules like any file created there (Windows); on macOS/Linux (unsupported) it is created readable by the owner only. On macOS/Linux another program is not kept from writing the file between connect's read and write.

### MCP tool annotations

MCP tool annotations say what the tools do: only `wait` and `frames` are read-only; all others (including `stop`) act on the live game or editor or write files (destructive); `start_run`, `probe`, `check_harness`, `run_harness` can reach the TypeSafe API (open world). Each tool has a title. Clients that ask before destructive tools will ask.

The `stop` tool ends the episode, check or probe still going where it is: the keys are let go and the game is paused; then call `wait` for what it gave until then. It and a client's cancellation (`notifications/cancelled`) act only on a call that plays: `probe`, `check_harness`, `run_harness`. For another call still going (`start_run`, for example), `stop` answers "<tool> is still going and can't be stopped: call wait."; for one that has already ended, "<tool> has ended: call wait for its result." A cancellation of the `wait` request stops the play it waits for. A stop that arrives while the call is still compiling or starting its child is kept, and the call answers "Stopped before it began."

When an agent's subagent starts its own `cluewren mcp` in the same working folder (as Codex does for the frame describer), its `frames` call is answered by the process that has the run, through a named pipe only the current user can open, named after the folder's identity on disk (so two spellings of one folder reach the same run). Only `frames` goes this way; frames are never written to disk. If two agent sessions in the same folder each have a run, the second run's pipe name is taken, and its frames are reachable only inside its own process.

### Linux and macOS platform status

Windows 11 x64 is the only supported system. Linux and macOS are unsupported. On other systems there is no container for harness code: with `CLUEWREN_SANDBOX` unset or `auto`, harness code runs in a plain child process with a note that it can read the user's files and reach the network (no CPU or memory limit); with `CLUEWREN_SANDBOX=required` it is refused.

### Remote call (rc) refusals and defense in depth

Through the engine plugin's remote calls (rc), harness code acts inside the editor with the editor's rights. The container does not limit that. Treat harness code as running with the editor's rights.

As defense in depth, rather than a security boundary, the engine plugins refuse nine categories of remote calls on all three engines (status 403):

1. Processes and the shell: Process execution, shell launches, and URL opening.
2. Files by path: File and directory access, resource loading and saving by path, preferences, and file-based captures.
3. The network: Sockets, HTTP clients, servers, networking subsystems, and multiplayer APIs.
4. Code: Reflection, interop, calls and property writes by name, script source modification, expressions, threads, and shader code.
5. Editor scripting: Editor-only assemblies, editor APIs, and editor module classes.
6. Host data: Host environment, clipboard, device identifiers, camera, and audio input.
7. Consoles and command strings: Console execution, console commands, and cheat scripts.
8. The user's settings: Editor and project settings beyond their getters, and config defaults.
9. What the harness wrote: Every string from harness code is checked (".cluewren" alone and percent-encoded forms such as "%2Ecluewren%2Fruns%2Fx" are refused). A string is refused when, read as a path, one of its parts names the project's .cluewren folder (".cluewren" in any case, also a drive-relative form such as "C:.cluewren\x", stream notation after ':' or trailing dots or spaces, or a part shaped like the folder's Windows short name, such as CLUEWR~1, and with it a few other names of that shape such as CLIENT~1), checked on the text as given and once percent-decoded, after the engine's own path tables (Godot uid://, Unreal package names); nothing the string names is opened or looked up, and it does not matter which drive, share or other name of the project leads there. A restart whose level names .cluewren is refused as well. A level chosen by harness code must be a level's name or a path inside the project as the engine names it; anything else (on Unreal only a level found among the project's levels, keeping out options such as "?listen" or a host to travel to) is answered as no such level, without any file or network access. Godot also refuses res://.godot/, and rc refuses the addon's own scripts by path text under every spelling and a script by its source in memory.

The refusals cost a few microseconds per remote call on Godot and are within noise on Unity and about 0.6 us per call on Unreal.

### Residual risks

The following routes are not refused:

- All engines: The game's own code, scripts, and Blueprints, which stay reachable and may perform any action; engine members that take no path-, URL-, or command-like string; and the game's own scenes and levels opened by restarts.
- Unity: `Resources` loading and the build's scenes; `UnityEvent.Invoke` reaching the game's registered listeners.
- Godot: Members of allowed singletons (`AudioServer`, `RenderingServer`, `Engine`); `PackedScene` data; and `emit_signal` reaching the game's connections.
- Unreal: Native functions in plugins that were not reviewed, outside the refused packages.

Additional residual risks:

- Link limits (see "What is not guaranteed" above).
- A local peer can delay an editor's connection (see "Which editor a run takes and how it is decided" above).
- If two agent sessions in the same folder each have a run, the second run's frames are reachable only inside its own process (see "MCP tool annotations" above).
- `connect claude` judges ownership from what `claude mcp get` displays (see "Agent setup and `cluewren connect`" above).
- `connect codex` writes `config.toml` in place, not atomically, with no copy (see the same section above).
- On systems other than Windows (unsupported) there is no container for harness code and no CPU or memory limit (see "Linux and macOS platform status" above).
- An invited editor of a matching project connects by itself, without asking the user.
- A link into the .cluewren folder made from outside it (a junction, symbolic link, subst drive or hard link) is not followed: harness code can write only inside the folder, and links made by the user, the game's code or another process of the user are trusted.
- Built games and the bridge:
  - Unity: the plugin's runtime is compiled into a player build only when the project sets the scripting define `CLUEWREN_PLAYTEST` (Development build or not); a build without it has none of the runtime.
  - Unreal: Shipping builds do not contain the plugin's runtime; Development and Test builds contain it.
  - Godot: exports, debug or release, carry the addon; in a release export it does nothing.
  - A built game that has the bridge does nothing with it unless started with `-CluewrenConnect` (Unity, Unreal) or `-- --cluewren-connect` (Godot): without the flag it writes no advert, opens no socket and connects nowhere.
- Godot starts `powershell.exe` once per invite file to check ownership.

## Verifying a download

Downloads are available on the [release page](https://github.com/crane-valley/cluewren/releases/tag/v0.2.0).

1. Check the hash of `cluewren-0.2.0-win-x64.zip` against `SHA256SUMS.txt`:
   ```powershell
   (Get-FileHash -Algorithm SHA256 .\cluewren-0.2.0-win-x64.zip).Hash.ToLowerInvariant()
   ```
   Compare the result with the line for `cluewren-0.2.0-win-x64.zip` in `SHA256SUMS.txt`.

2. Verify the GitHub build provenance attestation for any release asset:
   ```bash
   gh attestation verify <file> --repo crane-valley/cluewren
   ```

3. Review `cluewren-0.2.0.cdx.json`, the CycloneDX SBOM provided with the release.

## Hardening for a managed environment

For deployments in managed environments:

- Set `CLUEWREN_SANDBOX=required` in the user or machine environment to prevent harness code from running without an AppContainer.
- Restrict outbound network access for `cluewren.exe` to `api.typesafe.ai:443`, or to the address set in `TYPESAFE_BASE_URL`. The bridge requires loopback access only.
- Allow the Godot addon to run `powershell.exe -NoProfile -NonInteractive` once per invite file, which verifies that the invite file and its folder are owned by the current user, are not links, and that the inviting process is running.
- Store the TypeSafe API key in the environment or in `%LOCALAPPDATA%\Cluewren\keys.env` (protected by folder ACLs set to the current user only). Never store keys in the project or paste them into agent conversations.
- Start the agent in the game's folder or pass `project` to `start_run` (or `--project` to `cluewren run`) for an exact match.
- Do not pass `--approve-tools` to `cluewren connect codex` if Codex should ask before it calls Cluewren's tools; with it, Codex calls them without asking.
- Verify download integrity with `SHA256SUMS.txt`, GitHub attestations, and the CycloneDX SBOM before deployment.

## Design records

- [ADR 0004](docs/adr/0004-model-written-csharp-harness.md): model-written C# harness run in an AppContainer
- [ADR 0014](docs/adr/0014-rc-refusals-on-every-engine.md): remote call refusals on every engine, and a requirable container
- [ADR 0016](docs/adr/0016-the-link-bounds-what-a-harness-child-queues.md): limits on what a harness child queues for the game
- [ADR 0017](docs/adr/0017-editors-advertise-and-the-brain-invites.md): editors advertise and the brain invites one
- [ADR 0018](docs/adr/0018-what-harness-code-may-touch.md): what harness code may touch
- [ADR 0019](docs/adr/0019-one-folder-in-the-project.md): one folder in the project: .cluewren
- [ADR 0020](docs/adr/0020-decisions-behind-names-of-their-own.md): the decision model's settings, and an https address only for the key
- [ADR 0021](docs/adr/0021-a-second-process-reaches-frames-and-a-call-can-be-stopped.md): a second cluewren process reaches a run's frames, and a call can be stopped
- [ADR 0022](docs/adr/0022-a-build-has-the-bridge-only-when-asked.md): a build has the bridge only when asked
- [ADR 0023](docs/adr/0023-the-data-folder-is-refused-by-its-name.md): the data folder is refused by its name
