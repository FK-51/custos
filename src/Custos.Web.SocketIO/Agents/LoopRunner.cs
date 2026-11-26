using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Probes;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Web.SocketIO.Agents;

/// <summary>探索役。</summary>
public interface IExplorer
{
    /// <summary>名前と版（登録簿の found_by に使う）。</summary>
    string Name { get; }

    /// <summary>確認対象を1件確かめて、レポートを返す。</summary>
    Task<TrialReport> ExploreAsync(LedgerEntry? entry, string trialId, int seed, CancellationToken cancellationToken);
}

/// <summary>
/// 決定的な代役の探索役（AIを使わない。設計書 第27.2節の段階3を、API キーなしで一巡させるため。判断 D3）。
/// 確認対象に合わせた操作を、スクリプトクライアントとボットで行い、観測は段階1の機械判定で行う。
/// 操作は検証用リポジトリの scenarios/explore/&lt;種類&gt;.yaml（{target}、{seed} を使える）。なければ自由な対局。
/// </summary>
public sealed class ScriptedExplorer : IExplorer
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public ScriptedExplorer(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <inheritdoc />
    public string Name => $"scripted-explorer@{FrameworkInfo.Version}";

    /// <inheritdoc />
    public async Task<TrialReport> ExploreAsync(LedgerEntry? entry, string trialId, int seed, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var kind = entry?.Kind ?? "free";
        var file = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "explore", kind + ".yaml");
        if (!File.Exists(file))
        {
            file = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "explore", "free.yaml");
        }

        await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var from = _lab.Records.Activity().LastSeq;
        var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
        var runner = new ScenarioRunner(_lab, _run, _log);
        runner.Variables["target"] = JsonValue.Create(entry?.Target ?? string.Empty);
        runner.Variables["situation"] = JsonValue.Create(entry?.Situation ?? string.Empty);
        runner.Variables["seed"] = JsonValue.Create(seed);
        var scenario = Scenario.Load(file);
        var result = await runner.RunAsync(scenario, cancellationToken).ConfigureAwait(false);
        await _lab.SettleAsync(TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var to = _lab.Records.Activity().LastSeq;

        // 操作：ボットの着手と、シナリオで送ったイベント（記録と同じ形で書く）
        var actions = new List<TrialAction>();
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var protocol = BotProtocolLoader.Load(_lab.Workspace);
        foreach (var step in result.Steps.Where(s => s.Ok))
        {
            if (step.Kind == "bot" && step.Data is JsonObject bot)
            {
                foreach (var move in (bot["trace"] as JsonArray ?? []).OfType<JsonObject>().Where(m => m["stuck"] == null))
                {
                    var user = move["user"]?.ToString() ?? string.Empty;
                    var reply = Scenario.Substitute(protocol["reply_template"]?.DeepClone(), new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["room"] = runner.Variables.TryGetValue("room", out var room) ? room?.DeepClone() : null, ["choice"] = move["choice"]?.DeepClone() });
                    actions.Add(new TrialAction(user, protocol["reply_event"]?.ToString() ?? string.Empty, new JsonArray(reply), $"{move["kind"]} を選んだ"));
                }
            }
        }

        // シナリオで直接送ったイベント（最上位の emit。値は実行後の変数で埋める）
        var emitSteps = scenario.Steps.Where(st => st.Kind == "emit").ToList();
        var emitResults = result.Steps.Where(st => st.Kind == "emit").ToList();
        for (var i = 0; i < Math.Min(emitSteps.Count, emitResults.Count); i++)
        {
            if (!emitResults[i].Ok || Scenario.Substitute(emitSteps[i].Args.DeepClone(), runner.Variables.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)) is not JsonObject step)
            {
                continue;
            }

            actions.Add(new TrialAction(step["user"]?.ToString() ?? string.Empty, step["event"]?.ToString() ?? string.Empty, step["args"] as JsonArray is { } stepArgs ? (JsonArray)stepArgs.DeepClone() : [], "確認対象のイベントを送った"));
        }

        foreach (var user in actions.Select(a => a.User).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var info = await client.CallObjectAsync("user.info", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (info["socket_id"]?.GetValue<string>() is { } socketId)
                {
                    users[user] = socketId;
                }
            }
            catch (CustosException)
            {
            }
        }

        // 観測：段階1の機械判定
        var reports = await JudgeRunner.RunAsync(new JudgeContext(_lab, _run, baseline), JudgeRunner.Stage1(), cancellationToken).ConfigureAwait(false);
        var findings = reports.SelectMany(r => r.Findings).Where(f => f.Stage == 1).ToList();
        var dir = $"trials/{trialId}";
        var evidence = new List<TrialEvidence>();
        var recordsPath = _run.WriteText($"{dir}/records.jsonl", string.Join("\n", File.ReadLines(_lab.Records.EventsPath).Skip((int)Math.Max(0, from)).Take((int)Math.Max(0, to - from))));
        evidence.Add(new TrialEvidence("records", Path.GetRelativePath(_run.Dir, recordsPath), "この試行の通信の記録"));
        var judgePath = _run.WriteText($"{dir}/judges.txt", JudgeRunner.Summarize(reports));
        evidence.Add(new TrialEvidence("log", Path.GetRelativePath(_run.Dir, judgePath), "機械判定の結果"));
        var scenarioPath = _run.WriteText($"{dir}/scenario.json", Json.Serialize(result));
        evidence.Add(new TrialEvidence("log", Path.GetRelativePath(_run.Dir, scenarioPath), "実行したシナリオと結果"));
        var notes = new StringBuilder();
        notes.Append(CultureInfo.InvariantCulture, $"シナリオ {Path.GetFileName(file)}：{(result.Ok ? "最後まで実行" : "途中で失敗：" + result.Error)}。");
        notes.Append(findings.Count == 0 ? "機械判定で問題は見つからなかった。文面どおりかの判断はしていない（未確認）。" : $"機械判定で {findings.Count} 件：{findings[0].Summary}");
        var session = Path.GetFileName(_lab.Records.Dir);
        return new TrialReport(
            trialId,
            entry?.Id ?? $"free:{trialId}",
            "explorer",
            "scripted",
            null,
            Path.GetFileName(file),
            actions,
            findings.Count > 0 ? "bug" : "no_bug",
            evidence,
            $"records:{session}:{from.ToString(CultureInfo.InvariantCulture)}-{to.ToString(CultureInfo.InvariantCulture)}",
            new TrialCost(0, Math.Round(watch.Elapsed.TotalSeconds, 1), null, null),
            notes.ToString(),
            users,
            session,
            from,
            to)
        { BugIds = [] };
    }
}

