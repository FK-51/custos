using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Conformance;
using Custos.Core.Util;
using Custos.Web.SocketIO.Conformance;
using Custos.Web.SocketIO.Lab;

namespace Custos.Cli.Commands;

/// <summary>custos conformance（指示書 タスク10、設計書 第17章）。</summary>
public static class ConformanceCommands
{
    /// <summary>
    /// 適合テストを実行する。--item で項目を選ぶ。--break で「わざと壊す確認」をする（指示書 第5.2節）。
    /// --all-breakage で、すべての壊し方について、壊すべき項目が不合格になることを確かめる。
    /// </summary>
    public static async Task<int> RunAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        if (args.Flag("all-breakage"))
        {
            return await AllBreakageAsync(context, args, cancellationToken).ConfigureAwait(false);
        }

        var breakage = args.All("break").ToHashSet(StringComparer.Ordinal);
        var selected = (args.Get("item") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        if (breakage.Count > 0 && selected.Count == 0)
        {
            selected = breakage.SelectMany(b => WebConformance.BreakageExpectations.TryGetValue(b, out var items) ? items : []).ToHashSet(StringComparer.Ordinal);
        }

        var stage = args.Get("stage");
        if (stage != null && selected.Count == 0)
        {
            selected = StageItems(stage);
        }

        await EnsureEnvironmentAsync(context, breakage, cancellationToken).ConfigureAwait(false);
        var report = await RunItemsAsync(context, breakage, selected, cancellationToken).ConfigureAwait(false);
        CliContext.Out(ConformanceRunner.Summarize(report));
        if (selected.Count > 0)
        {
            CliContext.Out("一部の項目だけを実行しました。成熟度は custos maturity で、項目ごとの最新の結果から出します");
        }
        else
        {
            var maturity = Custos.Core.Maturity.MaturityCalculator.Compute(context.Config, report, null);
            CliContext.Out($"成熟度：レベル {maturity.Level}（{maturity.Reason}）");
        }
        return report.RequiredAllPassed ? 0 : 1;
    }

    /// <summary>段階ごとの項目（段階0：A、B、C、E の必須項目。段階1：D を加える）。</summary>
    private static HashSet<string> StageItems(string stage) => stage switch
    {
        "0" => ["A-1", "A-2", "A-3", "A-4", "B-1", "B-2", "C-1", "C-2", "C-3", "C-4", "E-1", "E-2", "E-3", "E-4", "E-5"],
        "1" => ["A-1", "A-2", "A-3", "A-4", "B-1", "B-2", "C-1", "C-2", "C-3", "C-4", "D-1", "D-2", "E-1", "E-2", "E-3", "E-4", "E-5"],
        _ => [],
    };

    private static async Task<ConformanceReport> RunItemsAsync(CliContext context, IReadOnlySet<string> breakage, IReadOnlySet<string> selected, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["breakage"] = string.Join(",", breakage.Order(StringComparer.Ordinal)),
            ["app_image"] = context.Lab.State.AppImage,
            ["session"] = context.Lab.State.SessionId,
        };
        var run = context.Results.Begin("conformance", context.Config.Hash, context.Config.FileHashes, labels);
        var conformance = new ConformanceContext(run, context.Config, context.Workspace, breakage);
        conformance.Add(context.Lab);
        var report = await ConformanceRunner.RunAsync(conformance, WebConformance.Items(), selected, CliContext.Log, cancellationToken).ConfigureAwait(false);
        var maturity = Custos.Core.Maturity.MaturityCalculator.Compute(context.Config, report, null);
        run.WriteJson("maturity.json", maturity);
        run.Seal(report.RequiredAllPassed ? "passed" : "failed");
        CliContext.Log($"結果：{run.Dir}");
        return report;
    }

    /// <summary>壊し方に合わせて環境を作り直す（ネットワークやマウントを変える壊し方だけ）。</summary>
    private static async Task EnsureEnvironmentAsync(CliContext context, IReadOnlySet<string> breakage, CancellationToken cancellationToken)
    {
        var lab = context.Lab;
        var needed = breakage.Where(b => ComposeWriter.Breaks.NeedRecreate.Contains(b)).Order(StringComparer.Ordinal).ToList();
        var current = lab.HasState ? lab.State.Breakage.Where(b => ComposeWriter.Breaks.NeedRecreate.Contains(b)).Order(StringComparer.Ordinal).ToList() : null;
        if (current != null && current.SequenceEqual(needed, StringComparer.Ordinal))
        {
            return;
        }

        var build = lab.HasState ? lab.State.BuildKind : "production";
        var profile = lab.HasState ? lab.State.Profile : null;
        CliContext.Log(needed.Count > 0 ? $"壊し方（{string.Join(", ", needed)}）に合わせて環境を作り直します" : "通常の環境に作り直します");
        await lab.UpAsync(new UpOptions(build, needed.ToHashSet(StringComparer.Ordinal), profile, false, false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>わざと壊す確認をすべて行う（指示書 第5.2節）。</summary>
    private static async Task<int> AllBreakageAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        var rows = new JsonArray();
        var allDetected = true;
        var only = (args.Get("break") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var (breakage, expected) in WebConformance.BreakageExpectations)
        {
            if (only.Count > 0 && !only.Contains(breakage))
            {
                continue;
            }

            CliContext.Log($"== わざと壊す：{breakage}（不合格になるべき項目：{string.Join(", ", expected)}）");
            var set = new HashSet<string>(StringComparer.Ordinal) { breakage };
            await EnsureEnvironmentAsync(context, set, cancellationToken).ConfigureAwait(false);
            var report = await RunItemsAsync(context, set, expected.ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            var verdicts = new JsonObject();
            var detected = true;
            foreach (var item in expected)
            {
                var verdict = report.Result(item)?.Verdict ?? Verdict.Error;
                verdicts[item] = ConformanceRunner.VerdictLabel(verdict) + "：" + (report.Result(item)?.Summary ?? string.Empty);
                if (verdict == Verdict.Pass || verdict == Verdict.Skip)
                {
                    detected = false;
                }
            }

            allDetected &= detected;
            rows.Add(new JsonObject { ["breakage"] = breakage, ["expected_fail"] = new JsonArray(expected.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()), ["detected"] = detected, ["verdicts"] = verdicts, ["run"] = report.RunId });
        }

        // 元の環境に戻す
        await EnsureEnvironmentAsync(context, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        var run = context.Results.Begin("breakage", context.Config.Hash, context.Config.FileHashes);
        run.WriteJson("breakage.json", rows);
        var markdown = new StringBuilder();
        markdown.AppendLine("# わざと壊す確認（指示書 第5.2節）");
        markdown.AppendLine();
        markdown.AppendLine("| わざと作る状態 | 不合格になるべき項目 | 結果 | 実行 |");
        markdown.AppendLine("|---|---|---|---|");
        foreach (var row in rows.OfType<JsonObject>())
        {
            var verdicts = string.Join("<br>", (row["verdicts"] as JsonObject ?? []).Select(p => $"{p.Key}：{p.Value?.GetValue<string>().Replace("|", "\\|", StringComparison.Ordinal)}"));
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {row["breakage"]} | {string.Join(", ", (row["expected_fail"] as JsonArray ?? []).Select(e => e?.GetValue<string>()))} | {(row["detected"]?.GetValue<bool>() == true ? "検出した" : "見逃した")}<br>{verdicts} | {row["run"]} |");
        }

        run.WriteText("breakage.md", markdown.ToString());
        run.Seal(allDetected ? "passed" : "failed");
        CliContext.Out(markdown.ToString());
        return allDetected ? 0 : 1;
    }
}
