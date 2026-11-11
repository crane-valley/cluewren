# 0014 rc refusals on every engine, and a container that can be required

Status: accepted
Date: 2026-11-11
Amends: 0004

## Context

ADR 0004 keeps `rc`'s abilities and says the container is no boundary against it. Before this, the Unity plugin refused
seven .NET namespaces, and the Godot and Unreal plugins refused nothing but Unreal's Play settings: one rc call reached
`OS.execute`, `ExecuteConsoleCommand` or a script's source. A business customer's review needs a claim it can check.

Two plan-review rounds tried to make the plugin side a complete filter. Each round found another route through a
reflective remote-call surface: loader calls, script source edits, a scene restart, object-typed arguments, property
paths. That surface is not a bounded domain.

## Decision

The owner decided on 2026-11-11:

- rc refuses the categories below on all three engines, with no security difference between engines: where one engine has
  a rule, the others have its equivalent, and what one allows in a category the others allow too.
- `CLUEWREN_SANDBOX=required` makes the container mandatory. Unset or `auto` keeps ADR 0004's fail-open fallback. With
  `required`, a platform with no container, a container that can't be created and a first call the child doesn't answer
  all fail the tool call, with the reason on stderr. Any other value stops `cluewren mcp` and `cluewren run` at start.

The claim: the rules below are enforced on every rc receiver, every object-typed rc argument (with the values a struct's
text or a default argument brings in) and every object along an rc property path. It is not a boundary. The routes
outside these rules are residual and listed below. Harness code is to be treated as running with the editor's rights;
the container limits what it can do directly.

A refusal answers status 403, "<what> is refused: <why>".

The categories, with the rule on each engine as shipped:

