using System.Text;
using System.Text.Json;
using PlaytestAgent.Kit;

namespace PlaytestAgent.Tests.Sim;

// What upstream's fakes do by answering dicts: an op's result as plain objects, encoded as the plugin would encode them.
public abstract class SimLink : IGameLink
{
    public double? GameTime { get; protected set; }

    public string? Closed { get; set; }

    public List<string> FeatureList { get; } = [];

    public IReadOnlyCollection<string> Features => FeatureList;

    public event Action<JsonElement>? Event;

    public int Listeners => Event?.GetInvocationList().Length ?? 0;

    public void Raise(object evt)
    {
        using var document = JsonDocument.Parse(Jsonable.Encode(evt, round: false));
        Event?.Invoke(document.RootElement);
    }

    protected abstract object? Answer(string op, JsonElement args);

    protected static JsonDocument Decode(Action<Utf8JsonWriter>? args)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            args?.Invoke(json);
            json.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    public virtual ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        try
        {
            using var decoded = Decode(args);
            var result = Answer(op, decoded.RootElement);
            var frame = new Dictionary<string, object?> { ["ok"] = true, ["result"] = result, ["t"] = GameTime };
            return new ValueTask<GameAnswer>(new GameAnswer(JsonDocument.Parse(Jsonable.Encode(frame, round: false))));
        }
        catch (Exception e) when (e is not GameException)
        {
            return ValueTask.FromException<GameAnswer>(e);
        }
    }

    public virtual void Post(string op, Action<Utf8JsonWriter>? args = null)
    {
        using var decoded = Decode(args);
        Answer(op, decoded.RootElement);
    }

    protected static double Num(JsonElement args, string name, double fallback = 0) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;

    protected static string? Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    protected static bool Flag(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

public sealed class VirtualClock : IClock
{
    public double T;

    public double Monotonic() => T;

    public ValueTask Delay(double seconds, CancellationToken ct = default)
    {
        T += seconds;
        return default;
    }

    public ITicker Ticker(double seconds) => new VirtualTicker(this, seconds);

    sealed class VirtualTicker(VirtualClock clock, double seconds) : ITicker
    {
        public ValueTask Wait(CancellationToken ct = default) => clock.Delay(seconds, ct);

        public void Dispose()
        {
        }
    }
}

// CPython's random.Random, so the clutter levels are upstream's levels.
public sealed class PyRandom
{
    readonly uint[] _mt = new uint[624];
    int _index = 625;

    public PyRandom(uint seed)
    {
        Init(19650218);
        uint i = 1, j = 0;
        for (var k = 624; k > 0; k--)
        {
            _mt[i] = (_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1664525)) + seed + j;
            i++;
            j++;
            if (i >= 624)
            {
                _mt[0] = _mt[623];
                i = 1;
            }
            if (j >= 1)
                j = 0;
        }
        for (var k = 623; k > 0; k--)
        {
            _mt[i] = (_mt[i] ^ ((_mt[i - 1] ^ (_mt[i - 1] >> 30)) * 1566083941)) - i;
            i++;
            if (i >= 624)
            {
                _mt[0] = _mt[623];
                i = 1;
            }
        }
        _mt[0] = 0x80000000;
    }

    void Init(uint s)
    {
        _mt[0] = s;
        for (uint i = 1; i < 624; i++)
            _mt[i] = 1812433253 * (_mt[i - 1] ^ (_mt[i - 1] >> 30)) + i;
        _index = 624;
    }

    uint Next()
    {
        if (_index >= 624)
        {
            for (var k = 0; k < 624; k++)
            {
                var y = (_mt[k] & 0x80000000) | (_mt[(k + 1) % 624] & 0x7fffffff);
                _mt[k] = _mt[(k + 397) % 624] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908b0dfu : 0);
            }
            _index = 0;
        }
        var v = _mt[_index++];
        v ^= v >> 11;
        v ^= (v << 7) & 0x9d2c5680;
        v ^= (v << 15) & 0xefc60000;
        return v ^ (v >> 18);
    }

    public double NextDouble()
    {
        var (a, b) = (Next() >> 5, Next() >> 6);
        return (a * 67108864.0 + b) * (1.0 / 9007199254740992.0);
    }

    public double Uniform(double a, double b) => a + (b - a) * NextDouble();
}
