using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cluewren.Kit;

public readonly record struct Pose(Vec At, Vec Eye, double Yaw, double Pitch, Vec Velocity, bool? Ground = null)
{
    public bool OnGround => Ground is true;
}

public readonly struct Read<T>
{
    readonly T _value;

    public Read(T value)
    {
        _value = value;
        Error = null;
    }

    public Read(GameException error)
    {
        _value = default!;
        Error = error;
    }

    public GameException? Error { get; }

    public bool Ok => Error is null;

    public T Value => Error is null ? _value : throw Error;

    public T? OrDefault => Error is null ? _value : default;

    public static implicit operator T(Read<T> read) => read.Value;

    public override string ToString() => Error?.Message ?? _value?.ToString() ?? "null";
}

public sealed class LookScale
{
    public double? X { get; set; }

    public double? Y { get; set; }
}

public sealed record Seen(double? Distance, double? ScreenX, double? ScreenY, bool? PartlyInView, bool CrosshairOn);

public sealed partial class Game
{
    public const double KeyLease = 0.6;

    static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(60);

    [ThreadStatic]
    static int t_inBatch;

    [GeneratedRegex("^(K2_)?(Destroy|Set|Spawn|Apply|Add|Remove|Teleport|Launch|Kill|Damage|Give|Grant|Equip|Unequip|Use|Fire|Open|Close|Start|Stop|Reset|Respawn|Possess|Attach|Detach)", RegexOptions.IgnoreCase)]
    private static partial Regex ChangesState();

    readonly Lock _lock = new();
    readonly Dictionary<string, object?> _held = [];
    readonly Dictionary<string, Action<Utf8JsonWriter>> _leases = [];
    readonly WaitCounter _rootWaits = new();
    GameValue? _context;
    double _contextAt;
    double _calibratedAt = -1e9;

    public Game(IGameLink link, IClock? clock = null, Action<string, Dictionary<string, object?>>? log = null)
    {
        Link = link;
        Clock = clock ?? RealClock.Instance;
        Log = log ?? ((_, _) => { });
        Engine = Engine.For(link.Engine);
        Keys = new Dictionary<string, string>(Engine.KeyNames);
    }

    public IGameLink Link { get; }

    public IClock Clock { get; }

    public Engine Engine { get; }

    public Action<string, Dictionary<string, object?>> Log { get; set; }

    public LookScale Look { get; set; } = new();

    public double TurnAsked { get; private set; }

    public Dictionary<string, string> Keys { get; }

    public List<Dictionary<string, object?>> Writes { get; } = [];

    public string? Level { get; set; }

    public Dictionary<string, NavMap> NavMaps { get; } = [];

    public double? CoastSeconds { get; set; }

    public MovementInfo? MovementCache { get; set; }

    public Stepper? Current { get; internal set; }

    public CancellationToken RunToken { get; set; }

    // Harness code passes no token: what it reads inside an option ends with that option, and the rest with the run.
    internal CancellationToken Ambient => Stepper.Counter.Value is { Token.CanBeCanceled: true } waits ? waits.Token : RunToken;

    public double T => Link.GameTime ?? 0;

    public IReadOnlyCollection<string> Features => Link.Features;

    public int Waits() => (Stepper.Counter.Value ?? _rootWaits).Count;

    public ValueTask Tick(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Current is { } stepper ? stepper.Tick() : Unmanaged(ct);
    }

