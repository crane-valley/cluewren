using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cluewren.Kit;

public static class Fmt
{
    public static long Round(double value) => (long)Math.Round(value, MidpointRounding.ToEven);

    public static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    public static string F(double value, int digits = 1) =>
        value.ToString("F" + digits, CultureInfo.InvariantCulture);

    public static string G(double value) => value.ToString("G6", CultureInfo.InvariantCulture);

    public static string Cut(string? text, int length) =>
        text is null || text.Length <= length ? text ?? "" : text[..length];

    public static string Json(object? value) => Encoding.UTF8.GetString(Jsonable.Encode(value));

    public static string Json(JsonElement element) => element.GetRawText();
}
