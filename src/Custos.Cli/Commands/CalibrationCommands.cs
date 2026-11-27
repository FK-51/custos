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
            found.AddRange((await new BotPlayRunner(lab, run, CliContext.Log).RunAsync(games, 7, true, token).ConfigureAwait(false)).Findings);
            var stage1 = args.Flag("stage1");
            if (args.Flag("phases") || stage1)
            {
                found.AddRange(await new PhaseProber(lab, run, CliContext.Log).RunAsync(token).ConfigureAwait(false));
            }

            // --stage1：段階1の機械判定をすべて探索に入れる（custos stage1 と同じ。静的解析はアプリのリポジトリを見るので除く）。
            // 以前は、ファジングで落ちる入力（クラッシュ系の校正用バグ）を探索しておらず、段階1なら見つかるものを見逃していた
            if (stage1)
            {
                found.AddRange(await new AuthorityProber(lab, run, CliContext.Log).RunAsync(token).ConfigureAwait(false));
                found.AddRange(await new SecurityProber(lab, run, CliContext.Log).RunAsync(token).ConfigureAwait(false));
                found.AddRange(await new EventFuzzer(lab, run, 1, CliContext.Log).RunAsync([], 1, token).ConfigureAwait(false));
            }
            // 探索は基準と校正用バグごとに呼ぶので、試行の ID に回の番号を付ける（同じ実行の中で証拠のファイルがぶつからないように）
            round++;
            if (args.Get("agent") is "scripted" or "claude-code")
            {
                var definition = LedgerDefinition.Load(context.Workspace, context.Config);
                var ledger = LedgerStore.Build(definition, null);
                IExplorer explorer = args.Get("agent") == "claude-code" ? new ClaudeCodeExplorer(lab, run, "explorer", context.Config.Agents?.Roles.GetValueOrDefault("explorer")?.Model) : new ScriptedExplorer(lab, run, CliContext.Log);
                var index = 0;
                foreach (var entry in ledger.Where(e => e.Kind == "card" && e.Situation == "normal").Take((int)args.Number("targets", 3)))
                {
                    // 台帳は1枚のカードに観点（効果の結果、相手の見え方）ごとの項目があり、対象（カードの番号）だけでは一意にならない
                    index++;
                    var trialId = string.Create(CultureInfo.InvariantCulture, $"K{round:D2}-{index:D2}");
                    var report = await explorer.ExploreAsync(entry, trialId, 11, token).ConfigureAwait(false);
                    if (report.Result == "bug")
                    {
                        found.Add(Finding.Create("explorer", "report_bug", Severity.High, 3, "exploration", report.Notes, null, [], entry.Id));
                    }
                }
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

        // 検証役の感度：校正の結果があればそれを使う。なければ、機械判定に寄せた代役の感度は「機械判定で見える誤りだけ」なので、上限の計算には使わず未確認とする
        var verifierAgent = args.Get("agent") ?? "scripted";
        double? sensitivity = args.Get("sensitivity") is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : null;
        var rows = new JsonArray();
        var errors = 0;
        foreach (var entry in sample)
        {
            // ブラインド判定：元の結論を伏せ、証拠（通信の記録）だけで、機械判定をやり直す（代役）
            var trial = entry.Reports.Count > 0 ? entry.Reports[^1] : null;
            var verdict = "no_bug";
            var detail = "証拠の記録を判定し直して、問題は見つからなかった";
            var reportPath = trial == null ? null : FindReport(context.Workspace.ResultsDir, trial);
            if (reportPath == null)
            {
                verdict = "unknown";
                detail = "レポートが見つからない";
            }
            else if (verifierAgent == "claude-code" && new ClaudeCodeLauncher(context.Lab).Unavailable() is { } reason)
            {
                verdict = "unknown";
                detail = "検証役（Claude Code）を起動できない：" + reason;
            }
            else
            {
                var report = System.Text.Json.JsonSerializer.Deserialize<TrialReport>(await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false), Json.Options);
                if (report != null && Directory.Exists(Path.Combine(context.Workspace.RecordsDir, report.RecordsSession)))
                {
                    // そのレポートのセッションと範囲の記録だけで判定し直す（以前は、いまの検証環境の記録を report.RecordsFrom から最後まで
                    // 読んでいて、別の日のカナリアの違反まで「誤り」と判定していた）
                    var store = new Custos.Web.SocketIO.Records.RecordStore(Path.Combine(context.Workspace.RecordsDir, report.RecordsSession));
                    using var db = await RecordsDatabase.BuildAsync(context.Lab, store, report.RecordsFrom, report.RecordsTo, false, cancellationToken).ConfigureAwait(false);
                    foreach (var invariant in (context.Config.Invariants?.Invariants ?? []).Where(i => i.Kind == InvariantKind.SentSql && i.Query != null))
                    {
                        try
                        {
                            if (db.Query(invariant.Query ?? string.Empty).Rows.Count > 0)
                            {
                                verdict = "bug";
                                detail = $"証拠の記録で不変条件 {invariant.Id} が破れている";
                                break;
                            }
                        }
                        catch (Microsoft.Data.Sqlite.SqliteException)
                        {
                        }
                    }
                }
            }

            if (verdict == "bug")
            {
                errors++;
            }

            rows.Add(new JsonObject { ["target"] = entry.Id, ["report"] = trial, ["verifier_verdict"] = verdict, ["detail"] = detail });
        }

        double? upper = n == 0 ? null : Proportions.ErrorRateUpperBound(errors, n, confidence, sensitivity ?? 1.0);
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
            ["sensitivity_note"] = sensitivity == null ? "検証役の感度が未測定のため、上限は感度1（見落としなし）を仮定した値。実際の上限はこれより大きい（未確認）" : null,
            ["epsilon_targets"] = new JsonArray(epsilon.Select(e => (JsonNode?)JsonNode.Parse(Json.SerializeCompact(e))).ToArray()),
            ["rows"] = rows,
        };
        run.WriteJson("sample.json", summary);
        var text = string.Create(CultureInfo.InvariantCulture, $"抜き取り：台帳 {ledger.Count} 件、確認済み {verified.Count} 件から {n} 件。検証役が誤りと判定 {errors} 件。誤り率の上限（{confidence:P0}）：{(upper is { } u ? u.ToString("P1", CultureInfo.InvariantCulture) : "出せない")}{(sensitivity == null ? "（検証役の感度は未測定。感度1を仮定）" : string.Empty)}");
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
        // 別の記録として見つけた分（例：BUG-0024 は BUG-0004 の重複）まで、未解決として二重に数えていた
        var open = registry.List()
            .Where(b => b.FixedIn == null && !registry.Links(b.Id).Any(l => l.Kind == "duplicate_of"))
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
            evidence = new CalibrationEvidence(canaryRate, 0.9, calibration?.At, calibration?.Basis ?? "none", null, null, LedgerStore.Coverage(LedgerStore.Load(context.Workspace)), null, total == 0 ? null : (double)hit / total);
        }

        return (report, MaturityCalculator.Compute(context.Config, report, evidence));
    }
}
