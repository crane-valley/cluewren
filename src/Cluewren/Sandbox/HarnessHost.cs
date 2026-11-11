using System.Text.Json;
using System.Text.Json.Nodes;
using Cluewren.Jev;
using Cluewren.Kit;

namespace Cluewren.Sandbox;

public sealed record HostOptions(IGameLink Link, string RunFolder)
{
    public IJevRelay? Jev { get; init; }

    public string? ProjectFolder { get; init; }

    public Action<string>? Say { get; init; }

    public int MaxJevDecisions { get; init; } = 5000;

    public long MemoryLimitBytes { get; init; } = 2L << 30;

    public SandboxMode Mode { get; init; } = SandboxMode.Auto;

    public double ProbeSeconds { get; init; } = 120;

    public IEpisodeRecorder? Recorder { get; init; }
}

public sealed record ProbeResult(string Text, string Sandbox, IReadOnlyList<CompileProblem> Problems)
{
    public bool Compiled => Problems.Count == 0;
}

public sealed record HarnessResult(string Text, bool Played, int Episode, string? Reason, string? State, string? Note,
    int Decisions, string Summary, string Sandbox, IReadOnlyList<CompileProblem> Problems, JsonElement? Stats);

public sealed class HarnessHost : IAsyncDisposable
{
    public const double MaxEpisode = 360;
    const int MaxOutput = 12000;
    const int MaxSummary = 14000;

    readonly HostOptions _options;
    readonly IGameLink _link;
    readonly Game _game;
    readonly HarnessCompiler _compiler;
    readonly string _runFolder;
    readonly string _harness;
    readonly string? _lib;
    readonly Action<string> _say;
    readonly SemaphoreSlim _one = new(1, 1);
    SandboxSession? _running;
    FileStream? _claim;
    string? _downgrade;
    string? _progress;
    int _episodes, _played, _mostDecisions, _decisions, _writesAfterPlay;
    bool _pawnSeen;

    public HarnessHost(HostOptions options)
    {
        _options = options;
        _link = options.Link;
        _game = new Game(_link);
        _runFolder = Path.GetFullPath(options.RunFolder);
        Directory.CreateDirectory(_runFolder);
        _compiler = new HarnessCompiler(_runFolder);
        _harness = Path.Combine(_runFolder, "harness.cs");
        var lib = Path.Combine(Path.GetFullPath(options.ProjectFolder ?? _runFolder), "lib.cs");
        _lib = lib;
        _say = options.Say ?? _link.Status;
    }

    public string Sandbox { get; private set; } = "";

    public string? SandboxNote => _downgrade;

    public int Episodes => _episodes;

    public int MostDecisions => _mostDecisions;

    public int JevDecisions => Volatile.Read(ref _decisions);

    public List<JsonObject> Writes { get; } = [];

    public string? Level => _game.Level;

    public Game Game => _game;

    ChildSpec Spec() => new(ChildRuntime.Dotnet(), Path.Combine(ChildRuntime.Materialize(), "Cluewren.Child.dll"),
        _runFolder, _options.MemoryLimitBytes);

    bool SpendDecision() => Interlocked.Increment(ref _decisions) <= _options.MaxJevDecisions;

    async Task<SandboxSession> Attach(ISandboxProcess process, CancellationToken ct)
    {
        SandboxSession? session = null;
        try
        {
            session = new SandboxSession(process, _link, _options.Jev, SpendDecision, _options.Recorder, _say,
                Path.Combine(_runFolder, "sandbox.log"));
            session.Init(_runFolder, _game.Level, Math.Max(0, _options.MaxJevDecisions - JevDecisions));
            if ((await session.Call("ping", null, 60, ct).ConfigureAwait(false)).GetString() != "pong")
                throw new SandboxException("the child didn't answer its first call");
            return session;
        }
        catch
        {
            if (session is null)
                process.Dispose();
            else
                session.Dispose();
            throw;
        }
    }

