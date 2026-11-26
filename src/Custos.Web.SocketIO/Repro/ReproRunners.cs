using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Web.SocketIO.Repro;

/// <summary>
/// 手のログからの再生（一致の検査。設計書 第25.10節、第27.2節の段階2）。
/// ログに残った状態の写し（スナップショット）から、次の写しまでの変化（changes）を、アプリ自身の「変化を当てる関数」で当て直し、
/// 次の写しと一致するかを確かめる。関数はアプリのコンテナの中で、管理側から実行する（アプリのプロセスには触れない）。
/// 設定は検証用リポジトリの rules/replay.yaml（log_sql、apply.module、apply.function、ignore_paths）。
/// </summary>
public sealed class ReplayChecker
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public ReplayChecker(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>再生する。game を指定すると、その対局だけ。</summary>
    public async Task<IReadOnlyList<Finding>> RunAsync(string? game, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_lab.Workspace.VerifyRepo, "rules", "replay.yaml");
        if (!File.Exists(path))
        {
            return [Finding.Create("replay", "not_prepared", Severity.Low, 1, "coverage", "再生の設定（rules/replay.yaml）がないため、手のログからの再生をしていません（未確認）", null, [], "replay")];
        }

        var errors = new ConfigErrors();
        var root = YamlDocument.Parse(File.ReadAllText(path), "replay.yaml", errors);
        var config = root == null ? null : Scenario.ToJson(root) as JsonObject;
        if (config == null || errors.Any)
        {
            throw new CustosException("rules/replay.yaml を読めません：" + string.Join("; ", errors.Items));
        }

        var sql = config["log_sql"]?.GetValue<string>() ?? throw new CustosException("replay.yaml に log_sql がありません");
        var module = config["apply"]?["module"]?.GetValue<string>() ?? throw new CustosException("replay.yaml に apply.module がありません");
        var function = config["apply"]?["function"]?.GetValue<string>() ?? throw new CustosException("replay.yaml に apply.function がありません");
        var ignore = (config["ignore_paths"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToList();
        var result = await _lab.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
        var games = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var row in (result["rows"] as JsonArray ?? []).OfType<JsonArray>())
        {
            var id = row[0]?.ToString() ?? string.Empty;
            if (game != null && id != game)
            {
                continue;
            }

            if (!games.TryGetValue(id, out var actions))
            {
                actions = [];
                games[id] = actions;
            }

            actions.Add(new JsonObject { ["seq"] = row[1]?.DeepClone(), ["changes"] = row[2]?.DeepClone(), ["snapshot"] = row.Count > 3 ? row[3]?.DeepClone() : null });
        }

        if (games.Count == 0)
        {
            return [Finding.Create("replay", "no_games", Severity.Low, 1, "coverage", "再生する対局がありません（未確認）", null, [], "no_games")];
        }

        var input = new JsonObject { ["games"] = new JsonArray(games.Select(g => (JsonNode?)new JsonObject { ["game"] = g.Key, ["actions"] = g.Value }).ToArray()), ["ignore"] = new JsonArray(ignore.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) };
        var script = ReplayScript.Replace("__MODULE__", JsonValue.Create(module).ToJsonString(), StringComparison.Ordinal).Replace("__FUNCTION__", JsonValue.Create(function).ToJsonString(), StringComparison.Ordinal);
        var run = await _lab.Docker.RunAsync(["exec", "-i", Lab.Containers.App(_lab.Project), "node", "-e", script], TimeSpan.FromMinutes(5), input.ToJsonString(), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!run.Succeeded)
        {
            throw new CustosException("再生に失敗しました：" + (run.StandardError.Length > 0 ? run.StandardError : run.StandardOutput));
        }

        var output = JsonNode.Parse(run.StandardOutput) as JsonArray ?? [];
        _run.WriteJson("replay.json", output);
        var findings = new List<Finding>();
        var compared = 0;
        foreach (var entry in output.OfType<JsonObject>())
        {
            compared += entry["compared"]?.GetValue<int>() ?? 0;
            foreach (var mismatch in (entry["mismatches"] as JsonArray ?? []).OfType<JsonObject>())
            {
                findings.Add(Finding.Create("replay", "replay_mismatch", Severity.High, 1, "replica", $"手のログを再生した状態が、ログの状態の写しと一致しません（対局 {entry["game"]}、手 {mismatch["seq"]}：{mismatch["diff"]?.ToJsonString()}）", null, [new Evidence("再生の結果", "replay.json")], $"{entry["game"]}:{mismatch["seq"]}"));
            }

            if (entry["error"] is { } error)
            {
                findings.Add(Finding.Create("replay", "replay_error", Severity.High, 1, "replica", $"手のログを再生できません（対局 {entry["game"]}）：{error}", null, [new Evidence("再生の結果", "replay.json")], $"{entry["game"]}:error"));
            }
        }

        _log($"再生：{games.Count} 対局、写しとの比較 {compared} 回、食い違い {findings.Count} 件");
        return findings;
    }

    /// <summary>アプリのコンテナで実行する再生のスクリプト（標準入力から対局を受け取り、標準出力に結果を返す）。</summary>
    private const string ReplayScript = """
        const mod = require(__MODULE__);
        const apply = mod[__FUNCTION__];
        let input = '';
        process.stdin.setEncoding('utf8');
        process.stdin.on('data', (d) => (input += d));
        process.stdin.on('end', () => {
          const { games, ignore } = JSON.parse(input);
          const skip = new Set(ignore || []);
          const diff = (a, b, path, out) => {
            if (out.length >= 5 || skip.has(path)) return;
            if (a === b) return;
            if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') { out.push({ path, replayed: a, logged: b }); return; }
            if (Array.isArray(a) !== Array.isArray(b)) { out.push({ path, replayed: 'array?' + Array.isArray(a), logged: 'array?' + Array.isArray(b) }); return; }
            const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
            for (const k of keys) diff(a[k], b[k], path + (Array.isArray(a) ? '[' + k + ']' : '.' + k), out);
          };
          const results = [];
          for (const g of games) {
            const r = { game: g.game, actions: g.actions.length, compared: 0, mismatches: [] };
            try {
              let state = null;
              for (const a of g.actions.sort((x, y) => Number(x.seq) - Number(y.seq))) {
                const changes = a.changes ? JSON.parse(a.changes) : [];
                if (state !== null) state = apply(state, changes);
                if (a.snapshot) {
                  const snap = JSON.parse(a.snapshot);
                  if (state !== null) {
                    const out = [];
                    diff(JSON.parse(JSON.stringify(state)), snap, '$', out);
                    r.compared++;
                    if (out.length > 0) r.mismatches.push({ seq: a.seq, diff: out });
                  }
                  state = snap;
                }
              }
            } catch (e) {
              r.error = String(e && e.message || e);
            }
            results.push(r);
          }
          process.stdout.write(JSON.stringify(results));
        });
        """;
}

