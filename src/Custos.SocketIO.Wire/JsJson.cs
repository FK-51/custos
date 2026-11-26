using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Custos.SocketIO.Wire;

/// <summary>
/// JavaScript の JSON.parse に近い読み方。重複したキーは後のものを採る（JSON.parse と同じ）。
/// System.Text.Json の JsonNode.Parse は重複したキーで例外になるため、JsonDocument から組み立て直す。
/// </summary>
public static class JsJson
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 4096 };

    /// <summary>読めたら true。JSON の null は node が null になる。</summary>
    public static bool TryParse(string json, out JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(json);
        node = null;
        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            node = Convert(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>JsonElement を JsonNode にする（重複したキーは後のものを採る）。</summary>
    public static JsonNode? Convert(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = Convert(property.Value);
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Convert(item));
                }

                return array;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    /// <summary>
    /// JavaScript の JSON.stringify と同じ書き方で JSON にする（公式の実装と同じ符号化にするため）。
    /// エスケープは " と \ と制御文字と孤立したサロゲートだけ（16進は小文字）。数値は JavaScript の Number の文字列化の規則に従う。
    /// </summary>
    public static string Stringify(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                return;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var pair in obj)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(pair.Key, builder);
                    builder.Append(':');
                    Write(pair.Value, builder);
                }

                builder.Append('}');
                return;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Write(array[i], builder);
                }

                builder.Append(']');
                return;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteString(value.GetValue<string>(), builder);
                        return;
                    case JsonValueKind.True:
                        builder.Append("true");
                        return;
                    case JsonValueKind.False:
                        builder.Append("false");
                        return;
                    case JsonValueKind.Number:
                        builder.Append(NumberToString(double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)));
                        return;
                    default:
                        builder.Append("null");
                        return;
                }

            default:
                builder.Append(node.ToJsonString());
                return;
        }
    }

    private static void WriteString(string text, StringBuilder builder)
    {
        builder.Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        builder.Append(c).Append(text[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // 孤立したサロゲートは、JSON.stringify（ES2019 以降）と同じくエスケープする
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>JavaScript の Number.prototype.toString()（基数10）と同じ文字列。</summary>
    public static string NumberToString(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "null";
        }

        if (value == 0)
        {
            return "0";
        }

        var negative = value < 0;
        var round = Math.Abs(value).ToString("E16", CultureInfo.InvariantCulture);
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);

        // 最短で元に戻る桁（digits）と、指数（n：小数点の位置）を求める
        string digits;
        int n;
        var ePos = shortest.IndexOfAny(['E', 'e']);
        var mantissa = ePos >= 0 ? shortest[..ePos] : shortest;
        var exponent = ePos >= 0 ? int.Parse(shortest[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        var fracPart = dot >= 0 ? mantissa[(dot + 1)..] : string.Empty;
        digits = (intPart + fracPart).TrimStart('0');
        var leadingZeros = (intPart + fracPart).Length - digits.Length;
        n = intPart.Length + exponent - leadingZeros;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            return "0";
        }

        _ = round;
        var k = digits.Length;
        string result;
        if (k <= n && n <= 21)
        {
            result = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            result = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            result = "0." + new string('0', -n) + digits;
        }
        else
        {
            var e = n - 1;
            var sign = e < 0 ? "-" : "+";
            result = (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + sign + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
        }

        return negative ? "-" + result : result;
    }
}
