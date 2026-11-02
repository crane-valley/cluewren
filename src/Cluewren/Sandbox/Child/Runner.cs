using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Cluewren.Kit;

namespace Cluewren.Sandbox.Child;

// The bodies upstream keeps in Session: what must run beside the harness, so its code never runs in the parent.
sealed class Runner
{
    const int MinJevDecisions = 3;

    static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    readonly ProxyLink _link;
    readonly LineWriter _out;
    readonly ConsoleRouter _console;
    readonly string _runFolder;
    readonly Game _game;
    readonly IDecider? _jev;
    readonly int _maxDecisions;
    readonly string _carry;
    readonly CancellationTokenSource _stop = new();
    volatile bool _stopped;

    public Runner(ProxyLink link, LineWriter output, ConsoleRouter console, string runFolder, string? level, bool jev,
        int maxDecisions)
    {
        _link = link;
        _out = output;
        _console = console;
        _runFolder = runFolder;
        _game = new Game(link) { Level = level };
        _jev = jev ? new RemoteJev(link) : null;
        _maxDecisions = maxDecisions;
        _carry = Path.Combine(runFolder, "carry.json");
        Carry.Load(_game, _carry);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(runFolder, "bin", name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }

    public void Stop()
    {
        _stopped = true;
        _stop.Cancel();
    }

    public async Task Handle(JsonElement call)
    {
        var id = call.GetProperty("id").GetInt64();
        var method = call.GetProperty("method").GetString();
        var args = call.GetProperty("args");
        object? value = null;
        string? error = null;
        try
        {
            value = method switch
            {
                "ping" => "pong",
                "probe" => await Probe(args).ConfigureAwait(false),
                "run_harness" => await RunHarness(args).ConfigureAwait(false),
                "check" => await CheckHarness(args).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"unknown call {method}"),
            };
            if (method != "ping")
                Carry.Save(_game, _carry);
        }
        catch (Exception e)
        {
            error = Fmt.Cut(e.ToString(), 1500);
        }
        _out.Write((id, value, error), static (json, s) =>
        {
            json.WriteString("type", "reply");
            json.WriteNumber("id", s.id);
            if (s.error is not null)
                json.WriteString("error", s.error);
            json.WritePropertyName("value");
            Jsonable.Write(json, s.value, 0, round: false);
        });
    }

    static Assembly Load(string path) => AssemblyLoadContext.Default.LoadFromAssemblyPath(path);

    static Harness NewHarness(JsonElement args)
    {
        var type = Load(args.GetProperty("assembly").GetString()!).GetType(args.GetProperty("type").GetString()!)
                   ?? throw new TypeLoadException("the compiled assembly doesn't hold the harness class");
        return (Harness)Activator.CreateInstance(type)!;
    }

    void Mark(int from, string flag)
    {
        for (var i = from; i < _game.Writes.Count; i++)
            _game.Writes[i][flag] = true;
    }

