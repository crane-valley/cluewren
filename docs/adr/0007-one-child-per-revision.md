# 0007 What one child per harness revision keeps, and how it is granted

Status: accepted
Date: 2026-10-31

## Context

ADR 0004 gives each harness revision and each probe a new child. Upstream's Game lives as long as the run, so its sonar
map, measured look scale and measured slide carry from one episode to the next, and a later walk knows the level. ADR
0006 does not allow losing that. A shared container SID would also let a later run read an earlier run's folder.

## Decision

- Before a child's reply, it writes what it learned to carry.json in the run folder: nav maps, look scale, slide, keys and
  level. The next child loads it. The file is the child's own, and the parent never reads it.
- Each run folder has its own AppContainer profile, named from a hash of the folder, and only that SID is granted the
  folder. The profile and the grant are removed when the host is disposed. The host holds a .container file in the run
  folder open while they may exist, so after a killed brain the next run in the project finds that claim unheld and
  removes them.
- The child's dll, its config and the kit are copied into a folder of their own under LocalAppData, named by their hash,
  and that folder is granted read and execute for all application packages (S-1-15-2-1). It holds no secret, and one
  grant is simpler than an entry per run on shared files. Nothing in the repository or the publish folder is granted.
- The child's environment is an allow-list. USERPROFILE and LOCALAPPDATA are on it because CreateProcess fails with error
  203 for a container without them. The container rewrites them, and the child cannot read the real folders.
- max_seconds stays upstream's length of play in game time. The wall-clock deadline of ADR 0001 is separate, starts before
  Setup and replaces upstream's wall-clock fallback. Either one ends the episode as inconclusive.

## Consequences

- A child killed before it replies loses what it learned since the last call.
- A self-contained publish needs the runtime folder in the child's folder or granted too. Only the framework-dependent
  path was run.
