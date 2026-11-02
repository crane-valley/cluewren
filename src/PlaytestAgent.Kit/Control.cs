namespace PlaytestAgent.Kit;

public readonly struct Target
{
    Target(Vec? point, string? actor, Func<ValueTask<Target>>? source)
    {
        Point = point;
        Actor = actor;
        Source = source;
    }

    public Vec? Point { get; }

    public string? Actor { get; }

    public Func<ValueTask<Target>>? Source { get; }

    public bool IsActor => Actor is not null;

    public static implicit operator Target(Vec point) => new(point, null, null);

    public static implicit operator Target(string actor) => new(null, actor, null);

    public static Target Of(Func<Vec> source) => new(null, null, () => new ValueTask<Target>(source()));

    public static Target Of(Func<string> source) => new(null, null, () => new ValueTask<Target>(source()));

    public static Target Of(Func<ValueTask<Target>> source) => new(null, null, source);

    public static Target Of(Func<ValueTask<Vec>> source) => new(null, null, async () => await source());

    public override string ToString() => Actor ?? Point?.ToString() ?? "(a callable)";
}

public static partial class Control
{
    public const double TurnGain = 0.8;
    public const double MaxTurnPerTick = 60.0;
    public const int MinCorrections = 6;
    public const double SonarEvery = 0.2;
    public const int SonarRing = 12;
    public const double WaypointReached = 45.0;
    public const double NoTurnGiveUp = 2.0;
    public const double ReplanAhead = 600.0;
    public const double ReverseAhead = 200.0;
    public const double ReverseHold = 3.0;
    public const double TurnBack = 100.0;

    static readonly double[] DefaultTurns = [0, -30, 30, -60, 60, -90, 90, 180];

    public static async ValueTask<Vec> PointOf(Game game, Target target, CancellationToken ct = default)
    {
        if (target.Source is { } source)
            target = await source();
        if (target.Actor is { } actor)
            return (await game.Bounds(actor, ct)).Center;
        return target.Point ?? default;
    }

    sealed class Pace(IClock clock)
    {
        double? _last;
        double _dt = 0.1;

        public double Step()
        {
            var now = clock.Monotonic();
            if (_last is { } last)
                _dt = Math.Min(1.0, Math.Max(0.02, now - last));
            _last = now;
            return _dt;
        }
    }

    static Dictionary<string, object?> D(params (string Key, object? Value)[] items)
    {
        var data = new Dictionary<string, object?>(items.Length);
        foreach (var (key, value) in items)
            data[key] = value;
        return data;
    }

    static double[] Pair(double right, double up) => [Fmt.Round(right, 1), Fmt.Round(up, 1)];

    static double Lead(Pose pose, Vec goal, double dt)
    {
        var v = pose.Velocity;
        var to = goal - pose.At;
        var n = Math.Sqrt(to.X * to.X + to.Y * to.Y);
        if (n < 1e-6)
            return 0.0;
        return Math.Max(0.0, (v.X * to.X + v.Y * to.Y) / n) * dt;
    }

    static async ValueTask<(double Right, double Up)> FlickToward(Game game, double rightDeg, double upDeg,
        CancellationToken ct)
    {
        var look = await game.CalibrateLook(ct: ct);
        var (rx, ry) = (look.X ?? 0, look.Y ?? 0);
        var r = Math.Max(-MaxTurnPerTick, Math.Min(MaxTurnPerTick, rightDeg * TurnGain));
        var u = Math.Max(-MaxTurnPerTick, Math.Min(MaxTurnPerTick, upDeg * TurnGain));
        game.Mouse(rx != 0 ? r / rx : 0, ry != 0 ? u / ry : 0);
        return (rx != 0 ? r : 0, ry != 0 ? u : 0);
    }

    public static async Task<Result> Wait(Game game, double seconds, CancellationToken ct = default)
    {
        var clock = game.Clock;
        var lastT = game.T;
        var changed = clock.Monotonic();
        var (credited, check) = (changed, changed);
        var waited = 0.0;
        var end = changed + seconds * 3 + 1;
        while (waited < seconds && clock.Monotonic() < end)
        {
            await game.Tick();
            var (now, t) = (clock.Monotonic(), game.T);
            if (t <= lastT && now - changed > 0.4 && now - check > 0.4)
            {
                try
                {
                    await game.Context(fresh: true, ct);
                }
                catch (GameException)
                {
                }
                check = now;
                t = game.T;
            }
            if (t > lastT)
            {
                waited += t - lastT;
                (lastT, changed, credited) = (t, now, now);
            }
            else if (now - changed > 0.4)
            {
                waited += now - Math.Max(credited, changed);
                credited = now;
            }
        }
        return new Result(true, "waited");
    }

    public static async Task<Result> Until(Game game, Func<ValueTask<bool>> condition, double timeout = 5,
        CancellationToken ct = default)
    {
        var t0 = game.Clock.Monotonic();
        while (game.Clock.Monotonic() - t0 < timeout)
        {
            if (await condition())
                return new Result(true, "condition met");
            await game.Tick();
        }
        return new Result(false, "timed out");
    }

    public static Task<Result> Until(Game game, Func<bool> condition, double timeout = 5, CancellationToken ct = default) =>
        Until(game, () => new ValueTask<bool>(condition()), timeout, ct);

    public static async ValueTask<double> FitTolerance(Game game, Target target, Vec? eye = null,
        CancellationToken ct = default)
    {
        if (target.Actor is not { } actor)
            return 2.5;
        Vec center, ext;
        try
        {
            (center, ext) = await game.Bounds(actor, ct);
            eye ??= (await game.Pose(ct)).Eye;
        }
        catch (GameException)
        {
            return 2.5;
        }
        ReadOnlySpan<double> sides = [Math.Abs(ext.X), Math.Abs(ext.Y), Math.Abs(ext.Z)];
        var size = Math.Max(Math.Min(sides[0], sides[1]), Math.Min(Math.Max(sides[0], sides[1]), sides[2]));
        var spans = 2 * Geom.ToDegrees(Math.Atan2(size, Math.Max(1.0, center.Dist(eye.Value))));
        return Math.Max(0.4, Math.Min(2.5, spans / 4));
    }

