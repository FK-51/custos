using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Custos.Core.Util;

/// <summary>
/// 結果、記録、レポートの JSON の書き方を一か所に揃える。
/// キーは snake_case（設定ファイルの YAML と同じ）にする。
/// </summary>
public static class Json
{
    /// <summary>機械が読む結果を書くときの既定の設定。</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: true);

    /// <summary>1行に1件を書く記録（JSON Lines）用の設定。</summary>
    public static readonly JsonSerializerOptions Compact = CreateOptions(indented: false);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>値を整形した JSON にする。</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>値を1行の JSON にする。</summary>
    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, Compact);

    /// <summary>JSON を読む。読めない、または null の場合は例外にする。</summary>
    public static T Deserialize<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, Options);
        return value ?? throw new CustosException($"JSON を {typeof(T).Name} として読めませんでした");
    }

    /// <summary>
    /// キーの順序を揃えた正規形の JSON にする。ハッシュの計算（状態のハッシュ、設定のハッシュ）に使う。
    /// </summary>
    public static string Canonical(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteCanonical(node, builder);
        return builder.ToString();
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(pair.Key));
                    builder.Append(':');
                    WriteCanonical(pair.Value, builder);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteCanonical(array[i], builder);
                }

                builder.Append(']');
                break;
            case JsonValue value:
                builder.Append(CanonicalScalar(value));
                break;
            default:
                builder.Append(node.ToJsonString());
                break;
        }
    }

    private static string CanonicalScalar(JsonValue value)
    {
        // JsonValue.Create(文字列) などで作った値は JsonElement として取り出せないので、JSON の文字列から読み直す
        using var document = JsonDocument.Parse(value.ToJsonString());
        var element = document.RootElement;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
        {
            // 1 と 1.0 を同じにする
            return number.ToString("R", CultureInfo.InvariantCulture);
        }

        return element.GetRawText();
    }

    /// <summary>JsonElement を JsonNode にする（null は JSON の null として扱う）。</summary>
    public static JsonNode? ToNode(JsonElement element) => JsonNode.Parse(element.GetRawText());

    /// <summary>
    /// 簡単な JSONPath（$.a.b[0].c、$[1]、$.a.* ）で値を取り出す。
    /// 見つからなければ空の列を返す。ワイルドカードは複数の値を返す。
    /// </summary>
    public static IReadOnlyList<JsonNode?> SelectPath(JsonNode? root, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var tokens = JsonPathTokens(path);
        IEnumerable<JsonNode?> current = new[] { root };
        foreach (var token in tokens)
        {
            current = Step(current, token).ToList();
        }

        return current.ToList();
    }

    private static IEnumerable<JsonNode?> Step(IEnumerable<JsonNode?> nodes, string token)
    {
        foreach (var node in nodes)
        {
            if (token == "*")
            {
                if (node is JsonObject wildcardObject)
                {
                    foreach (var pair in wildcardObject)
                    {
                        yield return pair.Value;
                    }
                }
                else if (node is JsonArray wildcardArray)
                {
                    foreach (var item in wildcardArray)
                    {
                        yield return item;
                    }
                }

                continue;
            }

            if (token.StartsWith('#'))
            {
                if (node is JsonArray array
                    && int.TryParse(token.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    if (index < 0)
                    {
                        index += array.Count;
                    }

                    if (index >= 0 && index < array.Count)
                    {
                        yield return array[index];
                    }
                }

                continue;
            }

            if (node is JsonObject obj && obj.TryGetPropertyValue(token, out var child))
            {
                yield return child;
            }
        }
    }

    /// <summary>JSONPath を、プロパティ名（そのまま）と添字（#n）とワイルドカード（*）の列にする。</summary>
    public static IReadOnlyList<string> JsonPathTokens(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var tokens = new List<string>();
        var i = 0;
        if (path.StartsWith('$'))
        {
            i = 1;
        }

        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                i++;
                var start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                {
                    i++;
                }

                tokens.Add(path[start..i]);
            }
            else if (c == '[')
            {
                var end = path.IndexOf(']', i);
                if (end < 0)
                {
                    throw new CustosException($"JSONPath の [ が閉じていません: {path}");
                }

                var inner = path[(i + 1)..end].Trim();
                if (inner == "*")
                {
                    tokens.Add("*");
                }
                else if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"'))
                {
                    tokens.Add(inner[1..^1]);
                }
                else
                {
                    tokens.Add("#" + inner);
                }

                i = end + 1;
            }
            else
            {
                throw new CustosException($"JSONPath を読めません（位置 {i}）: {path}");
            }
        }

        return tokens;
    }
}
