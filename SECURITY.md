# Security Policy

## Supported versions

Security fixes are made for 0.1.x.

## Reporting a vulnerability

Report security vulnerabilities through GitHub private vulnerability reporting via the repository's Security tab ("Report a vulnerability"). Do not report security issues through public issues.

There is no service level agreement. The report is acknowledged and handled through the private advisory, and fixes ship as 0.1.x releases with a changelog entry.

## Threat model

### What runs with what rights

- **Coding agent**: Runs with the user's rights and communicates with its own model provider. Cluewren does not limit or control the agent.
- **Cluewren process (`cluewren mcp` / `cluewren run`)**: Runs with the user's rights. It holds the TypeSafe API key and is the only process that connects to https://api.typesafe.ai (port 443). It never starts or drives a language model. It compiles model-written C# harnesses with Roslyn in its own process without running model code during compilation.
- **Harness child**: Runs the compiled C# harness in a child process inside a Windows AppContainer with no network access and write access only to its run folder. It is held by a Job Object (one process, memory limit, killed when the cluewren process exits). It reaches the game and the decision model only through the cluewren process over stdio.
- **Plain-child fallback and `CLUEWREN_SANDBOX=required`**: If the container cannot be created, the default setting (`CLUEWREN_SANDBOX=auto` or unset) runs harness code in a plain child process with no keys in its environment and notes the downgrade. That child process can read the user's files and reach the network. Setting `CLUEWREN_SANDBOX=required` makes the container mandatory: if the container cannot be created, Cluewren refuses to run harness code, and the tool call fails with the reason.
- **Editor and plugin**: The editor runs with the editor's rights (the user's rights). The plugin connects to the cluewren bridge over loopback (127.0.0.1) on an OS-assigned port with a per-start secret handshake. It accepts no connections from other machines and rejects connections carrying an `Origin` header.
- **Game code**: The game's own scripts and Blueprints run inside the editor with the editor's rights.

### Trust boundaries

- **Agent to cluewren**: MCP over stdio. The agent's own permission prompts do not apply to harness code, which is why the container exists.
- **Cluewren to harness child**: Windows AppContainer over stdio.
- **Cluewren to editor plugin**: Loopback WebSocket (127.0.0.1) with a per-start secret handshake.
- **Harness to editor through rc**: No boundary. Harness code runs with the editor's rights; refusals are defense in depth.

### Network and data handling

- The cluewren process connects out only to https://api.typesafe.ai (port 443) using the user's TypeSafe key. Requests carry text about the game state (the goal, the situation, the options and recent actions), never pictures. While a run is active, and only then, it sends a keep-warm request about every 15 seconds when idle.
- The bridge listens on 127.0.0.1 on an OS-assigned port. It refuses non-loopback connections and connections carrying an Origin header.
- The coding agent talks to its own provider directly. Game frames reach the agent's model through the frame-describer subagent when checking visual appearance.
- Cluewren contains no upload, telemetry, account, sign-in, or billing code.
- Frames are kept only in the cluewren process memory (the last 160 MiB) and are never written to disk by Cluewren.
- API keys are never sent to the engine plugin, logs, or run records. The plain child fallback receives no keys in its environment.

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

## Verifying a download

Downloads are available on the [release page](https://github.com/crane-valley/cluewren/releases/tag/v0.1.0).

1. Check the hash of `cluewren-0.1.0-win-x64.zip` against `SHA256SUMS.txt`:
   ```powershell
   (Get-FileHash -Algorithm SHA256 .\cluewren-0.1.0-win-x64.zip).Hash.ToLowerInvariant()
   ```
   Compare the result with the line for `cluewren-0.1.0-win-x64.zip` in `SHA256SUMS.txt`.

2. Verify the GitHub build provenance attestation for any release asset:
   ```bash
   gh attestation verify <file> --repo crane-valley/cluewren
   ```

3. Review `cluewren-0.1.0.cdx.json`, the CycloneDX SBOM provided with the release.

## Hardening for a managed environment

For deployments in managed environments:

- Set `CLUEWREN_SANDBOX=required` in the user or machine environment to prevent harness code from running without an AppContainer.
- Restrict outbound network access for `cluewren.exe` to `api.typesafe.ai:443`. The bridge requires loopback access only.
- Allow the Godot addon to run `powershell.exe -NoProfile -NonInteractive` once per connection check, which verifies that `brain.json` and its folder are owned by the current user and that the announced process is running.
- Store the TypeSafe API key in the environment or in `%LOCALAPPDATA%\Cluewren\keys.env` (protected by folder ACLs set to the current user only). Never store keys in the project or paste them into agent conversations.
- Verify download integrity with `SHA256SUMS.txt`, GitHub attestations, and the CycloneDX SBOM before deployment.
