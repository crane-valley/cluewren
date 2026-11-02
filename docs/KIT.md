# The harness kit (PlaytestAgent.Kit)

harness.cs is compiled with these in scope, with no `using` needed: `System`, `System.Collections.Generic`, `System.IO`,
`System.Linq`, `System.Threading`, `System.Threading.Tasks`, `PlaytestAgent.Kit`, and the static members of `Control`,
`Geom` and `Research`, so `WalkTo(...)` and `Where(...)` are called by their bare names.
Units: centimetres (Unreal units), degrees, seconds of game time.
What is particular to the game's engine (how its objects, classes, input actions and maps are named) is in the
engine's section at the end.

Every read is awaited: `await game.Loc(door)`. Executors are `async Task<Result>` methods, and one tick is
`await game.Tick()`.

## Game (`game`)

Reads (privileged: any object, any property):
- `await game.Pawn()`, `await game.Controller()`, `await game.World()`: paths ("" while there is no pawn, e.g. dead).
  `await game.Context(fresh: true)` re-reads.
- `await game.Actors(cls)` -> `List<string>`: actor paths of a class (subclasses too); how a class is named: the engine's
  section.
- `await game.Named("Door")`: actors whose object name contains the text. `await game.AllActors()`.
- `await game.Get(path, "Health")` -> `GameValue`: one property. `await game.Num(path, "Health")` -> `double`, 0 when it
  isn't a number; `await game.Text(path, "Name")` -> `string`, "" when it isn't text. `await game.Props(path)`: all of them.
- `await game.Call(path, "FunctionName", new { Param = value })` -> `GameValue` (`ReturnValue` and out params);
  `await game.Ret(...)` -> its ReturnValue.
- `await game.Describe(path)`: its class, properties, callable functions (with their arguments).
- `await game.Exists(path)`: whether it is in the level now. An actor the game destroyed (picked up, broken, killed) is
  gone at once, though `game.Get` can still read it for a moment: ask `Exists`, don't wait for a read to fail.
- `await game.Loc(path)` -> `Vec`; `await game.Locs(paths)` -> `Dictionary<string, Vec>`; `await game.Bounds(path)` ->
  `(Vec Center, Vec Extent)`, half extent; `await game.Velocity(path)`; `await game.Rot(path)`.
- `await game.Pose()` -> `Pose` with `At`, `Eye`, `Yaw`, `Pitch`, `Velocity`, `OnGround` of the player (one frame).
  `OnGround` is false when the game doesn't tell; `Ground` is the `bool?` that tells the two apart.
- These and `game.View` are in the kit's frame: X forward, Y right, Z up, centimetres; yaw turns right, pitch up.
  `game.ToKit(value)`: a position or velocity read with `game.Get` in the engine's own frame and units, as a `Vec?` in
  the kit's; `game.ToNative(vec)`: back, for `game.Set` / `game.Call`.
- `await game.AimError(point)` -> `(double Right, double Up)` degrees from the view to a point.
- `await game.View(new { max_count = 16 })`: what the player sees (visible things with distance/screen position, held
  items, camera, body: on_ground, falling, touching...). `new { sonar = true }` adds `body.sonar.ways`: how far the body
  could walk each way (`turn` right of the view, `clear` distance, `by` wall/drop/steep/reach). Costs part of a frame:
  not every tick.
- `await game.Sees(paths)` -> `Dictionary<string, Seen>` with `Distance`, `ScreenX`, `ScreenY`, `CrosshairOn` (a bool), for
  those of the actors the player can see now (in view, line of sight; ScreenX/ScreenY -1..1 from the view's center,
  right/up +): cheap enough for every tick (reflex play: is the enemy visible, is the crosshair on it).
- `await game.Screen()` -> `Dictionary<string, string>`, widget key to text, of every text on screen (HUD, menus, prompts).
- `await game.Ui()` -> the interactive UI elements on screen now, each a `GameValue` with "id", "kind"
  (button/checkbox/combo/slider/text_input/widget), "name", "owner" (its user widget), "label" (the texts inside it, as one
  string), "enabled", "checked"?, "selected"?, "value"?.
  `await game.UiAct(new { id = ..., action = "click" })`: action is click, right_click, double_click, set or drag, with
  value, text, option, or to (the id to drop on, for drag). Acts on one as the player's mouse would (ids are from the
  latest Ui() listing). Open menus with their key first (from the input mappings), then list and click.
