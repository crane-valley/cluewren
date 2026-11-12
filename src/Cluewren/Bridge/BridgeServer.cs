using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Cluewren.Bridge;

public sealed class BridgeServer : IAsyncDisposable
{
    public const int Protocol = 1;
    public const string NoMac = "update the Cluewren plugin in this project";
    public const string NotAuthenticated = "not authenticated";

    const int MaxHelloBytes = 64 * 1024;
    const int MaxHandshakes = 8;
    const int MaxRefusals = 8;
    const int MaxRefusedChars = 260;
    const int MaxCloseBytes = 123;

    // Unity closes with this code when it reloads scripts on entering play mode, and comes back with the same session.

    readonly byte[] _secret;
    readonly TimeSpan _resumeWindow;
    readonly WebApplication _app;
    readonly ConcurrentDictionary<string, BridgeLink> _sessions = new();
    readonly Channel<BridgeLink> _connected = Channel.CreateUnbounded<BridgeLink>();
    readonly CancellationTokenSource _stopping = new();
    readonly List<string> _refused = [];
    int _handshakes;

    BridgeServer(byte[] secret, ProjectScope scope, TimeSpan resumeWindow, WebApplication app)
    {
        _secret = secret;
        Scope = scope;
        _resumeWindow = resumeWindow;
        _app = app;
    }

    public string Url { get; private set; } = "";

    public ProjectScope Scope { get; }

    public IReadOnlyList<string> Refused
    {
        get
        {
            lock (_refused)
                return [.. _refused];
        }
    }

    public event Action<string>? Refusing;

    public static async Task<BridgeServer> StartAsync(byte[] secret, ProjectScope scope, TimeSpan? resumeWindow = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new BridgeServer(secret, scope, resumeWindow ?? TimeSpan.FromSeconds(90), app);
        app.UseWebSockets();
        app.Map("/v1/bridge", server.HandleAsync);
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.Url = $"ws://127.0.0.1:{new Uri(address).Port}/v1/bridge";
        return server;
    }

    public ValueTask<BridgeLink> NextLinkAsync(CancellationToken ct = default) => _connected.Reader.ReadAsync(ct);