/// <summary>
/// 制限時間の検査（設計書 第25.8節）：テスト用APIで時刻を止め、時間切れの直前・直後で、着手と時間切れを同時に起こす。
/// 手番がちょうど1回だけ進むこと（次の人への選択の要求が1回だけ、同じ人に同じ手番の要求が二度来ない）と、不変条件を確かめる。
/// 準備は検証用リポジトリの scenarios/time/setup.yaml（set: { current, other } を作る。whose_turn を使う）。
/// </summary>
public sealed class TimeProber
{
    private static readonly int[] Margins = [30, 10, 2, 0, -2, -10, -30];
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public TimeProber(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>実行する。</summary>
    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        if (!await _lab.HasTestApiAsync(cancellationToken).ConfigureAwait(false))
        {
            return [Finding.Create("time_prober", "no_test_api", Severity.Low, 2, "coverage", "テスト用API（時刻の制御）がないため、制限時間の検査をしていません（未確認。custos up --build test で起動する）", null, [], "no_test_api")];
        }

        var setup = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "time", "setup.yaml");
        if (!File.Exists(setup))
        {
            return [Finding.Create("time_prober", "not_prepared", Severity.Low, 2, "coverage", "制限時間の検査の準備（scenarios/time/setup.yaml）がありません（未確認）", null, [], "not_prepared")];
        }

