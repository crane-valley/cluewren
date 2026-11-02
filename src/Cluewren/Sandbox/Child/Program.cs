using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cluewren.Kit;

namespace Cluewren.Sandbox.Child;

// Whatever harness code prints goes to the probe that is listening, else to stderr: stdout is the protocol.
sealed class ConsoleRouter(TextWriter fallback) : TextWriter
{
    readonly Lock _lock = new();
    StringBuilder? _capture;

    public override Encoding Encoding => Encoding.UTF8;

    public void Listen(StringBuilder? capture)
    {
        lock (_lock)
            _capture = capture;
    }

    public override void Write(char value)
    {
        lock (_lock)
        {
            if (_capture is not null)
                _capture.Append(value);
            else
                fallback.Write(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_lock)
        {
            if (_capture is not null)
                _capture.Append(value);
            else
                fallback.Write(value);
        }
    }
}

static class Program
{
    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);

    static int Main()
    {
        var input = Console.OpenStandardInput();
        var output = new LineWriter(Console.OpenStandardOutput());
        var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        var router = new ConsoleRouter(error);
        Console.SetOut(router);
        Console.SetError(error);
        // Windows timers tick every 15.6 ms otherwise, and a 30 Hz harness would run at 21 Hz.
        if (OperatingSystem.IsWindows())
            timeBeginPeriod(1);
        var reader = new LineReader(input);
        if (reader.Next() is not { } first)
            return 1;
        ProxyLink link;
        Runner runner;
        using (var document = JsonDocument.Parse(first.Rented.AsMemory(0, first.Length)))
        {
            var init = document.RootElement;
            var features = init.GetProperty("features").EnumerateArray().Select(f => f.GetString() ?? "").ToArray();
            link = new ProxyLink(output, features, init.GetProperty("engine").GetString() ?? "unity");
            runner = new Runner(link, output, router, init.GetProperty("run_folder").GetString()!,
                init.TryGetProperty("level", out var level) ? level.GetString() : null,
                init.GetProperty("jev").GetBoolean(), init.GetProperty("max_decisions").GetInt32());
        }
        while (reader.Next() is { } line)
        {
            var document = JsonDocument.Parse(line.Rented.AsMemory(0, line.Length));
            var root = document.RootElement;
            switch (root.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "res":
                    link.Answer(document, line.Rented);
                    continue;
                case "event":
                    link.Raise(root.GetProperty("event"));
                    break;
                case "call":
                    var call = root.Clone();
                    _ = Task.Run(() => runner.Handle(call));
                    break;
                case "stop":
                    runner.Stop();
                    break;
            }
            document.Dispose();
            System.Buffers.ArrayPool<byte>.Shared.Return(line.Rented);
        }
        link.Close("the parent closed");
        Environment.Exit(0);
        return 0;
    }
}
