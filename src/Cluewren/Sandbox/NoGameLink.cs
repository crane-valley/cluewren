using System.Text.Json;
using Cluewren.Kit;

namespace Cluewren.Sandbox;

// The link a preflight child gets: the editor's project isn't verified yet, so nothing the child sends may reach it.
sealed class NoGameLink : IGameLink
{
    const string Why = "the run hasn't started";

    volatile bool _tried;

    public bool Tried => _tried;

    public string? Closed => Why;

    public ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        _tried = true;
        throw new GameDisconnectedException(Why);
    }

    public void Post(string op, Action<Utf8JsonWriter>? args = null) => _tried = true;

    public void Send(Action<Utf8JsonWriter> frame) => _tried = true;

    public void Status(string text) => _tried = true;
}
