using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

namespace PlaytestAgent.Kit;

public sealed class GameException(int status, string error) : Exception(error)
{
    public int Status { get; } = status;
}

public sealed class GameDisconnectedException(string why) : Exception(why);

public sealed class GameAnswer(JsonDocument document, byte[]? rented = null) : IDisposable
{
    public JsonElement Frame => document.RootElement;

    public JsonElement Result => Frame.TryGetProperty("result", out var result) ? result : default;

    public double? GameTime => Frame.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number
        ? t.GetDouble() : null;

    public void Dispose()
    {
        document.Dispose();
        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);
    }
}

public interface IGameLink
{
    ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default);

    void Post(string op, Action<Utf8JsonWriter>? args = null);

    double? GameTime => null;

    string? Closed => null;

    IReadOnlyCollection<string> Features => [];

    string Engine => "unity";

    void Status(string text)
    {
    }

    void Send(Action<Utf8JsonWriter> frame)
    {
    }

    // Reads started between Cork and Uncork leave in one write, so the plugin takes them in the same frame.
    void Cork()
    {
    }

    void Uncork()
    {
    }

    event Action<JsonElement>? Event
    {
        add { }
        remove { }
    }
}

public interface IClock
{
    double Monotonic();

    ValueTask Delay(double seconds, CancellationToken ct = default);

    ITicker Ticker(double seconds);
}

public interface ITicker : IDisposable
{
    ValueTask Wait(CancellationToken ct = default);
}

public sealed class RealClock : IClock
{
    public static readonly RealClock Instance = new();

    public double Monotonic() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public ValueTask Delay(double seconds, CancellationToken ct = default) =>
        seconds <= 0 ? default : new ValueTask(Task.Delay(TimeSpan.FromSeconds(seconds), ct));

    public ITicker Ticker(double seconds) => new PeriodicTicker(seconds);

    sealed class PeriodicTicker(double seconds) : ITicker
    {
        readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(seconds));

        public ValueTask Wait(CancellationToken ct = default)
        {
            var tick = _timer.WaitForNextTickAsync(ct);
            if (!tick.IsCompletedSuccessfully)
                return Slow(tick);
            // A timer that fired just after the call completes a pooled source, and the next wait throws unless it is consumed.
            tick.GetAwaiter().GetResult();
            return default;
        }

        static async ValueTask Slow(ValueTask<bool> tick) => await tick.ConfigureAwait(false);

        public void Dispose() => _timer.Dispose();
    }
}
