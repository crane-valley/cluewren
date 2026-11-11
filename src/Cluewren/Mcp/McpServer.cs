using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cluewren.Bridge;
using Cluewren.Jev;
using Cluewren.Sandbox;

namespace Cluewren.Mcp;

public sealed class McpServer(TextWriter output, double waitSeconds = 40, double connectSeconds = 60) : IAsyncDisposable
{
    const string Protocol = "2025-06-18";
    const string Name = "cluewren";

    public static string Version { get; } =
        typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    const string Instructions = "Cluewren playtests the game open in the user's Unity, Unreal or Godot editor: call start_run with the user's "
        + "request (explore it, test it, verify a claim, reach a goal), then follow the INSTRUCTIONS.md it names. A long call answers "
        + "\"still going\": call wait for its result.";

    static readonly HashSet<string> ReadOnly = ["wait", "snapshot", "frames"];

    static readonly (string Name, string Description, string Schema)[] Tools =
    [
        ("start_run",
            "Start a playtest run on the game open in the user's editor: the decision model plays it, in a harness you write. request: "
            + "what the user asks for, in their words (\"Verify that the door opens with the key\"). kind: verify for a claim to check "
            + "(CONFIRMED, DENIED or INCONCLUSIVE), goal for something to make happen in play, explore to look for bugs or to test the "
            + "game. minutes: an exploration's minutes of play (default 10). time_limit_minutes: the run's time limit (default 30). "
            + "level: the level to play, when you know its path or name (default: the one open in the editor). Answers with the "
            + "briefing and the instructions to follow; the game is paused between calls.",
            """{"type":"object","properties":{"request":{"type":"string"},"kind":{"type":"string","enum":["verify","goal","explore"]},"minutes":{"type":"number"},"time_limit_minutes":{"type":"number"},"level":{"type":"string"}},"required":["request","kind"]}"""),
        ("wait",
            "Wait for the call still going (an episode, the game starting) and give its result. If it is still going after about 40 s, it "
            + "says so: call wait again.",
            """{"type":"object","properties":{}}"""),
        ("probe",
            "Run C# statements against the live game (it runs while this tool runs, then pauses). `game` and everything in the kit "
            + "is in scope, with top-level await. Console output and the value of the last expression come back. Use it to find actors, "
            + "read properties, press inputs and read what changed, and to research by changing the game directly. At most 120 s each: "
            + "past that the keys are let go, the game paused, and what it printed comes back.",
            """{"type":"object","properties":{"code":{"type":"string"}},"required":["code"]}"""),
        ("use_harness",
            "Copy an earlier run's harness.cs into this run, instead of writing it out again. run: its id, from the briefing's Earlier runs.",
            """{"type":"object","properties":{"run":{"type":"string"}},"required":["run"]}"""),
        ("check_harness",
            "Try this run's harness.cs without Jev: Setup, the first observation with its facts, status, plan step and options, then each "
            + "option's executor alone, and say how each went. seconds: how long each may take. only: the option names to try.",
            """{"type":"object","properties":{"seconds":{"type":"number"},"restart":{"type":"boolean"},"only":{"type":"array","items":{"type":"string"}}}}"""),
        ("run_harness",
            "Run this request's harness.cs on the live game for `seconds` of play (5-360; default restart: the level starts over first; "
            + "restart=false continues play from where it is, and the plan's progress of the episode before is handed to the harness's "
            + "Resume) with Jev deciding. Returns a summary: how each option fared, the timeline, where the plan got, what Jev saw, "
            + "errors. The full trace is saved beside harness.cs as traceN.jsonl. frames_fps: the frames a second the episode is filmed "
            + "at (1-20; default 5: more for a fast animation).",
            """{"type":"object","properties":{"seconds":{"type":"number"},"restart":{"type":"boolean"},"frames_fps":{"type":"number"}},"required":["seconds"]}"""),
        ("snapshot",
            "What the player sees now: where it is, the visible things, what it holds, its body, the texts on screen.",
            """{"type":"object","properties":{}}"""),
        ("restart_level",
            "Start the level over (fresh state, the player respawned). level: play this level instead (a path from the briefing's list "
            + "of the project's levels): it plays from then on, every restart starting it over.",
            """{"type":"object","properties":{"level":{"type":"string"}}}"""),
        ("frames",
            "For a describer subagent only (not the agent working on the request): up to 12 of an episode's frames, as images, spread over "
            + "game times start-end. episode: its number (default: the latest); start, end: game times in it, in seconds (default: all of "
            + "it); count: how many (default 8).",
            """{"type":"object","properties":{"episode":{"type":"integer"},"start":{"type":"number"},"end":{"type":"number"},"count":{"type":"integer"}}}"""),
        ("report",
            "The final answer to the request. outcome: for a goal 'achieved' or 'not_achieved'; for a claim 'CONFIRMED', 'DENIED' or "
            + "'INCONCLUSIVE'; for an exploration 'explored'. summary: what happened, in a few sentences. evidence: the observations from "
            + "play it rests on (with trace file and game times; a description with its episode and times). findings: for an exploration, "
            + "each suspected bug with its observation. visual: true when the outcome rests on how something looks. The game's developer "
            + "reads it: call the player \"the agent\" (never Jev).",
            """{"type":"object","properties":{"outcome":{"type":"string"},"summary":{"type":"string"},"evidence":{"type":"array","items":{"type":"string"}},"findings":{"type":"array","items":{"type":"string"}},"visual":{"type":"boolean"}},"required":["outcome","summary","evidence"]}"""),
    ];

