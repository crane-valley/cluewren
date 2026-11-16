# 0017 Editors advertise and the brain invites one

Status: accepted
Date: 2026-11-16
Supersedes: 0015
Amends: 0008

## Context

Under ADR 0008 and 0015, there was one brain.json per Windows user; every open editor read it and decided for itself,
by comparing path text, whether the run was for its project. That comparison existed three times (C#, GDScript, C++),
with a table of 19 vectors copied into each engine's tests, and the brain compared again by file identity.

This left known limits: one run per user at a time (a second start_run took the announcement over); an editor that
opened its project through a subst drive, an 8.3 name or another junction did not match; Godot started powershell.exe
for every brain.json to check its owner; every open editor re-read brain.json once or twice a second.

A refusal by the brain before the welcome carried a reason, but the Unity and Godot plugins showed "it didn't prove it
is this machine's brain" for every such frame, so "update the plugin" and "another project" never reached the user.

## Decision

- An editor's plugin writes %LOCALAPPDATA%\Cluewren\editors\<pid>.json when it loads: pid, id (32 hex characters,
  random, made once per process and kept across script and domain reloads), engine, engine_version, plugin_version,
  protocol_min, protocol_max, project_dir, host. It holds no secret and is deleted when the editor quits.
- start_run and `cluewren run` read those adverts. One whose process is gone, or whose file is older than the process
  that has its pid (5 s slack), is deleted. An advert whose process the brain is not allowed to ask about (an editor run
  as administrator) is left alone, not deleted. The brain retries deleting an invite file that an editor has open for a
  moment (up to 20 tries 10 ms apart). Each remaining project_dir is compared with the run's folder by file identity,
  the comparison ADR 0015 introduced (volume serial and file id on an open handle, containment by walking parents).
  `related` and `exact` keep their meaning.
- If there is no editor of the project, start_run waits up to its 60 s for one to appear, then answers with the reason
  and the editors that are open. Editors of more than one project inside the agent's folder produce an error that
  lists them and asks for `project`. With several editors of one project, the newest advert is chosen. A plugin whose
  protocol range does not include the brain's produces an error that says to install the same release of both.
- The brain then binds the run to the chosen folder: the handle and identity taken when the advert was matched become
  the bridge's scope. A hello from any other folder is refused, also one inside the folder the agent works in.
- It writes %LOCALAPPDATA%\Cluewren\invites\<editor pid>.json, owner-only as brain.json was: url, secret, pid, born
  (the brain process's start time), started, protocol, editor_id (the advert's id). Looking at an existing invite,
  removing a dead brain's, writing and withdrawing happen under a lock file beside the invite, opened without sharing,
  so two brains cannot both take one editor. The brain gives an invite that replaces one still there a modified time in
  a later whole second, so a plugin that reads times in whole seconds tells them apart without reading them. An invite
  whose brain is alive (same pid and same start time) makes start_run answer that the editor is already in a run.
- A plugin reads only invites\<its own pid>.json, checks owner and reparse points as it did for brain.json, ignores an
  invite whose editor_id is not its own, and connects. It has no project filter.
- The handshake is ADR 0003's and 0015's: the hello's mac over project_dir is checked before anything the peer sent
  touches the file system. Protocol is 2.
- A refusal before the welcome is an error frame with a code (protocol, not_authenticated, project, resume, hello,
  proof). The plugin shows its own fixed text for the code, never the brain's message, since the brain has not proved
  itself yet; an unknown code is shown only if it matches [a-z0-9_]{1,32}, otherwise a fixed text. The close that
  follows carries the same code as its reason (WebSocket status 1008); a close with status 1008 alone before the
  welcome, without the error frame, is read as the refusal its reason names in all three plugins. The reason carries the
  code because Godot's socket can drop a text frame that arrives together with the close; all three plugins read it.
- docs/PROTOCOL.md is the reference for the files and frames; the three plugins and the brain implement it.

## Consequences

- Runs on different projects can go on at once, each with its own brain.
- A subst drive, an 8.3 name or a second junction match, because the brain compares identity and no plugin compares
  text.
- An editor nobody invited opens no socket and does nothing but look for one file once a second.
- An advert left by a crashed editor whose pid another program now has gets an invite nobody takes; start_run reports
  after its 60 s that the editor did not connect. The editor_id keeps a different editor with that pid from taking it.
- Processes of the same Windows user remain trusted, as in ADR 0015: they can write adverts and read invites.
- Brain 0.2.0 and plugin 0.2.0 talk only to each other.

## Alternatives rejected

- The plugin listens and the brain dials: that would require a WebSocket server in three engines, one of them without a
  library for it.
- brain.json per run, keeping the plugin-side filter: this allows concurrent runs but keeps the comparison in three
  languages.
- A process start time in the advert to tell process generations apart: GDScript cannot read its own, so the engines
  could not do the same; the id in the invite gives the property that matters.
- A named mutex for the invite: it is per logon session and the files are per user.
