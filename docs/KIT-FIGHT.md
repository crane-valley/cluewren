# The kit: fights

## Research for a fight

A request whose last step is a fight: `FightTrial` against the real target first. A fight the player loses straight on is
won by a tactic, and the tactic is found in probes before the harness is written, never by playing the lost fight again:
measure the enemy first (`EnemyProfile`: its speed, reach, attack period, damage and regeneration against the player's;
does it come at all, or wait until hit?), then try the tactics the numbers allow, each in a probe until one wins:
strike-and-retreat (`StrikeAndRetreat` with the measured reach and recovery: one hit, out of its reach while it recovers,
back in), the player's defensive inputs from the controls (a block, a dodge, a guard) against real hits, range, terrain (a
height it can't climb), another instance or several at once, and what makes the player stronger (points, gear, a level).
The kill step then runs the winning tactic with the kit's controller (its numbers from the measurement) or a reflex (see
Reacting in time), never a plain melee it has seen lose.

A probe of a fight is a few shots or swings, enough to read the damage per hit: `Shoot(game, foe, timeout: 4)` or
`FightTrial(..., seconds: 5)`. Playing the whole fight is for the episode.

- `await FightTrial(game, target, key: "LeftMouseButton", seconds: 20, reach: null, heal: true)`: a measurement: get in reach
  of the target (an actor path or a class name: its nearest; teleported near it, then walked the last stretch so the
  player stands on the game's own ground), look at it, press `key` every 0.6 s, reading its health and the player's
  each swing; when the first swings land nothing, once closer (0.6 of the reach) -> a dictionary with swings, hits,
  damage_per_hit, target_health, player_health, damage_taken, killed, player_dead, stance (distance, height: the target's
  box bottom above the player's feet), closer, healed, verdict... Tells, before the harness is written, whether the fight
  can be won and how long it takes (`health: "Prop"` names the health property when Health/CurrentHealth/HP aren't the
  game's). heal: true writes the player's health back to what it was (reported), so the next measurement doesn't start
  hurt; a player the trial killed stays dead (player_dead: restart the level).
- `await EnemyProfile(game, target, seconds: 20, safeDistance: null)`: a measurement without a fight: the player watches the
  target from 1200 away (teleported there) for `seconds` -> a dictionary with comes (whether it came at the player),
  approach_speed, speed, player_speed, attack_range (the distance at the first drop of the player's health), attack_period,
  damage_per_hit, regen_per_s (its health rising on its own; after damage: run FightTrial first), summary... The player is
  kept alive (under 40% health it is teleported back) and healed afterwards.

## Controllers for a fight

- `Shoot(game, target, key: "LeftMouseButton", every: 0.3, closeTo: 500, alive: null, timeout: 10)`: ranged: turns to
  the actor and walks toward it while it is out of sight, aims, presses `key` every `every` seconds (the weapon's refire
  time) while the crosshair is on it, and walks in to `closeTo` while the crosshair misses, until alive() is false or the
  actor is gone. Fails with "no line of sight" when it still can't see the actor from beside it. `.Data`: shots.
- `await Foes(game, cls, health: "Health")` -> `List<Foe>`, the living ones, nearest first, each with `Actor`, `At`,
  `Distance`, `Health`, `Visible`, `CrosshairOn`. Made for Observe.
- `Melee(game, target, key, reach: 150, cooldown: 0.5, alive: null, timeout: 12)`: close in and strike every cooldown
  while in reach and aimed, until alive() is false. alive is a `Func<ValueTask<bool>>`, as in
  `alive: async () => await game.Get(wolf, "Health") > 0`.
- `StrikeAndRetreat(game, target, key, reach: 150, strikes: 1, cooldown: 0.5, retreat: null, recover: 2.0, alive: null,
  health: null, timeout: 30)`: hit and run, for a fight a straight `Melee` loses (the enemy out-trades the player in
  reach): close in, strike `strikes` times, back off to `retreat` (3 x reach) and wait `recover` (its attack's recovery:
  measure it in the code, don't guess) before closing in again, until alive() is false. `health` (a
  `Func<ValueTask<double?>>`: the player's) backs off at once when it drops mid-approach and ends with "player dead" at 0.
  `.Data`: strikes, approaches, seconds, damage (taken), hits (that lowered the target's Health); each approach is in the
  trace ("tactic").
- `AimAndStrike(game, target, key, strikes: 1, cooldown: 0.5)`: look at the target and press `key` `strikes` times:
  one volley, for tactics of your own that choose when to be in reach.

A whole harness for a ranged fight:

```csharp
class DownTheGuards : Harness {
    public DownTheGuards() {
        Goal = "Shoot down every guard without dying.";
        Tempo = 4;
        StallSeconds = 45;
    }

    public override async Task Setup(Game game) => await game.CalibrateLook();

    public override async Task<Obs> Observe(Game game) {
        var foes = await Foes(game, "Guard", health: "HP");
        var pose = await game.Pose();
        var pawn = await game.Pawn();
        return new Obs {
            ["my_health"] = pawn == "" ? 0 : await game.Num(pawn, "HP"),
            ["guards_left"] = foes.Count,
            ["guards"] = foes.Select(f => new { f.Health, f.Visible, where = Where(pose, f.At) }).ToList(),
            ["_foes"] = foes,
        };
    }

    public override List<Option> Options(Obs obs) {
        var foes = obs.Get<List<Foe>>("_foes");
        if (foes.Count == 0)
            return [new Option("wait", "No guard is around yet.", (g, o) => Wait(g, 1)), new Option("hold", "Hold position.", (g, o) => Wait(g, 2))];
        bool? Fewer(Obs a, Obs b) => b.Num("guards_left") < a.Num("guards_left");
        var near = foes[0].Actor;
        var weak = foes.MinBy(f => f.Health)!.Actor;
        return [
            new Option("shoot_nearest", "Shoot the nearest guard until it is down.", (g, o) => Shoot(g, near)) { MaxSeconds = 12, Effect = Fewer },
            new Option("shoot_weakest", "Shoot the guard with least health.", (g, o) => Shoot(g, weak)) { MaxSeconds = 12, Effect = Fewer },
            new Option("back_away", "Move away from the nearest guard.", (g, o) => KeepAway(g, near, timeout: 3)) { MaxSeconds = 5, Urgent = obs.Num("my_health") < 30 },
        ];
    }

    public override Status Status(Obs obs) {
        var evidence = new { guards_left = obs.Num("guards_left"), my_health = obs.Num("my_health") };
        if (obs.Num("my_health") <= 0)
            return new Status("failed", note: "The player died.", evidence: evidence);
        return new Status(obs.Num("guards_left") == 0 ? "done" : "running", evidence: evidence);
    }
}
```

## Reacting in time (dodging, blocking, jumping)

A decision by Jev takes about 0.2-0.3 s from what it saw to what the player does: too slow for a hit that lands a
fraction of a second after it shows. Reactions like that go in executor code, which runs every tick:

- Find the threat's tell in the code, the earliest sign that the hit is coming: the attacker's current animation and
  how far into it it is (an animation montage and its position, an animator state's time, an animation player's
  position), the stretches of it in which a hit lands (animation notifies or events, a damage window), a
  projectile's position and velocity, a charge-up flag. Turn it into seconds until the hit.
- Read the player's own move in the code: how long it protects (invincibility frames, a guard window) and from when,
  how long it lasts, what it costs (stamina, a cooldown), whether it cancels an attack of the player's own.
- Every tick, read the tell (one `game.Batch`) and make the move once the hit is less than a lead away: the time from
  the press until the protection starts, plus the step time (the summary's "Executor steps"). Measure the lead in
  play (hits taken against moves made), don't guess it: it changes with the frame rate.
- React once per hit (remember which hit it was) and not again before the move's recovery ends: a reflex that fires
  for as long as a threat lasts leaves the player dodging and never attacking. Attack in the openings, when the next
  hit is further off than an attack takes.
- Put the reflex in every executor that runs near the threat (one helper in lib.cs), so the player defends whatever
  Jev picked; Jev's choices are what reflexes can't decide (close in or back off, heal, which target).
- Record each hit taken with the threat's tell at that moment, so the trace shows which hits still land and whether
  the lead is early or late.
- TickHz up to the game's frame rate for reflex executors (no gain above it: every read waits for a frame).
