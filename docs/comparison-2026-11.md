# Comparison 2026-11

## Scope and method

This record is the comparison called for by ADR 0006.

- Baseline: Autopilot 48966d1 with sharing off.
- Product: Playtest Agent.
- Currency and time: USD at list price, seconds from the run's report clock, all on the same machine, editors rendered.
- Method: same claude CLI plus MCP path on both sides, same request text, alternating order.
- Games: FPS Microgame and 2D Platformer, copies not the owner's projects.
- Requests: ammo, jetpack, clear on FPS Microgame; doublejump, door, visual, explore on 2D Platformer.
- Memory: cold and warm memory.

## Time and cost totals

- Final 10-pair cold pass (before the later fixes): Playtest Agent 2130 s, $9.40. Autopilot 2594 s, $12.08. Earlier
  batches of the same requests: Autopilot 2068 s, $10.34, which shows run-to-run noise of about 25 percent in time and
  17 percent in cost on Autopilot alone.
- Warm memory, 10 pairs: Playtest Agent 1016 s, $5.52 vs Autopilot 980 s, $6.38. Two runs shared one memory seed, so
  noise is understated.
- Confirming pass, 15 pairs on fixed code: Playtest Agent 3227 s, $14.43 vs Autopilot 3272 s, $16.60. All verdicts
  correct on both sides.

## Confirming pass per request

- Harness attempts in the confirming pass, 27 on Playtest Agent and 25 on Autopilot over 15 runs each.
- Per request in that pass, seconds and USD, Playtest Agent then Autopilot:

```
clear cold [275,529] [1.16,1.61] vs [314,666] [1.41,1.91]
clear warm [143,93] [0.72,0.52] vs [85,66] [0.75,0.48]
explore [490,374] [1.88,1.48] vs [540,316] [2.36,1.67]
ammo [77,76] [0.61,0.56] vs [92,77] [0.69,0.58]
jetpack [199,196] [0.77,0.69] vs [145,129] [0.74,0.74]
doublejump [149,163] [0.82,0.78] vs [139,114] [0.81,0.81]
visual [106] [0.58] vs [193] [1.09]
door cold [264] [1.67] vs [342] [2.03]
door warm [92] [0.57] vs [55] [0.54]
```

## Frame rate and main thread in the confirming pass

- Measured in the confirming pass, before the fixes to the plugin.
- Frames per second, Playtest Agent then Autopilot:

```
clear cold 133,93 vs 88,102
clear warm 136,132 vs 77,76
explore 179,171 vs 159,167
ammo 118,101 vs 122,50
jetpack 122,115 vs 109,56
doublejump 121,158 vs 129,108
visual 120 vs 176
door cold 136 vs 172
door warm 125 vs 96
```

- Main thread median ms per frame, same order:

```
clear cold 6.53,6.83 vs 7.05,6.54
clear warm 6.49,5.28 vs 6.98,6.98
explore 4.85,4.95 vs 5.28,4.91
ammo 7.14,8.23 vs 7.22,14.49
jetpack 7.77,7.92 vs 8.04,13.98
doublejump 5.41,4.89 vs 5.56,5.12
visual 4.73 vs 4.65
door cold 5.21 vs 4.96
door warm 5.34 vs 5.54
```

- Some Autopilot runs were far slower in one batch than in others, so single comparisons are noisy.
- Before the fixes Playtest Agent frame rate was lower on visual and door cold. After the fixes door is 153 and 165
  frames per second; visual was not rerun.

## Open time measures

- Before use_harness: Warm clear, all runs: Playtest Agent 117, 106, 143, 93, 94 s; Autopilot 85, 84, 85, 66 s.
  The ranges do not overlap, so this is worse beyond noise, by about 30 s, at equal cost. Cause measured: episode
  length is about equal; the extra time is model turns and reading outside the episode, not the runtime. See Warm
  memory after use_harness.
- Jetpack cold, all runs: Playtest Agent 130, 100, 199, 196 s vs Autopilot 110, 109, 145, 129 s, equal cost. Probably
  worse, not proven. See After the fixes in Godot and Unreal.
- Warm door overlaps: Playtest Agent 105, 92, 92, 83 s vs Autopilot 98, 99, 55 s.

## Warm memory after use_harness

- Cause of the warm clear gap of about 30 s, measured from file times and traces in one pair:

```
writing a longer harness out again +5 s
a check_harness round +10 s
a probe for the win screen after the episode +15 s
```

- Change: a use_harness tool copies an earlier run's harness instead of writing it out, and the instructions say
  code that already worked needs no check_harness and that a goal's Status says done when the game itself shows it
  achieved.
- Results with the change, Playtest Agent, all goals achieved:

```
clear warm 42, 48, 47 s and $0.29, $0.37, $0.35
Autopilot before: 85, 84, 85, 66 s and $0.62, $0.58, $0.75, $0.48
door warm 51, 47 s and $0.37, $0.29
Autopilot before: 98, 99, 55 s and $0.54 for the last run
```

- Autopilot was not rerun; its code is unchanged, so its numbers stand.
- Runs without the tool, blocked by the bench's permission list so only the instruction changes applied, are not
  results of use_harness: clear warm 76, 86, 70 s, door warm 76, 81 s.
- Cold clear on the final code: 316, 311 and 839 s, $1.44, $1.19 and $2.59. The 839 s run took 8 episodes dying to
  the turret before finding a cover spot. Earlier cold clear ranged 275 to 529 s, Autopilot 314 to 666 s. One
  outlier, cause in the harness's search, not the runtime. Recorded, not explained away.

## Decision latency

- Decision latency is the observation-to-input measure.
- Maximum: 0.15 to 0.38 s on Playtest Agent, 2.16 to 3.25 s on Autopilot.
- First decisions on Playtest Agent: 0.13 to 0.18 s.
- Median: about 0.14 s on both.
- Observation age at decision, median about 0.13 to 0.18 s on both, measured in the earlier cold pass.

## Accuracy

- Every verdict correct and every goal reached on both sides in every completed run.
- Exploration: 5 runs Playtest Agent, 4 runs Autopilot.
- Confirmed in the game's source:
  - ceiling stick, reported by both in play
  - speed kept while blocked by a wall, reported by both
  - buffered tap ignoring variable jump height, reported by Autopilot in 2 of 4 runs and by Playtest Agent in none
- Seen in play only: wall contact cutting a jump, both sides.
- Playtest Agent alone reported ground speed about 0.85 m/s below the configured speed in 2 runs, not confirmed.
- Sample too small to call a difference; the buffered tap miss is recorded.

## Allocation and frame rate on final code

- Allocation work after the confirming pass, on final code, Playtest Agent only.
- GC KB per frame, before then after:

```
clear cold 79 and 236 to 34
clear warm 89 and 149 to 44
explore 26 and 29 to 19
door 38 and 50 to 25
```

- Frames per second, before then after:

```
door 125 and 136 to 153 and 165
clear warm 132 and 136 to 165
```

- These final-code runs all gave correct results: clear cold 316 s $1.44, clear warm 94 s $0.59, explore 386 s $1.65,
  door cold 288 s $1.46, door warm 83 s $0.48.
- Autopilot GC in the confirming pass for comparison, KB per frame: clear 192 to 358, explore 32 and 65,
  door 30 and 62.

## Kit serializer and harness compile

- Kit serializer, measured: Facts dictionary write 1057 B to 0 B per write, 5.6 to 4.0 microseconds; anonymous object
  224 B to 48 B, 1.3 to 0.5 microseconds.
- Harness compile: first-load failures 12 of 24 runs before the API fixes, 3 of 15 after. First compile in a process
  2.05 s cold, 0.75 s with the warm-up at start_run, 0.1 s after.

## Godot and Unreal

- Method: same CLI and MCP path, rendered editor, sides alternated, one at a time. Claude Opus 5.5. Godot 4.7.2 mono,
  UE 5.8. Each pair ran with memory cleared (cold) or kept (warm). Seconds are the run report's clock. Cropout saves
  cleared before each run. Sharing off on Autopilot.
- Per run, Playtest Agent vs Autopilot:

```
Cropout wood (Unreal, tuned on), all achieved
  cold 265, 322 s $1.28, 1.41 vs 403, 529 s $1.55, 1.73
  warm 35, 49 s $0.30, 0.31 vs 82, 70 s $0.54, 0.51
Platformer 3D coins (Godot, tuned on), all achieved
  cold 463, 239 s $1.19, 1.03 vs 347, 303 s $1.18, 1.12
  warm 31, 128 s $0.29, 0.62 vs 60, 95 s $0.45, 0.49
Squash the Creeps (Godot C#, tuned on during the port), all achieved
  cold 112, 134 s $0.59, 0.63 vs 117, 158 s $0.69, 0.79
Platformer 2D coins (Godot, untuned), all achieved
  cold 75, 79 s $0.47, 0.45 vs 88, 97 s $0.58, 0.57
ArenaShooter (Unreal, untuned), timed pairs
  cold 367, 248 s $1.20, 1.19 vs 297, 244 s $1.19, 0.99
  prior warm-up pair, which also rebuilt the export: 432 s $1.31 vs 263 s $1.18
```

- Notes:
  - Platformer 3D: ranges overlap, so within noise. Playtest Agent's 463 s was the model spending probes on route
    tests. Playtest Agent's warm 128 s: the reused harness stalled in a low lane in its first episode and the model
    added an option and went on.
  - ArenaShooter: ranges of the timed pairs overlap, so within noise, but Playtest Agent was slower and dearer in all
    three pairs, so that is recorded as leaning worse on a small sample, and left open. See After the fixes.
  - ArenaShooter accuracy: the request said three enemies, the level supplies two. All six runs on both sides killed
    both and correctly reported not_achieved, naming the two spawners with Spawn Count 1. The request was the bench's
    mistake, not either tool's.
  - Maximum decision latency across these runs: Playtest Agent 0.25 s or less; Autopilot up to 3.28 s.
- Invalid and discarded: the first ArenaShooter pass (tags e1, c2) ran an empty level, because the bench project
  was assembled without the level's external actors. Both sides diagnosed it correctly. Figures not used.
- Bias:
  - Autopilot's Platformer 3D copy got a 3-line patch from this project, in favour of Autopilot, because its old
    keycodes produced control characters that failed the run. Playtest Agent's plugin needed no patch.
  - Codex export caches were warm on both sides in the timed runs.
  - user:// is shared between the Playtest Agent and Autopilot copies.
- Found and fixed during this pass: restart_level followed by run_harness returned to the earlier level, because
  carry.json held the level and overrode the one the host passed. The level no longer goes through carry.json. It has
  now been replayed on the Unreal editor and the level held. KIT-POINTER.md's legacy Input Manager entry now says it
  is for Unity.

### After the fixes

- Causes found for the earlier ArenaShooter result, all fixed in Playtest Agent:
  - Teleport set "position" on every engine, which fails on Unreal and only sets local position on Godot. It now
    uses K2_TeleportTo on Unreal and global_position on Godot.
  - game.Call renamed anonymous object parameters to snake_case, so NewLocation arrived as new_location and the call
    silently ran with defaults. Names now go as written.
  - start_run did not wait for a missing Unreal export, so the first run in a project worked without it. It now waits
    for summary.txt, at most 90 s.
  - Kit changes from the compile errors in 28 runs: Vec.Rounded returns a Vec; Pose.OnGround is a bool, with Ground
    for the unknown case; overloads for Request and AimError with a token; Fields is a dictionary; probes have Print.
- Teleport was checked on the Unreal and Godot editors, the rest of the list on Unreal. 186 tests pass.
- Request changed from three enemies to two, since the level supplies two. Sides alternated. Values Playtest Agent
  then Autopilot.
- ArenaShooter first run, export removed: wall clock 150 s $0.79 vs 166 s $0.83. All achieved. Wall clock is used
  here because Autopilot's report clock leaves out its export wait (its report says 102 s, Playtest Agent's 140 s).
- ArenaShooter cold, 3 pairs (report clock): 153, 136, 135 s $0.89, 0.80, 0.91 vs 127, 120, 117 s $0.76, 0.83, 0.92.
  All achieved. Ranges do not overlap on time: worse beyond noise by about 20 s. Cost within noise. Cause measured: the
  time outside the model is equal, wall clock minus API 56, 38, 44 s vs 55, 43, 42 s; API time is 108, 106, 99 s vs
  84, 90, 89 s, following output tokens 11.8k, 11.7k, 11.2k vs 9.2k, 9.2k, 9.5k, and API time runs about 9 s per
  1000 output tokens on both sides. The runtime is not the cause. See ArenaShooter output tokens below.
