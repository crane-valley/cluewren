using System.Text.Json;
using PlaytestAgent.Bridge;
using PlaytestAgent.Kit;
using PlaytestAgent.Sandbox;

namespace PlaytestAgent.Mcp;

public sealed record FramePick(string Text, IReadOnlyList<byte[]> Jpegs);

// Frames stay in this process's memory: ADR 0003 keeps no recording on disk and sends none anywhere.
public sealed class FrameRecorder : IEpisodeRecorder, IAsyncDisposable
{
    public const int MaxFrames = 12;
    public const int DefaultFrames = 8;
    public const int MaxFps = 20;
    public const int DefaultFps = 5;
    const int MaxDim = 960;
    const int Quality = 75;
    const long DefaultKeptBytes = 160L << 20;
    const int MaxAnswerBytes = 3_000_000;
    const double Margin = 0.5;

    readonly record struct Frame(double? T, double Wall, byte[] Jpeg);

    readonly BridgeLink _link;
    readonly long _keptBytes;
    readonly Queue<Frame> _frames = new();
    readonly Dictionary<int, (double Start, double? End)> _spans = [];
    readonly CancellationTokenSource _stop = new();
    readonly Task _reading;
    readonly object _gate = new();
    long _bytes;
    double _droppedBefore;
    volatile string? _missing = "no picture came";

    public FrameRecorder(BridgeLink link, long? keptBytes = null)
    {
        _link = link;
        _keptBytes = keptBytes ?? DefaultKeptBytes;
        _reading = ReadAsync(_stop.Token);
    }

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public bool CanFilm => _link.Features.Contains("media");

    public string? Missing => CanFilm ? _missing : "this build of the game can't capture its picture";

    public async Task FilmAsync(int fps = DefaultFps, CancellationToken ct = default)
    {
        if (!CanFilm)
            return;
        try
        {
            using var answer = await _link.RequestAsync("record", json =>
            {
                json.WriteString("action", "start");
                json.WriteNumber("fps", Math.Clamp(fps, 1, MaxFps));
                json.WriteNumber("max_dim", MaxDim);
                json.WriteNumber("quality", Quality);
            }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            _missing = null;
        }
        catch (Exception e) when (e is GameException or GameDisconnectedException)
        {
            _missing = e.Message;
        }
    }

    async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _link.Messages.ReadAllAsync(ct).ConfigureAwait(false))
                using (message)
                    if (message.RootElement.TryGetProperty("type", out var type) && type.ValueEquals("frame"))
                        Keep(message.RootElement);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Keep(JsonElement frame)
    {
        if (!frame.TryGetProperty("jpeg", out var data) || !data.TryGetBytesFromBase64(out var jpeg))
            return;
        var t = frame.TryGetProperty("t", out var time) && time.ValueKind == JsonValueKind.Number ? time.GetDouble() : (double?)null;
        var wall = frame.TryGetProperty("wall", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetDouble() : Now;
        lock (_gate)
        {
            _frames.Enqueue(new Frame(t, wall, jpeg));
            _bytes += jpeg.Length;
            while (_bytes > _keptBytes && _frames.TryDequeue(out var oldest))
            {
                _bytes -= oldest.Jpeg.Length;
                _droppedBefore = oldest.Wall;
            }
        }
    }

    public void Begin(int episode)
    {
        lock (_gate)
            _spans[episode] = (Now, null);
    }

    public void End(int episode)
    {
        lock (_gate)
            if (_spans.TryGetValue(episode, out var span))
                _spans[episode] = (span.Start, Now);
    }

    public FramePick Pick(int episode, double? start, double? end, int? count)
    {
        var label = $"episode {episode}";
        Frame[] frames;
        double dropped;
        (double Start, double? End) span;
        lock (_gate)
        {
            if (!_spans.TryGetValue(episode, out span))
                return new FramePick(episode > 0 ? $"No episode {episode} has been played in this run."
                    : "No episode has been played yet: run_harness first (its frames are what can be looked at).", []);
            var first = span.Start - Margin;
            var last = (span.End ?? Now) + Margin;
            frames = _frames.Where(frame => frame.Wall >= first && frame.Wall <= last).ToArray();
            dropped = _droppedBefore;
        }
        if (start is not null || end is not null)
        {
            var (low, high) = (start ?? double.MinValue, end ?? double.MaxValue);
            var shown = frames.Where(frame => frame.T >= low && frame.T <= high).ToArray();
            if (shown.Length == 0)
            {
                var times = frames.Where(frame => frame.T is not null).Select(frame => frame.T!.Value).ToArray();
                var there = times.Length > 0 ? $" (its frames show game times {times.Min():F2} to {times.Max():F2} s)" : "";
                return new FramePick($"No frames of {label} show game times {start} to {end}{there}.", []);
            }
            frames = shown;
        }
        if (frames.Length == 0)
            return new FramePick(span.End <= dropped
                ? $"The frames of {label} aren't kept any more (only the last minutes of play are): play it again, then look at it."
                : $"No frames of {label}: the game wasn't filmed while it played ({Missing ?? "no picture came: was its window minimized or hidden?"}).", []);
        var chosen = Spread(frames, Math.Clamp(count ?? DefaultFrames, 1, MaxFrames));
        var wanted = chosen.Length;
        while (wanted > 1 && chosen.Sum(frame => (long)frame.Jpeg.Length) * 4 / 3 > MaxAnswerBytes)
            chosen = Spread(frames, --wanted);
        var at = string.Join(", ", chosen.Select(frame => frame.T is { } t ? t.ToString("F2") : "?"));
        return new FramePick($"{chosen.Length} frames of {frames.Length} ({label}), in order, at game times {at} s:",
            chosen.Select(frame => frame.Jpeg).ToArray());
    }

    static Frame[] Spread(Frame[] frames, int count)
    {
        if (count >= frames.Length)
            return frames;
        if (count == 1)
            return [frames[frames.Length / 2]];
        return Enumerable.Range(0, count).Select(i => frames[(int)Math.Round(i * (frames.Length - 1.0) / (count - 1))]).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _reading.ConfigureAwait(false);
        _stop.Dispose();
        lock (_gate)
            _frames.Clear();
    }
}
