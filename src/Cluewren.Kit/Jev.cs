using System.Buffers;
using System.Text.Json;

namespace Cluewren.Kit;

public sealed record RecentOption(string Option, double Seconds, bool? Worked, string How);

public sealed record NowDoing(string Option, double ForSeconds);

public sealed record OfferedOption(string Name, string Description, IReadOnlyDictionary<string, object?> Facts);

public sealed record JevDecision(IReadOnlyDictionary<string, double> Probabilities, int InputTokens, int OutputTokens,
    long LatencyTicks)
{
    public Dictionary<string, object?> Extra { get; init; } = [];

    public static JevDecision Parse(ReadOnlySpan<byte> body, long latencyTicks)
    {
        var reader = new Utf8JsonReader(body);
        using var document = JsonDocument.ParseValue(ref reader);
        return Parse(document.RootElement, latencyTicks);
    }

    public static JevDecision Parse(JsonElement root, long latencyTicks)
    {
        var probabilities = new Dictionary<string, double>();
        var extra = new Dictionary<string, object?>();
        if (root.TryGetProperty("answers", out var answers) && answers.ValueKind == JsonValueKind.Object)
        {
            foreach (var answer in answers.EnumerateObject())
            {
                if (answer.Name == JevRequest.PickQuestion)
                {
                    ReadChoice(answer.Value, probabilities);
                    continue;
                }
                var kind = answer.Value.TryGetProperty("type", out var type) ? type.GetString() : null;
                if (kind == "noul" && answer.Value.TryGetProperty("noul", out var noul))
                {
                    extra[answer.Name] = noul.GetDouble();
                }
                else if (kind == "score" && answer.Value.TryGetProperty("score", out var score))
                {
                    extra[answer.Name] = score.GetDouble();
                }
                else if (kind == "choice")
                {
                    var choice = new Dictionary<string, double>();
                    ReadChoice(answer.Value, choice);
                    extra[answer.Name] = choice;
                }
            }
        }
        var (input, output) = (0, 0);
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var inputTokens))
                input = inputTokens;
            if (usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var outputTokens))
                output = outputTokens;
        }
        return new JevDecision(probabilities, input, output, latencyTicks) { Extra = extra };
    }

    static void ReadChoice(JsonElement answer, Dictionary<string, double> probabilities)
    {
        if (answer.TryGetProperty("probabilities", out var values))
            foreach (var value in values.EnumerateObject())
                probabilities[value.Name] = value.Value.GetDouble();
        if (probabilities.Count == 0 && answer.TryGetProperty("choice", out var choice) && choice.GetString() is { } only)
            probabilities[only] = 1;
    }
}

public interface IDecider
{
    Task<JevDecision> DecideAsync(JevRequest request, CancellationToken ct);
}

public sealed record JevRequest(string Goal, string Instructions, object? Situation,
    IReadOnlyList<OfferedOption> Options, NowDoing? NowDoing, IReadOnlyList<RecentOption> Recent)
{
    public const string PickQuestion = "pick";

    public const string DefaultInstructions =
        "You are the player of a video game. Choose the option to do now that best serves `goal`. `situation` holds facts "
        + "about the game now. `options.<name>` holds facts about each option. `now_doing` is the option being carried out "
        + "now: let it finish unless another option is clearly better now. `recent` lists the last options taken and "
        + "whether they worked: don't keep repeating one that keeps failing in the same situation. `plan`, when present, "
        + "is the goal's steps in order: do what `plan.now` needs; the steps in `plan.done` are done, don't redo them.";

    public object? Plan { get; init; }

    public IReadOnlyDictionary<string, Question>? Questions { get; init; }

    public byte[] Encode(string model) => Write(model);

    public byte[] EncodeBody() => Write(null);

    public byte[] Write(string? model)
    {
        var buffer = new ArrayBufferWriter<byte>(2048);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            if (model is not null)
                json.WriteString("model", model);
            json.WritePropertyName("state");
            WriteState(json);
            json.WriteStartObject("questions");
            json.WriteStartObject(PickQuestion);
            json.WriteString("type", "choice");
            json.WriteString("instructions", Instructions);
            json.WriteStartObject("criteria");
            foreach (var option in Options)
                json.WriteString(option.Name, option.Description);
            json.WriteEndObject();
            json.WriteEndObject();
            if (Questions is not null)
            {
                foreach (var (id, question) in Questions)
                {
                    if (id == PickQuestion)
                        continue;
                    json.WritePropertyName(id);
                    question.Write(json);
                }
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public void WriteState(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteString("goal", Goal);
        json.WritePropertyName("situation");
        Jsonable.Write(json, Situation);
        json.WriteStartObject("options");
        foreach (var option in Options)
        {
            json.WritePropertyName(option.Name);
            Jsonable.Write(json, option.Facts);
        }
        json.WriteEndObject();
        if (NowDoing is { } doing)
        {
            json.WriteStartObject("now_doing");
            json.WriteString("option", doing.Option);
            json.WriteNumber("for_seconds", Fmt.Round(doing.ForSeconds, 1));
            json.WriteEndObject();
        }
        else
        {
            json.WriteNull("now_doing");
        }
        json.WriteStartArray("recent");
        foreach (var recent in Recent)
        {
            json.WriteStartObject();
            json.WriteString("option", recent.Option);
            json.WriteNumber("seconds", Fmt.Round(recent.Seconds, 1));
            if (recent.Worked is { } worked)
                json.WriteBoolean("worked", worked);
            else
                json.WriteNull("worked");
            json.WriteString("how", recent.How);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        if (Plan is not null)
        {
            json.WritePropertyName("plan");
            Jsonable.Write(json, Plan);
        }
        json.WriteEndObject();
    }

    public byte[] StateJson()
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var json = new Utf8JsonWriter(buffer))
            WriteState(json);
        return buffer.WrittenSpan.ToArray();
    }
}