- C1 processes and the shell. Unity: System.Diagnostics, Application.OpenURL (and Device.Application's), UnityEngine.WSA.
  Godot: OS beyond its listed getters (execute, create_process, shell_open, kill...), LinkButton's uri. Unreal:
  KismetSystemLibrary LaunchURL, LaunchExternalUrl, QuitEditor.
- C2 files by path. Unity: System.IO, UnityEngine.Windows, Unity.IO, Unity.Loading, AssetBundle's loads from file, memory
  and stream, ScreenCapture.CaptureScreenshot, Profiler's log files, fonts from files, GraphicsStateCollection files,
  PlayerPrefs, Caching, Handheld movies, VideoPlayer.url, Apple, iOS, tvOS, NVIDIA and DedicatedServer. Godot: FileAccess,
  DirAccess, ConfigFile, ZIP, PCKPacker, XMLParser, FileDialog, CryptoKey, X509Certificate, GLTF, resource format loaders,
  savers and importers, TextServer, MovieWriter; Image, texture, font, audio and video loads and saves by path;
  ResourceLoader and ResourceSaver. Unreal: save-game slots (a slot name becomes a file path), render target and texture
  export and import, data and string table files, media opened from a file or URL, recording to a file, BlueprintPathsLibrary,
  BlueprintFileUtils, Interchange, image and GLTF export, trace files, automation mount points, movie scene capture, and
  writes to the properties that hold such a path or URL (FileMediaSource.FilePath, ImgMediaSource.SequencePath,
  StreamMediaSource.StreamUrl, the engine's screenshot folder and the others the census finds).
- C3 the network. Unity: System.Net, UnityEngine.Networking, Analytics, Purchasing, Unity.Networking, Unity.Netcode,
  Unity.Services, Ping, ClusterInput, WWW. Godot: HTTPRequest, HTTPClient, StreamPeer, PacketPeer (multiplayer peers and
  WebSocket among them), the TCP, UDP, UDS and DTLS servers, ENet, WebRTC, UPNP, MultiplayerAPI, IP. Unreal: OpenLevel (a
  level name can name a server), client travel, image download, Twitter, mobile patching, and the HttpBlueprint,
  WebBrowser and OnlineSubsystem packages.
- C4 code. Unity: System.Reflection, System.Runtime.InteropServices, Mono, System.Object's members but ToString, Equals and
  GetHashCode, SendMessage, SendMessageUpwards, BroadcastMessage, Invoke, InvokeRepeating, StartCoroutine(string), Burst's
  LoadAdditionalLibrary. Godot: Object's and Node's calls and writes by name (call, callv, set, get, propagate_call, rpc and
  the deferred and thread-safe forms), writing `script`, a Script object's own members but what the script declares,
  Expression, GDExtension, Thread, ScriptLanguage, ClassDB, Marshalls, Engine's singleton and script-language members,
  shader code. Unreal: KismetSystemLibrary's Set*PropertyByName family and timers by function name, a bound delegate in
  any converted value, Python.
- C5 editor scripting. Unity: the assemblies the editor alone compiles (UnityEditor, a game's Editor folders, editor
  packages) and Unity.* assemblies no player is built with. Godot: classes and singletons of the editor's API. Unreal: a
  class whose nearest native ancestor's package is flagged EditorOnly, Developer or UncookedOnly, which UHT sets for those
  module types.
- C6 host data. Unity: System.Environment (out of reach), the clipboard, deviceUniqueIdentifier, Microphone,
  WebCamTexture. Godot: OS's environment, command line, unique id and data folders, DisplayServer's clipboard,
  CameraServer, CameraFeed, microphone capture and recording, AudioServer's input devices. Unreal: the command line,
  device ids, AudioCapture.
- C7 consoles and command strings. Unity: UnityEngine has none. Godot: a game has none; Expression and EngineDebugger are
  refused. Unreal: ExecuteConsoleCommand, PlayerController.SendToConsole, PlayerInput.SetBind, CheatScript, console
  variable assets.
- C8 the user's settings. Unity: EditorPrefs, EditorSettings and PlayerSettings, through C5. Godot: ProjectSettings
  beyond its getters; EditorSettings through C5. Unreal: LevelEditorPlaySettings as receiver, argument or value, and
  writes to a config class's defaults.
- C9 what the harness wrote. Unity: UserSettings/Cluewren in rc string arguments and in a restart. Godot: res://.godot/
  in rc targets and arguments and in a restart, in the game and in the editor. Unreal: Saved/Cluewren, and /Temp/ which
  names it, in object resolution, arguments and a restart. Each resolves uid:// or a package path first and catches
  Windows 8.3 short names.

Each engine also has a census test: every reachable member taking a path-, URL-, address- or command-like string must be
refused or on a reviewed list with its reason, so an engine update that adds one fails the test. On Unity and Unreal it
covers writable string properties too.

A receiver is judged by its own runtime type, never by the type of the field or argument it came through, and so is
every object an argument converts to (a component a GameObject argument resolves to among them). Arguments are checked
after conversion as well as before, so a default argument or a value inside an untyped dictionary counts. A value nested
deeper than a check goes is refused, not passed. A
Godot property path is walked as the engine walks it, through objects, dictionaries and arrays, and every object on it is
asked.

Abilities upstream had that rc no longer has: on Unity, OpenURL, UnityWebRequest, AssetBundle loads, PlayerPrefs, calls
by name, a game's Editor-folder code and editor packages; on Godot, the singletons beyond the table, OS, ProjectSettings
and DisplayServer beyond their getters, calls and writes by name, a script's source; on Unreal, editor objects (Play
settings reads included), console commands, OpenLevel, save-game slots, timers and property writes by name, config
default writes.

Residual routes, not refused:

- Every engine: the game's own code, scripts and Blueprints, which may do anything; engine members the census doesn't
  name because they take no path-, URL- or command-like string; the game's own scenes and levels, which a restart opens.
- Unity: Resources and the build's scenes; UnityEvent.Invoke reaching the game's listeners.
- Godot: the members of the allowed singletons (AudioServer, RenderingServer, Engine); PackedScene data; emit_signal
  reaching the game's connections.
- Unreal: native functions in plugins the census didn't load, outside the refused packages.

Differences that remain, because the engines differ:

- Godot allow-lists its singletons and the getters of OS, ProjectSettings and DisplayServer, where Unity and Unreal refuse
  by type and member: Godot keeps process, file and host calls together on those singletons.
- Unity's SceneManager.LoadScene stays, as it opens only the build's scenes; Unreal's OpenLevel is refused because a level
  name can name a server.
- Godot alone has script source objects in reach, and property paths of several parts (`a:b`); Unity and Unreal take one
  member name.
- An object whose native class is refused is refused whole on every engine, the game's script or Blueprint on it too.

Alternatives rejected:

- A complete plugin-side filter, claiming harness code can't reach processes, files or the network through the editor.
  Two plan-review rounds kept finding new routes, so that claim would not hold.
- `required` as the default: it fails runs that ADR 0004 lets run with a note.
- Unreal left unfiltered and disclosed: the owner asked for no difference between engines.

## Consequences

- Tests: `dotnet test tests/Cluewren.Tests` for the brain; `godot --headless --path plugin/godot -s res://tests/rc_test.gd`;
  the package's EditMode tests in a project that references it (Unity batch mode, `-runTests -testPlatform EditMode
  -assemblyNames Cluewren.Tests`); `Automation RunTests Cluewren.Rc` in an editor with the plugin built by RunUAT
  BuildPlugin. The Unreal tests compile only with WITH_DEV_AUTOMATION_TESTS, and the Cluewren module is never built for
  Shipping.
- Cost on the game's main thread, measured per rc op on 2026-11-11 against main: Godot 4.7.2 reads take 6.3 us for 5.1,
  calls 9.7 to 11.7 us for 6.6 to 7.1, Props and Describe about 20% longer; Unity 6000.6 is within noise; Unreal adds
  about 0.6 us per op. Decisions are cached by class and member, so the cost is lookups, not ancestry walks.
- The reviewed lists need a look when an engine updates, which the census tests ask for by failing.
