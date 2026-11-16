# 0018 What harness code may touch

Status: accepted
Date: 2026-11-16
Amends: 0004, 0007

## Context

ADR 0004 put model-written harness code in an AppContainer child, and ADR 0014 says to treat that code as running with
the editor's rights through rc. In between, three things let the child reach what the brain and the agent rely on: the
container's SID had Modify on the whole run folder, which also held INSTRUCTIONS.md, REQUEST.md, prompt.txt and
report.json (read into later runs' briefings), harness.cs (copied into later runs by use_harness) and the compiled
assemblies the next child loads; the brain forwarded any op name from the child to the plugin, so the child could stop
the recording a visual verdict rests on, or end the game; and the report's gate ("at least 3 decisions") used the
child's own count.

There was no single statement of who is trusted with what.

## Decision

- There are three tiers. T0: the brain, the engine plugin and the user's own agent, which act for the user. T1:
  harness code (the agent wrote it, and game content may have steered it). T2: other users' processes and browser
  pages.
- T1 can: read its run's folder and load the assemblies there; write only under runs\<id>\work (its traces, what it
  carries to the next child, temporary files); ask the game for the ops on a list; and ask for decisions within the
  run's limit. Through rc it acts in the editor with the editor's rights, within ADR 0014's refusals.
- The container's SID gets ReadAndExecute on the run folder and Modify on work only. The child's working directory is
  work. In the container Windows gives it the container's own temporary folder; a plain child's is under work.
- The container is kept once it worked: in automatic sandbox mode (CLUEWREN_SANDBOX unset or auto) a run falls back
  to a plain child only while no contained child of that run has answered; after one has, a container that can't
  be made again fails the call instead of falling back to a plain child. What an earlier child left in the work
  folder (carry.json) is read by the next child only after its first answer and is ignored whatever is wrong with
  it.
- The ops a child may send are: rc, find, transforms, pose, movement, view, watch, screen, ui, ui_act, key, input,
  move, pointer, pause, context, levels, ping, and session with action restart. record, capture, detectors, timestep
  and session start, stop and status are the brain's. A refused request is answered with status 403 and the session
  goes on; a refused post is dropped. A restart whose map names .cluewren is refused by the brain as well as by the
  plugin.
- The gate counts, in the brain, per episode: a decision counts toward the report's gate (at least 3 decisions in an
  episode) only when the decision model's answer, read as the kit reads it, gives an offered option a probability
  above zero (and the request offered two or more options by different names). The report uses the smaller of that and
  the child's number.
- Which pictures belong to an episode is decided by the brain: an episode begins where the brain saw the level start
  over (the harness's restart answered) or, for play that goes on from where it is, just before the call is sent; it
  ends where run_harness returned. A harness's own marks can't move the beginning later or the end earlier; a probe
  or a check can't mark an episode at all (a mark outside an episode ends the session); an episode that was numbered
  and not played leaves no pictures to the one that gets its number next.
- Stopping an episode (the new stop tool, a cancelled call, the run's time limits) does not rest on the harness child:
  the brain sets the end after 15 seconds before it asks the child to stop, and asks it on another thread, so a child
  that does not read its input cannot hold the stop up. If the child has not stopped by then, the brain ends the
  session itself, lets go of the keys and pauses the game.
- T2 is ADR 0003's and 0017's: owner-only invites, the handshake, loopback only, no Origin.

## Consequences

- A trace is the child's word. The brain makes no decision from files under work; the agent reads them as output.
- The plain-process fallback has none of the file limits, as before; CLUEWREN_SANDBOX=required refuses it.
- A harness that called game.Request("record", ...) or ("capture", ...) gets 403.
- rc stays what ADR 0014 says: not a boundary.

## Alternatives rejected

- Leaving the gate to the child's count: the number the report rests on was the word of the code being judged.
- Ending the session on a refused op: a harness that tries one op too many loses the episode for no gain in safety.
- A complete filter on rc: ADR 0014 already found that domain unbounded.