    async Task<SandboxSession> StartChild(CancellationToken ct)
    {
        var spec = Spec();
        var mode = _downgrade is not null ? SandboxMode.Process : _options.Mode;
        if (OperatingSystem.IsWindows() && mode != SandboxMode.Process)
            _claim ??= WindowsContainer.Claim(_runFolder);
        var (process, note) = SandboxStarter.Start(spec, mode);
        _downgrade ??= note;
        SandboxSession session;
        try
        {
            session = await Attach(process, ct).ConfigureAwait(false);
        }
        catch (SandboxException e) when (_options.Mode == SandboxMode.Auto && process.Kind != "process")
        {
            _downgrade = SandboxStarter.Downgraded(e.Message);
            var (plain, _) = SandboxStarter.Start(spec, SandboxMode.Process);
            session = await Attach(plain, ct).ConfigureAwait(false);
        }
        Sandbox = session.Kind;
        return session;
    }

    string WithNote(string text) => _downgrade is null ? text : $"Note: {_downgrade}\n{text}";

    static string Cut(string text, int length) =>
        text.Length <= length ? text
            : text[..(length / 2)] + $"\n... ({text.Length - length} characters cut) ...\n" + text[^(length / 2)..];

    void Keep(JsonElement value)
    {
        if (!value.TryGetProperty("writes", out var writes))
            return;
        foreach (var write in writes.EnumerateArray())
            if (JsonNode.Parse(write.GetRawText()) is JsonObject node)
                Writes.Add(node);
    }

    async Task Abandon(SandboxSession session)
    {
        session.Dispose();
        try
        {
            _game.ReleaseAll();
            await _game.Pause(true).ConfigureAwait(false);
        }
        catch (Exception e) when (e is GameException or GameDisconnectedException or OperationCanceledException)
        {
        }
    }

