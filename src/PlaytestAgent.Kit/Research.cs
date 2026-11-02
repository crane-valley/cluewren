using System.Text.RegularExpressions;

namespace PlaytestAgent.Kit;

public sealed record Near(string Actor, Vec At, double Distance);

public sealed record Foe(string Actor, Vec At, double Distance, double Health, bool Visible, bool CrosshairOn);

public static partial class Research
{
    public const double SwingEvery = 0.6;
    public const int CloserAfter = 5;
    public const double WalkTimeout = 10.0;
    public const double SafeDistance = 1200.0;

    static readonly string[] HealthNames =
    [
        "Health", "CurrentHealth", "HP", "health", "Hp", "hp", "CurrentHP", "HealthPoints", "current_health", "Hitpoints",
        "HitPoints",
    ];

    static readonly string[] MaxHealthNames = ["MaxHealth", "HealthMax", "max_health", "MaxHP", "HPMax", "MaxHitPoints", "maxHealth"];

    [GeneratedRegex(@"health|\bhp\b|hitpoint", RegexOptions.IgnoreCase)]
    private static partial Regex LikelyHealth();

    [GeneratedRegex("max|regen|percent|ratio|bar|widget|text", RegexOptions.IgnoreCase)]
    private static partial Regex NotHealth();

    static GameException Wrapped(GameException e, string what) => new(e.Status, $"{what}: {e.Message}");

    public static async ValueTask<List<Near>> Nearest(Game game, string cls, int n = 5, double? within = null,
        CancellationToken ct = default)
    {
        try
        {
            return await Nearest(game, await game.Actors(cls, ct), n, within, ct);
        }
        catch (GameException e)
        {
            throw Wrapped(e, $"nearest({Fmt.Json(cls)})");
        }
    }

    public static async ValueTask<List<Near>> Nearest(Game game, IEnumerable<string> actors, int n = 5, double? within = null,
        CancellationToken ct = default)
    {
        try
        {
            var me = await game.Me(ct);
            var pawn = await game.Pawn(ct);
            var locs = await game.Locs(actors.Where(actor => actor != pawn).ToList(), ct);
            IEnumerable<Near> found = locs.Select(pair => new Near(pair.Key, pair.Value, me.Dist(pair.Value)))
                .OrderBy(near => near.Distance);
            if (within is { } limit)
                found = found.Where(near => near.Distance <= limit);
            return found.Take(n).Select(near => near with { Distance = Fmt.Round(near.Distance, 1) }).ToList();
        }
        catch (GameException e)
        {
            throw Wrapped(e, "nearest");
        }
    }

    public static async ValueTask<List<Foe>> Foes(Game game, string cls, string health = "Health", CancellationToken ct = default)
    {
        var actors = await game.Actors(cls, ct);
        var healths = await game.Map(actors, actor => game.Num(actor, health, ct));
        var living = new Dictionary<string, double>();
        for (var i = 0; i < actors.Count; i++)
            if (healths[i].Ok && healths[i].Value > 0)
                living[actors[i]] = healths[i].Value;
        if (living.Count == 0)
            return [];
        var pose = await game.Pose(ct);
        var locs = await game.Locs(living.Keys, ct);
        var sees = await game.Sees(living.Keys, ct);
        return locs.Select(pair => new Foe(pair.Key, pair.Value, Fmt.Round(pair.Value.Dist(pose.At), 1), living[pair.Key],
                sees.ContainsKey(pair.Key), sees.TryGetValue(pair.Key, out var seen) && seen.CrosshairOn))
            .OrderBy(foe => foe.Distance).ToList();
    }

    static async ValueTask<(Vec Point, double Yaw)> Landing(Game game, string actor, string near, double distance,
        CancellationToken ct)
    {
        var (center, ext) = await game.Bounds(near, ct);
        Vec at, mine;
        try
        {
            at = await game.Loc(actor, ct);
            (_, mine) = await game.Bounds(actor, ct);
        }
        catch (GameException)
        {
            (at, mine) = (center + new Vec(1), default);
        }
        var side = (at - center).Flat;
        side = side.Length > 1e-3 ? side.Unit : new Vec(1);
        var point = center + side * distance;
        point = new Vec(point.X, point.Y, center.Z - ext.Z + mine.Z + 5.0);
        return (point, Geom.YawOf(center - point));
    }

