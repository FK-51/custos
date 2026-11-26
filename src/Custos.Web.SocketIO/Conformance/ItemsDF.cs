using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Custos.Core.Conformance;
using Custos.Core.Config;
using Custos.Core.Judging;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.SocketIO.Wire;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.Records;
using Microsoft.Data.Sqlite;

namespace Custos.Web.SocketIO.Conformance;

/// <summary>
/// D-1：使う判定器ごとに、既知の違反を与えて検出されること（陽性の確認）。
/// - クラッシュ検知：プロセスを落とす
/// - 例外のログ：既知の例外の行を出す
/// - 不変条件：違反するデータを、管理側から DB の複製に書き込む（scenarios/conformance/judge-positives.yaml）
/// - 秘密の値の照合：フレームワーク自身が、秘密の値を別の利用者への通信に混ぜる
/// - 送信項目の許可一覧：一覧にない項目を含む通信を与える
/// </summary>
public sealed class D1JudgesDetect : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("D-1", ConformanceCategory.D, ItemKind.Required, "共通", "使う判定器ごとに、既知の違反を与えて検出される（陽性の確認）", "第17.3節 D");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var results = new JsonObject();
        var missed = new List<string>();

        // 例外のログ
        var baseline = await JudgeContext.CaptureAsync(lab, cancellationToken).ConfigureAwait(false);
        var judgeContext = new JudgeContext(lab, context.Run, baseline);
        await lab.Docker.RunAsync(["exec", Containers.App(lab.Project), "sh", "-c", "echo 'TypeError: Cannot read properties of undefined (reading \"probe\")' > /proc/1/fd/2"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        var logs = await new LogJudge().JudgeAsync(judgeContext, cancellationToken).ConfigureAwait(false);
        var logDetected = logs.Findings.Any(f => f.Rule == "unhandled_exception");
        results["logs"] = logDetected;
        if (!logDetected)
        {
            missed.Add("例外のログ");
        }

        // 不変条件
        var positives = LoadPositives(context);
        var invariantResults = new JsonArray();
        foreach (var (invariantId, sql) in positives)
        {
            var invariant = context.Config.Invariants?.Invariants.FirstOrDefault(i => i.Id == invariantId);
            if (invariant?.Query == null)
            {
                missed.Add($"不変条件 {invariantId}（invariants.yaml にない）");
                continue;
            }

            var detected = await InvariantOnCopyAsync(lab, invariant, sql, cancellationToken).ConfigureAwait(false);
            invariantResults.Add(new JsonObject { ["invariant"] = invariantId, ["detected"] = detected });
            if (!detected)
            {
                missed.Add($"不変条件 {invariantId}");
            }
        }

        results["invariants"] = invariantResults;
        if (positives.Count == 0 && (context.Config.Invariants?.Invariants.Count ?? 0) > 0)
        {
            missed.Add("不変条件の陽性の見本（judge-positives.yaml）がない");
        }

        // 秘密の値の照合
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var variants = new[] { token, token[..20] + "…", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(token)), Uri.EscapeDataString(token) };
        var secretDetected = variants.Take(1).Concat(variants.Skip(2)).All(v => SecretLeakJudge.Contains($"42[\"anotherTurn\",{{\"note\":\"{v}\"}}]", token));
        var secretNoFalse = !SecretLeakJudge.Contains("42[\"anotherTurn\",{\"note\":\"" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) + "\"}]", token);
        results["secret_match"] = secretDetected && secretNoFalse;
        if (!secretDetected || !secretNoFalse)
        {
            missed.Add("秘密の値の照合");
        }

        // 送信項目の許可一覧：一覧にない項目を含む記録を与える
        if (context.Config.Authority.Sent.Count > 0)
        {
            var dir = Path.Combine(Path.GetTempPath(), "custos-d1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var known = context.Config.Authority.Sent.Keys.First();
            var entry = new RecordEntry { Seq = 1, Ts = DateTimeOffset.UtcNow, Kind = "eio", Dir = "s2c", Sio = "event", EventName = known, Args = new JsonArray(new JsonObject { ["opponentHands"] = new JsonArray(1, 2) }), Sid = "x" };
            File.WriteAllText(Path.Combine(dir, "events.jsonl"), RecordJson.Serialize(entry) + "\n");
            var schema = await new SentSchemaJudge(new RecordStore(dir)).JudgeAsync(judgeContext, cancellationToken).ConfigureAwait(false);
            var detected = schema.Findings.Any(f => f.Rule == "unknown_path");
            results["sent_schema"] = detected;
            if (!detected)
            {
                missed.Add("送信項目の許可一覧");
            }

            Directory.Delete(dir, recursive: true);
        }

        // クラッシュ検知：プロセスを落とす（最後に行い、終わったら元に戻す）
        var crashBaseline = await JudgeContext.CaptureAsync(lab, cancellationToken).ConfigureAwait(false);
        await lab.Docker.RunAsync(["kill", "--signal", "KILL", Containers.App(lab.Project)], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        var health = await new AppHealthJudge().JudgeAsync(new JudgeContext(lab, context.Run, crashBaseline), cancellationToken).ConfigureAwait(false);
        var crashDetected = health.Findings.Any(f => f.Rule is "crash" or "restart");
        results["crash"] = crashDetected;
        if (!crashDetected)
        {
            missed.Add("クラッシュ検知");
        }

        await lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var path = Write(context, "d1-positives", results);
        return missed.Count == 0
            ? Pass($"判定器はすべて既知の違反を検出しました（例外のログ、不変条件 {invariantResults.Count} 件、秘密の値の照合、送信項目の許可一覧、クラッシュ検知）", [new Evidence("陽性の確認", Rel(context, path))])
            : Fail("検出しなかった判定器：" + string.Join("、", missed), [new Evidence("陽性の確認", Rel(context, path))]);
    }

    /// <summary>検証用リポジトリの scenarios/conformance/judge-positives.yaml（不変条件ごとの、違反を作る SQL）。</summary>
    private static List<(string Invariant, string Sql)> LoadPositives(ConformanceContext context)
    {
        var path = Path.Combine(context.Workspace.VerifyRepo, "scenarios", "conformance", "judge-positives.yaml");
        var list = new List<(string, string)>();
        if (!File.Exists(path))
        {
            return list;
        }

        var scenario = Scenario.Load(path);
        foreach (var step in scenario.Steps.Where(s => s.Kind == "set"))
        {
            if (step.Args["invariant"]?.GetValue<string>() is { } id && step.Args["sql"]?.GetValue<string>() is { } sql)
            {
                list.Add((id, sql));
            }
        }

        return list;
    }

    private static async Task<bool> InvariantOnCopyAsync(Lab.Lab lab, InvariantSpec invariant, string violatingSql, CancellationToken cancellationToken)
    {
        var name = "d1-" + Guid.NewGuid().ToString("N")[..10];
        var ops = await lab.OpsAsync(cancellationToken).ConfigureAwait(false);
        await ops.CallAsync("db.backup", new JsonObject { ["db"] = lab.DbPathInOps, ["out"] = $"/snapshots/tmp/{name}.db" }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        await ops.CallAsync("db.exec_copy", new JsonObject { ["db"] = $"/snapshots/tmp/{name}.db", ["sql"] = violatingSql }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        var copy = Path.Combine(lab.Workspace.SnapshotsDir, "tmp", name + ".db");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = invariant.Query;
            using var reader = command.ExecuteReader();
            return reader.Read();
        }
        finally
        {
            await ops.CallAsync("fs.remove", new JsonObject { ["path"] = $"/snapshots/tmp/{name}.db" }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>D-2：正常な状態では反応しないこと（誤検出の確認）。リセット直後の、何もしていない状態で判定器が何も出さない。</summary>
public sealed class D2NoFalsePositive : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("D-2", ConformanceCategory.D, ItemKind.Required, "共通", "正常な状態では判定器が反応しない（誤検出の確認）", "第17.3節 D");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        await lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var baseline = await JudgeContext.CaptureAsync(lab, cancellationToken).ConfigureAwait(false);
        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        var reports = await JudgeRunner.RunAsync(new JudgeContext(lab, context.Run, baseline), JudgeRunner.Stage1(), cancellationToken).ConfigureAwait(false);
        var findings = reports.SelectMany(r => r.Findings).Where(f => f.Stage == 1).ToList();
        var path = context.Run.WriteText("evidence/d2-idle-judges.txt", JudgeRunner.Summarize(reports));
        return findings.Count == 0
            ? Pass($"リセット直後の正常な状態で、判定器 {reports.Count(r => r.Ran)} 種は何も検出しませんでした", [new Evidence("判定の結果", Rel(context, path))])
            : Fail($"正常な状態で {findings.Count} 件を検出しました（誤検出の疑い）：{findings[0].Summary}", [new Evidence("判定の結果", Rel(context, path))]);
    }
}

/// <summary>D-3（能力依存）：バグ注入を有効にすると、実際に挙動が変わること（カナリアが成立するかの確認）。</summary>
public sealed class D3FaultInjection : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("D-3", ConformanceCategory.D, ItemKind.Capability, "共通", "バグ注入を有効にすると、実際に挙動が変わる", "第17.3節 D、第19.4節", "fault_injection");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);

        // プロキシ側の注入（アプリのコードを触らない。第19.4節）：遅延を注入し、応答が遅くなることを確かめる
        var client = await lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        async Task<double> MeasureAsync()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await client.CallAsync("probe.http", new JsonObject { ["url"] = lab.PublicUrl.ToString(), ["timeout_ms"] = 10000 }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return watch.Elapsed.TotalMilliseconds;
        }

        var normal = await MeasureAsync().ConfigureAwait(false);
        var rules = lab.ReadRules();
        rules["rules"] = new JsonArray(new JsonObject { ["name"] = "d3-delay", ["action"] = "delay", ["ms"] = 1500, ["match"] = new JsonObject { ["path_prefix"] = "/" } });
        await lab.WriteRulesAsync(rules).ConfigureAwait(false);
        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        var injected = await MeasureAsync().ConfigureAwait(false);
        rules["rules"] = new JsonArray();
        await lab.WriteRulesAsync(rules).ConfigureAwait(false);
        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        var restored = await MeasureAsync().ConfigureAwait(false);
        var path = Write(context, "d3-fault", new JsonObject { ["normal_ms"] = normal, ["injected_ms"] = injected, ["restored_ms"] = restored });
        var ok = injected - normal > 1000 && restored < injected - 1000;
        return ok
            ? Pass($"プロキシで注入した遅延が効き（{N(normal)} ms → {N(injected)} ms）、外すと戻りました（{N(restored)} ms）", [new Evidence("計測", Rel(context, path))])
            : Fail($"注入した異常が効きませんでした（{N(normal)} ms → {N(injected)} ms → {N(restored)} ms）", [new Evidence("計測", Rel(context, path))]);
    }
}

