using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;

namespace Custos.Web.SocketIO.Repro;

/// <summary>
/// 参照モデルとの食い違いの検査（設計書 第25.9節、第27.2節の段階6）。手のログを参照モデル（検証用リポジトリの reference-model/model.js）で再生し、
/// ログの状態の写しと比べる。参照モデルは管理側で、ネットワークなしのコンテナで動かす（node の土台のイメージ。ダイジェストで固定）。
/// 食い違いは候補（段階2）。参照モデル側が誤っていることもあるので、カードの文面を読んで別のAIか人間が判断する。
/// </summary>
public sealed class ReferenceHarness
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public ReferenceHarness(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>
    /// 対局ごとの、提示された選択肢の列（{ seq, turn_number, kind, choices }。届いた順）。
    /// 選択の要求のイベントと項目の位置は rules/replay.yaml の prompts で指定する（なければ渡さない）。
    /// 対局と要求は部屋の ID で結び付ける（同じ部屋に対局が2つ以上あるときは、どの対局の要求か分からないので渡さない）。
    /// </summary>
    private async Task<Dictionary<string, JsonArray>> PromptsByGameAsync(JsonObject? config, IEnumerable<string> gameIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        if (config?["prompts"] is not JsonObject spec || spec["event"]?.ToString() is not { } eventName)
        {
            return result;
        }

        var sessions = await _lab.QueryAsync(spec["session_sql"]?.ToString() ?? "SELECT id, roomId FROM GameSession", cancellationToken).ConfigureAwait(false);
        var gamesByRoom = (sessions["rows"] as JsonArray ?? []).OfType<JsonArray>()
            .Select(r => (Game: r[0]?.ToString() ?? string.Empty, Room: r[1]?.ToString() ?? string.Empty))
            .Where(r => gameIds.Contains(r.Game, StringComparer.Ordinal))
            .GroupBy(r => r.Room, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Game, StringComparer.Ordinal);
        string? Text(JsonNode? payload, string key) => spec[key]?.ToString() is { } path ? FirstOf(Json.SelectPath(payload, path))?.ToString() : null;
        foreach (var entry in _lab.Records.Events(0, "s2c", eventName))
        {
            var payload = entry.Args is JsonArray args && args.Count > 0 ? args[0] : null;
            if (Text(payload, "room_path") is not { } room || !gamesByRoom.TryGetValue(room, out var gameId))
            {
                continue;
            }

            if (!result.TryGetValue(gameId, out var list))
            {
                list = [];
                result[gameId] = list;
            }

            list.Add(new JsonObject
            {
                ["seq"] = entry.Seq,
                ["turn_number"] = int.TryParse(Text(payload, "turn_number_path"), out var turn) ? turn : null,
                ["kind"] = Text(payload, "kind_path"),
                ["choices"] = spec["choices_path"]?.ToString() is { } choicesPath ? FirstOf(Json.SelectPath(payload, choicesPath))?.DeepClone() : null,
            });
        }

        return result;
    }

    private static JsonNode? FirstOf(IReadOnlyList<JsonNode?> nodes) => nodes.Count > 0 ? nodes[0] : null;

    /// <summary>実行する。</summary>
    public async Task<(JsonObject Summary, IReadOnlyList<Finding> Findings)> RunAsync(string? game, CancellationToken cancellationToken)
    {
        var model = Path.Combine(_lab.Workspace.VerifyRepo, "reference-model");
        if (!File.Exists(Path.Combine(model, "model.js")))
        {
            return (new JsonObject { ["model"] = null }, [Finding.Create("reference", "no_model", Severity.Low, 2, "coverage", "参照モデル（reference-model/model.js）がないため、規則の食い違いは調べていません（未確認。段階A）", null, [], "no_model")]);
        }

        var replay = Path.Combine(_lab.Workspace.VerifyRepo, "rules", "replay.yaml");
        var errors = new ConfigErrors();
        var config = File.Exists(replay) ? Scenario.ToJson(YamlDocument.Parse(File.ReadAllText(replay), "replay.yaml", errors) ?? throw new CustosException("replay.yaml を読めません")) as JsonObject : null;
        var sql = config?["reference_sql"]?.ToString() ?? "SELECT gameSessionId, sequenceNumber, actionType, playerIndex, changesJson, stateSnapshotJson FROM GameLogAction ORDER BY gameSessionId, sequenceNumber";
        var rows = await _lab.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
        var games = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var row in (rows["rows"] as JsonArray ?? []).OfType<JsonArray>())
        {
            var id = row[0]?.ToString() ?? string.Empty;
            if (game != null && id != game)
            {
                continue;
            }

            if (!games.TryGetValue(id, out var list))
            {
                list = [];
                games[id] = list;
            }

            list.Add(new JsonObject { ["seq"] = row[1]?.DeepClone(), ["type"] = row[2]?.DeepClone(), ["player"] = row[3]?.DeepClone(), ["changes"] = row[4]?.DeepClone(), ["snapshot"] = row[5]?.DeepClone() });
        }

        // 提示された選択肢（設計書 第25.9節「打てる手の列挙」と比べるため）：対局の部屋で各プレイヤーに送られた選択の要求を、届いた順に渡す。
        // 手のログには、何枚から選ばせたかが残らない（選んだ1枚だけ）ので、候補の数の誤りは手のログだけでは見えない
        var prompts = await PromptsByGameAsync(config, games.Keys, cancellationToken).ConfigureAwait(false);
        var input = new JsonObject { ["games"] = new JsonArray(games.Select(g => (JsonNode?)new JsonObject { ["game"] = g.Key, ["actions"] = g.Value, ["prompts"] = prompts.GetValueOrDefault(g.Key) ?? [] }).ToArray()) };
        var node = new Lab.ImageBuilder(_lab.Workspace, _lab.Docker, _log).BaseImages()["node"];
        // コンテナに渡すのは、データの置き場所に写した実行器（フレームワークのリポジトリは、コンテナの実行環境から見えるとは限らない）
        var harness = Path.Combine(_lab.Workspace.DataDir, "tmp", "reference-harness");
        Directory.CreateDirectory(harness);
        File.Copy(Path.Combine(_lab.Workspace.FrameworkRepo, "adapters", "reference-model", "runner.js"), Path.Combine(harness, "runner.js"), overwrite: true);
        var modelCopy = Path.Combine(_lab.Workspace.DataDir, "tmp", "reference-model");
        if (Directory.Exists(modelCopy))
        {
            Directory.Delete(modelCopy, recursive: true);
        }

        Directory.CreateDirectory(modelCopy);
        foreach (var file in Directory.GetFiles(model))
        {
            File.Copy(file, Path.Combine(modelCopy, Path.GetFileName(file)));
        }

        model = modelCopy;
        var result = await _lab.Docker.RunAsync(["run", "--rm", "-i", "--network", "none", "-v", model + ":/ref:ro", "-v", harness + ":/harness:ro", "--entrypoint", "node", node, "/harness/runner.js"], TimeSpan.FromMinutes(10), input.ToJsonString(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new CustosException("参照モデルの実行に失敗しました：" + result.StandardError);
        }

        var output = JsonNode.Parse(result.StandardOutput) as JsonObject ?? [];

        // 同じ実行の中で何度呼ばれても（カナリアごと、校正の探索ごと）証拠がぶつからないようにする
        var evidence = _run.FreeName("reference.json");
        _run.WriteJson(evidence, output);
        var findings = new List<Finding>();
        var compared = 0;
        var checkedActions = 0;
        var unverified = 0;
        var violations = 0;
        var applyMismatches = 0;
        foreach (var entry in (output["results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            compared += entry["compared"]?.GetValue<int>() ?? 0;
            checkedActions += entry["checked"]?.GetValue<int>() ?? 0;
            unverified += entry["unverified_spans"]?.GetValue<int>() ?? 0;
            foreach (var mismatch in (entry["mismatches"] as JsonArray ?? []).OfType<JsonObject>())
            {
                findings.Add(Finding.Create("reference", "reference_mismatch", Severity.High, 2, "rules", $"参照モデルとアプリの状態が食い違う（対局 {entry["game"]}、手 {mismatch["seq"]}：{mismatch["diff"]?.ToJsonString()}）。どちらが正しいかは文面から判断する（候補）", null, [new Evidence("比較", evidence)], $"{entry["game"]}:{mismatch["seq"]}"));
            }

            // 検査役の形：手ごとに、アプリが記録した変化が規則に合うか（候補。参照モデルの解釈が誤っていることもある）
            foreach (var violation in (entry["violations"] as JsonArray ?? []).OfType<JsonObject>())
            {
                violations++;
                var rule = violation["rule"]?.ToString() ?? "unknown";
                findings.Add(Finding.Create("reference", "rule_violation", Severity.High, 2, "rules", $"参照モデルの規則の確認：{rule}：{violation["message"]}（対局 {entry["game"]}、手 {violation["seq"]}（{violation["type"]}、手番 {violation["player"]}））。どちらが正しいかは文面から判断する（候補）", null, [new Evidence("確認", evidence)], $"{rule}:{violation["type"]}"));
            }

            // 参照モデルの apply（変化の当て方）と写しの食い違い。参照モデル側の誤りのことが多いので、網羅の不足として出す
            foreach (var mismatch in (entry["apply_mismatches"] as JsonArray ?? []).OfType<JsonObject>())
            {
                applyMismatches++;
                findings.Add(Finding.Create("reference", "apply_mismatch", Severity.Low, 2, "coverage", $"参照モデルで変化を当てた状態が、アプリの写しと食い違う（対局 {entry["game"]}、手 {mismatch["seq"]}：{mismatch["diff"]?.ToJsonString()}）。参照モデルの apply を直すまで、その区間の確認は当てにならない", null, [new Evidence("確認", evidence)], $"apply:{entry["game"]}:{mismatch["seq"]}"));
            }
        }

        var cards = (output["supported"]?["cards"] as JsonArray)?.Count ?? 0;
        var summary = new JsonObject
        {
            ["model_version"] = output["model_version"]?.DeepClone(),
            ["games"] = games.Count,
            ["compared"] = compared,
            ["checked_actions"] = checkedActions,
            ["unverified_spans"] = unverified,
            ["supported_cards"] = cards,
            ["violations"] = violations,
            ["apply_mismatches"] = applyMismatches,
            ["mismatches"] = findings.Count(f => f.Rule == "reference_mismatch"),
        };
        _log($"参照モデル（{output["model_version"]}）：{games.Count} 対局、確かめた手 {checkedActions}、比べた写し {compared}、未検証の区間 {unverified}、対応しているカード {cards}、規則に反する点 {violations}、食い違い {summary["mismatches"]}、apply の食い違い {applyMismatches}");
        return (summary, findings);
    }
}
