using System.Diagnostics;
using System.Text.Json;

namespace Cluewren.Kit;

public sealed class Counter : Dictionary<string, int>
{
    public void Add(string key) => this[key] = this.GetValueOrDefault(key) + 1;

    public int Get(string key) => this.GetValueOrDefault(key);
}

public sealed record StepReport(int Steps, double MedianMs, double P90Ms, double MaxMs, double WaitsMean, int WaitsMax);

public sealed record TickReport(double AskedHz, double? RanHz);

public readonly record struct LatencySummary(int Count, double P50Ms, double P95Ms, double MaxMs);

public sealed class LatencySeries(int capacity = 1 << 16)
{
    readonly long[] _samples = new long[capacity];
    int _count;

    public void Add(long stopwatchTicks) => _samples[_count++ % _samples.Length] = stopwatchTicks;

    public LatencySummary Summarize()
    {
        var count = Math.Min(_count, _samples.Length);
        if (count == 0)
            return default;
        var sorted = _samples[..count];
        Array.Sort(sorted);
        return new LatencySummary(count, Ms(sorted[(count - 1) / 2]), Ms(sorted[(count - 1) * 95 / 100]), Ms(sorted[^1]));
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

public sealed record DecisionStats(int Asked, int Answered, int Errors, int Started, int Kept, int Switched, int Held,
    int Dropped, long InputTokens, long OutputTokens, LatencySummary Latency, string? LastError)
{
    public double JevCostUsd => InputTokens * 0.042 / 1_000_000;
}

public sealed record EpisodeStats(int Ticks, LatencySummary Step, LatencySummary ObserveRoundTrip,
    LatencySummary ObservationAge, double AllocatedBytesPerTick, DecisionStats Decisions);

public sealed class EpisodeResult
{
    public string Reason { get; init; } = "";

    public Status? Status { get; init; }

    public int Decisions { get; init; }

    public Dictionary<string, Counter> Options { get; init; } = [];

    public Dictionary<string, int> Errors { get; init; } = [];

    public Dictionary<string, StepReport> Steps { get; init; } = [];

    public TickReport Ticks { get; init; } = new(0, null);

    public List<Dictionary<string, object?>> Writes { get; init; } = [];

    public object? LastFacts { get; init; }

    public Progress Progress { get; init; } = new();

    public EpisodeStats? Stats { get; init; }
}

public sealed partial class Runtime
{
    public const int MaxInFlight = 2;
    public const double MinAskGap = 0.08;
    public const double JevTimeout = 1.5;
    public const int MaxConsecutiveErrors = 25;
    public const double ObsTraceEvery = 1.0;
    public const int StepSamples = 4000;
    public const double SlowWaits = 3.0;
    public const double ResumeAfter = 1.5;
    public const double ResponseEvery = 1.0;
    public const double ResponseMinTurn = 10.0;
    public const double UnresponsiveAfter = 2.0;
    public const string NoPolicy = "more than one option is legal and this run has no policy to choose";
    public const string TimeLimit = "time limit";
    public const string DecisionLimit = "decision limit";
    public const double DrainSeconds = 2.0;

    sealed class Running(Option option, Stepper stepper, Obs before, double t0, double? p)
    {
        public Option Option { get; } = option;
        public Stepper Stepper { get; } = stepper;
        public Obs Before { get; } = before;
        public double T0 { get; } = t0;
        public double? P { get; set; } = p;
    }

    sealed record Looked(Obs Obs, Status Status, List<Option> Options, double Begun);

    sealed record Pending(Task<JevDecision> Task, double Asked, List<string> Names, double T, JevRequest Request);

    sealed record Ending(Option Option, Obs Before, double T0, bool Ok, string Why, Dictionary<string, object?>? Data,
        double At, double TEnd);

    sealed class StepSample
    {
        public int N;
        public int Kept;
        public readonly double[] Ms = new double[StepSamples];
        public long Waits;
        public int MaxWaits;
    }

    readonly Game _game;
    readonly Harness _h;
    readonly Trace _trace;
    readonly IDecider? _client;
    readonly double _maxSeconds;
    readonly double _deadlineSeconds;
    readonly int _maxDecisions;
    readonly Action<string> _say;
    readonly Func<bool> _stop;
    readonly Progress? _progress;
    readonly Action<string, Dictionary<string, object?>> _logBefore;
    readonly bool _idles;
    Func<CancellationToken, ValueTask>? _resume;

    List<Step> _plan = [];
    readonly Dictionary<string, double?> _planDone = [];
    int? _planStep;
    double? _stepSince;
    bool _planSeen;
    readonly HashSet<string> _doneFailed = [];
    readonly HashSet<string> _mismatched = [];
    double? _progressT;
    Vec? _stallAt;
    Dictionary<string, Counter> _stallTried = [];
    Stall? _stalled;
    double? _tStart;
    readonly List<Pending> _pending = [];
    Running? _running;
    readonly Queue<RecentOption> _recent = new();
    readonly Dictionary<string, Counter> _stats = [];
    Dictionary<string, Counter> _statsAtStep = [];
    int _decisions;
    List<string>? _lastNames;
    double _lastAsk = -1e9;
    readonly Counter _errors = new();
    Stepper? _idle;
    readonly Dictionary<string, double> _cooldown = [];
    readonly Counter _quickFails = new();
    readonly List<Ending> _ending = [];
    readonly Dictionary<string, StepSample> _steps = [];
    int _ticks;
    double? _wallStart;

    double _responseT = -1e9;
    double? _responseYaw;
    double _responseAsked;
    double? _frozenSince;
    bool _flagged;
    Task _responseRead = Task.CompletedTask;
    Task<Looked>? _seen;
    CancellationTokenSource _work = new();
    CancellationToken _outer;

    readonly LatencySeries _stepLatency = new();
    readonly LatencySeries _observeLatency = new();
    readonly LatencySeries _ageLatency = new();
    readonly LatencySeries _decisionLatency = new();
    int _asked, _answered, _jevErrors, _consecutiveJevErrors, _started, _kept, _switched, _held, _dropped;
    long _inputTokens, _outputTokens;
    string? _lastJevError;
    long _lookedAt;

    public Runtime(Game game, Harness harness, string tracePath, IDecider? client = null, double maxSeconds = 120,
        Action<string>? say = null, Func<bool>? stop = null, Func<CancellationToken, ValueTask>? resume = null,
        Progress? progress = null, double? deadlineSeconds = null, int maxDecisions = int.MaxValue)
    {
        _game = game;
        _h = harness;
        _trace = new Trace(tracePath);
        _logBefore = game.Log;
        game.Log = _trace.Log;
        _client = client;
        _maxSeconds = maxSeconds;
        _deadlineSeconds = deadlineSeconds ?? maxSeconds * 3 + 30;
        _maxDecisions = maxDecisions;
        _say = say ?? (_ => { });
        _stop = stop ?? (() => false);
        _resume = resume;
        _progress = progress;
        _idles = harness.GetType().GetMethod(nameof(Harness.Idle))?.DeclaringType != typeof(Harness);
    }

    public string? RunningOption => _running?.Option.Name;

    static Dictionary<string, object?> F(params (string Key, object? Value)[] items)
    {
        var fields = new Dictionary<string, object?>(items.Length);
        foreach (var (key, value) in items)
            fields[key] = value;
        return fields;
    }

    Counter Stats(string name)
    {
        if (!_stats.TryGetValue(name, out var counter))
            _stats[name] = counter = new Counter();
        return counter;
    }

    Counter Tried(string name)
    {
        if (!_stallTried.TryGetValue(name, out var counter))
            _stallTried[name] = counter = new Counter();
        return counter;
    }

    double Cooldown(string name)
    {
        lock (_cooldown)
            return _cooldown.GetValueOrDefault(name, -1e9);
    }

    void Remember(RecentOption outcome)
    {
        _recent.Enqueue(outcome);
        while (_recent.Count > Math.Max(1, _h.RecentKept))
            _recent.Dequeue();
    }

    void Ask(Obs obs, List<Option> opts, CancellationToken ct)
    {
        NowDoing? doing = _running is { } running
            ? new NowDoing(running.Option.Name, Fmt.Round(_game.T - running.T0, 1))
            : null;
        var questions = new Dictionary<string, Question>();
        try
        {
            foreach (var (id, question) in _h.Questions(obs) ?? [])
                questions[id] = question;
        }
        catch (Exception e)
        {
            Error("questions", e);
        }
        var request = new JevRequest(_h.Goal, _h.Instructions ?? JevRequest.DefaultInstructions, _h.Facts(obs),
            opts.Select(o => new OfferedOption(o.Name, o.Description, o.Facts)).ToList(), doing, [.. _recent])
        {
            Plan = _plan.Count > 0 ? PlanFacts() : null,
            Questions = questions,
        };
        var names = opts.Select(o => o.Name).ToList();
        var client = _client!;
        _asked++;
        var asked = _game.Clock.Monotonic();
        // Off the tick: encoding and connecting must never hold a controller back.
        _pending.Add(new Pending(Task.Run(() => client.DecideAsync(request, ct), ct), asked, names, _game.T, request));
        _lastAsk = asked;
    }

    List<(Pending Item, JevDecision Decision)> Answers()
    {
        List<(Pending, JevDecision)>? done = null;
        for (var i = 0; i < _pending.Count;)
        {
            var item = _pending[i];
            if (!item.Task.IsCompleted)
            {
                if (_game.Clock.Monotonic() - item.Asked > JevTimeout * 2.5)
                {
                    _trace.Log("jev_error", F(("error", "no answer in time"), ("t", _game.T)));
                    _pending.RemoveAt(i);
                    continue;
                }
                i++;
                continue;
            }
            _pending.RemoveAt(i);
            if (item.Task.IsCompletedSuccessfully)
            {
                _consecutiveJevErrors = 0;
                (done ??= []).Add((item, item.Task.Result));
                continue;
            }
            var e = item.Task.Exception?.InnerException ?? new OperationCanceledException();
            _jevErrors++;
            _consecutiveJevErrors++;
            _lastJevError = e.Message;
            _errors.Add($"jev: {e.GetType().Name}");
            _trace.Log("jev_error", F(("error", Fmt.Cut($"{e.GetType().Name}: {e.Message}", 300)), ("t", _game.T)));
        }
        return done ?? [];
    }

    public void Start(Option option, Obs obs, double? p = null, string why = "picked", CancellationToken ct = default)
    {
        var game = _game;
        var stepper = new Stepper(game, _ => option.Run(game, obs), ct);
        _running = new Running(option, stepper, obs, game.T, p);
        _started++;
        Stats(option.Name).Add("started");
        Tried(option.Name).Add("started");
        _trace.Log("option_start", F(("t", game.T), ("option", option.Name), ("p", p), ("why", why), ("facts", option.Facts)));
        _say($"Now: {Fmt.Cut(option.Description, 80)}");
    }

    async ValueTask WatchResponse()
    {
        double yaw;
        try
        {
            yaw = (await _game.ViewRotation(_work.Token).ConfigureAwait(false)).Yaw;
        }
        catch (Exception)
        {
            return;
        }
        var (asked, now) = (_game.TurnAsked - _responseAsked, _game.T);
        if (_responseYaw is { } was && asked >= ResponseMinTurn)
        {
            var turned = Math.Abs(Geom.Wrap(yaw - was));
            if (turned < Math.Min(1.0, 0.1 * asked))
            {
                _frozenSince ??= _responseT;
                if (!_flagged && now - _frozenSince >= UnresponsiveAfter)
                {
                    _flagged = true;
                    _trace.Log("unresponsive", F(("t", _frozenSince), ("asked_degrees", Fmt.Round(asked)),
                        ("turned_degrees", Fmt.Round(turned, 1))));
                }
            }
            else
            {
                if (_flagged)
                    _trace.Log("responsive", F(("t", now)));
                (_frozenSince, _flagged) = (null, false);
            }
        }
        (_responseT, _responseYaw, _responseAsked) = (now, yaw, _game.TurnAsked);
    }

    StepSample SampleOf(string name)
    {
        if (!_steps.TryGetValue(name, out var sample))
            _steps[name] = sample = new StepSample();
        return sample;
    }

    void Timed(StepSample sample, long started, int waits)
    {
        var elapsed = Stopwatch.GetTimestamp() - started;
        _stepLatency.Add(elapsed);
        sample.N++;
        sample.Waits += waits;
        sample.MaxWaits = Math.Max(sample.MaxWaits, waits);
        if (sample.Kept < StepSamples)
            sample.Ms[sample.Kept++] = elapsed * 1000.0 / Stopwatch.Frequency;
    }

    Dictionary<string, StepReport> StepReports()
    {
        var reports = new Dictionary<string, StepReport>();
        foreach (var (name, s) in _steps)
        {
            if (s.N == 0)
                continue;
            var ms = s.Ms[..s.Kept];
            Array.Sort(ms);
            reports[name] = new StepReport(s.N, Fmt.Round(ms[ms.Length / 2], 1), Fmt.Round(ms[(int)(ms.Length * 0.9)], 1),
                Fmt.Round(ms[^1], 1), Fmt.Round((double)s.Waits / s.N, 2), s.MaxWaits);
        }
        return reports;
    }

    public async ValueTask StopRunning(string why)
    {
        if (_running is not { } r)
            return;
        try
        {
            if (!await r.Stepper.Stop().ConfigureAwait(false))
                Error($"option {r.Option.Name} close", new TimeoutException("it didn't stop when cancelled"));
            else if (r.Stepper.Task is { IsFaulted: true } task)
                Error($"option {r.Option.Name} close", task.Exception!.InnerException!);
        }
        catch (Exception e)
        {
            Error($"option {r.Option.Name} close", e);
        }
        _game.ReleaseAll();
        _running = null;
        Stats(r.Option.Name).Add("interrupted");
        Tried(r.Option.Name).Add("interrupted");
        var seconds = _game.T - r.T0;
        _trace.Log("option_end", F(("t", _game.T), ("option", r.Option.Name), ("seconds", Fmt.Round(seconds, 2)),
            ("ok", null), ("why", why), ("interrupted", true)));
        Remember(new RecentOption(r.Option.Name, Fmt.Round(seconds, 1), null, $"stopped: {why}"));
    }

    void FinishRecord(Option option, Obs before, Obs after, double t0, bool ok, string why,
        Dictionary<string, object?>? data, double? tEnd = null)
    {
        var end = tEnd ?? _game.T;
        bool? effect = null;
        if (option.Effect is not null)
        {
            try
            {
                effect = option.Effect(before, after);
            }
            catch (Exception e)
            {
                Error($"option {option.Name} effect", e);
            }
        }
        var worked = effect ?? ok;
        if (!worked && end - t0 < 0.5)
        {
            _quickFails.Add(option.Name);
            var hold = Math.Min(8.0, 0.5 * Math.Pow(2, _quickFails.Get(option.Name) - 1));
            lock (_cooldown)
                _cooldown[option.Name] = _game.T + hold;
            Stats(option.Name).Add("held_back");
        }
        else if (worked)
        {
            _quickFails[option.Name] = 0;
        }
        Stats(option.Name).Add("finished");
        var how = worked ? "worked" : "failed";
        Stats(option.Name).Add(how);
        Tried(option.Name).Add(how);
        if (worked)
            Progressed(after);
        _trace.Log("option_end", F(("t", end), ("option", option.Name), ("seconds", Fmt.Round(end - t0, 2)), ("ok", ok),
            ("why", why), ("effect", effect), ("data", data)));
        Remember(new RecentOption(option.Name, Fmt.Round(end - t0, 1), worked, Fmt.Cut(why, 80)));
    }

    async ValueTask StepRunning(Running r)
    {
        if (_game.T - r.T0 > r.Option.MaxSeconds)
        {
            await StopRunning($"took longer than {Fmt.F(r.Option.MaxSeconds, 0)}s").ConfigureAwait(false);
            Stats(r.Option.Name).Add("timed_out");
            return;
        }
        await r.Stepper.Step();
        if (!r.Stepper.Done)
            return;
        var task = r.Stepper.Task!;
        if (task.IsCanceled && _work.IsCancellationRequested)
            return;
        bool ok;
        string why;
        Dictionary<string, object?>? data = null;
        if (task.IsCompletedSuccessfully)
        {
            var result = task.Result;
            ok = result is null || result.Ok;
            why = result is null ? "done" : result.Why.Length > 0 ? result.Why : Fmt.Cut(result.ToString(), 120);
            data = result?.Data;
        }
        else
        {
            var e = task.Exception?.InnerException ?? new OperationCanceledException();
            Error($"option {r.Option.Name}", e);
            ok = false;
            why = Fmt.Cut($"error: {e.GetType().Name}: {e.Message}", 200);
        }
        _running = null;
        _game.ReleaseAll();
        _ending.Add(new Ending(r.Option, r.Before, r.T0, ok, why, data, _game.Clock.Monotonic(), _game.T));
    }

    void SettleEndings(Obs? obs, double? begun = null)
    {
        for (var i = 0; i < _ending.Count;)
        {
            var e = _ending[i];
            if (begun is null || begun > e.At)
            {
                _ending.RemoveAt(i);
                FinishRecord(e.Option, e.Before, obs!, e.T0, e.Ok, e.Why, e.Data, e.TEnd);
            }
            else
            {
                i++;
            }
        }
    }

    public async ValueTask Apply(IReadOnlyDictionary<string, double> probabilities, Dictionary<string, object?> extra,
        double latency, List<Option> optsNow, Obs obs, double askedT, JevRequest? request = null,
        CancellationToken ct = default)
    {
        _decisions++;
        Option? best = null;
        var bestP = double.MinValue;
        var live = new Dictionary<string, double>();
        foreach (var (name, p) in probabilities)
        {
            var option = optsNow.Find(o => o.Name == name);
            if (option is null)
                continue;
            live[name] = p;
            if (p > bestP)
                (best, bestP) = (option, p);
        }
        var action = "dropped";
        try
        {
            _h.OnAnswers(extra, obs);
        }
        catch (Exception e)
        {
            Error("on_answers", e);
        }
        if (best is null)
        {
            _dropped++;
        }
        else if (_running is null)
        {
            Start(best, obs, Fmt.Round(bestP, 3), ct: ct);
            action = "started";
        }
        else if (best.Name == _running.Option.Name)
        {
            action = "kept";
            _kept++;
            _running.P = bestP;
        }
        else
        {
            var current = _running.Option.Name;
            var margin = bestP - live.GetValueOrDefault(current, 0.0);
            if (_running.Option.Interruptible && (best.Urgent || margin >= _h.SwitchMargin))
            {
                await StopRunning($"switched to {best.Name}").ConfigureAwait(false);
                Start(best, obs, Fmt.Round(bestP, 3), $"switched from {current}", ct);
                action = "switched";
                _switched++;
            }
            else
            {
                action = "kept_running";
                _held++;
            }
        }
        _trace.Log("decision", F(("t", askedT), ("applied_t", _game.T), ("latency", Fmt.Round(latency, 3)),
            ("probs", probabilities), ("extra", extra), ("chosen", best?.Name), ("action", action), ("state", request)));
    }

    Step? CurrentStep() => _plan.Count > 0 && _planStep is { } index ? _plan[index] : null;

    Dictionary<string, object?> PlanFacts()
    {
        var step = CurrentStep();
        Dictionary<string, object?>? now = null;
        if (step is not null)
        {
            var since = _stepSince ?? _game.T;
            now = new Dictionary<string, object?>
            {
                ["step"] = step.Name, ["what"] = step.Description, ["for_seconds"] = Fmt.Round(_game.T - since, 1),
            };
        }
        var i = _planStep ?? _plan.Count;
        return new Dictionary<string, object?>
        {
            ["done"] = _planDone.Keys.ToList(), ["now"] = now,
            ["next"] = _plan.Skip(i + 1).Select(s => s.Description).ToList(),
        };
    }

    void PlanUpdate(Obs obs)
    {
        if (_plan.Count == 0)
            return;
        var n = _planDone.Count;
        var step = PlanAdvance(_plan, obs, _planDone, _game.T, DoneError);
        int? index = step is not null ? _plan.IndexOf(step) : null;
        if (!_planSeen || index != _planStep)
        {
            var was = CurrentStep();
            _trace.Log("step", F(("t", _game.T), ("from", was?.Name), ("to", step?.Name),
                ("done", _planDone.Keys.ToList())));
            (_planSeen, _planStep, _stepSince) = (true, index, _game.T);
            _statsAtStep = _stats.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));
            _say(step is not null ? $"Plan: {Fmt.Cut(step.Description, 70)}" : "Plan: every step is done");
        }
        if (_planDone.Count > n)
            Progressed(obs);
    }

