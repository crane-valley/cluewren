# Cluewren

Cluewren plays the game open in your Unity, Godot or Unreal editor for your coding agent (Claude Code or Codex). Ask your agent to explore the game for bugs, test it and report every problem found, verify a claim about how it works or looks, or reach a goal in play.

## How it works

Cluewren itself never starts or drives a language model and holds no language model key. It is an MCP server named `cluewren`, a skill, and a frame-describer subagent. Your coding agent bills whatever you logged in with (subscription or API key).

Your agent researches the game's code, tries things in the live game, and writes a C# harness for this game and request. A fast decision model (TypeSafe's Jev) makes the choices in play inside that harness. The agent looks at frames through a frame-describer subagent that sees the frames but not the claim, then reports what play showed, with evidence.

The plugin connects out to the Cluewren process on the same machine over a loopback WebSocket (127.0.0.1) with a per-start secret handshake.

## Requirements

- .NET 10 SDK
- Claude Code or Codex CLI
- A TypeSafe API key for the decision model
- One supported game engine:
  - Unity 6000.0 or later
  - Godot 4 (tested with 4.7.2)
  - Unreal Engine 5 (tested with 5.8; the plugin is Win64 only and you need a C++ toolchain to build it)

## Quick start

1. Clone and build Cluewren:
   ```bash
   git clone https://github.com/crane-valley/cluewren.git
   cd cluewren
   dotnet build -c Release
   ```
   The binary is `./src/Cluewren/bin/Release/net10.0/cluewren`.

2. Connect your coding agent:

   For Claude Code:
   ```bash
   ./src/Cluewren/bin/Release/net10.0/cluewren connect claude
   ```
   This registers the MCP server with `claude mcp add --scope user` (every project) and writes `~/.claude/skills/cluewren/SKILL.md` and `~/.claude/agents/frame-describer.md`.

   For Codex:
   ```bash
   ./src/Cluewren/bin/Release/net10.0/cluewren connect codex
   ```
   This adds a marked `[mcp_servers."cluewren"]` table to `~/.codex/config.toml` (backing up the old file as `config.toml.before-cluewren`), and writes `~/.agents/skills/cluewren/SKILL.md` and `~/.codex/agents/frame_describer.toml`.

   Use `--remove` with either command to take them out.

3. Install the engine plugin into your project (see [Engine plugins](#engine-plugins)).

4. Put your TypeSafe API key in place. Set the `TYPESAFE_API_KEY` environment variable, or add a line to `%LOCALAPPDATA%\Cluewren\keys.env`:
   ```text
   TYPESAFE_API_KEY=your-key-here
   ```
   The skill tells the agent never to ask you to paste a key into the conversation.

5. Open the game's project in its editor, start a new agent session, and ask, for example:
   ```text
   Use Cluewren to verify that the door opens with the red key.
   ```

## Commands

- `cluewren connect claude|codex [--remove]`: sets up your agent.
- `cluewren mcp`: the MCP server your agent starts.
- `cluewren run <run folder> [seconds]`: plays `<run folder>/harness.cs` once without an agent.

## Engine plugins

- Unity 6000.0+: `plugin/unity/com.cranevalley.cluewren` (package 0.1.0) - [install](https://docs.cluewren.dev/engines/unity/)
- Godot 4 (tested with 4.7.2): `plugin/godot/addons/cluewren` - [install](https://docs.cluewren.dev/engines/godot/)
- Unreal Engine 5 (tested with 5.8, Win64 only, beta): `plugin/unreal/Cluewren` - [install](https://docs.cluewren.dev/engines/unreal/)

## Privacy

What leaves the machine:
- Decision requests go to TypeSafe's API (https://api.typesafe.ai) with your TypeSafe key. They carry text about the game state: the goal, the situation, the options and recent actions. No pictures. While a run is active the client also pings the API about every 15 seconds when idle to keep the connection warm.
- Your coding agent talks to its own provider as it always does: it reads the game's code with its own tools, and frames reach its model through the frame-describer when it checks how something looks.

Nothing else leaves the machine. There is no upload, telemetry, accounts, sign-in or billing in Cluewren.

What stays local:
- Recordings, screenshots, traces and run memory stay on disk in the project (`UserSettings/Cluewren` for Unity, `.godot/cluewren` for Godot, `Saved/Cluewren` for Unreal).
- API keys are never sent to the plugin, logs or run records.
- The harness the agent writes runs in a Windows AppContainer (or under bubblewrap on Linux when available); elsewhere it is not sandboxed.

## Status

Early (Unity package 0.1.0; the Unreal plugin is marked beta). Tested on Windows 11 only.

## License and attribution

Licensed under Apache-2.0; see [LICENSE](LICENSE).

Cluewren contains material derived from Aura Autopilot (https://github.com/ramenvr/autopilot, commit 48966d1), licensed under Apache-2.0; see [NOTICE](NOTICE). The ports were rewritten in C# and leave out Autopilot's upload, sign-in and billing code.

## Documentation

- [Documentation](https://docs.cluewren.dev)
- [Japanese documentation](https://docs.cluewren.dev/ja/)
- [Landing page](https://cluewren.dev)