    public static async Task<Result> LookAt(Game game, Target target, double? tolerance = null, double timeout = 1.5,
        bool level = false, CancellationToken ct = default)
    {
        var clock = game.Clock;
        var t0 = clock.Monotonic();
        var steps = 0;
        var fit = tolerance ?? (level ? 2.5 : await FitTolerance(game, target, ct: ct));
        (double Yaw, double Pitch, double Size)? sent = null;
        var waits = 0;
        var k = new[] { 1.0, 1.0 };
        (double Yaw, double Pitch, double Right, double Up, double SentUp)? before = null;
        var stalled = 0;
        while (true)
        {
            Pose p;
            Vec point;
            try
            {
                p = await game.Pose(ct);
                point = await PointOf(game, target, ct);
            }
            catch (GameException e)
            {
                return new Result(false, $"can't read: {e.Message}");
            }
            if (sent is { } was && waits < 6
                                && Math.Abs(Geom.Wrap(p.Yaw - was.Yaw)) + Math.Abs(p.Pitch - was.Pitch) < 0.9 * was.Size)
            {
                waits++;
                await game.Tick();
                continue;
            }
            sent = null;
            waits = 0;
            var (right, up) = await game.AimError(point, p, ct);
            if (level)
                up = -p.Pitch;
            if (Math.Abs(right) <= fit && Math.Abs(up) <= fit)
                return new Result(true, "on target", D(("error", Pair(right, up))));
            var below = p.Eye.Z - point.Z;
            if (!level && below > 0 && point.Dist2d(p.Eye) < 0.15 * below && point.Dist2d(p.At) < 80)
                return new Result(false, "it is right below the player: step back from it (1-2 m) to aim at it",
                    D(("error", Pair(right, up)), ("distance", Fmt.Round(point.Dist2d(p.At)))));
            if (before is { } b)
            {
                var turned = (Yaw: Geom.Wrap(p.Yaw - b.Yaw), Pitch: p.Pitch - b.Pitch);
                if (Math.Abs(turned.Yaw) >= 1.0)
                    k[0] = Math.Max(0.25, Math.Min(2.0, (b.Right - right) / turned.Yaw));
                if (Math.Abs(turned.Pitch) >= 1.0)
                    k[1] = Math.Max(0.25, Math.Min(2.0, (b.Up - up) / turned.Pitch));
                stalled = Math.Abs(b.SentUp) >= 1.0 && Math.Abs(turned.Pitch) < 0.15 * Math.Abs(b.SentUp) ? stalled + 1 : 0;
                if (stalled >= 2 && Math.Abs(up) > fit)
                    return new Result(false,
                        $"the view stops pitching {(up < 0 ? "down" : "up")} at {Fmt.F(p.Pitch, 0)} degrees (the camera's "
                        + $"limit), {Fmt.F(Math.Abs(up))} short of the target: "
                        + (up < 0 ? "it is too close below the camera: stand further from it"
                            : "it is too high above: stand further from it"),
                        D(("error", Pair(right, up)), ("pitch", Fmt.Round(p.Pitch, 1))));
            }
            var elapsed = clock.Monotonic() - t0;
            if ((elapsed > timeout && steps >= MinCorrections) || elapsed > 4 * timeout)
            {
                if (game.Look.X is null or 0)
                    return new Result(false, "the mouse doesn't turn the view (yet?): no effect measured; controls not "
                                             + "active (an intro, a menu, a cutscene)?", D(("error", Pair(right, up))));
                return new Result(false, "turn timed out", D(("error", Pair(right, up))));
            }
            var (r, u) = await FlickToward(game, right / k[0], up / k[1], ct);
            before = (p.Yaw, p.Pitch, right, up, u);
            if (Math.Abs(r) + Math.Abs(u) >= 0.5)
                sent = (p.Yaw, p.Pitch, Math.Abs(r) + Math.Abs(u));
            steps++;
            await game.Tick();
        }
    }

    public static async Task<Result> Track(Game game, Target target, double seconds, double tolerance = 1.0,
        CancellationToken ct = default)
    {
        var t0 = game.Clock.Monotonic();
        while (game.Clock.Monotonic() - t0 < seconds)
        {
            double right, up;
            try
            {
                var p = await game.Pose(ct);
                (right, up) = await game.AimError(await PointOf(game, target, ct), p, ct);
            }
            catch (GameException e)
            {
                return new Result(false, $"lost target: {e.Message}");
            }
            if (Math.Abs(right) > tolerance || Math.Abs(up) > tolerance)
                await FlickToward(game, right, up, ct);
            await game.Tick();
        }
        return new Result(true, "tracked");
    }

    public static async Task<Result> HoldFor(Game game, string key, double seconds, object? value = null,
        CancellationToken ct = default)
    {
        if (game.HoldTimed(key, seconds, value))
        {
            var finished = false;
            try
            {
                await Wait(game, seconds, ct);
                finished = true;
            }
            finally
            {
                if (!finished)
                    game.LetGo(key);
            }
            return new Result(true, "held");
        }
        game.Hold(key, value);
        try
        {
            await Wait(game, seconds, ct);
        }
        finally
        {
            game.Release(key);
        }
        return new Result(true, "held");
    }

    public static async Task<Result> Press(Game game, string key, int times = 1, double every = 0.15,
        object? value = null, CancellationToken ct = default)
    {
        for (var i = 0; i < times; i++)
        {
            game.Press(key, value);
            if (i < times - 1)
                await Wait(game, every, ct);
        }
        await game.Tick();
        return new Result(true, "pressed");
    }

    public static async ValueTask<List<SonarWay>> Sonar(Game game, IReadOnlyList<double>? turns = null,
        double reach = 1500, CancellationToken ct = default)
    {
        turns ??= DefaultTurns;
        GameValue view;
        try
        {
            view = await game.Request("view", json =>
            {
                json.WriteNumber("max_count", 1);
                json.WriteBoolean("sonar", true);
                json.WriteStartArray("sonar_turns");
                foreach (var turn in turns)
                    json.WriteNumberValue(turn);
                json.WriteEndArray();
                json.WriteNumber("sonar_reach", reach);
            }, null, ct);
        }
        catch (GameException)
        {
            return [];
        }
        var ways = new List<SonarWay>();
        foreach (var way in view["body"]["sonar"]["ways"].Items)
        {
            var normal = way["normal"];
            ways.Add(new SonarWay(way["turn"].Number ?? 0, way["clear"].Number ?? 0, way["by"].Text, way["drop"].Number,
                normal.IsObject && normal.Count > 0 ? normal.Vector : null, way.Has("clear")));
        }
        return ways;
    }

    public static bool DeepDrop(SonarWay way, double maxDrop) =>
        way.By == "drop" && (way.Drop is null || way.Drop > maxDrop);