    static Counter Copy(Counter source)
    {
        var copy = new Counter();
        foreach (var (key, value) in source)
            copy[key] = value;
        return copy;
    }

    void DoneError(Step step, Exception e)
    {
        if (_doneFailed.Add(step.Name))
            Error($"plan step {step.Name} done()", e);
    }

    List<Option> PlanOffer(List<Option> opts)
    {
        var step = CurrentStep();
        var (offered, mismatch) = StepOptions(step, opts);
        if (mismatch is not null && _mismatched.Add(step!.Name))
            _trace.Log("plan_mismatch", F(("t", _game.T), ("step", step.Name), ("listed", mismatch),
                ("legal", opts.Select(o => o.Name).ToList())));
        return offered;
    }

    void Progressed(Obs? obs)
    {
        _progressT = _game.T;
        _stallAt = PoseAt(obs);
        _stallTried = [];
    }

    static long[] Whole(Vec point)
    {
        var rounded = point.Rounded();
        return [(long)rounded.X, (long)rounded.Y, (long)rounded.Z];
    }

    Stall StallInfo(Obs obs)
    {
        var step = CurrentStep();
        var now = PoseAt(obs);
        StallPlayer? player = null;
        if (_stallAt is { } at && now is { } here)
            player = new StallPlayer
            {
                At = Whole(at),
                Now = Whole(here),
                Moved = Fmt.Round(here.Dist(at)),
            };
        return new Stall
        {
            Step = step?.Name,
            SinceT = Fmt.Round(_progressT ?? 0, 2),
            Seconds = Fmt.Round(_game.T - (_progressT ?? 0), 1),
            Tried = _stallTried.ToDictionary(pair => pair.Key, pair => new Dictionary<string, int>(pair.Value)),
            Player = player,
        };
    }

