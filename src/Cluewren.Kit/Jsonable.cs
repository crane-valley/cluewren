using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cluewren.Kit;

public static class Jsonable
{
    const int MaxDepth = 8;

    static readonly JsonWriterOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static readonly ConcurrentDictionary<Type, Field[]> Shapes = new();

    readonly record struct Field(JsonEncodedText Name, PropertyInfo Property);

    public static void Write(Utf8JsonWriter json, object? value, int depth = 0, bool round = true)
    {
        if (depth > MaxDepth)
        {
            json.WriteStringValue(value?.ToString());
            return;
        }
        switch (value)
        {
            case null:
                json.WriteNullValue();
                break;
            case string text:
                json.WriteStringValue(text);
                break;
            case bool flag:
                json.WriteBooleanValue(flag);
                break;
            case double number:
                WriteNumber(json, number, round);
                break;
            case float number:
                WriteNumber(json, number, round);
                break;
            case int number:
                json.WriteNumberValue(number);
                break;
            case long number:
                json.WriteNumberValue(number);
                break;
            case Enum named:
                json.WriteStringValue(named.ToString());
                break;
            case decimal number:
                json.WriteNumberValue(number);
                break;
            case short or byte or uint or ushort or ulong or sbyte:
                json.WriteNumberValue(Convert.ToDecimal(value));
                break;
            case Vec vec:
                WriteVector(json, vec);
                break;
            case GameValue game:
                WriteElement(json, game.Element, round);
                break;
            case JsonElement element:
                WriteElement(json, element, round);
                break;
            case JevRequest request:
                request.WriteState(json);
                break;
            case Pose pose:
                WritePose(json, pose);
                break;
            // The shapes a decision writes several times over: through IDictionary and IEnumerable they would box the
            // enumerator, every entry and every number.
            case Dictionary<string, object?> map:
                json.WriteStartObject();
                WriteEntries(json, map, depth, round);
                json.WriteEndObject();
                break;
            case List<object?> items:
                json.WriteStartArray();
                foreach (var item in items)
                    Write(json, item, depth + 1, round);
                json.WriteEndArray();
                break;
            case List<string> texts:
                json.WriteStartArray();
                foreach (var text in texts)
                    json.WriteStringValue(text);
                json.WriteEndArray();
                break;
            case List<double> numbers:
                WriteNumbers(json, CollectionsMarshal.AsSpan(numbers), round);
                break;
            case double[] numbers:
                WriteNumbers(json, numbers, round);
                break;
            case IDictionary map:
                json.WriteStartObject();
                foreach (DictionaryEntry entry in map)
                {
                    json.WritePropertyName(entry.Key.ToString() ?? "");
                    Write(json, entry.Value, depth + 1, round);
                }
                json.WriteEndObject();
                break;
            case ITuple tuple:
                json.WriteStartArray();
                for (var i = 0; i < tuple.Length; i++)
                    Write(json, tuple[i], depth + 1, round);
                json.WriteEndArray();
                break;
            case IEnumerable items:
                json.WriteStartArray();
                foreach (var item in items)
                    Write(json, item, depth + 1, round);
                json.WriteEndArray();
                break;
            default:
                WriteObject(json, value, depth, round);
                break;
        }
    }

    internal static void WriteEntries(Utf8JsonWriter json, Dictionary<string, object?> map, int depth, bool round)
    {
        foreach (var (key, value) in map)
        {
            json.WritePropertyName(key);
            Write(json, value, depth + 1, round);
        }
    }

    static void WriteNumbers(Utf8JsonWriter json, ReadOnlySpan<double> numbers, bool round)
    {
        json.WriteStartArray();
        foreach (var number in numbers)
            WriteNumber(json, number, round);
        json.WriteEndArray();
    }

    static void WriteNumber(Utf8JsonWriter json, double number, bool round)
    {
        if (double.IsNaN(number) || double.IsInfinity(number))
            json.WriteNullValue();
        else
            json.WriteNumberValue(round ? Fmt.Round(number, 3) : number);
    }

    static void WriteVector(Utf8JsonWriter json, Vec vec)
    {
        json.WriteStartArray();
        json.WriteNumberValue(Fmt.Round(vec.X));
        json.WriteNumberValue(Fmt.Round(vec.Y));
        json.WriteNumberValue(Fmt.Round(vec.Z));
        json.WriteEndArray();
    }

    static void WritePose(Utf8JsonWriter json, Pose pose)
    {
        json.WriteStartObject();
        json.WritePropertyName("at");
        WriteVector(json, pose.At);
        json.WritePropertyName("eye");
        WriteVector(json, pose.Eye);
        json.WriteNumber("yaw", Fmt.Round(pose.Yaw, 3));
        json.WriteNumber("pitch", Fmt.Round(pose.Pitch, 3));
        json.WritePropertyName("velocity");
        WriteVector(json, pose.Velocity);
        if (pose.Ground is { } grounded)
            json.WriteBoolean("on_ground", grounded);
        else
            json.WriteNull("on_ground");
        json.WriteEndObject();
    }

    static void WriteElement(Utf8JsonWriter json, JsonElement element, bool round)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
                json.WriteNullValue();
                break;
            case JsonValueKind.Number when round:
                WriteNumber(json, element.GetDouble(), true);
                break;
            case JsonValueKind.Object:
                json.WriteStartObject();
                foreach (var field in element.EnumerateObject())
                {
                    json.WritePropertyName(field.Name);
                    WriteElement(json, field.Value, round);
                }
                json.WriteEndObject();
                break;
            case JsonValueKind.Array:
                json.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(json, item, round);
                json.WriteEndArray();
                break;
            default:
                element.WriteTo(json);
                break;
        }
    }

    static void WriteObject(Utf8JsonWriter json, object value, int depth, bool round)
    {
        var fields = Shapes.GetOrAdd(value.GetType(), ShapeOf);
        if (fields.Length == 0)
        {
            json.WriteStringValue(value.ToString());
            return;
        }
        json.WriteStartObject();
        foreach (var field in fields)
        {
            json.WritePropertyName(field.Name);
            Write(json, field.Property.GetValue(value), depth + 1, round);
        }
        json.WriteEndObject();
    }

    static Field[] ShapeOf(Type type) => type.IsPrimitive
        ? []
        : type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0
                               && property.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .Select(property => new Field(JsonEncodedText.Encode(WireName(type, property.Name), Plain.Encoder), property))
            .ToArray();

    // An anonymous object's names are the model's own: for game.Call they are the game's parameter names, and a parameter
    // that arrives renamed is silently left at its default.
    static string WireName(Type type, string name) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), false) ? name : Snake(name);

    static string Snake(string name) =>
        name.Length == 0 || !char.IsUpper(name[0]) ? name : JsonNamingPolicy.SnakeCaseLower.ConvertName(name);

    public static byte[] Encode(object? value, bool round = true)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, Plain))
            Write(json, value, 0, round);
        return stream.ToArray();
    }
}
