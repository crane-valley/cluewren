using System.Text.Json;
using PlaytestAgent.Kit;
using PlaytestAgent.Sandbox;

namespace PlaytestAgent.Tools;

// A brain that dies with its child still running, for the test that the job takes the child with it.
sealed class QuietLink : IGameLink
{
    public ValueTask<GameAnswer> RequestAsync(string op, Action<Utf8JsonWriter>? args = null, TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        new(new GameAnswer(JsonDocument.Parse("""{"ok":true,"result":{}}""")));

    public void Post(string op, Action<Utf8JsonWriter>? args = null)
    {
    }
}

static class Hold
{
    public static async Task<int> Run(string folder)
    {
        await using var host = new HarnessHost(new HostOptions(new QuietLink(), folder)
        {
            Mode = SandboxMode.Contained, Say = _ => { }, ProbeSeconds = 120,
        });
        var pid = Path.Combine(folder, "pid.txt").Replace("\\", "\\\\");
        await host.ProbeAsync($$"""File.WriteAllText("{{pid}}", Environment.ProcessId.ToString()); await Task.Delay(100000);""");
        return 0;
    }
}
