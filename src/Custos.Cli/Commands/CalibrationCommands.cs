using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Maturity;
using Custos.Core.Util;
using Custos.Registry;
using Custos.Registry.Bugs;
using Custos.Statistics;
using Custos.Statistics.Release;
using Custos.Statistics.Sampling;
using Custos.Web.SocketIO.Agents;
using Custos.Web.SocketIO.Calibration;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Probes;

namespace Custos.Cli.Commands;

/// <summary>段階4：校正と統計のコマンド（設計書 第27.2節）。</summary>
public static class CalibrationCommands
{
    /// <summary>custos seeds check：校正用バグの品質チェック（第19.5節）と層の割り当て（第19.11節）。</summary>
    public static async Task<int> SeedsCheckAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var seeds = Select(context, args.Get("seeds"));
        var run = context.Results.Begin("seeds-check", context.Config.Hash, context.Config.FileHashes);
        var results = await new SeedChecker(context.Lab, run, CliContext.Log).CheckAsync(seeds, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        text.AppendLine("| 校正用バグ | 種類 | パッチ | 効いている | 自明すぎる | 層 | 詳細 |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var seed = seeds.First(s => s.Id == r.Id);
            text.AppendLine(CultureInfo.InvariantCulture, $"| {r.Id} | {seed.Kind} | {(r.Applies ? "当たる" : "当たらない")} | {(r.Effective ? "はい" : "いいえ")} | {(r.Trivial ? "はい" : "いいえ")} | {r.Layer} | {r.Detail.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        run.WriteText("seeds-check.md", text.ToString());
        SeedStore.SaveChecks(SeedStore.ChecksPath(context.Workspace), results);
        run.Seal(results.All(r => r.Usable) ? "passed" : "failed");
        CliContext.Out(text.ToString());
        return results.All(r => r.Usable) ? 0 : 1;
    }

    /// <summary>
    /// custos seeds author [--agent scripted|claude-code] [--count N] [--seed S] [--paths p1,p2]：シード作成役（第19.8節）。
    /// 作った下書きを確かめ（アプリの HEAD に当たる、目印がない、書式どおり）、使えるものを次の ID で seeds/ に置く。
    /// 効いているか、自明すぎないか、層は、この後の custos seeds check で決める。
    /// </summary>
    public static async Task<int> SeedsAuthorAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var agent = args.Get("agent") ?? "scripted";
        var count = (int)args.Number("count", 10);
        var run = context.Results.Begin("seeds-author", context.Config.Hash, context.Config.FileHashes, new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = agent });
        IReadOnlyList<SeedDraft> drafts = agent == "claude-code"
            ? await new ClaudeSeedAuthor(context.Lab, run).RunAsync(count, context.Config.Agents?.Roles.GetValueOrDefault("seed_author")?.Model, cancellationToken).ConfigureAwait(false)
            : await MutationSeedAuthor.GenerateAsync(context.Workspace, context.Workspace.AppRepo, count, (int)args.Number("seed", 1), (args.Get("paths") ?? string.Empty).Split(',', StringSplitOptions.TrimEntries), cancellationToken).ConfigureAwait(false);
        var checks = await SeedAuthoring.CheckAsync(context.Workspace.AppRepo, drafts, cancellationToken).ConfigureAwait(false);
        var ids = SeedAuthoring.Save(context.Workspace, checks.Where(c => c.Ok).Select(c => c.Draft));
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"シード作成役（{agent}）：下書き {drafts.Count} 件、使える {ids.Count} 件を seeds/ に置いた（{string.Join(", ", ids)}）。次に custos seeds check --seeds {string.Join(",", ids)} で品質を確かめる");
        text.AppendLine();
        text.AppendLine("| 結果 | 種類 | 説明・理由 |");
        text.AppendLine("|---|---|---|");
        var saved = 0;
        foreach (var check in checks)
        {
            var label = check.Ok ? ids[saved++] : "外した";
            text.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {check.Draft.Kind} | {(check.Ok ? check.Draft.Description : check.Detail).Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        run.WriteText("seeds-author.md", text.ToString());
        run.WriteJson("seeds-author.json", checks.Select(c => new { c.Draft.Kind, c.Draft.Description, c.Ok, c.Detail }).ToList());
        run.Seal(ids.Count > 0 ? "passed" : "failed");
        CliContext.Out(text.ToString());
        return ids.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// custos verifier measure [--calibration &lt;ID&gt;] [--agent scripted|claude-code]：検証役の感度の測定（第21.2節）。
    /// 校正（省くと最新の calibrate）の各校正用バグの証拠を、結論を伏せて検証役に判定させる。結果は data/calibration/verifier.jsonl に累積し、
    /// 抜き取り検証（sample）と成熟度が使う。
    /// </summary>
    public static async Task<int> VerifierMeasureAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var runs = Path.Combine(context.Workspace.ResultsDir, "runs");
        var calibration = (args.Get("calibration") is { } id
                ? Directory.GetDirectories(runs, id + "*").FirstOrDefault()
                : Directory.GetDirectories(runs, "*-calibrate").Where(d => Directory.Exists(Path.Combine(d, "calibration"))).OrderDescending(StringComparer.Ordinal).FirstOrDefault())
            ?? throw new CustosException("校正の結果がありません（custos calibrate を先に実行してください）");
        var agent = args.Get("agent") ?? "scripted";
        var run = context.Results.Begin("verifier-measure", context.Config.Hash, context.Config.FileHashes, new Dictionary<string, string>(StringComparer.Ordinal) { ["agent"] = agent, ["calibration"] = Path.GetFileName(calibration) });
        var measurement = await VerifierSensitivity.MeasureAsync(context.Lab, run, calibration, agent, CliContext.Log, cancellationToken).ConfigureAwait(false);
        run.WriteJson("verifier.json", measurement);
        var cumulative = VerifierSensitivity.Cumulative(context.Workspace, agent);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# 検証役の感度（{Path.GetFileName(calibration)} の証拠）");
        text.AppendLine();
        text.AppendLine(VerifierSensitivity.Describe(measurement));
        text.AppendLine(cumulative is { } c
            ? string.Create(CultureInfo.InvariantCulture, $"累積（{agent}）：使う感度（95% 信頼区間の下限の低い方）{c.Used:P0}（見落としたもの {c.MissedTotal} 件で {(c.Missed is { } m ? m.ToString("P0", CultureInfo.InvariantCulture) : "-")}、すべて {c.AllTotal} 件で {(c.All is { } a ? a.ToString("P0", CultureInfo.InvariantCulture) : "-")}）。基準 {VerifierSensitivity.Threshold:P0}{(c.Used < VerifierSensitivity.Threshold ? "を下回る：この検証役での抜き取りは当てにならない（人間の担当にする。第21.3節）" : " 以上")}")
            : "判定できた証拠がない（感度は出せない）");
        text.AppendLine();
        text.AppendLine("| 校正用バグ | 探索 | 検証役 | 理由 |");
        text.AppendLine("|---|---|---|---|");
        foreach (var row in measurement.Rows)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {row["seed"]} | {(row["explore_detected"]?.GetValue<bool>() == true ? "見つかった" : row["explore_detected"] == null ? "-" : "見落とした")} | {row["verdict"]} | {row["detail"]?.ToString().Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        run.WriteText("verifier.md", text.ToString());
        run.Seal(measurement.AllTotal > 0 ? "passed" : "failed");
        CliContext.Out(text.ToString());
        return measurement.AllTotal > 0 ? 0 : 1;
    }

    /// <summary>custos calibrate：校正を実行する（検出率の測定。第7章）。</summary>
    public static async Task<int> CalibrateAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var used = SeedStore.Used(context.Workspace);
        var seeds = Select(context, args.Get("seeds")).Where(s => args.Flag("allow-used") || !used.Contains(s.Id)).ToList();
        if (seeds.Count == 0)
        {
            throw new CustosException("使っていない校正用バグがありません（使用済みは再利用しない。第19.8節）");
        }

        var checks = SeedStore.LoadChecks(SeedStore.ChecksPath(context.Workspace)).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var run = context.Results.Begin("calibrate", context.Config.Hash, context.Config.FileHashes);
        var lab = context.Lab;
        var games = (int)args.Number("games", 2);
        var round = 0;

        // 探索：段階1の機械判定（ボットの対局と汎用の判定・不変条件、局面の検査）。探索役のループ（--agent）は台帳の対象を割り当てる
        async Task<IReadOnlyList<Finding>> ExploreAsync(CancellationToken token)
        {
            var found = new List<Finding>();

            // 探索の手順が失敗しても（校正用バグで準備のシナリオが通らない、など）校正を止めず、失敗を所見として残す。
            // パッチなし（基準）でも同じ手順が失敗していれば、指紋が同じなので校正用バグの検出には数えない
            async Task Step(string name, Func<Task<IEnumerable<Finding>>> body)
            {
                try
                {
                    found.AddRange(await body().ConfigureAwait(false));
                }
                catch (Exception e) when (e is CustosException or TimeoutException or IOException)
                {
                    found.Add(Finding.Create("calibrate", "explore_step_failed", Severity.Medium, 2, "exploration", $"探索の手順「{name}」が失敗した：{e.Message}", null, [], name));
                }
            }

            await Step("ボットの対局", async () => (await new BotPlayRunner(lab, run, CliContext.Log).RunAsync(games, 7, true, token).ConfigureAwait(false)).Findings).ConfigureAwait(false);
            var stage1 = args.Flag("stage1");
            if (stage1)
            {
                // 参照モデル（あれば）でボットの対局の手のログを確かめる。局面の検査などがリセットして手のログを消す前に行う
                await Step("参照モデルとの照合", async () => (await new Custos.Web.SocketIO.Repro.ReferenceHarness(lab, run, CliContext.Log).RunAsync(null, token).ConfigureAwait(false)).Findings).ConfigureAwait(false);
            }

            if (args.Flag("phases") || stage1)
            {
                await Step("局面の検査", async () => await new PhaseProber(lab, run, CliContext.Log).RunAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            }

            // --stage1：段階1の機械判定をすべて探索に入れる（custos stage1 と同じ。静的解析はアプリのリポジトリを見るので除く）。
            // 以前は、ファジングで落ちる入力（クラッシュ系の校正用バグ）を探索しておらず、段階1なら見つかるものを見逃していた
            if (stage1)
            {
                await Step("宣言と実態のずれの検査", async () => await new AuthorityProber(lab, run, CliContext.Log).RunAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
                await Step("隠し情報となりすましの検査", async () => await new SecurityProber(lab, run, CliContext.Log).RunAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
                await Step("ファジング", async () => await new EventFuzzer(lab, run, 1, CliContext.Log).RunAsync([], 1, token).ConfigureAwait(false)).ConfigureAwait(false);
            }

            if (args.Get("agent") is "scripted" or "claude-code")
            {
                await Step("探索役", async () =>
                {
                    var reports = new List<Finding>();
                    var definition = LedgerDefinition.Load(context.Workspace, context.Config);
                    var ledger = LedgerStore.Build(definition, null);
                    IExplorer explorer = args.Get("agent") == "claude-code" ? new ClaudeCodeExplorer(lab, run, "explorer", context.Config.Agents?.Roles.GetValueOrDefault("explorer")?.Model) : new ScriptedExplorer(lab, run, CliContext.Log);

                    // 探索は基準と校正用バグごとに呼ぶので、試行の ID に回の番号を付ける（同じ実行の中で証拠のファイルがぶつからないように）
                    round++;
                    var index = 0;
                    foreach (var entry in ledger.Where(e => e.Kind == "card" && e.Situation == "normal").Take((int)args.Number("targets", 3)))
                    {
                        // 台帳は1枚のカードに観点（効果の結果、相手の見え方）ごとの項目があり、対象（カードの番号）だけでは一意にならない
                        index++;
                        var trialId = string.Create(CultureInfo.InvariantCulture, $"K{round:D2}-{index:D2}");
                        var report = await explorer.ExploreAsync(entry, trialId, 11, token).ConfigureAwait(false);
                        if (report.Result == "bug")
                        {
                            reports.Add(Finding.Create("explorer", "report_bug", Severity.High, 3, "exploration", report.Notes, null, [], entry.Id));
                        }
                    }

                    return reports;
                }).ConfigureAwait(false);
            }

            return found;
        }

        var realBugs = BugRegistry.OpenAdmin(RegistryPaths.From(context.Workspace).RegistryDb).List().Count(b => b.FoundBy != "setup" || b.Severity is "critical" or "high");
        var result = await new Calibrator(lab, run, CliContext.Log).RunAsync(seeds, checks, realBugs, ExploreAsync, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# 校正（{run.Id}）");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"仕込んだ {result.Seeds.Count} 個のうち、見つかった {result.Detected.Count} 個（{string.Join(", ", result.Detected)}）。");
        text.AppendLine();
        text.AppendLine("| 層 | 仕込んだ数 | 見つかった数 | 検出率 | 95%信頼区間 | 推定に使う |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var layer in result.Layers)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {layer.Layer} | {layer.Seeded} | {layer.Detected} | {layer.Rate:P0} | {layer.Lower:P0}〜{layer.Upper:P0} | {(layer.Estimable ? "はい" : "いいえ（自明な層か、検出率30%未満か、数が足りない）")} |");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"検出率の下限（自明な層を除く）：{(result.OverallLower is { } low ? low.ToString("P0", CultureInfo.InvariantCulture) : "推定不能")}。実バグ {result.RealBugs} 件から見た残りの上限：{(result.ResidualUpper is { } r ? r.ToString("F1", CultureInfo.InvariantCulture) + " 件" : "推定不能")}（第19.6節）。");
        text.AppendLine("数が少ないため、区間は広い。層あたり20個に満たない層は、隣の層とまとめて推定する（第19.11節）。");
        run.WriteText("calibration.md", text.ToString());
        if (args.Flag("share"))
        {
            // 層ごとの集計値だけを、フレームワークの共有データに追記する（バグの中身は入れない。第19.10節）
            var shared = Path.Combine(context.Workspace.FrameworkRepo, "calibration", "shared-corrections.json");
            var root = File.Exists(shared) ? JsonNode.Parse(await File.ReadAllTextAsync(shared, cancellationToken).ConfigureAwait(false)) as JsonObject ?? [] : [];
            var entries = root["entries"] as JsonArray ?? [];
            root["entries"] = entries;
            entries.Add(new JsonObject
            {
                ["stack"] = "node-socketio-sqlite",
                ["at"] = result.At.ToString("O", CultureInfo.InvariantCulture),
                ["origin"] = result.Basis,
                ["layers"] = new JsonArray(result.Layers.Select(l => (JsonNode?)new JsonObject { ["layer"] = l.Layer, ["seeded"] = l.Seeded, ["detected"] = l.Detected }).ToArray()),
            });
            await File.WriteAllTextAsync(shared, root.ToJsonString(Json.Options), cancellationToken).ConfigureAwait(false);
            CliContext.Log($"共有する補正値に追記しました：{shared}");
        }

        run.Seal("passed");
        CliContext.Out(text.ToString());
        return 0;
    }

    /// <summary>custos canary：毎ループのカナリア（第19.7節）。</summary>
    public static async Task<int> CanaryAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("canary", context.Config.Hash, context.Config.FileHashes);
        var (results, rate) = await new CanaryRunner(context.Lab, run, CliContext.Log).RunAsync((int)args.Number("count", 3), (int)args.Number("seed", DateTime.UtcNow.DayOfYear), cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        text.AppendLine("| カナリア | 方法 | 効いた | 異常の箇所を通った | 見つかった | 詳細 |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var exercised = r.Exercised switch { true => "はい", false => "いいえ", null => "-" };
            text.AppendLine(CultureInfo.InvariantCulture, $"| {r.Name} | {r.Method} | {(r.Applied ? "はい" : "いいえ")} | {exercised} | {(r.Detected ? "はい" : "いいえ")} | {r.Detail.Replace("|", "\\|", StringComparison.Ordinal)} |");
        }

        // 異常の箇所を通らなかったもの（exercised=false）は、見逃しではないので数えない（累積の検出率も同じ）
        var counted = results.Where(r => r.Exercised != false).ToList();
        var skipped = results.Count - counted.Count;
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"今回：{counted.Count(r => r.Detected)}/{counted.Count} 個を検出{(skipped > 0 ? $"（ほかに {skipped} 個は {CanaryRunner.MaxGames} 対局まで回しても異常の箇所を通らず、数えない）" : string.Empty)}。累積の検出率：{rate:P0}（劣化の兆候として監視する。1回の結果から検出率は推定しない）");
        run.WriteText("canary.md", text.ToString());
        var allFound = counted.Count > 0 && counted.All(r => r.Detected);
        run.Seal(allFound ? "passed" : "failed");
        CliContext.Out(text.ToString());
        return allFound ? 0 : 1;
    }

    /// <summary>custos sample：抜き取り検証（ブラインド判定と誤り率の上限。第21章）。</summary>
    public static async Task<int> SampleAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("sample", context.Config.Hash, context.Config.FileHashes);
        var ledger = LedgerStore.Load(context.Workspace);
        var verified = ledger.Where(e => e.Status == "verified").ToList();
        var n = Math.Min((int)args.Number("n", 10), verified.Count);
        var random = new Random((int)args.Number("seed", 1));
        var sample = verified.OrderBy(_ => random.Next()).Take(n).ToList();
        var sampling = context.Config.Sampling;
        var confidence = sampling?.Confidence ?? 0.95;

        // 検証役の感度：--sensitivity がなければ、測った値（custos verifier measure の累積。95% 信頼区間の下限）を使う（第21.2〜21.3節）
        var verifierAgent = args.Get("agent") ?? "scripted";
        double? sensitivity = args.Get("sensitivity") is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : null;
        var measured = sensitivity == null ? VerifierSensitivity.Cumulative(context.Workspace, verifierAgent) : null;
        sensitivity ??= measured?.Used;
        var rows = new JsonArray();
        var errors = 0;
        foreach (var entry in sample)
        {
            // ブラインド判定：元の結論を伏せ、証拠（そのレポートのセッションと範囲の通信の記録）だけで判定し直す
            var trial = entry.Reports.Count > 0 ? entry.Reports[^1] : null;
            var reportPath = trial == null ? null : FindReport(context.Workspace.ResultsDir, trial);
            var report = reportPath == null ? null : System.Text.Json.JsonSerializer.Deserialize<TrialReport>(await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false), Json.Options);
            var verdict = report == null
                ? new BlindVerdict("unknown", "レポートが見つからない")
                : await BlindVerifier.JudgeAsync(context.Lab, run, verifierAgent, report.RecordsSession, report.RecordsFrom, report.RecordsTo, cancellationToken).ConfigureAwait(false);
            if (verdict.Verdict == "bug")
            {
                errors++;
            }

            rows.Add(new JsonObject { ["target"] = entry.Id, ["report"] = trial, ["verifier_verdict"] = verdict.Verdict, ["detail"] = verdict.Detail });
        }

        // 感度が 0（検証役が誤りを1件も指摘できない）なら、上限は出せない（1 とする）
        double? upper = n == 0 ? null : sensitivity is <= 0 ? 1.0 : Proportions.ErrorRateUpperBound(errors, n, confidence, sensitivity ?? 1.0);
        var epsilon = sampling?.Categories.Values.Select(c => EpsilonPolicy.Resolve(c, ledger.Count, sensitivity)).ToList() ?? [];
        var summary = new JsonObject
        {
            ["population"] = ledger.Count,
            ["verified"] = verified.Count,
            ["sampled"] = n,
            ["errors"] = errors,
            ["confidence"] = confidence,
            ["verifier"] = verifierAgent,
            ["verifier_sensitivity"] = sensitivity,
            ["error_rate_upper"] = upper,
            ["sensitivity_source"] = args.Get("sensitivity") != null ? "--sensitivity" : measured != null ? "custos verifier measure の累積（95% 信頼区間の下限）" : null,
            ["sensitivity_note"] = sensitivity == null ? "検証役の感度が未測定のため、上限は感度1（見落としなし）を仮定した値。実際の上限はこれより大きい（未確認）" : sensitivity < VerifierSensitivity.Threshold ? $"検証役の感度 {sensitivity:P0} が基準 {VerifierSensitivity.Threshold:P0} を下回る。この検証役での抜き取りは当てにならない（人間の担当にする。第21.3節）" : null,
            ["epsilon_targets"] = new JsonArray(epsilon.Select(e => (JsonNode?)JsonNode.Parse(Json.SerializeCompact(e))).ToArray()),
            ["rows"] = rows,
        };
        run.WriteJson("sample.json", summary);
        var text = string.Create(CultureInfo.InvariantCulture, $"抜き取り：台帳 {ledger.Count} 件、確認済み {verified.Count} 件から {n} 件。検証役が誤りと判定 {errors} 件。誤り率の上限（{confidence:P0}）：{(upper is { } u ? u.ToString("P1", CultureInfo.InvariantCulture) : "出せない")}{(sensitivity == null ? "（検証役の感度は未測定。感度1を仮定）" : $"（検証役の感度 {sensitivity:P0}{(measured != null ? "。測った値" : string.Empty)}）")}");
        run.WriteText("sample.md", text);
        run.Seal("passed");
        CliContext.Out(text);
        return 0;
    }

    /// <summary>custos maturity：成熟度のレベルと個別の数値（第17.5〜17.6節）。</summary>
    public static Task<int> MaturityAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (report, maturity) = LatestMaturity(context);
        if (report == null || maturity == null)
        {
            throw new CustosException("適合テストの結果がありません（custos conformance を先に実行してください）");
        }

        CliContext.Out($"成熟度：レベル {maturity.Level}（{maturity.Reason}）。必要なレベル {maturity.Required}：{(maturity.MeetsRequired ? "満たす" : "満たさない")}");
        CliContext.Out(maturity.Metrics.ToJsonString(Json.Options));
        return Task.FromResult(0);
    }