    static async ValueTask TeleportActor(Game game, string actor, Vec point, double? yaw, string why, CancellationToken ct)
    {
        var rotation = game.Engine.RotationToNative(yaw ?? 0);
        await game.Call(actor, "K2_TeleportTo", new Dictionary<string, object?>
        {
            ["DestLocation"] = game.ToNative(point), ["DestRotation"] = rotation,
        }, ct);
        game.Writes[^1]["why"] = why;
        if (yaw is null || actor != await game.Pawn(ct))
            return;
        try
        {
            // The view is the controller's: the pawn's rotation alone leaves the camera where it was.
            await game.Call(await game.Controller(ct), "SetControlRotation",
                new Dictionary<string, object?> { ["NewRotation"] = rotation }, ct);
        }
        catch (GameException)
        {
        }
    }

    public static async ValueTask<Dictionary<string, object?>> Teleport(Game game, Target to, string? actor = null,
        string why = "research", double distance = 150, CancellationToken ct = default)
    {
        actor ??= await game.Pawn(ct);
        if (actor.Length == 0)
            throw new GameException(400, "teleport: no player pawn now (dead? a menu?): give actor=");
        Vec point;
        Vec at;
        double? yaw = null;
        try
        {
            if (to.Source is not null)
                point = await Control.PointOf(game, to, ct);
            else if (to.Actor is { } near)
                (point, yaw) = await Landing(game, actor, near, distance, ct);
            else
                point = to.Point ?? default;
            if (game.Engine is Unreal)
                await TeleportActor(game, actor, point, yaw, why, ct);
            else
                await game.Set(actor, game.Engine is Godot ? "global_position" : "position", game.ToNative(point), why, ct);
            await game.Clock.Delay(0.1, ct);
            at = await game.Loc(actor, ct);
        }
        catch (GameException e)
        {
            throw Wrapped(e, $"teleport({to})");
        }
        var landed = at.Dist(point) <= 100.0;
        var result = new Dictionary<string, object?>
        {
            ["actor"] = actor, ["asked"] = point.Rounded(), ["at"] = at.Rounded(), ["landed"] = landed,
        };
        if (!landed)
            result["note"] = $"the game put it {Fmt.F(at.Dist(point), 0)} from where it was asked (blocked by collision, or "
                             + "the game's own movement component moved it back: try a point a little further out)";
        return result;
    }

    static ValueTask<GameValue> Read(Game game, string obj, string? prop, CancellationToken ct) =>
        prop is null ? default : game.Get(obj, prop, ct);

    static async ValueTask<string?> HealthProp(Game game, string obj, string? prefer, CancellationToken ct)
    {
        var names = prefer is not null ? new[] { prefer } : HealthNames;
        var values = await game.Map(names, name => game.Get(obj, name, ct));
        for (var i = 0; i < names.Length; i++)
            if (values[i].Ok && values[i].Value.IsNumber)
                return names[i];
        if (prefer is not null)
            return null;
        List<string> likely;
        try
        {
            likely = (await game.Describe(obj, ct))["Properties"].Items.Select(p => p["Name"].Text)
                .Where(p => p is not null && LikelyHealth().IsMatch(p) && !NotHealth().IsMatch(p)).Select(p => p!).ToList();
        }
        catch (GameException)
        {
            return null;
        }
        var read = await game.Map(likely, name => game.Get(obj, name, ct));
        for (var i = 0; i < likely.Count; i++)
            if (read[i].Ok && read[i].Value.IsNumber)
                return likely[i];
        return null;
    }

    static async ValueTask<string> Actor(Game game, string target, CancellationToken ct)
    {
        try
        {
            await game.Loc(target, ct);
            return target;
        }
        catch (GameException)
        {
            var near = await Nearest(game, target, 1, null, ct);
            return near.Count > 0 ? near[0].Actor : throw new GameException(400, $"no actor {target} in the level now");
        }
    }

