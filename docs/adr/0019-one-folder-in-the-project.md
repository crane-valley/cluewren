# 0019 One folder in the project: .cluewren

Status: accepted, how the folder is refused amended by 0023
Date: 2026-11-16
Amends: 0003, 0014

## Context

Run memory was in UserSettings/Cluewren (Unity), .godot/cluewren (Godot) and Saved/Cluewren (Unreal), and the project
export in Library/CluewrenCodex, .godot/cluewren_codex and Saved/CluewrenCodex. .godot and Saved are caches developers
delete, and the agent's notes.md and lib.cs went with them. The brain decided which layout applied by looking for
project.godot or a .uproject, apart from what the connected plugin said its engine was. The export's name, codex, is
also the name of one of the two agents Cluewren supports.

## Decision

- <project>/.cluewren is used for the three engines: memory/ (notes.md, lib.cs, kit.txt), runs/<id>/ and export/.
- Whoever makes .cluewren first writes .cluewren/.gitignore with "*", so requests, reports and the project as text
  stay out of a repository unless the user edits that file (to share memory/ with a team, for example).
- The engine comes from the hello, not from files in the project.
- The export is called the export everywhere: folder, files, classes, the Unreal module and commandlet
  (CluewrenExport), and the kit reference.
- ADR 0014's C9 protects .cluewren, all of it, on the three engines; Godot keeps refusing res://.godot/ too.
- memory/kit.txt records the kit version a project's lib.cs and harnesses were last used with; a run started with
  another version says so in its briefing.
- The command-line flags that make a process connect are -CluewrenConnect, -CluewrenConnectTimeout and -CluewrenMap on
  Unity and Unreal, and --cluewren-connect, --cluewren-connect-timeout and --cluewren-map on Godot. The Godot autoload
  script is addons/cluewren/game.gd.

## Consequences

- Memory survives deleting .godot, Saved or Library.
- Version control other than git is the user's to configure for .cluewren.
- .cluewren is a dot folder: Unity and Unreal import nothing from it, and a C# Godot project does not compile the
  .cs in it.
- Folders 0.1.x left (UserSettings/Cluewren and the others) are not read or moved.

## Alternatives rejected

- Keeping one folder per engine and fixing only the names: memory would stay in two caches.
- Having the plugin tell the brain where the folders are: one fixed place needs no message and nothing to validate.
- Ignoring only runs/ and export/ by default: notes and helper code would be committed by accident.