    void Error(string where, Exception e)
    {
        var key = $"{where}: {e.GetType().Name}: {Fmt.Cut(e.Message, 120)}";
        _errors.Add(key);
        if (_errors.Get(key) <= 3)
        {
            var text = e.ToString();
            _trace.Log("error", F(("t", _game.T), ("where", where),
                ("error", Fmt.Cut($"{e.GetType().Name}: {e.Message}", 500)),
                ("traceback", text.Length > 1500 ? text[^1500..] : text)));
        }
    }

    void OnEvent(JsonElement raw)
    {
        var e = GameValue.Of(raw);
        var kind = e["kind"].Text;
        if (kind == "finding")
        {
            var f = e["finding"];
            _trace.Log("finding", F(("t", e["t"]), ("detector", f["detector"]), ("severity", f["severity"]),
                ("summary", f["summary"]), ("actor", f["actor"]), ("signature", f["signature"])));
        }
        else if (kind == "log")
        {
            _trace.Log("game_log", F(("t", e["t"]), ("verbosity", e["verbosity"]), ("category", e["category"]),
                ("message", Fmt.Cut(e["message"].ToString(), 300))));
        }
    }

    public async Task<EpisodeResult> Run(CancellationToken ct = default)
    {
        Action<JsonElement> listener = OnEvent;
        _game.Link.Event += listener;
        var token = _game.RunToken;
        _outer = ct;
        _work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _work.CancelAfter(TimeSpan.FromSeconds(_deadlineSeconds));
        _game.RunToken = _work.Token;
        try
        {
            return await RunEpisode(_work.Token).ConfigureAwait(false);
        }
        finally
        {
            _game.RunToken = token;
            _game.Link.Event -= listener;
            _work.Dispose();
        }
    }