    static async ValueTask<double?> MaxHealth(Game game, string obj, string? prop, CancellationToken ct)
    {
        var names = MaxHealthNames.ToList();
        if (prop is not null)
            names = [$"Max{prop}", $"{prop}Max", .. names.Where(n => n != $"Max{prop}" && n != $"{prop}Max")];
        var values = await game.Map(names, name => game.Get(obj, name, ct));
        foreach (var value in values)
            if (value.Ok && value.Value.Number is { } number)
                return number;
        return null;
    }

    static async ValueTask<Dictionary<string, object?>?> Stance(Game game, string pawn, string target, CancellationToken ct)
    {
        var (mine, theirs) = await game.Batch(() => game.Bounds(pawn, ct), () => game.Bounds(target, ct));
        if (!mine.Ok || !theirs.Ok)
            return null;
        var ((cp, ep), (ct2, et)) = (mine.Value, theirs.Value);
        return new Dictionary<string, object?>
        {
            ["distance"] = Fmt.Round(cp.Dist2d(ct2)), ["height"] = Fmt.Round(ct2.Z - et.Z - (cp.Z - ep.Z)),
        };
    }

    static async ValueTask<bool> Heal(Game game, string pawn, string prop, double value, string why, CancellationToken ct)
    {
        try
        {
            await game.Set(pawn, prop, value, why, ct);
            var back = (await game.Get(pawn, prop, ct)).Number;
            return back is { } number && Math.Abs(number - value) <= 0.5;
        }
        catch (GameException)
        {
            return false;
        }
    }

    static async ValueTask<bool> Alive(Game game, double? health, CancellationToken ct)
    {
        if (health is <= 0)
            return false;
        try
        {
            return (await game.Context(fresh: true, ct))["character"].Truthy;
        }
        catch (GameException)
        {
            return true;
        }
    }

    static async Task<Result> PauseFor(Game game, double seconds, CancellationToken ct)
    {
        var t0 = game.T;
        var end = game.Clock.Monotonic() + 3 * seconds + 1;
        while (game.Clock.Monotonic() < end)
        {
            await game.Tick();
            try
            {
                await game.Context(fresh: true, ct);
            }
            catch (GameException)
            {
            }
            if (game.T - t0 >= seconds)
                break;
        }
        return new Result(true, "waited");
    }