    static double ClearOr(SonarWay way, double fallback) => way.HasClear ? way.Clear : fallback;

    static async ValueTask<double> GoalHeight(Game game, Target target, CancellationToken ct) =>
        target.Actor is { } actor ? (await game.Loc(actor, ct)).Z : (await PointOf(game, target, ct)).Z;

    static async ValueTask<List<(Vec Foot, Vec Top)>?> WaysUpSeen(Game game, CancellationToken ct)
    {
        GameValue view;
        try
        {
            view = await game.View(new Dictionary<string, object?> { ["ways_up"] = true, ["max_count"] = 1 }, ct);
        }
        catch (GameException)
        {
            return null;
        }
        var body = view["body"];
        if (!body.Has("ways_up"))
            return null;
        var ways = new List<(Vec, Vec)>();
        foreach (var way in body["ways_up"].Items)
            if (way["foot"].Vector is { } foot && way["top"].Vector is { } top)
                ways.Add((foot, top));
        return ways;
    }

    public static async Task<(Vec Foot, Vec Top)?> FindWayUp(Game game, Vec goal, double floor, bool lookAround = true,
        CancellationToken ct = default)
    {
        (double Cost, Vec Foot, Vec Top)? best = null;

        async ValueTask Consider(IEnumerable<(Vec Foot, Vec Top)> ways)
        {
            var at = (await game.Pose(ct)).At;
            foreach (var (foot, top) in ways)
            {
                if (top.Z < floor - 70)
                    continue;
                var cost = foot.Dist2d(at) + top.Dist2d(goal) + 10.0 * Math.Max(0.0, floor - top.Z);
                if (best is null || cost < best.Value.Cost)
                    best = (cost, foot, top);
            }
        }

        var ways = await WaysUpSeen(game, ct);
        if (ways is null)
            return null;
        await Consider(ways);
        var far = (await game.Pose(ct)).At.Dist2d(goal);
        bool Near() => best is { } b && b.Top.Dist2d(goal) <= Math.Max(700.0, 0.75 * far);
        var turns = 0;
        while (!Near() && lookAround && turns < 3)
        {
            await game.Turn(90, 0, ct);
            turns++;
            await Wait(game, 0.3, ct);
            await Consider(await WaysUpSeen(game, ct) ?? []);
        }
        if (best is not { } found || found.Top.Dist2d(goal) >= far)
            return null;
        return (found.Foot, found.Top);
    }

    sealed record WalkStyle(bool AvoidDrops, bool JumpWhenStuck, string? RunKey, bool LookLevel, double MaxDrop);

    public static async Task<Result> WalkTo(Game game, Target target, double within = 120, double timeout = 20,
        bool avoidDrops = true, bool jumpWhenStuck = true, string? runKey = null, bool lookLevel = true,
        double maxDrop = 300, bool climb = true, CancellationToken ct = default)
    {
        var clock = game.Clock;
        var t0 = clock.Monotonic();
        double Left() => Math.Max(2.0, timeout - (clock.Monotonic() - t0));
        var style = new WalkStyle(avoidDrops, jumpWhenStuck, runKey, lookLevel, maxDrop);
        var (high, floor) = (false, 0.0);
        if (climb)
        {
            try
            {
                var m = await Movement(game, ct);
                var at = (await game.Pose(ct)).At;
                var goalZ = await GoalHeight(game, target, ct);
                high = goalZ - at.Z > (m.Step is > 0 or < 0 ? m.Step.Value : 45.0) + 40.0;
                floor = goalZ - 40.0;
            }
            catch (GameException)
            {
                high = false;
            }
        }
        var climbed = false;
        if (high)
            climbed = await Climb(game, target, floor, Left, style, lookAround: false, ct);
        var r = await WalkOn(game, target, within, Left(), style, settle: true, ct);
        if (!r && high && !climbed && clock.Monotonic() - t0 < timeout
            && await Climb(game, target, floor, Left, style, lookAround: true, ct))
            r = await WalkOn(game, target, within, Left(), style, settle: true, ct);
        return r;
    }

    static async Task<bool> Climb(Game game, Target target, double floor, Func<double> left, WalkStyle style,
        bool lookAround, CancellationToken ct)
    {
        Vec goal;
        try
        {
            goal = await PointOf(game, target, ct);
        }
        catch (GameException)
        {
            return false;
        }
        if (!lookAround)
            await LookAt(game, goal, tolerance: 10.0, timeout: 0.8, level: true, ct: ct);
        if (await FindWayUp(game, goal, floor, lookAround, ct) is not { } way)
            return false;
        var (foot, top) = way;
        var at = (await game.Pose(ct)).At;
        if (!(at.Z > foot.Z + 30 && at.Dist2d(top) < foot.Dist2d(top)))
        {
            var reached = await WalkOn(game, foot, 80.0, left(), style, settle: false, ct);
            if (!reached)
                return false;
        }
        var up = style with { AvoidDrops = false };
        return await WalkOn(game, top, 80.0, left(), up, settle: false, ct);
    }

    static double Coast(Game game) => game.CoastSeconds is > 0 or < 0 ? game.CoastSeconds.Value : 0.12;

    static async ValueTask<Pose?> Settle(Game game, double speed, Vec start, CancellationToken ct, double timeout = 1.0)
    {
        var clock = game.Clock;
        var t0 = clock.Monotonic();
        Pose? last = null;
        while (clock.Monotonic() - t0 < timeout)
        {
            await game.Tick();
            try
            {
                last = await game.Pose(ct);
            }
            catch (GameException)
            {
                return null;
            }
            if (last.Value.Velocity.Flat.Length < 20)
                break;
        }
        if (last is { } p && speed > 150)
        {
            var slid = p.At.Dist2d(start) / speed;
            game.CoastSeconds = Math.Max(0.0, Math.Min(0.6,
                game.CoastSeconds is { } known ? 0.5 * known + 0.5 * slid : slid));
        }
        return last;
    }

    static bool TurnsBack(Vec at, Vec oldNext, Vec newNext)
    {
        var (a, b) = (oldNext - at, newNext - at);
        if (a.Flat.Length < 1.0 || b.Flat.Length < 1.0)
            return false;
        return Math.Abs(Geom.Wrap(Geom.ToDegrees(Math.Atan2(b.Y, b.X) - Math.Atan2(a.Y, a.X)))) > TurnBack;
    }

