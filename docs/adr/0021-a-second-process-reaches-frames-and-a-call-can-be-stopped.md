# 0021 A second cluewren process reaches a run's frames, and a call can be stopped

Status: accepted
Date: 2026-11-16
Amends: 0008

## Context

Under ADR 0008, the MCP process is the brain and holds a run's frames in memory. Codex starts a `cluewren mcp` process
of its own for a subagent, so its frame describer reached a brain with no run; ADR 0008 left that open.

A tools/call that the client gave up on kept playing: the server ignored notifications, an episode lasts up to 360 s,
and there was no tool to stop one.

## Decision

- While a run is open, its process listens on a named pipe that only the same user can open, named after the working
  folder's identity on disk (as folders are compared elsewhere), not its spelling: two spellings of one folder reach
  the same run. A `cluewren mcp` process with no run that gets `frames` asks over that pipe and hands the answer on. Only
  `frames` goes this way. Frames are still never written to disk.
- The `stop` tool and a client's cancellation (notifications/cancelled) act on a call that plays: probe, check_harness,
  run_harness (ADR 0018 says how the brain enforces it). For another call still going (start_run, for example), `stop`
  answers "<tool> is still going and can't be stopped: call wait."; for one that has already ended, "<tool> has ended:
  call wait for its result." A cancellation of the `wait` request stops the play it waits for. A stop that arrives while
  the call is still compiling or starting its child is kept, and the call answers "Stopped before it began." `wait` then
  gives what the call produced until then.

## Consequences

- If two agent sessions are started in the same folder with a run each, the second run's pipe name is taken, and its
  frames are reachable only inside its own process.
- The describer's process must be started in the same folder as the agent's, which is how Codex starts it.

## Alternatives rejected

- A service process that owns runs for every client: ADR 0008's reasons against a second long-lived process stand.
- Frames in a file for the second process to read: ADR 0003 keeps them off the disk.