    async Task<Looked> Look(CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var begun = _game.Clock.Monotonic();
        var obs = await _h.Observe(_game).ConfigureAwait(false);
        var status = _h.Status(obs) ?? new Status();
        var listed = _h.Options(obs) ?? [];
        var options = new List<Option>(listed.Count);
        foreach (var option in listed)
            if (Cooldown(option.Name) <= _game.T)
                options.Add(option);
        var took = Stopwatch.GetTimestamp();
        _observeLatency.Add(took - started);
        Volatile.Write(ref _lookedAt, took);
        return new Looked(obs, status, options, begun);
    }

    async Task<EpisodeResult> RunEpisode(CancellationToken ct)
    {
        var (g, h) = (_game, _h);
        var clock = g.Clock;
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        _trace.Log("start", F(("harness", h.GetType().Name), ("goal", h.Goal), ("tempo", h.Tempo), ("tick_hz", h.TickHz),
            ("max_seconds", _maxSeconds)));
        var reason = "";
        try
        {
            await h.Setup(g).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await Finish(EndedBy(), null, null, allocatedBefore).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Error("setup", e);
            return await Finish($"setup failed: {e.GetType().Name}: {e.Message}", null, null, allocatedBefore)
                .ConfigureAwait(false);
        }
        if (_progress is not null)
        {
            try
            {
                await h.Resume(g, _progress).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Error("harness resume", e);
            }
            _trace.Log("continued", F(("t", g.T), ("progress", _progress)));
        }
        _plan = h.Plan?.ToList() ?? [];
        if (_plan.Count > 0)
        {
            var names = _plan.Select(s => s.Name).ToHashSet();
            foreach (var name in _progress?.Done ?? [])
                if (names.Contains(name))
                    _planDone[name] = null;
            _trace.Log("plan", F(("t", g.T), ("done", _planDone.Keys.ToList()), ("stall_seconds", h.StallSeconds),
                ("steps", _plan.Select(s => new Dictionary<string, object?>
                {
                    ["name"] = s.Name, ["what"] = s.Description, ["options"] = s.Options,
                }).ToList())));
        }
        var (tStart, wallStart) = (g.T, clock.Monotonic());
        (_tStart, _wallStart) = (tStart, wallStart);
        double? firstObs = null;
        var tick = 1.0 / Math.Max(1.0, h.TickHz);
        var consecutive = 0;
        var lastObsTrace = -1e9;
        Obs? obs = null;
        List<Option> opts = [];
        List<string> names2 = [];
        var status = new Status();
        Status? over = null;
        Func<Task<Looked>> look = () => Look(ct);
        using var ticker = clock.Ticker(tick);
        try
        {
            while (true)
            {
                var began = clock.Monotonic();
                var beganTicks = Stopwatch.GetTimestamp();
                if (_stop() || ct.IsCancellationRequested)
                {
                    reason = _stop() ? "stopped" : EndedBy();
                    break;
                }
                if (g.Link.Closed is { } closed)
                {
                    reason = $"the game disconnected: {closed}";
                    break;
                }
                g.Renew();
                _seen ??= Task.Run(look, CancellationToken.None);
                if (_seen.IsCompleted)
                {
                    if (!_seen.IsCompletedSuccessfully)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            reason = EndedBy();
                            break;
                        }
                        var e = _seen.Exception?.InnerException ?? new OperationCanceledException();
                        consecutive++;
                        Error("observe/status/options", e);
                        _seen = null;
                        try
                        {
                            if (!(await g.Context(fresh: true, ct).ConfigureAwait(false))["character"].Truthy)
                            {
                                status = new Status(Status.Failed, "the player is gone (died or despawned)");
                                reason = "the player is gone";
                                break;
                            }
                        }
                        catch (Exception other) when (other is not OperationCanceledException)
                        {
                        }
                        if (consecutive >= MaxConsecutiveErrors)
                        {
                            reason = $"observe kept failing: {e.GetType().Name}: {e.Message}";
                            break;
                        }
                        await ticker.Wait(ct).ConfigureAwait(false);
                        continue;
                    }
                    var looked = _seen.Result;
                    (obs, status, opts) = (looked.Obs, looked.Status, looked.Options);
                    consecutive = 0;
                    SettleEndings(obs, looked.Begun);
                    _seen = null;
                    if (_progressT is null)
                        (_progressT, _stallAt) = (g.T, PoseAt(obs));
                    PlanUpdate(obs);
                    opts = PlanOffer(opts);
                    names2 = NamesOf(opts, names2);
                    if (g.T - _responseT >= ResponseEvery && _responseRead.IsCompleted)
                        _responseRead = WatchResponse().AsTask();
                    if (g.T - lastObsTrace >= ObsTraceEvery)
                    {
                        lastObsTrace = g.T;
                        _trace.Log("obs", F(("t", g.T), ("facts", h.Facts(obs)), ("status", status.State),
                            ("note", status.Note), ("options", names2), ("running", _running?.Option.Name),
                            ("step", CurrentStep()?.Name)));
                    }
                    if (over is not null)
                        status = status.Over ? status : over;
                    if (status.Over)
                    {
                        // The observation that ended play began before the option's end: it would judge the option on
                        // the world from before what it did. One more, begun after, settles it.
                        if (_ending.Count > 0 && over is null)
                        {
                            over = status;
                            await ticker.Wait(ct).ConfigureAwait(false);
                            continue;
                        }
                        reason = $"status {status.State}";
                        break;
                    }
                }
                if (obs is null)
                {
                    await clock.Delay(Math.Min(tick, 0.02), ct).ConfigureAwait(false);
                    continue;
                }
                firstObs ??= clock.Monotonic();
                _ageLatency.Add(beganTicks - Volatile.Read(ref _lookedAt));
                if (g.T - tStart > _maxSeconds)
                {
                    reason = TimeLimit;
                    break;
                }
                var stepLong = CurrentStep() is not null && h.StepSeconds > 0 && _stepSince is { } since
                               && g.T - since >= h.StepSeconds;
                if ((h.StallSeconds > 0 && g.T - _progressT >= h.StallSeconds) || stepLong)
                {
                    _stalled = StallInfo(obs);
                    if (stepLong)
                    {
                        _stalled.Kind = "step";
                        _stalled.Seconds = Fmt.Round(g.T - _stepSince!.Value, 1);
                        _stalled.SinceT = Fmt.Round(_stepSince!.Value, 2);
                        _stalled.Tried = TriedSinceStep();
                    }
                    _trace.Log("stalled", F(("t", g.T), ("cause", _stalled.Kind), ("step", _stalled.Step),
                        ("since_t", _stalled.SinceT), ("seconds", _stalled.Seconds), ("tried", _stalled.Tried),
                        ("player", _stalled.Player)));
                    reason = "stalled";
                    break;
                }
                if (_ending.Count > 0 && _running is null)
                {
                    await ticker.Wait(ct).ConfigureAwait(false);
                    continue;
                }
                foreach (var (item, decision) in Answers())
                {
                    _answered++;
                    _inputTokens += decision.InputTokens;
                    _outputTokens += decision.OutputTokens;
                    _decisionLatency.Add(decision.LatencyTicks);
                    await Apply(decision.Probabilities, decision.Extra, decision.LatencyTicks / (double)Stopwatch.Frequency,
                        Offered(opts, names2), obs, item.T, item.Request, ct).ConfigureAwait(false);
                }
                if (_consecutiveJevErrors >= MaxConsecutiveErrors)
                {
                    reason = $"Jev failed {MaxConsecutiveErrors} times in a row";
                    status = new Status(Status.Failed, reason);
                    break;
                }
                if (_client is null && _running is null && opts.Count > 1)
                {
                    reason = NoPolicy;
                    status = new Status(Status.Failed, reason);
                    break;
                }
                var now = clock.Monotonic();
                var changed = _lastNames is null || !_lastNames.SequenceEqual(names2);
                _lastNames = names2;
                var due = _running is null || changed || (h.Tempo > 0 && now - _lastAsk >= 1.0 / h.Tempo);
                if (due && _pending.Count < MaxInFlight && now - _lastAsk >= MinAskGap)
                {
                    var offered = Offered(opts, names2);
                    if (opts.Count == 1 && _running is null)
                    {
                        Start(opts[0], obs, 1.0, "the only option", ct);
                        _lastAsk = now;
                    }
                    else if (offered.Count >= 2 && _client is not null && (_running is null || _pending.Count == 0))
                    {
                        if (_asked >= _maxDecisions)
                        {
                            reason = DecisionLimit;
                            break;
                        }
                        Ask(obs, offered, ct);
                    }
                }
                if (_resume is { } resume)
                {
                    if (_running is null && clock.Monotonic() - firstObs < ResumeAfter)
                    {
                        await ticker.Wait(ct).ConfigureAwait(false);
                        continue;
                    }
                    _resume = null;
                    try
                    {
                        await resume(ct).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Error("resume", e);
                    }
                    _trace.Log("resumed", F(("t", g.T), ("held_seconds", Fmt.Round(clock.Monotonic() - wallStart, 2)),
                        ("option", _running?.Option.Name)));
                }
                _ticks++;
                if (_running is { } running)
                {
                    if (_idle is { } idle)
                    {
                        _idle = null;
                        await idle.Stop().ConfigureAwait(false);
                    }
                    var sample = SampleOf(running.Option.Name);
                    var started = Stopwatch.GetTimestamp();
                    var waits = running.Stepper.Waits.Count;
                    await StepRunning(running).ConfigureAwait(false);
                    Timed(sample, started, running.Stepper.Waits.Count - waits);
                }
                else if (_idles)
                {
                    var sample = SampleOf("(idle)");
                    var started = Stopwatch.GetTimestamp();
                    var waits = _idle?.Waits.Count ?? 0;
                    await IdleStep(obs, ct).ConfigureAwait(false);
                    Timed(sample, started, (_idle?.Waits.Count ?? waits) - waits);
                }
                else
                {
                    Timed(SampleOf("(idle)"), Stopwatch.GetTimestamp(), 0);
                }
                await ticker.Wait(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = EndedBy();
        }
        return await Finish(reason, status, obs, allocatedBefore).ConfigureAwait(false);
    }