- `await game.Batch(() => game.Pose(), () => game.Get(door, "Open"))`: many reads at once, resolved in one game
  frame. Up to seven reads of different types give a tuple of `Read<T>`; a list of `Func<ValueTask<T>>` gives a `Read<T>[]`.
  A failed read comes back as a `Read<T>` with `.Ok` false and `.Error` set; `.Value`, or using it as a `T`, throws that
  GameException. `await game.Map(items, item => read)`: read for each item, at once. Use these in Observe: every read is
  a round trip.
- `game.T`: game time of the latest answer.

`GameValue` is a read value of any JSON shape: `.Number` (double?), `.Text`, `.Truthy`, `.IsNull`, `.Vector`, `["field"]`,
`[index]`, `.Items`, `.Fields`, `.Count`. It converts to double, string and bool: `if (await game.Get(wolf, "Health") <= 0)`.
A GameValue that is not a number throws when used as one: where a value may be missing, test `.Number`.

Inputs (the player's hands; the only way to act in play):
- `game.Press("E")`: a quick press of a key (FKey names, whatever the engine: W, A, S, D, E, F, R, Q, SpaceBar, LeftShift,
  LeftControl, Tab, Escape, Enter, One..Nine, LeftMouseButton, RightMouseButton, MiddleMouseButton...) or of an input
  action (as the engine's section names them; value optional).
- `game.Hold("W")` ... `game.Release("W")`; `game.ReleaseAll()`. Holds are leased: the runtime renews them every tick.
- `game.Mouse(rightCounts, upCounts)`: a mouse flick. `await game.Turn(rightDeg, upDeg)`: turn the view by degrees (uses
  the measured sensitivity; `await game.CalibrateLook()` measures it, once).
- `game.Keys`: movement keys the controllers use, a dictionary: "forward": W, "back": S, "left": A, "right": D,
  "jump": SpaceBar. Set them in Setup from the game's input mappings if they differ.
- `await game.Set(path, prop, value, why: "...")`: writes state directly. NOT a player action. In a probe it is research
  (with the research helpers: Teleport, FightTrial, CallAny); in an episode only to set up a verification (e.g. take away
  what only gets in the way of testing). Every write is reported, an episode's apart from research's.
- `await game.Restart(map: null)`: start the run's level over (the briefing's), or open a map (named as the engine's section
  says). For a game that boots into a main menu, set the harness's `StartMap` to its gameplay level (from the codex),
  or click through the menu in Setup (`game.Ui()`, `game.UiAct`).

## Controllers (the Control class): async methods, `await` them inside executors

Each does one tick of control per `await game.Tick()` and returns a `Result` (`if (!r)` is false when it worked, `.Why`,
`.Data`). A target is a `Vec`, an actor path, or `Target.Of(() => ...)` for a callable. An option stopped
or switched ends at its next await, then all held input is released.
- `WalkTo(game, target, within: 120, timeout: 20, avoidDrops: true, jumpWhenStuck: true, runKey: null)`: target is a
  Vec/point, an actor path (its box center, re-read each tick: follows moving things) or a callable. Turns with the mouse,
  holds forward and finds its way: it feels the way with sonar, keeps it on a map of the level (later walks know it)
  and walks the shortest way the map leaves open, planned again when what it feels blocks it (round rocks and walls,
  out of dead ends, through doorways). Jumps when stuck on something it can't feel; won't walk off a drop deeper than
  `maxDrop` (300). No waypoints needed. A goal higher than a step up (a platform, an upper floor) it reaches by a way
  up it sees (a ramp, stairs: the view's `ways_up`, where the engine's plugin tells them), else it looks round for one:
  don't write routes of the level's ramps for it. `climb: false`: no ways up. It lets go of forward early by how far
  the body slides (measured in this game) and returns stopped, about `within` from the target. Fails with "stuck" or
  "walk timed out" (`.Data`: distance left, the `way` it last planned) when it finds no way: the target likely needs
  a jump (JumpTo) or isn't reachable. Fails within about 2 s with "the view doesn't turn" when the mouse has no
  effect (a menu or an interaction holding the mouse): end that first.
- `LookAt(game, target, tolerance: null, timeout: 1.5)`: turn the view onto a target: by default until the crosshair
  is well inside an actor's box (2.5 degrees for a point), measuring each turn's effect (third-person cameras too). Pass
  a tolerance only to be looser on purpose (a big enemy). Fails fast when the thing is right below the player or past
  the camera's pitch limit: step back from it.
- `Track(game, target, seconds)`: keep the view on a moving target (e.g. while holding fire).
- `FaceAndPress(game, target, key)`, `GoAndPress(game, target, key, within: 150)`: the "use X" macros.
- `KeepAway(game, threat, distance: 800, timeout: 6)`: move away from a threat along the most open way.
- `await Movement(game)` -> the character's `MovementInfo`: `WalkSpeed`, `JumpZ`, `Gravity`, `HalfHeight`, `Peak` (how
  high a jump lifts the feet), `Step` (the highest step it walks up), `ReachUp` (Peak + Step: about the highest ledge a
  jump gets onto), `ReachUpMax` (ReachUp + 10: a ledge between the two is borderline, reached by perching on the edge: try
  it, never rule it out), `AirControl`, read from the game. Each is a double?, null when it can't be read.
