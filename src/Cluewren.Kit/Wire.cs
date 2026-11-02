using System.Buffers;
using System.Text.Json;

namespace Cluewren.Kit;

public sealed class LineWriter(Stream stream)
{
    readonly Lock _lock = new();
    readonly ArrayBufferWriter<byte> _buffer = new(4096);
    readonly Utf8JsonWriter _json = new(Stream.Null);
    int _corks;

    public void Write<T>(T state, Action<Utf8JsonWriter, T> body)
    {
        lock (_lock)
        {
            _json.Reset(_buffer);
            _json.WriteStartObject();
            body(_json, state);
            _json.WriteEndObject();
            _json.Flush();
            _buffer.GetSpan(1)[0] = (byte)'\n';
            _buffer.Advance(1);
            if (_corks == 0)
                Flush();
        }
    }

    public void Cork()
    {
        lock (_lock)
            _corks++;
    }

    public void Uncork()
    {
        lock (_lock)
        {
            if (--_corks == 0 && _buffer.WrittenCount > 0)
                Flush();
        }
    }

    void Flush()
    {
        try
        {
            stream.Write(_buffer.WrittenSpan);
            stream.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        _buffer.Clear();
    }
}

public sealed class LineReader(Stream stream, int maxLine = 32 << 20)
{
    readonly byte[] _chunk = new byte[1 << 16];
    int _start, _end;

    // The line, in a rented array that stays valid until its JsonDocument is done with: GameAnswer gives it back.
    public (byte[] Rented, int Length)? Next()
    {
        byte[]? held = null;
        var heldLength = 0;
        while (true)
        {
            var at = Array.IndexOf(_chunk, (byte)'\n', _start, _end - _start);
            var take = (at >= 0 ? at : _end) - _start;
            if (heldLength + take > maxLine)
            {
                if (held is not null)
                    ArrayPool<byte>.Shared.Return(held);
                throw new InvalidDataException("a line longer than the limit");
            }
            if (take > 0 || at >= 0)
            {
                var grown = ArrayPool<byte>.Shared.Rent(Math.Max(heldLength + take, 512));
                if (held is not null)
                {
                    held.AsSpan(0, heldLength).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(held);
                }
                _chunk.AsSpan(_start, take).CopyTo(grown.AsSpan(heldLength));
                held = grown;
                heldLength += take;
            }
            if (at >= 0)
            {
                _start = at + 1;
                return (held!, heldLength);
            }
            _start = _end = 0;
            var read = stream.Read(_chunk, 0, _chunk.Length);
            if (read <= 0)
            {
                if (held is not null)
                    ArrayPool<byte>.Shared.Return(held);
                return null;
            }
            _end = read;
        }
    }
}

public sealed class ProbeGlobals(Game game, Func<double, bool, IReadOnlyList<string>?, CancellationToken, Task<string>>? check,
    CancellationToken token)
{
    public Game game { get; } = game;

    public CancellationToken ct { get; } = token;

    public void Print(params object?[] values) =>
        Console.WriteLine(string.Join(' ', values.Select(value => value is string or Vec ? value.ToString() : Fmt.Json(value))));

    public Task<Result> Run(Func<Task<Result>> controller, double timeout = 15, double hz = 15) =>
        Control.Run(game, _ => controller(), timeout, hz, ct);

    public Task<Result> Run(Game on, Func<Task<Result>> controller, double timeout = 15, double hz = 15) =>
        Control.Run(on, _ => controller(), timeout, hz, ct);

    public Task<string> CheckHarness(double seconds = Runtime.CheckSeconds, bool restart = true,
        IReadOnlyList<string>? only = null) =>
        check?.Invoke(seconds, restart, only, ct)
        ?? Task.FromResult("harness.cs doesn't exist yet: write it first.");
}
