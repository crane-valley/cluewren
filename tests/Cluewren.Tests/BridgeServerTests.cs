using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cluewren.Bridge;
using Cluewren.Kit;

namespace Cluewren.Tests;

public sealed class FakePlugin : IAsyncDisposable
{
    public ClientWebSocket Socket { get; } = new();

    public string Session { get; private set; } = "";

    public static Dictionary<string, object?> Hello(byte[] secret, byte[] nonce, string project, string? resume = null,
        string? engine = null) => new()
    {
        ["type"] = "hello",
        ["protocol"] = 1,
        ["nonce"] = Convert.ToBase64String(nonce),
        ["engine"] = engine,
        ["project_dir"] = project,
        ["mac"] = Convert.ToBase64String(Handshake.ProveHello(secret, nonce, project)),
        ["resume"] = resume,
        ["received"] = resume is null ? null : 0,
    };

    public static async Task<FakePlugin> ConnectAsync(string url, byte[] secret, string project, string? resume = null,
        string? engine = null)
    {
        var plugin = new FakePlugin();
        await plugin.Socket.ConnectAsync(new Uri(url), default);
        var nonce = Handshake.NewNonce();
        await plugin.SendAsync(Hello(secret, nonce, project, resume, engine));
        using var welcome = await plugin.ReceiveAsync();
        var root = welcome.RootElement;
        Assert.True("welcome" == root.GetProperty("type").GetString(), root.GetRawText());
        var brainNonce = root.GetProperty("nonce").GetBytesFromBase64();
        Assert.True(Handshake.Verify(secret, Handshake.BrainRole, nonce, brainNonce,
            root.GetProperty("proof").GetBytesFromBase64()));
        plugin.Session = root.GetProperty("session").GetString()!;
        var proof = Handshake.Prove(secret, Handshake.PluginRole, nonce, brainNonce);
        await plugin.SendAsync(new Dictionary<string, object?>
        {
            ["type"] = "proof",
            ["proof"] = Convert.ToBase64String(proof),
        });
        return plugin;
    }

    public Task SendAsync(object frame) =>
        Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, default);

    public async Task<JsonDocument> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        var result = await Socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    public async Task AnswerNextAsync(object result)
    {
        using var request = await ReceiveAsync();
        await SendAsync(new Dictionary<string, object?>
        {
            ["type"] = "response",
            ["id"] = request.RootElement.GetProperty("id").GetInt64(),
            ["ok"] = true,
            ["result"] = result,
            ["t"] = 1.5,
        });
    }

    public ValueTask DisposeAsync()
    {
        Socket.Dispose();
        return default;
    }
}

public sealed class BridgeServerTests : IDisposable
{
    static readonly byte[] Secret = Handshake.NewSecret();

    readonly string _project = Directory.CreateTempSubdirectory("cluewren-project").FullName;

    Task<BridgeServer> StartAsync(TimeSpan? resumeWindow = null) =>
        BridgeServer.StartAsync(Secret, ProjectScope.Related(_project), resumeWindow);

    public void Dispose() => Directory.Delete(_project, true);