- `JumpTo(game, target, runKey: null)`: run and jump to land on something. target: an actor path (a box, a
  platform, a ledge: lands on its top, taking off so the top of the jump comes over its near edge, then steps in off
  the edge) or a point (the far side of a gap: lands on it). Works out reachability and take-off from Movement and
  the speed the character actually runs at; it backs off for a run-up when it starts too close. ok when the feet are on it.
- `Melee`, `StrikeAndRetreat`, `AimAndStrike`, `Shoot`, `Foes`, a whole fight harness: KIT-FIGHT.md.
- `HoldFor(game, key, seconds)`, `Press(game, key, times: 1, every: 0.15)`, `Wait(game, seconds)`,
  `Until(game, condition, timeout)`.
- `Run(game, () => WalkTo(game, door), timeout: 15)`: drive a controller to its end from plain code (Setup,
  Teardown), as in probes; inside executors `await` it instead.
- `await Sonar(game, turns, reach: 1500)` -> `List<SonarWay>`: the sonar ways (see view).
- Write your own controllers the same way when the game needs one (aim leading a moving target, timing a jump off an
  edge, circling an enemy): a loop that reads, acts, `await game.Tick()`s, and returns a Result. Await one controller at a
  time: two ticking at once is an error. Don't catch OperationCanceledException: it is how an option is stopped. Let
  go of what you hold in `finally`.

## Geometry

`new Vec(x, y, z)` (`.Dist`, `.Dist2d`, `.Length`, `.Length2d`, `.Unit`, `.Flat`, `.Dot`, `.Rounded()`),
`Bearing(at, yaw, point)` (degrees right), `Clock(bearing)` (ahead / ahead-left / left / behind...), `Forward(yaw, pitch)`,
`RightOf(yaw)`, `Wrap`, `YawOf`, `PitchOf`, `Where(pose, point)` -> dictionary with "distance", "direction", "bearing",
"height" (facts for Jev).

## The harness

```csharp
class MyHarness : Harness {
    public MyHarness() {
        Goal = "...";          // the goal in the game's own words, as Jev reads it
        Tempo = 2.0;           // decisions/second while an option runs (reflex play: 4-8; deliberate: 0.5-2)
        TickHz = 15.0;         // observe + one executor step per tick
        SwitchMargin = 0.25;   // how much more Jev must prefer another option to interrupt the running one
    }

    public override Task Setup(Game game) ...             // find things, set game.Keys, calibrate
    public override Task<Obs> Observe(Game game) ...      // cheap reads each tick; "_"-keys hidden from Jev
    public override object Facts(Obs obs) ...                                   // optional: what Jev sees of the situation
    public override List<Option> Options(Obs obs) ...                           // legal options now, each with facts
    public override Dictionary<string, Question> Questions(Obs obs) ...         // optional extra Noul/Score/Choice -> OnAnswers(answers, obs)
    public override Status Status(Obs obs) ...                                  // running / done / failed (+ note, evidence)
    public override async Task Idle(Game game, Obs obs) => await game.Tick();  // while nothing runs
}
```