    /// <summary>custos release：リリース判定（第10章）。</summary>
    public static async Task<int> ReleaseAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var (report, maturity) = LatestMaturity(context);
        if (report == null || maturity == null)
        {
            throw new CustosException("適合テストの結果がありません");
        }

        var registry = BugRegistry.OpenAdmin(RegistryPaths.From(context.Workspace).RegistryDb);

        // 重複（link の duplicate_of）は元のバグで代表させて数えない（第20.7節）。以前は、機械判定が同じ不具合を
        // 別の記録として見つけた分（例：BUG-0024 は BUG-0004 の重複）まで、未解決として二重に数えていた。
        // 取り下げた記録（withdrawn：バグではなかった）も数えない
        var open = registry.List()
            .Where(b => b.FixedIn == null && !registry.Links(b.Id).Any(l => l.Kind is "duplicate_of" or "withdrawn"))
            .ToList();
        var findings = open.GroupBy(b => (Severity: ParseSeverity(b.Severity), Security: b.Category is "hidden_info" or "seat_hijack" or "xss" or "sqli" or "server_intrusion" or "secret"))
            .Select(g => new OpenFindings(g.Key.Severity, g.Key.Security, g.Count())).ToList();
        var canaryRate = ReadCanaryRate(context.Workspace);
        var hidden = LatestJson(context.Workspace.ResultsDir, "hidden", "hidden.json");
        var sample = LatestJson(context.Workspace.ResultsDir, "sample", "sample.json");
        var categories = (context.Config.Sampling?.Categories.Values ?? []).Select(c => new CategoryBound(c.Name, sample?["error_rate_upper"]?.GetValue<double?>(), c.ErrorUpper ?? c.FallbackErrorUpper)).ToList();
        if (!MaturityLevel.TryParse(maturity.Level, out var achieved))
        {
            achieved = MaturityLevel.Zero;
        }

