# 0011 Harness code passes no cancellation token

Status: accepted
Date: 2026-11-02

## Context

A harness had to take a `CancellationToken` in every override and executor and pass it to every read and controller.
One ArenaShooter harness named `ct` 45 times. Forgetting one left a read running after its option stopped, and the
token did nothing a harness could use: Tick already throws when the option is stopped.

## Decision

- Setup, Resume, Observe, Idle, Teardown and executors take no token. Harnesses and probes pass none.
- A read with no token uses the running option's, held in the AsyncLocal the option's wait counter already sits in, and
  outside an option the run's. Reading it allocates nothing.
- Kit methods keep their optional token, because the brain calls them outside any option.
- The kit reference shows the shorter forms: `game.Num`, `game.Text`, settings in the constructor, braces on the line.

## Consequences

- Harnesses and helpers written against the old signatures stop compiling. Nothing has been released.
- A stopped option's read ends with the option without the harness doing anything.
- This did not close the ArenaShooter time gap. See [docs/comparison-2026-11.md](../comparison-2026-11.md).