/// <summary>D-4（能力依存）：解析の外部コンポーネントが、既知の見本で検出し、正常な見本では反応しないこと（第12.8節）。</summary>
public sealed class D4Analyzers : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("D-4", ConformanceCategory.D, ItemKind.Capability, "共通", "解析の外部コンポーネントが、見本で検出し、正常な見本で反応しない", "第17.3節 D、第12.8節", "analyzer");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var analyzer = new Analysis.AnalyzerComponent(lab.Docker, context.Workspace);
        if (!await analyzer.AvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return Skip("解析の外部コンポーネント（custos-analyzer-image）を使っていない");
        }

        var check = await analyzer.SelfCheckAsync(cancellationToken).ConfigureAwait(false);
        var path = Write(context, "d4-analyzer", check);
        var ok = check["positives_detected"]?.GetValue<bool>() == true && check["negatives_clean"]?.GetValue<bool>() == true;
        return ok
            ? Pass($"解析の外部コンポーネント（{check["version"]}）は、崩れた見本を検出し、正常な見本には反応しませんでした", [new Evidence("見本での確認", Rel(context, path))])
            : Fail("解析の外部コンポーネントの陽性・陰性の確認に失敗しました", [new Evidence("見本での確認", Rel(context, path))]);
    }
}

/// <summary>F-1（性能）：リソースプロファイルが実際に適用されていること。</summary>
public sealed class F1ProfileApplied : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("F-1", ConformanceCategory.F, ItemKind.Capability, "共通", "リソースプロファイルが実際に適用されている", "第17.3節 F、第16.5節", "performance");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        if (lab.State.Profile == null)
        {
            return Skip("リソースプロファイルを使っていない（性能を検証しない）");
        }

        var check = await Performance.PerfRunner.CheckProfileAsync(lab, cancellationToken).ConfigureAwait(false);
        var path = Write(context, "f1-profile", check);
        return check["ok"]?.GetValue<bool>() == true
            ? Pass($"プロファイル {lab.State.Profile} の制限がコンテナから見えます（{check["summary"]}）", [new Evidence("確認", Rel(context, path))])
            : Fail($"プロファイルの制限が見えません（{check["summary"]}）", [new Evidence("確認", Rel(context, path))]);
    }
}

