using System.Text.Json;

namespace PlaytestAgent.Kit;

public sealed class Result(bool ok, string why = "", Dictionary<string, object?>? data = null)
{
    public bool Ok { get; } = ok;

    public string Why { get; } = why;

    public Dictionary<string, object?> Data { get; } = data ?? [];

    public static implicit operator bool(Result? result) => result is { Ok: true };

    public static bool operator true(Result? result) => result is { Ok: true };

    public static bool operator false(Result? result) => result is not { Ok: true };

    public override string ToString() => $"Result({Ok}, {Why})";
}

public class Obs : Dictionary<string, object?>
{
    internal double Begun { get; set; }

    public T? Get<T>(string key) => TryGetValue(key, out var value) && value is T typed ? typed : default;

    public bool Bool(string key) => TryGetValue(key, out var value) && value switch
    {
        null => false,
        bool flag => flag,
        GameValue game => game.Truthy,
        double number => number != 0,
        int number => number != 0,
        long number => number != 0,
        string text => text.Length > 0,
        System.Collections.ICollection items => items.Count > 0,
        _ => true,
    };

    public double Num(string key) => TryGetValue(key, out var value) ? value switch
    {
        double number => number,
        int number => number,
        long number => number,
        float number => number,
        GameValue game => game.Number ?? 0,
        _ => 0,
    } : 0;

    public string? Text(string key) => TryGetValue(key, out var value) ? value switch
    {
        string text => text,
        GameValue game => game.Text,
        _ => value?.ToString(),
    } : null;
}

public delegate Task<Result> Executor(Game game, Obs obs);

public sealed record Option(string Name, string Description, Executor Run)
{
    public Dictionary<string, object?> Facts { get; init; } = [];

    public bool Interruptible { get; init; } = true;

    public bool Urgent { get; init; }

    public Func<Obs, Obs, bool?>? Effect { get; init; }

    public double MaxSeconds { get; init; } = 30;
}

public sealed record Step(string Name, string Description, Func<Obs, bool> Done, IReadOnlyList<string>? Options = null);

public sealed record Status
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Inconclusive = "inconclusive";

    // The dictionary overload is what gives `new() { ["a"] = 1 }` a type; the other takes what else model-written harnesses
    // keep their measurements in, a list or an anonymous object, which otherwise cost them a failed compile.
    public Status(string state = Running, string note = "", Dictionary<string, object?>? evidence = null)
        : this(state, note, (object?)evidence)
    {
    }

    public Status(string state, object? evidence)
        : this(state, "", evidence)
    {
    }

    public Status(string state, string note, object? evidence)
    {
        State = state;
        Note = note;
        Evidence = evidence;
    }

    public string State { get; init; }

    public string Note { get; init; }

    public object? Evidence { get; init; }

    public bool Over => State != Running;
}

public sealed class Stall
{
    public string Kind { get; set; } = "no progress";

    public string? Step { get; set; }

    public double SinceT { get; set; }

    public double Seconds { get; set; }

    public Dictionary<string, Dictionary<string, int>> Tried { get; set; } = [];

    public StallPlayer? Player { get; set; }
}

public sealed class StallPlayer
{
    public long[] At { get; set; } = [];

    public long[] Now { get; set; } = [];

    public long Moved { get; set; }
}

public sealed class Progress
{
    public int? Episode { get; set; }

    public List<string> Done { get; set; } = [];

    public string? Step { get; set; }

    public double? T { get; set; }

    public Stall? Stalled { get; set; }

    public JsonElement? LastFacts { get; set; }
}

public abstract class Question
{
    public string? Instructions { get; init; }

    internal abstract void Write(Utf8JsonWriter json);
}

public sealed class Choice(Dictionary<string, string?> criteria) : Question
{
    public Dictionary<string, string?> Criteria { get; } = criteria;

    internal override void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteString("type", "choice");
        if (Instructions is not null)
            json.WriteString("instructions", Instructions);
        json.WriteStartObject("criteria");
        foreach (var (name, description) in Criteria)
            json.WriteString(name, description);
        json.WriteEndObject();
        json.WriteEndObject();
    }
}

public sealed class Noul : Question
{
    public string? IfTrue { get; init; }

    public string? IfFalse { get; init; }

    internal override void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteString("type", "noul");
        if (Instructions is not null)
            json.WriteString("instructions", Instructions);
        if (IfTrue is not null || IfFalse is not null)
        {
            json.WriteStartObject("criteria");
            if (IfTrue is not null)
                json.WriteString("true", IfTrue);
            if (IfFalse is not null)
                json.WriteString("false", IfFalse);
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }
}

public sealed class Score(IReadOnlyList<string> criteria) : Question
{
    public IReadOnlyList<string> Criteria { get; } = criteria;

    internal override void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteString("type", "score");
        if (Instructions is not null)
            json.WriteString("instructions", Instructions);
        json.WriteStartArray("criteria");
        foreach (var level in Criteria)
            json.WriteStringValue(level);
        json.WriteEndArray();
        json.WriteEndObject();
    }
}

public abstract class Harness
{
    public virtual string Goal { get; set; } = "";

    public virtual double Tempo { get; set; } = 2.0;

    public virtual double TickHz { get; set; } = 15.0;

    public virtual double SwitchMargin { get; set; } = 0.25;

    public virtual string? Instructions { get; set; }

    public virtual int RecentKept { get; set; } = 6;

    public virtual string? StartMap { get; set; }

    public virtual List<Step> Plan { get; set; } = [];

    public virtual double StallSeconds { get; set; } = 45.0;

    public virtual double StepSeconds { get; set; } = 150.0;

    public virtual Task Setup(Game game) => Task.CompletedTask;

    public virtual Task Resume(Game game, Progress progress) => Task.CompletedTask;

    public virtual Task<Obs> Observe(Game game) => Task.FromResult(new Obs());

    public virtual object? Facts(Obs obs)
    {
        var facts = new Dictionary<string, object?>(obs.Count);
        foreach (var (key, value) in obs)
            if (!key.StartsWith('_'))
                facts[key] = value;
        return facts;
    }

    public virtual List<Option> Options(Obs obs) => [];

    public virtual Dictionary<string, Question> Questions(Obs obs) => [];

    public virtual void OnAnswers(Dictionary<string, object?> answers, Obs obs)
    {
    }

    public virtual Status Status(Obs obs) => new();

    public virtual async Task Idle(Game game, Obs obs) => await game.Tick();

    public virtual Task Teardown(Game game) => Task.CompletedTask;
}
