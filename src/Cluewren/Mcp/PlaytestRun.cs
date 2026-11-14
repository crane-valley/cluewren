using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cluewren.Bridge;
using Cluewren.Jev;
using Cluewren.Kit;
using Cluewren.Sandbox;

namespace Cluewren.Mcp;

public sealed record RunRequest(string Request, string Kind, double? Minutes = null, double? TimeLimitMinutes = null,
    string? Level = null);

public sealed record ToolResult(string Text, IReadOnlyList<byte[]> Images, bool Error = false)
{
    public static ToolResult Say(string text, bool error = false) => new(text, [], error);
}

public sealed partial class PlaytestRun : IAsyncDisposable
{
    public const double ProbeSeconds = 120;
    public const double ResearchSeconds = 600;
    public const double DefaultBudget = 1800;
    public const double IdleSeconds = 1200;
    public const double PlayGrace = 90;
    public const int MinJevDecisions = 3;
    const string DefaultExploreMinutes = "10";
    const int BriefFile = 12000;
    const int BriefRuns = 8;
    const int BriefHits = 24;
    const int BriefLevels = 40;
    const long MaxSearched = 2_000_000;
    const string NoReport = "No report";

    static readonly HashSet<string> StopWords = new("""
        verify that this those these there their they them then than when what where which while with without from into
        onto over under after before about does doesn't should would could must will have has been being player player's
        players game games level levels make makes made keep keeps goes going still stays same each every some only also
        more less most least much many first last other another again time times seconds minutes
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static readonly string[] SearchedExport = ["scenes", "prefabs", "data"];

    static readonly HashSet<string> UnrealText = new([".h", ".cpp", ".cs", ".txt"], StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> GodotText = new([".gd", ".cs", ".tscn", ".tres", ".gdshader"], StringComparer.OrdinalIgnoreCase);

    readonly BridgeLink _link;
    readonly RunRequest _request;
    readonly HarnessHost _host;
    readonly FrameRecorder _recorder;
    readonly string _project;
    readonly string _memory;
    readonly string _client;
    readonly double _budget;
    readonly double? _playSeconds;
    readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    readonly SemaphoreSlim _one = new(1, 1);
    DateTimeOffset _lastCall = DateTimeOffset.UtcNow;
    DateTimeOffset? _harnessWritten;
    DateTimeOffset? _playStarted;
    JsonObject? _report;
    int _described;
    string? _lookFailed;
    bool _busy;

    PlaytestRun(BridgeLink link, RunRequest request, HarnessHost host, FrameRecorder recorder, string project, string memory,
        string runFolder, string runId, string client)
    {
        (_link, _request, _host, _recorder, _project, _memory, RunFolder, RunId, _client) =
            (link, request, host, recorder, project, memory, runFolder, runId, client);
        _budget = request.TimeLimitMinutes is > 0 ? request.TimeLimitMinutes.Value * 60 : DefaultBudget;
        _playSeconds = request.Kind == "explore" ? (request.Minutes ?? double.Parse(DefaultExploreMinutes, CultureInfo.InvariantCulture)) * 60 : null;
    }

    public string RunId { get; }

    public string RunFolder { get; }

    public string? Ended { get; private set; }

    public string Opening { get; private set; } = "";

    // A C# Godot project compiles every .cs it finds outside dot folders, which a run's harness.cs must not be part of.
    public static string MemoryFolder(string project) => File.Exists(Path.Combine(project, "project.godot"))
        ? Path.Combine(project, ".godot", "cluewren")
        : IsUnreal(project) ? Path.Combine(project, "Saved", "Cluewren")
        : Path.Combine(project, "UserSettings", "Cluewren");

    static bool IsUnreal(string project) => Directory.Exists(project) && Directory.EnumerateFiles(project, "*.uproject").Any();

    public static async Task<PlaytestRun> OpenAsync(BridgeLink link, RunRequest request, IJevRelay jev, string client,
        SandboxMode sandbox, CancellationToken ct = default)
    {
        var project = await StartGameAsync(link, request.Level, ct).ConfigureAwait(false);
        var memory = MemoryFolder(project);
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var runFolder = Path.Combine(memory, "runs", id);
        Directory.CreateDirectory(runFolder);
        if (OperatingSystem.IsWindows())
            WindowsContainer.RetireLeftovers(Path.Combine(memory, "runs"), runFolder);
        var recorder = new FrameRecorder(link);
        var host = new HarnessHost(new HostOptions(link, runFolder)
        {
            Jev = jev, ProjectFolder = memory, Recorder = recorder, Mode = sandbox,
        });
        if (!string.IsNullOrEmpty(request.Level))
            host.Game.Level = request.Level;
        var run = new PlaytestRun(link, request, host, recorder, project, memory, runFolder, id, client);
        try
        {
            await run.PrepareAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await run.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(runFolder, true);
            throw;
        }
        return run;
    }

    // Godot answers context from the editor until its game runs, so the project is asked again once the game is up.
    // Nothing is written, filmed or announced before both answers name the folder the editor connected for.
    public static async Task<string> StartGameAsync(BridgeLink link, string? level, CancellationToken ct)
    {
        var project = link.Project ?? throw new InvalidOperationException("the editor connected without a verified project");
        var game = new Game(link);
        await ConfirmProjectAsync(game, project, ct).ConfigureAwait(false);
        await game.Request("session", json =>
        {
            json.WriteString("action", "start");
            if (!string.IsNullOrEmpty(level))
                json.WriteString("map", level);
        }, 120, ct).ConfigureAwait(false);
        await ConfirmProjectAsync(game, project, ct).ConfigureAwait(false);
        return project.Path;
    }

    static async Task ConfirmProjectAsync(Game game, VerifiedFolder project, CancellationToken ct)
    {
        var said = (await game.Context(fresh: true, ct).ConfigureAwait(false))["project_dir"].Text;
        if (string.IsNullOrEmpty(said) || !project.Holds(said))
            throw new InvalidOperationException($"the editor connected for {project.Path}, but the game says its project is "
                + $"{(string.IsNullOrEmpty(said) ? "none" : said)}");
        if (!project.Holds(project.Path))
            throw new InvalidOperationException($"{project.Path} is no longer the folder the editor connected for");
    }

    async Task PrepareAsync(CancellationToken ct)
    {
        await WaitForExportAsync(Path.Combine(_project, EngineTexts[_link.Engine].Export), ExportPatience, ct).ConfigureAwait(false);
        _link.Send(json => json.WriteString("type", "run_started"));
        await _recorder.FilmAsync(ct: ct).ConfigureAwait(false);
        var opening = await _host.OpeningAsync(ct).ConfigureAwait(false);
        var levels = await LevelBriefingAsync(ct).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(RunFolder, "INSTRUCTIONS.md"), InstructionsText(_link.Engine), Utf8);
        // Every run reads INSTRUCTIONS.md whole and pays for it again on each turn, so what only some requests need stays
        // out of it.
        foreach (var part in (string[])["KIT-PLAN.md", "KIT-FIGHT.md", "KIT-POINTER.md"])
            File.WriteAllText(Path.Combine(RunFolder, part), Resource(part), Utf8);
        File.WriteAllText(Path.Combine(RunFolder, "prompt.txt"), _request.Request, Utf8);
        var message = $"Request: {_request.Request}\n\nThe game: {_project}. This run's folder: {RunFolder}. Write "
            + $"{Path.Combine(RunFolder, "harness.cs")}, run it, revise, then report.\n\n{Briefing(levels, opening)}\n\n{LookingNote()}";
        File.WriteAllText(Path.Combine(RunFolder, "REQUEST.md"), message, Utf8);
        Opening = $"""
            Playtest run {RunId} started on {_project}: the game is open in the user's editor and paused between your calls.
            First read {Path.Combine(RunFolder, "INSTRUCTIONS.md")}, all of it: it is how to work and what
            the kit is. The request and its briefing follow (also in {Path.Combine(RunFolder, "REQUEST.md")}).

            {message}
            """;
    }

    // Unreal exports in a process of its own that starts as the editor connects, and a model that finds no export works
    // blind from probes for the whole run. summary.txt is written last. Past the patience the run goes on without it:
    // the export may be unable to start at all.
    public static async Task WaitForExportAsync(string folder, TimeSpan patience, CancellationToken ct)
    {
        var summary = Path.Combine(folder, "summary.txt");
        var deadline = DateTimeOffset.UtcNow + patience;
        while (!File.Exists(summary) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(250, ct).ConfigureAwait(false);
    }

    static readonly TimeSpan ExportPatience = TimeSpan.FromSeconds(90);

    static readonly UTF8Encoding Utf8 = new(false);

    static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"no resource {name}");
        using var reader = new StreamReader(stream, Utf8);
        return reader.ReadToEnd();
    }

    public static string SkillText() => Resource("Skill.md");

    static readonly Dictionary<string, (string Code, string Export, string Contents, string Kit)> EngineTexts = new()
    {
        [Unity.Id] = ("its code is under Assets/ and Packages/", "Library/CluewrenCodex",
            "the project as text: summary.txt first, then scenes/, prefabs/, data/, scripts.txt, build_scenes.txt", "KIT-UNITY.md"),
        [Godot.Id] = ("scripts, scenes (.tscn) and resources (.tres) are text files in it", ".godot/cluewren_codex",
            "summary.txt: the main scene, autoloads, input actions, classes and each scene's nodes", "KIT-GODOT.md"),
        [Unreal.Id] = ("C++ is under Source/ and settings under Config/; Blueprints are binary, so read them in the export", "Saved/CluewrenCodex",
            "the project as text: summary.txt first, then blueprints/, input/, data/, assets.txt, maps.txt", "KIT-UNREAL.md"),
    };

    string InstructionsText(string engine) => Resource("Instructions.md")
        .Replace("{engine}", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(engine))
        .Replace("{code_folders}", EngineTexts[engine].Code)
        .Replace("{export_folder}", EngineTexts[engine].Export)
        .Replace("{export_contents}", EngineTexts[engine].Contents)
        .Replace("{project}", _project).Replace("{memory}", _memory).Replace("{run}", RunFolder)
        .Replace("{probe_seconds}", ProbeSeconds.ToString(CultureInfo.InvariantCulture))
        .Replace("{research_minutes}", (ResearchSeconds / 60).ToString(CultureInfo.InvariantCulture))
        .Replace("{min_decisions}", MinJevDecisions.ToString(CultureInfo.InvariantCulture))
        .Replace("{max_frames}", FrameRecorder.MaxFrames.ToString(CultureInfo.InvariantCulture))
        .Replace("{max_fps}", FrameRecorder.MaxFps.ToString(CultureInfo.InvariantCulture))
        .Replace("{kit}", Resource("KIT.md") + Environment.NewLine + Resource(EngineTexts[engine].Kit));

    async Task<string?> LevelBriefingAsync(CancellationToken ct)
    {
        GameValue answer;
        try
        {
            answer = await _host.Game.Request("levels", (Action<Utf8JsonWriter>?)null, null, ct).ConfigureAwait(false);
        }
        catch (GameException)
        {
            return null;
        }
        var levels = answer["levels"].Strings();
        var playing = answer["playing"].Text ?? answer["open"].Text;
        if (levels.Count == 0 && string.IsNullOrEmpty(playing))
            return null;
        var own = "the editor's own level (the one its Play button plays)";
        var why = string.IsNullOrEmpty(_request.Level) ? own : "the run was asked to play it";
        var lines = new List<string> { $"This run plays {(string.IsNullOrEmpty(playing) ? own : playing)}: {why}." };
        if (levels.Count > 0)
        {
            var more = levels.Count > BriefLevels ? $" (and {levels.Count - BriefLevels} more)" : "";
            lines.Add($"The project's levels ({levels.Count}): {string.Join(", ", levels.Take(BriefLevels))}{more}.");
            if (string.IsNullOrEmpty(_request.Level))
                lines.Add("Your first decision: does the request mean a level other than this one (by its name, or by describing "
                    + "it: \"the harbor\", \"the 2D one\", \"level 3\")? If so, call restart_level with `level` set to that level's "
                    + "path from the list before anything else. If it names none, or the level it means is the one playing, play "
                    + "on here.");
        }
        lines.Add("restart_level with `level` plays that level from then on, and every restart (run_harness's too) starts it over.");
        return "## The level\n" + string.Join("\n", lines);
    }

    static string? Read(string path, int length = BriefFile)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text.Length <= length ? text : text[..length] + "\n... (cut)";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    string Briefing(string? levels, string opening)
    {
        var parts = new List<string>();
        if (levels is not null)
            parts.Add(levels);
        var (notes, lib) = (Read(Path.Combine(_memory, "notes.md")), Read(Path.Combine(_memory, "lib.cs")));
        if (notes is not null)
            parts.Add($"## notes.md (what earlier requests on this game verified)\n{notes}");
        if (lib is not null)
            parts.Add($"## lib.cs (compiled with every harness)\n```csharp\n{lib}\n```");
        if (notes is null && lib is null)
            parts.Add("## notes.md, lib.cs\nNone yet: the first request on this game.");
        var runs = EarlierRuns();
        if (runs.Count > 0)
            parts.Add("## Earlier runs (each folder keeps its harness versions, traces and report; use_harness copies one's harness here)\n"
                + string.Join("\n", runs.TakeLast(BriefRuns)));
        var hits = WordHits(_project, _request.Request);
        if (hits is not null)
            parts.Add("## Where the request's words appear in the project (files, with how often)\n" + string.Join("\n", hits));
        parts.Add($"## The live game now (looked at just before you started; it is paused)\n{opening}");
        return string.Join("\n\n", parts);
    }

    // The name comes from the model: it counts only if it is one of the folders listed, so it can't walk out of runs/.
    public static string? EarlierHarness(string memory, string thisRun, string? run)
    {
        var folders = Directory.GetDirectories(Path.Combine(memory, "runs"));
        var id = Path.GetFileName(run?.TrimEnd('/', '\\'));
        var folder = id == thisRun ? null : folders.FirstOrDefault(path => Path.GetFileName(path) == id);
        var harness = folder is null ? null : Path.Combine(folder, "harness.cs");
        return File.Exists(harness) ? harness : null;
    }

    List<string> EarlierRuns()
    {
        var folder = Path.Combine(_memory, "runs");
        var lines = new List<string>();
        foreach (var run in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(run);
            var prompt = Read(Path.Combine(run, "prompt.txt"), 300);
            if (id == RunId || prompt is null)
                continue;
            var (outcome, summary) = ("no report", "");
            try
            {
                using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "report.json")));
                outcome = report.RootElement.TryGetProperty("outcome", out var o) ? o.GetString() ?? outcome : outcome;
                summary = report.RootElement.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
            }
            lines.Add($"- runs/{id}: {prompt} -> {outcome}: {(summary.Length > 300 ? summary[..300] : summary)}");
        }
        return lines;
    }

    static string Stem(string word)
    {
        if (word.EndsWith("es", StringComparison.Ordinal) && word.Length >= 5
            && (word[..^2].EndsWith('s') || word[..^2].EndsWith('x') || word[..^2].EndsWith('z')
                || word[..^2].EndsWith("ch", StringComparison.Ordinal) || word[..^2].EndsWith("sh", StringComparison.Ordinal)))
            return word[..^2];
        foreach (var suffix in new[] { "ing", "ed", "s" })
            if (word.EndsWith(suffix, StringComparison.Ordinal) && word.Length - suffix.Length >= 4
                && !word.EndsWith("ss", StringComparison.Ordinal))
                return word[..^suffix.Length];
        return word;
    }

    [GeneratedRegex("[A-Za-z][A-Za-z']+")]
    private static partial Regex Words();

    static int Count(string text, string word)
    {
        var (count, at) = (0, 0);
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += word.Length;
        }
        return count;
    }

    public static IEnumerable<string> SearchedFiles(string project) =>
        File.Exists(Path.Combine(project, "project.godot")) ? GodotFiles(project)
        : IsUnreal(project) ? UnrealFiles(project) : UnityFiles(project);

    static IEnumerable<string> UnrealFiles(string project)
    {
        foreach (var folder in new[] { Path.Combine(project, "Source"), Path.Combine(project, "Saved", "CluewrenCodex") }.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                if (UnrealText.Contains(Path.GetExtension(file)))
                    yield return file;
    }

    static IEnumerable<string> GodotFiles(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
            if (GodotText.Contains(Path.GetExtension(file)))
                yield return file;
        foreach (var inner in Directory.EnumerateDirectories(folder))
            if (!Path.GetFileName(inner).StartsWith('.') && !inner.EndsWith(Path.Combine("addons", "cluewren"), StringComparison.OrdinalIgnoreCase))
                foreach (var file in GodotFiles(inner))
                    yield return file;
    }

    static IEnumerable<string> UnityFiles(string project)
    {
        var folders = new[] { Path.Combine(project, "Assets") }
            .Concat(SearchedExport.Select(name => Path.Combine(project, "Library", "CluewrenCodex", name)));
        foreach (var folder in folders.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    yield return file;
    }

    public static List<string>? WordHits(string project, string prompt)
    {
        var words = new List<string>();
        foreach (Match match in Words().Matches(prompt))
        {
            var word = match.Value.ToLowerInvariant();
            word = word.EndsWith("'s", StringComparison.Ordinal) ? word[..^2] : word;
            word = word.Trim('\'');
            if (word.Length >= 4 && !StopWords.Contains(word) && !words.Contains(Stem(word)))
                words.Add(Stem(word));
        }
        if (words.Count == 0)
            return null;
        var files = new List<(string Rel, Dictionary<string, int> Hits, string Name)>();
        foreach (var file in SearchedFiles(project))
        {
            try
            {
                if (new FileInfo(file).Length > MaxSearched)
                    continue;
                var text = File.ReadAllText(file).ToLowerInvariant();
                var rel = Path.GetRelativePath(project, file).Replace('\\', '/');
                files.Add((rel, words.ToDictionary(word => word, word => Count(text, word)), Path.GetFileName(rel).ToLowerInvariant()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (files.Count == 0)
            return null;
        var common = words.Where(word => files.Count >= 20 && files.Count(f => f.Hits[word] > 0) > 0.3 * files.Count).ToList();
        var rare = words.Except(common).ToList();
        var weight = rare.ToDictionary(word => word, word => Math.Log(files.Count / (1.0 + files.Count(f => f.Hits[word] > 0))) + 1);
        var scored = files.Select(f => (f.Rel, f.Hits, Found: rare.Count(w => f.Hits[w] > 0),
                Score: rare.Where(w => f.Hits[w] > 0).Sum(w => weight[w] * (f.Name.Contains(w, StringComparison.Ordinal) ? 1.5 : 1)),
                Total: rare.Sum(w => f.Hits[w])))
            .Where(f => f.Score > 0).OrderByDescending(f => f.Found).ThenByDescending(f => f.Score).ThenByDescending(f => f.Total)
            .ThenBy(f => f.Rel, StringComparer.Ordinal).ToList();
        var lines = scored.Take(BriefHits)
            .Select(f => $"- {f.Rel} ({string.Join(", ", rare.Where(w => f.Hits[w] > 0).Select(w => $"{w}: {f.Hits[w]}"))})").ToList();
        if (common.Count > 0)
            lines.Add($"(in most files, so not ranked: {string.Join(", ", common)})");
        if (scored.Count > BriefHits)
            lines.Add($"({scored.Count - BriefHits} more files have some of them: grep for the rest)");
        var missing = rare.Where(w => files.All(f => f.Hits[w] == 0)).ToList();
        if (missing.Count > 0)
            lines.Add($"(not in the project: {string.Join(", ", missing)})");
        return lines;
    }

    string LookingNote() => _recorder.Missing is not null
        ? $"## Looking at the picture\nThe game's frames can't be had in this run: {_recorder.Missing}. A request about how something "
          + "looks can't be settled: report it INCONCLUSIVE, with visual: true, saying so."
        : $"## Looking at the picture\nThe game is filmed while it plays ({FrameRecorder.DefaultFps} frames a second; run_harness "
          + $"frames_fps films an episode at up to {FrameRecorder.MaxFps}): a describer subagent looks at an episode's frames "
          + "(INSTRUCTIONS.md, \"Looking at the picture here\").";

    double Elapsed => (DateTimeOffset.UtcNow - _started).TotalSeconds;

    double? ResearchLeft()
    {
        if (_host.Episodes > 0)
            return null;
        if (_harnessWritten is null && File.Exists(Path.Combine(RunFolder, "harness.cs")))
            _harnessWritten = DateTimeOffset.UtcNow;
        var used = ((_harnessWritten ?? DateTimeOffset.UtcNow) - _started).TotalSeconds;
        return ResearchSeconds - used;
    }

    double? PlayLeft() => _playSeconds is null ? null
        : _playSeconds - (_playStarted is { } since ? (DateTimeOffset.UtcNow - since).TotalSeconds : 0);

    bool OutOfTime() => PlayLeft() < -PlayGrace;

    static string F(double value, int digits = 1) => value.ToString($"F{digits}", CultureInfo.InvariantCulture);

    public string Clock()
    {
        var used = Elapsed;
        var share = used / _budget;
        var line = $"[time: {F(used / 60)} of {F(_budget / 60, 0)} min used";
        var left = PlayLeft();
        if (left is { } play)
            line = $"[time: {F(used / 60)} min used; play: {F(Math.Max(0, _playSeconds!.Value - play) / 60)} of {F(_playSeconds.Value / 60, 0)} min used";
        var research = ResearchLeft();
        if (research is { } remaining)
        {
            line += $"; reading and probing: {F((ResearchSeconds - Math.Max(0, remaining)) / 60)} of {ResearchSeconds / 60:0} min used]";
            if (_report is null && remaining <= 0)
                return line + " The time for reading and probing is up: write harness.cs now from what you know and call run_harness. Probes are closed until an episode has played.";
            if (_report is null && remaining < 0.2 * ResearchSeconds)
                line += $" {F(remaining, 0)} s of reading and probing left: settle what the harness needs, then write it.";
        }
        else
            line += "]";
        if (left is { } remainingPlay)
        {
            if (_report is null && remainingPlay < 5)
                line += $" The exploration's play time is up: call report now with what the episodes so far showed (the run ends in {F(Math.Max(0, PlayGrace + remainingPlay), 0)} s).";
            else if (_report is null && remainingPlay < 0.3 * _playSeconds!.Value)
                line += $" {F(remainingPlay, 0)} s of play left: make the next episode the last, then report.";
            return line;
        }
        if (_report is null && share >= 0.9)
            line += " Time is nearly up: call report now with what the episodes so far show (INCONCLUSIVE if the request isn't settled).";
        else if (_report is null && share >= 0.7)
            line += " Wrap up: once an episode settles the request, report.";
        return line;
    }

    public async Task<ToolResult> CallAsync(string tool, JsonObject args, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Ended is not null)
                return ToolResult.Say($"This run has ended ({Ended}). Tell the user its outcome; start_run starts another.", true);
            _busy = true;
            _lastCall = DateTimeOffset.UtcNow;
            var result = await DispatchAsync(tool, args, ct).ConfigureAwait(false);
            return result with { Text = $"{result.Text}\n{Clock()}" };
        }
        catch (Exception e) when (e is GameException or GameDisconnectedException or SandboxException or IOException)
        {
            if (e is SandboxException)
                Console.Error.WriteLine($"cluewren: {tool}: the sandbox failed: {e.Message}");
            return ToolResult.Say($"{tool} failed: {e.GetType().Name}: {e.Message}\n{Clock()}", true);
        }
        finally
        {
            _busy = false;
            _lastCall = DateTimeOffset.UtcNow;
            _one.Release();
        }
    }

    static double? Number(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    static string? Text(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool Flag(JsonObject args, string name, bool fallback) =>
        args[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    async Task<ToolResult> DispatchAsync(string tool, JsonObject args, CancellationToken ct)
    {
        switch (tool)
        {
            case "probe":
                return ToolResult.Say(await ProbeAsync(Text(args, "code") ?? "", ct).ConfigureAwait(false));
            case "use_harness":
                if (EarlierHarness(_memory, RunId, Text(args, "run")) is not { } earlier)
                    return ToolResult.Say($"No earlier run {Text(args, "run")} with a harness: the briefing lists them.", true);
                File.Copy(earlier, Path.Combine(RunFolder, "harness.cs"), true);
                return ToolResult.Say($"harness.cs is now {earlier}'s. Edit what this request changes, or run it as it is.");
            case "check_harness":
                var only =args["only"]?.AsArray().Select(name => name?.GetValue<string>() ?? "").ToList();
                return ToolResult.Say(await _host.CheckHarnessAsync(Number(args, "seconds") ?? Runtime.CheckSeconds,
                    Flag(args, "restart", true), only, ct).ConfigureAwait(false));
            case "run_harness":
                return ToolResult.Say(await RunHarnessAsync(Number(args, "seconds") ?? 30, Flag(args, "restart", true),
                    Number(args, "frames_fps"), ct).ConfigureAwait(false));
            case "snapshot":
                return ToolResult.Say(await _host.SnapshotAsync(ct).ConfigureAwait(false));
            case "restart_level":
                return ToolResult.Say(await _host.RestartLevelAsync(Text(args, "level"), ct).ConfigureAwait(false));
            case "frames":
                return Frames(Number(args, "episode"), Number(args, "start"), Number(args, "end"), Number(args, "count"));
            case "report":
                return ToolResult.Say(await ReportAsync(args, ct).ConfigureAwait(false));
            default:
                return ToolResult.Say($"No tool {tool} here.", true);
        }
    }

    async Task<string> ProbeAsync(string code, CancellationToken ct)
    {
        var research = ResearchLeft();
        if (research <= 0)
            return $"Not run: the {ResearchSeconds / 60:0} minutes of reading and probing before the first episode are up. Write "
                + "harness.cs from what you know now and call run_harness; probes open again once an episode has played.";
        var seconds = research is { } left ? Math.Max(1, Math.Min(ProbeSeconds, left)) : ProbeSeconds;
        return (await _host.ProbeAsync(code, ct, seconds).ConfigureAwait(false)).Text;
    }

    async Task<string> RunHarnessAsync(double seconds, bool restart, double? fps, CancellationToken ct)
    {
        if (PlayLeft() is { } left)
        {
            if (left < 5)
                return $"No episode: the exploration's {_playSeconds!.Value / 60:0} minutes of play are used up. Call report now with what the episodes so far showed.";
            seconds = Math.Min(seconds, left);
        }
        var started = DateTimeOffset.UtcNow;
        if (fps is > 0)
            await _recorder.FilmAsync((int)fps.Value, ct).ConfigureAwait(false);
        try
        {
            var result = await _host.RunHarnessAsync(seconds, restart, fps, ct).ConfigureAwait(false);
            if (result.Played)
                _playStarted ??= started;
            return result.Text;
        }
        finally
        {
            if (fps is > 0)
                await _recorder.FilmAsync(ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    ToolResult Frames(double? episode, double? start, double? end, double? count)
    {
        var n = episode is > 0 ? (int)episode.Value : _host.Episodes;
        var picked = _recorder.Pick(n, start, end, count is null ? null : (int)count.Value);
        if (picked.Jpegs.Count > 0)
        {
            _described++;
            _lookFailed = null;
        }
        else
            _lookFailed = picked.Text;
        return new ToolResult(picked.Text, picked.Jpegs);
    }

    async Task<string> ReportAsync(JsonObject args, CancellationToken ct)
    {
        var outcome = (Text(args, "outcome") ?? "").Trim();
        var upper = outcome.ToUpperInvariant();
        if (_host.Episodes == 0 && upper != "INCONCLUSIVE")
            return "Not reported: no run_harness episode has been played yet. Write harness.cs and run it: the outcome must rest on an "
                + "episode in which Jev played (probes only check assumptions). Report INCONCLUSIVE if the game can't be played at all.";
        if (_host.MostDecisions < MinJevDecisions && upper != "INCONCLUSIVE")
            return $"Not reported: in no episode did Jev make {MinJevDecisions} decisions (the most: {_host.MostDecisions}). Options that "
                + "run alone are a script, not Jev playing. Give Jev real choices: at its decision points offer 2+ legal options that are "
                + "each a sensible thing to do now (e.g. which target, approach or action next, or keep going vs. something else), then run again.";
        if (Flag(args, "visual", false) && _described == 0 && upper is "CONFIRMED" or "DENIED")
            return _recorder.Missing is { } why
                ? $"Not reported: the outcome rests on how something looks, and the game's picture can't be looked at in this run ({why}). Report INCONCLUSIVE, saying so."
                : "Not reported: the outcome rests on how something looks, and no description of the episode's frames (frames, looked at by a "
                  + $"describer subagent) backs it{(_lookFailed is null ? "" : $" (the last one failed: {_lookFailed})")}. Describe the episode's "
                  + "moment (open questions), hold the description against what true and false would look like, then report.";
        _report = new JsonObject();
        foreach (var field in new[] { "outcome", "summary", "evidence", "findings", "visual" })
            if (args[field] is { } value)
                _report[field] = value.DeepClone();
        await FinishAsync(null, ct).ConfigureAwait(false);
        return "Reported. You can stop now.";
    }

    public async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (Ended is null)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var why = _link.Closed is { } closed ? $"the game went away ({closed})"
                    : OutOfTime() ? $"its {_playSeconds!.Value / 60:0} minutes of play were up"
                    : Elapsed > _budget ? "timed out"
                    : !_busy && (DateTimeOffset.UtcNow - _lastCall).TotalSeconds > IdleSeconds
                        ? $"the agent called no tool for {IdleSeconds / 60:0} minutes" : null;
                if (why is null)
                    continue;
                _host.EndEpisode();
                await _one.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Ended is null)
                        await FinishAsync(why, ct).ConfigureAwait(false);
                }
                finally
                {
                    _one.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task FinishAsync(string? stopped, CancellationToken ct)
    {
        var report = _report ?? new JsonObject
        {
            ["outcome"] = _request.Kind == "explore" ? "explored" : _request.Kind == "verify" ? "INCONCLUSIVE" : "not_achieved",
            ["summary"] = $"{NoReport} ({stopped ?? "the agent ended without one"}).",
            ["evidence"] = new JsonArray(),
        };
        var (inPlay, research) = (new JsonArray(), new JsonArray());
        foreach (var write in _host.Writes)
            (write["probe"] is not null && write["in_play"] is null ? research : inPlay).Add(write.DeepClone());
        report["run"] = RunId;
        report["project"] = _project;
        report["prompt"] = _request.Request;
        report["seconds"] = Math.Round(Elapsed, 1);
        report["episodes"] = _host.Episodes;
        report["stopped"] = stopped;
        report["client"] = _client;
        report["level_modified"] = inPlay;
        report["research_modified"] = research;
        File.WriteAllText(Path.Combine(RunFolder, "report.json"),
            report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
        Ended = stopped ?? "reported";
        try
        {
            await _host.Game.Request("record", json => json.WriteString("action", "stop"), 10, ct).ConfigureAwait(false);
            await _host.Game.Pause(false, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is GameException or GameDisconnectedException)
        {
        }
        _link.Send(json => json.WriteString("type", "result"));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync().ConfigureAwait(false);
        await _recorder.DisposeAsync().ConfigureAwait(false);
        _one.Dispose();
    }
}