    string EndedBy() => _outer.IsCancellationRequested ? "stopped" : TimeLimit;

    async Task Drain()
    {
        var work = new List<Task>(_pending.Count + 2) { _responseRead };
        if (_seen is not null)
            work.Add(_seen);
        work.AddRange(_pending.Select(item => item.Task));
        try
        {
            await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(DrainSeconds)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or GameException
                                      or GameDisconnectedException)
        {
        }
        catch (Exception e)
        {
            Error("drain", e);
        }
    }

    static List<string> NamesOf(List<Option> opts, List<string> known)
    {
        var same = opts.Count == known.Count;
        for (var i = 0; same && i < opts.Count; i++)
            same = opts[i].Name == known[i];
        if (same)
            return known;
        var names = new List<string>(opts.Count);
        foreach (var option in opts)
            names.Add(option.Name);
        return names;
    }

    List<Option> Offered(List<Option> opts, List<string> names) =>
        _running is { } running && !names.Contains(running.Option.Name) ? [.. opts, running.Option] : opts;

    Dictionary<string, Dictionary<string, int>> TriedSinceStep()
    {
        var tried = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (name, counter) in _stats)
        {
            var before = _statsAtStep.GetValueOrDefault(name);
            var since = new Dictionary<string, int>();
            foreach (var (how, n) in counter)
            {
                var more = n - (before?.Get(how) ?? 0);
                if (more > 0)
                    since[how] = more;
            }
            if (since.Count > 0)
                tried[name] = since;
        }
        return tried;
    }

    async ValueTask IdleStep(Obs obs, CancellationToken ct)
    {
        var game = _game;
        _idle ??= new Stepper(game, async _ =>
        {
            await _h.Idle(game, obs).ConfigureAwait(false);
            return null!;
        }, ct);
        await _idle.Step();
        if (_idle.Done)
        {
            if (_idle.Task is { IsFaulted: true } task)
                Error("idle", task.Exception!.InnerException!);
            _idle = null;
        }
    }

    async Task<EpisodeResult> Finish(string reason, Status? status, Obs? obs, long allocatedBefore)
    {
        await _work.CancelAsync().ConfigureAwait(false);
        if (reason is TimeLimit or DecisionLimit)
            status = new Status(Status.Inconclusive, reason, status?.Evidence);
        try
        {
            _game.ReleaseAll();
        }
        catch (Exception e)
        {
            Error("release", e);
        }
        if (_running is not null)
            await StopRunning($"episode ended ({reason})").ConfigureAwait(false);
        if (_idle is { } idle)
            await idle.Stop().ConfigureAwait(false);
        await Drain().ConfigureAwait(false);
        SettleEndings(obs);
        using var teardown = new CancellationTokenSource(TimeSpan.FromSeconds(DrainSeconds * 5));
        _game.RunToken = teardown.Token;
        try
        {
            await _h.Teardown(_game).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Error("teardown", e);
        }
        _game.RunToken = default;
        var clock = _game.Clock;
        object? lastFacts = obs is null ? null : _h.Facts(obs);
        var step = CurrentStep();
        JsonElement? facts = null;
        if (lastFacts is not null)
        {
            using var document = JsonDocument.Parse(Jsonable.Encode(lastFacts));
            facts = document.RootElement.Clone();
        }
        var progress = new Progress
        {
            Done = _planDone.Keys.ToList(),
            Step = step?.Name,
            T = _tStart is { } start ? Fmt.Round(_game.T - start, 1) : null,
            Stalled = _stalled,
            LastFacts = facts,
        };
        var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
        var result = new EpisodeResult
        {
            Reason = reason,
            Status = status,
            Decisions = _decisions,
            Options = _stats.ToDictionary(pair => pair.Key, pair => Copy(pair.Value)),
            Errors = new Dictionary<string, int>(_errors),
            Steps = StepReports(),
            Ticks = new TickReport(_h.TickHz, _wallStart is { } wall
                ? Fmt.Round(_ticks / Math.Max(1e-3, clock.Monotonic() - wall), 1) : null),
            Writes = [.. _game.Writes],
            LastFacts = lastFacts,
            Progress = progress,
            Stats = new EpisodeStats(_ticks, _stepLatency.Summarize(), _observeLatency.Summarize(),
                _ageLatency.Summarize(), _ticks == 0 ? 0 : (double)allocated / _ticks,
                new DecisionStats(_asked, _answered, _jevErrors, _started, _kept, _switched, _held, _dropped, _inputTokens,
                    _outputTokens, _decisionLatency.Summarize(), _lastJevError)),
        };
        _trace.Log("end", F(("t", _game.T), ("reason", reason), ("status", StatusFields(status)), ("seconds", null),
            ("decisions", _decisions), ("options", result.Options), ("errors", result.Errors),
            ("steps", result.Steps.ToDictionary(pair => pair.Key, pair => new Dictionary<string, object?>
            {
                ["steps"] = pair.Value.Steps, ["median_ms"] = pair.Value.MedianMs, ["p90_ms"] = pair.Value.P90Ms,
                ["max_ms"] = pair.Value.MaxMs, ["waits_mean"] = pair.Value.WaitsMean, ["waits_max"] = pair.Value.WaitsMax,
            })),
            ("ticks", new Dictionary<string, object?> { ["asked_hz"] = _h.TickHz, ["ran_hz"] = result.Ticks.RanHz }),
            ("writes", result.Writes), ("last_facts", lastFacts), ("progress", progress)));
        await _trace.CloseAsync().ConfigureAwait(false);
        _game.Log = _logBefore;
        return result;
    }

    static Dictionary<string, object?>? StatusFields(Status? status) => status is null
        ? null
        : new Dictionary<string, object?> { ["state"] = status.State, ["note"] = status.Note, ["evidence"] = status.Evidence };

    public static Vec? PoseAt(Obs? obs)
    {
        if (obs is null || !obs.TryGetValue("_pose", out var pose))
            return null;
        return pose switch
        {
            Pose known => known.At,
            GameValue value => value["at"].Vector,
            _ => null,
        };
    }

    public static Step? PlanAdvance(IReadOnlyList<Step> plan, Obs obs, Dictionary<string, double?> done, double t,
        Action<Step, Exception>? onError = null)
    {
        foreach (var step in plan)
        {
            if (done.ContainsKey(step.Name))
                continue;
            bool achieved;
            try
            {
                achieved = step.Done(obs);
            }
            catch (Exception e)
            {
                achieved = false;
                onError?.Invoke(step, e);
            }
            if (!achieved)
                return step;
            done[step.Name] = t;
        }
        return null;
    }

    public static (List<Option> Offered, List<string>? Mismatch) StepOptions(Step? step, List<Option> opts)
    {
        if (step?.Options is not { } named)
            return (opts, null);
        var offered = new List<Option>(opts.Count);
        var matched = opts.Count == 0;
        foreach (var option in opts)
        {
            var listed = named.Contains(option.Name);
            matched |= listed;
            if (listed || option.Urgent)
                offered.Add(option);
        }
        return matched ? (offered, null) : (opts, [.. named]);
    }
}