    [Fact]
    public async Task Refuses_browser_origin()
    {
        await using var server = await StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://example.invalid");
        var refused = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri(server.Url), default));
        Assert.Contains("403", refused.Message);
    }

    [Fact]
    public async Task Binds_loopback_only()
    {
        await using var server = await StartAsync();
        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(new Uri(server.Url).Host)));
    }

    [Fact]
    public async Task Plugin_with_wrong_secret_gets_no_link()
    {
        await using var server = await StartAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.Url), default);
        var nonce = Handshake.NewNonce();
        await Send(socket, FakePlugin.Hello(Secret, nonce, _project));
        using var welcome = await Receive(socket);
        var brainNonce = welcome.RootElement.GetProperty("nonce").GetBytesFromBase64();
        var wrong = Handshake.Prove(Handshake.NewSecret(), Handshake.PluginRole, nonce, brainNonce);
        await Send(socket, new { type = "proof", proof = Convert.ToBase64String(wrong) });
        using var refusal = await Receive(socket);
        Assert.Equal("error", refusal.RootElement.GetProperty("type").GetString());
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.NextLinkAsync(wait.Token));
    }

    [Fact]
    public async Task Reflected_brain_proof_is_refused()
    {
        await using var server = await StartAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.Url), default);
        await Send(socket, FakePlugin.Hello(Secret, Handshake.NewNonce(), _project));
        using var welcome = await Receive(socket);
        await Send(socket, new { type = "proof", proof = welcome.RootElement.GetProperty("proof").GetString() });
        using var refusal = await Receive(socket);
        Assert.Equal("error", refusal.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Hello_without_nonce_is_refused()
    {
        await using var server = await StartAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(server.Url), default);
        await Send(socket, new { type = "hello", protocol = 1 });
        using var refusal = await Receive(socket);
        Assert.Equal("error", refusal.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Request_gets_its_answer()
    {
        await using var server = await StartAsync();
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, _project);
        var link = await server.NextLinkAsync();
        var asked = link.RequestAsync("pose");
        await plugin.AnswerNextAsync(new { yaw = 90 });
        using var answer = await asked;
        Assert.Equal(90, answer.Result.GetProperty("yaw").GetInt32());
        Assert.Equal(1.5, answer.GameTime);
    }

    [Fact]
    public async Task An_answer_survives_the_frames_after_it()
    {
        await using var server = await StartAsync();
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, _project);
        var link = await server.NextLinkAsync();
        var first = link.RequestAsync("pose");
        await plugin.AnswerNextAsync(new { yaw = 1111 });
        using var answer = await first;
        var second = link.RequestAsync("pose");
        await plugin.AnswerNextAsync(new { yaw = 2222 });
        using var later = await second;
        Assert.Equal(1111, answer.Result.GetProperty("yaw").GetInt32());
        Assert.Equal(2222, later.Result.GetProperty("yaw").GetInt32());
    }

    [Fact]
    public async Task Error_answer_raises_game_exception()
    {
        await using var server = await StartAsync();
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, _project);
        var link = await server.NextLinkAsync();
        var asked = link.RequestAsync("pose");
        using var request = await plugin.ReceiveAsync();
        await plugin.SendAsync(new Dictionary<string, object?>
        {
            ["type"] = "response",
            ["id"] = request.RootElement.GetProperty("id").GetInt64(),
            ["ok"] = false,
            ["status"] = 409,
            ["error"] = "no player",
        });
        var failed = await Assert.ThrowsAsync<GameException>(() => asked);
        Assert.Equal(409, failed.Status);
    }

    [Fact]
    public async Task Disconnect_fails_waiting_requests()
    {
        await using var server = await StartAsync();
        await using var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, _project);
        var link = await server.NextLinkAsync();
        var asked = link.RequestAsync("pose");
        await plugin.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", default);
        await Assert.ThrowsAsync<GameDisconnectedException>(() => asked);
    }

    [Fact]
    public async Task Script_reload_keeps_the_session_and_its_requests()
    {
        await using var server = await StartAsync();
        var first = await FakePlugin.ConnectAsync(server.Url, Secret, _project);
        var link = await server.NextLinkAsync();
        await first.Socket.CloseOutputAsync((WebSocketCloseStatus)1012, "reload", default);
        await first.DisposeAsync();

        var asked = link.RequestAsync("pose");
        await using var second = await ResumeAsync(server.Url, first.Session, _project);
        Assert.Equal(first.Session, second.Session);
        await second.AnswerNextAsync(new { yaw = 1 });
        using var answer = await asked;
        Assert.Equal(1, answer.Result.GetProperty("yaw").GetInt32());
    }

    [Fact]
    public async Task A_reload_that_loses_its_close_frame_keeps_the_session()
    {
        await using var server = await StartAsync();
        var first = await FakePlugin.ConnectAsync(server.Url, Secret, _project);
        var link = await server.NextLinkAsync();
        var asked = link.RequestAsync("pose");
        first.Socket.Abort();
        await first.DisposeAsync();

        await using var second = await ResumeAsync(server.Url, first.Session, _project);
        await second.AnswerNextAsync(new { yaw = 2 });
        using var answer = await asked;
        Assert.Equal(2, answer.Result.GetProperty("yaw").GetInt32());
    }

    [Fact]
    public async Task Link_closes_when_the_plugin_never_comes_back()
    {
        await using var server = await StartAsync(TimeSpan.FromMilliseconds(100));
        var plugin = await FakePlugin.ConnectAsync(server.Url, Secret, _project);
        var link = await server.NextLinkAsync();
        var asked = link.RequestAsync("pose");
        await plugin.Socket.CloseOutputAsync((WebSocketCloseStatus)1012, "reload", default);
        await plugin.DisposeAsync();
        await Assert.ThrowsAsync<GameDisconnectedException>(() => asked);
    }

    static async Task<FakePlugin> ResumeAsync(string url, string session, string project)
    {
        for (var i = 0; i < 200; i++)
        {
            var plugin = await FakePlugin.ConnectAsync(url, Secret, project, resume: session);
            if (plugin.Session == session)
                return plugin;
            await plugin.DisposeAsync();
            await Task.Delay(10);
        }
        throw new TimeoutException("the session was never resumable");
    }

    static Task Send(ClientWebSocket socket, object frame) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, default);

    static async Task<JsonDocument> Receive(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
}