        var protocol = BotProtocolLoader.Load(_lab.Workspace);
        var choiceEvent = protocol["choice_event"]?.GetValue<string>() ?? string.Empty;
        var deadlinePath = protocol["deadline_path"]?.GetValue<string>() ?? "$.deadline";
        var findings = new List<Finding>();
        var rounds = new JsonArray();
        foreach (var margin in Margins)
        {
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            await _lab.TestApiAsync("POST", "/__verify/clock", new JsonObject { ["action"] = "resume" }, cancellationToken).ConfigureAwait(false);
            var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
            var runner = new ScenarioRunner(_lab, _run, _log);
            var result = await runner.RunAsync(Scenario.Load(setup), cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                rounds.Add(new JsonObject { ["margin_ms"] = margin, ["error"] = result.Error });
                continue;
            }

            var current = runner.Variables.TryGetValue("current", out var currentNode) ? currentNode?.ToString() ?? string.Empty : string.Empty;
            var other = runner.Variables.TryGetValue("other", out var otherNode) ? otherNode?.ToString() ?? string.Empty : string.Empty;
            var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
            var frozen = await _lab.TestApiAsync("POST", "/__verify/clock", new JsonObject { ["action"] = "freeze" }, cancellationToken).ConfigureAwait(false);
            var events = await client.CallObjectAsync("user.events", new JsonObject { ["user"] = current, ["since"] = 0 }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var pending = (events["events"] as JsonArray ?? []).OfType<JsonObject>().LastOrDefault(e => e["event"]?.GetValue<string>() == choiceEvent);
            var payload = (pending?["args"] as JsonArray)?.FirstOrDefault();
            var deadlineNodes = Json.SelectPath(payload, deadlinePath);
            var deadline = deadlineNodes.Count > 0 && deadlineNodes[0] is JsonValue dv && dv.TryGetValue<double>(out var d) ? d : (double?)null;
            var now = frozen["now"]?.GetValue<double>() ?? frozen["now_ms"]?.GetValue<double>();
            if (deadline == null || now == null)
            {
                rounds.Add(new JsonObject { ["margin_ms"] = margin, ["error"] = "期限または時計の現在時刻が読めません", ["clock"] = frozen.DeepClone() });
                await _lab.TestApiAsync("POST", "/__verify/clock", new JsonObject { ["action"] = "resume" }, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 期限の margin ミリ秒前まで進め、着手と「期限を過ぎるまで進める」を同時に送る
            var toEdge = Math.Max(0, deadline.Value - now.Value - margin);
            await _lab.TestApiAsync("POST", "/__verify/clock", new JsonObject { ["action"] = "advance", ["ms"] = toEdge }, cancellationToken).ConfigureAwait(false);
            var cursorOther = (await client.CallObjectAsync("user.info", new JsonObject { ["user"] = other }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))["last_seq"]?.GetValue<long>() ?? 0;
            var cursorCurrent = (await client.CallObjectAsync("user.info", new JsonObject { ["user"] = current }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))["last_seq"]?.GetValue<long>() ?? 0;
            var choices = Json.SelectPath(payload, protocol["choices_path"]?.GetValue<string>() ?? "$.choices");
            var choice = choices.Count > 0 && choices[0] is JsonArray list && list.Count > 0 ? list[0] : null;
            if (choice is JsonObject obj && protocol["object_choice_field"]?.GetValue<string>() is { } field)
            {
                choice = obj[field];
            }

            var variables = new Dictionary<string, JsonNode?>(runner.Variables.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.Ordinal), StringComparer.Ordinal) { ["choice"] = choice?.DeepClone() };
            var reply = Scenario.Substitute(protocol["reply_template"]?.DeepClone(), variables);
            var send = client.CallAsync("user.emit", new JsonObject { ["user"] = current, ["event"] = protocol["reply_event"]?.GetValue<string>(), ["args"] = new JsonArray(reply), ["ack"] = false }, TimeSpan.FromSeconds(10), cancellationToken);
            var tick = _lab.TestApiAsync("POST", "/__verify/clock", new JsonObject { ["action"] = "advance", ["ms"] = Math.Abs(margin) * 2 + 20 }, cancellationToken);
            await Task.WhenAll(send, tick).ConfigureAwait(false);
            await _lab.TestApiAsync("POST", "/__verify/clock", new JsonObject { ["action"] = "resume" }, cancellationToken).ConfigureAwait(false);
            await _lab.TestApiAsync("POST", "/__verify/settle", new JsonObject { ["timeout_ms"] = 15000 }, cancellationToken).ConfigureAwait(false);
            await _lab.SettleAsync(TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

            var otherEvents = await client.CallObjectAsync("user.events", new JsonObject { ["user"] = other, ["since"] = cursorOther }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var currentEvents = await client.CallObjectAsync("user.events", new JsonObject { ["user"] = current, ["since"] = cursorCurrent }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var nextRequests = (otherEvents["events"] as JsonArray ?? []).Count(e => e?["event"]?.GetValue<string>() == choiceEvent);
            var repeatRequests = (currentEvents["events"] as JsonArray ?? []).Count(e => e?["event"]?.GetValue<string>() == choiceEvent);
            var judges = await JudgeRunner.RunAsync(new JudgeContext(_lab, _run, baseline), [new AppHealthJudge(), new LogJudge(), new InvariantJudge()], cancellationToken).ConfigureAwait(false);
            var judgeFindings = judges.SelectMany(r => r.Findings).Where(f => f.Stage == 1).ToList();
            var round = new JsonObject
            {
                ["margin_ms"] = margin,
                ["current"] = current,
                ["deadline"] = deadline,
                ["choice_sent"] = choice?.DeepClone(),
                ["next_player_requests"] = nextRequests,
                ["same_player_requests"] = repeatRequests,
                ["judge_findings"] = judgeFindings.Count,
            };
            rounds.Add(round);

            // ちょうど1回だけ進む：次の人への要求が1回。時間切れの自動処理と着手の両方が通ると、2回になるか、手番が飛ぶ
            if (nextRequests > 1)
            {
                findings.Add(Finding.Create("time_prober", "double_advance", Severity.High, 1, "time", $"時間切れと着手が同時に起きると、手番が2回進みました（期限の {margin} ms 前に着手。次の人への選択の要求 {nextRequests} 回）", null, [new Evidence("試した記録", "time-rounds.json")], "double_advance"));
            }

            findings.AddRange(judgeFindings.Select(f => f with { Summary = $"時間切れと着手の同時（{margin} ms）の後：" + f.Summary }));
        }

        _run.WriteJson("time-rounds.json", rounds);
        _log($"制限時間の検査：{Margins.Length} 通り（問題 {findings.Count} 件）");
        return findings;
    }
}

/// <summary>
/// 切断・再接続・再起動の注入（設計書 第25.8節、第26.5節）。検証用リポジトリの scenarios/disruption/*.yaml を順に実行し、
/// シナリオの中の確認（expect、query の rows、assert）と、汎用の判定・不変条件で判定する。
/// </summary>
public sealed class DisruptionProber
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public DisruptionProber(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>実行する。</summary>
    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var dir = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "disruption");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.yaml").Order(StringComparer.Ordinal).ToList() : [];
        if (files.Count == 0)
        {
            return [Finding.Create("disruption", "not_prepared", Severity.Low, 1, "coverage", "切断・再起動の注入のシナリオ（scenarios/disruption/）がありません（未確認）", null, [], "not_prepared")];
        }

        var findings = new List<Finding>();
        var summary = new JsonArray();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
            var scenario = Scenario.Load(file);
            var result = await new ScenarioRunner(_lab, _run, _log).RunAsync(scenario, cancellationToken).ConfigureAwait(false);
            _run.WriteJson($"disruption/{name}.json", JsonNode.Parse(Json.SerializeCompact(result)));
            await _lab.SettleAsync(TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var judges = await JudgeRunner.RunAsync(new JudgeContext(_lab, _run, baseline), JudgeRunner.Stage1(), cancellationToken).ConfigureAwait(false);
            var judged = judges.SelectMany(r => r.Findings).Where(f => !(f.Rule is "restart" or "crash" && scenario.Steps.Any(s => s.Kind == "restart_app"))).ToList();
            summary.Add(new JsonObject { ["scenario"] = name, ["ok"] = result.Ok, ["error"] = result.Error, ["judge_findings"] = judged.Count });
            if (!result.Ok)
            {
                var description = scenario.Description ?? name;
                findings.Add(Finding.Create("disruption", "disruption_check_failed", Severity.High, 1, "game_stall", $"{description}：{result.Error}", null, [new Evidence("シナリオの結果", $"disruption/{name}.json")], name));
            }

            findings.AddRange(judged.Select(f => f with { Summary = $"{name} の後：" + f.Summary }));
            _log($"注入 {name}：{(result.Ok ? "確認に合格" : "失敗：" + result.Error)}、判定 {judged.Count} 件");
        }

        _run.WriteJson("disruption.json", summary);
        return findings;
    }
}

/// <summary>小さな補助。</summary>
internal static class ReproText
{
    public static string N(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}