`Obs` is a `Dictionary<string, object?>` with `.Bool(key)`, `.Num(key)`, `.Text(key)` and `.Get<T>(key)`. Options, Status and
Facts are not async: put what they need into the observation.

`new Option(name, description, run) { Facts = new() { ["tried"] = n }, Interruptible = true, Urgent = false, Effect = (a, b) => ..., MaxSeconds = 30 }`.
`run` is `(game, obs) => ...` giving a `Task<Result>`: `new Result(true)` or `new Result(false, "why")`, there is no
`Result.Ok()`. `Facts` is a dictionary, not an anonymous object.
`new Status("running" | "done" | "failed", note: "", evidence: ...)`, the evidence being a dictionary, a list or an anonymous
object; the last one a Status gave is kept however the episode ends. An episode that reaches its time limit or its limit on
Jev decisions ends as "inconclusive": unfinished, not failed.

## What makes a harness play well

- Facts in the player's terms: distances, directions, counts against limits, what is held, what is in danger.
- Few options (2-8), each a whole meaningful step, each only when legal.
- Reflex games: higher tempo, short executors that aim/steer in code every tick, urgent options for threats; a hit
  to dodge or block is a reflex in code (KIT-FIGHT.md).
- Using a thing by the crosshair (pick up, open, talk to): walk to inside the game's use range but not on top of it
  (a thing on the floor needs 1-2.5 m: standing over it, a third-person camera can't see it), `LookAt` it with the
  default tolerance, check the game's own sign that it is targeted (`(await game.Sees([thing]))[thing].CrosshairOn`, or
  the game's focus/target property from the code) and only then press.

## The contract

The runtime runs a harness like this.

    over and over:          Observe                 code reads the game: the facts that matter, on a loop of its
                            Status                  own: a slow Observe never holds up the executor. done / failed /
                                                    running (the episode ends when not running)
    at decision points:     Options                 the legal options now, each with its own facts. Jev picks one (a
                                                    Choice over the options' descriptions, given goal + facts +
                                                    options' facts + what is running + how recent options turned out),
                                                    plus Questions if any
    every tick (TickHz):    the chosen option's     advances one step (an executor: controllers that drive the
                            Run                     player's inputs)

Decision points: when no option is running, when the running one finished, when the set of legal options changed,
and every 1/Tempo seconds while one runs (Tempo 0: never mid-option). A different pick replaces the running option
only if it is interruptible and Jev prefers the new one by `SwitchMargin` (or the new one is urgent): this keeps
play from dithering. A running option that Options no longer lists keeps going until its executor returns (or
MaxSeconds): an executor ends itself when its target is gone. A single legal option runs without asking Jev.

Design rules (they are why hand-made harnesses work):
- Options are game-meaningful macro actions (mine the nearest ore vein, carry the crate to the dock, shoot the closest
  enemy, jump the gap ahead), not keys. Make only legal/possible options available (Tetris: reachable placements
  only; Pac-Man: no "walk into the wall").
- Facts are computed by code, in plain words and numbers a player would use (distance, ahead/left/behind, counts
  against maximums, what the backpack holds). Give each option its own facts (Pac-Man's per-direction block). Keep the
  state small: what bears on the choice.
- Describe options in the same words the facts use (Flappy: "upper half of the gap" in both).
- Executors do the fast part in code every tick (aim, steer, time a press); Jev chooses what to do, at its tempo.
- Every option says what should change when it worked (`Effect`), so a run shows which options don't work.
- A goal of several things in order (find the key, then open the door) is a plan (KIT-PLAN.md).

Option:
- `Name`: identifier (letters, digits, _), stable across ticks: it's how Jev's pick and the running option are matched.
  Name it by its role (harvest_nearest_mushroom, attack_closest_enemy), not by an instance (harvest_mushroom_7) that
  changes as the scene does.
- `Description`: what it does, in words for Jev (the Choice's criterion for it); keep it the same across ticks.
- `Run`: `(game, obs) => Task<Result>`, the executor: `await game.Tick()` once per tick; return a Result when finished.
  It may await controllers.
- `Facts`: facts about this option now (distance to its target, what it would get, how often it worked).
- `Interruptible`: whether a different pick may stop it midway (false for things that must finish, e.g. a jump).
- `Urgent`: switch to it without the margin when Jev picks it (e.g. defending when hit).
- `Effect`: `(before, after) => true/false/null`: did it work (null: can't tell).
- `MaxSeconds`: the runtime stops it after this long (it then counts as failed).

Status: state is "running", "done" (the goal is reached / the question is settled), or "failed" (it can't be reached any
more, e.g. the player died in a run that must not die). note: why, in words. evidence: values that support it (for
verification: what was observed).

Harness:
- `Goal`: the goal as Jev should see it, in the game's own terms.
- `Tempo`: decisions a second while an option runs (0: only when it ends or the options change).
- `TickHz`: runtime ticks a second (observe + one executor step).
- `SwitchMargin`: how much more Jev must prefer another option to stop the running one.
- `Instructions`: the Choice's instructions (default: the runtime's).
- `RecentKept`: how many recent option outcomes Jev sees.
- `StartMap`: a map to start each episode in (e.g. the gameplay level of a game that boots into a main menu); null: the
  level is restarted as it is. Setup can also get through menus itself.
- `Plan`: the ordered Steps of a request of more than one step (KIT-PLAN.md).
- `StallSeconds`: seconds of game time the plan may stand still (no step achieved, no option that worked) before the
  runtime ends the episode as stalled, reporting what was tried and where the player stood. 0: never. Works without a
  plan too (then only options working count as progress).
- `Setup`: once, before the episode (the level is fresh): find the things the harness works with, calibrate. When play goes
  on from where it was (run_harness restart=false), the game is paused through Setup and the first observation (reads
  work; inputs and the game clock don't) and runs again just before the first option's first step: a Setup that must press
  inputs or wait for the game then calls `await game.Pause(false)` first.
- `Observe`: read the game: return the observation. Every tick, so keep it to a few reads; use `game.Batch` to read many
  things in one frame. Keys starting with "_" are kept from Jev (paths, raw vectors).
- `Facts`: what Jev is told about the situation (default: obs without "_" keys).
- `Options`: the legal options now.
- `Questions`: extra questions for Jev, asked with each decision: id to `Noul`, `Score` or `Choice`; their answers go to
  OnAnswers (e.g. a Noul "is the player in danger" that code uses to gate options).
  `new Noul { Instructions = "...", IfTrue = "...", IfFalse = "..." }`, `new Choice(criteria) { Instructions = "..." }`,
  `new Score(levels) { Instructions = "..." }`.
- `OnAnswers`: answers: id to a double (Noul or Score) or a `Dictionary<string, double>` of each option's probability (Choice).
- `Status`: how the episode stands.
- `Idle`: what the player does while no option runs (default: nothing).
- `Teardown`: after the episode.

## Research helpers (in probes)

A probe is C# statements, run against the live game with top-level `await`. `Print(a, b, ...)` writes one line of anything (a GameValue, a Vec, a list, an anonymous object) and is shorter than `Console.WriteLine`, which is ambiguous on a GameValue. `game` is in scope, and everything
above. What is printed and the value of a last expression without a semicolon come back.

- `await Nearest(game, "Goblin", n: 5, within: 3000)`: actors of a class by distance from the player: `List<Near>` with
  `Actor`, `At`, `Distance`.
- `await Teleport(game, to, actor: null, why: "research", distance: 150)`: move the player's pawn (or `actor`) to a Vec, or
  next to an actor path (`distance` short of it). A direct change, only to set up a measurement in a probe (never in a
  harness: play is Jev's, through inputs); every one is reported.
- `await CallAny(game, obj, ["GiveItem", "AddItem", "AddToInventory"], new { Item = "...", Amount = 1 })`: the first function
  name the game accepts -> `(Called, Result)`; GameException with every refusal when none is.
- `await CheckHarness(seconds: 8, restart: true, only: null)`: tries this run's harness without Jev (Setup, the first
  observation with its facts, status, plan step and options, then each option's executor alone) and reports how each went;
  `only`: the option names to try.