        var input = new ReleaseInput(
            report.RequiredAllPassed,
            achieved,
            context.Config.App.RequiredMaturity,
            context.Config.App.Criticality,
            CriticalMachineChecksPassed: !open.Any(b => b.Severity == "critical"),
            canaryRate,
            0.9,
            findings,
            categories,
            [],
            LoopCost: null,
            FixLoopCost: null,
            ExpectedFoundNextLoop: null,
            PerformanceState.NotApplicable,
            PerformanceRequired: false,
            UnknownMainlineCommits: false,
            VisibleFailureRate: hidden?["visible_runs"]?.GetValue<int>() is > 0 and var vr ? (double)(hidden["visible_failures"]?.GetValue<int>() ?? 0) / vr : null,
            HiddenFailureRate: hidden?["hidden_runs"]?.GetValue<int>() is > 0 and var hr ? (double)(hidden["hidden_failures"]?.GetValue<int>() ?? 0) / hr : null);
        var decision = ReleaseDecider.Decide(input);
        var run = context.Results.Begin("release", context.Config.Hash, context.Config.FileHashes);
        run.WriteJson("release.json", new JsonObject { ["input"] = JsonNode.Parse(Json.SerializeCompact(input)), ["decision"] = JsonNode.Parse(Json.SerializeCompact(decision)) });
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"リリース判定：{decision.Verdict switch { ReleaseVerdict.Release => "リリースしてよい", ReleaseVerdict.Continue => "ループを続ける", _ => "止める（条件を満たさない）" }}");
        foreach (var reason in decision.Reasons)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- [{reason.Kind}] {reason.Message}");
        }

        run.WriteText("release.md", text.ToString());
        run.Seal(decision.Verdict == ReleaseVerdict.Release ? "passed" : "failed");
        CliContext.Out(text.ToString());
        await Task.CompletedTask.ConfigureAwait(false);
        return decision.Verdict == ReleaseVerdict.Release ? 0 : 1;
    }

    private static List<SeedDefinition> Select(CliContext context, string? ids)
    {
        var all = SeedStore.Load(context.Workspace);
        var wanted = (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var seeds = all.Where(s => wanted.Count == 0 || wanted.Contains(s.Id)).ToList();
        if (seeds.Count == 0)
        {
            throw new CustosException("校正用バグがありません（検証用リポジトリの seeds/）");
        }

        return seeds;
    }

    private static Severity ParseSeverity(string? text) => text switch
    {
        "critical" => Severity.Critical,
        "high" => Severity.High,
        "medium" => Severity.Medium,
        _ => Severity.Low,
    };

    // カナリアの表示と同じ数え方（excluded・exercised=false を数えない）。以前はここだけ applied しか見ておらず、
    // 判定が当てにならないと印を付けた試行（excluded）まで数えていた
    private static double? ReadCanaryRate(Workspace workspace) =>
        CanaryRunner.CumulativeRate(Path.Combine(workspace.DataDir, "calibration", "canaries.jsonl"));

    private static string? FindReport(string resultsDir, string trialId)
    {
        var runs = Path.Combine(resultsDir, "runs");
        return Directory.Exists(runs)
            ? Directory.GetDirectories(runs).OrderDescending(StringComparer.Ordinal).Select(d => Path.Combine(d, "trials", trialId, "report.json")).FirstOrDefault(File.Exists)
            : null;
    }

    private static JsonObject? LatestJson(string resultsDir, string kind, string file)
    {
        var runs = Path.Combine(resultsDir, "runs");
        var path = Directory.Exists(runs) ? Directory.GetDirectories(runs, "*-" + kind).OrderDescending(StringComparer.Ordinal).Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists) : null;
        return path == null ? null : JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    }

    /// <summary>最新の適合テストの結果から、成熟度を出す（校正の結果があれば使う）。</summary>
    private static (ConformanceReport? Report, MaturityReport? Maturity) LatestMaturity(CliContext context)
    {
        // 項目ごとに、いちばん新しい結果を使う（一部の項目だけ実行した結果で、成熟度を判断しない）
        var runs = Path.Combine(context.Workspace.ResultsDir, "runs");
        var paths = Directory.Exists(runs) ? Directory.GetDirectories(runs, "*-conformance").OrderDescending(StringComparer.Ordinal).Select(d => Path.Combine(d, "conformance.json")).Where(File.Exists).ToList() : [];
        if (paths.Count == 0)
        {
            return (null, null);
        }

        var infos = new Dictionary<string, ConformanceItemInfo>(StringComparer.Ordinal);
        var results = new Dictionary<string, ItemResult>(StringComparer.Ordinal);
        ConformanceReport? newest = null;
        foreach (var path in paths)
        {
            var one = ConformanceReport.Load(path);
            if (one.Breakage.Count > 0)
            {
                continue;
            }

            newest ??= one;
            foreach (var info in one.Items)
            {
                if (!infos.ContainsKey(info.Id) && one.Result(info.Id) is { } result)
                {
                    infos[info.Id] = info;
                    results[info.Id] = result;
                }
            }
        }

        if (newest == null)
        {
            return (null, null);
        }

        var report = newest with { Items = infos.Values.OrderBy(i => i.Id, StringComparer.Ordinal).ToList(), Results = results.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList() };
        // レベル3の条件の「カナリアの検出率」は、毎ループのカナリアの累積（第17.5節、第19.7節）。リリース判定と同じ数え方。
        // 以前は校正で仕込んだバグの検出率をカナリアの検出率として渡していた（校正用バグの検出率は参考の数値として別に出す）
        var latest = Path.Combine(context.Workspace.DataDir, "calibration", "latest.json");
        var calibration = File.Exists(latest) ? System.Text.Json.JsonSerializer.Deserialize<CalibrationResult>(File.ReadAllText(latest), Json.Options) : null;
        var canaryRate = ReadCanaryRate(context.Workspace);
        CalibrationEvidence? evidence = null;
        if (calibration != null || canaryRate != null)
        {
            var total = calibration?.Layers.Where(l => l.Layer != "R1V1").Sum(l => l.Seeded) ?? 0;
            var hit = calibration?.Layers.Where(l => l.Layer != "R1V1").Sum(l => l.Detected) ?? 0;
            var verifier = VerifierSensitivity.Cumulative(context.Workspace, "claude-code") ?? VerifierSensitivity.Cumulative(context.Workspace, "scripted");
            evidence = new CalibrationEvidence(canaryRate, 0.9, calibration?.At, calibration?.Basis ?? "none", verifier?.Used, null, LedgerStore.Coverage(LedgerStore.Load(context.Workspace)), null, total == 0 ? null : (double)hit / total);
        }

        return (report, MaturityCalculator.Compute(context.Config, report, evidence));
    }
}
