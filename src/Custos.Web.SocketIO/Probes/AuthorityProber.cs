using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Scenarios;
using Custos.Web.SocketIO.State;

namespace Custos.Web.SocketIO.Probes;

/// <summary>
/// 宣言と実態のずれの検査（設計書 第15.3節「サーバー権威の項目」、第15.4節）。
/// authority.yaml でサーバー権威と宣言した状態のキーごとに、その値を表す項目（列の名前、値の位置の最後の名前、キーの最後の名前）を、
/// 目印になる偽の値で、すべての入力（イベント）に付けて送る。状態のどれかが偽の値になれば、サーバーがクライアントの値を信用している。
/// 準備は scenarios/fuzz/context.yaml（対局中）、値の見本は scenarios/fuzz/templates.yaml を使う。
/// </summary>
public sealed class AuthorityProber
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public AuthorityProber(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>実行する。</summary>
    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var context = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "fuzz", "context.yaml");
        if (!File.Exists(context))
        {
            return [Finding.Create("authority_prober", "not_prepared", Severity.Low, 1, "coverage", "宣言と実態のずれの検査の準備（scenarios/fuzz/context.yaml）がありません（未確認）", null, [], "authority")];
        }

        // サーバー権威のキーと、偽の値（型ごとに目印になる値）
        var fakes = new Dictionary<string, (StateKey Key, JsonNode Fake)>(StringComparer.Ordinal);
        var index = 0;
        foreach (var key in _lab.Config.State.Keys.Values.Where(k => k.Source is SqlSource or SocketSource))
        {
            // 真偽の値は、偽の値と本物の値を見分けられないので除く
            if (_lab.Config.Authority.Resolve(key.Name).Authority != Authority.Server || key.Type == StateType.Bool)
            {
                continue;
            }

            index++;
            var fake = Fake(key.Type, index);
            foreach (var field in FieldNames(key))
            {
                fakes.TryAdd(field, (key, fake));
            }
        }

        if (fakes.Count == 0)
        {
            return [];
        }

        var templates = FuzzTemplates.Load(_lab.Workspace);
        var events = _lab.Config.Authority.Inputs.Values.Where(i => i.Kind == "event").Select(i => i.Target).Distinct(StringComparer.Ordinal).ToList();
        var findings = new List<Finding>();
        var rows = new JsonArray();
        foreach (var name in events)
        {
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            var runner = new ScenarioRunner(_lab, _run, _log);
            var prepared = await runner.RunAsync(Scenario.Load(context), cancellationToken).ConfigureAwait(false);
            if (!prepared.Ok)
            {
                throw new CustosException("宣言と実態のずれの検査の準備が失敗しました：" + prepared.Error);
            }

            var variables = runner.Variables.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.Ordinal);
            var sender = variables.TryGetValue("fuzz_member", out var member) ? member?.ToString() ?? "alice" : "alice";
            var args = templates.TryGetValue(name, out var template) ? (JsonArray)template.DeepClone() : new JsonArray(new JsonObject { ["roomId"] = "{room}" });
            var filled = Scenario.Substitute(args, variables) as JsonArray ?? [];
            var payload = filled.Count > 0 && filled[0] is JsonObject obj ? obj : new JsonObject();
            foreach (var (field, (_, fake)) in fakes)
            {
                payload[field] = fake.DeepClone();
            }

            if (filled.Count == 0 || filled[0] is not JsonObject)
            {
                filled.Insert(0, payload);
            }

            var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
            try
            {
                await client.CallAsync("user.emit", new JsonObject { ["user"] = sender, ["event"] = name, ["args"] = filled, ["ack"] = false }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is CustosException or TimeoutException)
            {
                rows.Add(new JsonObject { ["event"] = name, ["error"] = e.Message });
                continue;
            }

            await _lab.SettleAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var state = await new StateReader(_lab).ReadAsync(fakes.Values.Select(f => f.Key).DistinctBy(k => k.Name).ToList(), null, "main", cancellationToken).ConfigureAwait(false);
            var accepted = new List<string>();
            foreach (var result in state)
            {
                var fake = fakes.Values.First(f => f.Key.Name == result.Key).Fake;
                if (result.Values.Any(v => v.Value != null && JsonNode.DeepEquals(Normalize(v.Value), Normalize(fake))))
                {
                    accepted.Add(result.Key);
                }
            }

            rows.Add(new JsonObject { ["event"] = name, ["sender"] = sender, ["sent"] = filled.DeepClone(), ["accepted"] = new JsonArray(accepted.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()) });
            foreach (var key in accepted)
            {
                findings.Add(Finding.Create("authority_prober", "server_authority_violated", Severity.Critical, 1, "rule_break", $"サーバー権威と宣言した {key} を、クライアントが {name} に付けた偽の値で書き換えられました（宣言と実態のずれ。第15.4節）", null, [new Evidence("試した記録", "authority-probe.json")], $"{key}:{name}"));
            }
        }

        _run.WriteJson("authority-probe.json", new JsonObject { ["fields"] = new JsonArray(fakes.Select(f => (JsonNode?)new JsonObject { ["field"] = f.Key, ["key"] = f.Value.Key.Name, ["fake"] = f.Value.Fake.DeepClone() }).ToArray()), ["events"] = rows });
        _log($"宣言と実態のずれ：サーバー権威の項目 {fakes.Count} 個 × イベント {events.Count} 個（受け入れられた {findings.Count} 件）");
        return findings;
    }

    /// <summary>状態のキーを表しそうな項目の名前（列の名前、値の位置の最後の名前、キーの最後の名前のキャメルケース）。</summary>
    private static IEnumerable<string> FieldNames(StateKey key)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var last = key.Name.Split('.')[^1];
        names.Add(Camel(last));
        switch (key.Source)
        {
            case SqlSource sql:
                var select = sql.Sql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
                var from = sql.Sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
                if (select >= 0 && from > select)
                {
                    var columns = sql.Sql[(select + 6)..from].Split(',');
                    var column = columns[^1].Trim();
                    var dot = column.LastIndexOf('.');
                    column = (dot >= 0 ? column[(dot + 1)..] : column).Trim('"', ' ', ')');
                    if (column.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    {
                        names.Add(column);
                    }
                }

                break;
            case SocketSource socket:
                names.Add(socket.Path.Split('.')[^1]);
                break;
        }

        return names.Where(n => n.Length > 1 && n != "id");
    }

    private static string Camel(string snake)
    {
        var parts = snake.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static JsonNode Fake(StateType type, int index) => type switch
    {
        StateType.Integral => JsonValue.Create(7700 + index),
        StateType.Real => JsonValue.Create(7700.5 + index),
        StateType.Bool => JsonValue.Create(true),
        StateType.List => new JsonArray(77, 77),
        _ => JsonValue.Create($"custos-fake-{index.ToString(CultureInfo.InvariantCulture)}"),
    };

    private static JsonNode? Normalize(JsonNode? node) =>
        node is JsonValue v && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? JsonValue.Create(number) : node?.DeepClone();
}
