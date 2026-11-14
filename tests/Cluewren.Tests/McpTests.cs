using System.Text.Json;
using System.Text.Json.Nodes;
using Cluewren.Mcp;

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
        await using var server = new McpServer(output) { JevKey = "test-key" };
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
        Assert.Equal(["start_run", "wait", "probe", "use_harness", "check_harness", "run_harness", "snapshot", "restart_level", "frames", "report"], tools);
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
        await using var server = new McpServer(new StringWriter()) { JevKey = "" };
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
            var hits = PlaytestRun.WordHits(project, "Verify that the jetpack ascends over the zeppelin")!;
            Assert.StartsWith("- Assets/Scripts/Jetpack.cs", hits[0]);
            Assert.DoesNotContain(hits, line => line.Contains("Other.cs"));
            Assert.Contains(hits, line => line.Contains("not in the project") && line.Contains("zeppelin"));
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
        var memory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(memory, "harness.cs"), "outside");
            foreach (var name in (string[])["before", "now", "empty"])
                Directory.CreateDirectory(Path.Combine(memory, "runs", name));
            File.WriteAllText(Path.Combine(memory, "runs", "before", "harness.cs"), "class H;");
            File.WriteAllText(Path.Combine(memory, "runs", "now", "harness.cs"), "class H;");
            Assert.Null(PlaytestRun.EarlierHarness(memory, "now", run));
            Assert.Equal(Path.Combine(memory, "runs", "before", "harness.cs"), PlaytestRun.EarlierHarness(memory, "now", "runs/before/"));
        }
        finally
        {
            Directory.Delete(memory, true);
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