    readonly SemaphoreSlim _send = new(1, 1);
    readonly object _gate = new();
    readonly CancellationTokenSource _stopping = new();
    readonly SemaphoreSlim _runLock = new(1, 1);
    (Task<ToolResult> Task, string Tool, DateTimeOffset Since)? _pending;
    string _client = "an MCP client";
    BridgeServer? _bridge;
    (string Path, Discovery Announced)? _discovery;
    JevClient? _jev;
    PlaytestRun? _run;
    (string Run, string Reason, string Folder)? _ended;
    Task? _watch;

    public string? JevKey { get; init; }

    public async Task ServeAsync(TextReader input)
    {
        var calls = new List<Task>();
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (message is not null)
                calls.Add(HandleAsync(message));
            calls.RemoveAll(call => call.IsCompleted);
        }
    }

    async Task SendAsync(JsonObject message)
    {
        message["jsonrpc"] = "2.0";
        var text = message.ToJsonString();
        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(text).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    static string ClientName(JsonNode? info)
    {
        var name = (info?["name"]?.GetValue<string>() ?? "").Trim();
        return name.Contains("claude", StringComparison.OrdinalIgnoreCase) ? "claude-code"
            : name.Contains("codex", StringComparison.OrdinalIgnoreCase) ? "codex" : name.Length > 0 ? name : "an MCP client";
    }

    public static JsonArray ToolList()
    {
        var list = new JsonArray();
        foreach (var (name, description, schema) in Tools)
            list.Add(new JsonObject
            {
                ["name"] = name, ["description"] = description, ["inputSchema"] = JsonNode.Parse(schema),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = ReadOnly.Contains(name), ["destructiveHint"] = false, ["openWorldHint"] = false,
                },
            });
        return list;
    }

    async Task HandleAsync(JsonObject message)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        if (id is null)
            return;
        JsonNode? result;
        switch (method)
        {
            case "initialize":
                var parameters = message["params"];
                _client = ClientName(parameters?["clientInfo"]);
                result = new JsonObject
                {
                    ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = Version },
                    ["instructions"] = Instructions,
                };
                break;
            case "tools/list":
                result = new JsonObject { ["tools"] = ToolList() };
                break;
            case "ping":
                result = new JsonObject();
                break;
            case "tools/call":
                var call = message["params"];
                var tool = call?["name"]?.GetValue<string>() ?? "";
                var args = call?["arguments"]?.DeepClone() as JsonObject ?? [];
                ToolResult answer;
                try
                {
                    answer = await CallAsync(tool, args).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    answer = ToolResult.Say($"Cluewren failed: {e.GetType().Name}: {e.Message}", true);
                }
                result = Render(answer);
                break;
            default:
                await SendAsync(new JsonObject
                {
                    ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"No method {method}" },
                }).ConfigureAwait(false);
                return;
        }
        await SendAsync(new JsonObject { ["id"] = id, ["result"] = result }).ConfigureAwait(false);
    }

    static JsonObject Render(ToolResult result)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result.Text } };
        foreach (var jpeg in result.Images)
            content.Add(new JsonObject
            {
                ["type"] = "image", ["data"] = Convert.ToBase64String(jpeg), ["mimeType"] = "image/jpeg",
            });
        return new JsonObject { ["content"] = content, ["isError"] = result.Error };
    }

    public async Task<ToolResult> CallAsync(string tool, JsonObject args)
    {
        (Task<ToolResult> Task, string Tool, DateTimeOffset Since) pending;
        lock (_gate)
        {
            if (tool == "wait")
            {
                if (_pending is null)
                    return ToolResult.Say("Nothing is going: no call is waiting for its result.");
                pending = _pending.Value;
            }
            else if (_pending is { } going)
                return ToolResult.Say($"{going.Tool} is still going ({Seconds(going.Since)} s): call wait for its result first.", true);
            else
            {
                pending = (Task.Run(() => DoAsync(tool, args)), tool, DateTimeOffset.UtcNow);
                _pending = pending;
            }
        }
        await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(waitSeconds))).ConfigureAwait(false);
        lock (_gate)
        {
            if (!pending.Task.IsCompleted)
                return ToolResult.Say($"{pending.Tool} is still going ({Seconds(pending.Since)} s): call wait for its result.");
            if (_pending?.Task == pending.Task)
                _pending = null;
        }
        return await pending.Task.ConfigureAwait(false);
    }

    static string Seconds(DateTimeOffset since) => ((int)(DateTimeOffset.UtcNow - since).TotalSeconds).ToString();

    async Task<ToolResult> DoAsync(string tool, JsonObject args)
    {
        await _runLock.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            if (tool == "start_run")
                return await StartAsync(args).ConfigureAwait(false);
            if (_run is not { } run)
                return NoRun();
            var endedBefore = run.Ended is not null;
            var result = await run.CallAsync(tool, args, _stopping.Token).ConfigureAwait(false);
            if (run.Ended is not null)
                await CloseRunAsync(remember: !endedBefore).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _runLock.Release();
        }
    }

    ToolResult NoRun()
    {
        if (_ended is not { } ended)
            return ToolResult.Say("No run has started: call start_run first.", true);
        _ended = null;
        return ToolResult.Say($"Run {ended.Run} has ended ({ended.Reason}); its report is {Path.Combine(ended.Folder, "report.json")}. "
            + "Tell the user its outcome; start_run starts another.", true);
    }

    // The watch's cleanup waits for the tool call in progress and closes only the run it watched: by then a later start_run
    // may have replaced it.
    async Task CloseWhenEndedAsync(PlaytestRun run)
    {
        await run.WatchAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            await _runLock.WaitAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            if (_run == run && run.Ended is not null)
                await CloseRunAsync(remember: true).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    async Task<ToolResult> StartAsync(JsonObject args)
    {
        if (_run is { Ended: null })
            return ToolResult.Say($"Run {_run.RunId} is going: finish it with report first.", true);
        _ended = null;
        await CloseRunAsync().ConfigureAwait(false);
        var key = JevKey ?? ApiKeys.Find(ApiKeys.TypeSafeKey);
        if (string.IsNullOrWhiteSpace(key))
            return ToolResult.Say($"The decision model's key is missing. Tell the user to put it in {ApiKeys.KeysFile} as "
                + $"{ApiKeys.TypeSafeKey}=<key>, and never to paste it here, then call start_run again. (Set in the environment "
                + "instead, it takes effect only once the agent session is restarted.)", true);
        var request = args["request"]?.GetValue<string>();
        var kind = args["kind"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(request) || kind is not ("verify" or "goal" or "explore"))
            return ToolResult.Say("start_run needs request and kind (verify, goal or explore).", true);
        _ = Task.Run(HarnessCompiler.Warm);
        var secret = Handshake.NewSecret();
        _bridge = await BridgeServer.StartAsync(secret).ConfigureAwait(false);
        var announced = new Discovery(_bridge.Url, Convert.ToBase64String(secret), Environment.ProcessId, DateTimeOffset.UtcNow);
        _discovery = (DiscoveryFile.Write(announced, DiscoveryFile.DefaultFolder), announced);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(connectSeconds));
        try
        {
            var link = await _bridge.NextLinkAsync(deadline.Token).ConfigureAwait(false);
            _jev = new JevClient(new JevOptions(key));
            _run = await PlaytestRun.OpenAsync(link, new RunRequest(request, kind, args["minutes"]?.GetValue<double>(),
                args["time_limit_minutes"]?.GetValue<double>(), args["level"]?.GetValue<string>()), _jev, _client, _stopping.Token).ConfigureAwait(false);
            _watch = CloseWhenEndedAsync(_run);
            return ToolResult.Say(_run.Opening);
        }
        catch (OperationCanceledException)
        {
            await CloseRunAsync().ConfigureAwait(false);
            return ToolResult.Say($"No editor connected within {connectSeconds:0} s. Tell the user to open the game's project in its editor with the "
                + "Cluewren plugin, then call start_run again.", true);
        }
        catch (Exception e)
        {
            await CloseRunAsync().ConfigureAwait(false);
            return ToolResult.Say($"The run didn't start: {e.Message}", true);
        }
    }

    async Task CloseRunAsync(bool remember = false)
    {
        if (_run is not null)
        {
            if (remember && _run.Ended is { } reason)
                _ended = (_run.RunId, reason, _run.RunFolder);
            await _run.DisposeAsync().ConfigureAwait(false);
        }
        _run = null;
        if (_jev is not null)
            await _jev.DisposeAsync().ConfigureAwait(false);
        _jev = null;
        if (_discovery is { } discovery)
            DiscoveryFile.Withdraw(discovery.Path, discovery.Announced);
        _discovery = null;
        if (_bridge is not null)
            await _bridge.DisposeAsync().ConfigureAwait(false);
        _bridge = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_watch is not null)
            await _watch.ConfigureAwait(false);
        await CloseRunAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _send.Dispose();
        _runLock.Dispose();
    }
}
