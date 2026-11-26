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

        var input = new JsonObject { ["games"] = new JsonArray(games.Select(g => (JsonNode?)new JsonObject { ["game"] = g.Key, ["actions"] = g.Value }).ToArray()) };
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
        _run.WriteJson("reference.json", output);
        var findings = new List<Finding>();
        var compared = 0;
        var unverified = 0;
        foreach (var entry in (output["results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            compared += entry["compared"]?.GetValue<int>() ?? 0;
            unverified += entry["unverified_spans"]?.GetValue<int>() ?? 0;
            foreach (var mismatch in (entry["mismatches"] as JsonArray ?? []).OfType<JsonObject>())
            {
                findings.Add(Finding.Create("reference", "reference_mismatch", Severity.High, 2, "rules", $"参照モデルとアプリの状態が食い違う（対局 {entry["game"]}、手 {mismatch["seq"]}：{mismatch["diff"]?.ToJsonString()}）。どちらが正しいかは文面から判断する（候補）", null, [new Evidence("比較", "reference.json")], $"{entry["game"]}:{mismatch["seq"]}"));
            }
        }

        var cards = (output["supported"]?["cards"] as JsonArray)?.Count ?? 0;
        var summary = new JsonObject
        {
            ["model_version"] = output["model_version"]?.DeepClone(),
            ["games"] = games.Count,
            ["compared"] = compared,
            ["unverified_spans"] = unverified,
            ["supported_cards"] = cards,
            ["mismatches"] = findings.Count,
        };
        _log($"参照モデル（{output["model_version"]}）：{games.Count} 対局、比べた写し {compared}、未検証の区間 {unverified}、対応しているカード {cards}、食い違い {findings.Count}");
        return (summary, findings);
    }
}
