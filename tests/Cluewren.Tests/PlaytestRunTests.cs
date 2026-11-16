using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluewren.Bridge;
using Cluewren.Decisions;
using Cluewren.Mcp;
using Cluewren.Sandbox;

namespace Cluewren.Tests;

sealed class FakeEditor : IAsyncDisposable
{
    static readonly byte[] Secret = Handshake.NewSecret();

    readonly BridgeServer? _server;
    readonly FakePlugin _plugin;
    readonly BridgeLink? _link;
    readonly CancellationTokenSource _closing = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource> _seen = new();
    readonly Task _answering;

    volatile Task _gate = Task.CompletedTask;

    public Task Gate
    {
        set => _gate = value;
    }

    public string Project { get; }

    public BridgeLink Link => _link ?? throw new InvalidOperationException("the brain this editor connected to is not the test's");

    FakeEditor(BridgeServer? server, FakePlugin plugin, BridgeLink? link, string project)
    {
        (_server, _plugin, _link, Project) = (server, plugin, link, project);
        _answering = AnswerAsync();
    }

    public Task LeaveAsync() => _plugin.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "the editor closed", default);

    public Task Saw(string op) =>
        _seen.GetOrAdd(op, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    static void Export(string project)
    {
        Directory.CreateDirectory(PlaytestRun.ExportFolder(project));
        File.WriteAllText(Path.Combine(PlaytestRun.ExportFolder(project), "summary.txt"), "");
    }

    public static async Task<FakeEditor> StartAsync(Action<string>? prepare = null, bool exported = true)
    {
        var project = Directory.CreateTempSubdirectory("cluewren-project").FullName;
        if (exported)
            Export(project);
        prepare?.Invoke(project);
        var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(project));
        var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, project);
        var link = await server.NextLinkAsync();
        return new FakeEditor(server, plugin, link, project);
    }

    // The advert's pid is the test process's: the brain takes only an advert whose process is running.
    public static async Task<FakeEditor> InvitedAsync(string rendezvous, string project, string engine = "unity")
    {
        var id = Guid.NewGuid().ToString("N");
        Advert.Write(rendezvous, Environment.ProcessId, project, engine, id);
        var path = Rendezvous.InvitePath(rendezvous, Environment.ProcessId);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        Invite? invite;
        while ((invite = Rendezvous.ReadInvite(path)) is null || invite.EditorId != id)
        {
            Assert.True(DateTime.UtcNow < deadline, "no brain invited the editor");
            await Task.Delay(20);
        }
        var plugin = await FakePlugin.ConnectAsync(invite.Url, Convert.FromBase64String(invite.Secret), project, engine: engine);
        Export(project);
        return new FakeEditor(null, plugin, null, project);
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
                _seen.GetOrAdd(op, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
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
        _closing.Dispose();
        if (_server is null)
            return;
        await _server.DisposeAsync();
        Directory.Delete(Project, true);
    }
}

