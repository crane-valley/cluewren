# 0008 The MCP process is the brain, for one run at a time

Status: accepted, which editor a run takes amended by 0015
Date: 2026-10-31

## Context

Upstream runs two processes for the user's own agent: `autopilot serve`, started by the editor plugin, holds the bridge,
and `autopilot mcp`, started by the agent, forwards calls to it over a local HTTP endpoint with a secret. ADR 0005 removes
the plugin's installers and its run request, so nothing in the editor starts a service any more.

## Decision

- `playtest-agent mcp` is the brain. The agent starts it, and there is no second process or control endpoint.
- It opens no port until start_run. start_run starts the bridge, writes brain.json and waits for the editor, whose plugin
  connects by itself once brain.json appears. When the run ends the bridge closes and brain.json goes, so a session that
  never playtests leaves no listener and no discovery file.
- Each run gets a new secret and a new JevClient.
- Calls past 40 s answer "still going" and `wait` gives the result, as upstream, because Codex gives up on a call after 60 s.
- Frames stay in the brain's memory, the last 160 MB, and leave only through the `frames` tool. Nothing is encoded to disk.
- The brain tells the plugin a run started and ended with upstream's run_started and result frames, which drive input
  isolation. `result` carries nothing, since the report is a file in the run's folder.
- The research clock stops once harness.cs exists, because the brain doesn't see the agent's file tools. Upstream stops it
  at the first write through its own file tools.

## Consequences

- Fewer moving parts and no local HTTP endpoint to defend.
- Two agent sessions can't run at once against one machine's brain.json: the later start_run takes over discovery.
- With two editors open, the first to connect is the one played. start_run does not yet check the project against the
  agent's working folder.
- Codex starts a new MCP process for each subagent, so its frame describer reaches a brain with no run. Upstream's proxy
  avoids this. Claude Code subagents share the connection and are not affected. Codex needs a fix before it is claimed.
- A batch-mode editor started with -PlaytestConnect stays up after the run, and whoever started it stops it.

## Alternatives rejected

- Keep upstream's two processes: a service with no one to start it, plus an endpoint, for the Codex describer alone.
- Start the bridge with the MCP process: every agent session in any project would hold a port and overwrite brain.json.
