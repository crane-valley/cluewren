# The kit: plans

## The plan (a request of more than one step)

A goal that takes steps in order (gather, then make, then use; open, then cross, then reach) is a `Plan`: ordered
`Step`s, each done when the game's own state says so, each naming the options legal in it. The runtime offers Jev the
current step's options (plus urgent ones), tells it the plan's standing (`plan.done`, `plan.now`, `plan.next`), ends
an episode that stands still (no step advancing, no option working, for `StallSeconds`; or one step taking
`StepSeconds` though options work) as STALLED, and on a continuation (run_harness restart=false) hands `Resume` the
progress so the steps done stay done.

```csharp
class RepairHarness : Harness {
    string _part, _pump;

    public RepairHarness() { Goal = "Fetch the part from the shed and fit it to the pump"; StallSeconds = 45; }

    public override async Task Setup(Game game) {
        _part = (await game.Actors("PumpPart"))[0];
        _pump = (await game.Actors("Pump"))[0];
        Plan =
        [
            new Step("get_part", "Pick up the pump part from the shed", o => o.Bool("holding_part"),
                ["walk_to_shed", "pick_up_part"]),
            new Step("fit_part", "Fit the part to the pump", o => o.Bool("pump_repaired"), ["walk_to_pump", "fit_part"]),
        ];
    }

    public override async Task<Obs> Observe(Game game) {
        var pawn = await game.Pawn();
        var (holding, repaired, pose, partAt, pumpAt) = await game.Batch(() => game.Get(pawn, "HeldItem"),
            () => game.Get(_pump, "repaired"), () => game.Pose(), () => game.Loc(_part), () => game.Loc(_pump));
        return new Obs
        {
            ["_pose"] = pose.Value, ["holding_part"] = holding.Value.Text == "Part", ["pump_repaired"] = repaired.Value.Truthy,
            ["part"] = Where(pose, partAt), ["pump"] = Where(pose, pumpAt),
        };
    }

    public override List<Option> Options(Obs obs) {    // every option that is legal now: the plan picks the current step's among them
        ...
    }

    public override Status Status(Obs obs) =>
        obs.Bool("pump_repaired") ? new Status("done", "the pump works", new() { ["repaired"] = true }) : new Status();
}
```

A Done reads the game (a counter, a flag, what is held), never the options' history; a step with nothing to do
yet (the thing isn't there) is one to get around in the plan, not to wait on. `await CheckHarness()` in a probe says which
step the first observation is at and tries that step's options.

## The harness members of a plan

- A goal of several things in order (find the key, then open the door) is a plan: ordered Steps (`Plan`) whose
  Done predicates read the game's own state (the backpack holds the key; the door's state is open), never what
  options ran. The runtime offers Jev only the current step's options and tells it where the plan stands, so an
  option that stays legal after its step is done (pull the lever once more) isn't taken again by mistake. When
  nothing advances the plan for `StallSeconds` the episode ends as stalled, with what was tried and where the player
  stood; a stalled harness is continued, not restarted: revise the plan's continuation (the steps from here on, or
  an option's approach) and run_harness(restart=false) goes on from where play stands, the steps done staying done
  (Resume is told the progress).

Step:
- `Name`: identifier (letters, digits, _), stable across ticks: how the plan's progress is kept and reported.
- `Description`: what the step achieves, in the game's own words, for Jev (it sees it as `plan.now.what`, and the later
  steps' as `plan.next`).
- `Done`: `obs => true` once the step is achieved. Read it from the observation, i.e. the game's own state (the backpack
  holds the key; the door's state is open), not from what options ran: the runtime calls it on every observation, and a
  step found done stays done for the episode.
- `Options`: names of the options legal in this step: while it is the current step, Jev is offered only these (plus
  urgent ones). null: every legal option. When none of the named options is legal, every legal option is offered (the
  player is never stranded) and the mismatch goes to the trace.

- `Plan`: the ordered Steps the goal needs achieved one after another (empty: no plan, every legal option is offered all
  the time). Set it in the constructor or in Setup. The current step is the first not yet done; Jev is offered the options
  it names and sees `plan` (done, now, next) with every decision.
- `StepSeconds`: seconds of game time one step of the plan may take, options working or not, before the runtime ends the
  episode as stalled: options that keep working without the step getting done (what they gather is lost again, the wrong
  thing is counted, Done reads the wrong state) are a loop too. 0: never.
- `Resume`: after Setup, when the episode continues the previous one (run_harness restart=false: play went on from where
  it was, the level not restarted). `Progress` is where the previous episode left the plan: `Episode`, `Done` (names of the
  steps done then), `Step` (the step it was at, or null: all done), `T` (seconds of game time it played), `Stalled` (why it
  stalled: step, seconds, tried: what each option did meanwhile, player: where it stood and how far it moved; or null),
  `LastFacts`. The steps in Done start this episode done (the runtime keeps them so). Default: nothing; a harness that
  keeps state between options (a target it was after) can take it up here.

