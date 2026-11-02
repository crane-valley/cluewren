using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace Cluewren.Kit;

// Events are serialized on the writer's task: a line of trace never costs the tick that logged it its time.
public sealed class Trace
{
    static readonly JsonWriterOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly Channel<(string Kind, double Wall, Dictionary<string, object?> Fields)> _events =
        Channel.CreateUnbounded<(string, double, Dictionary<string, object?>)>();

    readonly FileStream _file;
    readonly Task _writer;
    int _closed;

    public Trace(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 14);
        _writer = Task.Run(WriteLoopAsync);
    }

    public void Log(string kind, Dictionary<string, object?> fields) =>
        _events.Writer.TryWrite((kind, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, fields));

    async Task WriteLoopAsync()
    {
        var buffer = new MemoryStream(1 << 12);
        await foreach (var (kind, wall, fields) in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            buffer.SetLength(0);
            using (var json = new Utf8JsonWriter(buffer, Plain))
                WriteEvent(json, kind, wall, fields);
            buffer.WriteByte((byte)'\n');
            await _file.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)).ConfigureAwait(false);
            if (_events.Reader.Count == 0)
                await _file.FlushAsync().ConfigureAwait(false);
        }
    }

    static void WriteEvent(Utf8JsonWriter json, string kind, double wall, Dictionary<string, object?> fields)
    {
        json.WriteStartObject();
        json.WriteString("kind", kind);
        json.WriteNumber("wall", wall);
        foreach (var (name, value) in fields)
        {
            json.WritePropertyName(name);
            try
            {
                Jsonable.Write(json, value);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                json.WriteStringValue($"(unwritable: {e.GetType().Name})");
            }
        }
        json.WriteEndObject();
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _events.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        await _file.DisposeAsync().ConfigureAwait(false);
    }

    public static List<JsonElement> Read(string path)
    {
        var events = new List<JsonElement>();
        using var share = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(share, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                events.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
            }
        }
        return events;
    }
}
