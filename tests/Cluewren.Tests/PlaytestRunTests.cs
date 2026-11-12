using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluewren.Bridge;
using Cluewren.Jev;
using Cluewren.Mcp;
using Cluewren.Sandbox;

namespace Cluewren.Tests;

sealed class FakeEditor : IAsyncDisposable
{
    static readonly byte[] Secret = Handshake.NewSecret();

    readonly BridgeServer _server;
    readonly FakePlugin _plugin;
    readonly CancellationTokenSource _closing = new();
    readonly Task _answering;

    volatile Task _gate = Task.CompletedTask;

    public Task Gate
    {
        set => _gate = value;
    }

    public string Project { get; }

    public BridgeLink Link { get; }

    FakeEditor(BridgeServer server, FakePlugin plugin, BridgeLink link, string project)
    {
        (_server, _plugin, Link, Project) = (server, plugin, link, project);
        _answering = AnswerAsync();
    }

    public static async Task<FakeEditor> StartAsync()
    {
        var project = Directory.CreateTempSubdirectory("cluewren-project").FullName;
        Directory.CreateDirectory(Path.Combine(project, "Library", "CluewrenCodex"));
        await File.WriteAllTextAsync(Path.Combine(project, "Library", "CluewrenCodex", "summary.txt"), "");
        var server = await BridgeServer.StartAsync(Secret);
        var plugin = await FakePlugin.ConnectAsync(server.Url, Secret);
        var link = await server.NextLinkAsync();
        return new FakeEditor(server, plugin, link, project);
    }

    async Task AnswerAsync()
    {
        var buffer = new byte[1 << 16];
        try
        {
            while (true)
            {
                var received = await _plugin.Socket.ReceiveAsync(buffer, _closing.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                    return;
                var frame = JsonNode.Parse(buffer.AsSpan(0, received.Count))!;
                if (frame["type"]?.GetValue<string>() != "request")
                    continue;
                await _gate.WaitAsync(_closing.Token);
                var op = frame["op"]!.GetValue<string>();
                var result = op == "context"
                    ? new JsonObject { ["project_dir"] = Project, ["world"] = "Main", ["character"] = "" }
                    : new JsonObject();
                await _plugin.SendAsync(new JsonObject
                {
                    ["type"] = "response", ["id"] = frame["id"]!.GetValue<long>(), ["ok"] = true, ["result"] = result, ["t"] = 1.0,
                });
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        await _answering;
        await _plugin.DisposeAsync();
        await _server.DisposeAsync();
        _closing.Dispose();
        Directory.Delete(Project, true);
    }
}

sealed class NoJev : IJevRelay
{
    public Task<(byte[] Body, long LatencyTicks)> DecideRawAsync(JsonElement stateAndQuestions, CancellationToken ct) =>
        throw new InvalidOperationException("no decisions in this test");
}

public class PlaytestRunTests
{
    static Task<PlaytestRun> OpenAsync(FakeEditor editor, RunRequest request) =>
        PlaytestRun.OpenAsync(editor.Link, request, new NoJev(), "test", SandboxMode.Auto);

    [Fact]
    public async Task Opening_a_run_writes_its_briefing_into_its_own_folder()
    {
        await using var editor = await FakeEditor.StartAsync();
        await using var run = await OpenAsync(editor, new RunRequest("Open the gate", "goal"));
        Assert.StartsWith(Path.Combine(editor.Project, "UserSettings", "Cluewren", "runs"), run.RunFolder);
        foreach (var file in (string[])["INSTRUCTIONS.md", "REQUEST.md", "KIT-PLAN.md", "KIT-FIGHT.md", "KIT-POINTER.md"])
            Assert.True(File.Exists(Path.Combine(run.RunFolder, file)), file);
        Assert.Equal("Open the gate", await File.ReadAllTextAsync(Path.Combine(run.RunFolder, "prompt.txt")));
        Assert.Contains(Path.Combine(run.RunFolder, "INSTRUCTIONS.md"), run.Opening);
        Assert.Null(run.Ended);
    }

    [Fact]
    public async Task A_run_past_its_time_limit_ends_itself_with_a_report()
    {
        await using var editor = await FakeEditor.StartAsync();
        await using var run = await OpenAsync(editor, new RunRequest("Open the gate", "goal", TimeLimitMinutes: 0.001));
        await run.WatchAsync(default).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("timed out", run.Ended);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run.RunFolder, "report.json")));
        Assert.Equal("not_achieved", report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("timed out", report.RootElement.GetProperty("stopped").GetString());
        var after = await run.CallAsync("snapshot", []);
        Assert.True(after.Error);
        Assert.Contains("has ended", after.Text);
    }

    [Fact]
    public async Task A_call_waiting_behind_the_report_finds_the_run_ended()
    {
        await using var editor = await FakeEditor.StartAsync();
        await using var run = await OpenAsync(editor, new RunRequest("Open the gate", "verify"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Gate = gate.Task;
        var busy = run.CallAsync("snapshot", []);
        var report = run.CallAsync("report", new JsonObject
        {
            ["outcome"] = "INCONCLUSIVE", ["summary"] = "the gate never showed", ["evidence"] = new JsonArray(),
        });
        var late = run.CallAsync("frames", []);
        Assert.False(busy.IsCompleted || report.IsCompleted || late.IsCompleted);
        gate.SetResult();
        await busy;
        Assert.Contains("Reported", (await report).Text);
        var answer = await late;
        Assert.True(answer.Error, answer.Text);
        Assert.Contains("has ended", answer.Text);
    }
}