    public static async Task<Dictionary<string, object?>> FightTrial(Game game, string target, string key = "LeftMouseButton",
        double seconds = 20, double? reach = null, string? health = null, double every = SwingEvery, bool heal = true,
        CancellationToken ct = default)
    {
        var within = reach is > 0 or < 0 ? reach.Value : 120;
        var clock = game.Clock;
        var (swings, hits, dealt, killed, took, dead) = (0, 0, 0.0, false, 0.0, false);
        Result? aim = null;
        Dictionary<string, object?>? stance = null, closer = null;
        string? walkedWhy = null;
        double? lastT, lastP, tHealth, pHealth;
        string pawn;
        string? tProp, pProp;
        double? pMax;
        Dictionary<string, object?> land;
        Result result;
        double started;

        async ValueTask<(double? Target, double? Player, bool Exists)> ReadAll()
        {
            var (a, b, c) = await game.Batch(() => Read(game, target, tProp, ct), () => Read(game, pawn, pProp, ct),
                () => game.Exists(target, ct));
            return (a.Ok ? a.Value.Number : null, b.Ok ? b.Value.Number : null, c.Ok && c.Value);
        }

        try
        {
            target = await Actor(game, target, ct);
            pawn = await game.Pawn(ct);
            if (pawn.Length == 0)
                throw new GameException(400, "no player pawn now");
            land = await Teleport(game, target, why: $"fight trial against {target}", distance: within + 200.0, ct: ct);
            (tProp, pProp) = (await HealthProp(game, target, health, ct), await HealthProp(game, pawn, health, ct));
            pMax = pProp is not null ? await MaxHealth(game, pawn, pProp, ct) : null;
            var first = await ReadAll();
            (tHealth, pHealth) = (first.Target, first.Player);
            (lastT, lastP) = (tHealth, pHealth);

            async Task<Result?> Swing(CancellationToken c)
            {
                game.Press(key);
                swings++;
                await PauseFor(game, every, c);
                var (tNow, pNow, exists) = await ReadAll();
                if (lastT is { } t && tNow is { } tn && tn < t)
                {
                    hits++;
                    dealt += t - tn;
                }
                if (lastP is { } p && pNow is { } pn && pn < p)
                    took += p - pn;
                lastT = tNow ?? lastT;
                lastP = pNow ?? lastP;
                if (!exists || tNow is <= 0)
                {
                    killed = true;
                    return new Result(true, "target down");
                }
                if (pNow is <= 0)
                {
                    dead = true;
                    return new Result(false, "the player died");
                }
                return null;
            }

            async Task<Result> Trial(CancellationToken c)
            {
                var walked = await Control.WalkTo(game, target, within: within, timeout: WalkTimeout, ct: c);
                walkedWhy = walked.Why;
                aim = await Control.LookAt(game, target, timeout: 1.5, ct: c);
                stance = await Stance(game, pawn, target, c);
                var begun = game.T;
                var end = clock.Monotonic() + seconds;
                var stepped = false;
                while (clock.Monotonic() < end)
                {
                    var over = await Swing(c);
                    if (over is not null)
                    {
                        over.Data["seconds"] = game.T - begun;
                        return over;
                    }
                    if (!stepped && hits == 0 && swings >= CloserAfter && end - clock.Monotonic() >= 3 * every)
                    {
                        stepped = true;
                        var paused = clock.Monotonic();
                        await Control.WalkTo(game, target, within: 0.6 * within, timeout: Math.Min(5.0, WalkTimeout), ct: c);
                        await Control.LookAt(game, target, timeout: 1.5, ct: c);
                        closer = await Stance(game, pawn, target, c);
                        end += clock.Monotonic() - paused;
                    }
                }
                return new Result(false, "time up", new Dictionary<string, object?> { ["seconds"] = game.T - begun });
            }

            started = clock.Monotonic();
            result = await Control.Run(game, Trial, seconds + 2 * WalkTimeout + 5.0, ct: ct);
        }
        catch (GameException e)
        {
            throw Wrapped(e, $"fight_trial({Fmt.Json(target)})");
        }
        var played = Fmt.Round(result.Data.GetValueOrDefault("seconds") is double s ? s : clock.Monotonic() - started, 1);
        double? perHit = hits > 0 ? Fmt.Round(dealt / hits, 2) : null;
        dead = dead || !await Alive(game, lastP, ct);
        bool? healed = null;
        if (heal && !dead && pProp is not null && pHealth is { } was && lastP != was)
            healed = await Heal(game, pawn, pProp, was, "research: undo the trial's damage", ct);
        var trial = new Dictionary<string, object?>
        {
            ["target"] = target, ["swings"] = swings, ["hits"] = hits, ["damage_per_hit"] = perHit,
            ["target_health"] = new[] { tHealth, lastT }, ["player_health"] = new[] { pHealth, lastP },
            ["player_health_max"] = pMax, ["damage_taken"] = Fmt.Round(took, 2), ["seconds"] = played, ["killed"] = killed,
            ["player_dead"] = dead, ["stance"] = stance, ["closer"] = closer, ["healed"] = healed,
            ["health_props"] = new Dictionary<string, object?> { ["target"] = tProp, ["player"] = pProp },
            ["aim"] = aim?.Why, ["landed"] = land.GetValueOrDefault("landed"), ["walked"] = walkedWhy,
            ["stopped"] = result.Why,
        };
        trial["verdict"] = Verdict(trial, every);
        return trial;
    }

    static string At(Dictionary<string, object?>? stance) =>
        stance is null ? "an unread distance" : $"{stance["distance"]} units and {stance["height"]} height";

    public static string Verdict(Dictionary<string, object?> r, double every)
    {
        var words = FightWords(r, every);
        var player = (double?[])r["player_health"]!;
        var props = (Dictionary<string, object?>)r["health_props"]!;
        if (r["player_dead"] is true)
            words += "; the player died: the level must be restarted before the next measurement (run_harness restarts "
                     + "it by default)";
        else if (r["healed"] is true)
            words += $"; the player's health was written back to {Fmt.G(player[0] ?? 0)}";
        else if (r["healed"] is false)
            words += $"; the player is left at {Fmt.G(player[1] ?? 0)} health (its {props["player"]} couldn't be written back)";
        return words;
    }

