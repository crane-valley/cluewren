using System.Diagnostics;
using System.Text.Json;
using PlaytestAgent.Kit;

namespace PlaytestAgent.Tests;

public sealed class WolfLink(int wolfHealth = 30, int playerHealth = 100) : FakeLink
{
    public const string Wolf = "Main:/Level/Wolf_C_0";

    double _nearFor;

    public Vec WolfAt { get; set; } = new(600, 0, 100);

    public int WolfHealth { get; private set; } = wolfHealth;

    public int PlayerHealth { get; private set; } = playerHealth;

    public int Bites { get; private set; }

    public int Strikes { get; private set; }

    public bool InSight { get; set; }

    public bool OnCrosshair { get; set; }

    protected override void Advance()
    {
        var before = GameTime ?? 0;
        base.Advance();
        if (Paused || WolfHealth <= 0)
            return;
        if (Pos.Dist2d(WolfAt) < 120)
        {
            _nearFor += (GameTime ?? 0) - before;
            if (_nearFor > 1.0)
            {
                PlayerHealth = Math.Max(0, PlayerHealth - 20);
                Bites++;
                _nearFor = 0.0;
            }
        }
        else
        {
            _nearFor = 0.0;
        }
    }

    protected override Vec ActorAt(string obj) => obj.Contains("Wolf") ? WolfAt : base.ActorAt(obj);

    protected override Vec ExtentOf(string obj) => obj.Contains("Wolf") ? new Vec(40, 40, 40) : base.ExtentOf(obj);

    protected override object? Read(string op, JsonElement args)
    {
        if (op == "view")
            return InSight
                ? D(("visible", new List<object> { D(("name", "Wolf_C_0"), ("distance", Pos.Dist(WolfAt)), ("screen_x", 0.0), ("screen_y", 0.0), ("crosshair_on", OnCrosshair)) }))
                : D(("visible", new List<object>()));
        if (op == "key")
        {
            var answer = base.Read(op, args);
            if (Str(args, "key") == "LeftMouseButton" && Str(args, "mode") == "press")
            {
                Strikes++;
                if (WolfHealth > 0 && Pos.Dist2d(WolfAt) < 150)
                    WolfHealth = Math.Max(0, WolfHealth - 10);
            }
            return answer;
        }
        if (op == "rc")
        {
            var body = args.GetProperty("body");
            var (obj, verb, prop) = (Str(body, "objectPath") ?? "", Str(args, "verb"), Str(body, "propertyName"));
            if (obj.Contains("Wolf"))
            {
                if (verb == "describe")
                {
                    if (WolfHealth > 0)
                        return D(("Name", obj), ("Class", "x"));
                    throw new GameException(400, $"Object {obj} does not exist");
                }
                if (verb == "property")
                {
                    if (prop == "Health")
                        return D(("Health", WolfHealth));
                    throw new GameException(400, "no property");
                }
                throw new GameException(400, $"no function {Str(body, "functionName")}");
            }
            if (verb == "property" && obj.Contains("Hero") && prop == "Health")
                return D(("Health", PlayerHealth));
        }
        return base.Read(op, args);
    }
}

[Collection("wall clock")]
public class TacticsTests
{
    const string Wolf = WolfLink.Wolf;
    const string Hero = FakeLink.HeroPath;
    const string Key = "LeftMouseButton";

    readonly WolfLink _link;
    readonly Game _game;
    readonly List<Dictionary<string, object?>> _tactics = [];

    public TacticsTests()
    {
        (_link, _game) = Make(new WolfLink());
    }

    (WolfLink, Game) Make(WolfLink link)
    {
        var game = new Game(link, PreciseClock.Instance) { Look = new LookScale { X = 0.1, Y = 0.1 } };
        game.Log = (kind, fields) =>
        {
            if (kind == "tactic")
                _tactics.Add(fields);
        };
        return (link, game);
    }

    static Func<ValueTask<double?>> PlayerHealth(Game game) => async () => (await game.Get(Hero, "Health")).Number;

