# Cluewren

Cluewren plays the game open in your Unity, Godot or Unreal editor for your coding agent (Claude Code or Codex). Ask your agent to explore the game for bugs, test it and report every problem found, verify a claim about how it works or looks, or reach a goal in play.

## How it works

Cluewren itself never starts or drives a language model and holds no language model key. It is an MCP server named `cluewren`, a skill, and a frame-describer subagent. Your coding agent bills whatever you logged in with (subscription or API key).

Your agent researches the game's code, tries things in the live game, and writes a C# harness for this game and request. A fast decision model (TypeSafe's Jev) makes the choices in play inside that harness. The agent looks at frames through a frame-describer subagent that sees the frames but not the claim, then reports what play showed, with evidence.

The plugin connects out to the Cluewren process on the same machine over a loopback WebSocket (127.0.0.1) with a per-start secret handshake. An editor connects only when its project is the agent's working folder, inside it, or contains it.

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

1. Download `cluewren-0.1.1-win-x64.zip` and `SHA256SUMS.txt` from the [release page](https://github.com/crane-valley/cluewren/releases/tag/v0.1.1) and check the zip against `SHA256SUMS.txt`:
   ```powershell
   (Get-FileHash -Algorithm SHA256 .\cluewren-0.1.1-win-x64.zip).Hash.ToLowerInvariant()
   ```
   Compare the hash with the line for that file in `SHA256SUMS.txt`. You can also verify the GitHub build provenance attestation:
   ```bash
   gh attestation verify cluewren-0.1.1-win-x64.zip --repo crane-valley/cluewren
   ```

2. Unzip it to a folder that will stay put.

3. In that folder, run `.\cluewren.exe connect claude` or `.\cluewren.exe connect codex`:
   - `connect claude` registers the MCP server with `claude mcp add --scope user` (every project) only when Claude Code has no server named cluewren, and writes `~/.claude/skills/cluewren/SKILL.md` and `~/.claude/agents/frame-describer.md`. Any other entry named cluewren is left as it is, and connect says what to do by hand.
   - `connect codex` adds a `[mcp_servers."cluewren"]` table to `~/.codex/config.toml` (editing the file in place and writing no backup) and writes `~/.agents/skills/cluewren/SKILL.md` and `~/.codex/agents/frame_describer.toml`. On an existing cluewren table running Cluewren, it changes only the command and args, keeping every other line byte for byte. A cluewren table that runs something else, an HTTP server, or a config.toml connect cannot read exactly is left unchanged, and connect prints the lines to set by hand. `connect codex --remove`, and an add followed by a remove, leave every byte outside Cluewren's own table as it was (line endings included).
   `connect` registers no automatic approval. It records the absolute path of the binary. After moving the folder or installing a new version, run `connect` again. Use `--remove` with either command to take them out.

4. Install the engine plugin into your project (see [Engine plugins](#engine-plugins)).

5. Put your TypeSafe API key in place. Set the `TYPESAFE_API_KEY` environment variable, or add a line to `%LOCALAPPDATA%\Cluewren\keys.env`:
   ```text
   TYPESAFE_API_KEY=your-key-here
   ```
   The skill tells the agent never to ask you to paste a key into the conversation.

6. Open the game's project in its editor, start a new agent session in the game's folder (or pass `project` to `start_run`), and ask, for example:
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
- `cluewren connect codex --approve-tools`: set up Codex and write `default_tools_approval_mode = "approve"` so Codex calls Cluewren's tools without asking
- `cluewren mcp`: the MCP server your agent starts
- `cluewren run <run folder> [seconds] [--project <folder>]`: play `<run folder>/harness.cs` once, without an agent (default 30 seconds; `--project` specifies the game project folder)
- `cluewren --version`: this build's version
- `cluewren --help`: the usage text

## Environment variables

- `TYPESAFE_API_KEY`: the decision model key; otherwise `keys.env` is read.
- `CLUEWREN_SANDBOX`: unset or `auto`: run harness code in the container, with a plain-child fallback and a note; `required`: refuse to run harness code without the container; any other value stops `cluewren mcp` and `cluewren run` at start.
- `CLAUDE_CONFIG_DIR` and `CODEX_HOME`: where `connect` writes instead of `~/.claude` and `~/.codex`.

## Engine plugins

- Unity 6000.0+: `cluewren-unity-0.1.1.zip` (package `com.cranevalley.cluewren` 0.1.1); source `plugin/unity/com.cranevalley.cluewren` - [install](https://docs.cluewren.dev/engines/unity/)
- Godot 4 (tested with 4.7.2): `cluewren-godot-0.1.1.zip` (unzips to `addons/cluewren` in the project root); source `plugin/godot/addons/cluewren` - [install](https://docs.cluewren.dev/engines/godot/)
- Unreal Engine 5 (tested with 5.8, Win64 only, beta): `cluewren-unreal-0.1.1.zip` (source only, built with your engine); source `plugin/unreal/Cluewren` - [install](https://docs.cluewren.dev/engines/unreal/)

## Privacy

What leaves the machine:
- Decision requests go to TypeSafe's API (https://api.typesafe.ai) with your TypeSafe key. They carry text about the game state: the goal, the situation, the options and recent actions. No pictures. While a run is active, and only then, the client also sends a keep-warm request about every 15 seconds when idle.
- Your coding agent talks to its own provider as it always does: it reads the game's code with its own tools, and frames reach its model through the frame-describer when it checks how something looks.

Nothing else leaves the machine. There is no upload, telemetry, accounts, sign-in or billing in Cluewren.

What stays local:
- Frames (pictures of the game) are kept only in the cluewren process's memory (the last 160 MiB) and are never written to disk. Traces, reports and run memory stay on disk in the project (`UserSettings/Cluewren` for Unity, `.godot/cluewren` for Godot, `Saved/Cluewren` for Unreal).
- API keys are never sent to the plugin, logs or run records.

## Security

An editor connects only when its project is the agent's working folder, inside it, or contains it.

The compiled C# harness runs in a child process inside a Windows AppContainer with no network access and write access only to its run folder, held by a Job Object with a CPU ceiling of two logical processors' worth of the whole machine (a ceiling, not a reservation). It reaches the game and the decision model only through the cluewren process over stdio.

Input from the harness child is treated as untrusted: a line over 4 MiB, a line that is not JSON, a message of the wrong shape, a reply without value or error, or a `send` from the child ends the session and kills the child (the brain itself keeps running); per child the brain holds at most 512 items or 16 MiB across game requests and Jev requests being served and statuses waiting to be shown, past which the session ends. Not guaranteed (issue 19): frames the child has the brain forward to the game (`post`) are not counted or limited, and some link-side replay behaviour is unchecked; see SECURITY.md.

If the container cannot be created, the default (`auto`) runs harness code in a plain child process created directly inside its job object with no keys in its environment and notes the downgrade. That child can read the user's files and reach the network. Setting `CLUEWREN_SANDBOX=required` makes the container mandatory and refuses to run harness code without it.

Linux and macOS are unsupported (untested), not disabled. On Linux, the harness can read every file the user can read (the whole file system is bound read-only), and no CPU or memory limit is enforced.

Through the engine plugin's remote calls (rc), harness code acts inside the editor with the editor's rights. The container does not limit that. The plugins refuse nine categories of remote calls on all three engines as defense in depth, not as a boundary. Game code remains reachable and may do anything. Treat harness code as running with the editor's rights.

For the threat model, verification steps and hardening guidance, see [SECURITY.md](SECURITY.md), [ADR 0014](docs/adr/0014-rc-refusals-on-every-engine.md), and [ADR 0015](docs/adr/0015-a-run-takes-only-its-projects-editor.md).

## Files and uninstall

Files written outside the game project:
- `%LOCALAPPDATA%\Cluewren\`: `keys.env` if created (plain text, protected by the folder's ACL set to the current user only); `brain.json`, the announcement the plugin reads, present while a run is active.
- `%LOCALAPPDATA%\Cluewren-child\v2\<version>\`: a copy of the child runtime per version. Folders created by 0.1.0 directly under Cluewren-child remain until the Cluewren-child folder is deleted.
- The files `connect` writes (see Quick start step 3).

Files written in the game project:
- Unity: `UserSettings/Cluewren` (runs, traces, reports, run memory) and `Library/CluewrenCodex` (the project as text for the agent).
- Godot: `.godot/cluewren` (runs, traces, reports, run memory) and `.godot/cluewren_codex` (project summary; falls back to `user://cluewren_codex` when `res://` is not writable).
- Unreal: `Saved/Cluewren` (runs, traces, reports, run memory), `Saved/CluewrenCodex` (the Blueprint export), and `Saved/Logs/CluewrenCodexExport.log`.

To uninstall, run `cluewren connect claude --remove` or `cluewren connect codex --remove`. Then delete the folders listed above, the engine plugin from each project, and the binary folder. `--remove` does not delete `keys.env`, the `Cluewren-child` folder, or project data; 0.1.1 writes no backup, and a `config.toml.before-cluewren` left by 0.1.0 is the user's to delete.

## Upgrade

To upgrade:

1. Download `cluewren-0.1.1-win-x64.zip` and replace the binary folder's contents (or unzip to a new folder).
2. Upgrade the engine plugin in each project to 0.1.1. Brain 0.1.1 needs plugin 0.1.1 in the game project: a 0.1.0 plugin is refused with the text "update the Cluewren plugin in this project". Plugin 0.1.1 still works with a 0.1.0 brain. Upgrade both.
3. For Codex, run connect once to remove the automatic approval line 0.1.0 wrote:
   ```powershell
   .\cluewren.exe connect codex
   ```
4. For Claude Code after moving or reinstalling the executable:
   (a) Remove the current registration:
   ```powershell
   .\cluewren.exe connect claude --remove
   ```
   (b) If the old entry had settings you added (for example `-e CLUEWREN_SANDBOX=required`), `--remove` leaves it, so remove it with:
   ```powershell
   claude mcp remove --scope user cluewren
   ```
   (c) Register the new executable:
   ```powershell
   .\cluewren.exe connect claude
   ```
   (d) Add your own settings again.

## Status

Version 0.1.1, Windows 11 x64 only. Linux and macOS are not supported. The Unreal plugin is marked beta. See [CHANGELOG.md](CHANGELOG.md) for known limitations.

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