    static double[] LookTurns(double heading, bool flip)
    {
        var step = 360.0 / SonarRing;
        var turns = new SortedSet<long>();
        for (var i = 0; i < SonarRing; i++)
            turns.Add(Fmt.Round(Geom.Wrap(i * step + (flip ? step / 2 : 0.0))));
        foreach (var k in (ReadOnlySpan<int>)[0, -10, 10, -20, 20])
            turns.Add(Fmt.Round(Geom.Wrap(heading + k)));
        return turns.Take(16).Select(turn => (double)turn).ToArray();
    }

    static object Rounded(IEnumerable<Vec>? path) => (path ?? []).Select(q => q.Rounded()).ToList();

    static async Task<Result> WalkOn(Game game, Target target, double within, double timeout, WalkStyle style,
        bool settle, CancellationToken ct)
    {
        var (avoidDrops, jumpWhenStuck, runKey, lookLevel, maxDrop) = style;
        var clock = game.Clock;
        var fwd = game.Keys["forward"];
        var nav = await NavMap.Of(game, ct);
        var t0 = clock.Monotonic();
        var lastProgressAt = t0;
        double? bestLeft = null;
        var sinceProgress = 0;
        var stuckTries = 0;
        var @short = 0;
        var (sonarAt, flip) = (-1e9, false);
        List<Vec>? path = null;
        var plannedVersion = -1;
        Vec? plannedGoal = null;
        var replans = 0;
        List<SonarWay> ways = [];
        var waysYaw = 0.0;
        var blind = false;
        (double Time, double Yaw)? turningSince = null;
        (double Yaw, double Degrees, int Ticks)? turnSent = null;
        var turnedBackAt = -1e9;
        var pace = new Pace(clock);
        try
        {
            while (true)
            {
                Pose p;
                Vec goal;
                try
                {
                    p = await game.Pose(ct);
                    goal = await PointOf(game, target, ct);
                }
                catch (GameException e)
                {
                    return new Result(false, $"can't read: {e.Message}");
                }
                var at = p.At;
                var d = goal.Dist2d(at);
                var step = pace.Step();
                if (d - Lead(p, goal, Coast(game) + (settle ? 0.5 * step : step)) <= within)
                {
                    if (!settle)
                        return new Result(true, "arrived", D(("distance", Fmt.Round(d)), ("replans", replans)));
                    game.Release(fwd);
                    if (runKey is not null)
                        game.Release(runKey);
                    var end = await Settle(game, p.Velocity.Flat.Length, at, ct);
                    try
                    {
                        if (end is { } ended)
                            d = (await PointOf(game, target, ct)).Dist2d(ended.At);
                    }
                    catch (GameException)
                    {
                    }
                    if (d > within + Math.Max(30.0, 0.25 * within) && @short < 2 && clock.Monotonic() - t0 < timeout)
                    {
                        @short++;
                        continue;
                    }
                    return new Result(true, "arrived", D(("distance", Fmt.Round(d)), ("replans", replans)));
                }
                var now = clock.Monotonic();
                if (now - t0 > timeout)
                    return new Result(false, "walk timed out",
                        D(("distance", Fmt.Round(d)), ("replans", replans), ("way", Rounded(path))));
                var feet = at.Z;
                nav.Linger(at, step, now, feet);
                if (now - sonarAt >= SonarEvery)
                {
                    (sonarAt, flip) = (now, !flip);
                    var heading = Geom.Bearing(at, p.Yaw, path is { Count: > 0 } ? path[0] : goal);
                    ways = await Sonar(game, LookTurns(heading, flip), Math.Min(1500, d + 200), ct);
                    waysYaw = p.Yaw;
                    if (ways.Count > 0)
                        nav.Observe(at, p.Yaw, ways, now, avoidDrops, maxDrop, feet);
                }
                var movedGoal = plannedGoal is not { } planned || goal.Dist2d(planned) > Math.Max(100.0, 0.15 * d);
                if (path is null || movedGoal || (nav.Version != plannedVersion
                                                  && nav.WayBlocked(at, path, feet, ReplanAhead)))
                {
                    (plannedVersion, plannedGoal) = (nav.Version, goal);
                    var old = path;
                    if (nav.LineFree(at, goal, feet))
                    {
                        path = [goal];
                    }
                    else
                    {
                        path = nav.Plan(at, goal, within, feet);
                        if (path is null)
                        {
                            nav.Forget(now - 5.0);
                            plannedVersion = nav.Version;
                            path = nav.Plan(at, goal, within, feet);
                        }
                        replans++;
                    }
                    if (path is not null && old is { Count: > 0 } && !movedGoal && TurnsBack(at, old[0], path[0]))
                    {
                        if (now - turnedBackAt < ReverseHold && !nav.WayBlocked(at, old, feet, ReverseAhead))
                            path = old;
                        else
                            turnedBackAt = now;
                    }
                    blind = path is null;
                    path = blind ? [goal] : [.. path!];
                }
                var vel = p.Velocity.Flat;
                var reach = Math.Max(WaypointReached, vel.Length * step);
                while (path.Count > 1 && (path[0].Dist2d(at) < reach || nav.LineFree(at, path[1], feet)
                                          || (path[0].Dist2d(at) < 2 * reach && vel.Length > 100
                                                                              && (path[0] - at).Flat.Dot(vel) < 0)))
                    path.RemoveAt(0);
                var aim = path.Count == 1 ? goal : path[0];
                var steer = Geom.Bearing(at, p.Yaw, aim);
                var onWay = 0.0;
                if (turnSent is { } turn)
                {
                    var shown = Geom.Wrap(p.Yaw - turn.Yaw);
                    if (turn.Ticks < 4 && Math.Abs(shown) < 0.9 * Math.Abs(turn.Degrees))
                        onWay = turn.Degrees - shown;
                }
                var (r, _) = await FlickToward(game, steer - onWay, lookLevel ? -p.Pitch : 0.0, ct);
                var ticks = turnSent is { } sentBefore && onWay != 0 ? sentBefore.Ticks + 1 : 0;
                turnSent = Math.Abs(onWay + r) >= 1.0 ? (p.Yaw, onWay + r, ticks) : null;
                var walking = Math.Abs(steer) < (aim.Dist2d(at) > 3 * within + 150 ? 50 : 20);
                if (walking && avoidDrops && ways.Count > 0)
                {
                    var ahead = ways[0];
                    var closest = double.MaxValue;
                    foreach (var way in ways)
                    {
                        var off = Math.Abs(Geom.Wrap(waysYaw + way.Turn - p.Yaw - steer));
                        if (off < closest)
                            (ahead, closest) = (way, off);
                    }
                    if (DeepDrop(ahead, maxDrop) && ClearOr(ahead, 1e9) < 80)
                    {
                        if (blind)
                            return new Result(false, $"a drop of {(ahead.Drop is { } drop ? Fmt.G(drop) : "unknown depth")} ahead",
                                D(("distance", Fmt.Round(d)), ("replans", replans)));
                        walking = false;
                    }
                }
                if (walking)
                {
                    game.Hold(fwd);
                    if (runKey is not null)
                        game.Hold(runKey);
                    turningSince = null;
                }
                else
                {
                    game.Release(fwd);
                    if (turningSince is not { } since || Math.Abs(Geom.Wrap(p.Yaw - since.Yaw)) > 3.0)
                        turningSince = (now, p.Yaw);
                    else if (now - since.Time > NoTurnGiveUp)
                        return new Result(false, "the view doesn't turn: the mouse has no effect now (a menu, an intro, "
                                                 + "or an interaction holding the mouse?)",
                            D(("distance", Fmt.Round(d)), ("replans", replans)));
                }
                var left = aim.Dist2d(at);
                for (var i = 0; i + 1 < path.Count; i++)
                    left += path[i].Dist2d(path[i + 1]);
                if (bestLeft is null || left < bestLeft - 25 || !walking)
                {
                    (bestLeft, lastProgressAt, sinceProgress) = (left, now, 0);
                }
                else
                {
                    sinceProgress++;
                }
                if (now - lastProgressAt > 1.0 && sinceProgress >= MinCorrections / 2 && walking)
                {
                    stuckTries++;
                    if (stuckTries > 5)
                        return new Result(false, "stuck",
                            D(("distance", Fmt.Round(d)), ("replans", replans), ("way", Rounded(path))));
                    nav.BlockAhead(at, Geom.Wrap(p.Yaw + steer), now, feet);
                    var edge = avoidDrops && ways.Any(way => DeepDrop(way, maxDrop) && ClearOr(way, 1e9) < 150
                        && Math.Abs(Geom.Wrap(waysYaw + way.Turn - p.Yaw - steer)) < 45);
                    if (jumpWhenStuck && !edge)
                    {
                        game.Press(game.Keys["jump"]);
                        await Wait(game, 0.3, ct);
                    }
                    (lastProgressAt, bestLeft, sinceProgress) = (clock.Monotonic(), null, 0);
                }
                await game.Tick();
            }
        }
        finally
        {
            game.Release(fwd);
            if (runKey is not null)
                game.Release(runKey);
        }
    }

