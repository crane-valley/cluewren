# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-11-11

Cluewren is an MCP server (`cluewren mcp`), a skill and a frame-describer subagent that let your coding agent (Claude Code or Codex) playtest the game open in a Unity, Godot or Unreal editor through engine plugins over a loopback WebSocket. First public release.

### Added

Release assets:
- `cluewren-0.1.0-win-x64.zip`: the application binary (`cluewren.exe` and its runtime files), `LICENSE`, `NOTICE`, and `THIRD-PARTY-NOTICES.txt`.
- `cluewren-unity-0.1.0.zip`: the Unity package (unzips to `com.cranevalley.cluewren/`).
- `cluewren-godot-0.1.0.zip`: the Godot addon (unzips to `addons/cluewren` in the project root).
- `cluewren-unreal-0.1.0.zip`: the Unreal Engine plugin source (unzips to `Cluewren/`; built by the user with their engine, Win64 only).
- `cluewren-0.1.0.cdx.json`: CycloneDX SBOM of the application.
- `SHA256SUMS.txt`: SHA-256 checksums of the other release assets.

Install instructions: https://github.com/crane-valley/cluewren#quick-start

### Requirements

- Windows 11 x64 only (Linux and macOS are not supported).
- ASP.NET Core Runtime 10 (x64), which includes the .NET runtime. The .NET 10 SDK is needed only when building from source.
- Claude Code or Codex CLI.
- A TypeSafe API key for the decision model (TypeSafe's Jev). TypeSafe is a separate service with its own account, pricing and terms; Cluewren grants no rights to it.
- One supported engine: Unity 6000.0 or later, Godot 4 (tested with 4.7.2), or Unreal Engine 5 (tested with 5.8, Win64 only, needs a C++ toolchain to build the plugin, marked beta).
- No administrator rights are needed.

### Security

- Remote call (rc) refusals on every engine: The plugins refuse the same nine categories on Unity, Godot and Unreal as defense in depth. A game's own code stays reachable. Treat harness code as running with the editor's rights. See [ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md) and [SECURITY.md](https://github.com/crane-valley/cluewren/blob/main/SECURITY.md).
- Model-written C# harnesses run in a child process inside a Windows AppContainer with no network access and write access limited to their run folder.
- `CLUEWREN_SANDBOX`: Unset or `auto` runs in the container and falls back to a plain child process with no keys in its environment if the container cannot be created. Setting `CLUEWREN_SANDBOX=required` refuses to run harness code without the container (the tool call fails with the reason).

### Known limitations

- Windows 11 x64 only. Linux bubblewrap and macOS code paths exist in code but are untested and unsupported.
- The Unreal plugin is beta and source-only.
- With two editors open, either may attach to the announced brain, and a second agent session takes over the announcement (see [ADR 0010](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0010-unreal-plugin-owns-its-socket.md) and [ADR 0008](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0008-mcp-process-is-the-brain.md)).
- Unity development builds include the plugin runtime (inactive unless started for a playtest); release builds do not. Unreal Shipping builds never include it. Godot release exports carry the addon files but never activate them.
- Unmeasured as of release ([ADR 0006](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0006-autopilot-is-the-floor.md)): a comparison on an untuned Unity game; decision-model token and subscription usage; frame rate on Godot and Unreal.
- rc refusals add a few microseconds per remote call on Godot ([ADR 0014](https://github.com/crane-valley/cluewren/blob/main/docs/adr/0014-rc-refusals-on-every-engine.md)).
- Four Unity input-isolation tests fail in batch mode on the tested editors; the cause is not yet found.

### Verification

Check the SHA-256 hash against `SHA256SUMS.txt`:
```powershell
(Get-FileHash -Algorithm SHA256 .\cluewren-0.1.0-win-x64.zip).Hash.ToLowerInvariant()
```
Compare the hash with the line for that file in `SHA256SUMS.txt`.

Verify GitHub build provenance attestations:
```bash
gh attestation verify <file> --repo crane-valley/cluewren
```

Review the CycloneDX SBOM: `cluewren-0.1.0.cdx.json`.

[0.1.0]: https://github.com/crane-valley/cluewren/releases/tag/v0.1.0