    [Fact]
    public async Task Hit_and_run_wins_untouched()
    {
        var r = await Control.Run(_game, ct => Control.StrikeAndRetreat(_game, Wolf, Key, reach: 150, strikes: 1, cooldown: 0.5,
            recover: 0.5, health: PlayerHealth(_game), timeout: 30, ct: ct), 30);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("target down", r.Why);
        Assert.Equal(3, r.Data["approaches"]);
        Assert.Equal(3, r.Data["strikes"]);
        Assert.Equal(3, r.Data["hits"]);
        Assert.Equal(0.0, r.Data["damage"]);
        Assert.True((double)r.Data["seconds"]! > 0);
        Assert.True(_link.PlayerHealth >= 60);
        Assert.Equal(0, _link.Bites);
        Assert.Equal(0, _link.WolfHealth);
        Assert.False(await _game.Exists(Wolf));
        Assert.Empty(_game.Held());
        Assert.Equal([1, 2, 3], _tactics.Select(e => (int)e["approach"]!));
        Assert.Equal(30.0, _tactics[0]["target_health"]);
        Assert.Equal(100.0, _tactics[0]["health"]);
        Assert.Equal(1, _tactics[0]["strikes"]);
        Assert.True(Convert.ToDouble(_tactics[0]["retreat"]) >= 450);
        Assert.False((bool)_tactics[0]["hit"]!);
    }

    [Fact]
    public async Task Too_short_a_retreat_takes_hits()
    {
        var r = await Control.Run(_game, ct => Control.StrikeAndRetreat(_game, Wolf, Key, reach: 150, retreat: 100, recover: 1.5,
            health: PlayerHealth(_game), timeout: 30, ct: ct), 30);
        Assert.True(r.Ok, r.ToString());
        Assert.True(_link.Bites >= 1 && _link.PlayerHealth < 100);
        Assert.Equal(100.0 - _link.PlayerHealth, r.Data["damage"]);
        Assert.True((double)r.Data["damage"]! >= 20);
        Assert.Contains(_tactics, e => e["hit"] is true);
    }

    [Fact]
    public async Task Backs_off_when_hit_mid_volley()
    {
        var r = await Control.Run(_game, ct => Control.StrikeAndRetreat(_game, Wolf, Key, reach: 150, strikes: 4, cooldown: 0.6,
            recover: 0.5, health: PlayerHealth(_game), timeout: 30, ct: ct), 30);
        Assert.True(r.Ok, r.ToString());
        var first = _tactics[0];
        Assert.True((bool)first["hit"]!);
        Assert.True((int)first["strikes"]! is >= 1 and < 4, $"{first["strikes"]}");
        Assert.True((int)r.Data["approaches"]! >= 2 && (double)r.Data["damage"]! >= 20);
    }

    [Fact]
    public async Task Alive_false_ends_it()
    {
        var r = await Control.Run(_game, ct => Control.StrikeAndRetreat(_game, Wolf, Key, recover: 0.5, timeout: 30,
            alive: () => new ValueTask<bool>(_link.WolfHealth > 10), ct: ct), 30);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("target down", r.Why);
        Assert.Equal(2, r.Data["approaches"]);
        Assert.Equal(2, r.Data["strikes"]);
        Assert.Equal(2, r.Data["hits"]);
        Assert.False(r.Data.ContainsKey("damage"));
        Assert.Equal(10, _link.WolfHealth);
    }

    [Fact]
    public async Task Timeout_is_falsy_with_why()
    {
        var (_, game) = Make(new WolfLink(wolfHealth: 1000));
        var r = await Control.Run(game, ct => Control.StrikeAndRetreat(game, Wolf, Key, recover: 0.5, timeout: 2, ct: ct), 15);
        Assert.False(r.Ok);
        Assert.Equal("timeout", r.Why);
        Assert.True((int)r.Data["approaches"]! >= 1 && (int)r.Data["strikes"]! >= 1 && (double)r.Data["seconds"]! >= 2);
    }

    [Fact]
    public async Task Player_dead_is_falsy()
    {
        var (link, game) = Make(new WolfLink(playerHealth: 20));
        var r = await Control.Run(game, ct => Control.StrikeAndRetreat(game, Wolf, Key, retreat: 100, recover: 1.5,
            health: PlayerHealth(game), timeout: 30, ct: ct), 30);
        Assert.False(r.Ok);
        Assert.Equal("player dead", r.Why);
        Assert.Equal(20.0, r.Data["damage"]);
        Assert.Equal(0, link.PlayerHealth);
    }

