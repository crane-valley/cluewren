using System.Globalization;
using System.Text.Json;

namespace Cluewren.Kit;

public sealed partial class Runtime
{
    static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";

    static string? TextOrNull(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;

    static JsonElement Field(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) ? value : default;

    static string Show(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => "null",
        JsonValueKind.String => value.GetString()!,
        _ => Fmt.Json(value),
    };

    static string Dumped(JsonElement value) =>
        value.ValueKind == JsonValueKind.Undefined ? "null" : Fmt.Json(value);

    static IEnumerable<JsonElement> Items(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                yield return item;
    }

    static IEnumerable<JsonProperty> Props(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var item in value.EnumerateObject())
                yield return item;
    }

    public static string Summarize(string tracePath, int maxTimeline = 50, bool errorLog = false)
    {
        var events = Trace.Read(tracePath);
        if (events.Count == 0)
            return "(empty trace)";
        var text = new List<string>();
        var start = events.FirstOrDefault(e => Text(e, "kind") == "start");
        var endEvent = events.LastOrDefault(e => Text(e, "kind") == "end");
        var hasEnd = endEvent.ValueKind == JsonValueKind.Object;
        var tFirst = events.Select(e => Num(e, "t")).FirstOrDefault(t => t is not null) ?? 0;
        text.Add($"Harness {Show(Field(start, "harness"))}  goal: {Fmt.Json(TextOrNull(start, "goal"))}");
        if (hasEnd)
        {
            var st = Field(endEvent, "status");
            text.Add($"Ended: {Show(Field(endEvent, "reason"))} at t={Fmt.F((Num(endEvent, "t") ?? 0) - tFirst)}s  "
                     + $"status={Show(Field(st, "state"))} note={Fmt.Json(TextOrNull(st, "note"))} "
                     + $"evidence={Fmt.Cut(Dumped(Field(st, "evidence")), 400)}");
            text.Add($"Decisions by Jev: {Show(Field(endEvent, "decisions"))}");
        }
        else
        {
            text.Add("Ended: (no end event: the run crashed or is still going)");
        }
        var spans = new List<string>();
        double? since = null;
        foreach (var e in events)
        {
            var kind = Text(e, "kind");
            if (kind == "unresponsive" && since is null)
            {
                since = Num(e, "t") ?? 0;
            }
            else if (kind == "responsive" && since is { } from)
            {
                spans.Add($"t={Fmt.F(from - tFirst)}-{Fmt.F((Num(e, "t") ?? 0) - tFirst)}s");
                since = null;
            }
        }
        if (since is { } open)
            spans.Add($"t={Fmt.F(open - tFirst)}s to the end");
        if (spans.Count > 0)
            text.Add($"THE PLAYER DIDN'T ANSWER ITS INPUTS: {string.Join(", ", spans)}: the view didn't turn though the mouse "
                     + "turned it. Dead? A menu, cutscene or other screen holding the controls? Find out (the player's "
                     + "state, the screen) before reading what options did then: their failures there say nothing of them.");
        var planEvent = events.FirstOrDefault(e => Text(e, "kind") == "plan");
        var progress = Field(endEvent, "progress");
        if (planEvent.ValueKind == JsonValueKind.Object && Items(Field(planEvent, "steps")).Any())
        {
            var before = Items(Field(planEvent, "done")).Select(item => item.GetString()).ToHashSet();
            var doneAt = new Dictionary<string, double>();
            foreach (var e in events.Where(e => Text(e, "kind") == "step"))
                foreach (var name in Items(Field(e, "done")))
                    doneAt.TryAdd(name.GetString()!, Num(e, "t") ?? 0);
            text.Add("\nPlan (the steps in order):");
            var number = 0;
            foreach (var s in Items(Field(planEvent, "steps")))
            {
                number++;
                var name = Text(s, "name");
                var how = before.Contains(name) ? "done before this episode"
                    : doneAt.TryGetValue(name, out var at) ? $"done at t={Fmt.F(at - tFirst)}s" : "not reached";
                if (name == TextOrNull(progress, "step"))
                    how += "  <- the current step at the end";
                text.Add($"  {number}. {name}: {how}  ({Show(Field(s, "what"))})");
            }
            foreach (var e in events.Where(e => Text(e, "kind") == "plan_mismatch"))
                text.Add($"  PLAN MISMATCH at step {Show(Field(e, "step"))}: it lists options "
                         + $"{Fmt.Json(Items(Field(e, "listed")).Select(item => item.ToString()))} but the legal options were "
                         + $"{Fmt.Json(Items(Field(e, "legal")).Select(item => item.ToString()))}, so all of them were offered. "
                         + "Name the options the step really has, or make them legal in it.");
        }
        var stalled = Field(progress, "stalled");
        if (stalled.ValueKind == JsonValueKind.Object)
        {
            var triedParts = Props(Field(stalled, "tried")).Select(pair => pair.Name + " " + string.Join(", ",
                Props(pair.Value).Select(how => $"{how.Name} {how.Value}"))).ToList();
            var tried = triedParts.Count > 0 ? string.Join("; ", triedParts) : "nothing";
            var player = Field(stalled, "player");
            var moved = player.ValueKind == JsonValueKind.Object
                ? $"the player moved {Show(Field(player, "moved"))} units (from {Dumped(Field(player, "at"))} to "
                  + $"{Dumped(Field(player, "now"))})"
                : "where the player stood isn't known (no _pose in the observation)";
            var at = TextOrNull(stalled, "step") is { Length: > 0 } stuck ? $" at step {stuck}" : "";
            var from = (Num(stalled, "since_t") ?? 0) - tFirst;
            var why = Text(stalled, "kind") == "step"
                ? $"the step has taken that long though options worked (since the last one that worked: {tried}): what "
                  + "they achieve doesn't get the step done (what they gather is lost again, the wrong thing is counted, "
                  + "or the step's done() reads the wrong state)"
                : $"nothing advanced the plan and no option worked (tried: {tried})";
            text.Add($"STALLED{at} for {Show(Field(stalled, "seconds"))} s (from t={Fmt.F(from)}s): {why}; {moved}. "
                     + (spans.Count > 0
                         ? "THE PLAYER DIDN'T ANSWER ITS INPUTS MEANWHILE: first find out whether it is dead (its health, "
                           + "the screen's texts) or a menu holds the controls; a dead player can't be continued (make "
                           + "status() say failed when it dies, and run_harness with a restart), a menu is closed in a probe. "
                           + "Otherwise look"
                         : "Look")
                     + " at where the player is and what blocked it (describe_frames if it can, a probe), then revise "
                     + "the plan's continuation (add or change the steps from here on, or an option's approach) and go "
                     + "on from this state with run_harness(restart=False): the level is not restarted and the steps "
                     + "already done stay done.");
        }
        text.Add("\nOptions (started / worked / failed / unknown / interrupted / timed out / held back):");
        int Stat(JsonElement s, string name) => (int)(Num(s, name) ?? 0);
        foreach (var pair in Props(Field(endEvent, "options")).OrderByDescending(p => Stat(p.Value, "started")))
        {
            var s = pair.Value;
            text.Add($"  {pair.Name}: {Stat(s, "started")} / {Stat(s, "worked")} / {Stat(s, "failed")} / "
                     + $"{Stat(s, "unknown")} / {Stat(s, "interrupted")} / {Stat(s, "timed_out")} / {Stat(s, "held_back")}");
        }
        var whys = new Dictionary<string, List<(string Why, int Count)>>();
        foreach (var e in events.Where(e => Text(e, "kind") == "option_end"))
        {
            var option = Text(e, "option");
            if (!whys.TryGetValue(option, out var list))
                whys[option] = list = [];
            var why = Fmt.Cut(Show(Field(e, "why")), 80);
            var index = list.FindIndex(item => item.Why == why);
            if (index >= 0)
                list[index] = (why, list[index].Count + 1);
            else
                list.Add((why, 1));
        }
        foreach (var (name, list) in whys)
            text.Add($"  {name} ended because: " + string.Join("; ",
                list.OrderByDescending(item => item.Count).Take(4).Select(item => $"{item.Why} x{item.Count}")));
        var steps = Field(endEvent, "steps");
        if (Props(steps).Any())
        {
            var ticks = Field(endEvent, "ticks");
            var asked = Num(ticks, "asked_hz") ?? Num(start, "tick_hz") ?? 15.0;
            text.Add("\nExecutor steps (steps, ms a step median/p90/max, waits on the game a step mean/max; a tick is "
                     + $"{Fmt.F(1000 / Math.Max(1.0, asked), 0)} ms at tick_hz {Fmt.G(asked)}, "
                     + $"the loop ran {Show(Field(ticks, "ran_hz"))} ticks/s):");
            var slow = new List<string>();
            foreach (var pair in Props(steps).OrderByDescending(p => Num(p.Value, "steps") ?? 0))
            {
                var s = pair.Value;
                var (count, median, mean) = (Num(s, "steps") ?? 0, Num(s, "median_ms") ?? 0, Num(s, "waits_mean") ?? 0);
                text.Add($"  {pair.Name}: {count:0} steps, {Fmt.F(median, 0)}/{Fmt.F(Num(s, "p90_ms") ?? 0, 0)}/"
                         + $"{Fmt.F(Num(s, "max_ms") ?? 0, 0)} ms, {Fmt.F(mean)}/{Show(Field(s, "waits_max"))} waits");
                if (count >= 10 && mean >= SlowWaits)
                {
                    var per = median / Math.Max(1.0, mean);
                    slow.Add($"{pair.Name} (~{Fmt.F(median, 0)} ms a step, {Fmt.F(mean)} waits of ~{Fmt.F(per, 0)} ms)");
                }
            }
            if (slow.Count > 0)
                text.Add("  SLOW STEPS: " + string.Join("; ", slow) + ". Every read waits for the game's answer (about a "
                         + "frame) and reads one after another add up: a step reacts to what it read that long ago, and the "
                         + "next step comes that much later. Gather a step's reads into one game.Batch(...) (one wait) and "
                         + "read only what the step needs. A reaction timed to the game (dodging before a hit lands, jumping "
                         + "at an edge) must allow for the step time, which changes with the frame rate.");
        }
        text.Add("\nTimeline (t: option, p, how it ended, seconds):");
        var lines = new List<string>();
        var starts = new Dictionary<string, JsonElement>();
        foreach (var e in events)
        {
            var kind = Text(e, "kind");
            if (kind == "option_start")
            {
                starts[Text(e, "option")] = e;
            }
            else if (kind == "option_end")
            {
                var option = Text(e, "option");
                starts.Remove(option, out var s);
                var p = Num(s, "p");
                var effect = Field(e, "effect");
                var ok = Field(e, "ok");
                var ended = effect.ValueKind == JsonValueKind.True
                            || (effect.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined && ok.ValueKind == JsonValueKind.True)
                    ? "worked"
                    : ok.ValueKind == JsonValueKind.False || effect.ValueKind == JsonValueKind.False ? "FAILED" : "stopped";
                var at = (Num(s, "t") ?? Num(e, "t") ?? 0) - tFirst;
                lines.Add($"  t={at.ToString("F1", CultureInfo.InvariantCulture),6} {option}"
                          + (p is { } value ? $" p={Fmt.F(value, 2)}" : "") + $" -> {ended} ({Fmt.Cut(Show(Field(e, "why")), 70)}) "
                          + $"{Show(Field(e, "seconds"))}s");
            }
        }
        if (lines.Count > maxTimeline)
            lines = [.. lines.Take(maxTimeline / 2), $"  ... {lines.Count - maxTimeline} more ...",
                .. lines.Skip(lines.Count - maxTimeline / 2)];
        text.AddRange(lines);
        var decisions = events.Where(e => Text(e, "kind") == "decision").ToList();
        if (decisions.Count > 0)
        {
            var latency = decisions.Select(d => Num(d, "latency") ?? 0).Order().ToList();
            var actions = new Dictionary<string, int>();
            foreach (var d in decisions)
                actions[Text(d, "action")] = actions.GetValueOrDefault(Text(d, "action")) + 1;
            var shown = Fmt.Json(actions);
            text.Add($"\nJev: {decisions.Count} answers, latency median {Fmt.F(latency[latency.Count / 2], 2)}s; actions {shown}");
            text.Add("First decision Jev saw (state): " + Fmt.Cut(Dumped(Field(decisions[0], "state")), 1500));
            text.Add("Its answer: " + Dumped(Field(decisions[0], "probs")));
        }
        var jevErrors = events.Where(e => Text(e, "kind") == "jev_error").ToList();
        if (jevErrors.Count > 0)
            text.Add($"Jev errors: {jevErrors.Count} e.g. {Show(Field(jevErrors[0], "error"))}");
        var observations = events.Where(e => Text(e, "kind") == "obs").ToList();
        if (observations.Count > 0)
        {
            text.Add("\nSituation over time (every ~5 s):");
            var last = -1e9;
            for (var i = 0; i < observations.Count; i++)
            {
                var o = observations[i];
                var t = Num(o, "t") ?? 0;
                if (t - last < 5 && i != observations.Count - 1)
                    continue;
                last = t;
                var options = Fmt.Json(Items(Field(o, "options")).Select(item => item.ToString()));
                var step = TextOrNull(o, "step") is { Length: > 0 } now ? $" step={now}" : "";
                text.Add($"  t={(t - tFirst).ToString("F1", CultureInfo.InvariantCulture),6} "
                         + $"{Fmt.Cut(Dumped(Field(o, "facts")), 300)}  [{Show(Field(o, "status"))}] options={options}{step}");
            }
        }
        var errors = events.Where(e => Text(e, "kind") == "error").ToList();
        if (errors.Count > 0)
        {
            text.Add("\nErrors in harness code:");
            var seen = new HashSet<(string, string)>();
            foreach (var e in errors)
            {
                if (!seen.Add((Text(e, "where"), Text(e, "error"))))
                    continue;
                var trace = Text(e, "traceback").Trim().Replace("\n", "\n    ");
                text.Add($"  [{Text(e, "where")}] {Text(e, "error")}\n    " + (trace.Length > 700 ? trace[^700..] : trace));
                if (seen.Count >= 6)
                    break;
            }
        }
        var finds = events.Where(e => Text(e, "kind") is "finding" or "game_log").ToList();
        if (finds.Count > 0)
        {
            text.Add("\nWhat the game's own detectors and error log reported (count, first time):");
            var groups = new Dictionary<string, (int Count, JsonElement First)>();
            foreach (var e in finds)
            {
                var key = TextOrNull(e, "signature") ?? TextOrNull(e, "summary") ?? Text(e, "message");
                groups[key] = groups.TryGetValue(key, out var group) ? (group.Count + 1, group.First) : (1, e);
            }
            foreach (var (_, group) in groups.OrderByDescending(pair => pair.Value.Count).Take(15))
            {
                var e = group.First;
                var what = TextOrNull(e, "summary") ?? $"[{Show(Field(e, "verbosity"))}] {Show(Field(e, "category"))}: {Show(Field(e, "message"))}";
                text.Add($"  x{group.Count} t={Fmt.F((Num(e, "t") ?? 0) - tFirst)} {Text(e, "kind")}: {Fmt.Cut(what, 220)}");
            }
        }
        if (errorLog && !events.Any(e => Text(e, "kind") == "game_log"))
            text.Add("\nThe game's error log: no errors logged during this episode.");
        var writes = events.Count(e => Text(e, "kind") == "write");
        if (writes > 0)
            text.Add($"\nState written directly (not player input): {writes}");
        return string.Join("\n", text);
    }
}