    static string FightWords(Dictionary<string, object?> r, double every)
    {
        var (hits, swings) = ((int)r["hits"]!, (int)r["swings"]!);
        var (seconds, taken) = (Convert.ToDouble(r["seconds"]), Convert.ToDouble(r["damage_taken"]));
        var props = (Dictionary<string, object?>)r["health_props"]!;
        var closer = r["closer"] as Dictionary<string, object?>;
        if (r["killed"] is true)
            return $"killed: {hits} hits of {swings} swings in {Fmt.G(seconds)} s; the player lost {Fmt.G(taken)} health meanwhile";
        if (props["target"] is null)
            return "no health property found on the target (tried Health, CurrentHealth, HP... and its described "
                   + "properties): give health=<name>, or it may keep health on a component";
        if (hits == 0)
        {
            if (closer is not null)
                return $"no hit from {At(r["stance"] as Dictionary<string, object?>)}; closer ({closer["distance"]}) neither: "
                       + $"not aimed (aim: {r["aim"]}), the wrong key, a needed item or mode, an invulnerable target, or a "
                       + $"reach under {closer["distance"]}";
            return $"no swing of {swings} changed its health from {At(r["stance"] as Dictionary<string, object?>)}: out of "
                   + $"reach, not aimed (aim: {r["aim"]}), the wrong key, a needed item or mode, or an invulnerable target";
        }
        var target = (double?[])r["target_health"]!;
        var perHit = Convert.ToDouble(r["damage_per_hit"]);
        var left = target[1] ?? 0.0;
        var rate = perHit * hits / Math.Max(seconds, every);
        var toKill = rate > 0 ? left / rate : double.PositiveInfinity;
        var hitsLeft = Math.Ceiling(left / perHit);
        var words = $"{hitsLeft:0} more hits ({Fmt.G(perHit)} each) would kill it, about {Fmt.F(toKill, 0)} s more at this "
                    + $"rate ({hits} hits in {swings} swings over {Fmt.G(seconds)} s)";
        var player = ((double?[])r["player_health"]!)[1];
        if (taken > 0 && player is { } p && seconds > 0)
        {
            var takenRate = taken / seconds;
            var toDie = takenRate > 0 ? p / takenRate : double.PositiveInfinity;
            var lost = $"lost {Fmt.G(taken)} in {Fmt.G(seconds)} s, {Fmt.G(p)} left";
            words += toDie < toKill
                ? $"; but the player would die first, in about {Fmt.F(toDie, 0)} s ({lost})"
                : $"; the player would outlast it ({lost})";
        }
        else if (taken == 0)
        {
            words += "; the player took no damage";
        }
        return words;
    }

    static async ValueTask<double?> PlayerSpeed(Game game, CancellationToken ct)
    {
        try
        {
            return (await Control.Movement(game, ct)).WalkSpeed;
        }
        catch (GameException)
        {
            return null;
        }
    }