    public static async Task<Result> KeepAway(Game game, Target threat, double distance = 800, double timeout = 6,
        string? runKey = null, CancellationToken ct = default)
    {
        var clock = game.Clock;
        var fwd = game.Keys["forward"];
        var t0 = clock.Monotonic();
        double? heading = null;
        var headingAt = 0.0;
        var d = 0.0;
        try
        {
            while (clock.Monotonic() - t0 < timeout)
            {
                Pose p;
                Vec danger;
                try
                {
                    p = await game.Pose(ct);
                    danger = await PointOf(game, threat, ct);
                }
                catch (GameException e)
                {
                    return new Result(false, $"can't read: {e.Message}");
                }
                d = danger.Dist2d(p.At);
                if (d >= distance)
                    return new Result(true, "far enough", D(("distance", Fmt.Round(d))));
                var away = Geom.Wrap(Geom.Bearing(p.At, p.Yaw, danger) + 180.0);
                var now = clock.Monotonic();
                if (heading is null || now - headingAt > 0.6)
                {
                    headingAt = now;
                    var center = Fmt.Round(away);
                    var turns = new double[] { center - 90, center - 60, center - 30, center, center + 30, center + 60, center + 90 };
                    var ways = await Sonar(game, turns, 600, ct);
                    SonarWay? pick = null;
                    var closest = double.MaxValue;
                    foreach (var way in ways)
                    {
                        if (way.Clear < 250 || DeepDrop(way, 300.0))
                            continue;
                        var off = Math.Abs(Geom.Wrap(way.Turn - away));
                        if (off < closest)
                            (pick, closest) = (way, off);
                    }
                    heading = Geom.Wrap(p.Yaw + (pick?.Turn ?? away));
                }
                var steer = Geom.Wrap(heading.Value - p.Yaw);
                await FlickToward(game, steer, -p.Pitch, ct);
                if (Math.Abs(steer) < 60)
                {
                    game.Hold(fwd);
                    if (runKey is not null)
                        game.Hold(runKey);
                }
                else
                {
                    game.Release(fwd);
                }
                await game.Tick();
            }
            return new Result(false, "still close", D(("distance", Fmt.Round(d))));
        }
        finally
        {
            game.Release(fwd);
            if (runKey is not null)
                game.Release(runKey);
        }
    }

    public static async Task<Result> Melee(Game game, Target target, string key, double reach = 150, double cooldown = 0.5,
        Func<ValueTask<bool>>? alive = null, double timeout = 12, double tolerance = 8, CancellationToken ct = default)
    {
        alive ??= () => target.Actor is { } actor ? game.Exists(actor, ct) : new ValueTask<bool>(true);
        var clock = game.Clock;
        var fwd = game.Keys["forward"];
        var t0 = clock.Monotonic();
        var lastHit = -1e9;
        var hits = 0;
        var pace = new Pace(clock);
        try
        {
            while (clock.Monotonic() - t0 < timeout)
            {
                if (!await alive())
                    return new Result(true, "target down", D(("hits", hits)));
                Pose p;
                Vec at;
                try
                {
                    p = await game.Pose(ct);
                    at = await PointOf(game, target, ct);
                }
                catch (GameException)
                {
                    return new Result(true, "target gone", D(("hits", hits)));
                }
                var d = at.Dist2d(p.At);
                var (right, up) = await game.AimError(at, p, ct);
                await FlickToward(game, right, up, ct);
                if (d - Lead(p, at, pace.Step()) > reach * 0.8)
                    game.Hold(fwd);
                else
                    game.Release(fwd);
                if (d <= reach && Math.Abs(right) <= tolerance && game.T - lastHit >= cooldown)
                {
                    game.Press(key);
                    lastHit = game.T;
                    hits++;
                }
                await game.Tick();
            }
            return new Result(false, "timed out", D(("hits", hits)));
        }
        finally
        {
            game.Release(fwd);
        }
    }

