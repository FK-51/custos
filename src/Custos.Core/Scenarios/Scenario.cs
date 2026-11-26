using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Util;

namespace Custos.Core.Scenarios;

/// <summary>シナリオの1手順。Kind は手順の種類、Args はその引数（YAML をそのまま JSON にしたもの）。</summary>
public sealed record ScenarioStep(string Kind, JsonObject Args, int Line);

/// <summary>
/// シナリオ（操作の列）。探索AIのレポートの「試した操作」、確認用シナリオ（校正用バグ）、隠しシナリオ、適合テストで共用する。
/// 決定的に再生できる形にする（設計書 第4.2節、第12.4節）。
/// </summary>
public sealed record Scenario(string Name, string? Description, IReadOnlyDictionary<string, JsonNode?> Variables, IReadOnlyList<ScenarioStep> Steps, string? SourcePath)
{
    /// <summary>手順の種類（アダプタが実行する）。</summary>
    public static readonly IReadOnlyList<string> KnownKinds =
    [
        "connect", "emit", "emit_concurrent", "remember", "wait", "bot", "settle", "sleep", "expect", "disconnect", "reconnect",
        "raw_connect", "raw_send", "raw_close", "browser_open", "browser_fill", "browser_click", "browser_wait", "browser_read",
        "browser_screenshot", "browser_eval", "set", "include", "reset", "snapshot", "fault", "clock", "restart_app", "drop_connections", "query", "assert", "whose_turn",
    ];

    /// <summary>YAML から読む。</summary>
    public static Scenario Parse(string yaml, string file)
    {
        var errors = new ConfigErrors();
        var root = YamlDocument.Parse(yaml, file, errors);
        var scenario = root == null ? null : FromNode(root, file);
        if (errors.Any || scenario == null)
        {
            throw new CustosException("シナリオを読めません:\n" + string.Join("\n", errors.Items.Select(e => "  " + e)));
        }

        return scenario;
    }

    /// <summary>ファイルから読む。</summary>
    public static Scenario Load(string path) => Parse(File.ReadAllText(path), path) with { SourcePath = path };

    private static Scenario? FromNode(YNode root, string file)
    {
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        var version = map.Whole("version", required: true);
        if (version is { } v && v != 1)
        {
            map.Get("version")?.Error("対応していない版です");
        }

        var name = map.Str("name", required: true) ?? Path.GetFileNameWithoutExtension(file);
        var description = map.Str("description");
        var variables = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (map.Map("variables") is { } vars)
        {
            foreach (var (key, value) in vars.Entries())
            {
                variables[key] = ToJson(value);
            }

            vars.Done();
        }

        var steps = new List<ScenarioStep>();
        var stepsNode = map.Get("steps", required: true);
        if (stepsNode != null)
        {
            foreach (var item in stepsNode.AsList())
            {
                if (item.AsMap() is not { } stepMap)
                {
                    continue;
                }

                var keys = stepMap.Keys.ToList();
                if (keys.Count != 1)
                {
                    item.Error("手順は「種類: 引数」の1組で書いてください");
                    continue;
                }

                var kind = keys[0];
                if (!KnownKinds.Contains(kind, StringComparer.Ordinal))
                {
                    item.Error($"知らない手順です：{kind}（{string.Join(", ", KnownKinds)}）");
                }

                var argsNode = stepMap.Get(kind);
                var args = argsNode == null || argsNode.IsNull ? new JsonObject() : ToJson(argsNode) as JsonObject;
                if (args == null)
                {
                    item.Error("手順の引数はマッピングで書いてください");
                    continue;
                }

                steps.Add(new ScenarioStep(kind, args, (int)item.Node.Start.Line));
                stepMap.Done();
            }
        }

        map.Done();
        return new Scenario(name, description, variables, steps, null);
    }

    /// <summary>YAML のノードを JSON にする（数値と真偽値は型を推定する）。</summary>
    public static JsonNode? ToJson(YNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsMap && node.AsMap() is { } map)
        {
            var obj = new JsonObject();
            foreach (var (key, value) in map.Entries())
            {
                obj[key] = ToJson(value);
            }

            return obj;
        }

        if (node.IsList)
        {
            var array = new JsonArray();
            foreach (var item in node.AsList())
            {
                array.Add(ToJson(item));
            }

            return array;
        }

        if (node.Node is YamlDotNet.RepresentationModel.YamlScalarNode scalar)
        {
            if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain)
            {
                return JsonValue.Create(scalar.Value ?? string.Empty);
            }

            var text = node.Scalar;
            if (text == null)
            {
                return null;
            }

            if (text is "true" or "false")
            {
                return JsonValue.Create(text == "true");
            }

            if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
            {
                return JsonValue.Create(l);
            }

            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && text.Any(char.IsDigit))
            {
                return JsonValue.Create(d);
            }

            return JsonValue.Create(text);
        }

        return null;
    }

    /// <summary>
    /// 値の中の "{名前}" を変数で置き換える。文字列全体が "{名前}" なら、変数の型（数値やオブジェクト）を保つ。
    /// </summary>
    public static JsonNode? Substitute(JsonNode? template, IReadOnlyDictionary<string, JsonNode?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        switch (template)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var pair in obj)
                {
                    copy[Substitute(JsonValue.Create(pair.Key), variables)?.ToString() ?? pair.Key] = Substitute(pair.Value, variables);
                }

                return copy;
            case JsonArray array:
                var list = new JsonArray();
                foreach (var item in array)
                {
                    list.Add(Substitute(item, variables));
                }

                return list;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text.Length > 2 && text[0] == '{' && text[^1] == '}' && text.IndexOf('{', 1) < 0 && variables.TryGetValue(text[1..^1], out var whole))
                {
                    return whole?.DeepClone();
                }

                var result = text;
                foreach (var pair in variables)
                {
                    var token = "{" + pair.Key + "}";
                    if (result.Contains(token, StringComparison.Ordinal))
                    {
                        var replacement = pair.Value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : pair.Value?.ToJsonString() ?? "null";
                        result = result.Replace(token, replacement, StringComparison.Ordinal);
                    }
                }

                return JsonValue.Create(result);
            default:
                return template?.DeepClone();
        }
    }

    /// <summary>シナリオを YAML にする（レポートの操作ログとして残すため）。</summary>
    public string ToYaml()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("version: 1");
        builder.AppendLine("name: " + System.Text.Json.JsonSerializer.Serialize(Name));
        if (Description != null)
        {
            builder.AppendLine("description: " + System.Text.Json.JsonSerializer.Serialize(Description));
        }

        if (Variables.Count > 0)
        {
            builder.AppendLine("variables:");
            foreach (var pair in Variables)
            {
                builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {System.Text.Json.JsonSerializer.Serialize(pair.Key)}: {pair.Value?.ToJsonString() ?? "null"}");
            }
        }

        builder.AppendLine("steps:");
        foreach (var step in Steps)
        {
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  - {step.Kind}: {step.Args.ToJsonString()}");
        }

        return builder.ToString();
    }
}
