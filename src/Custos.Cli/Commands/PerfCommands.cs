using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Util;
using Custos.Statistics.Performance;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.Performance;

namespace Custos.Cli.Commands;

/// <summary>段階5：性能のコマンド（設計書 第13章、第23章、第26.4節）。</summary>
public static class PerfCommands
{
    /// <summary>custos perf run：負荷をかけて計測する（妥当性チェック付き。第23.1節）。</summary>
    public static async Task<int> RunAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        await EnsureProfileAsync(context, args.Get("profile"), cancellationToken).ConfigureAwait(false);
        var run = context.Results.Begin("perf", context.Config.Hash, context.Config.FileHashes, new Dictionary<string, string>(StringComparer.Ordinal) { ["app_image"] = context.Lab.State.AppImage, ["profile"] = context.Lab.State.Profile ?? "none" });
        var runs = (int)args.Number("runs", 5);
        var (valid, all, environmentProblem) = await PerfRunner.SeriesAsync(context.Lab, runs, (int)args.Number("games", 10), (int)args.Number("concurrency", 5), (int)args.Number("seed", 1), run, "series", cancellationToken).ConfigureAwait(false);
        var p99 = valid.Select(v => v.P99Ms).ToList();
        var summary = new JsonObject
        {
            ["app_image"] = context.Lab.State.AppImage,
            ["profile"] = context.Lab.State.Profile,
            ["valid_runs"] = valid.Count,
            ["all_runs"] = all.Count,
            ["environment_problem"] = environmentProblem,
            ["p99_ms"] = new JsonArray(p99.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["p99_median_ms"] = p99.Count == 0 ? null : Descriptive.Median(p99), // Percentile の q は 0〜1（以前は 50 を渡して例外で落ちていた）
            ["cv"] = p99.Count < 2 ? null : Descriptive.CoefficientOfVariation(p99),
            ["error_rate_max"] = valid.Count == 0 ? null : valid.Max(v => v.ErrorRate),
            ["memory_growth"] = valid.Count == 0 ? null : valid.Max(v => v.AppMemoryBeforeMb > 0 ? v.AppMemoryAfterMb / v.AppMemoryBeforeMb : 1),
            ["invalid_reasons"] = new JsonArray(all.Where(m => m.State == MeasurementState.Invalid).SelectMany(m => m.Reasons).Distinct(StringComparer.Ordinal).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["slo_violations"] = all.Count(m => m.State == MeasurementState.SloViolation),
        };
        run.WriteJson("perf.json", summary);
        var text = string.Create(CultureInfo.InvariantCulture, $"計測：有効 {valid.Count}/{all.Count} 回{(environmentProblem ? "（3回続けて無効：環境の問題）" : string.Empty)}。着手の反映の99パーセンタイル（中央値）{summary["p99_median_ms"]} ms、変動係数 {summary["cv"]}、エラー率の最大 {summary["error_rate_max"]}、SLO 違反 {summary["slo_violations"]} 回");
        run.WriteText("perf.md", text);
        run.Seal(environmentProblem ? "failed" : "passed");
        CliContext.Out(text);
        CliContext.Log($"結果：{run.Dir}");
        return environmentProblem ? 1 : 0;
    }

    /// <summary>custos perf compare &lt;基準の結果&gt; &lt;比べる結果&gt;：ビルド間の比較（第23.2節）。</summary>
    public static Task<int> CompareAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var first = args.Arg(0, "基準の結果のID");
        var second = args.Arg(1, "比べる結果のID");
        var baseline = ReadSeries(context, first);
        var candidate = ReadSeries(context, second);
        var detectable = context.Config.Environment.ResourceProfiles.Values.FirstOrDefault()?.Validity.DetectRegression ?? 0.2;
        var result = Comparison.Compare(baseline, candidate, detectable, 5, Comparison.MaxCoefficientOfVariation(detectable, 5), higherIsWorse: true, alpha: 0.05, minimumRelativeChange: detectable / 2);
        var run = context.Results.Begin("perf-compare", context.Config.Hash, context.Config.FileHashes);
        var json = new JsonObject
        {
            ["baseline"] = first,
            ["candidate"] = second,
            ["outcome"] = result.Outcome.ToString(),
            ["relative_change"] = double.IsNaN(result.RelativeChange) ? null : result.RelativeChange,
            ["reasons"] = new JsonArray(result.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        };
        run.WriteJson("compare.json", json);
        var label = result.Outcome switch
        {
            ComparisonOutcome.NoDifference => "差なし",
            ComparisonOutcome.Regression => "劣化あり",
            ComparisonOutcome.Improvement => "改善",
            _ => "判断できない",
        };
        var text = string.Create(CultureInfo.InvariantCulture, $"比較：{label}（相対変化 {(double.IsNaN(result.RelativeChange) ? "-" : result.RelativeChange.ToString("P1", CultureInfo.InvariantCulture))}）。{string.Join("／", result.Reasons)}");
        run.WriteText("compare.md", text);
        run.Seal(result.Outcome == ComparisonOutcome.Regression ? "failed" : "passed");
        CliContext.Out(text);
        return Task.FromResult(result.Outcome == ComparisonOutcome.Regression ? 1 : 0);
    }

    /// <summary>custos perf capacity：基準を守れる最大の同時対局数（第26.4節）。</summary>
    public static async Task<int> CapacityAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        await EnsureProfileAsync(context, args.Get("profile"), cancellationToken).ConfigureAwait(false);
        var run = context.Results.Begin("perf-capacity", context.Config.Hash, context.Config.FileHashes);
        var result = await PerfRunner.CapacityAsync(context.Lab, run, (int)args.Number("max", 64), cancellationToken).ConfigureAwait(false);
        run.WriteJson("capacity.json", result);
        var text = string.Create(CultureInfo.InvariantCulture, $"容量：同時 {result["capacity_concurrent_games"]} 対局まで基準（{result["slo_ms"]} ms）を守れた。限界の要因：{result["bottleneck"]}");
        run.WriteText("capacity.md", text);
        run.Seal("passed");
        CliContext.Out(text);
        return 0;
    }

    /// <summary>プロファイルを指定したら、その構成で起動し直す。</summary>
    private static async Task EnsureProfileAsync(CliContext context, string? profile, CancellationToken cancellationToken)
    {
        if (profile == null || (context.Lab.HasState && context.Lab.State.Profile == profile))
        {
            return;
        }

        var build = context.Lab.HasState ? context.Lab.State.BuildKind : "production";
        await context.Lab.UpAsync(new UpOptions(build, new HashSet<string>(StringComparer.Ordinal), profile, false, false), cancellationToken).ConfigureAwait(false);
    }

    private static List<double> ReadSeries(CliContext context, string id)
    {
        var dir = Directory.GetDirectories(Path.Combine(context.Workspace.ResultsDir, "runs"), id + "*").FirstOrDefault()
            ?? throw new CustosException($"結果が見つかりません：{id}");
        var file = Path.Combine(dir, "perf", "series.jsonl");
        if (!File.Exists(file))
        {
            throw new CustosException($"性能の計測の結果ではありません：{id}");
        }

        return File.ReadAllLines(file).Select(l => JsonNode.Parse(l)).OfType<JsonNode>()
            .Where(n => n["state"]?.ToString() != "Invalid")
            .Select(n => n["latency_p99_ms"]?.GetValue<double>() ?? double.NaN)
            .Where(v => !double.IsNaN(v))
            .ToList();
    }
}

/// <summary>文字列の補助。</summary>
internal static class PerfText
{
    public static string Format(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    public static string Join(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }
}