    async Task<object?> Probe(JsonElement args)
    {
        var seconds = args.GetProperty("seconds").GetDouble();
        var keepRunning = args.TryGetProperty("keep_running", out var keep) && keep.GetBoolean();
        var capture = new StringBuilder();
        _console.Listen(capture);
        var written = _game.Writes.Count;
        object? value = null;
        var late = "";
        var ct = _stop.Token;
        _game.RunToken = ct;
        Func<double, bool, IReadOnlyList<string>?, CancellationToken, Task<string>>? check = null;
        if (args.TryGetProperty("harness", out var harness) && harness.ValueKind == JsonValueKind.Object)
        {
            var (assembly, type) = (harness.GetProperty("assembly").GetString()!, harness.GetProperty("type").GetString()!);
            check = (limit, restart, only, token) =>
            {
                var instance = (Harness)Activator.CreateInstance(Load(assembly).GetType(type)!)!;
                return Runtime.Check(_game, instance, limit, restart, only, token);
            };
        }
        else if (args.TryGetProperty("harness_problem", out var problem))
        {
            var text = problem.GetString()!;
            check = (_, _, _, _) => Task.FromResult(text);
        }
        var globals = new ProbeGlobals(_game, check, ct);
        var entry = Load(args.GetProperty("assembly").GetString()!).GetType(args.GetProperty("type").GetString()!)!
            .GetMethod(args.GetProperty("method").GetString()!, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var work = Task.Run(async () =>
        {
            try
            {
                var submission = new object?[] { globals, null };
                value = await ((Task<object>)entry.Invoke(null, [submission])!).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                capture.Append((e is TargetInvocationException { InnerException: { } inner } ? inner : e).ToString());
                capture.Append('\n');
            }
        });
        try
        {
            await _game.Pause(false, ct).ConfigureAwait(false);
            var limit = Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            if (await Task.WhenAny(work, limit).ConfigureAwait(false) != work)
                late = $"\nTHE PROBE TOOK MORE THAN {Fmt.F(seconds, 0)} s AND WAS CUT OFF HERE: the keys were let go and "
                       + "the game paused; what it printed until then is above. Keep a probe to one question's worth; "
                       + "what takes long (whole walks, a fight to the end) belongs in run_harness.";
        }
        finally
        {
            _console.Listen(null);
            try
            {
                _game.ReleaseAll();
                if (!keepRunning)
                    await _game.Pause(true).ConfigureAwait(false);
            }
            catch (Exception e) when (e is GameException or GameDisconnectedException or OperationCanceledException)
            {
            }
        }
        Mark(written, "probe");
        var output = capture.ToString();
        if (value is not null)
            output += (output.Length == 0 || output.EndsWith('\n') ? "" : "\n")
                      + (value is string text ? text : Fmt.Cut(Fmt.Json(value), 8000));
        return new Dictionary<string, object?>
        {
            ["text"] = output + late, ["writes"] = _game.Writes.Skip(written).ToList(), ["cut_off"] = late.Length > 0,
        };
    }

    async Task<object?> CheckHarness(JsonElement args)
    {
        var only = args.TryGetProperty("only", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(n => n.GetString() ?? "").ToList() : null;
        var harness = NewHarness(args);
        var written = _game.Writes.Count;
        _game.RunToken = _stop.Token;
        var text = await Runtime.Check(_game, harness, args.GetProperty("seconds").GetDouble(),
            args.GetProperty("restart").GetBoolean(), only, _stop.Token).ConfigureAwait(false);
        try
        {
            _game.ReleaseAll();
            await _game.Pause(true).ConfigureAwait(false);
        }
        catch (Exception e) when (e is GameException or GameDisconnectedException)
        {
        }
        Mark(written, "probe");
        return new Dictionary<string, object?> { ["text"] = text, ["writes"] = _game.Writes.Skip(written).ToList() };
    }

    static Dictionary<string, object?> NoEpisode(string text, bool started = false) => new()
    {
        ["text"] = text, ["played"] = false, ["started"] = started,
    };

    async Task<object?> RunHarness(JsonElement args)
    {
        var (n, seconds) = (args.GetProperty("n").GetInt32(), args.GetProperty("seconds").GetDouble());
        var restart = args.GetProperty("restart").GetBoolean();
        var pawnSeen = args.GetProperty("pawn_seen").GetBoolean();
        var mostDecisions = args.GetProperty("most_decisions").GetInt32();
        var trace = Path.Combine(_runFolder, $"trace{n}.jsonl");
        Harness harness;
        try
        {
            harness = NewHarness(args);
        }
        catch (Exception e)
        {
            return NoEpisode("The harness doesn't load:\n" + (e is TargetInvocationException { InnerException: { } inner } ? inner : e));
        }
        Progress? progress = !restart && args.TryGetProperty("progress", out var saved) && saved.ValueKind == JsonValueKind.Object
            ? saved.Deserialize<Progress>(Snake) : null;
        var note = "";
        Func<CancellationToken, ValueTask>? resume = null;
        EpisodeResult result;
        var ct = _stop.Token;
        var written = _game.Writes.Count;
        _link.Status($"Trying the harness (run {n})");
        try
        {
            if (restart)
            {
                if (await _game.Restart(harness.StartMap, 10.0, ct).ConfigureAwait(false))
                    pawnSeen = true;
                else
                    note = "Note: after the restart there was no player pawn within 10 s (a main menu? a loading screen?): "
                           + "Setup ran anyway. Set StartMap to the gameplay level, or get through the menu in Setup.\n";
                await _game.Pause(false, ct).ConfigureAwait(false);
            }
            else
            {
                bool alive;
                try
                {
                    alive = (await _game.Context(fresh: true, ct).ConfigureAwait(false))["character"].Truthy;
                }
                catch (GameException)
                {
                    alive = true;
                }
                if (alive)
                    pawnSeen = true;
                if (!alive && pawnSeen)
                    return NoEpisode("Not played: there is no player pawn to continue with (dead with no respawn? a menu or "
                                     + "loading screen?). Find out what the screen shows (a probe: game.Screen(), game.Ui()), "
                                     + "get through a menu in a probe, or run_harness with a restart (the level starts over; "
                                     + "Setup must get through any menu).");
                await _game.Pause(true, ct).ConfigureAwait(false);
                resume = token => Unpause(token);
            }
            var runtime = new Runtime(_game, harness, trace, _jev, seconds, _link.Status, () => _stopped, resume, progress,
                maxDecisions: _maxDecisions);
            await _link.Mark("begin", n).ConfigureAwait(false);
            try
            {
                result = await runtime.Run(ct).ConfigureAwait(false);
            }
            finally
            {
                await _link.Mark("end", n).ConfigureAwait(false);
            }
            if (result.Reason == "stopped")
                note += "Note: the episode was stopped from outside: it ended there, and the game is paused where it was.\n";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return NoEpisode("The runtime failed:\n" + e, started: true);
        }
        finally
        {
            try
            {
                _game.ReleaseAll();
                await _game.Pause(true).ConfigureAwait(false);
            }
            catch (Exception e) when (e is GameException or GameDisconnectedException)
            {
            }
            _link.Status("Looking at the results");
        }
        var summary = Runtime.Summarize(trace, errorLog: true);
        await File.WriteAllTextAsync(Path.Combine(_runFolder, $"trace{n}_summary.txt"), summary).ConfigureAwait(false);
        result.Progress.Episode = n;
        mostDecisions = Math.Max(mostDecisions, result.Decisions);
        try
        {
            if (result.Reason == "the player is gone"
                || (await _game.Context(fresh: true).ConfigureAwait(false))["character"].Truthy)
                pawnSeen = true;
        }
        catch (Exception e) when (e is GameException or GameDisconnectedException)
        {
        }
        if (mostDecisions < MinJevDecisions)
            note += $"Note: Jev made {result.Decisions} decisions in this episode; a report needs an episode with at least "
                    + $"{MinJevDecisions} (decisions among 2+ options), so this one can't settle the request.\n";
        return new Dictionary<string, object?>
        {
            ["text"] = note, ["summary"] = summary, ["played"] = true, ["started"] = true, ["reason"] = result.Reason,
            ["state"] = result.Status?.State, ["note"] = result.Status?.Note, ["decisions"] = result.Decisions,
            ["most_decisions"] = mostDecisions, ["pawn_seen"] = pawnSeen, ["progress"] = result.Progress,
            ["writes"] = _game.Writes.Skip(written).ToList(), ["ran_hz"] = result.Ticks.RanHz, ["stats"] = result.Stats,
        };
    }

    async ValueTask Unpause(CancellationToken ct) => await _game.Pause(false, ct).ConfigureAwait(false);
}

// Upstream's Game lives as long as the run. Here each revision gets a new process, and what a walk learned must not die
// with it: the sonar map, the measured look scale and slide.
static class Carry
{
    public static void Save(Game game, string path)
    {
        var maps = game.NavMaps.ToDictionary(pair => pair.Key, pair => new Dictionary<string, object?>
        {
            ["hits"] = Marks(pair.Value.Hits), ["solid"] = Marks(pair.Value.Solid), ["guesses"] = Marks(pair.Value.Guesses),
            ["passes"] = Marks(pair.Value.Passes),
            ["lingered"] = pair.Value.Lingered.Select(m => new object[] { m.Key.Item1, m.Key.Item2, m.Key.Item3, m.Value.Spent, m.Value.When }).ToList(),
            ["linger_t"] = pair.Value.LingerT, ["version"] = pair.Value.Version,
        });
        var carry = new Dictionary<string, object?>
        {
            ["look_x"] = game.Look.X, ["look_y"] = game.Look.Y, ["coast"] = game.CoastSeconds, ["keys"] = game.Keys,
            ["maps"] = maps,
        };
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Jsonable.Encode(carry, round: false));
        File.Move(temporary, path, overwrite: true);
    }

    static List<object[]> Marks(Dictionary<(int, int, int), double> marks) =>
        marks.Select(mark => new object[] { mark.Key.Item1, mark.Key.Item2, mark.Key.Item3, mark.Value }).ToList();

    static Dictionary<(int, int, int), double> Marks(JsonElement marks)
    {
        var read = new Dictionary<(int, int, int), double>();
        foreach (var mark in marks.EnumerateArray())
            read[(mark[0].GetInt32(), mark[1].GetInt32(), mark[2].GetInt32())] = mark[3].GetDouble();
        return read;
    }

    public static void Load(Game game, string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var c = new GameValue(document.RootElement);
            (game.Look.X, game.Look.Y, game.CoastSeconds) = (c["look_x"].Number, c["look_y"].Number, c["coast"].Number);
            foreach (var (name, key) in c["keys"].Fields)
                game.Keys[name] = key.Text ?? game.Keys[name];
            foreach (var (world, saved) in c["maps"].Fields)
            {
                var map = new NavMap
                {
                    Hits = Marks(saved["hits"].Element), Solid = Marks(saved["solid"].Element),
                    Guesses = Marks(saved["guesses"].Element), Passes = Marks(saved["passes"].Element),
                    LingerT = saved["linger_t"].Number ?? 0, Version = (int)(saved["version"].Number ?? 0),
                };
                foreach (var mark in saved["lingered"].Items)
                    map.Lingered[((int)mark[0], (int)mark[1], (int)mark[2])] = (mark[3], mark[4]);
                game.NavMaps[world] = map;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine($"carry.json ignored: {e.Message}");
        }
    }
}
