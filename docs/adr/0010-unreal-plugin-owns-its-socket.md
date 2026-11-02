# 0010 The Unreal plugin owns its socket and crypto, and is Windows only

Status: accepted
Date: 2026-11-01

## Context

The brain refuses a WebSocket with an Origin header (ADR 0003), and UE's WebSockets module, through libwebsockets, sets one
(LwsWebSocket.cpp:804, read from the source, not tested on the wire). Core has an SHA-1 HMAC only, and the handshake is
HMAC-SHA-256 with random nonces.

## Decision

- `PlaytestSocket` speaks WebSocket over WinHTTP, and the handshake uses BCrypt for HMAC and nonces.
- The plugin is Win64 only, which ADR 0009 allows.
- Upstream's Unreal plugin answers reads through `rc` alone. Ours adds `find`, `transforms`, `pose`, `movement` and `pointer`,
  so the Kit's Game has one path for every engine.

## Consequences

- A port to another platform has to replace these two pieces first.
- The Kit's Unreal class holds only frames and names: nothing in Game depends on the engine beyond them.
- Any open editor connects to whichever brain is announced (ADR 0008). A test game connected to another run's brain this way
  on 2026-11-01 and was not played, so the known gap of two editors stays open.