    public static async Task<Result> Shoot(Game game, string target, string key = "LeftMouseButton", double every = 0.3,
        double closeTo = 500, Func<ValueTask<bool>>? alive = null, double timeout = 10, CancellationToken ct = default)
    {
        alive ??= () => game.Exists(target, ct);
        var clock = game.Clock;
        var t0 = clock.Monotonic();
        var nextShot = 0.0;
        var shots = 0;
        var beside = false;
        string[] only = [target];
        while (clock.Monotonic() - t0 < timeout)
        {
            try
            {
                if (!await alive())
                    return new Result(true, "target down", D(("shots", shots)));
                if ((await game.Sees(only, ct)).Count == 0)
                {
                    // Walking to closeTo returns at once inside it, so an unseen target in range is closed on until
                    // seen; the view is turned first, as off screen also reads unseen.
                    await LookAt(game, target, timeout: 0.3, ct: ct);
                    if ((await game.Sees(only, ct)).Count == 0)
                    {
                        if (beside)
                            return new Result(false, "no line of sight", D(("shots", shots)));
                        beside = (await WalkTo(game, target, within: 150, timeout: 1, ct: ct)).Ok;
                    }
                }
                else
                {
                    beside = false;
                    await LookAt(game, target, timeout: 0.3, ct: ct);
                    if ((await game.Sees(only, ct)).TryGetValue(target, out var seen) && seen.CrosshairOn)
                    {
                        if (game.T >= nextShot)
                        {
                            game.Press(key);
                            nextShot = game.T + every;
                            shots++;
                        }
                    }
                    else if (seen?.Distance > closeTo)
                        await WalkTo(game, target, within: closeTo, timeout: 0.5, ct: ct);
                }
            }
            catch (GameException)
            {
                return new Result(true, "target gone", D(("shots", shots)));
            }
            await game.Tick();
        }
        return new Result(false, "timed out", D(("shots", shots)));
    }

    public static async Task<Result> AimAndStrike(Game game, Target target, string key, int strikes = 1,
        double cooldown = 0.5, Func<ValueTask<bool>>? stop = null, CancellationToken ct = default)
    {
        var aimed = await LookAt(game, target, tolerance: 8.0, timeout: 1.0, ct: ct);
        var made = 0;
        for (var i = 0; i < strikes; i++)
        {
            if (stop is not null && await stop())
                break;
            game.Press(key);
            made++;
            if (i < strikes - 1)
                await Wait(game, cooldown, ct);
            else
                await game.Tick();
        }
        return new Result(made > 0, made > 0 ? "struck" : "stopped", D(("strikes", made), ("aimed", aimed.Ok)));
    }

    static async Task<Result?> StepUntil(Game game, Func<CancellationToken, Task<Result>> controller,
        Func<ValueTask<bool>> stop, CancellationToken ct)
    {
        var outer = game.Current;
        var inner = new Stepper(game, controller, ct);
        try
        {
            while (true)
            {
                await inner.Step();
                if (inner.Done)
                    return await inner.Task!;
                if (await stop())
                {
                    await inner.Stop();
                    return null;
                }
                game.Current = outer;
                await game.Tick();
            }
        }
        finally
        {
            if (!inner.Done)
                await inner.Stop();
            game.Current = outer;
        }
    }

    static async ValueTask<double?> HealthOf(Game game, Target target, CancellationToken ct)
    {
        if (target.Actor is not { } actor)
            return null;
        try
        {
            return (await game.Get(actor, "Health", ct)).Number;
        }
        catch (GameException)
        {
            return null;
        }
    }

    public static async Task<Result> StrikeAndRetreat(Game game, Target target, string key, double reach = 150,
        int strikes = 1, double cooldown = 0.5, double? retreat = null, double recover = 2.0,
        Func<ValueTask<bool>>? alive = null, Func<ValueTask<double?>>? health = null, double timeout = 30,
        string? runKey = null, CancellationToken ct = default)
    {
        alive ??= () => target.Actor is { } actor ? game.Exists(actor, ct) : new ValueTask<bool>(true);
        var distance = retreat ?? 3.0 * reach;
        var clock = game.Clock;
        var t0 = clock.Monotonic();
        double Left() => Math.Max(0.5, timeout - (clock.Monotonic() - t0));
        var made = new Dictionary<string, object?> { ["strikes"] = 0, ["approaches"] = 0, ["seconds"] = 0.0 };
        var hp = health is not null ? await health() : null;
        if (health is not null)
            made["damage"] = 0.0;
        var th = await HealthOf(game, target, ct);
        if (th is not null)
            made["hits"] = 0;
        var hit = false;

        Result Done(bool ok, string why)
        {
            made["seconds"] = Fmt.Round(clock.Monotonic() - t0, 1);
            return new Result(ok, why, made);
        }

        async ValueTask<Result?> Over()
        {
            if (!await alive())
                return Done(true, "target down");
            if (hp is <= 0)
                return Done(false, "player dead");
            if (clock.Monotonic() - t0 > timeout)
                return Done(false, "timeout");
            return null;
        }

        async ValueTask<bool> TookHit()
        {
            if (health is null)
                return false;
            var now = await health();
            if (now is null)
                return false;
            if (hp is { } was && now < was)
            {
                made["damage"] = (double)made["damage"]! + (was - now.Value);
                hit = true;
            }
            hp = now;
            return hit;
        }

        async ValueTask<bool> Landed()
        {
            var now = th is not null ? await HealthOf(game, target, ct) : null;
            if (now is null)
                return false;
            var was = th!.Value;
            th = now;
            if (now < was)
            {
                made["hits"] = (int)made["hits"]! + 1;
                return true;
            }
            return false;
        }

        async ValueTask<bool> Stop()
        {
            await Landed();
            return !await alive() || await TookHit();
        }

        while (true)
        {
            var over = await Over();
            if (over is not null)
                return over;
            made["approaches"] = (int)made["approaches"]! + 1;
            hit = false;
            var entry = new Dictionary<string, object?>
            {
                ["approach"] = made["approaches"], ["health"] = hp, ["target_health"] = th, ["strikes"] = 0,
            };
            var walked = await StepUntil(game,
                c => WalkTo(game, target, within: 0.8 * reach, timeout: Left(), runKey: runKey, ct: c), TookHit, ct);
            if (walked is not null && !walked)
            {
                entry["hit"] = hit;
                entry["walk"] = walked.Why;
                game.Log("tactic", entry);
                return Done(false, $"can't close in: {walked.Why}");
            }
            if (walked is not null)
            {
                var volley = await AimAndStrike(game, target, key, strikes, cooldown, Stop, ct);
                var struck = (int)volley.Data["strikes"]!;
                made["strikes"] = (int)made["strikes"]! + struck;
                entry["strikes"] = struck;
                await Landed();
            }
            over = await Over();
            if (over is not null)
            {
                entry["hit"] = hit;
                entry["retreat"] = null;
                game.Log("tactic", entry);
                return over;
            }
            var away = await KeepAway(game, target, distance: distance, timeout: Math.Min(6.0, Left()), runKey: runKey, ct: ct);
            await TookHit();
            if (await alive() && hp is not <= 0)
            {
                await Wait(game, Math.Min(recover, Left()), ct);
                await TookHit();
            }
            entry["hit"] = hit;
            entry["retreat"] = away.Data.GetValueOrDefault("distance");
            entry["damage"] = made.GetValueOrDefault("damage");
            game.Log("tactic", entry);
        }
    }