- Decision latency maximum in these runs: Playtest Agent 0.27 s or less; Autopilot up to 2.85 s.
- Jetpack cold, 3 pairs: 103, 211, 77 s $0.54, 0.83, 0.48 vs 93, 110, 225 s $0.65, 0.75, 1.20. All verdicts
  correct. Ranges overlap. Each side had one long run where an extra episode ended with the player gone. Means
  130 s $0.62 vs 143 s $0.87. Earlier 4 pairs stay in the record. Jetpack is no longer worse on this evidence.

### ArenaShooter output tokens

- Per-turn tokens from the transcripts: the turn that writes the harness is 4.7k, 6.1k, 4.8k vs 3.0k, 3.3k, 3.4k, which
  is nearly the whole gap.
- Correction: an earlier version of this page said visible code and text were about the same size. They are not:
  visible output is 15.9k to 17.5k characters vs 14.2k to 15.1k, the harness 7.5k to 8.0k vs 6.0k to 6.5k. Arguments to
  the file tools add about 1000 characters, since Read takes absolute paths and Autopilot's read_file relative ones.
- Correction: an earlier version of this page gave a fit of 0.40 tokens per visible character vs 0.35. It left tool
  arguments and thinking unseparated and does not reproduce: a Codex check gets about 0.52 on both sides over g1 to g3.
- Correction: an earlier version of this page said thinking shows no steady gap. That came from one pair and signature
  lengths. The run result carries `output_tokens_details.thinking_tokens`: means 2575 vs 1763, ranges 1690 to 3675 vs
  1457 to 2035. Of the 2279 token output gap, thinking is 812 and the rest 1468.
- Change tried ([ADR 0011](adr/0011-harness-code-passes-no-token.md)): no cancellation token in harness code, `game.Num`
  and `game.Text`, settings in the constructor, braces on the line. 185 tests pass.
- ArenaShooter cold after it, 3 runs against the Autopilot runs above: 147, 149, 158 s $0.84, 0.85, 0.92, all achieved.
  Harness 6.7k to 7.7k characters, output tokens 9.7k, 11.6k, 11.7k. No time gain beyond noise.
- All clean runs on this request, client clock: Playtest Agent 7 runs, 143 to 164 s, mean 151 s, $0.86; Autopilot 5 runs,
  105 to 139 s, mean 124 s, $0.82. Two of Autopilot's came after the first estimate and were its fastest, so the gap is
  about 25 s, not 20 s. Cost within noise. API time means 100 s vs 83 s.
- The mean gap is 27 s: 17 s API time and 10 s outside it. Outside it, probes are 26.5 s vs 17.6 s and run_harness 18.2 s
  vs 14.7 s. That is mostly game time: probes that play the whole fight, against short firing tests. A probe with no
  game time is 0.6 to 1.6 s vs 0.1 to 0.4 s, so compiling is a small part. The first three pairs were level outside the
  API, 46 s vs 47 s; the 10 s comes from the later runs, where Autopilot has only 2.
- Correction: an earlier version of this page put the rest down to the C# itself. The evidence does not show that. Part
  is thinking and game time, which a shorter harness shape might also change, and `Read<T>` already converts implicitly.
- Sample limits: 7 vs 5 runs, not interleaved after the first three pairs, and pre and post ADR 0011 runs are pooled.
- The same kit on other engines: jetpack DENIED, correct, 129 s $0.51. Platformer 2D achieved, 92 s $0.48, against
  98 to 107 s $0.57 to $0.58 for Autopilot.
- Invalid and discarded: tag t1 Playtest Agent. A stray earlier start of the bench ran at the same time and closed its
  editor. The bench was at fault, not the product. It did show that the no-editor message said Unity on an Unreal
  project, now fixed.

### ArenaShooter controlled experiment

- Design: 10 randomized blocks of 3 runs, 2026-11-02, same day and same copies: A without the change, B with `Shoot`,
  `Foes`, a worked fight example and the short probe rule, C Autopilot. Bar set beforehand: aim for 15 s; stop tuning if
  the interval rules out a gain of 10 s.
- All 30 runs exited clean and reported achieved. Reports were not audited beyond the outliers.
- A: mean 161 s, median 151 s, sd 23, API 98 s, $0.87. B: mean 146 s, median 140 s, sd 31, API 91 s, $0.85. C: mean
  126 s, median 124 s, sd 7, API 85 s, $0.83.