    [Fact]
    public async Task Walk_failure_is_falsy()
    {
        _link.WolfAt = new Vec(60000, 0, 100);
        var r = await Control.Run(_game, ct => Control.StrikeAndRetreat(_game, Wolf, Key, timeout: 3, ct: ct), 15);
        Assert.False(r.Ok);
        Assert.StartsWith("can't close in: ", r.Why);
        Assert.Equal(0, r.Data["strikes"]);
        Assert.Equal(1, r.Data["approaches"]);
    }

    [Fact]
    public async Task Shoot_fires_at_its_rate_until_the_target_is_down()
    {
        (_link.InSight, _link.OnCrosshair, _link.WolfAt) = (true, true, new Vec(140, 0, 100));
        var clock = Stopwatch.StartNew();
        var r = await Control.Run(_game, ct => Control.Shoot(_game, Wolf, every: 0.2, timeout: 5, ct: ct), 8);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("target down", r.Why);
        Assert.Equal(3, r.Data["shots"]);
        Assert.Equal(3, _link.Strikes);
        Assert.True(clock.Elapsed.TotalSeconds >= 0.4, $"{clock.Elapsed.TotalSeconds}");
    }

    [Fact]
    public async Task Shoot_holds_fire_until_the_crosshair_is_on()
    {
        (_link.InSight, _link.OnCrosshair, _link.WolfAt) = (true, false, new Vec(140, 0, 100));
        var r = await Control.Run(_game, ct => Control.Shoot(_game, Wolf, timeout: 1, ct: ct), 5);
        Assert.False(r.Ok);
        Assert.Equal("timed out", r.Why);
        Assert.Equal(0, r.Data["shots"]);
        Assert.Equal(0, _link.Strikes);
    }

    [Fact]
    public async Task Shoot_closes_in_on_a_target_it_cannot_see()
    {
        _link.WolfAt = new Vec(1200, 0, 100);
        var r = await Control.Run(_game, ct => Control.Shoot(_game, Wolf, closeTo: 500, timeout: 6,
            alive: () => new ValueTask<bool>(_link.Pos.Dist2d(_link.WolfAt) > 550), ct: ct), 10);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(0, _link.Strikes);
    }

    [Fact]
    public async Task Shoot_closes_in_on_an_unseen_target_already_within_range()
    {
        _link.WolfAt = new Vec(400, 0, 100);
        var r = await Control.Run(_game, ct => Control.Shoot(_game, Wolf, closeTo: 500, timeout: 4,
            alive: () => new ValueTask<bool>(_link.Pos.Dist2d(_link.WolfAt) > 250), ct: ct), 8);
        Assert.True(r.Ok, r.ToString());
    }

    [Fact]
    public async Task Shoot_gives_up_on_a_target_it_cannot_see_from_beside_it()
    {
        _link.WolfAt = new Vec(100, 0, 100);
        var clock = Stopwatch.StartNew();
        var r = await Control.Run(_game, ct => Control.Shoot(_game, Wolf, timeout: 4, ct: ct), 8);
        Assert.False(r.Ok);
        Assert.Equal("no line of sight", r.Why);
        Assert.True(clock.Elapsed.TotalSeconds < 3, $"{clock.Elapsed.TotalSeconds}");
    }

    [Fact]
    public async Task Shoot_closes_in_on_a_far_target_the_crosshair_misses()
    {
        (_link.InSight, _link.OnCrosshair, _link.WolfAt) = (true, false, new Vec(1200, 0, 100));
        var r = await Control.Run(_game, ct => Control.Shoot(_game, Wolf, closeTo: 500, timeout: 6,
            alive: () => new ValueTask<bool>(_link.Pos.Dist2d(_link.WolfAt) > 550), ct: ct), 10);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(0, _link.Strikes);
    }

    [Fact]
    public async Task Aim_and_strike_is_one_volley()
    {
        _link.Pos = new Vec(500, 0, 100);
        var r = await Control.Run(_game, ct => Control.AimAndStrike(_game, Wolf, Key, strikes: 2, cooldown: 0.2, ct: ct), 5);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(2, r.Data.Count);
        Assert.Equal(2, r.Data["strikes"]);
        Assert.Equal(true, r.Data["aimed"]);
        Assert.Equal(10, _link.WolfHealth);
        Assert.Equal(2, _link.Strikes);
        var stopped = await Control.Run(_game, ct => Control.AimAndStrike(_game, Wolf, Key, strikes: 3,
            stop: () => new ValueTask<bool>(true), ct: ct), 5);
        Assert.False(stopped.Ok);
        Assert.Equal(0, stopped.Data["strikes"]);
    }
}
