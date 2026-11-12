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
            Assert.True(ProjectScope.SameFolder(project, link.Project), project);
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
        Assert.True(ProjectScope.SameFolder(foo, (await server.NextLinkAsync()).Project));
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
        Assert.True(ProjectScope.SameFolder(foo, (await server.NextLinkAsync()).Project));
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
        Assert.False(ProjectScope.SameFolder(upper, lower));
        await using var server = await BridgeServer.StartAsync(Secret, ProjectScope.Related(upper));
        await RefusedAsync(server.Url, FakePlugin.Hello(Secret, Handshake.NewNonce(), lower));
        await AssertNoLinkAsync(server);
    }

    [Fact]
    public void The_announced_folders_are_full_paths_without_a_trailing_separator_or_prefix()
    {
        var foo = Folder(@"Games\Foo");
        Assert.Equal([foo], ProjectScope.Related(foo + @"\").Folders);
        Assert.Equal([foo], ProjectScope.Related(foo.Replace('\\', '/')).Folders);
        Assert.Equal("exact", ProjectScope.ExactlyAt(foo).Match);
        Assert.Equal("related", ProjectScope.Related(foo).Match);
        var root = Path.GetPathRoot(foo)!;
        Assert.Equal([root], ProjectScope.Related(root).Folders);
        if (OperatingSystem.IsWindows() && ShortName(Folder("A long folder name")) is { } shortName
            && !shortName.EndsWith("A long folder name", StringComparison.Ordinal))
        {
            var announced = ProjectScope.Related(shortName).Folders;
            Assert.Equal([shortName, Path.Combine(_root, "A long folder name")], announced, StringComparer.OrdinalIgnoreCase);
        }
        else
            output.WriteLine("8.3 names are off on this volume: the short-name announcement was not checked.");
        Assert.All(ProjectScope.Related(foo).Folders, folder => Assert.False(folder.StartsWith(@"\\?\", StringComparison.Ordinal)));
    }

    [Fact]
    public void Brain_json_carries_the_folders_and_match()
    {
        var foo = Folder("Foo");
        var scope = ProjectScope.ExactlyAt(foo);
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