    public async Task<ProbeResult> ProbeAsync(string code, CancellationToken ct = default, double? seconds = null,
        bool keepRunning = false)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var compiled = _compiler.CompileProbe(code, _lib);
            if (!compiled.Ok)
                return new ProbeResult(WithNote("The probe doesn't compile:\n" + compiled.ProblemText), Sandbox, compiled.Problems);
            var limit = seconds ?? _options.ProbeSeconds;
            var args = new Dictionary<string, object?>
            {
                ["assembly"] = compiled.Assembly, ["type"] = compiled.Type, ["method"] = compiled.Method, ["seconds"] = limit,
                ["keep_running"] = keepRunning,
            };
            if (code.Contains("CheckHarness", StringComparison.Ordinal))
            {
                var harness = _compiler.CompileHarness(_harness, _lib);
                if (harness.Ok)
                    args["harness"] = new Dictionary<string, object?> { ["assembly"] = harness.Assembly, ["type"] = harness.Type };
                else
                    args["harness_problem"] = "The harness doesn't load:\n" + harness.ProblemText;
            }
            var session = await StartChild(ct).ConfigureAwait(false);
            _running = session;
            try
            {
                var value = await session.Call("probe", args, limit + 30, ct).ConfigureAwait(false);
                Keep(value);
                var text = value.GetProperty("text").GetString() ?? "";
                return new ProbeResult(WithNote(Cut(text.Length > 0 ? text : "(no output)", MaxOutput)), Sandbox, []);
            }
            catch (SandboxException e)
            {
                return new ProbeResult(WithNote($"The sandbox failed: {e.Message}"), Sandbox, []);
            }
            finally
            {
                _running = null;
                await Abandon(session).ConfigureAwait(false);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<string> CheckHarnessAsync(double seconds = Runtime.CheckSeconds, bool restart = true,
        IReadOnlyList<string>? only = null, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var compiled = _compiler.CompileHarness(_harness, _lib);
            if (!compiled.Ok)
                return WithNote("The harness doesn't load:\n" + compiled.ProblemText);
            var session = await StartChild(ct).ConfigureAwait(false);
            _running = session;
            try
            {
                var value = await session.Call("check", new Dictionary<string, object?>
                {
                    ["assembly"] = compiled.Assembly, ["type"] = compiled.Type, ["seconds"] = seconds, ["restart"] = restart,
                    ["only"] = only,
                }, Runtime.CheckMaxOptions * (seconds * 3 + 10) + 90, ct).ConfigureAwait(false);
                Keep(value);
                return WithNote(value.GetProperty("text").GetString() ?? "");
            }
            catch (SandboxException e)
            {
                return WithNote($"The sandbox failed: {e.Message}");
            }
            finally
            {
                _running = null;
                await Abandon(session).ConfigureAwait(false);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    HarnessResult NoEpisode(string text, IReadOnlyList<CompileProblem>? problems = null) =>
        new(WithNote(text), false, _episodes, null, null, null, 0, "", Sandbox, problems ?? [], null);

    public async Task<HarnessResult> RunHarnessAsync(double seconds, bool restart = true, double? framesFps = null,
        CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_harness))
                return NoEpisode("harness.cs doesn't exist yet: write it first.");
            var compiled = _compiler.CompileHarness(_harness, _lib);
            if (!compiled.Ok)
                return NoEpisode("The harness doesn't load:\n" + compiled.ProblemText, compiled.Problems);
            seconds = Math.Max(5.0, Math.Min(seconds, MaxEpisode));
            var n = ++_episodes;
            _played++;
            if (!restart)
            {
                foreach (var write in Writes.Skip(_writesAfterPlay))
                    if (write["probe"] is not null)
                        write["in_play"] = true;
            }
            JsonElement? progress = null;
            if (!restart && _progress is not null)
            {
                using var saved = JsonDocument.Parse(_progress);
                progress = saved.RootElement.Clone();
            }
            var session = await StartChild(ct).ConfigureAwait(false);
            _running = session;
            using var stop = ct.Register(session.Stop);
            try
            {
                var value = await session.Call("run_harness", new Dictionary<string, object?>
                {
                    ["assembly"] = compiled.Assembly, ["type"] = compiled.Type, ["n"] = n, ["seconds"] = seconds,
                    ["restart"] = restart, ["pawn_seen"] = _pawnSeen, ["most_decisions"] = _mostDecisions,
                    ["progress"] = progress, ["frames_fps"] = framesFps,
                }, seconds * 3 + 150, CancellationToken.None).ConfigureAwait(false);
                Keep(value);
                var note = value.GetProperty("text").GetString() ?? "";
                if (!value.GetProperty("played").GetBoolean())
                {
                    if (!value.GetProperty("started").GetBoolean())
                    {
                        _episodes--;
                        _played--;
                    }
                    return NoEpisode(note);
                }
                File.Copy(_harness, Path.Combine(_runFolder, $"harness{n}.cs"), overwrite: true);
                _mostDecisions = value.GetProperty("most_decisions").GetInt32();
                _pawnSeen = value.GetProperty("pawn_seen").GetBoolean();
                _progress = value.GetProperty("progress").GetRawText();
                _writesAfterPlay = Writes.Count;
                var summary = value.GetProperty("summary").GetString() ?? "";
                var state = value.GetProperty("state").GetString();
                return new HarnessResult(
                    WithNote(note + $"Episode {n} (trace{n}.jsonl; harness saved as harness{n}.cs)\n" + Cut(summary, MaxSummary)),
                    true, n, value.GetProperty("reason").GetString(), state, value.GetProperty("note").GetString(),
                    value.GetProperty("decisions").GetInt32(), summary, Sandbox, [], value.GetProperty("stats"));
            }
            catch (SandboxException e)
            {
                return NoEpisode($"The sandbox failed: {e.Message}");
            }
            finally
            {
                _running = null;
                await Abandon(session).ConfigureAwait(false);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    public void EndEpisode() => _running?.Stop();

    public async Task<string> SnapshotAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        var seen = new Dictionary<string, object?>();
        try
        {
            await _game.Pause(false, ct).ConfigureAwait(false);
            var p = await _game.Pose(ct).ConfigureAwait(false);
            seen["player"] = new Dictionary<string, object?>
            {
                ["pawn"] = await _game.Pawn(ct).ConfigureAwait(false), ["at"] = p.At.Rounded(), ["yaw"] = Fmt.Round(p.Yaw),
                ["pitch"] = Fmt.Round(p.Pitch), ["speed"] = Fmt.Round(p.Velocity.Length),
            };
            var view = await _game.View(new Dictionary<string, object?> { ["max_count"] = 16, ["max_distance"] = 4000 }, ct)
                .ConfigureAwait(false);
            string[] kept = ["name", "class", "distance", "screen_x", "screen_y", "label"];
            seen["visible"] = view["visible"].Items.Select(thing => kept.Where(key => !thing[key].IsNull)
                .ToDictionary(key => key, key => (object?)thing[key])).ToList();
            seen["held"] = view["held"].Items.Select(thing => thing["name"].Text).ToList();
            seen["body"] = new[] { "on_ground", "falling", "crouched" }.ToDictionary(key => key, key => (object?)view["body"][key]);
            seen["screen"] = await _game.Screen(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is GameException or GameDisconnectedException)
        {
            seen["error"] = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            try
            {
                await _game.Pause(true).ConfigureAwait(false);
            }
            catch (Exception e) when (e is GameException or GameDisconnectedException)
            {
            }
            _one.Release();
        }
        return Cut(Fmt.Json(seen), 10000);
    }

    public async Task<string> OpeningAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        var seen = new Dictionary<string, object?>();
        async Task Part(string key, Func<Task<object?>> read)
        {
            try
            {
                seen[key] = await read().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                seen[key] = $"(couldn't read: {e.GetType().Name}: {e.Message})";
            }
        }
        try
        {
            await _game.Pause(false, ct).ConfigureAwait(false);
            var context = await _game.Context(fresh: true, ct).ConfigureAwait(false);
            seen["level"] = context["world"].Text;
            seen["player"] = context["character"].Truthy ? context["character"].Text
                : "(no player pawn: a menu, a loading screen, dead?)";
            seen["game_time"] = context["game_time"].Number is { } time ? Fmt.Round(time, 1) : null;
            var controls = new Dictionary<string, object?>();
            foreach (var control in context["controls"].Items)
                controls[control["name"].Text ?? ""] = control["keys"].Strings();
            seen["controls"] = controls;
            if (context["input"]["notes"].Count > 0)
                seen["input_notes"] = context["input"]["notes"].Strings();
            if (context["character"].Truthy)
            {
                await Part("pose", async () =>
                {
                    var p = await _game.Pose(ct).ConfigureAwait(false);
                    return new Dictionary<string, object?>
                    {
                        ["at"] = p.At.Rounded(), ["yaw"] = Fmt.Round(p.Yaw), ["pitch"] = Fmt.Round(p.Pitch),
                        ["speed"] = Fmt.Round(p.Velocity.Length),
                    };
                }).ConfigureAwait(false);
                await Part("sees", async () =>
                {
                    var view = await _game.View(new Dictionary<string, object?> { ["max_count"] = 16, ["max_distance"] = 4000 }, ct)
                        .ConfigureAwait(false);
                    string[] kept = ["name", "class", "distance", "label"];
                    return new Dictionary<string, object?>
                    {
                        ["visible"] = view["visible"].Items.Select(thing => kept.Where(key => !thing[key].IsNull)
                            .ToDictionary(key => key, key => (object?)thing[key])).ToList(),
                        ["held"] = view["held"].Items.Select(thing => thing["name"].Text).ToList(),
                    };
                }).ConfigureAwait(false);
            }
            await Part("screen_texts", async () => (await _game.Screen(ct).ConfigureAwait(false)).Take(60)
                .ToDictionary(pair => pair.Key, pair => pair.Value)).ConfigureAwait(false);
            await Part("ui", async () =>
            {
                string[] kept = ["id", "kind", "label", "enabled"];
                return (await _game.Ui(ct).ConfigureAwait(false)).Take(40).Select(element => kept
                    .Where(key => !element[key].IsNull && element[key].Text != "")
                    .ToDictionary(key => key, key => (object?)element[key])).ToList();
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is GameException or GameDisconnectedException)
        {
            seen["error"] = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            try
            {
                await _game.Pause(true).ConfigureAwait(false);
            }
            catch (Exception e) when (e is GameException or GameDisconnectedException)
            {
            }
            _one.Release();
        }
        return Cut(string.Join("\n", seen.Select(pair => $"{pair.Key}: {Fmt.Json(pair.Value)}")), 10000);
    }

    public async Task<string> RestartLevelAsync(string? level = null, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var where = level is not null ? $" in {level}" : "";
            try
            {
                var ok = await _game.Restart(level, ct: ct).ConfigureAwait(false);
                await _game.Pause(true, ct).ConfigureAwait(false);
                if (level is not null)
                    _game.Level = level;
                return ok ? $"Restarted{where}." : $"Restarted{where}, but no player appeared within 20 s.";
            }
            catch (Exception e) when (e is GameException or GameDisconnectedException)
            {
                return $"Restart failed: {e.Message}" + (level is not null ? $" (still playing {_game.Level ?? "the level it was"})" : "");
            }
        }
        finally
        {
            _one.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _running?.Dispose();
        _one.Dispose();
        if (OperatingSystem.IsWindows() && _claim is not null)
            WindowsContainer.Release(_claim, _runFolder);
        return default;
    }
}