sealed class NoJev : IDecisionRelay
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
        Assert.Equal(Path.Combine(editor.Project, ".cluewren", "runs", run.RunId), run.RunFolder);
        foreach (var file in (string[])["INSTRUCTIONS.md", "REQUEST.md", "KIT-PLAN.md", "KIT-FIGHT.md", "KIT-POINTER.md"])
            Assert.True(File.Exists(Path.Combine(run.RunFolder, file)), file);
        Assert.Equal("Open the gate", await File.ReadAllTextAsync(Path.Combine(run.RunFolder, "prompt.txt")));
        Assert.Contains(Path.Combine(run.RunFolder, "INSTRUCTIONS.md"), run.Opening);
        Assert.Null(run.Ended);
    }

    [Fact]
    public async Task The_projects_home_is_one_dot_folder_that_git_ignores_whatever_the_engine()
    {
        await using var editor = await FakeEditor.StartAsync();
        var home = Path.Combine(editor.Project, ".cluewren");
        Assert.Equal((home, Path.Combine(home, "memory"), Path.Combine(home, "runs"), Path.Combine(home, "export")),
            (PlaytestRun.HomeFolder(editor.Project), PlaytestRun.MemoryFolder(editor.Project),
                PlaytestRun.RunsFolder(editor.Project), PlaytestRun.ExportFolder(editor.Project)));
        await using var run = await OpenAsync(editor, new RunRequest("Open the gate", "goal"));
        Assert.Equal([".cluewren"], Directory.GetFileSystemEntries(editor.Project).Select(Path.GetFileName));
        Assert.Equal(new[] { ".gitignore", "export", "memory", "runs" },
            Directory.GetFileSystemEntries(home).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("*\n", await File.ReadAllTextAsync(Path.Combine(home, ".gitignore")));
        Assert.True(Directory.Exists(Path.Combine(run.RunFolder, "work")));
        var instructions = await File.ReadAllTextAsync(Path.Combine(run.RunFolder, "INSTRUCTIONS.md"));
        foreach (var folder in (string[])[Path.Combine(home, "memory"), Path.Combine(home, "export"), run.RunFolder])
            Assert.Contains(folder, instructions);
    }

    [Fact]
    public async Task Opening_a_run_waits_for_the_export_summary_in_the_dot_folder()
    {
        await using var editor = await FakeEditor.StartAsync(exported: false);
        var opening = OpenAsync(editor, new RunRequest("Open the gate", "goal"));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!Directory.Exists(PlaytestRun.MemoryFolder(editor.Project)))
        {
            Assert.True(DateTime.UtcNow < deadline, "the run never made its home");
            await Task.Delay(20);
        }
        await Task.Delay(300);
        Assert.False(opening.IsCompleted);
        Directory.CreateDirectory(PlaytestRun.ExportFolder(editor.Project));
        await File.WriteAllTextAsync(Path.Combine(editor.Project, ".cluewren", "export", "summary.txt"), "");
        // Well inside the 90 s the run goes on waiting for an export that never comes.
        await using var run = await opening.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_gitignore_the_user_changed_is_left_as_it_is()
    {
        const string shared = "*\n!memory/\n";
        await using var editor = await FakeEditor.StartAsync(
            project => File.WriteAllText(Path.Combine(PlaytestRun.HomeFolder(project), ".gitignore"), shared));
        await using var run = await OpenAsync(editor, new RunRequest("Open the gate", "goal"));
        Assert.Equal(shared, await File.ReadAllTextAsync(Path.Combine(PlaytestRun.HomeFolder(editor.Project), ".gitignore")));
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
    public async Task A_finished_run_records_the_kit_it_was_played_with()
    {
        Assert.NotEqual("", PlaytestRun.KitVersion);
        Assert.DoesNotContain('+', PlaytestRun.KitVersion);
        await using var editor = await FakeEditor.StartAsync();
        await using var run = await OpenAsync(editor, new RunRequest("Open the gate", "verify"));
        var kit = Path.Combine(PlaytestRun.MemoryFolder(editor.Project), "kit.txt");
        Assert.False(File.Exists(kit));
        await run.CallAsync("report", new JsonObject
        {
            ["outcome"] = "INCONCLUSIVE", ["summary"] = "the gate never showed", ["evidence"] = new JsonArray(),
        });
        Assert.Equal("reported", run.Ended);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run.RunFolder, "report.json")));
        Assert.Equal(PlaytestRun.KitVersion, report.RootElement.GetProperty("kit").GetString());
        Assert.Equal(PlaytestRun.KitVersion, await File.ReadAllTextAsync(kit));
    }

    [Theory]
    [InlineData("0.0.1", true, false, true)]
    [InlineData("0.0.1", false, true, true)]
    [InlineData("0.0.1", false, false, false)]
    [InlineData("this", true, true, false)]
    [InlineData(null, true, true, false)]
    public async Task The_briefing_says_the_kit_changed_when_what_was_written_for_it_last_ran_on_another(string? kit, bool lib,
        bool earlier, bool said)
    {
        await using var editor = await FakeEditor.StartAsync(project =>
        {
            var memory = Directory.CreateDirectory(PlaytestRun.MemoryFolder(project)).FullName;
            if (kit is not null)
                File.WriteAllText(Path.Combine(memory, "kit.txt"), kit == "this" ? PlaytestRun.KitVersion : kit);
            if (lib)
                File.WriteAllText(Path.Combine(memory, "lib.cs"), "static class Lib { }\n");
            if (!earlier)
                return;
            var run = Directory.CreateDirectory(Path.Combine(PlaytestRun.RunsFolder(project), "20250101-000000")).FullName;
            File.WriteAllText(Path.Combine(run, "prompt.txt"), "Close the gate");
        });
        await using var run = await OpenAsync(editor, new RunRequest("Open the gate", "goal"));
        if (said)
            Assert.Contains($"## The kit changed\nlib.cs and the earlier harnesses were last used with kit {kit}; this is kit "
                + $"{PlaytestRun.KitVersion}.", run.Opening);
        else
            Assert.DoesNotContain("The kit changed", run.Opening);
        Assert.Equal(lib, run.Opening.Contains("## lib.cs (compiled with every harness)"));
        Assert.Equal(earlier, run.Opening.Contains("- runs/20250101-000000: Close the gate -> no report"));
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
