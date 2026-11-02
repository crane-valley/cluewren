using System.Globalization;
using System.Text.Json;

namespace PlaytestAgent.Kit;

public readonly struct GameValue(JsonElement element)
{
    public JsonElement Element { get; } = element;

    public static GameValue Of(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? default : new GameValue(element.Clone());

    public bool IsNull => Element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    public bool IsNumber => Element.ValueKind == JsonValueKind.Number;

    public bool IsObject => Element.ValueKind == JsonValueKind.Object;

    public bool IsArray => Element.ValueKind == JsonValueKind.Array;

    public double? Number => IsNumber ? Element.GetDouble() : null;

    public string? Text => Element.ValueKind == JsonValueKind.String ? Element.GetString() : null;

    public bool Truthy => Element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => Element.GetDouble() != 0,
        JsonValueKind.String => Element.GetString()!.Length > 0,
        JsonValueKind.Array => Element.GetArrayLength() > 0,
        JsonValueKind.Object => Element.EnumerateObject().Any(),
        _ => false,
    };

    public GameValue this[string name] =>
        IsObject && Element.TryGetProperty(name, out var value) ? new GameValue(value) : default;

    public GameValue this[int index] =>
        IsArray && index >= 0 && index < Element.GetArrayLength() ? new GameValue(Element[index]) : default;

    public bool Has(string name) => IsObject && Element.TryGetProperty(name, out _);

    public int Count => IsArray ? Element.GetArrayLength() : IsObject ? Element.EnumerateObject().Count() : 0;

    public IEnumerable<GameValue> Items
    {
        get
        {
            if (IsArray)
                foreach (var item in Element.EnumerateArray())
                    yield return new GameValue(item);
        }
    }

    public Dictionary<string, GameValue> Fields
    {
        get
        {
            var fields = new Dictionary<string, GameValue>();
            if (IsObject)
                foreach (var member in Element.EnumerateObject())
                    fields[member.Name] = new GameValue(member.Value);
            return fields;
        }
    }

    public List<string> Strings() => Items.Select(item => item.Text ?? "").ToList();

    public Vec? Vector
    {
        get
        {
            if (IsObject)
                return new Vec(Axis("X", "x"), Axis("Y", "y"), Axis("Z", "z"));
            if (!IsArray || Count < 2)
                return null;
            return new Vec(this[0].Number ?? 0, this[1].Number ?? 0, this[2].Number ?? 0);
        }
    }

    double Axis(string upper, string lower) => this[upper].Number ?? this[lower].Number ?? 0;

    public static implicit operator double(GameValue value) =>
        value.Number ?? throw new InvalidOperationException($"not a number: {value}");

    public static implicit operator string?(GameValue value) => value.Text;

    public static implicit operator bool(GameValue value) => value.Truthy;

    public override string ToString() => Element.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => "null",
        JsonValueKind.String => Element.GetString()!,
        JsonValueKind.Number => Element.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        _ => Element.GetRawText(),
    };
}