/// <summary>F-2（性能）：基準の負荷をかけたとき、負荷を出す側に余裕があること（資源を2倍にしても結果が変わらない）。</summary>
public sealed class F2LoadHeadroom : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("F-2", ConformanceCategory.F, ItemKind.Capability, "共通", "負荷を出す側に余裕がある", "第17.3節 F、第23.3節", "performance");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        if (lab.State.Profile == null)
        {
            return Skip("リソースプロファイルを使っていない（性能を検証しない）");
        }

        var result = await Performance.PerfRunner.HeadroomAsync(lab, context.Run, cancellationToken).ConfigureAwait(false);
        var path = Write(context, "f2-headroom", result);
        return result["ok"]?.GetValue<bool>() == true
            ? Pass(result["summary"]?.GetValue<string>() ?? "余裕あり", [new Evidence("計測", Rel(context, path))])
            : Fail(result["summary"]?.GetValue<string>() ?? "余裕がない", [new Evidence("計測", Rel(context, path))]);
    }
}

/// <summary>F-3（性能）：同じビルド同士で「差なし」、性能のカナリアで「劣化あり」と判定されること（第23.3節）。</summary>
public sealed class F3CompareCanaries : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("F-3", ConformanceCategory.F, ItemKind.Capability, "共通", "同じビルド同士で差なし、性能のカナリアで劣化あり", "第17.3節 F、第23.3節", "performance");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        if (lab.State.Profile == null)
        {
            return Skip("リソースプロファイルを使っていない（性能を検証しない）");
        }

        var result = await Performance.PerfRunner.NegativePositiveAsync(lab, context.Run, cancellationToken).ConfigureAwait(false);
        var path = Write(context, "f3-compare", result);
        return result["ok"]?.GetValue<bool>() == true
            ? Pass(result["summary"]?.GetValue<string>() ?? "合格", [new Evidence("比較", Rel(context, path))])
            : Fail(result["summary"]?.GetValue<string>() ?? "不合格", [new Evidence("比較", Rel(context, path))]);
    }
}

/// <summary>F-4（性能）：同じ条件で複数回計測し、ばらつきが許容範囲内であること。</summary>
public sealed class F4Variance : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("F-4", ConformanceCategory.F, ItemKind.Capability, "共通", "複数回の計測のばらつきが許容範囲内", "第17.3節 F、第23.2節", "performance");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        if (lab.State.Profile == null)
        {
            return Skip("リソースプロファイルを使っていない（性能を検証しない）");
        }

        var result = await Performance.PerfRunner.VarianceAsync(lab, context.Run, cancellationToken).ConfigureAwait(false);
        var path = Write(context, "f4-variance", result);
        return result["ok"]?.GetValue<bool>() == true
            ? Pass(result["summary"]?.GetValue<string>() ?? "合格", [new Evidence("計測", Rel(context, path))])
            : Fail(result["summary"]?.GetValue<string>() ?? "不合格", [new Evidence("計測", Rel(context, path))]);
    }
}