- A minus B: mean 15.0 s, 95% interval -17 to 47; median 11.7 s, bootstrap 5.6 to 42.9; B faster in 9 of 10 blocks, sign
  test p 0.02. Cost $0.02, interval -$0.03 to $0.06. The aim is met on the mean and the bar does not stop tuning, but
  the size is not pinned down.
- B minus C: mean 19.9 s, interval -4 to 44; median 8.0 s, bootstrap 3.9 to 24.2; C faster in 9 of 10 blocks. Cost
  $0.02, interval -$0.05 to $0.09. A minus C is 34.9 s, interval 18 to 52, 10 of 10: the unblocked 25 s was too low.
- What moved: probes 28.7 s to 18.0 s (C 21.0 s), thinking 2205 to 1609 tokens (C 1962), output 10855 to 10154 tokens
  (C 9045), harness 6944 to 5146 characters. `Foes` was used in all 10 B runs, `Shoot` in 7.
- run_harness did not move: 19.5 s to 24.4 s, 20.4 s without block 9 (C 14.1 s).
- Outlier: block 9 B took 231 s. Its first episode stalled for 64 s because the harness took Current Weapon, not null
  while unarmed, to mean armed. A has its own tail, 204, 191 and 184 s; C has none over 139 s.
- Defect in B: 7 of 10 runs failed check_harness with CS0407, against 1 of 10 in A. The example declared `bool Fewer(Obs,
  Obs)` and wrapped it in a lambda, and the model shortened that to `Effect = Fewer` while `Effect` returns `bool?`. The
  repair cycle averaged 10.4 s, about 7 s a run across B. The example now declares `bool?` and passes the method group.
- Second pass the same day, about 3 hours later: the example fix against Autopilot, 5 pairs, all achieved. Fixed build:
  130 to 172 s, mean 151 s, median 149 s, API 92 s, $0.87. Autopilot: 135 to 172 s, mean 148 s, median 143 s, API 85 s,
  $0.86. Fixed minus Autopilot: mean 3.0 s, 95% interval -17 to 23; median -4.0 s; Autopilot faster in 2 of 5. The
  difference is unresolved, not shown to be parity.
- Bench fault: the seeded shuffle put Autopilot first in all 5 pairs, so this pass cannot separate the difference from
  an order effect.
- Autopilot's mean rose 21 s from the morning's 126 s with the same CLI 2.1.314 and model. Its API time (85.3 s to
  84.6 s) and output tokens (9045 to 9025) did not move; the rise is all outside the API (41 s to 63 s), in probes that
  went from 21.0 s to 37.8 s with longer firing tests. Trivial probes stayed at 0.1 to 0.4 s, which does not rule out a
  slower game. The cause is not known. Ours outside the API: 55 s in the morning, 59 s in this pass.
- The fix removed the failure in these 5 runs. Its time effect is not estimated, because B and the fix ran in different
  passes.
- The API gap is steady across both passes: ours 91.2 s and 91.6 s against Autopilot 85.3 s and 84.6 s, from about 1100
  more output tokens at about 9 s per 1000 on both sides. The swing between passes is outside the API.
- The two passes disagree in direction, so they are stated apart. Pooled only as history, 15 pairs: mean difference
  14.3 s, 95% interval -2.0 to 30.5; median pair difference 6 s; Autopilot faster in 11 of 15. Cost close in both
  passes.
- Double play checked and ruled out: in 3 of the 5 fixed runs check_harness played the game for 18.7 to 23.2 s before
  run_harness played it again, but Autopilot does the same through `check_harness()` in a probe (4 of 5 in that pass, 8
  of 10 in the morning). Check time over the 15 pairs: ours 7.8 s, Autopilot 6.5 s. The gap is in run_harness: 24.4 s
  against 14.1 s in the morning (one timed-out 40 s episode, one run of 3 episodes) and 22.9 s against 18.1 s in the
  second pass, with episodes to done 15.5 s against 13.3 s and 21.4 s against 17.1 s.
- Kill time, episodes that ended done in one: enemies appear at about 5.5 s on both sides. Appearance to first kill
  4.4 s against 3.6 s in the morning and 8.6 s against 4.8 s in the second pass; first kill to second 7.2 s against 4.5
  s and 8.2 s against 7.7 s. Shots per episode are alike (10.2 and 10.4 against 9.4 and 9.0) and the refire inside a
  kill is the same 0.3 s.
