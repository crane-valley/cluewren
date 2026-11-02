# 0003 Local only, with no upload path

Status: accepted
Date: 2026-10-31

## Context

Autopilot sends recordings, screenshots, agent traces and memories to app.tryaura.dev unless the user turns sharing
off. It is on by default, for runs with the user's own keys too (telemetry.sharing, training.py, aura_memory.py). The
plugin also carries sign-in, billing, an installer that fetches uv and Bun, and a link for Aura's own plugin.

## Decision

Sharing is not off by default. It does not exist. The brain and the plugin fork contain no upload, telemetry, sign-in,
billing or remote memory code, and no installer.

The only outbound connection the brain makes is to Jev.

Pictures of the game are kept, because a request about how something looks cannot be judged from state and dropping
them would lower accuracy. Runtime/Media.cs and its `record` and `capture` ops stay. Recordings remain on this machine
in the run's folder. Frames reach only the language model the user configured, through a frame-describer subagent that
sees the frames and not the claim, as upstream's does, for visual requests and to diagnose a stalled run. A per-project
setting turns frames off at the cost of that accuracy. No other party ever receives them.

Removed from the plugin fork: Runtime/Aura.cs, the Aura parts of Runtime/Account.cs, Editor/HostLink.cs, the installer
paths of Editor/AgentService.cs, the Account and sharing setup steps, usage billing, the uninstall of remote copies, and
the `codex_manifest` and `codex_read` ops, since the brain and the agent run on the same machine as the project. The
text export of scenes and assets stays.

The plugin keeps connecting out to a brain on this machine. The brain listens on 127.0.0.1 on a random port, announces
it in a discovery file with a per-start secret, and refuses any request with an Origin header. Autopilot checks loopback,
but a browser page on the same machine is loopback too, and its WebSocket carries an Origin.

The secret never travels. Both sides prove they know it: the hello carries a plugin nonce, the welcome answers with a
brain nonce and an HMAC-SHA256 over both, and the plugin answers with its own HMAC before either side acts on any other
frame. Each MAC input starts with a fixed role label, "brain-proof" or "plugin-proof", so one side's proof cannot be
reflected back as the other's. A stale discovery file whose port another process has taken therefore gets no secret and is not trusted as a
brain. The handshake is required, so this plugin and upstream's do not interoperate.

The plugin parses the URL before every connection and refuses anything but ws:// to 127.0.0.1 or [::1] with no userinfo.
There is no URL setting for a remote brain.

The discovery file and its folder are owner-only (an explicit ACL on Windows, mode 0600 and 0700 elsewhere), written
through a temporary file with the same ACL. The plugin checks the owner before trusting it, so another local user can
neither read the secret nor redirect the plugin.

The user's agent reads source with its own tools, so the brain cannot limit or list what reaches that agent's provider
and claims nothing about it (ADR 0005).

API keys stay in the brain process, read from the environment or a user-scoped file, and are never sent to the plugin
or written to logs or run records. The harness child gets none, and the AppContainer keeps it from reading them from
disk (ADR 0004). Run records stay on disk in the project's ignored folders.

## Consequences

- No remote team memory and no shared learning across machines.
- Nothing to audit for what leaves the machine except the model calls. Those do carry the game's facts, and in slice 3
  its code, to the provider.
- The handshake is a small plugin change on top of upstream's hello and welcome.

## Alternatives rejected

- Keep upload with a default-off flag: dead code, one setting away from sending data.
- Keep upload behind a compile symbol: same, and it still has to be maintained.