/// <summary>Claude Code の探索役（API キーがあるときだけ動く）。</summary>
public sealed class ClaudeCodeExplorer : IExplorer
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly string? _model;
    private readonly string _role;

    /// <summary>作る。</summary>
    public ClaudeCodeExplorer(Lab.Lab lab, Run run, string role, string? model)
    {
        _lab = lab;
        _run = run;
        _role = role;
        _model = model;
    }

    /// <inheritdoc />
    public string Name => $"claude-code:{_model ?? "default"}";

    /// <inheritdoc />
    public async Task<TrialReport> ExploreAsync(LedgerEntry? entry, string trialId, int seed, CancellationToken cancellationToken)
    {
        await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var from = _lab.Records.Activity().LastSeq;
        var launcher = new ClaudeCodeLauncher(_lab);
        var url = _lab.PublicUrl.ToString().TrimEnd('/');
        var system = _role == "attacker" ? RolePrompts.Attacker(url) : RolePrompts.Explorer(url);
        var assignment = entry == null
            ? "対象を指定しない自由な探索です。気になるところを確かめてください。"
            : $"確認対象：{entry.Kind} の「{entry.Target}」を、状況「{entry.Situation}」で使い、観点「{entry.Viewpoint}」を確かめてください。";
        var transcript = Path.Combine(_run.Dir, "trials", trialId, "transcript.txt");
        var run = await launcher.RunAsync(_role, _model, system, ["Bash", "Read", "Glob", "Grep", "Write"], RolePrompts.ReportSchema, assignment + "\nレポートは指定の JSON の形で返してください。", transcript, cancellationToken).ConfigureAwait(false);
        await _lab.SettleAsync(TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var to = _lab.Records.Activity().LastSeq;
        var output = run.Structured as JsonObject ?? [];
        var actions = (output["actions"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(a => new TrialAction(a["user"]?.ToString() ?? string.Empty, a["event"]?.ToString() ?? string.Empty, a["args"] as JsonArray is { } args ? (JsonArray)args.DeepClone() : [], a["note"]?.ToString()))
            .ToList();
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in output["users"] as JsonObject ?? [])
        {
            users[pair.Key] = pair.Value?.ToString() ?? string.Empty;
        }

        var session = Path.GetFileName(_lab.Records.Dir);
        var recordsPath = _run.WriteText($"trials/{trialId}/records.jsonl", string.Join("\n", File.ReadLines(_lab.Records.EventsPath).Skip((int)Math.Max(0, from)).Take((int)Math.Max(0, to - from))));
        var evidence = new List<TrialEvidence> { new("records", Path.GetRelativePath(_run.Dir, recordsPath), "この試行の通信の記録") };
        if (run.TranscriptPath != null)
        {
            evidence.Add(new TrialEvidence("log", Path.GetRelativePath(_run.Dir, run.TranscriptPath), "エージェントの出力"));
        }

        return new TrialReport(trialId, entry?.Id ?? $"free:{trialId}", _role, "claude-code", _model, "agent", actions, run.Ok ? output["result"]?.ToString() ?? "no_bug" : "no_bug", evidence, $"records:{session}:{from.ToString(CultureInfo.InvariantCulture)}-{to.ToString(CultureInfo.InvariantCulture)}", run.Cost, run.Ok ? output["notes"]?.ToString() ?? string.Empty : "エージェントが動かなかった（未確認）：" + run.Error, users, session, from, to);
    }
}

/// <summary>修正の結果。</summary>
public sealed record FixResult(string BugId, bool Committed, string? Commit, string Detail);

/// <summary>
/// 修正役。修正の差分は、作業用の複製（サンドボックスの /workspace）に作らせ、フレームワークが種類と署名を付けて対象アプリのリポジトリにコミットする（第20.4節のコミットの入口）。
/// - scripted：data/fixes/&lt;バグのID&gt;.patch があれば当てる（人間か外のAIが用意した修正）。なければ「修正の提案なし」
/// - claude-code：再現手順と証拠を渡して、サンドボックスで直させる
/// </summary>
public sealed class Fixer
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly string _agent;
    private readonly string? _model;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public Fixer(Lab.Lab lab, Run run, string agent, string? model, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _agent = agent;
        _model = model;
        _log = log;
    }

    /// <summary>直す。成功したら、作業用の複製に差分が残る（コミットは呼び出し側が行う）。</summary>
    public async Task<(bool Changed, string Detail)> ProposeAsync(string bugId, string reproduction, CancellationToken cancellationToken)
    {
        var workspace = _lab.Workspace.SandboxWorkspaceDir;
        if (_agent == "claude-code")
        {
            var launcher = new ClaudeCodeLauncher(_lab);
            var transcript = Path.Combine(_run.Dir, "fixes", bugId, "transcript.txt");
            var run = await launcher.RunAsync("fixer", _model, RolePrompts.Fixer, ["Bash", "Read", "Edit", "Write", "Glob", "Grep"], null, $"不具合 {bugId} を直してください。\n再現手順と証拠：\n{reproduction}", transcript, cancellationToken).ConfigureAwait(false);
            if (!run.Ok)
            {
                return (false, "修正役が動かなかった：" + run.Error);
            }
        }
        else
        {
            var patch = Path.Combine(_lab.Workspace.DataDir, "fixes", bugId + ".patch");
            if (!File.Exists(patch))
            {
                return (false, $"修正の提案なし（{Path.GetRelativePath(_lab.Workspace.DataDir, patch)} がない）");
            }

            var apply = await ProcessRunner.RunAsync("git", ["-C", workspace, "apply", "--index", patch], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!apply.Succeeded)
            {
                return (false, "修正の差分を当てられない：" + apply.StandardError);
            }
        }

        var diff = await ProcessRunner.RunAsync("git", ["-C", workspace, "diff", "HEAD", "--stat"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
        _log($"修正の提案（{bugId}）：{diff.StandardOutput.Trim()}");
        return (diff.StandardOutput.Trim().Length > 0, diff.StandardOutput.Trim());
    }
}

/// <summary>ループ1周の結果。</summary>
public sealed record LoopIteration(int Iteration, int Assigned, int Reports, int ValidReports, int BugReports, int MachineFindings, IReadOnlyList<string> NewBugs, IReadOnlyList<FixResult> Fixes, double Coverage, JsonObject SandboxCheck);

/// <summary>
/// 探索・判定・修正のループ（設計書 第2.4節、第27.2節の段階3）。
/// 1周：台帳を作り直す → 確認対象を割り当てる（一部は自由な探索）→ 探索 → レポートの機械チェック → 台帳に反映 →
/// 段階1の機械判定（ボットの対局）→ 登録簿へ → 修正役 → コミットの入口 → 作り直して回帰の確認。
/// </summary>
public sealed class LoopRunner
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public LoopRunner(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>回す。commit は修正をコミットする処理（コミットの入口。呼び出し側が登録簿と署名の設定を持つ）。</summary>
    public async Task<IReadOnlyList<LoopIteration>> RunAsync(
        int iterations,
        int targetsPerIteration,
        string agent,
        Func<string, string, CancellationToken, Task<string?>> commit,
        Func<IEnumerable<Finding>, string, IReadOnlyList<(string Fingerprint, string BugId, bool Created)>> register,
        CancellationToken cancellationToken)
    {
        var definition = LedgerDefinition.Load(_lab.Workspace, _lab.Config);
        var results = new List<LoopIteration>();
        var explorerAssignment = _lab.Config.Agents?.Roles.GetValueOrDefault("explorer");
        var fixerAssignment = _lab.Config.Agents?.Roles.GetValueOrDefault("fixer");
        IExplorer explorer = agent == "claude-code" ? new ClaudeCodeExplorer(_lab, _run, "explorer", explorerAssignment?.Model) : new ScriptedExplorer(_lab, _run, _log);
        var fixer = new Fixer(_lab, _run, agent, fixerAssignment?.Model, _log);
        var sandbox = await SandboxWriteCheck.RunAsync(_lab, cancellationToken).ConfigureAwait(false);
        _run.WriteJson("sandbox-write-check.json", sandbox);
        _log($"修正役のサンドボックスから判定器と設定に書き込めないこと：{(sandbox["ok"]?.GetValue<bool>() == true ? "確認した" : "書き込める（問題）")}");
        for (var iteration = 1; iteration <= iterations; iteration++)
        {
            _log($"== ループ {iteration}/{iterations}");
            var ledger = LedgerStore.Build(definition, LedgerStore.Load(_lab.Workspace));
            var checker = new ReportChecker(_lab, definition, ledger);

            // 割り当て：未確認のものを重大度の高い順に。予算の一部は自由な探索
            var free = Math.Max(targetsPerIteration > 1 ? 1 : 0, (int)Math.Round(targetsPerIteration * definition.FreeShare));
            var assigned = ledger.Where(e => e.Status == "unverified").OrderBy(e => e.Attempts).ThenBy(e => SeverityRank(e.Severity)).ThenBy(e => e.Id, StringComparer.Ordinal).Take(targetsPerIteration - free).Select(e => (LedgerEntry?)e).ToList();
            assigned.AddRange(Enumerable.Repeat<LedgerEntry?>(null, free));
            var checkedReports = new List<(TrialReport Report, ReportCheck Check)>();
            var index = 0;
            foreach (var entry in assigned)
            {
                index++;
                var trialId = $"L{iteration.ToString("D2", CultureInfo.InvariantCulture)}-{index.ToString("D2", CultureInfo.InvariantCulture)}";
                _log($"探索 {trialId}：{entry?.Id ?? "自由な探索"}");
                var report = await explorer.ExploreAsync(entry, trialId, iteration * 100 + index, cancellationToken).ConfigureAwait(false);
                var check = await checker.CheckAsync(report, _run.Dir, cancellationToken).ConfigureAwait(false);
                _run.WriteJson($"trials/{trialId}/report.json", report);
                _run.WriteJson($"trials/{trialId}/check.json", check);
                checkedReports.Add((report, check));
                _log($"  結果 {report.Result}、機械チェック {(check.Valid ? "合格" : "不合格：" + string.Join("／", check.Problems))}");
            }

            // バグありのレポートを登録簿へ（機械チェックを通ったものだけ）
            var bugFindings = checkedReports.Where(r => r.Check.Valid && r.Report.Result == "bug")
                .Select(r => Finding.Create("explorer", "report_bug", Severity.High, 1, "exploration", $"探索で見つかった不具合（{r.Report.TargetId}）：{r.Report.Notes}", null, r.Report.Evidence.Select(e => new Evidence(e.Description, e.Path)), r.Report.TargetId))
                .ToList();
            var registered = register(bugFindings, explorer.Name);
            var withBugs = checkedReports.Select(r => (r.Report with { BugIds = registered.Where(x => bugFindings.Any(f => f.Fingerprint == x.Fingerprint && f.Summary.Contains(r.Report.TargetId, StringComparison.Ordinal))).Select(x => x.BugId).ToList() }, r.Check)).ToList();
            ledger = LedgerStore.Apply(ledger, withBugs);
            LedgerStore.Save(_lab.Workspace, ledger);

            // 段階1の機械判定（ボットの対局）
            var (_, machine, _) = await new BotPlayRunner(_lab, _run, _log).RunAsync(1, iteration, true, cancellationToken).ConfigureAwait(false);
            var machineRegistered = register(machine, $"custos-bot@{FrameworkInfo.Version}");
            var newBugs = registered.Concat(machineRegistered).Where(r => r.Created).Select(r => r.BugId).Distinct(StringComparer.Ordinal).ToList();

            // 修正役 → コミットの入口
            var fixes = new List<FixResult>();
            foreach (var bug in newBugs)
            {
                var (changed, detail) = await fixer.ProposeAsync(bug, $"登録簿 {bug} を参照（再現は記録 {Path.GetFileName(_lab.Records.Dir)}）", cancellationToken).ConfigureAwait(false);
                if (!changed)
                {
                    fixes.Add(new FixResult(bug, false, null, detail));
                    continue;
                }

                var sha = await commit(bug, detail, cancellationToken).ConfigureAwait(false);
                fixes.Add(new FixResult(bug, sha != null, sha, sha != null ? "コミットした（回帰の確認は次の周で、作り直したビルドで行う）" : "コミットの入口で止められた"));
            }

            var coverage = LedgerStore.Coverage(ledger);
            var result = new LoopIteration(iteration, assigned.Count, checkedReports.Count, checkedReports.Count(r => r.Check.Valid), checkedReports.Count(r => r.Report.Result == "bug"), machine.Count(f => f.Category != "coverage"), newBugs, fixes, coverage, sandbox);
            results.Add(result);
            _run.AppendJsonLine("loop.jsonl", result);
            _log($"ループ {iteration}：レポート {result.Reports}（機械チェック合格 {result.ValidReports}）、バグあり {result.BugReports}、機械判定 {result.MachineFindings}、新しいバグ {newBugs.Count}、網羅 {coverage:P1}");
        }

        return results;
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        _ => 3,
    };
}

/// <summary>
/// 隠しシナリオ（設計書 第12.6節「見える判定への合わせ込み」）。修正ループでは使わず、リリース判定のときだけ回す。
/// 検証用リポジトリの hidden-scenarios/*.yaml を回し、見えるシナリオ（scenarios/bot-game.yaml）の失敗率と比べる。
/// 隠しシナリオの失敗率が明らかに高ければ、合わせ込みの兆候とみなす。
/// </summary>
public sealed class HiddenRunner
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public HiddenRunner(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>回す。返り値は結果と見つけたもの。</summary>
    public async Task<(JsonObject Summary, IReadOnlyList<Finding> Findings)> RunAsync(int visibleRuns, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(_lab.Workspace.VerifyRepo, "hidden-scenarios");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.yaml").Order(StringComparer.Ordinal).ToList() : [];
        var findings = new List<Finding>();
        if (files.Count == 0)
        {
            return (new JsonObject { ["hidden"] = 0 }, [Finding.Create("hidden", "not_prepared", Severity.Low, 1, "coverage", "隠しシナリオ（hidden-scenarios/）がありません（未確認）", null, [], "hidden")]);
        }

        var hiddenFailures = 0;
        var rows = new JsonArray();
        foreach (var file in files)
        {
            var (failed, detail, found) = await RunOneAsync(file, new Dictionary<string, JsonNode?>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            hiddenFailures += failed ? 1 : 0;
            findings.AddRange(found);
            rows.Add(new JsonObject { ["scenario"] = Path.GetFileName(file), ["kind"] = "hidden", ["failed"] = failed, ["detail"] = detail });
        }

        var visible = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "bot-game.yaml");
        var visibleFailures = 0;
        for (var i = 1; i <= visibleRuns && File.Exists(visible); i++)
        {
            var (failed, detail, _) = await RunOneAsync(visible, new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["seed"] = JsonValue.Create(500 + i), ["game"] = JsonValue.Create(i) }, cancellationToken).ConfigureAwait(false);
            visibleFailures += failed ? 1 : 0;
            rows.Add(new JsonObject { ["scenario"] = "bot-game.yaml", ["kind"] = "visible", ["failed"] = failed, ["detail"] = detail });
        }

        var hidden = Custos.Statistics.Proportions.Wilson(hiddenFailures, files.Count, 0.95);
        var seen = Custos.Statistics.Proportions.Wilson(visibleFailures, Math.Max(1, visibleRuns), 0.95);
        var overfit = hidden.Lower > seen.Upper;
        var summary = new JsonObject
        {
            ["hidden_runs"] = files.Count,
            ["hidden_failures"] = hiddenFailures,
            ["hidden_interval"] = new JsonArray(hidden.Lower, hidden.Upper),
            ["visible_runs"] = visibleRuns,
            ["visible_failures"] = visibleFailures,
            ["visible_interval"] = new JsonArray(seen.Lower, seen.Upper),
            ["overfitting_sign"] = overfit,
            ["rows"] = rows,
        };
        if (overfit)
        {
            findings.Add(Finding.Create("hidden", "overfitting", Severity.High, 2, "overfitting", $"隠しシナリオの失敗率（{hiddenFailures}/{files.Count}）が、見えるシナリオ（{visibleFailures}/{visibleRuns}）より明らかに高い（合わせ込みの兆候）", null, [new Evidence("比較", "hidden.json")], "overfitting"));
        }

        _run.WriteJson("hidden.json", summary);
        _log($"隠しシナリオ：失敗 {hiddenFailures}/{files.Count}、見えるシナリオ：失敗 {visibleFailures}/{visibleRuns}");
        return (summary, findings);
    }

    private async Task<(bool Failed, string Detail, IReadOnlyList<Finding> Findings)> RunOneAsync(string file, Dictionary<string, JsonNode?> variables, CancellationToken cancellationToken)
    {
        await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
        var runner = new ScenarioRunner(_lab, null, _log);
        foreach (var pair in variables)
        {
            runner.Variables[pair.Key] = pair.Value;
        }

        var result = await runner.RunAsync(Scenario.Load(file), cancellationToken).ConfigureAwait(false);
        await _lab.SettleAsync(TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var judges = await JudgeRunner.RunAsync(new JudgeContext(_lab, _run, baseline), JudgeRunner.Stage1(), cancellationToken).ConfigureAwait(false);
        var findings = judges.SelectMany(r => r.Findings).Where(f => f.Stage == 1 && f.Category != "coverage").ToList();
        var failed = !result.Ok || findings.Count > 0;
        return (failed, result.Ok ? (findings.Count == 0 ? "合格" : findings[0].Summary) : "シナリオの失敗：" + result.Error, findings);
    }
}