- The difference is time standing without a shot after enemies appear: 5.4 s against 1.6 s a run in the morning, 9.1
  s against 4.4 s in the second pass, about the whole episode gap. Walking without a shot is alike.
- What that standing is, over our 13 single-episode runs, from the harness facts at each observation: the target unseen
  and already within Shoot's closeTo 3.4 s a run, seen beyond closeTo with the crosshair not coming on 1.7 s, seen
  within closeTo 1.1 s. The first is a Kit defect: when the target is unseen, Shoot walks to within closeTo, which
  returns at once when it is already that close, so Shoot stands until it times out (y2: 15 s, no shot, target at
  255). Autopilot's harnesses end their shoot option at once when there is no line of sight, and Jev picks a close-in
  option instead.
- Fix: Shoot turns to an unseen target and walks toward it until it is seen, fails with "no line of sight" when it still
  can't see it from beside it, and walks in to closeTo while the crosshair misses a seen target. Three tests, each failing
  before the fix; 193 pass.

### ArenaShooter Shoot fix, balanced pass

- Design, set beforehand: 6 blocks the same evening, each of the 6 orders of the 3 arms once (tags s1-s6): the previous
  build (fix), the Shoot fix (shoot), Autopilot (up). Primary measures: episode length and time standing without a shot;
  wall time reported, with 6 blocks not expected to resolve a few seconds.
- All 18 runs achieved, each in one episode. The shoot arm called Kit `Shoot` in all 6 harnesses, fix in 4.
- fix: mean 117.6 s, median 112.9 s, API 77.7 s, $0.686, output 7640 tokens. shoot: 118.2 s, 117.4 s, 79.7 s, $0.664,
  7386. up: 123.3 s, 111.1 s, 80.3 s, $0.712, 7115.
- shoot minus fix: episode -2.0 s (95% -5.7 to 1.7), standing -2.0 s (-6.2 to 2.3), lower in 4 of 6 on both; wall +0.5 s
  (-24 to 25), cost level.
- shoot minus up: wall -5.1 s (-35 to 24), median -3.9 s, lower in 3 of 6; cost -$0.048, lower in 4 of 6; episode -2.2 s
  (-9.4 to 5.1); standing +1.2 s (-1.7 to 4.1).
- The fix did what it was for: unseen within closeTo while standing fell from 1.0 to 0.4 s a run and seen beyond
  closeTo while standing from 1.8 to 0.3 s, now walking instead. What stands still now is mostly aiming at a seen target
  in range, 1.4 s a run.
- The whole pass ran faster and cheaper than the morning (Autopilot 123 s $0.71 against 126 s $0.83 in the morning and
  148 s $0.86 in the afternoon; output about 7100 tokens against 9000), on the same CLI and model. Position in the block
  showed 127.6, 102.7 and 128.8 s for first, second and third, balanced across arms by the design.
- Regression on the fixed build, 3 runs each. Jetpack (Unity): DENIED 3 of 3, correct, 130 to 146 s, $0.55 to $0.60,
  against one earlier 129 s $0.51 and Autopilot's mean 143 s $0.87 over the last 3. Platformer 2D (Godot): achieved 3 of
  3, 62 to 79 s, $0.40 to $0.42, against an earlier 92 s $0.48 and Autopilot's 98 to 107 s $0.57 to $0.58. Two of those
  three hit compile errors, CS1061 and CS8130, and were repaired.
- `Shoot` and `Foes` have behavior tests; 190 tests pass, and each of 5 deliberate faults fails one test.

## Not measured and limits

- Not measured per run: Jev tokens on either side, subscription usage.
- Cost is list-price USD by tokens. Jev tokens and subscription usage were not measured, so the cost floor is met on
  tokens only.
- Exploration and warm samples are small.
- Tested on Windows only.
- Both Unity games were used while tuning, so the ADR 0006 rule of at least one game the change was not tuned on
  is not met for Unity.
- The comparison ran on bench copies made from the owner's test games.
- The 2D bench copy reimports for about 70 s at each editor start since its manifest was edited for the Unity tests.
  The run's seconds clock starts after that, so it affects start_run wait only and is a bench artifact.
- Frame rate and main-thread figures for Godot and Unreal were not measured.
- The Unity requests other than jetpack were not rerun after these Kit changes.