    public static async ValueTask<MovementInfo> Movement(Game game, CancellationToken ct = default)
    {
        var pawn = await game.Pawn(ct);
        if (game.MovementCache is { } cached && cached.Pawn == pawn)
            return cached;
        var raw = default(GameValue);
        try
        {
            raw = await game.Request("movement", (Action<System.Text.Json.Utf8JsonWriter>?)null, null, ct);
        }
        catch (GameException)
        {
        }
        double? Number(string name) => raw[name].Number;
        var (jumpZ, gravity, step) = (Number("jump_z"), Number("gravity"), Number("step"));
        double? peak = jumpZ is > 0 or < 0 && gravity is > 0 or < 0 ? jumpZ * jumpZ / (2 * gravity) : null;
        double? reachUp = peak + (step ?? 0.0);
        var info = new MovementInfo
        {
            Pawn = pawn, WalkSpeed = Number("walk_speed"), JumpZ = jumpZ, Gravity = gravity,
            HalfHeight = Number("half_height"), Radius = Number("radius"), Step = step,
            MaxSlope = Number("max_slope"), AirControl = Number("air_control"), Peak = peak, ReachUp = reachUp,
            ReachUpMax = reachUp + 10.0,
        };
        game.MovementCache = info;
        return info;
    }

    static (double Distance, Vec Near) FootprintDistance(Vec at, Vec center, Vec ext)
    {
        var nx = Math.Min(Math.Max(at.X, center.X - ext.X), center.X + ext.X);
        var ny = Math.Min(Math.Max(at.Y, center.Y - ext.Y), center.Y + ext.Y);
        var near = new Vec(nx, ny, center.Z + ext.Z);
        return (near.Dist2d(at), near);
    }

    public static async Task<Result> JumpTo(Game game, Target target, string? runKey = null, double timeout = 8,
        int tries = 3, CancellationToken ct = default)
    {
        var r = new Result(false, "not tried");
        for (var i = 0; i < Math.Max(1, tries); i++)
        {
            r = await JumpOnce(game, target, runKey, timeout, ct);
            if (r.Ok || r.Why is not ("jump timed out" or "ran past the take-off point at too low a speed"))
                return r;
        }
        return r;
    }