    async Task HandleAsync(HttpContext context)
    {
        // A page in any browser on this machine is loopback too, but browsers always send Origin on a WebSocket.
        var remote = context.Connection.RemoteIpAddress;
        if (context.Request.Headers.ContainsKey(HeaderNames.Origin) || remote is null || !IPAddress.IsLoopback(remote))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (Interlocked.Increment(ref _handshakes) > MaxHandshakes)
        {
            Interlocked.Decrement(ref _handshakes);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        WebSocket accepted;
        try
        {
            accepted = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _handshakes);
            throw;
        }
        using var socket = accepted;
        (BridgeLink Link, JsonDocument Hello)? attached;
        try
        {
            attached = await AuthenticateAsync(socket).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _handshakes);
        }
        if (attached is not { } welcomed)
            return;
        var (link, hello) = welcomed;
        using (hello)
        {
            var generation = Interlocked.Increment(ref link.Generation);
            link.Suspended = false;
            var status = await link.PumpAsync(socket, _stopping.Token).ConfigureAwait(false);
            if (Volatile.Read(ref link.Generation) != generation)
                return;
            // A domain reload can tear the socket down before the 1012 close frame gets out, so only a clean close is final.
            if (status != WebSocketCloseStatus.NormalClosure)
            {
                link.Suspended = true;
                _ = ExpireAsync(link, generation);
                return;
            }
            _sessions.TryRemove(link.Session, out _);
            link.Close($"the plugin disconnected ({socket.CloseStatusDescription})");
        }
    }

    async Task ExpireAsync(BridgeLink link, int generation)
    {
        try
        {
            await Task.Delay(_resumeWindow, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        if (Volatile.Read(ref link.Generation) == generation && link.Suspended)
        {
            _sessions.TryRemove(link.Session, out _);
            link.Close("the plugin didn't come back after reloading");
        }
    }

    async Task<(BridgeLink Link, JsonDocument Hello)?> AuthenticateAsync(WebSocket socket)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        JsonDocument? hello = null;
        try
        {
            var (first, tooLarge) = await ReceiveHelloAsync(socket, deadline.Token).ConfigureAwait(false);
            if (tooLarge)
            {
                await RefuseAsync(socket, "hello too large", deadline.Token).ConfigureAwait(false);
                return null;
            }
            hello = first;
            if (hello is null)
                return null;
            var root = hello.RootElement;
            if (!IsHello(root) || !TryNonce(root, "nonce", out var pluginNonce))
            {
                await RefuseAsync(socket, $"expected hello with protocol {Protocol}", deadline.Token).ConfigureAwait(false);
                hello.Dispose();
                return null;
            }
            // Until its mac is checked the peer may be another user's process, which reaches this port but can't read
            // brain.json: nothing it sent touches the file system or a message before that.
            if (!root.TryGetProperty("mac", out _))
            {
                await RefuseAsync(socket, NoMac, deadline.Token,
                    "an editor with an older Cluewren plugin: update the plugin in its project").ConfigureAwait(false);
                hello.Dispose();
                return null;
            }
            if (!TryText(root, "project_dir", out var project) || !TryBytes(root, "mac", out var mac)
                || !Handshake.VerifyHello(_secret, pluginNonce, project, mac))
            {
                await RefuseAsync(socket, NotAuthenticated, deadline.Token, "an editor that did not authenticate")
                    .ConfigureAwait(false);
                hello.Dispose();
                return null;
            }
            if (!ProjectScope.IsLocalPath(project))
            {
                await RefuseAsync(socket, @"the project folder isn't a full local path (like C:\Games\Foo)", deadline.Token,
                    $"{Shown(project)}: not a full local path").ConfigureAwait(false);
                hello.Dispose();
                return null;
            }
            if (!Scope.Admits(project))
            {
                await RefuseAsync(socket, $"this agent's run is for another project: {Scope.Described}", deadline.Token,
                    $"{Shown(project)}: another project").ConfigureAwait(false);
                hello.Dispose();
                return null;
            }

            var resumable = root.TryGetProperty("resume", out var resume) && resume.GetString() is { } id
                && _sessions.TryGetValue(id, out var found) && found.Suspended ? found : null;
            if (resumable is not null
                && (EngineOf(root) != resumable.Engine || !ProjectScope.SameFolder(resumable.Project, project)))
            {
                await RefuseAsync(socket, "the session it resumes is of another project or engine", deadline.Token,
                    $"{Shown(project)}: resumed a session of another project or engine").ConfigureAwait(false);
                hello.Dispose();
                return null;
            }
            var session = resumable?.Session ?? $"local-{Guid.NewGuid():N}";
            var brainNonce = Handshake.NewNonce();
            var proof = Handshake.Prove(_secret, Handshake.BrainRole, pluginNonce, brainNonce);
            await SendWelcomeAsync(socket, session, brainNonce, proof, deadline.Token).ConfigureAwait(false);

            using var answer = await BridgeLink.ReceiveAsync(socket, deadline.Token).ConfigureAwait(false);
            if (answer is null || !IsProof(answer.RootElement, pluginNonce, brainNonce))
            {
                await RefuseAsync(socket, "bad proof", deadline.Token).ConfigureAwait(false);
                hello.Dispose();
                return null;
            }

            if (resumable is not null)
            {
                resumable.Hello = root;
                resumable.Rewind(root.TryGetProperty("received", out var received) && received.TryGetInt64(out var seen)
                    ? seen : long.MaxValue);
                return (resumable, hello);
            }
            var link = new BridgeLink(session, root) { Project = ProjectScope.Normal(project) };
            _sessions[session] = link;
            await _connected.Writer.WriteAsync(link, deadline.Token).ConfigureAwait(false);
            return (link, hello);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException
                                      or InvalidOperationException)
        {
            hello?.Dispose();
            return null;
        }
    }

    static bool IsHello(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type) && type.ValueEquals("hello")
        && root.TryGetProperty("protocol", out var protocol) && protocol.TryGetInt32(out var version)
        && version == Protocol;

    bool IsProof(JsonElement root, byte[] pluginNonce, byte[] brainNonce) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type) && type.ValueEquals("proof")
        && TryBytes(root, "proof", out var proof)
        && Handshake.Verify(_secret, Handshake.PluginRole, pluginNonce, brainNonce, proof);

    static string EngineOf(JsonElement hello) =>
        hello.TryGetProperty("engine", out var engine) && engine.ValueKind == JsonValueKind.String
        && engine.GetString() is { Length: > 0 } name ? name.ToLowerInvariant() : "unity";

    static string Shown(string folder) => folder.Length > MaxRefusedChars ? folder[..MaxRefusedChars] : folder;

    static bool TryText(JsonElement root, string name, out string text)
    {
        text = "";
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString()!;
        return true;
    }

    static bool TryNonce(JsonElement root, string name, out byte[] nonce) =>
        TryBytes(root, name, out nonce) && nonce.Length == Handshake.NonceBytes;

    static bool TryBytes(JsonElement root, string name, out byte[] bytes)
    {
        bytes = [];
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.TryGetBytesFromBase64(out bytes!);
    }

    static async Task SendWelcomeAsync(WebSocket socket, string session, byte[] nonce, byte[] proof, CancellationToken ct)
    {
        var frame = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["type"] = "welcome",
            ["role"] = "service",
            ["session"] = session,
            ["features"] = Array.Empty<string>(),
            ["nonce"] = Convert.ToBase64String(nonce),
            ["proof"] = Convert.ToBase64String(proof),
        });
        await socket.SendAsync(frame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    static async Task<(JsonDocument? Hello, bool TooLarge)> ReceiveHelloAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[MaxHelloBytes];
        var used = 0;
        while (true)
        {
            if (used == buffer.Length)
                return (null, true);
            var result = await socket.ReceiveAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return (null, false);
            used += result.Count;
            if (result.EndOfMessage)
                return (JsonDocument.Parse(buffer.AsMemory(0, used)), false);
        }
    }

    async Task RefuseAsync(WebSocket socket, string message, CancellationToken ct, string noted)
    {
        Note(noted);
        await RefuseAsync(socket, message, ct).ConfigureAwait(false);
    }

    void Note(string refusal)
    {
        lock (_refused)
        {
            if (_refused.Count >= MaxRefusals || _refused.Contains(refusal))
                return;
            _refused.Add(refusal);
        }
        Refusing?.Invoke(refusal);
    }

    static async Task RefuseAsync(WebSocket socket, string message, CancellationToken ct)
    {
        var frame = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["type"] = "error",
            ["message"] = message,
        });
        await socket.SendAsync(frame, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        var reason = Encoding.UTF8.GetByteCount(message) <= MaxCloseBytes ? message : "refused";
        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var link in _sessions.Values)
            link.Close("the agent service stopped");
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
