# Cluewren

Cluewren plays the game open in your Unity, Godot or Unreal editor for your coding agent (Claude Code or Codex). Ask your agent to explore the game for bugs, test it and report every problem found, verify a claim about how it works or looks, or reach a goal in play.

## How it works

Cluewren itself never starts or drives a language model and holds no language model key. It is an MCP server named `cluewren`, a skill, and a frame-describer subagent. Your coding agent bills whatever you logged in with (subscription or API key).

Your agent researches the game's code, tries things in the live game, and writes a C# harness for this game and request. A fast decision model (TypeSafe's Jev) makes the choices in play inside that harness. The agent looks at frames through a frame-describer subagent that sees the frames but not the claim, then reports what play showed, with evidence.

The plugin connects out to the Cluewren process on the same machine over a loopback WebSocket (127.0.0.1) with a per-start secret handshake.

## Requirements

- Windows 11 x64 only (Linux and macOS are not supported)
- ASP.NET Core Runtime 10 (x64), which includes the .NET runtime (the .NET 10 SDK is needed only to build from source)
- Claude Code or Codex CLI
- A TypeSafe API key for the decision model (TypeSafe's Jev). TypeSafe is a separate service with its own account, pricing and terms; Cluewren grants no rights to it
- One supported game engine:
  - Unity 6000.0 or later
  - Godot 4 (tested with 4.7.2)
  - Unreal Engine 5 (tested with 5.8, Win64 only, needs a C++ toolchain to build the plugin, plugin marked beta)
- No administrator rights are needed

## Quick start

1. Download `cluewren-0.1.0-win-x64.zip` and `SHA256SUMS.txt` from the [release page](https://github.com/crane-valley/cluewren/releases/tag/v0.1.0) and check the zip against `SHA256SUMS.txt`:
   ```powershell
   (Get-FileHash -Algorithm SHA256 .\cluewren-0.1.0-win-x64.zip).Hash.ToLowerInvariant()
   ```
   Compare the hash with the line for that file in `SHA256SUMS.txt`. You can also verify the GitHub build provenance attestation:
   ```bash
   gh attestation verify cluewren-0.1.0-win-x64.zip --repo crane-valley/cluewren
   ```

2. Unzip it to a folder that will stay put.

3. In that folder, run `.\cluewren.exe connect claude` or `.\cluewren.exe connect codex`:
   - `connect claude` registers the MCP server with `claude mcp add --scope user` (every project) and writes `~/.claude/skills/cluewren/SKILL.md` and `~/.claude/agents/frame-describer.md`.
   - `connect codex` adds a marked `[mcp_servers."cluewren"]` table to `~/.codex/config.toml` (backing up the old file as `config.toml.before-cluewren`) and writes `~/.agents/skills/cluewren/SKILL.md` and `~/.codex/agents/frame_describer.toml`.
   `connect` records the absolute path of the binary. After moving the folder or installing a new version, run `connect` again. Use `--remove` with either command to take them out.

4. Install the engine plugin into your project (see [Engine plugins](#engine-plugins)).

5. Put your TypeSafe API key in place. Set the `TYPESAFE_API_KEY` environment variable, or add a line to `%LOCALAPPDATA%\Cluewren\keys.env`:
   ```text
   TYPESAFE_API_KEY=your-key-here
   ```
   The skill tells the agent never to ask you to paste a key into the conversation.

6. Open the game's project in its editor, start a new agent session, and ask, for example:
   ```text
   Use Cluewren to verify that the door opens with the red key.
   ```

### Build from source

```bash
git clone https://github.com/crane-valley/cluewren.git
cd cluewren
dotnet build -c Release
```
This requires the .NET 10 SDK. The binary is `src/Cluewren/bin/Release/net10.0/cluewren.exe`. Run `src/Cluewren/bin/Release/net10.0/cluewren.exe connect claude` (or `connect codex`).

## Commands

- `cluewren connect claude|codex [--remove]`: set up your agent (`--remove` takes it out)
- `cluewren mcp`: the MCP server your agent starts
- `cluewren run <run folder> [seconds]`: play `<run folder>/harness.cs` once, without an agent (default 30 seconds)
- `cluewren --version`: this build's version
- `cluewren --help`: the usage text

## Environment variables

- `TYPESAFE_API_KEY`: the decision model key; otherwise `keys.env` is read.
- `CLUEWREN_SANDBOX`: unset or `auto`: run harness code in the container, with a plain-child fallback and a note; `required`: refuse to run harness code without the container; any other value stops `cluewren mcp` and `cluewren run` at start.
- `CLAUDE_CONFIG_DIR` and `CODEX_HOME`: where `connect` writes instead of `~/.claude` and `~/.codex`.

## Engine plugins

- Unity 6000.0+: `cluewren-unity-0.1.0.zip` (package `com.cranevalley.cluewren` 0.1.0); source `plugin/unity/com.cranevalley.cluewren` - [install](https://docs.cluewren.dev/engines/unity/)
- Godot 4 (tested with 4.7.2): `cluewren-godot-0.1.0.zip` (unzips to `addons/cluewren` in the project root); source `plugin/godot/addons/cluewren` - [install](https://docs.cluewren.dev/engines/godot/)
- Unreal Engine 5 (tested with 5.8, Win64 only, beta): `cluewren-unreal-0.1.0.zip` (source only, built with your engine); source `plugin/unreal/Cluewren` - [install](https://docs.cluewren.dev/engines/unreal/)

## Privacy

What leaves the machine:
- Decision requests go to TypeSafe's API (https://api.typesafe.ai) with your TypeSafe key. They carry text about the game state: the goal, the situation, the options and recent actions. No pictures. While a run is active, and only then, the client also sends a keep-warm request about every 15 seconds when idle.
- Your coding agent talks to its own provider as it always does: it reads the game's code with its own tools, and frames reach its model through the frame-describer when it checks how something looks.

Nothing else leaves the machine. There is no upload, telemetry, accounts, sign-in or billing in Cluewren.

What stays local:
- Frames (pictures of the game) are kept only in the cluewren process's memory (the last 160 MiB) and are never written to disk. Traces, reports and run memory stay on disk in the project (`UserSettings/Cluewren` for Unity, `.godot/cluewren` for Godot, `Saved/Cluewren` for Unreal).
- API keys are never sent to the plugin, logs or run records.

## Security

The compiled C# harness runs in a child process inside a Windows AppContainer with no network access and write access only to its run folder, held by a Job Object. It reaches the game and the decision model only through the cluewren process over stdio.

If the container cannot be created, the default (`auto`) runs harness code in a plain child process with no keys in its environment and notes the downgrade. That child can read the user's files and reach the network. Setting `CLUEWREN_SANDBOX=required` makes the container mandatory and refuses to run harness code without it.

Through the engine plugin's remote calls (rc), harness code acts inside the editor with the editor's rights. The container does not limit that. The plugins refuse nine categories of remote calls on all three engines as defense in depth, not as a boundary. Game code remains reachable and may do anything. Treat harness code as running with the editor's rights.

For the threat model, verification steps and hardening guidance, see [SECURITY.md](SECURITY.md) and [ADR 0014](docs/adr/0014-rc-refusals-on-every-engine.md).

## Files and uninstall

Files written outside the game project:
- `%LOCALAPPDATA%\Cluewren\`: `keys.env` if created (plain text, protected by the folder's ACL set to the current user only); `brain.json`, the announcement the plugin reads, present while a run is active.
- `%LOCALAPPDATA%\Cluewren-child\<hash>\`: a copy of the child runtime per version.
- The files `connect` writes (see Quick start step 3).

Files written in the game project:
- Unity: `UserSettings/Cluewren` (runs, traces, reports, run memory) and `Library/CluewrenCodex` (the project as text for the agent).
- Godot: `.godot/cluewren` (runs, traces, reports, run memory) and `.godot/cluewren_codex` (project summary; falls back to `user://cluewren_codex` when `res://` is not writable).
- Unreal: `Saved/Cluewren` (runs, traces, reports, run memory), `Saved/CluewrenCodex` (the Blueprint export), and `Saved/Logs/CluewrenCodexExport.log`.

To uninstall, run `cluewren connect claude --remove` or `cluewren connect codex --remove`. Then delete the folders listed above, the engine plugin from each project, and the binary folder. `--remove` does not delete `keys.env`, the `Cluewren-child` folders, project data, or `config.toml.before-cluewren`.

## Upgrade

To upgrade, download the new zip, replace the folder's contents (or unzip to a new folder), run `.\cluewren.exe connect claude` or `.\cluewren.exe connect codex` in that folder again, and replace the engine plugin in each project with the same version. Upgrade the binary and the engine plugin together; they do not negotiate versions.

## Status

Version 0.1.0, Windows 11 x64 only. Linux and macOS are not supported. The Unreal plugin is marked beta. See [CHANGELOG.md](CHANGELOG.md) for known limitations.

## Support

File issues at https://github.com/crane-valley/cluewren/issues. Cluewren is maintained by Crane Valley with no service level agreement. For security vulnerabilities, report them privately as described in [SECURITY.md](SECURITY.md).

## License and attribution

Licensed under Apache-2.0; see [LICENSE](LICENSE).

Cluewren contains material derived from Aura Autopilot (https://github.com/ramenvr/autopilot, commit 48966d1), licensed under Apache-2.0; see [NOTICE](NOTICE). The ports were rewritten in C# and leave out Autopilot's upload, sign-in and billing code.

The Windows release includes third-party components under the MIT License; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Unity, Unreal Engine, Godot, Claude Code, Codex, TypeSafe and Jev are trademarks of their respective owners. Cluewren is not affiliated with or endorsed by them or by the Aura Autopilot authors.

## Documentation

- [Documentation](https://docs.cluewren.dev)
- [Japanese documentation](https://docs.cluewren.dev/ja/)
- [Landing page](https://cluewren.dev)
