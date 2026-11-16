# 0023 The data folder is refused by its name

Status: accepted
Date: 2026-11-16
Amends: 0014, 0019

## Context

Refusal category 9 (ADR 0014, ADR 0019) was first decided by text under the project root, which missed other names of
the root; it was then decided by opening the path and comparing folder identity, and three review rounds on that kept
finding new cases (junctions below the folder's top, UNC paths opened by the check, symbolic links leading to another
machine, volumes without a drive letter, stale verdicts): the check itself had effects (a connection to another machine
in the user's name, the main thread waiting) and no end of cases.

## Decision

A string from harness code is refused (status 403) when, read as a path, one of its segments names the data folder:
- Every string from harness code that the plugin checks (rc targets, string arguments and values, names, a session's
  map) goes through the rule whether or not it holds a separator, a ':' or a '%'; ".cluewren" alone and
  "%2Ecluewren%2Fruns%2Fx" are refused.
- Segments: the string split at every '/' and '\'.
- A segment names the data folder when, with a leading drive spec taken off first (refusing drive-relative paths
  such as "C:.cluewren\x"; Unreal takes it off any segment), cut at its first ':' (NTFS stream or attribute notation)
  and with dots and spaces trimmed from its end, it equals ".cluewren" ignoring case (ordinal), or has the shape of an
  8.3 short name Windows could give the folder. Godot applies the same rule to ".godot" as well as ".cluewren".
- The 8.3 short name is not asked of the system: asking the system needed a process or a file system call per check,
  a remembered answer could be wrong, and Godot's listing went through cmd, which expands %NAME% in a project's path.
  Instead, a segment, cut at ':' and trimmed as before, names the data folder when it has the shape of an 8.3 name
  Windows could give the folder: no '.', at most 8 characters, begins with the first two letters of the folder's name
  without its leading dot ("CL" for .cluewren; Godot also "GO" for .godot), ignoring case, and ends with '~' and one or
  more digits. That covers what Windows generates (CLUEWR~1 to ~4, then two letters, four hexadecimal digits and ~N)
  without knowing which one it gave; it refuses a few other names of that shape (CLIENT~1), which is accepted.
- The check is made on the string as given and, when it holds a '%', also on the string percent-decoded once.
- An engine path is turned into text first only where the engine does that from its own tables, without the file
  system: Godot uid:// to res://, an Unreal long package name to a file name through the mount points. The rule is
  applied to both forms.
- No comparison with the project root is made at all. Any path with such a segment is refused under whatever drive,
  share, device or short name of an ancestor it is reached.
- No file or folder the string names is opened, listed, stat-ed, hashed or resolved. No identity comparison, no final
  path, and no cache of verdicts that depend on the file system.

A session's map is harness-chosen text too: a map is a level's name or a path inside the project as the engine names
it; anything else is answered 404 without any file system or network call:
- Unity: a ':' anywhere, a leading separator, or a ".", ".." or empty segment makes a map none of the project's. The
  command-line map of Unity (-CluewrenMap) is the user's own and is not restricted.
- Godot: a res:// map is a scene only when every segment after res:// is a plain name (no "." or "..", no empty
  segment, no '\' or ':'), uid:// is turned into its res:// path through the engine's table first, or a scene's file
  name without '/', '\' or ':'; an unknown map is answered 404.
- Unreal: an allow-list: a map is taken only when its text is made of letters, digits, '_', '-', '/' and at most one
  '.' (an object part, or .umap on a short name) and it is found among the project's levels in the engine's asset
  registry (levels under /Game and the project's plugins); the engine is given the package name found there, never the
  string as sent (keeping out URL options such as "Arena?listen" or hosts to travel to); an unknown level answers 404
  in the editor as in a built game; -CluewrenMap on a built game's command line goes through the same check.

The same principle is applied twice more:
- Godot's rc refuses the addon's own scripts by path text under every spelling and a Script object by the source it
  already holds in memory, and reads no file to judge a string argument.
- Godot tells invites apart by modified time and size without reading them, and the brain writes an invite that
  replaces one still there with a modified time in a later whole second.

## Consequences

- No file system access on behalf of a harness-chosen string.
- Other names of the project root no longer matter because no root is compared.
- Links into the folder from outside (junction, symbolic link, subst drive, hard link) are left to the user, whom the
  protocol trusts, since harness code can write only inside the folder.
- An unknown or outside map is answered 404 and costs no file system or network call.
- Some paths outside the project with such a segment are refused too (for example C:\other\.cluewren\x).

## Alternatives rejected

- Identity comparison: opening the path has effects of its own (connections to remote machines, blocking the main
  thread) and unbounded cases.
- Refusing every 8.3-looking segment anywhere: it refuses ordinary paths such as a user profile's short name; the shape
  checked here is its narrow form (only names that begin with the folder's two letters).
