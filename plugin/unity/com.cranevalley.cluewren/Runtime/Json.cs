// Modified by Crane Valley: names renamed from Autopilot to Cluewren.
// A small JSON reader and writer (no dependencies): objects are Dictionary<string, object>, arrays List<object>,
// numbers double (or long when integral), strings, bools and null.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cluewren
{
    public static class Json
    {
        public static object Parse(string text)
        {
            var reader = new Reader(text);
            reader.SkipSpace();
            var value = reader.Value();
            reader.SkipSpace();
            if (!reader.End)
                throw new FormatException("trailing characters after JSON value");
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return Parse(text) as Dictionary<string, object>;
        }

        const int LargestSpare = 1 << 16;

        static readonly char[] StringStops = { '"', '\\' };

        [ThreadStatic] static StringBuilder spare;

        public static string Write(object value)
        {
            var sb = spare ?? new StringBuilder(256);
            spare = null;
            WriteValue(sb, value, 0);
            var text = sb.ToString();
            if (sb.Capacity <= LargestSpare) spare = sb.Clear();
            return text;
        }

        public static string Pretty(object value)
        {
            var sb = new StringBuilder(256);
            WritePretty(sb, value, 0);
            return sb.ToString();
        }

        // -- helpers for reading args ---------------------------------------------------------------------

        public static string Str(Dictionary<string, object> o, string key, string fallback = "")
        {
            if (o != null && o.TryGetValue(key, out var v) && v != null)
                return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
            return fallback;
        }

        public static double Num(Dictionary<string, object> o, string key, double fallback)
        {
            if (o != null && o.TryGetValue(key, out var v) && v != null)
            {
                if (v is double d) return d;
                if (v is long l) return l;
                if (v is bool b) return b ? 1 : 0;
                if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
            }
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> o, string key, bool fallback)
        {
            if (o != null && o.TryGetValue(key, out var v) && v != null)
            {
                if (v is bool b) return b;
                if (v is double d) return d != 0;
                if (v is long l) return l != 0;
                if (v is string s) return s == "true" || s == "1";
            }
            return fallback;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string key)
        {
            if (o != null && o.TryGetValue(key, out var v))
                return v as Dictionary<string, object>;
            return null;
        }

        public static List<object> List(Dictionary<string, object> o, string key)
        {
            if (o != null && o.TryGetValue(key, out var v))
                return v as List<object>;
            return null;
        }

        public static bool Has(Dictionary<string, object> o, string key)
        {
            return o != null && o.ContainsKey(key) && o[key] != null;
        }

        /// <summary>A field by name, ignoring case (exact match first).</summary>
        public static bool TryGetIgnoreCase(Dictionary<string, object> o, string key, out object value)
        {
            value = null;
            if (o == null) return false;
            if (o.TryGetValue(key, out value)) return true;
            foreach (var pair in o)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            return false;
        }

        public static double Round(double v, int digits)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return v;
            return Math.Round(v, digits, MidpointRounding.AwayFromZero);
        }

        // -- writing ---------------------------------------------------------------------------------------

        static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (depth > 64)
            {
                sb.Append("null");
                return;
            }
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return;
                case string s:
                    WriteString(sb, s);
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case double d:
                    WriteNumber(sb, d);
                    return;
                case float f:
                    WriteNumber(sb, f);
                    return;
                case int i:
                    WriteInteger(sb, i);
                    return;
                case long l:
                    WriteInteger(sb, l);
                    return;
                case uint ui:
                    sb.Append(ui.ToString(CultureInfo.InvariantCulture));
                    return;
                case ulong ul:
                    sb.Append(ul.ToString(CultureInfo.InvariantCulture));
                    return;
                case short sh:
                    sb.Append(sh.ToString(CultureInfo.InvariantCulture));
                    return;
                case ushort ush:
                    sb.Append(ush.ToString(CultureInfo.InvariantCulture));
                    return;
                case byte by:
                    sb.Append(by.ToString(CultureInfo.InvariantCulture));
                    return;
                case sbyte sby:
                    sb.Append(sby.ToString(CultureInfo.InvariantCulture));
                    return;
                case decimal dec:
                    sb.Append(dec.ToString(CultureInfo.InvariantCulture));
                    return;
                case char c:
                    WriteString(sb, c.ToString());
                    return;
                case Dictionary<string, object> map:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (var pair in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, pair.Key);
                        sb.Append(':');
                        WriteValue(sb, pair.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
                case List<object> items:
                {
                    sb.Append('[');
                    for (var n = 0; n < items.Count; ++n)
                    {
                        if (n > 0) sb.Append(',');
                        WriteValue(sb, items[n], depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
                case IDictionary dict:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        WriteValue(sb, e.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    var first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
            }
        }

        static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                sb.Append("null");
                return;
            }
            if (Math.Abs(d) < 1e15 && d == Math.Floor(d))
            {
                WriteInteger(sb, (long)d);
                return;
            }
#if NET_STANDARD_2_1
            Span<char> digits = stackalloc char[32];
            if (d.TryFormat(digits, out var written, "R", CultureInfo.InvariantCulture))
            {
                sb.Append(digits.Slice(0, written));
                return;
            }
#endif
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteInteger(StringBuilder sb, long value)
        {
#if NET_STANDARD_2_1
            Span<char> digits = stackalloc char[24];
            value.TryFormat(digits, out var written, default, CultureInfo.InvariantCulture);
            sb.Append(digits.Slice(0, written));
#else
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
#endif
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static void WritePretty(StringBuilder sb, object value, int indent)
        {
            if (value is IDictionary dict && dict.Count > 0)
            {
                sb.Append("{\n");
                var i = 0;
                foreach (DictionaryEntry e in dict)
                {
                    sb.Append(' ', indent + 2);
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    WritePretty(sb, e.Value, indent + 2);
                    if (++i < dict.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', indent).Append('}');
                return;
            }
            if (value is IList list && list.Count > 0)
            {
                sb.Append("[\n");
                for (var i = 0; i < list.Count; ++i)
                {
                    sb.Append(' ', indent + 2);
                    WritePretty(sb, list[i], indent + 2);
                    if (i + 1 < list.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', indent).Append(']');
                return;
            }
            WriteValue(sb, value, 0);
        }

        // -- reading ---------------------------------------------------------------------------------------

        sealed class Reader
        {
            readonly string s;
            int i;

            public Reader(string text)
            {
                s = text ?? "";
            }

            public bool End => i >= s.Length;

            public void SkipSpace()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) ++i;
            }

            public object Value()
            {
                if (i >= s.Length) throw new FormatException("unexpected end of JSON");
                var c = s[i];
                switch (c)
                {
                    case '{': return Object();
                    case '[': return Array();
                    case '"': return String();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Number();
                        throw new FormatException($"unexpected character '{c}' at {i}");
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                    throw new FormatException($"expected {word} at {i}");
                i += word.Length;
            }

            Dictionary<string, object> Object()
            {
                var o = new Dictionary<string, object>();
                ++i;
                SkipSpace();
                if (i < s.Length && s[i] == '}')
                {
                    ++i;
                    return o;
                }
                while (true)
                {
                    SkipSpace();
                    if (i >= s.Length || s[i] != '"') throw new FormatException($"expected a key at {i}");
                    var key = String();
                    SkipSpace();
                    if (i >= s.Length || s[i] != ':') throw new FormatException($"expected ':' at {i}");
                    ++i;
                    SkipSpace();
                    o[key] = Value();
                    SkipSpace();
                    if (i >= s.Length) throw new FormatException("unexpected end of JSON object");
                    if (s[i] == ',')
                    {
                        ++i;
                        continue;
                    }
                    if (s[i] == '}')
                    {
                        ++i;
                        return o;
                    }
                    throw new FormatException($"expected ',' or '}}' at {i}");
                }
            }

            List<object> Array()
            {
                var a = new List<object>();
                ++i;
                SkipSpace();
                if (i < s.Length && s[i] == ']')
                {
                    ++i;
                    return a;
                }
                while (true)
                {
                    SkipSpace();
                    a.Add(Value());
                    SkipSpace();
                    if (i >= s.Length) throw new FormatException("unexpected end of JSON array");
                    if (s[i] == ',')
                    {
                        ++i;
                        continue;
                    }
                    if (s[i] == ']')
                    {
                        ++i;
                        return a;
                    }
                    throw new FormatException($"expected ',' or ']' at {i}");
                }
            }

            string String()
            {
                ++i;
                var stop = s.IndexOfAny(StringStops, i);
                if (stop >= 0 && s[stop] == '"')
                {
                    var plain = s.Substring(i, stop - i);
                    i = stop + 1;
                    return plain;
                }
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    var c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (i >= s.Length) break;
                    var e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                throw new FormatException("unterminated string");
            }

            object Number()
            {
                var start = i;
                if (s[i] == '-') ++i;
                var integral = true;
                while (i < s.Length)
                {
                    var c = s[i];
                    if (c >= '0' && c <= '9')
                    {
                        ++i;
                        continue;
                    }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    {
                        integral = false;
                        ++i;
                        continue;
                    }
                    break;
                }
                var text = s.Substring(start, i - start);
                if (integral && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return (double)l;
                return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
