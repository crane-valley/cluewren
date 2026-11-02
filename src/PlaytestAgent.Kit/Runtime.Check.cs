using System.Text.Json;

namespace PlaytestAgent.Kit;

public sealed partial class Runtime
{
    public const double CheckSeconds = 8.0;
    public const int CheckMaxOptions = 12;

    static Dictionary<string, JsonElement> FactsOf(Harness harness, Obs obs)
    {
        try
        {
            using var document = JsonDocument.Parse(Jsonable.Encode(harness.Facts(obs)));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document.RootElement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone());
            return new Dictionary<string, JsonElement> { ["(facts)"] = document.RootElement.Clone() };
        }
        catch (Exception e)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize($"{e.GetType().Name}: {e.Message}"));
            return new Dictionary<string, JsonElement> { ["(facts failed)"] = document.RootElement.Clone() };
        }
    }

    public static async Task<string> Check(Game game, Harness harness, double seconds = CheckSeconds, bool restart = true,
        IReadOnlyList<string>? only = null, CancellationToken ct = default)
    {
        var text = new List<string>();
        var done = new Dictionary<string, double?>();
        var noted = new HashSet<string>();
        List<Step> plan = [];

        async Task<(Obs, Status, List<Option>, Step?)> Look()
        {
            var obs = await harness.Observe(game).ConfigureAwait(false);
            var opts = harness.Options(obs) ?? [];
            var step = PlanAdvance(plan, obs, done, game.T, (s, e) =>
                text.Add($"  plan step {s.Name}: done() raised {e.GetType().Name}: {e.Message} (it counts as not done)"));
            return (obs, harness.Status(obs) ?? new Status(), opts, step);
        }

        string Standing(Step? step) => step is null
            ? $"every step is done: {Fmt.Json(done.Keys)}"
            : $"at step {plan.IndexOf(step) + 1} of {plan.Count}, {step.Name} ({step.Description}); done: {Fmt.Json(done.Keys)}";

        List<string> ToTry(Step? step, List<Option> opts)
        {
            if (only is not null)
                return opts.Where(o => only.Contains(o.Name)).Select(o => o.Name).ToList();
            var (offered, mismatch) = StepOptions(step, opts);
            if (mismatch is not null && noted.Add(step!.Name))
                text.Add($"  PLAN MISMATCH: step {step.Name} lists options {Fmt.Json(mismatch)} but none of them is legal "
                         + "now: every legal option is offered");
            return offered.Select(o => o.Name).ToList();
        }

        if (restart && !await game.Restart(harness.StartMap, 10.0, ct).ConfigureAwait(false))
            text.Add("After the restart there was no player pawn within 10 s (a menu? a loading screen?).");
        await game.Pause(false, ct).ConfigureAwait(false);
        try
        {
            await harness.Setup(game).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            text.Add("setup() failed:\n" + e);
            return string.Join("\n", text);
        }
        text.Add("setup() ran.");
        plan = harness.Plan?.ToList() ?? [];
        Obs obs;
        Status status;
        List<Option> opts;
        Step? step;
        try
        {
            (obs, status, opts, step) = await Look().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            text.Add("observe/status/options failed on the first observation:\n" + e);
            return string.Join("\n", text);
        }
        text.Add($"First observation: status={status.State}{(status.Note.Length > 0 ? $" ({status.Note})" : "")}");
        if (status.Over)
            text.Add("  THE EPISODE WOULD END AT ONCE: status is not running at the start.");
        text.Add("  facts: " + Fmt.Cut(Fmt.Json(FactsOf(harness, obs)), 1500));
        var legal = opts.Select(o => o.Name).ToList();
        text.Add($"  options legal now: {Fmt.Json(legal)}"
                 + (legal.Count >= 2 ? "" : "  (fewer than 2: Jev has nothing to choose between here)"));
        if (plan.Count > 0)
            text.Add($"  plan: {Standing(step)}");
        var queue = ToTry(step, opts);
        if (plan.Count > 0 && !queue.SequenceEqual(legal))
            text.Add($"  tried (the current step's options): {Fmt.Json(queue)}");
        var tried = new List<string>();
        while (queue.Count > 0 && tried.Count < CheckMaxOptions)
        {
            var name = queue[0];
            queue.RemoveAt(0);
            tried.Add(name);
            try
            {
                (obs, status, opts, step) = await Look().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                text.Add($"\n{name}: observe failed before it:\n" + e);
                continue;
            }
            var option = opts.FirstOrDefault(o => o.Name == name);
            if (option is null)
            {
                text.Add($"\n{name}: not legal any more (after the options before it)");
                continue;
            }
            var f0 = FactsOf(harness, obs);
            var (t0, w0) = (game.T, game.Clock.Monotonic());
            Result? result = null;
            string? error = null;
            var before = obs;
            var stepper = new Stepper(game, _ => option.Run(game, before), ct);
            var outer = game.Current;
            try
            {
                var limit = Math.Min(seconds, option.MaxSeconds);
                while (true)
                {
                    if (game.Clock.Monotonic() - w0 > limit * 3 + 5 || game.T - t0 > limit)
                    {
                        await stepper.Stop().ConfigureAwait(false);
                        result = new Result(false, $"still running after {Fmt.F(limit, 0)} s (check's limit)");
                        break;
                    }
                    game.Renew();
                    await stepper.Step();
                    if (stepper.Done)
                    {
                        result = await stepper.Task!.ConfigureAwait(false);
                        break;
                    }
                    await game.Clock.Delay(1.0 / Math.Max(1.0, harness.TickHz), ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = e.ToString();
            }
            finally
            {
                game.Current = outer;
                try
                {
                    game.ReleaseAll();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                }
            }
            List<Option> later = [];
            Step? then = null;
            Obs? after = null;
            Status? now = null;
            Dictionary<string, JsonElement> f1 = [];
            string effect;
            try
            {
                (after, now, later, then) = await Look().ConfigureAwait(false);
                f1 = FactsOf(harness, after);
                effect = Fmt.Json(option.Effect?.Invoke(before, after));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                after = null;
                effect = $"(effect failed: {e.GetType().Name}: {e.Message})";
            }
            var changed = new Dictionary<string, object?>();
            foreach (var key in f0.Keys.Concat(f1.Keys.Where(k => !f0.ContainsKey(k))))
            {
                var (hadA, hadB) = (f0.TryGetValue(key, out var a), f1.TryGetValue(key, out var b));
                if (hadA != hadB || (hadA && a.GetRawText() != b.GetRawText()))
                    changed[key] = new object?[] { hadA ? a : null, hadB ? b : null };
            }
            var how = error is not null ? "error" : result is null || result.Ok ? "ok" : "failed";
            var why = result is null ? "" : result.Why.Length > 0 ? result.Why : Fmt.Cut(result.ToString(), 160);
            text.Add($"\n{name}: {how} in {Fmt.F(game.T - t0)} s game time{(why.Length > 0 ? $": {why}" : "")}; effect={effect}");
            if (error is not null)
            {
                var tail = error.Trim().Replace("\n", "\n  ");
                text.Add("  " + (tail.Length > 1200 ? tail[^1200..] : tail));
            }
            text.Add("  facts changed: " + (changed.Count > 0 ? Fmt.Cut(Fmt.Json(changed), 900) : "(none)"));
            if (after is null)
                continue;
            if (plan.Count > 0 && !ReferenceEquals(then, step))
                text.Add($"  plan moved on: {Standing(then)}");
            var fresh = ToTry(then, later).Where(n => !tried.Contains(n) && !queue.Contains(n)).ToList();
            if (fresh.Count > 0)
            {
                text.Add($"  legal now as well: {Fmt.Json(fresh)} (tried next)");
                queue.AddRange(fresh);
            }
            if (now is { Over: true })
            {
                text.Add($"  status is {now.State} now{(now.Note.Length > 0 ? $" ({now.Note})" : "")}: the check stops here.");
                break;
            }
        }
        try
        {
            await harness.Teardown(game).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            text.Add($"teardown() failed: {e.GetType().Name}: {e.Message}");
        }
        text.Add("\n(Options were run one after another, each from where the one before left the game. The level is "
                 + "restarted by the next run_harness.)");
        return string.Join("\n", text);
    }
}
