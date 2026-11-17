# 0022 A build has the bridge only when asked

Status: accepted
Date: 2026-11-16

## Context

The engine plugin sits in the user's game project so the editor can be driven. The question is what of it ends up in a
game the user builds and ships, and what such a build does.

Before this release a Unity Development build carried the bridge automatically: the runtime assembly's define
constraint was "UNITY_EDITOR || DEVELOPMENT_BUILD || CLUEWREN_PLAYTEST". A studio that handed a Development build to
testers shipped the bridge without having asked for it.

## Decision

- Unity: the runtime assembly is compiled into a player build only when the project sets the scripting define
  CLUEWREN_PLAYTEST. plugin/unity/com.cranevalley.cluewren/Runtime/Cluewren.asmdef has the define constraint
  "UNITY_EDITOR || CLUEWREN_PLAYTEST", and Editor/BuildHook.cs (Included) checks only that define before it adds the
  build info to the first scene. The build hook reads the scripting defines of the build's own named target (a
  dedicated server build its own) and the extra defines given to that build, so the build info is in exactly the builds
  the runtime is compiled into. A Development build without the define has none of the runtime.
- Unreal: the runtime module Cluewren is denied in Shipping (TargetConfigurationDenyList in Cluewren.uplugin), and
  Source/Cluewren/Private/CluewrenModule.cpp makes a Shipping compile of it an error (#error under UE_BUILD_SHIPPING).
  The two other modules, CluewrenEditor and CluewrenExport, are of type Editor. Development and Test builds do contain
  the runtime module.
- Godot: the addon is GDScript in the project, and its autoload (addons/cluewren/game.gd, registered by plugin.gd) is
  part of an export; this release does not strip it. In a release export (OS.is_debug_build() false) game.gd returns
  at once from _ready and does nothing, flag or not. In a debug export game.gd runs and waits for the connect flag
  like the other engines.
- On every engine a built game that has the bridge does nothing with it unless it was started with the connect flag:
  -CluewrenConnect for Unity and Unreal, "-- --cluewren-connect" for Godot. Only then does it write its advert and look
  for an invite; otherwise it writes no advert, opens no socket and connects nowhere (Runtime/Bootstrap.cs,
  UPlaytestSubsystem::Initialize in Source/Cluewren/Private/PlaytestSubsystem.cpp, game.gd).
- The connect flag is the user's own request, so with it a process prepares itself before an invite is validated;
  it opens its socket only after its invite has passed its checks, and makes no input devices before a verified
  welcome. See docs/PROTOCOL.md for the per-engine list.
- Batch and unattended processes are treated the same way: a Unity batch-mode editor advertises only with
  -CluewrenConnect or when started through -executeMethod Cluewren.Editor.Headless.Run (Editor/EditorHost.cs); an
  unattended Unreal editor only with -CluewrenConnect, and a commandlet never.
- The brain's address never comes from a flag, only from the invite file (ADR 0017, docs/PROTOCOL.md).

## Consequences

- Playtesting a Unity build now needs someone to add CLUEWREN_PLAYTEST to the project's scripting defines.
- Unreal Development and Test builds still carry bridge code that stays inert without the flag, and Godot exports,
  debug or release, still carry the addon; this is accepted for now.
- Left for later: removing the addon from a Godot export (an export plugin that drops the autoload and the addon's
  files), and making the Unreal runtime module opt-in for non-Shipping builds.

## Alternatives rejected

- Keeping Development builds automatic in Unity: a build would carry a remote-control surface nobody asked for.
- Stripping everything from every build and supporting only the editor: playtesting a built game is a use the connect
  flags exist for.
