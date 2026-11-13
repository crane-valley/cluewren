using System.Diagnostics;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluewren.Bridge;
using Cluewren.Mcp;
using Cluewren.Sandbox;
using Xunit.Abstractions;

namespace Cluewren.Tests;

public sealed class ProjectBindingTests(ITestOutputHelper output) : IDisposable
{
    static readonly byte[] Secret = Handshake.NewSecret();

    readonly string _root = Directory.CreateTempSubdirectory("cluewren-binding").FullName;

    string Folder(string relative) => Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task An_editor_of_the_folder_inside_it_or_containing_it_is_welcomed()
    {
        var games = Folder("Games");
        var foo = Folder(@"Games\Foo");
        var assets = Folder(@"Games\Foo\Assets");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        foreach (var project in new[] { foo, foo + @"\", foo.Replace('\\', '/').ToLowerInvariant(), assets, games })
        {
            await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, project);
            var link = await server.NextLinkAsync();
            Assert.True(link.Project!.Holds(project), project);
        }
        Assert.Empty(server.Refused);
    }

    // Regression: the brain played the first authenticated editor, whatever its project.
    [Fact]
    public async Task An_editor_of_another_project_is_refused_before_welcome_and_named_when_none_connects()
    {
        var foo = Folder(@"Games\Foo");
        var sibling = Folder(@"Games\FooBar");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        var message = await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), sibling));
        Assert.Contains(foo, message);
        Assert.DoesNotContain(sibling, message);
        await AssertNoLinkAsync(server);
        var text = McpServer.NoEditorText(server, 60);
        Assert.Contains(foo, text);
        Assert.Contains($"{sibling}: another project", text);
        Assert.Contains("own Cluewren window", text);
    }

    [Fact]
    public async Task Exact_mode_takes_that_folder_alone()
    {
        var games = Folder("Games");
        var foo = Folder(@"Games\Foo");
        var assets = Folder(@"Games\Foo\Assets");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.ExactlyAt(foo));
        foreach (var other in new[] { games, assets })
            await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), other));
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo + @"\");
        Assert.True((await server.NextLinkAsync()).Project!.Holds(foo));
    }

    // Regression: a 0.1.0 plugin, whose hello carries no project, was welcomed.
    [Fact]
    public async Task A_hello_without_a_mac_gets_a_fixed_text()
    {
        var foo = Folder("Foo");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        var hello = new Dictionary<string, object?>
        {
            ["type"] = "hello", ["protocol"] = 1, ["nonce"] = Convert.ToBase64String(Handshake.NewNonce()),
        };
        Assert.Equal(BridgeServer.NoMac, await RefusedAsync(server.Url, hello));
        await AssertNoLinkAsync(server);
    }

    // Regression: a peer that can't read brain.json (another user's process) made the brain open a folder it named, or
    // learnt the run's folder from the refusal.
    [Fact]
    public async Task A_hello_with_a_wrong_mac_touches_no_file_and_names_no_folder()
    {
        var foo = Folder("Foo");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        var other = Handshake.NewSecret();
        Assert.Equal(BridgeServer.NotAuthenticated,
            await RefusedAsync(server.Url, FakePlugin.Hello(other, Handshake.NewNonce(), foo)));
        Assert.Equal(BridgeServer.NotAuthenticated,
            await RefusedAsync(server.Url, FakePlugin.Hello(other, Handshake.NewNonce(), Folder("Bar"))));
        var nonce = Handshake.NewNonce();
        var swapped = FakePlugin.Hello(Secret, nonce, Folder("Bar"));
        swapped["project_dir"] = foo;
        Assert.Equal(BridgeServer.NotAuthenticated, await RefusedAsync(server.Url, swapped));
        if (OperatingSystem.IsWindows())
        {
            var (pipe, opened, path) = Pipe();
            using (pipe)
            {
                Assert.Equal(BridgeServer.NotAuthenticated,
                    await RefusedAsync(server.Url, FakePlugin.Hello(other, Handshake.NewNonce(), path)));
                await Task.Delay(300);
                Assert.False(opened.IsCompleted, "the brain opened the path of an unauthenticated hello");
            }
        }
        Assert.DoesNotContain(server.Refused, refusal => refusal.Contains(foo) || refusal.Contains("Bar"));
        await AssertNoLinkAsync(server);
    }

    // Regression: a UNC or device project_dir made the brain reach a server or a device while comparing folders.
    [Fact]
    public async Task A_project_that_is_not_a_local_drive_path_is_refused_before_any_file_call()
    {
        var foo = Folder("Foo");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(_root));
        foreach (var project in new[] { @"\\server\share\Foo", @"\\?\" + foo, "Foo", "", "C:Foo", "/Games/Foo" })
        {
            var message = await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), project));
            if (OperatingSystem.IsWindows())
                Assert.Contains("full local path", message);
        }
        if (OperatingSystem.IsWindows())
        {
            var (pipe, opened, path) = Pipe();
            using (pipe)
            {
                await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), path));
                await Task.Delay(300);
                Assert.False(opened.IsCompleted, "the brain opened a device path");
            }
        }
        await AssertNoLinkAsync(server);
    }

    // Regression: the hello was read with the 16 MiB frame limit before anything was checked.
    [Fact]
    public async Task An_oversized_hello_is_refused()
    {
        var foo = Folder("Foo");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        var hello = FakePlugin.Hello(Secret, Handshake.NewNonce(), foo);
        hello["padding"] = new string('x', 64 * 1024);
        Assert.Equal("hello too large", await RefusedAsync(server.Url, hello));
        await AssertNoLinkAsync(server);
    }

    // Regression: any number of handshakes could be held open at once.
    [Fact]
    public async Task A_ninth_handshake_in_progress_is_turned_away()
    {
        var foo = Folder("Foo");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        var waiting = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var socket = new ClientWebSocket();
                waiting.Add(socket);
                await socket.ConnectAsync(new Uri(server.Url), default);
            }
            using var ninth = new ClientWebSocket();
            var turned = await Assert.ThrowsAsync<WebSocketException>(() => ninth.ConnectAsync(new Uri(server.Url), default));
            Assert.Contains("503", turned.Message);
            await waiting[0].CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo);
                    break;
                }
                catch (WebSocketException) when (attempt < 100)
                {
                    await Task.Delay(20);
                }
            }
            await server.NextLinkAsync();
        }
        finally
        {
            foreach (var socket in waiting)
                socket.Dispose();
        }
    }

    [Fact]
    public async Task A_refused_editor_leaves_the_run_to_a_matching_one()
    {
        var foo = Folder("Foo");
        var bar = Folder("Bar");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), bar));
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo);
        Assert.True((await server.NextLinkAsync()).Project!.Holds(foo));
        Assert.Equal([$"{bar}: another project"], server.Refused);
    }

    // Regression: a resume took over a suspended session whatever project or engine it came from.
    [Fact]
    public async Task A_resume_carries_the_project_and_engine_of_its_session()
    {
        var foo = Folder(@"Games\Foo");
        var bar = Folder(@"Games\Bar");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(Path.GetDirectoryName(foo)!));
        var first = await FakePlugin.ConnectAsync(server.Url, Secret, foo, engine: "godot");
        await server.NextLinkAsync();
        await first.Socket.CloseOutputAsync((WebSocketCloseStatus)1012, "reload", default);
        await DrainAsync(first.Socket);
        await first.DisposeAsync();

        Assert.Contains("another project or engine",
            await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), bar, first.Session, "godot")));
        Assert.Contains("another project or engine",
            await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), foo, first.Session, "unity")));
        await using var second = await FakePlugin.ConnectAsync(server.Url, Secret, foo.ToUpperInvariant(), first.Session, "godot");
        Assert.Equal(first.Session, second.Session);
    }

    // Regression: the memory folder came from the game's own context answer, not from the project the brain checked.
    [Fact]
    public async Task A_run_stops_when_the_game_names_another_project_than_the_editor_connected_for()
    {
        var foo = Folder("Foo");
        var bar = Folder("Bar");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo);
        var link = await server.NextLinkAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var opening = PlaytestRun.OpenAsync(link, new RunRequest("x", "goal"), null!, "test", SandboxMode.Auto, limit.Token);
        await plugin.AnswerNextAsync(new { project_dir = bar });
        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => opening);
        Assert.Contains(bar, stopped.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(foo));
        Assert.Empty(Directory.EnumerateFileSystemEntries(bar));
    }

    [Fact]
    public async Task A_junction_to_the_project_is_announced_and_matches_by_identity()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var foo = Folder(@"Games\Foo");
        var link = Path.Combine(_root, "Link");
        Assert.Equal(0, await RunAsync("cmd.exe", "/c", "mklink", "/J", link, foo));
        try
        {
            var scope = ProjectScope.Related(link);
            Assert.Equal([link, foo], scope.Folders, StringComparer.OrdinalIgnoreCase);
            foreach (var exact in new[] { ProjectScope.ExactlyAt(link), scope })
            {
                await using var server = await BridgeServer.StartAsync(Secret, exact);
                await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo);
                await server.NextLinkAsync();
            }
        }
        finally
        {
            // A recursive delete is refused on a junction.
            Directory.Delete(link);
        }
    }

    // Regression: a textual, case-insensitive comparison takes a sibling that differs in case alone for the project.
    [Fact]
    public async Task A_sibling_that_differs_in_case_in_a_case_sensitive_folder_is_another_project()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var parent = Folder("Sensitive");
        var made = await RunAsync("fsutil.exe", "file", "setCaseSensitiveInfo", parent, "enable");
        if (made != 0)
        {
            output.WriteLine($"Skipped: fsutil can't make a case-sensitive folder on this machine (exit {made}).");
            return;
        }
        var upper = Folder(@"Sensitive\Foo");
        var lower = Folder(@"Sensitive\foo");
        using (var scope = ProjectScope.ExactlyAt(upper))
            Assert.Null(scope.Admit(lower));
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(upper));
        await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), lower));
        await AssertNoLinkAsync(server);
    }

    [Fact]
    public void The_announced_folders_are_full_paths_without_a_trailing_separator_or_prefix()
    {
        var foo = Folder(@"Games\Foo");
        Assert.Equal([foo], Announced(foo + @"\"));
        Assert.Equal([foo], Announced(foo.Replace('\\', '/')));
        using (var exact = ProjectScope.ExactlyAt(foo))
            Assert.Equal("exact", exact.Match);
        using (var related = ProjectScope.Related(foo))
            Assert.Equal("related", related.Match);
        var root = Path.GetPathRoot(foo)!;
        Assert.Equal([root], Announced(root));
        if (OperatingSystem.IsWindows() && ShortName(Folder("A long folder name")) is { } shortName
            && !shortName.EndsWith("A long folder name", StringComparison.Ordinal))
        {
            var announced = Announced(shortName);
            Assert.Equal([shortName, Path.Combine(_root, "A long folder name")], announced, StringComparer.OrdinalIgnoreCase);
        }
        else
            output.WriteLine("8.3 names are off on this volume: the short-name announcement was not checked.");
        Assert.All(Announced(foo), folder => Assert.False(folder.StartsWith(@"\\?\", StringComparison.Ordinal)));
    }

    static string[] Announced(string folder)
    {
        using var scope = ProjectScope.Related(folder);
        return [.. scope.Folders];
    }

    [Fact]
    public void Brain_json_carries_the_folders_and_match()
    {
        var foo = Folder("Foo");
        using var scope = ProjectScope.ExactlyAt(foo);
        var announced = new Discovery("ws://127.0.0.1:1/v1/bridge", "c2VjcmV0", 7, DateTimeOffset.UnixEpoch, scope.Folders,
            scope.Match);
        var path = DiscoveryFile.Write(announced, Folder("discovery"));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(foo, json["folders"]![0]!.GetValue<string>());
        Assert.Equal("exact", json["match"]!.GetValue<string>());
        Assert.Equal(announced, DiscoveryFile.Read(path));
    }

    [Fact]
    public async Task Start_run_takes_only_a_full_local_folder_as_project_before_opening_a_port()
    {
        await using var server = new McpServer(new StringWriter()) { JevKey = "test-key" };
        foreach (var project in new[] { "Games\\Foo", Path.Combine(_root, "missing"), @"\\server\share\Foo", "" })
        {
            var result = await server.CallAsync("start_run",
                new JsonObject { ["request"] = "x", ["kind"] = "goal", ["project"] = project });
            Assert.True(result.Error);
            Assert.Contains("project must be", result.Text);
        }
    }

    [Theory]
    [InlineData("Games\\Foo")]
    [InlineData(null)]
    public async Task Cluewren_run_refuses_a_project_that_is_not_a_full_local_folder(string? project)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Cluewren.exe"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(Folder("run"));
        start.ArgumentList.Add("--project");
        if (project is not null)
            start.ArgumentList.Add(project);
        start.Environment["CLUEWREN_SANDBOX"] = "auto";
        using var cluewren = Process.Start(start)!;
        var error = cluewren.StandardError.ReadToEndAsync();
        var said = cluewren.StandardOutput.ReadToEndAsync();
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await cluewren.WaitForExitAsync(limit.Token);
            Assert.Equal(2, cluewren.ExitCode);
            Assert.Contains("--project", await error);
            Assert.DoesNotContain("Waiting", await said);
        }
        finally
        {
            if (!cluewren.HasExited)
                cluewren.Kill();
        }
    }

    // Regression: a resume compared a re-opened path, so a junction re-pointed while the link was down passed.
    [Fact]
    public async Task A_resume_through_a_re_pointed_junction_is_another_project()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var foo = Folder(@"Games\Foo");
        var bar = Folder(@"Games\Bar");
        var link = await JunctionAsync("Link", foo);
        try
        {
            await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(_root));
            var first = await FakePlugin.ConnectAsync(server.Url, Secret, link, engine: "godot");
            await server.NextLinkAsync();
            await first.Socket.CloseOutputAsync((WebSocketCloseStatus)1012, "reload", default);
            await DrainAsync(first.Socket);
            await first.DisposeAsync();
            await RepointAsync(link, bar);

            Assert.Contains("another project or engine",
                await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), link, first.Session, "godot")));
            await using var second = await FakePlugin.ConnectAsync(server.Url, Secret, foo, first.Session, "godot");
            Assert.Equal(first.Session, second.Session);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    // Regression: the game's project was compared with a re-opened path, not with the folder verified at the handshake.
    [Fact]
    public async Task A_context_through_a_re_pointed_junction_stops_the_run()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var foo = Folder(@"Games\Foo");
        var bar = Folder(@"Games\Bar");
        var link = await JunctionAsync("Link", foo);
        try
        {
            await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(_root));
            await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, link);
            var connected = await server.NextLinkAsync();
            await RepointAsync(link, bar);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var opening = PlaytestRun.OpenAsync(connected, new RunRequest("x", "goal"), null!, "test", SandboxMode.Auto,
                limit.Token);
            await plugin.AnswerNextAsync(new { project_dir = link });
            await Assert.ThrowsAsync<InvalidOperationException>(() => opening);
            Assert.Empty(Directory.EnumerateFileSystemEntries(foo));
            Assert.Empty(Directory.EnumerateFileSystemEntries(bar));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    // Regression: the run's own folder was opened again at each hello, so its junction re-pointed after start_run moved
    // the run to another project.
    [Fact]
    public async Task The_run_keeps_the_folder_its_junction_named_at_the_start()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var foo = Folder(@"Games\Foo");
        var bar = Folder(@"Games\Bar");
        var link = await JunctionAsync("Link", foo);
        try
        {
            var scope = ProjectScope.Related(link);
            await RepointAsync(link, bar);
            await using var server = await BridgeServer.StartAsync(Secret, scope);
            await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), bar));
            await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo);
            await server.NextLinkAsync();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    // Regression: the project was checked only before the game started, when Godot's editor answers for it.
    [Fact]
    public async Task A_game_that_names_another_project_once_started_stops_the_run_before_anything_is_written()
    {
        var foo = Folder("Foo");
        var bar = Folder("Bar");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo);
        var link = await server.NextLinkAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var opening = PlaytestRun.OpenAsync(link, new RunRequest("x", "goal"), null!, "test", SandboxMode.Auto, limit.Token);
        await plugin.AnswerNextAsync(new { project_dir = foo });
        await plugin.AnswerNextAsync(new { running = true });
        await plugin.AnswerNextAsync(new { project_dir = bar });
        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => opening);
        Assert.Contains(bar, stopped.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(foo));
        await AssertNothingMoreAsync(plugin);
    }

    // Regression: `cluewren run` started the harness straight from the link, with no project check at all; then it started
    // the default scene, where the harness's StartMap had been played before.
    [Fact]
    public async Task Cluewren_run_starts_the_harness_start_map_and_stops_when_that_game_names_another_project()
    {
        var foo = Folder("Foo");
        var bar = Folder("Bar");
        var runFolder = Folder("run");
        await File.WriteAllTextAsync(Path.Combine(runFolder, "harness.cs"),
            "class A : Harness { public override string? StartMap => \"res://levels/start.tscn\"; }\n");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo);
        var link = await server.NextLinkAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var playing = RunCommand.PlayAsync(link, runFolder, 1, SandboxMode.Auto, limit.Token);
        await plugin.AnswerNextAsync(new { project_dir = foo });
        using (var start = await plugin.ReceiveAsync())
        {
            Assert.Equal("session", start.RootElement.GetProperty("op").GetString());
            Assert.Equal("res://levels/start.tscn", start.RootElement.GetProperty("args").GetProperty("map").GetString());
            await AnswerAsync(plugin, start, new { running = true });
        }
        await plugin.AnswerNextAsync(new { project_dir = bar });
        Assert.Equal(1, await playing);
        Assert.Empty(Directory.EnumerateFiles(runFolder, "trace*"));
        await AssertNothingMoreAsync(plugin);
    }

    // Regression: a plugin's start error or a disconnect in the new start step escaped `cluewren run` as an exception.
    [Fact]
    public async Task Cluewren_run_reports_a_start_error_or_a_disconnect_as_a_run_that_did_not_start()
    {
        var foo = Folder("Foo");
        var runFolder = Folder("run");
        await File.WriteAllTextAsync(Path.Combine(runFolder, "harness.cs"), "class A : Harness { }\n");
        var said = new StringWriter();
        var error = Console.Error;
        Console.SetError(said);
        try
        {
            foreach (var disconnect in new[] { false, true })
            {
                await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
                await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, foo);
                var link = await server.NextLinkAsync();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var playing = RunCommand.PlayAsync(link, runFolder, 1, SandboxMode.Auto, limit.Token);
                await plugin.AnswerNextAsync(new { project_dir = foo });
                if (disconnect)
                    await plugin.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
                else
                {
                    using var start = await plugin.ReceiveAsync();
                    await plugin.SendAsync(new Dictionary<string, object?>
                    {
                        ["type"] = "response", ["id"] = start.RootElement.GetProperty("id").GetInt64(), ["ok"] = false,
                        ["status"] = 500, ["error"] = "no main scene",
                    });
                }
                Assert.Equal(1, await playing);
            }
        }
        finally
        {
            Console.SetError(error);
        }
        Assert.Contains("The run didn't start: session: no main scene", said.ToString());
        Assert.Equal(2, said.ToString().Split("The run didn't start").Length - 1);
    }

    // Regression: a hello of exactly 64 KiB was refused when its last fragment was empty.
    [Fact]
    public async Task A_hello_of_exactly_the_limit_is_taken_however_it_is_fragmented()
    {
        var foo = Folder("Foo");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        var exact = HelloOfSize(foo, 64 * 1024);
        foreach (var parts in new[] { new[] { exact.Length }, new[] { 1000, exact.Length - 1000, 0 }, new[] { exact.Length, 0 } })
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(server.Url), default);
            var sent = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                await socket.SendAsync(exact.AsMemory(sent, parts[i]), WebSocketMessageType.Text, i == parts.Length - 1, default);
                sent += parts[i];
            }
            using var reply = await ReceiveAsync(socket);
            Assert.Equal("welcome", reply.RootElement.GetProperty("type").GetString());
        }
        using var over = new ClientWebSocket();
        await over.ConnectAsync(new Uri(server.Url), default);
        var larger = HelloOfSize(foo, 64 * 1024 + 1);
        await over.SendAsync(larger.AsMemory(0, 64 * 1024), WebSocketMessageType.Text, false, default);
        await over.SendAsync(larger.AsMemory(64 * 1024), WebSocketMessageType.Text, true, default);
        using var refusal = await ReceiveAsync(over);
        Assert.Equal("hello too large", refusal.RootElement.GetProperty("message").GetString());
    }

    // Regression: an oversized hello, a hello of another protocol and a failed proof were refused without a word for the
    // agent.
    [Fact]
    public async Task Every_refusal_at_the_handshake_is_recorded_for_the_agent()
    {
        var foo = Folder("Foo");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        var oversized = FakePlugin.Hello(Secret, Handshake.NewNonce(), foo);
        oversized["padding"] = new string('x', 64 * 1024);
        await RefusedAsync(server.Url, oversized);
        var otherProtocol = FakePlugin.Hello(Secret, Handshake.NewNonce(), foo);
        otherProtocol["protocol"] = 2;
        await RefusedAsync(server.Url, otherProtocol);
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(new Uri(server.Url), default);
            var nonce = Handshake.NewNonce();
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(FakePlugin.Hello(Secret, nonce, foo)),
                WebSocketMessageType.Text, true, default);
            using var welcome = await ReceiveAsync(socket);
            var wrong = Handshake.Prove(Handshake.NewSecret(), Handshake.PluginRole, nonce,
                welcome.RootElement.GetProperty("nonce").GetBytesFromBase64());
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "proof", proof = Convert.ToBase64String(wrong) }),
                WebSocketMessageType.Text, true, default);
            using var refusal = await ReceiveAsync(socket);
            Assert.Equal("error", refusal.RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(["an editor whose hello was too large", "an editor whose hello isn't one of protocol 1",
            $"{foo}: failed the proof"], server.Refused);
        Assert.Contains("failed the proof", McpServer.NoEditorText(server, 60));
    }

    // Regression: the stored file id was kept without its folder held open, and Windows may give a deleted folder's id to
    // another one. An open handle shows as a parent that can't be renamed; the id reuse itself can't be provoked here.
    [Fact]
    public async Task The_run_folder_is_held_open_for_the_server_and_let_go_after()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var games = Folder("Games");
        var foo = Folder(@"Games\Foo");
        var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(foo));
        try
        {
            Assert.Throws<IOException>(() => Directory.Move(games, games + "2"));
        }
        finally
        {
            await server.DisposeAsync();
        }
        Directory.Move(games, games + "2");
    }

    [Fact]
    public async Task A_link_holds_its_folder_until_it_ends_and_a_refused_hello_holds_nothing()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var games = Folder("Games");
        var foo = Folder(@"Games\Foo");
        var sub = Folder(@"Games\Foo\Sub");
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(games),
            TimeSpan.FromSeconds(1));

        var nonce = Handshake.NewNonce();
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(new Uri(server.Url), default);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(FakePlugin.Hello(Secret, nonce, sub)),
                WebSocketMessageType.Text, true, default);
            using var welcome = await ReceiveAsync(socket);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "proof", proof = "AAAA" }),
                WebSocketMessageType.Text, true, default);
            using var refusal = await ReceiveAsync(socket);
        }
        await MovesAsync(foo);

        var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, sub);
        await server.NextLinkAsync();
        Assert.Throws<IOException>(() => Directory.Move(foo, foo + "2"));
        await plugin.Socket.CloseOutputAsync((WebSocketCloseStatus)1012, "reload", default);
        await DrainAsync(plugin.Socket);
        await plugin.DisposeAsync();
        Assert.Throws<IOException>(() => Directory.Move(foo, foo + "2"));
        await MovesAsync(foo);

        await using var again = await FakePlugin.ConnectAsync(server.Url, Secret, sub);
        await server.NextLinkAsync();
        await again.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        await DrainAsync(again.Socket);
        await MovesAsync(foo);
    }

    // Each move is undone, so the folder is where the next step expects it.
    static async Task MovesAsync(string folder)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(folder, folder + "2");
                Directory.Move(folder + "2", folder);
                return;
            }
            catch (IOException) when (attempt < 250)
            {
                await Task.Delay(20);
            }
        }
    }

    static Task AnswerAsync(FakePlugin plugin, JsonDocument request, object result) =>
        plugin.SendAsync(new Dictionary<string, object?>
        {
            ["type"] = "response", ["id"] = request.RootElement.GetProperty("id").GetInt64(), ["ok"] = true,
            ["result"] = result, ["t"] = 1.5,
        });

    static byte[] HelloOfSize(string project, int size)
    {
        var hello = FakePlugin.Hello(Secret, Handshake.NewNonce(), project);
        hello["padding"] = "";
        var bare = JsonSerializer.SerializeToUtf8Bytes(hello).Length;
        hello["padding"] = new string('x', size - bare);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(hello);
        Assert.Equal(size, bytes.Length);
        return bytes;
    }

    static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    static async Task AssertNothingMoreAsync(FakePlugin plugin)
    {
        using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.Socket.ReceiveAsync(new byte[1024], quiet.Token));
    }

    async Task<string> JunctionAsync(string name, string target)
    {
        var link = Path.Combine(_root, name);
        Assert.Equal(0, await RunAsync("cmd.exe", "/c", "mklink", "/J", link, target));
        return link;
    }

    static async Task RepointAsync(string link, string target)
    {
        Directory.Delete(link);
        Assert.Equal(0, await RunAsync("cmd.exe", "/c", "mklink", "/J", link, target));
    }

    // The brain marks the session suspended before it lets go of the socket.
    static async Task DrainAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while ((await socket.ReceiveAsync(new byte[1024], timeout.Token)).MessageType != WebSocketMessageType.Close)
            {
            }
        }
        catch (WebSocketException)
        {
        }
    }

    static async Task<string> RefusedAsync(string url, object hello)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(url), default);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(hello), WebSocketMessageType.Text, true, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        Assert.True("error" == reply.RootElement.GetProperty("type").GetString(), reply.RootElement.GetRawText());
        return reply.RootElement.GetProperty("message").GetString()!;
    }

    static async Task AssertNoLinkAsync(BridgeServer server)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.NextLinkAsync(wait.Token));
    }

    static (NamedPipeServerStream Pipe, Task Opened, string Path) Pipe()
    {
        var name = "cluewren-test-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        return (pipe, pipe.WaitForConnectionAsync(), $@"\\.\pipe\{name}");
    }

    static async Task<int> RunAsync(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    static string? ShortName(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetShortPathName(string path, StringBuilder shortPath, int length);
}
