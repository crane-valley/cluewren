using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cluewren.Bridge;
using Cluewren.Decisions;
using Cluewren.Mcp;
using Cluewren.Sandbox;

namespace Cluewren.Tests;

public class McpServerTests
{
    [Fact]
    public async Task A_run_waits_for_an_export_being_written_but_not_for_ever()
    {
        var folder = Directory.CreateTempSubdirectory("export").FullName;
        try
        {
            var patience = TimeSpan.FromSeconds(30);
            var waiting = PlaytestRun.WaitForExportAsync(folder, patience, default);
            await Task.Delay(50);
            Assert.False(waiting.IsCompleted);
            await File.WriteAllTextAsync(Path.Combine(folder, "summary.txt"), "");
            await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(Path.Combine(folder, "summary.txt"));
            await PlaytestRun.WaitForExportAsync(folder, TimeSpan.Zero, default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    static async Task<List<JsonObject>> Talk(params string[] lines)
    {
        var output = new StringWriter();
        await using var server = new McpServer(output) { DecisionKey = "test-key" };
        await server.ServeAsync(new StringReader(string.Join('\n', lines)));
        for (var i = 0; i < 300 && output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length < lines.Length; i++)
            await Task.Delay(10);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
    }

    [Fact]
    public async Task Lists_the_adrs_tools_and_answers_on_stdout_alone()
    {
        var answers = await Talk(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"claude-code"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var byId = answers.ToDictionary(answer => answer["id"]!.GetValue<int>());
        Assert.Equal("2025-06-18", byId[1]["result"]!["protocolVersion"]!.GetValue<string>());
        var tools = byId[2]["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["start_run", "wait", "stop", "probe", "use_harness", "check_harness", "run_harness", "snapshot", "restart_level", "frames", "report"], tools);
    }

    [Fact]
    public async Task A_game_tool_before_start_run_says_so()
    {
        var output = new StringWriter();
        await using var server = new McpServer(output);
        var result = await server.CallAsync("probe", new JsonObject { ["code"] = "1" });
        Assert.True(result.Error);
        Assert.Contains("start_run", result.Text);
    }

    [Fact]
    public async Task A_call_that_reaches_a_stopped_server_is_told_so()
    {
        var server = new McpServer(new StringWriter());
        await server.DisposeAsync();
        var result = await server.CallAsync("probe", new JsonObject { ["code"] = "1" });
        Assert.True(result.Error);
        Assert.Contains("the server is stopping", result.Text);
    }

    [Fact]
    public async Task Wait_with_nothing_going_is_no_error()
    {
        await using var server = new McpServer(new StringWriter());
        var result = await server.CallAsync("wait", []);
        Assert.False(result.Error);
    }

    [Fact]
    public async Task Stop_with_nothing_going_is_no_error()
    {
        await using var server = new McpServer(new StringWriter());
        var result = await server.CallAsync("stop", []);
        Assert.False(result.Error);
        Assert.Equal("Nothing is going: no call is there to stop.", result.Text);
    }

    [Fact]
    public async Task A_cancellation_with_no_call_in_progress_is_ignored()
    {
        var answers = await Talk(
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1,"reason":"gave up"}}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled"}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.Equal(2, Assert.Single(answers)["id"]!.GetValue<int>());
    }

    [Fact]
    public void The_text_for_no_editor_gives_the_reason_and_the_project_the_run_takes()
    {
        var folder = Directory.CreateTempSubdirectory("cluewren-project").FullName;
        try
        {
            using (var related = ProjectScope.Related(folder))
                Assert.Equal($"The run didn't start: No editor with the Cluewren plugin is open. This run takes an editor whose "
                    + $"project is {folder}, a folder inside it or one that contains it (start_run's project names one exactly). "
                    + "Tell the user to open the game's project in its editor with the Cluewren plugin, then call start_run again.",
                    McpServer.NoEditorText(related, "No editor with the Cluewren plugin is open"));
            using var exact = ProjectScope.ExactlyAt(folder);
            Assert.StartsWith($"The run didn't start: Pass `project`. This run takes an editor whose project is {folder} exactly. Tell ",
                McpServer.NoEditorText(exact, "Pass `project`."));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task An_unknown_method_is_refused_by_number()
    {
        var answers = await Talk("""{"jsonrpc":"2.0","id":7,"method":"nope"}""");
        Assert.Equal(-32601, answers[0]["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("requird", false)]
    [InlineData("off", false)]
    [InlineData("required", true)]
    [InlineData("Auto", true)]
    public async Task The_brain_starts_only_with_a_sandbox_setting_it_knows(string setting, bool starts)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Cluewren.exe"))
        {
            ArgumentList = { "mcp" }, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["CLUEWREN_SANDBOX"] = setting;
        using var brain = System.Diagnostics.Process.Start(start)!;
        var error = brain.StandardError.ReadToEndAsync();
        _ = brain.StandardOutput.ReadToEndAsync();
        try
        {
            if (starts)
            {
                await Task.Delay(2000);
                Assert.False(brain.HasExited, brain.HasExited ? await error : "");
                brain.StandardInput.Close();
            }
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await brain.WaitForExitAsync(limit.Token);
            Assert.Equal(starts ? 0 : 2, brain.ExitCode);
            if (!starts)
                Assert.Contains("CLUEWREN_SANDBOX", await error);
        }
        finally
        {
            if (!brain.HasExited)
                brain.Kill();
        }
    }

    [Theory]
    [InlineData("--version", 0, null)]
    [InlineData("--help", 0, null)]
    [InlineData("-h", 0, null)]
    [InlineData("", 2, null)]
    [InlineData("serve", 2, null)]
    [InlineData("connect", 2, null)]
    [InlineData("run", 2, null)]
    [InlineData("run|folder", 2, "nope")]
    public async Task The_command_line_answers_what_it_knows_and_refuses_the_rest(string line, int code, string? sandbox)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Cluewren.exe"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in line.Split('|', StringSplitOptions.RemoveEmptyEntries))
            start.ArgumentList.Add(arg);
        if (sandbox is not null)
            start.Environment["CLUEWREN_SANDBOX"] = sandbox;
        using var cli = System.Diagnostics.Process.Start(start)!;
        cli.StandardInput.Close();
        var (output, error) = (cli.StandardOutput.ReadToEndAsync(), cli.StandardError.ReadToEndAsync());
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await cli.WaitForExitAsync(limit.Token);
            Assert.Equal(code, cli.ExitCode);
            if (line == "--version")
                Assert.Equal($"cluewren {McpServer.Version}", (await output).Trim());
            else if (code == 0)
                Assert.Contains("usage:", await output);
            else
                Assert.Contains(sandbox is null ? "usage:" : "CLUEWREN_SANDBOX", await error);
        }
        finally
        {
            if (!cli.HasExited)
                cli.Kill();
        }
    }

    [Fact]
    public async Task Start_run_with_no_key_asks_for_it_without_opening_a_port()
    {
        await using var server = new McpServer(new StringWriter()) { DecisionKey = "" };
        var result = await server.CallAsync("start_run", new JsonObject { ["request"] = "x", ["kind"] = "goal" });
        Assert.True(result.Error);
        Assert.Contains("TYPESAFE_API_KEY", result.Text);
        Assert.DoesNotContain("test-key", result.Text);
    }
}

public class CompileMessageTests
{
    [Fact]
    public void Problems_reach_the_agent_in_english_whatever_this_machine_speaks()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ja-JP");
            var compiled = new Cluewren.Sandbox.HarnessCompiler(folder).CompileProbe("int x = ;", null);
            Assert.False(compiled.Ok);
            Assert.All(compiled.ProblemText, c => Assert.True(c < 128, compiled.ProblemText));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = culture;
            Directory.Delete(folder, true);
        }
    }
}

public class WordHitsTests
{
    [Fact]
    public void Ranks_files_by_the_requests_words_and_names_what_is_missing()
    {
        var project = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(project, "Assets", "Scripts")).FullName;
            File.WriteAllText(Path.Combine(scripts, "Jetpack.cs"), "class Jetpack { float ascend; float jetpack; }");
            File.WriteAllText(Path.Combine(scripts, "Other.cs"), "class Other { }");
            var hits = PlaytestRun.WordHits(project, "unity", "Verify that the jetpack ascends over the zeppelin")!;
            Assert.StartsWith("- Assets/Scripts/Jetpack.cs", hits[0]);
            Assert.DoesNotContain(hits, line => line.Contains("Other.cs"));
            Assert.Contains(hits, line => line.Contains("not in the project") && line.Contains("zeppelin"));
        }
        finally
        {
            Directory.Delete(project, true);
        }
    }

    static string[] Searched(string project, string engine) =>
        [.. PlaytestRun.SearchedFiles(project, engine).Select(file => Path.GetRelativePath(project, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void Each_engine_searches_its_own_text_and_never_the_runs_and_memory_in_the_dot_folder()
    {
        var project = Directory.CreateTempSubdirectory().FullName;
        try
        {
            foreach (var file in (string[])
                     [
                         "Assets/Player.cs", "Source/Game/Door.cpp", "door.gd", "levels/main.tscn", "addons/cluewren/plugin.gd",
                         ".godot/cache.gd", ".cluewren/export/summary.txt", ".cluewren/export/scenes/Main.txt",
                         ".cluewren/export/blueprints/BP_Door.txt", ".cluewren/runs/20250101-000000/harness.cs",
                         ".cluewren/memory/lib.cs", ".cluewren/memory/kit.txt",
                     ])
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(project, file))!);
                File.WriteAllText(Path.Combine(project, file), "door");
            }
            Assert.Equal([".cluewren/export/scenes/Main.txt", "Assets/Player.cs"], Searched(project, "unity"));
            Assert.Equal([".cluewren/export/blueprints/BP_Door.txt", ".cluewren/export/scenes/Main.txt", ".cluewren/export/summary.txt",
                "Source/Game/Door.cpp"], Searched(project, "unreal"));
            Assert.Equal(["Assets/Player.cs", "door.gd", "levels/main.tscn"], Searched(project, "godot"));
            Assert.Contains(PlaytestRun.WordHits(project, "godot", "Open the door")!, line => line.StartsWith("- door.gd (door: 1)"));
        }
        finally
        {
            Directory.Delete(project, true);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("now")]
    [InlineData("empty")]
    [InlineData("gone")]
    [InlineData(null)]
    public void Only_another_runs_own_harness_can_be_copied(string? run)
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var runs = Path.Combine(home, "runs");
            File.WriteAllText(Path.Combine(home, "harness.cs"), "outside");
            foreach (var name in (string[])["before", "now", "empty"])
                Directory.CreateDirectory(Path.Combine(runs, name));
            File.WriteAllText(Path.Combine(runs, "before", "harness.cs"), "class H;");
            File.WriteAllText(Path.Combine(runs, "now", "harness.cs"), "class H;");
            Assert.Null(PlaytestRun.EarlierHarness(runs, "now", run));
            Assert.Equal(Path.Combine(runs, "before", "harness.cs"), PlaytestRun.EarlierHarness(runs, "now", "runs/before/"));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }
}

public class ConnectTests
{
    static readonly string[] Command = ["C:/tools/cluewren.exe", "mcp"];

    [Fact]
    public void Codex_config_keeps_everything_else_and_removal_restores_it()
    {
        const string mine = "model = \"gpt\"\n\n[mcp_servers.other]\ncommand = \"x\"\n";
        var added = Connect.CodexConfig(mine, Command);
        Assert.Contains("[mcp_servers.\"cluewren\"]", added);
        Assert.Contains("[mcp_servers.other]", added);
        Assert.Equal(mine, Connect.CodexConfig(added, null));
    }

    [Fact]
    public void Codex_config_stays_the_same_when_connected_twice()
    {
        var once = Connect.CodexConfig("", Command);
        Assert.Equal(once, Connect.CodexConfig(once, Command));
    }

    [Fact]
    public void A_table_after_ours_survives_removal()
    {
        var added = Connect.CodexConfig("", Command) + "\n[profiles.a]\nmodel = \"b\"\n";
        var removed = Connect.CodexConfig(added, null);
        Assert.Contains("[profiles.a]", removed);
        Assert.DoesNotContain("cluewren", removed);
    }

    [Fact]
    public void A_server_set_up_by_a_dotted_key_is_left_to_the_user()
    {
        Assert.Throws<FormatException>(() => Connect.CodexConfig("mcp_servers.cluewren.command = \"x\"\n", Command));
    }

    [Fact]
    public void The_describer_is_never_told_the_request_and_has_only_the_frames_tool()
    {
        var text = Connect.ClaudeDescriberText();
        Assert.Contains("tools: mcp__cluewren__frames\n", text);
        Assert.Contains("Never judge", text);
    }

    [Fact]
    public void Connect_claude_sets_up_its_own_server_and_files_and_leaves_the_users_alone()
    {
        var home = Directory.CreateTempSubdirectory("claude-home").FullName;
        var was = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", home);
            var skill = Path.Combine(home, "skills", "cluewren", "SKILL.md");
            var describer = Path.Combine(home, "agents", "frame-describer.md");
            var commands = new List<string[]>();
            (string Command, string Args)? server = null;
            var connect = new Connect(_ => { }, command =>
            {
                commands.Add(command);
                if (command[2] == "get")
                    return server is not { } shown ? (1, "No MCP server named \"cluewren\". Run `claude mcp add` to add one.\n")
                        : (0, "cluewren:\n  Scope: User config (available in all your projects)\n  Status: \u2714 Connected\n  Type: stdio\n"
                            + $"  Command: {shown.Command}\n  Args: {shown.Args}\n  Environment:\n\n"
                            + "To remove this server, run: claude mcp remove cluewren -s user\n");
                if (command[2] == "remove")
                    server = null;
                return (0, "");
            });

            Assert.Equal(0, connect.Add("claude"));
            Assert.Equal((string[])["claude", "mcp", "add", "--scope", "user", "cluewren", "--", .. Connect.ServerCommand()], commands[^1]);
            Assert.Contains("cluewren connect", File.ReadAllText(skill));
            Assert.Contains("cluewren connect", File.ReadAllText(describer));

            server = ("C:/tools/cluewren.exe", "mcp");
            Assert.Equal(0, connect.Remove("claude"));
            Assert.Null(server);
            Assert.False(File.Exists(skill) || File.Exists(describer));

            server = ("npx", "someone-elses-server");
            Directory.CreateDirectory(Path.GetDirectoryName(describer)!);
            File.WriteAllText(describer, "mine");
            commands.Clear();
            Assert.Equal(3, connect.Add("claude"));
            Assert.DoesNotContain(commands, command => command[2] is "add" or "remove");
            Assert.Equal("mine", File.ReadAllText(describer));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", was);
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void Files_carry_the_mark_that_lets_connect_replace_them()
    {
        Assert.Contains("cluewren connect", Connect.SkillText());
        Assert.Contains("cluewren connect", Connect.ClaudeDescriberText());
        Assert.Contains("cluewren connect", Connect.CodexDescriberText());
    }
}

public class FrameRecorderTests
{
    static FrameRecorder Recorder(long? keptBytes = null) =>
        new(new Bridge.BridgeLink("test", JsonDocument.Parse("""{"features":["media"]}""").RootElement), keptBytes);

    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    static void Feed(FrameRecorder recorder, double t, int bytes = 8)
    {
        using var frame = JsonDocument.Parse(JsonSerializer.Serialize(new { t, wall = Now, jpeg = Convert.ToBase64String(new byte[bytes]) }));
        recorder.Keep(frame.RootElement);
    }

    [Fact]
    public async Task Spreads_the_count_over_the_episode()
    {
        await using var recorder = Recorder();
        recorder.Begin(1);
        for (var t = 0; t < 100; t++)
            Feed(recorder, t);
        recorder.End(1);
        var picked = recorder.Pick(1, null, null, 4);
        Assert.Equal(4, picked.Jpegs.Count);
        Assert.Contains("at game times 0.00, 33.00, 66.00, 99.00 s", picked.Text);
    }

    [Fact]
    public async Task Narrows_to_game_times_and_says_when_none_show_them()
    {
        await using var recorder = Recorder();
        recorder.Begin(1);
        for (var t = 0; t < 10; t++)
            Feed(recorder, t);
        recorder.End(1);
        Assert.Contains("at game times 2.00, 3.00 s", recorder.Pick(1, 2, 3, 12).Text);
        var none = recorder.Pick(1, 50, 60, 4);
        Assert.Empty(none.Jpegs);
        Assert.Contains("0.00 to 9.00", none.Text);
    }

    [Fact]
    public async Task Never_gives_more_than_the_most_a_describer_takes()
    {
        await using var recorder = Recorder();
        recorder.Begin(1);
        for (var t = 0; t < 50; t++)
            Feed(recorder, t);
        recorder.End(1);
        Assert.Equal(FrameRecorder.MaxFrames, recorder.Pick(1, null, null, 500).Jpegs.Count);
    }

    [Fact]
    public async Task Forgets_the_oldest_past_its_memory_and_says_so()
    {
        await using var recorder = Recorder(keptBytes: 64);
        recorder.Begin(1);
        Feed(recorder, 0, 64);
        recorder.End(1);
        await Task.Delay(1100);
        recorder.Begin(2);
        Feed(recorder, 5, 64);
        Feed(recorder, 6, 64);
        recorder.End(2);
        await Task.Delay(600);
        Assert.Contains("aren't kept any more", recorder.Pick(1, null, null, 4).Text);
        Assert.NotEmpty(recorder.Pick(2, null, null, 4).Jpegs);
    }

    // Before, each begin moved the episode's start, so a harness that marked its beginning again put what was filmed
    // before out of reach.
    [Fact]
    public async Task A_second_begin_of_an_episode_keeps_its_start()
    {
        await using var recorder = Recorder();
        recorder.Begin(1);
        Feed(recorder, 0);
        await Task.Delay(1100);
        recorder.Begin(1);
        Feed(recorder, 5);
        recorder.End(1);
        Assert.Contains("at game times 0.00, 5.00 s", recorder.Pick(1, null, null, 4).Text);
    }

    // Before, an episode that was numbered and then not played left its beginning behind: the next one got its number,
    // and with it what was filmed since the first try.
    [Fact]
    public async Task An_episode_that_was_not_played_leaves_nothing_to_the_one_that_gets_its_number()
    {
        await using var recorder = Recorder();
        recorder.Begin(1);
        Feed(recorder, 0);
        recorder.Forget(1);
        Assert.Contains("No episode 1", recorder.Pick(1, null, null, null).Text);
        await Task.Delay(1100);
        recorder.Begin(1);
        Feed(recorder, 5);
        recorder.End(1);
        Assert.Contains("at game times 5.00 s", recorder.Pick(1, null, null, 4).Text);
    }

    [Fact]
    public async Task An_episode_that_was_never_played_is_named()
    {
        await using var recorder = Recorder();
        Assert.Contains("No episode 3", recorder.Pick(3, null, null, null).Text);
        Assert.Contains("run_harness first", recorder.Pick(0, null, null, null).Text);
    }

    [Fact]
    public async Task Game_times_read_the_same_whatever_this_machine_speaks()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            await using var recorder = Recorder();
            recorder.Begin(1);
            for (var t = 0; t < 10; t++)
                Feed(recorder, t + 0.5);
            recorder.End(1);
            Assert.Contains("at game times 0.50, 9.50 s", recorder.Pick(1, null, null, 2).Text);
            Assert.Contains("show game times 50.5 to 60.5 (its frames show game times 0.50 to 9.50 s)", recorder.Pick(1, 50.5, 60.5, 4).Text);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }
}

sealed class LineInput : TextReader
{
    readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public void Send(string line) => _lines.Writer.TryWrite(line);

    public void End() => _lines.Writer.TryComplete();

    public override async Task<string?> ReadLineAsync() =>
        await _lines.Reader.WaitToReadAsync() && _lines.Reader.TryRead(out var line) ? line : null;
}

sealed class LineOutput : TextWriter
{
    readonly Channel<JsonObject> _written = Channel.CreateUnbounded<JsonObject>();
    readonly Dictionary<int, JsonObject> _answers = [];

    public override Encoding Encoding => Encoding.UTF8;

    public override Task WriteLineAsync(string? value)
    {
        _written.Writer.TryWrite(JsonNode.Parse(value!)!.AsObject());
        return Task.CompletedTask;
    }

    public bool Answered(int id)
    {
        while (_written.Reader.TryRead(out var answer))
            _answers[answer["id"]!.GetValue<int>()] = answer;
        return _answers.ContainsKey(id);
    }

    public async Task<JsonObject> Answer(int id)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        while (!Answered(id))
            await _written.Reader.WaitToReadAsync(patience.Token);
        return _answers[id];
    }
}

public sealed class McpRunTests : IDisposable
{
    static readonly int Me = Environment.ProcessId;
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    // The child unpauses the game beside the probe's first request, and a stop that finds that still unanswered ends the
    // probe without its output. By the time the last of these round trips reaches the editor it is long answered.
    const string Endless = """
        Console.WriteLine("before the stop");
        for (var trip = 0; trip < 3; trip++)
            await game.Request("ping");
        await game.Request("find");
        await Task.Delay(System.Threading.Timeout.Infinite);
        """;

    readonly string _rendezvous = Path.Combine(Path.GetTempPath(), "cluewren-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _project = Directory.CreateTempSubdirectory("cluewren-project").FullName;
    readonly string? _baseUrl = Environment.GetEnvironmentVariable(ApiKeys.TypeSafeBaseUrl);

    // A run that starts keeps a connection to the decision model warm: here that is a closed port on this machine.
    public McpRunTests() => Environment.SetEnvironmentVariable(ApiKeys.TypeSafeBaseUrl, "https://127.0.0.1:1");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ApiKeys.TypeSafeBaseUrl, _baseUrl);
        foreach (var folder in (string[])[_rendezvous, _project])
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    McpServer Server(TextWriter? output = null, double connectSeconds = 60, double waitSeconds = 300) =>
        new(output ?? new StringWriter(), waitSeconds, connectSeconds)
        {
            DecisionKey = "test-key", RendezvousFolder = _rendezvous, WorkingFolder = _project, Sandbox = SandboxMode.Auto,
        };

    static JsonObject Request => new() { ["request"] = "Open the gate", ["kind"] = "goal" };

    string InvitePath => Rendezvous.InvitePath(_rendezvous, Me);

    [Fact]
    public async Task Start_run_invites_the_editor_that_adverts_the_project_and_the_run_lasts_until_that_editor_closes()
    {
        var server = Server();
        try
        {
            var starting = server.CallAsync("start_run", Request);
            await using var editor = await FakeEditor.InvitedAsync(_rendezvous, _project);
            var started = await starting;
            Assert.False(started.Error, started.Text);
            Assert.Contains($"started on {_project}", started.Text);

            var invite = Rendezvous.ReadInvite(InvitePath)!;
            Assert.Equal((Me, BridgeServer.Protocol), (invite.Pid, invite.Protocol));
            Assert.True(Rendezvous.Alive(invite));
            Assert.Equal(Rendezvous.Editors(_rendezvous).Single().Id, invite.EditorId);

            var home = PlaytestRun.HomeFolder(_project);
            Assert.Equal([".cluewren"], Directory.GetFileSystemEntries(_project).Select(Path.GetFileName));
            Assert.Equal(new[] { ".gitignore", "export", "memory", "runs" },
                Directory.GetFileSystemEntries(home).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal("*\n", await File.ReadAllTextAsync(Path.Combine(home, ".gitignore")));
            var run = Assert.Single(Directory.GetDirectories(PlaytestRun.RunsFolder(_project)));
            Assert.Contains(Path.Combine(run, "INSTRUCTIONS.md"), started.Text);
            Assert.Equal(new[] { "INSTRUCTIONS.md", "KIT-FIGHT.md", "KIT-PLAN.md", "KIT-POINTER.md", "REQUEST.md", "prompt.txt", "work" },
                Directory.GetFileSystemEntries(run).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            var again = await server.CallAsync("start_run", Request);
            Assert.True(again.Error);
            Assert.Contains("is going: finish it with report first", again.Text);

            await editor.LeaveAsync();
            var deadline = DateTime.UtcNow + Patience;
            while (File.Exists(InvitePath))
            {
                Assert.True(DateTime.UtcNow < deadline, "the invite outlived the run");
                await Task.Delay(20);
            }
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "report.json")));
            Assert.StartsWith("the game went away", report.RootElement.GetProperty("stopped").GetString());
            Assert.Equal(PlaytestRun.KitVersion, report.RootElement.GetProperty("kit").GetString());
            Assert.Equal(PlaytestRun.KitVersion, await File.ReadAllTextAsync(Path.Combine(home, "memory", "kit.txt")));
            var after = await server.CallAsync("snapshot", []);
            Assert.True(after.Error);
            Assert.Contains("has ended (the game went away", after.Text);
            Assert.Contains(Path.Combine(run, "report.json"), after.Text);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Closing_the_server_withdraws_the_invite_of_a_run_still_going()
    {
        var server = Server();
        FakeEditor? editor = null;
        try
        {
            var starting = server.CallAsync("start_run", Request);
            editor = await FakeEditor.InvitedAsync(_rendezvous, _project);
            Assert.False((await starting).Error);
            Assert.True(File.Exists(InvitePath));
        }
        finally
        {
            await server.DisposeAsync();
            if (editor is not null)
                await editor.DisposeAsync();
        }
        Assert.False(File.Exists(InvitePath));
        Assert.Empty(Directory.GetFiles(Rendezvous.InvitesFolder(_rendezvous)));
    }

    [Fact]
    public async Task Start_run_with_no_editor_of_the_project_says_what_is_open_and_invites_no_one()
    {
        await using var server = Server(connectSeconds: 1);
        var none = await server.CallAsync("start_run", Request);
        Assert.True(none.Error);
        Assert.StartsWith("The run didn't start: No editor with the Cluewren plugin is open. This run takes an editor whose project is "
            + $"{_project}, a folder inside it or one that contains it", none.Text);

        var other = Directory.CreateTempSubdirectory("cluewren-other").FullName;
        try
        {
            Advert.Write(_rendezvous, Me, other, "godot");
            var named = await server.CallAsync("start_run", Request);
            Assert.True(named.Error);
            Assert.StartsWith($"The run didn't start: No open editor has that project. Open: godot {other}. This run takes", named.Text);
            Assert.EndsWith("Tell the user to open the game's project in its editor with the Cluewren plugin, then call start_run again.",
                named.Text);

            var exact = Request;
            exact["project"] = _project;
            Assert.Contains($"whose project is {_project} exactly. Tell", (await server.CallAsync("start_run", exact)).Text);
        }
        finally
        {
            Directory.Delete(other, true);
        }
        Assert.False(Directory.Exists(Rendezvous.InvitesFolder(_rendezvous)));
        Assert.True(File.Exists(Path.Combine(Rendezvous.EditorsFolder(_rendezvous), $"{Me}.json")));
        Assert.Empty(Directory.GetFileSystemEntries(_project));
    }

    [Fact]
    public async Task Start_run_does_not_wait_for_an_editor_whose_plugin_speaks_another_protocol()
    {
        Advert.Write(_rendezvous, Me, _project, min: 1, max: 1, pluginVersion: "0.1.0");
        await using var server = Server(connectSeconds: 600);
        var result = await server.CallAsync("start_run", Request).WaitAsync(Patience);
        Assert.True(result.Error);
        Assert.Contains($"(0.1.0) speaks protocol 1-1 and this cluewren speaks {BridgeServer.Protocol}", result.Text);
        Assert.False(Directory.Exists(Rendezvous.InvitesFolder(_rendezvous)));
    }

    [Fact]
    public async Task An_invited_editor_that_does_not_connect_is_named_and_its_invite_withdrawn()
    {
        Advert.Write(_rendezvous, Me, _project, "unreal");
        await using var server = Server(connectSeconds: 1);
        var result = await server.CallAsync("start_run", Request);
        Assert.True(result.Error);
        Assert.StartsWith($"The editor of {_project} (unreal) was invited and didn't connect within 1 s. Its Cluewren window shows why.",
            result.Text);
        Assert.Empty(Directory.GetFiles(Rendezvous.InvitesFolder(_rendezvous)));
    }

    [Fact]
    public async Task Stop_ends_the_call_in_progress_and_its_result_is_what_it_gave_until_then()
    {
        await using var server = Server();
        var starting = server.CallAsync("start_run", Request);
        await using var editor = await FakeEditor.InvitedAsync(_rendezvous, _project);
        Assert.False((await starting).Error);
        Assert.Equal("Nothing is going: no call is there to stop.", (await server.CallAsync("stop", [])).Text);

        var probing = server.CallAsync("probe", new JsonObject { ["code"] = Endless });
        await editor.Saw("find").WaitAsync(Patience);
        Assert.False(probing.IsCompleted);
        var stopping = await server.CallAsync("stop", []);
        Assert.False(stopping.Error);
        Assert.Equal("Stopping probe: call wait for what it gave until then.", stopping.Text);
        // Inside the 120 s a probe may take: a stop that did nothing would leave it going until then.
        var probed = await probing.WaitAsync(Patience);
        Assert.Contains("before the stop", probed.Text);
        Assert.DoesNotContain("sandbox failed", probed.Text);
        Assert.Equal("Nothing is going: no call is there to stop.", (await server.CallAsync("stop", [])).Text);
    }

    // Parsed, as a client's arguments are: a number set from an int is not read as the double a run takes it as.
    static JsonObject Episode(int n) => JsonNode.Parse($$"""{"episode":{{n}}}""")!.AsObject();

    static string BeforeTheClock(ToolResult result) => result.Text.Split("\n[time:")[0];

    [Fact]
    public async Task A_server_without_a_run_answers_frames_from_the_run_another_server_has_in_the_same_folder()
    {
        var first = Server();
        var closed = false;
        await using var second = Server();
        var elsewhere = Directory.CreateTempSubdirectory("cluewren-other").FullName;
        try
        {
            await using var third = new McpServer(new StringWriter()) { WorkingFolder = elsewhere };
            var starting = first.CallAsync("start_run", Request);
            await using var editor = await FakeEditor.InvitedAsync(_rendezvous, _project);
            Assert.False((await starting).Error);

            var own = await first.CallAsync("frames", Episode(3));
            Assert.StartsWith("No episode 3 has been played in this run.", own.Text);
            var relayed = await second.CallAsync("frames", Episode(3));
            Assert.Equal((BeforeTheClock(own), own.Error), (BeforeTheClock(relayed), relayed.Error));
            Assert.Contains("\n[time:", relayed.Text);

            var other = await second.CallAsync("snapshot", []);
            Assert.True(other.Error);
            Assert.Equal("No run has started: call start_run first.", other.Text);
            var crossed = await third.CallAsync("frames", Episode(3));
            Assert.True(crossed.Error);
            Assert.Equal("No run has started: call start_run first.", crossed.Text);

            closed = true;
            await first.DisposeAsync();
            var after = await second.CallAsync("frames", Episode(3));
            Assert.True(after.Error);
            Assert.Equal("No run has started: call start_run first.", after.Text);
        }
        finally
        {
            if (!closed)
                await first.DisposeAsync();
            Directory.Delete(elsewhere, true);
        }
    }

    static string Call(int id, string tool, JsonObject args) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
    }.ToJsonString();

    static string Cancelled(int id) => new JsonObject
    {
        ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = id },
    }.ToJsonString();

    static string Text(JsonObject answer) => answer["result"]!["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public async Task A_client_that_cancels_the_request_of_the_call_in_progress_stops_it()
    {
        var (input, output) = (new LineInput(), new LineOutput());
        await using var server = Server(output);
        var serving = server.ServeAsync(input);
        input.Send(Call(1, "start_run", Request));
        await using var editor = await FakeEditor.InvitedAsync(_rendezvous, _project);
        var started = await output.Answer(1);
        Assert.False(started["result"]!["isError"]!.GetValue<bool>(), Text(started));

        input.Send(Call(2, "probe", new JsonObject { ["code"] = Endless }));
        await editor.Saw("find").WaitAsync(Patience);
        input.Send(Cancelled(7));
        input.Send("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        await output.Answer(3);
        Assert.False(output.Answered(2), "the cancellation of another request stopped the call");

        input.Send(Cancelled(2));
        var probed = await output.Answer(2);
        Assert.Contains("before the stop", Text(probed));
        Assert.DoesNotContain("sandbox failed", Text(probed));
        input.End();
        await serving.WaitAsync(Patience);
    }

    // Before, the request a cancellation had to name stayed the call that began the play, which was already answered:
    // the wait a client gave up on left the play going until the probe's own limit.
    [Fact]
    public async Task A_client_that_cancels_its_wait_for_the_call_in_progress_stops_it()
    {
        var (input, output) = (new LineInput(), new LineOutput());
        await using var server = Server(output, waitSeconds: 10);
        var starting = server.CallAsync("start_run", Request);
        await using var editor = await FakeEditor.InvitedAsync(_rendezvous, _project);
        var started = await starting;
        while (started.Text.StartsWith("start_run is still going", StringComparison.Ordinal))
            started = await server.CallAsync("wait", []);
        Assert.False(started.Error, started.Text);
        var serving = server.ServeAsync(input);

        input.Send(Call(2, "probe", new JsonObject { ["code"] = Endless }));
        await editor.Saw("find").WaitAsync(Patience);
        Assert.StartsWith("probe is still going", Text(await output.Answer(2)));
        input.Send(Call(3, "wait", []));
        input.Send(Cancelled(3));
        var waited = Text(await output.Answer(3));
        for (var id = 4; waited.StartsWith("probe is still going", StringComparison.Ordinal); id++)
        {
            input.Send(Call(id, "wait", []));
            waited = Text(await output.Answer(id));
        }
        Assert.Contains("before the stop", waited);
        Assert.Contains("THE PROBE WAS STOPPED HERE", waited);
        input.End();
        await serving.WaitAsync(Patience);
    }

    // Before, a stop or a cancellation taken while a call that does not play was going was kept for the next call that
    // does, which then ended before it began.
    [Fact]
    public async Task A_stop_while_a_call_that_does_not_play_is_going_is_refused_and_the_next_probe_plays()
    {
        await using var server = Server();
        var starting = server.CallAsync("start_run", Request);
        Assert.Equal("start_run is still going and can't be stopped: call wait.", (await server.CallAsync("stop", [])).Text);
        await using var editor = await FakeEditor.InvitedAsync(_rendezvous, _project);
        Assert.False((await starting).Error);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Gate = gate.Task;
        var looking = server.CallAsync("snapshot", []);
        Assert.Equal("snapshot is still going and can't be stopped: call wait.", (await server.CallAsync("stop", [])).Text);
        gate.SetResult();
        await looking.WaitAsync(Patience);

        var probed = await server.CallAsync("probe", new JsonObject { ["code"] = "\"played\"" }).WaitAsync(Patience);
        Assert.Contains("played", probed.Text);
        Assert.DoesNotContain("Stopped before it began.", probed.Text);
    }

    [Fact]
    public async Task A_cancellation_of_a_call_that_does_not_play_leaves_the_next_probe_to_play()
    {
        var (input, output) = (new LineInput(), new LineOutput());
        await using var server = Server(output);
        var serving = server.ServeAsync(input);
        input.Send(Call(1, "start_run", Request));
        await using var editor = await FakeEditor.InvitedAsync(_rendezvous, _project);
        Assert.False((await output.Answer(1))["result"]!["isError"]!.GetValue<bool>());

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Gate = gate.Task;
        input.Send(Call(2, "snapshot", []));
        input.Send(Cancelled(2));
        input.Send("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        await output.Answer(3);
        gate.SetResult();
        await output.Answer(2);

        input.Send(Call(4, "probe", new JsonObject { ["code"] = "\"played\"" }));
        var probed = Text(await output.Answer(4));
        Assert.Contains("played", probed);
        Assert.DoesNotContain("Stopped before it began.", probed);
        input.End();
        await serving.WaitAsync(Patience);
    }
}

public class FramesRelayTests
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    static string Folder() => Path.Combine(Path.GetTempPath(), "cluewren-relay-" + Guid.NewGuid().ToString("N"));

    static Task<ToolResult?> Ask(string folder, JsonObject args) => FramesRelay.AskAsync(folder, args, default).WaitAsync(Patience);

    [Fact]
    public async Task What_serves_a_folder_answers_its_frames_calls_with_the_text_and_every_image()
    {
        var folder = Folder();
        var large = new byte[3_000_000];
        Random.Shared.NextBytes(large);
        byte[][] jpegs = [[0xFF, 0xD8, 0xFF], large, []];
        var asked = new List<string>();
        await using var relay = FramesRelay.Serve(folder, args =>
        {
            asked.Add(args.ToJsonString());
            return Task.FromResult(asked.Count == 1
                ? new ToolResult("3 frames of episode 2", jpegs)
                : ToolResult.Say("No episode 7: the latest is 2.", true));
        });

        var frames = await Ask(folder, new JsonObject { ["episode"] = 2, ["start"] = 1.5 });
        Assert.Equal(("3 frames of episode 2", false), (frames!.Text, frames.Error));
        Assert.Equal(jpegs, frames.Images);

        var none = await Ask(folder.ToUpperInvariant() + Path.DirectorySeparatorChar, new JsonObject { ["episode"] = 7 });
        Assert.Equal(("No episode 7: the latest is 2.", true), (none!.Text, none.Error));
        Assert.Empty(none.Images);
        Assert.Equal(["""{"episode":2,"start":1.5}""", """{"episode":7}"""], asked);
    }

    [Fact]
    public async Task A_call_that_fails_where_it_is_served_gets_no_answer_and_the_next_one_is_served()
    {
        var folder = Folder();
        var calls = 0;
        await using var relay = FramesRelay.Serve(folder, _ => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("the run is closing")
            : Task.FromResult(ToolResult.Say("served")));
        Assert.Null(await Ask(folder, []));
        Assert.Equal("served", (await Ask(folder, []))!.Text);
    }

    [Fact]
    public async Task Nothing_is_answered_for_a_folder_that_nobody_serves_or_no_longer_serves()
    {
        var (served, other) = (Folder(), Folder());
        Assert.NotEqual(FramesRelay.NameFor(served), FramesRelay.NameFor(other));
        Assert.Null(await Ask(other, []));
        var relay = FramesRelay.Serve(served, _ => Task.FromResult(ToolResult.Say("served")));
        try
        {
            Assert.Null(await Ask(other, []));
            Assert.Null(await Ask(Path.GetDirectoryName(served)!, []));
            Assert.Equal("served", (await Ask(served, []))!.Text);
        }
        finally
        {
            await relay.DisposeAsync().AsTask().WaitAsync(Patience);
        }
        Assert.Null(await Ask(served, []));
    }

    // Before, the name came from the folder's path in lower case: a junction to the folder named another relay, and two
    // folders that differ in case alone named one.
    [Fact]
    public async Task A_folder_is_named_by_what_it_is_and_not_by_how_it_is_spelled()
    {
        var root = Directory.CreateTempSubdirectory("cluewren-relay").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Project")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(root, "Other")).FullName;
            Assert.Equal(FramesRelay.NameFor(folder), FramesRelay.NameFor(folder.ToUpperInvariant() + Path.DirectorySeparatorChar));
            Assert.NotEqual(FramesRelay.NameFor(folder), FramesRelay.NameFor(other));
            if (!OperatingSystem.IsWindows())
                return;
            var link = Path.Combine(root, "Link");
            Assert.Equal(0, await ProjectBindingTests.RunAsync("cmd.exe", "/c", "mklink", "/J", link, folder));
            try
            {
                Assert.Equal(FramesRelay.NameFor(folder), FramesRelay.NameFor(link));
            }
            finally
            {
                Directory.Delete(link);
            }
            var sensitive = Directory.CreateDirectory(Path.Combine(root, "Sensitive")).FullName;
            if (await ProjectBindingTests.RunAsync("fsutil.exe", "file", "setCaseSensitiveInfo", sensitive, "enable") != 0)
                return;
            var upper = Directory.CreateDirectory(Path.Combine(sensitive, "Foo")).FullName;
            var lower = Directory.CreateDirectory(Path.Combine(sensitive, "foo")).FullName;
            Assert.NotEqual(FramesRelay.NameFor(upper), FramesRelay.NameFor(lower));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Closing_does_not_wait_for_a_call_still_being_answered()
    {
        var folder = Folder();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = FramesRelay.Serve(folder, _ =>
        {
            entered.SetResult();
            return new TaskCompletionSource<ToolResult>().Task;
        });
        var asking = Ask(folder, []);
        await entered.Task.WaitAsync(Patience);
        await relay.DisposeAsync().AsTask().WaitAsync(Patience);
        Assert.Null(await asking);
    }
}