    static (long Speed, long Approach) Rates(List<(double T, Vec Target, Vec Me)> samples, double window = 0.2)
    {
        var (speed, approach) = (0.0, 0.0);
        var j = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var (t0, tp0, me0) = samples[i];
            while (j < samples.Count && samples[j].T - t0 < window)
                j++;
            if (j >= samples.Count)
                break;
            var (t1, tp1, me1) = samples[j];
            var dt = t1 - t0;
            speed = Math.Max(speed, tp0.Dist(tp1) / dt);
            approach = Math.Max(approach, (tp0.Dist2d(me0) - tp1.Dist2d(me1)) / dt);
        }
        return (Fmt.Round(speed), Fmt.Round(approach));
    }

    public static async Task<Dictionary<string, object?>> EnemyProfile(Game game, string target, double seconds = 20,
        double? safeDistance = null, bool heal = true, CancellationToken ct = default)
    {
        var safe = safeDistance is > 0 or < 0 ? safeDistance.Value : SafeDistance;
        var samples = new List<(double T, Vec Target, Vec Me)>();
        var healths = new List<(double T, double? Target, double? Player)>();
        var stopped = "time up";
        string pawn;
        string? tProp, pProp;
        double? pMax, tHealth, pHealth;
        var clock = game.Clock;

        async ValueTask<(Read<Vec>, Read<Vec>, Read<GameValue>, Read<GameValue>)> ReadAll() =>
            await game.Batch(() => game.Loc(target, ct), () => game.Me(ct), () => Read(game, target, tProp, ct),
                () => Read(game, pawn, pProp, ct));

        try
        {
            target = await Actor(game, target, ct);
            pawn = await game.Pawn(ct);
            if (pawn.Length == 0)
                throw new GameException(400, "no player pawn now");
            await Teleport(game, target, why: $"enemy profile of {target}", distance: safe, ct: ct);
            (tProp, pProp) = (await HealthProp(game, target, null, ct), await HealthProp(game, pawn, null, ct));
            pMax = pProp is not null ? await MaxHealth(game, pawn, pProp, ct) : null;
            var (tp0, me0, th0, ph0) = await ReadAll();
            if (!tp0.Ok)
                throw tp0.Error!;
            if (!me0.Ok)
                throw me0.Error!;
            (tHealth, pHealth) = (th0.OrDefault.Number, ph0.OrDefault.Number);
            var floor = 0.4 * (pMax ?? pHealth ?? 0.0);

            async Task<Result> Watch(CancellationToken c)
            {
                var end = clock.Monotonic() + seconds;
                var (tLast, wLast) = (game.T, clock.Monotonic());
                var elapsed = 0.0;
                while (clock.Monotonic() < end)
                {
                    var (tp, me, th, ph) = await ReadAll();
                    if (!tp.Ok)
                    {
                        stopped = "target gone";
                        break;
                    }
                    if (!me.Ok)
                    {
                        stopped = "the player's pawn is gone";
                        break;
                    }
                    var (tNow, wNow) = (game.T, clock.Monotonic());
                    elapsed += tNow > tLast ? tNow - tLast : wNow - wLast;
                    (tLast, wLast) = (tNow, wNow);
                    samples.Add((elapsed, tp.Value, me.Value));
                    var pNow = ph.OrDefault.Number;
                    healths.Add((elapsed, th.OrDefault.Number, pNow));
                    if (pNow is <= 0)
                    {
                        stopped = "the player died";
                        break;
                    }
                    if (pNow is { } now && floor != 0 && now < floor)
                    {
                        stopped = $"the player's health fell under 40% ({Fmt.G(now)}): teleported back to {Fmt.G(safe)}";
                        await Teleport(game, target, why: $"enemy profile of {target}: back to safety", distance: safe, ct: c);
                        break;
                    }
                    if (samples.Count == 1)
                        await Control.LookAt(game, target, timeout: 1.5, ct: c);
                    await game.Tick();
                }
                return new Result(true, stopped);
            }

            await Control.Run(game, Watch, seconds + 10.0, ct: ct);
        }
        catch (GameException e)
        {
            throw Wrapped(e, $"enemy_profile({Fmt.Json(target)})");
        }
        var profile = Profile(target, samples, healths, tHealth, pHealth);
        profile["player_health_max"] = pMax;
        profile["player_speed"] = await PlayerSpeed(game, ct);
        profile["stopped"] = stopped;
        profile["health_props"] = new Dictionary<string, object?> { ["target"] = tProp, ["player"] = pProp };
        var pLast = ((double?[])profile["player_health"]!)[1];
        var dead = !await Alive(game, pLast, ct);
        profile["player_dead"] = dead;
        profile["healed"] = null;
        if (heal && !dead && pProp is not null && pHealth is { } was && pLast is { } last && last != was)
            profile["healed"] = await Heal(game, pawn, pProp, was, "research: undo the profile's damage", ct);
        profile["summary"] = ProfileSummary(profile);
        return profile;
    }

    static Dictionary<string, object?> Profile(string target, List<(double T, Vec Target, Vec Me)> samples,
        List<(double T, double? Target, double? Player)> healths, double? tHealth, double? pHealth)
    {
        var seconds = samples.Count > 0 ? Fmt.Round(samples[^1].T, 1) : 0.0;
        var distances = samples.Select(s => s.Target.Dist2d(s.Me)).ToList();
        long? startD = distances.Count > 0 ? Fmt.Round(distances[0]) : null;
        long? minD = distances.Count > 0 ? Fmt.Round(distances.Min()) : null;
        var (speed, approach) = Rates(samples);
        var comes = distances.Count > 0 && startD - minD > Math.Max(200.0, 0.25 * startD!.Value);
        var hits = new List<(double T, double Damage, double Distance)>();
        var tRise = 0.0;
        var (lastT, lastP) = (healths.Count > 0 ? healths[0].Target : tHealth, pHealth);
        for (var i = 0; i < healths.Count; i++)
        {
            var (t, th, ph) = healths[i];
            if (lastP is { } p && ph is { } now && now < p)
                hits.Add((t, p - now, distances[i]));
            if (lastT is { } was && th is { } tn && tn > was)
                tRise += tn - was;
            lastT = th ?? lastT;
            lastP = ph ?? lastP;
        }
        var gaps = hits.Zip(hits.Skip(1), (a, b) => b.T - a.T).ToList();
        return new Dictionary<string, object?>
        {
            ["target"] = target, ["seconds"] = seconds, ["start_distance"] = startD, ["min_distance"] = minD,
            ["comes"] = comes, ["approach_speed"] = approach, ["speed"] = speed,
            ["attack_range"] = hits.Count > 0 ? Fmt.Round(hits[0].Distance) : null,
            ["attack_period"] = gaps.Count > 0 ? Fmt.Round(gaps.Sum() / gaps.Count, 2) : null,
            ["damage_per_hit"] = hits.Count > 0 ? Fmt.Round(hits.Sum(h => h.Damage) / hits.Count, 2) : null,
            ["hits"] = hits.Count, ["damage_taken"] = Fmt.Round(hits.Sum(h => h.Damage), 2),
            ["target_health"] = new[] { tHealth, lastT },
            ["regen_per_s"] = tHealth is not null ? (seconds != 0 ? Fmt.Round(tRise / seconds, 2) : 0.0) : null,
            ["player_health"] = new[] { pHealth, lastP },
        };
    }

    public static string ProfileSummary(Dictionary<string, object?> r)
    {
        var parts = new List<string>();
        var player = r.GetValueOrDefault("player_speed") as double?;
        var walks = player is { } speed ? $" (the player walks {Fmt.G(speed)})" : "";
        var approach = Convert.ToDouble(r["approach_speed"]);
        if (r["comes"] is true)
        {
            var words = $"comes at the player at {Fmt.G(approach)}/s{walks}";
            if (player is { } walk)
                words += approach < 0.9 * walk ? ": can be out-run" : ": can't be out-run";
            parts.Add(words);
        }
        else
        {
            parts.Add($"doesn't come at the player in {Fmt.G(Convert.ToDouble(r["seconds"]))} s from {r["start_distance"]} away "
                      + $"(passive until hit?); moves at {Fmt.G(Convert.ToDouble(r["speed"]))}/s at most{walks}");
        }
        if ((int)r["hits"]! > 0)
        {
            var words = $"hits for {Fmt.G(Convert.ToDouble(r["damage_per_hit"]))}";
            if (r["attack_period"] is { } period)
                words += $" every {Fmt.G(Convert.ToDouble(period))} s";
            words += $" from {r["attack_range"]} away";
            parts.Add(words);
        }
        else
        {
            parts.Add($"never attacked (nearest {r["min_distance"]})");
        }
        if (r["regen_per_s"] is null)
            parts.Add("no health property found on it");
        else if (Convert.ToDouble(r["regen_per_s"]) > 0)
            parts.Add($"health regenerates {Fmt.G(Convert.ToDouble(r["regen_per_s"]))}/s");
        else
            parts.Add("health doesn't regenerate on its own");
        if (r.GetValueOrDefault("player_dead") is true)
            parts.Add("the player died: the level must be restarted (run_harness restarts it by default)");
        else if ((string?)r.GetValueOrDefault("stopped") is { } stopped && stopped != "time up")
            parts.Add($"stopped early: {stopped}");
        return string.Join("; ", parts);
    }

    public static async ValueTask<(string Called, GameValue Result)> CallAny(Game game, string obj, IEnumerable<string> names,
        object? parameters = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var name in names)
        {
            try
            {
                return (name, await game.Call(obj, name, parameters, ct));
            }
            catch (GameException e)
            {
                errors.Add($"{name}: {e.Message}");
            }
        }
        throw new GameException(400, $"call_any({obj}): none accepted: " + string.Join("; ", errors));
    }
}
