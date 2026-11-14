# 0016 The link bounds what a harness child queues, without tracking who queued it

Status: accepted
Date: 2026-11-14

## Context

The harness child is untrusted code in a sandbox. 0.1.1 bounded what it can make the brain hold inside its session
(line size, requests being served, statuses waiting). The link between the brain and the engine plugin was left
unbounded: frames the child had the brain forward queued without limit while the plugin did not read, the replay
window had no byte cap, and a resume went on silently when frames the plugin needed had left the window (issue 19).

A first attempt tracked, per child, every frame the link held (the queue, the frame being written, the replay
window), evicted per owner and recorded lost history per owner. Two reviews found more problems each time, all in
that accounting, and the attempt was dropped.

## Decision

The link keeps two plain limits and no notion of who owns a frame.

- What waits to be written to the socket is counted in bytes. Frames a harness child has the link send (a post, a
  request, a status) are taken only while less than 16 MiB waits. A request that is not taken fails at once, a status
  is dropped, and a post that is not taken ends the child's session. The brain's own frames (releasing input,
  pausing, ending a run) are always taken: their number is the brain's, and losing them would leave the game
  running with input held.
- The replay window is at most 1024 frames and 32 MiB. The link remembers the highest request id that left it. A
  resume whose plugin has not received that request ends the link, with a plain close after the welcome so the
  plugin ends as for any lost brain, instead of going on with a gap.

So what the link holds on behalf of a child is at most 16 MiB waiting plus 32 MiB kept for replay.

Repairs that came with it: request ids are assigned in queue order; a resume interrupted a second time keeps the
order and does not resend a request the plugin says it has; messages from the plugin that nobody reads are kept to
the newest 32; an episode mark for another episode than the one the host started ends the session.

## Rejected

- Per-owner accounting in the link: unbounded in what it has to get right (see Context).
- Refusing the brain's own frames at the same limit: a full queue then also lost the release of held input and the
  pause that end a run.
- An error frame for the refused resume: after the welcome, the Godot editor treats an error as a refusal before
  connecting and skips its end-of-connection work.

## Consequences

- Posts a child queued before its session failed are still delivered, and replayed on a resume, within the limits
  above.
- A status is lost while the plugin is 16 MiB behind.
- A plugin that stays away long enough for a request to leave the replay window loses the run on its return.
- The one message being written is buffered by the web server outside these counters.
