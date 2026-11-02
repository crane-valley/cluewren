# 0001 C# brain on the Autopilot bridge protocol

Status: accepted
Date: 2026-10-31

## Context

Aura Autopilot (github.com/ramenvr/autopilot, Apache-2.0, reviewed at commit 48966d1) plays a game through an editor
plugin and a separate brain process. The brain is about 17k lines of Python. The Unity plugin is about 21k lines of C#.
The owner decided to rebuild it rather than run it, with the Python parts written in C#, because it wants one language
across its Unity, Godot and Unreal tooling (Gauntlet, UBT and UBA are C# as well).

Facts that bound the decision:

- Only Unity 6 (6000.3, 6000.6) is installed on the development machine.
- The Unreal plugin must remain C++ and the Godot addon is GDScript upstream; C# does not replace them.
- The plugin and the brain already meet at a small versioned WebSocket contract (bridge.py, PROTOCOL = 1).
- Autopilot's KIT.md says every read waits for a game frame, and upstream's Unity `view` walks every Renderer with
  visibility checks and raycasts on the main thread. Those costs stay whatever the brain's language, so .NET alone only
  lowers the brain's own CPU and allocation share.

## Decision

- Write the brain as a .NET process targeting net10.0.
- Keep bridge protocol 1 on the wire, so the plugin is a fork rather than a rewrite.
- Fork the Unity plugin first and strip it (ADR 0003). Godot and Unreal are deferred until the Unity path works end to
  end.
- Build in vertical slices, each usable without the next:
  1. Bridge and tick loop running a hand-written harness, with no language model and no Jev.
  2. Jev decisions through a hand-written HttpClient call to POST /v1/systemone.
  3. Harness authoring by the user's coding agent.

Better performance than the Python brain is a requirement, reached where the time goes:

- One observation's reads travel as one batched request answered in one game frame, never as a round trip per read.
- Controllers step on the latest observation while the next one is in flight, so a slow read never stalls input.
- Observations name what they need. The plugin reads those objects and does not scan every Renderer unless a harness
  asks for a view, and resolved members are cached.
- The brain's hot path uses System.Text.Json source generation and avoids per-tick allocation.
- Run records are written off the tick thread.
- Judged against upstream on the same scene and machine: observation age at decision, observation-to-input latency,
  Unity main-thread time per frame, GC allocation in the game, FPS, and brain CPU and allocation per tick.

The Jev client is written by hand because TypeSafe ships only Python.

Slice 3 follows ADR 0005 for the language model and ADR 0004 for the harness. ADR 0006 judges the finished path.

Every run has one wall-clock deadline covering setup, observation, control and Jev, and a limit on Jev decisions.
Reaching either first cancels work in flight and releases held input, then reports the run as inconclusive. The user's
agent owns its own model limits (ADR 0005).

## Consequences

- C# for the brain and the Unity plugin, and a self-contained publish with no uv, Bun or Python. The Godot addon stays
  GDScript and the Unreal plugin C++ unless a later ADR ports them.
- Upstream fixes to the plugin and to KIT.md are merged by hand. The brain gets none of upstream's Python fixes.
- Copied plugin code stays under Apache-2.0: keep LICENSE and a change notice in every modified file (section 4(b)).
- framedash product-direction.md holds autonomous AI testing behind a separate experiment. This repository is that
  experiment, so nothing here is a framedash commitment.

## Alternatives rejected

- Fork the Python brain: least work, but it does not meet the language decision.
- Rewrite the plugin as well: 21k lines of engine integration with no benefit to the language decision.
- Run the brain inside the Unity editor: script reloads on entering play mode would kill it, and it would not serve
  Unreal.