    static async Task<Result> JumpOnce(Game game, Target target, string? runKey, double timeout, CancellationToken ct)
    {
        var m = await Movement(game, ct);
        var clock = game.Clock;
        var (fwd, jump) = (game.Keys["forward"], game.Keys["jump"]);
        if (m.JumpZ is null or 0 || m.Gravity is null or 0)
            return new Result(false, "not a character with jump numbers (movement() found none)");
        (Vec Center, Vec Ext)? box = null;
        if (target.Actor is { } actor)
        {
            try
            {
                box = await game.Bounds(actor, ct);
            }
            catch (GameException e)
            {
                return new Result(false, $"can't read the target: {e.Message}");
            }
        }
        var (vz, g) = (m.JumpZ.Value, m.Gravity.Value);
        var tApex = vz / g;
        if (box is { } room)
        {
            Pose p0;
            try
            {
                p0 = await game.Pose(ct);
            }
            catch (GameException e)
            {
                return new Result(false, $"can't read: {e.Message}");
            }
            var (edge0, near0) = FootprintDistance(p0.At, room.Center, room.Ext);
            var runSpeed = Math.Max(p0.Velocity.Length2d, 0.6 * (m.WalkSpeed is > 0 or < 0 ? m.WalkSpeed.Value : 600));
            var need = runSpeed * tApex + 450.0;
            if (edge0 < need)
            {
                var away = (p0.At - near0).Flat;
                away = away.Length > 1 ? away.Unit : (p0.At - room.Center).Flat.Unit;
                var start = near0 + away * (need + 60.0);
                await WalkTo(game, new Vec(start.X, start.Y, p0.At.Z), within: 60, timeout: 6, avoidDrops: true, ct: ct);
            }
        }
        var t0 = clock.Monotonic();
        double? jumpedAt = null;
        var riseAtTakeoff = 0.0;
        var bestFeet = -1e9;
        var speeds = new List<(double Time, double Speed)>();
        var previousStill = false;
        var pace = new Pace(clock);
        var halfHeight = m.HalfHeight is > 0 or < 0 ? m.HalfHeight.Value : 90.0;
        try
        {
            while (clock.Monotonic() - t0 < timeout)
            {
                Pose p;
                Vec goal;
                double read;
                try
                {
                    var asked = clock.Monotonic();
                    p = await game.Pose(ct);
                    read = clock.Monotonic() - asked;
                    goal = box is { } top ? new Vec(top.Center.X, top.Center.Y, top.Center.Z + top.Ext.Z)
                        : await PointOf(game, target, ct);
                }
                catch (GameException e)
                {
                    return new Result(false, $"can't read: {e.Message}");
                }
                var dt = pace.Step();
                var feet = p.At.Z - halfHeight;
                bestFeet = Math.Max(bestFeet, feet);
                var rise = goal.Z - feet;
                var d = goal.Dist2d(p.At);
                var speed = p.Velocity.Length2d;
                if (jumpedAt is null)
                {
                    if (rise > (m.ReachUpMax ?? 0))
                        return new Result(false,
                            $"too high: {Fmt.F(rise, 0)} above the feet, a jump reaches {Fmt.F(m.ReachUp ?? 0, 0)} "
                            + $"(lifts {Fmt.F(m.Peak ?? 0, 0)}, steps up {Fmt.F(m.Step ?? 0, 0)})",
                            D(("rise", Fmt.Round(rise)), ("reach_up", Fmt.Round(m.ReachUp ?? 0))));
                    bool ready;
                    Vec aimAt;
                    var (edge, takeoff) = (0.0, 0.0);
                    if (box is { } b)
                    {
                        (edge, var near) = FootprintDistance(p.At, b.Center, b.Ext);
                        aimAt = edge > 60 ? near : goal;
                        takeoff = speed * tApex + speed * (read + 0.5 * dt);
                        ready = edge <= takeoff;
                    }
                    else
                    {
                        var disc = vz * vz - 2 * g * Math.Max(0.0, rise);
                        var air = disc > 0 ? (vz + Math.Sqrt(disc)) / g : tApex;
                        aimAt = goal;
                        ready = d - speed * (read + 0.5 * dt) <= speed * air * 0.9;
                    }
                    var bearing = Geom.Bearing(p.At, p.Yaw, box is null ? goal : aimAt);
                    await FlickToward(game, bearing, -p.Pitch, ct);
                    if (Math.Abs(bearing) < 25)
                    {
                        game.Hold(fwd);
                        if (runKey is not null)
                            game.Hold(runKey);
                    }
                    else
                    {
                        game.Release(fwd);
                    }
                    var now = clock.Monotonic();
                    for (var i = speeds.Count - 1; i >= 0; i--)
                        if (now - speeds[i].Time >= 0.5)
                            speeds.RemoveAt(i);
                    speeds.Add((now, speed));
                    var older = -1.0;
                    foreach (var (time, value) in speeds)
                        if (now - time > 0.3)
                            older = Math.Max(older, value);
                    var fast = speed > 150 && older >= 0 && speed <= older * 1.04;
                    if (ready && Math.Abs(bearing) < 12 && fast)
                    {
                        game.Press(jump);
                        jumpedAt = clock.Monotonic();
                        riseAtTakeoff = rise;
                    }
                    else if (box is not null && !fast && edge < 0.5 * takeoff)
                    {
                        return new Result(false, "ran past the take-off point at too low a speed",
                            D(("edge", Fmt.Round(edge)), ("speed", Fmt.Round(speed))));
                    }
                }
                else
                {
                    var bearing = Geom.Bearing(p.At, p.Yaw, goal);
                    if (box is { } b1 && p.Velocity.Z < 0 && feet - goal.Z < 60
                        && FootprintDistance(p.At, b1.Center, b1.Ext).Distance < 1)
                    {
                        game.Release(fwd);
                    }
                    else
                    {
                        await FlickToward(game, bearing * 0.5, -p.Pitch, ct);
                        game.Hold(fwd);
                    }
                    var still = Math.Abs(p.Velocity.Z) < 5;
                    var landed = still && previousStill;
                    previousStill = still;
                    if (clock.Monotonic() - jumpedAt.Value > 0.25 && landed)
                    {
                        game.Release(fwd);
                        if (box is { } b2 && Math.Abs(feet - goal.Z) < 40
                                          && FootprintDistance(p.At, b2.Center, b2.Ext).Distance < 1)
                            return new Result(true, "landed on it",
                                D(("feet", Fmt.Round(feet)), ("target_z", Fmt.Round(goal.Z)), ("distance", Fmt.Round(d)),
                                    ("best_feet", Fmt.Round(bestFeet))));
                        if (box is not null || d > 60)
                        {
                            game.Release(fwd);
                            await WalkTo(game, goal, within: box is not null ? 50 : 40, timeout: 2.0, avoidDrops: true, ct: ct);
                            try
                            {
                                p = await game.Pose(ct);
                            }
                            catch (GameException e)
                            {
                                return new Result(false, $"can't read: {e.Message}");
                            }
                            feet = p.At.Z - halfHeight;
                            d = goal.Dist2d(p.At);
                        }
                        var on = Math.Abs(feet - goal.Z) < 40;
                        if (box is { } b3)
                            on = on && FootprintDistance(p.At, b3.Center, b3.Ext).Distance < 1;
                        else
                            on = on && d < 200;
                        var why = on ? "landed on it" : "landed short or off";
                        if (!on && box is not null && riseAtTakeoff > (m.ReachUp ?? 0))
                            why += $" (the top was {Fmt.F(riseAtTakeoff, 0)} above the feet: within a jump's reach "
                                   + $"({Fmt.F(m.ReachUpMax ?? 0, 0)}) by perching on the edge, so a miss is not proof it "
                                   + "can't be done: try again)";
                        return new Result(on, why,
                            D(("feet", Fmt.Round(feet)), ("target_z", Fmt.Round(goal.Z)), ("distance", Fmt.Round(d)),
                                ("best_feet", Fmt.Round(bestFeet))));
                    }
                }
                await game.Tick();
            }
            return new Result(false, "jump timed out", D(("best_feet", Fmt.Round(bestFeet)), ("jumped", jumpedAt is not null)));
        }
        finally
        {
            game.Release(fwd);
            if (runKey is not null)
                game.Release(runKey);
        }
    }

    public static async Task<Result> FaceAndPress(Game game, Target target, string key, double? tolerance = null,
        CancellationToken ct = default)
    {
        var r = await LookAt(game, target, tolerance, ct: ct);
        game.Press(key);
        await game.Tick();
        return new Result(true, "pressed", D(("aimed", r.Ok)));
    }

    public static async Task<Result> GoAndPress(Game game, Target target, string key, double within = 150,
        double? tolerance = null, double timeout = 20, CancellationToken ct = default)
    {
        var r = await WalkTo(game, target, within: within, timeout: timeout, ct: ct);
        return !r ? r : await FaceAndPress(game, target, key, tolerance, ct);
    }

    public static Task<Result> Run(Game game, Func<Task<Result>> controller, double timeout = 15, double hz = 15) =>
        Run(game, _ => controller(), timeout, hz);

    public static async Task<Result> Run(Game game, Func<CancellationToken, Task<Result>> controller, double timeout = 15,
        double hz = 15, CancellationToken ct = default)
    {
        var clock = game.Clock;
        var t0 = clock.Monotonic();
        var outer = game.Current;
        var stepper = new Stepper(game, controller, ct);
        try
        {
            while (clock.Monotonic() - t0 < timeout)
            {
                game.Renew();
                await stepper.Step();
                if (stepper.Done)
                    return await stepper.Task!;
                await clock.Delay(1.0 / hz, ct);
            }
            await stepper.Stop();
            return new Result(false, "run timed out");
        }
        finally
        {
            if (stepper.Started && !stepper.Done)
                await stepper.Stop();
            game.Current = outer;
            game.ReleaseAll();
        }
    }
}