## An example (an invented game, for the shape only)

A game where the player must open a gate by pulling a lever, then escort a cart to town while wolves attack it.
Pulling needs the player within 150 of the lever and the Use key (E, found in the input mapping context); the code
says wolves have 60 health and the sword (left mouse) deals 20 per swing with a 0.6 s cooldown.

```csharp
class EscortHarness : Harness {
    string _lever, _cart;
    Vec _leverAt;

    public EscortHarness() {
        Goal = "Open the gate with the lever, then keep the cart alive until it reaches town";
        Tempo = 4.0;     // fights need quick reactions
        SwitchMargin = 0.2;
    }

    public override async Task Setup(Game game) {
        _lever = (await game.Actors("Lever"))[0];
        _cart = (await game.Actors("Cart"))[0];
        _leverAt = await game.Loc(_lever);
        await game.CalibrateLook();
    }

    public override async Task<Obs> Observe(Game game) {
        var wolves = await game.Actors("Wolf");
        var (pose, leverOn, cartHp, arrived, cartAt, wolfAt, wolfHp) = await game.Batch(
            () => game.Pose(), () => game.Get(_lever, "pulled"), () => game.Get(_cart, "Health"),
            () => game.Get(_cart, "arrived"), () => game.Loc(_cart), () => game.Locs(wolves),
            () => game.Map(wolves, w => game.Get(w, "Health")));
        var live = wolves.Select((w, i) => (Wolf: w, Hp: wolfHp.Value[i]))
            .Where(x => wolfAt.Value.ContainsKey(x.Wolf) && x.Hp.Ok && x.Hp.Value.Number > 0)
            .Select(x => (x.Wolf, At: wolfAt.Value[x.Wolf])).ToList();
        return new Obs
        {
            ["_pose"] = pose.Value, ["_wolves"] = live, ["gate_open"] = leverOn.Value.Truthy, ["cart_health"] = cartHp.Value,
            ["cart_arrived"] = arrived.Value.Truthy, ["cart"] = Where(pose, cartAt),
            ["wolves_near_cart"] = live.Count(w => w.At.Dist(cartAt) < 800),
            ["nearest_wolf"] = live.Count > 0 ? Where(pose, live.MinBy(w => w.At.Dist(pose.Value.At)).At) : null,
        };
    }

    public override List<Option> Options(Obs obs) {
        var options = new List<Option>();
        if (!obs.Bool("gate_open"))
            options.Add(new Option("pull_lever", "Walk to the lever and pull it to open the gate",
                (g, o) => GoAndPress(g, _lever, "E", within: 140)) {
                Facts = new() { ["lever"] = Where(obs.Get<Pose>("_pose"), _leverAt) },
                Effect = (a, b) => b.Bool("gate_open"),
            });
        var wolves = obs.Get<List<(string Wolf, Vec At)>>("_wolves");
        if (wolves.Count > 0)
            options.Add(new Option("fight_nearest_wolf", "Attack the wolf nearest to the player with the sword", Fight) {
                Facts = new() { ["wolf"] = obs["nearest_wolf"] }, Urgent = true,
                Effect = (a, b) => b.Get<List<(string, Vec)>>("_wolves").Count < a.Get<List<(string, Vec)>>("_wolves").Count,
            });
        options.Add(new Option("stay_with_cart", "Walk beside the cart as it travels",
            (g, o) => WalkTo(g, _cart, within: 250, timeout: 8)) {
            Facts = new() { ["cart"] = obs["cart"] },
        });
        return options;
    }

    async Task<Result> Fight(Game game, Obs obs) {
        var at = obs.Get<Pose>("_pose").At;
        var wolf = obs.Get<List<(string Wolf, Vec At)>>("_wolves").MinBy(w => w.At.Dist(at)).Wolf;
        var r = await WalkTo(game, wolf, within: 170, timeout: 6);
        if (!r)
            return r;
        for (var i = 0; i < 4; i++) {                  // 3 swings kill at full health; one spare
            await LookAt(game, wolf, tolerance: 6, timeout: 0.5);
            game.Press("LeftMouseButton");
            await Wait(game, 0.65);              // the cooldown from the code
            if (!await game.Exists(wolf) || await game.Get(wolf, "Health") <= 0)
                return new Result(true, "wolf down");
        }
        return new Result(false, "wolf still standing");
    }

    public override Status Status(Obs obs) {
        var health = obs.Get<GameValue>("cart_health").Number;
        if (health <= 0)
            return new Status("failed", "the cart was destroyed", new() { ["cart_health"] = health });
        if (obs.Bool("cart_arrived"))     // the cart's own flag, found in its code (set when it reaches town)
            return new Status("done", "the cart reached town", new() { ["cart_health"] = health });
        return new Status();
    }
}
```
