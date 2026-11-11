# 0006 Autopilot is the floor on every axis

Status: accepted
Date: 2026-10-31

## Context

The owner's requirement: as the later design, this must beat Autopilot in general, and rising cost, falling performance
or falling accuracy against it are not acceptable. Security and removal of data sharing are added on top, never paid
for with those. Design intent cannot show this. Only a measurement on the same work can.

## Decision

Autopilot at commit 48966d1 with sharing off is the baseline. The same prompts run on both, on the same machine and on
copies of the same game, from cold and from warm memory, both through the same user CLI and MCP path, since upstream's
own bench runs its agent inside the brain and would compare the agent path instead of the system. Visual checks, FPS and
main-thread time are compared in a rendered editor run on both sides.

Measured:

- Cost: USD per run at list price, from tokens per model split by input, output, cache write and cache read, plus Jev
  tokens, and subscription usage where the run used one.
- Speed: time to verdict, harness attempts, observation age at decision, observation-to-input latency, Unity main-thread
  time per frame, GC allocation in the game, FPS.
- Accuracy: correct verdicts on prompts with known answers, goal completion rate, findings confirmed by hand.

Rules:

- At least one game the change was not tuned on, as upstream's CONTRIBUTING asks for prompt changes.
- The finished path passes when no measure is worse than the baseline beyond run-to-run noise, established by repeating
  unchanged runs. A worse measure blocks release, unless the owner accepts that one explicitly. Earlier slices are not
  held to this.
- Autopilot's features that serve those measures stay unless a measure shows they do nothing: memory across runs,
  harness revision, reflex controllers, navigation map, plans, visual checks, detectors.
- Security work that lowers a measure is redesigned, not accepted.

## Consequences

- The comparison needs the owner's game copies, and both Jev and model usage.
- Results replace the design's claims. Until they exist, "better than Autopilot" is a target and not a fact.

## Results 2026-11

- [docs/comparison-2026-11.md](../comparison-2026-11.md)
- Unity, met: token cost, decision latency, and frame rate or main thread within noise, with visual not rerun after the
  plugin fixes.
- Every verdict and goal correct on both sides, while the exploration sample is too small to call and the buffered
  tap miss stands.
- Unity: warm clear and warm door are now better than Autopilot on time and cost.
- Godot and Unreal met on cost, time and accuracy on tuned games and on the untuned Platformer 2D.
- ArenaShooter accepted by the owner on 2026-11-02, with time not shown to be no worse: Autopilot was faster in 9 of 10
  randomized blocks (mean 19.9 s, interval -4 to 44) before the Shoot fix; after it, 6 balanced blocks give mean
  -5.1 s, interval -35 to 24, median -3.9 s, ours faster in 3 of 6, cost $0.66 against $0.71.
- Accepted by the owner on 2026-11-11 for release 0.1.0, and still unmeasured: no untuned Unity game; Jev tokens and
  subscription usage; frame rate on Godot and Unreal. The changelog lists them as known limitations.
- The rc refusals of [ADR 0014](0014-rc-refusals-on-every-engine.md) add time per rc op on Godot (measured there). Frame
  rate in a game was not re-measured after them.
- [ADR 0009](0009-unreal-and-godot-before-release.md) requires Unreal and Godot comparisons before release.
