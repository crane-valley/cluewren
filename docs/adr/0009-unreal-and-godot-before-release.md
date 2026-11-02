# 0009 Unreal and Godot support are required for release, and nothing past them is planned

Status: accepted
Date: 2026-11-01

## Context

The rebuild plays Unity games only: the plugin, the Kit's engine class and the bench are Unity's. The owner also tests
games made in Unreal and Godot. Upstream has plugins for both (`plugin/unreal`, `plugin/godot`) and engine modules to
port from.

Three other directions were weighed on 2026-11-01: a networked bridge for Steam Deck and console development builds, and
a screen-and-input mode for shipped Steam and Epic games. A screen-and-input mode can't read game state, so decisions
would take seconds rather than the 0.14 s measured, and verdicts could rest only on what is on screen.

## Decision

- Ship with Unity, Unreal and Godot plugins. Both new plugins are required before release.
- The Kit gets an engine seam for what differs by engine, and each engine goes through the same ADR 0006 floor as Unity.
- Not planned: the networked bridge, Linux and console builds, and a screen-and-input mode. The bridge stays on loopback.

## Consequences

- ADR 0006 needs an Unreal and a Godot comparison before release, each with a game the change was not tuned on.
- The Windows-only and loopback-only limits stay limits of the product, not gaps to close.