    async ValueTask Unmanaged(CancellationToken ct)
    {
        Renew();
        await Clock.Delay(1.0 / 15, ct).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal async ValueTask<GameAnswer> Raw(string op, Action<Utf8JsonWriter>? args, double? timeout, CancellationToken ct)
    {
        if (t_inBatch == 0)
            (Stepper.Counter.Value ?? _rootWaits).Count++;
        try
        {
            var limit = timeout is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            return await Link.RequestAsync(op, args, limit, ct.CanBeCanceled ? ct : Ambient).ConfigureAwait(false);
        }
        catch (GameException e)
        {
            throw new GameException(e.Status, $"{op}: {e.Message}");
        }
    }

    public async ValueTask<GameValue> Request(string op, object? args = null, double? timeout = null,
        CancellationToken ct = default) =>
        await Request(op, Fields(args), timeout, ct).ConfigureAwait(false);

    public ValueTask<GameValue> Request(string op, object? args, CancellationToken ct) => Request(op, args, null, ct);

    public async ValueTask<GameValue> Request(string op, Action<Utf8JsonWriter>? args, double? timeout = null,
        CancellationToken ct = default)
    {
        using var answer = await Raw(op, args, timeout, ct).ConfigureAwait(false);
        return GameValue.Of(answer.Result);
    }

    public void Post(string op, Action<Utf8JsonWriter>? args = null) => Link.Post(op, args);

    public void Post(string op, object? args) => Link.Post(op, Fields(args));

    public static Action<Utf8JsonWriter>? Fields(object? args)
    {
        if (args is null)
            return null;
        return json =>
        {
            if (args is Dictionary<string, object?> map)
            {
                Jsonable.WriteEntries(json, map, 0, round: false);
                return;
            }
            using var document = JsonDocument.Parse(Jsonable.Encode(args, round: false));
            foreach (var field in document.RootElement.EnumerateObject())
                field.WriteTo(json);
        };
    }

    WaitCounter BeginBatch()
    {
        t_inBatch++;
        Link.Cork();
        return Stepper.Counter.Value ?? _rootWaits;
    }

    void EndBatch(WaitCounter counter)
    {
        Link.Uncork();
        if (--t_inBatch == 0)
            counter.Count++;
    }

    static async ValueTask<Read<T>> Go<T>(Func<ValueTask<T>> read)
    {
        try
        {
            return new Read<T>(await read().ConfigureAwait(false));
        }
        catch (GameException e)
        {
            return new Read<T>(e);
        }
    }

    public async ValueTask<Read<T>[]> Batch<T>(IEnumerable<Func<ValueTask<T>>> reads)
    {
        var counter = BeginBatch();
        var started = new List<ValueTask<Read<T>>>();
        try
        {
            foreach (var read in reads)
                started.Add(Go(read));
        }
        finally
        {
            EndBatch(counter);
        }
        var answers = new Read<T>[started.Count];
        for (var i = 0; i < answers.Length; i++)
            answers[i] = await started[i].ConfigureAwait(false);
        return answers;
    }

    public ValueTask<Read<TOut>[]> Map<TIn, TOut>(IEnumerable<TIn> items, Func<TIn, ValueTask<TOut>> read) =>
        Batch(items.Select(item => (Func<ValueTask<TOut>>)(() => read(item))));

    public async ValueTask<(Read<T1>, Read<T2>)> Batch<T1, T2>(Func<ValueTask<T1>> a, Func<ValueTask<T2>> b)
    {
        var counter = BeginBatch();
        ValueTask<Read<T1>> ta;
        ValueTask<Read<T2>> tb;
        try
        {
            ta = Go(a);
            tb = Go(b);
        }
        finally
        {
            EndBatch(counter);
        }
        return (await ta.ConfigureAwait(false), await tb.ConfigureAwait(false));
    }

    public async ValueTask<(Read<T1>, Read<T2>, Read<T3>)> Batch<T1, T2, T3>(Func<ValueTask<T1>> a,
        Func<ValueTask<T2>> b, Func<ValueTask<T3>> c)
    {
        var counter = BeginBatch();
        ValueTask<Read<T1>> ta;
        ValueTask<Read<T2>> tb;
        ValueTask<Read<T3>> tc;
        try
        {
            ta = Go(a);
            tb = Go(b);
            tc = Go(c);
        }
        finally
        {
            EndBatch(counter);
        }
        return (await ta.ConfigureAwait(false), await tb.ConfigureAwait(false), await tc.ConfigureAwait(false));
    }

    public async ValueTask<(Read<T1>, Read<T2>, Read<T3>, Read<T4>)> Batch<T1, T2, T3, T4>(Func<ValueTask<T1>> a,
        Func<ValueTask<T2>> b, Func<ValueTask<T3>> c, Func<ValueTask<T4>> d)
    {
        var counter = BeginBatch();
        ValueTask<Read<T1>> ta;
        ValueTask<Read<T2>> tb;
        ValueTask<Read<T3>> tc;
        ValueTask<Read<T4>> td;
        try
        {
            ta = Go(a);
            tb = Go(b);
            tc = Go(c);
            td = Go(d);
        }
        finally
        {
            EndBatch(counter);
        }
        return (await ta.ConfigureAwait(false), await tb.ConfigureAwait(false), await tc.ConfigureAwait(false),
            await td.ConfigureAwait(false));
    }

    public async ValueTask<(Read<T1>, Read<T2>, Read<T3>, Read<T4>, Read<T5>)> Batch<T1, T2, T3, T4, T5>(
        Func<ValueTask<T1>> a, Func<ValueTask<T2>> b, Func<ValueTask<T3>> c, Func<ValueTask<T4>> d,
        Func<ValueTask<T5>> e)
    {
        var counter = BeginBatch();
        ValueTask<Read<T1>> ta;
        ValueTask<Read<T2>> tb;
        ValueTask<Read<T3>> tc;
        ValueTask<Read<T4>> td;
        ValueTask<Read<T5>> te;
        try
        {
            ta = Go(a);
            tb = Go(b);
            tc = Go(c);
            td = Go(d);
            te = Go(e);
        }
        finally
        {
            EndBatch(counter);
        }
        return (await ta.ConfigureAwait(false), await tb.ConfigureAwait(false), await tc.ConfigureAwait(false),
            await td.ConfigureAwait(false), await te.ConfigureAwait(false));
    }

    public async ValueTask<(Read<T1>, Read<T2>, Read<T3>, Read<T4>, Read<T5>, Read<T6>)> Batch<T1, T2, T3, T4, T5, T6>(
        Func<ValueTask<T1>> a, Func<ValueTask<T2>> b, Func<ValueTask<T3>> c, Func<ValueTask<T4>> d,
        Func<ValueTask<T5>> e, Func<ValueTask<T6>> f)
    {
        var counter = BeginBatch();
        ValueTask<Read<T1>> ta;
        ValueTask<Read<T2>> tb;
        ValueTask<Read<T3>> tc;
        ValueTask<Read<T4>> td;
        ValueTask<Read<T5>> te;
        ValueTask<Read<T6>> tf;
        try
        {
            ta = Go(a);
            tb = Go(b);
            tc = Go(c);
            td = Go(d);
            te = Go(e);
            tf = Go(f);
        }
        finally
        {
            EndBatch(counter);
        }
        return (await ta.ConfigureAwait(false), await tb.ConfigureAwait(false), await tc.ConfigureAwait(false),
            await td.ConfigureAwait(false), await te.ConfigureAwait(false), await tf.ConfigureAwait(false));
    }

    public async ValueTask<(Read<T1>, Read<T2>, Read<T3>, Read<T4>, Read<T5>, Read<T6>, Read<T7>)>
        Batch<T1, T2, T3, T4, T5, T6, T7>(Func<ValueTask<T1>> a, Func<ValueTask<T2>> b, Func<ValueTask<T3>> c,
            Func<ValueTask<T4>> d, Func<ValueTask<T5>> e, Func<ValueTask<T6>> f, Func<ValueTask<T7>> g)
    {
        var counter = BeginBatch();
        ValueTask<Read<T1>> ta;
        ValueTask<Read<T2>> tb;
        ValueTask<Read<T3>> tc;
        ValueTask<Read<T4>> td;
        ValueTask<Read<T5>> te;
        ValueTask<Read<T6>> tf;
        ValueTask<Read<T7>> tg;
        try
        {
            ta = Go(a);
            tb = Go(b);
            tc = Go(c);
            td = Go(d);
            te = Go(e);
            tf = Go(f);
            tg = Go(g);
        }
        finally
        {
            EndBatch(counter);
        }
        return (await ta.ConfigureAwait(false), await tb.ConfigureAwait(false), await tc.ConfigureAwait(false),
            await td.ConfigureAwait(false), await te.ConfigureAwait(false), await tf.ConfigureAwait(false),
            await tg.ConfigureAwait(false));
    }

    public async ValueTask<GameValue> Context(bool fresh = false, CancellationToken ct = default)
    {
        if (fresh || _context is null || Clock.Monotonic() - _contextAt > 0.5)
        {
            _context = await Request("context", (Action<Utf8JsonWriter>?)null, null, ct).ConfigureAwait(false);
            _contextAt = Clock.Monotonic();
        }
        return _context.Value;
    }

    public void ForgetContext() => _context = null;

    public async ValueTask<string> World(CancellationToken ct = default) =>
        (await Context(ct: ct).ConfigureAwait(false))["world"].Text ?? "";

    public async ValueTask<string> Pawn(CancellationToken ct = default) =>
        (await Context(ct: ct).ConfigureAwait(false))["character"].Text ?? "";

    public async ValueTask<string> Controller(CancellationToken ct = default) =>
        (await Context(ct: ct).ConfigureAwait(false))["controller"].Text ?? "";

    public async ValueTask<GameValue> Call(string obj, string function, object? parameters = null,
        CancellationToken ct = default)
    {
        if (ChangesState().IsMatch(function))
        {
            Writes.Add(new Dictionary<string, object?>
            {
                ["t"] = T, ["object"] = obj, ["name"] = Engine.NameOf(obj), ["call"] = function,
                ["parameters"] = parameters,
            });
            Log("write", new Dictionary<string, object?>
            {
                ["object"] = obj, ["call"] = function, ["parameters"] = parameters,
            });
        }
        return await Request("rc", new Dictionary<string, object?>
        {
            ["verb"] = "call",
            ["body"] = new Dictionary<string, object?>
            {
                ["objectPath"] = obj, ["functionName"] = function,
                ["parameters"] = parameters ?? new Dictionary<string, object?>(),
            },
        }, null, ct).ConfigureAwait(false);
    }

    public async ValueTask<GameValue> Ret(string obj, string function, object? parameters = null,
        CancellationToken ct = default) =>
        (await Call(obj, function, parameters, ct).ConfigureAwait(false))["ReturnValue"];

    public async ValueTask<GameValue> Get(string obj, string prop, CancellationToken ct = default)
    {
        var answer = await Request("rc", json =>
        {
            json.WriteString("verb", "property");
            json.WriteStartObject("body");
            json.WriteString("objectPath", obj);
            json.WriteString("propertyName", prop);
            json.WriteEndObject();
        }, null, ct).ConfigureAwait(false);
        if (answer.Has(prop))
            return answer[prop];
        return answer.Count == 1 && answer.IsObject ? answer.Fields.First().Value : answer;
    }

    public async ValueTask<double> Num(string obj, string prop, CancellationToken ct = default) =>
        (await Get(obj, prop, ct).ConfigureAwait(false)).Number ?? 0;

    public async ValueTask<string> Text(string obj, string prop, CancellationToken ct = default) =>
        (await Get(obj, prop, ct).ConfigureAwait(false)).Text ?? "";

    public ValueTask<GameValue> Props(string obj, CancellationToken ct = default) =>
        Request("rc", json =>
        {
            json.WriteString("verb", "property");
            json.WriteStartObject("body");
            json.WriteString("objectPath", obj);
            json.WriteEndObject();
        }, null, ct);

    public ValueTask<GameValue> Describe(string obj, CancellationToken ct = default) =>
        Request("rc", json =>
        {
            json.WriteString("verb", "describe");
            json.WriteStartObject("body");
            json.WriteString("objectPath", obj);
            json.WriteEndObject();
        }, null, ct);

    public async ValueTask Set(string obj, string prop, object? value, string why = "", CancellationToken ct = default)
    {
        await Request("rc", new Dictionary<string, object?>
        {
            ["verb"] = "property",
            ["body"] = new Dictionary<string, object?>
            {
                ["objectPath"] = obj, ["propertyName"] = prop,
                ["propertyValue"] = new Dictionary<string, object?> { [prop] = value },
                ["access"] = "WRITE_ACCESS",
            },
        }, null, ct).ConfigureAwait(false);
        Writes.Add(new Dictionary<string, object?>
        {
            ["t"] = T, ["object"] = obj, ["name"] = Engine.NameOf(obj), ["property"] = prop, ["value"] = value,
            ["why"] = why,
        });
        Log("write", new Dictionary<string, object?>
        {
            ["object"] = obj, ["property"] = prop, ["value"] = value, ["why"] = why,
        });
    }

    public string ResolveClass(string name) => name;

    public async ValueTask<List<string>> Actors(string cls, CancellationToken ct = default) =>
        (await Request("find", json => json.WriteString("class", cls), null, ct).ConfigureAwait(false))["objects"]
        .Strings();

    public async ValueTask<List<string>> AllActors(CancellationToken ct = default) =>
        (await Request("find", json => json.WriteBoolean("things", true), null, ct).ConfigureAwait(false))["objects"]
        .Strings();

    public async ValueTask<List<string>> Named(string part, CancellationToken ct = default)
    {
        var all = await AllActors(ct).ConfigureAwait(false);
        return all.Where(actor => Engine.NameOf(actor).Contains(part, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async ValueTask<bool> Exists(string obj, CancellationToken ct = default)
    {
        try
        {
            await Describe(obj, ct).ConfigureAwait(false);
            return true;
        }
        catch (GameException)
        {
            return false;
        }
    }

    public async ValueTask<GameValue> Transforms(IReadOnlyCollection<string> objects, CancellationToken ct = default) =>
        (await Request("transforms", json =>
        {
            json.WriteStartArray("objects");
            foreach (var path in objects)
                json.WriteStringValue(path);
            json.WriteEndArray();
        }, null, ct).ConfigureAwait(false))["objects"];

    async ValueTask<GameValue> One(string actor, CancellationToken ct)
    {
        var one = (await Transforms([actor], ct).ConfigureAwait(false))[actor];
        return one.IsNull ? throw new GameException(400, $"transforms: Object {actor} does not exist") : one;
    }

    public async ValueTask<Vec> Loc(string actor, CancellationToken ct = default) =>
        (await One(actor, ct).ConfigureAwait(false))["location"].Vector ?? default;

    public async ValueTask<GameValue> Rot(string actor, CancellationToken ct = default) =>
        (await One(actor, ct).ConfigureAwait(false))["rotation"];

    public async ValueTask<(Vec Center, Vec Extent)> Bounds(string actor, CancellationToken ct = default)
    {
        var t = await One(actor, ct).ConfigureAwait(false);
        return (t["center"].Vector ?? t["location"].Vector ?? default, t["extent"].Vector ?? default);
    }

    public async ValueTask<Vec> Velocity(string actor, CancellationToken ct = default) =>
        (await One(actor, ct).ConfigureAwait(false))["velocity"].Vector ?? default;

    public async ValueTask<Dictionary<string, Vec>> Locs(IReadOnlyCollection<string> actors,
        CancellationToken ct = default)
    {
        var found = new Dictionary<string, Vec>();
        if (actors.Count == 0)
            return found;
        var got = await Transforms(actors, ct).ConfigureAwait(false);
        foreach (var actor in actors)
            if (got[actor]["location"].Vector is { } at)
                found[actor] = at;
        return found;
    }

    public string NameOf(string path) => Engine.NameOf(path);

    public Vec? ToKit(GameValue value) => Engine.ToKit(value);

    public Dictionary<string, object?> ToNative(Vec value) => Engine.ToNative(value);

    public async ValueTask<Vec> Me(CancellationToken ct = default) =>
        (await Pose(ct).ConfigureAwait(false)).At;

    public async ValueTask<(double Yaw, double Pitch)> ViewRotation(CancellationToken ct = default)
    {
        using var answer = await Raw("pose", null, null, ct).ConfigureAwait(false);
        var pose = new GameValue(answer.Result);
        return (pose["yaw"].Number ?? 0, pose["pitch"].Number ?? 0);
    }

    public async ValueTask<Vec> Eye(CancellationToken ct = default) =>
        (await Pose(ct).ConfigureAwait(false)).Eye;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<Pose> Pose(CancellationToken ct = default)
    {
        using var answer = await Raw("pose", null, null, ct).ConfigureAwait(false);
        return ParsePose(answer.Result);
    }

    static Pose ParsePose(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("at", out var atField)
                                                 || atField.ValueKind == JsonValueKind.Null)
            throw new GameException(400, "pose: no player");
        var at = Point(atField);
        var eye = p.TryGetProperty("eye", out var e) && e.ValueKind == JsonValueKind.Object ? Point(e) : at;
        var yaw = p.TryGetProperty("yaw", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetDouble() : 0;
        var pitch = p.TryGetProperty("pitch", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetDouble() : 0;
        var velocity = p.TryGetProperty("velocity", out var v) && v.ValueKind == JsonValueKind.Object ? Point(v) : default;
        bool? ground = p.TryGetProperty("on_ground", out var g) && g.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? g.GetBoolean() : null;
        return new Pose(at, eye, yaw, pitch, velocity, ground);
    }

    static Vec Point(JsonElement v) => new(Axis(v, "X"), Axis(v, "Y"), Axis(v, "Z"));

    static double Axis(JsonElement v, string name) =>
        v.TryGetProperty(name, out var axis) && axis.ValueKind == JsonValueKind.Number ? axis.GetDouble() : 0;

    public ValueTask<(double Right, double Up)> AimError(Vec target, CancellationToken ct) => AimError(target, null, ct);

    public async ValueTask<(double Right, double Up)> AimError(Vec target, Pose? pose = null,
        CancellationToken ct = default)
    {
        var p = pose ?? await Pose(ct).ConfigureAwait(false);
        return Geom.AimError(p.Eye, p.Yaw, p.Pitch, target);
    }

    public ValueTask<GameValue> View(object? args = null, CancellationToken ct = default) =>
        Request("view", args, null, ct);

    public async ValueTask<Dictionary<string, Seen>> Sees(IEnumerable<string> actors, CancellationToken ct = default)
    {
        var byName = new Dictionary<string, string>();
        foreach (var actor in actors)
            byName[NameOf(actor)] = actor;
        var found = new Dictionary<string, Seen>();
        if (byName.Count == 0)
            return found;
        var view = await Request("view", json =>
        {
            json.WriteStartArray("only");
            foreach (var name in byName.Keys)
                json.WriteStringValue(name);
            json.WriteEndArray();
            json.WriteNumber("max_count", byName.Count);
        }, null, ct).ConfigureAwait(false);
        foreach (var thing in view["visible"].Items)
        {
            if (thing["name"].Text is { } name && byName.TryGetValue(name, out var actor))
                found[actor] = new Seen(thing["distance"].Number, thing["screen_x"].Number, thing["screen_y"].Number,
                    thing.Has("partly_in_view") && !thing["partly_in_view"].IsNull ? thing["partly_in_view"].Truthy : null,
                    thing["crosshair_on"].Truthy);
        }
        return found;
    }

    public async ValueTask<Dictionary<string, string>> Screen(CancellationToken ct = default)
    {
        var texts = (await Request("screen", (Action<Utf8JsonWriter>?)null, null, ct).ConfigureAwait(false))["texts"];
        return texts.Fields.ToDictionary(field => field.Key, field => field.Value.Text ?? field.Value.ToString());
    }

    public async ValueTask<List<GameValue>> Ui(CancellationToken ct = default) =>
        (await Request("ui", (Action<Utf8JsonWriter>?)null, null, ct).ConfigureAwait(false))["elements"].Items.ToList();

    public ValueTask<GameValue> UiAct(object args, CancellationToken ct = default) => Request("ui_act", args, null, ct);

    static object? AxisValue(object? value) => value switch
    {
        null => new Vec(1).Ue(),
        double or float or int or long => new Vec(Convert.ToDouble(value)).Ue(),
        Vec vec => vec.Ue(),
        _ => value,
    };

    static void WriteValue(Utf8JsonWriter json, object? value)
    {
        json.WritePropertyName("value");
        Jsonable.Write(json, value, 0, round: false);
    }

    public void Press(string key, object? value = null)
    {
        if (key.StartsWith('/'))
        {
            Link.Post("input", json =>
            {
                json.WriteString("action", key);
                WriteValue(json, AxisValue(value));
                json.WriteString("mode", "press");
            });
        }
        else
        {
            Link.Post("key", json =>
            {
                json.WriteString("key", key);
                json.WriteString("mode", "press");
                if (value is not null)
                    WriteValue(json, value);
            });
        }
        Log("input", new Dictionary<string, object?> { ["key"] = key, ["mode"] = "press", ["value"] = value });
    }

    Action<Utf8JsonWriter> Lease(string key, object? value)
    {
        if (value is not null)
            return json =>
            {
                json.WriteString("key", key);
                json.WriteString("mode", "hold");
                json.WriteNumber("lease", KeyLease);
                WriteValue(json, value);
            };
        lock (_lock)
        {
            if (!_leases.TryGetValue(key, out var lease))
                _leases[key] = lease = json =>
                {
                    json.WriteString("key", key);
                    json.WriteString("mode", "hold");
                    json.WriteNumber("lease", KeyLease);
                };
            return lease;
        }
    }

    public void Hold(string key, object? value = null)
    {
        bool isNew;
        lock (_lock)
        {
            isNew = !_held.TryGetValue(key, out var was) || !Equals(was, value);
            _held[key] = value;
        }
        if (key.StartsWith('/'))
            Link.Post("input", json =>
            {
                json.WriteString("action", key);
                WriteValue(json, AxisValue(value));
                json.WriteString("mode", "hold");
            });
        else
            Link.Post("key", Lease(key, value));
        if (isNew)
            Log("input", new Dictionary<string, object?> { ["key"] = key, ["mode"] = "hold", ["value"] = value });
    }

    public bool HoldTimed(string key, double seconds, object? value = null)
    {
        if (key.StartsWith('/') || !Features.Contains("timed_hold"))
            return false;
        Link.Post("key", json =>
        {
            json.WriteString("key", key);
            json.WriteString("mode", "hold");
            json.WriteNumber("seconds", seconds);
            json.WriteNumber("lease", KeyLease);
            if (value is not null)
                WriteValue(json, value);
        });
        Log("input", new Dictionary<string, object?>
        {
            ["key"] = key, ["mode"] = "hold", ["value"] = value, ["seconds"] = seconds,
        });
        return true;
    }

    public void Release(string key)
    {
        lock (_lock)
        {
            if (!_held.Remove(key))
                return;
        }
        LetGo(key);
        Log("input", new Dictionary<string, object?> { ["key"] = key, ["mode"] = "release" });
    }

    internal void LetGo(string key)
    {
        if (key.StartsWith('/'))
            Link.Post("input", json =>
            {
                json.WriteString("action", key);
                json.WriteString("mode", "release");
            });
        else
            Link.Post("key", json =>
            {
                json.WriteString("key", key);
                json.WriteString("mode", "release");
            });
    }

    public void ReleaseAll()
    {
        List<string> held;
        lock (_lock)
        {
            held = [.. _held.Keys];
            _held.Clear();
        }
        foreach (var key in held)
            if (key.StartsWith('/'))
                LetGo(key);
        Link.Post("key", static json => json.WriteString("mode", "release_all"));
        if (held.Count > 0)
            Log("input", new Dictionary<string, object?> { ["key"] = "*", ["mode"] = "release_all" });
    }

    public Dictionary<string, object?> Held()
    {
        lock (_lock)
            return new Dictionary<string, object?>(_held);
    }

    public void Renew()
    {
        lock (_lock)
        {
            foreach (var (key, value) in _held)
                if (!key.StartsWith('/'))
                    Link.Post("key", Lease(key, value));
        }
    }

    public void Mouse(double right = 0, double up = 0)
    {
        if (Math.Abs(right) >= 0.5)
        {
            var counts = Fmt.Round(right, 1);
            Link.Post("key", json =>
            {
                json.WriteString("key", "MouseX");
                json.WriteString("mode", "press");
                json.WriteNumber("value", counts);
            });
            TurnAsked += Math.Abs(right * (Look.X ?? 0));
        }
        if (Math.Abs(up) >= 0.5)
        {
            var counts = Fmt.Round(up, 1);
            Link.Post("key", json =>
            {
                json.WriteString("key", "MouseY");
                json.WriteString("mode", "press");
                json.WriteNumber("value", counts);
            });
        }
    }

    public async ValueTask Turn(double rightDeg = 0, double upDeg = 0, CancellationToken ct = default)
    {
        await CalibrateLook(ct: ct).ConfigureAwait(false);
        var (rx, ry) = (Look.X, Look.Y);
        Mouse(rx is > 0 or < 0 ? rightDeg / rx.Value : 0, ry is > 0 or < 0 ? upDeg / ry.Value : 0);
    }

    public async ValueTask<LookScale> CalibrateLook(double counts = 60, CancellationToken ct = default)
    {
        if (Look.X is > 0 or < 0 && Look.Y is > 0 or < 0)
            return Look;
        if (Clock.Monotonic() - _calibratedAt < 0.5)
            return Look;
        _calibratedAt = Clock.Monotonic();
        foreach (var horizontal in new[] { true, false })
        {
            if (horizontal ? Look.X is > 0 or < 0 : Look.Y is > 0 or < 0)
                continue;
            var (y0, p0) = await ViewRotation(ct).ConfigureAwait(false);
            Mouse(horizontal ? counts : 0, horizontal ? 0 : counts);
            await Clock.Delay(0.15, ct).ConfigureAwait(false);
            var (y1, p1) = await ViewRotation(ct).ConfigureAwait(false);
            var moved = horizontal ? Geom.Wrap(y1 - y0) : p1 - p0;
            if (Math.Abs(moved) > 0.05)
            {
                if (horizontal)
                    Look.X = moved / counts;
                else
                    Look.Y = moved / counts;
                Mouse(horizontal ? -counts : 0, horizontal ? 0 : -counts);
                await Clock.Delay(0.1, ct).ConfigureAwait(false);
            }
            else
            {
                Mouse(0, horizontal ? 0 : -counts);
                await Clock.Delay(0.15, ct).ConfigureAwait(false);
                var (_, p2) = await ViewRotation(ct).ConfigureAwait(false);
                var other = horizontal ? 0 : p2 - p1;
                if (Math.Abs(other) > 0.05)
                {
                    Look.Y = -other / counts;
                    Mouse(0, counts);
                    await Clock.Delay(0.1, ct).ConfigureAwait(false);
                }
            }
        }
        Log("calibrated", new Dictionary<string, object?>
        {
            ["look"] = new Dictionary<string, object?> { ["x"] = Look.X, ["y"] = Look.Y },
        });
        return Look;
    }

    public ValueTask<GameValue> Pause(bool paused = true, CancellationToken ct = default) =>
        Request("pause", json => json.WriteBoolean("paused", paused), null, ct);

    public async ValueTask<bool> Restart(string? map = null, double waitForPawn = 20, CancellationToken ct = default)
    {
        ReleaseAll();
        map ??= Level;
        await Request("session", json =>
        {
            json.WriteString("action", "restart");
            if (!string.IsNullOrEmpty(map))
                json.WriteString("map", map);
        }, SessionTimeout.TotalSeconds, ct).ConfigureAwait(false);
        _context = null;
        var deadline = Clock.Monotonic() + waitForPawn;
        while (Clock.Monotonic() < deadline)
        {
            try
            {
                if ((await Context(fresh: true, ct).ConfigureAwait(false))["character"].Truthy)
                    return true;
            }
            catch (GameException)
            {
            }
            await Clock.Delay(0.25, ct).ConfigureAwait(false);
        }
        return false;
    }

    public ValueTask Sleep(double seconds, CancellationToken ct = default) => Clock.Delay(seconds, ct);
}
