using System.Globalization;
using Custos.Core.Config;
using Custos.Core.Judging;
using Custos.Web.SocketIO.Determinism;
using Custos.Web.SocketIO.Repro;

namespace Custos.Cli.Commands;

/// <summary>段階2：再現性のコマンド（設計書 第27.2節）。</summary>
public static class ReproCommands
{
    /// <summary>custos replay：手のログからの再生（一致の検査）。</summary>
    public static async Task<int> ReplayAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("replay", context.Config.Hash, context.Config.FileHashes);
        var findings = await new ReplayChecker(context.Lab, run, CliContext.Log).RunAsync(args.Get("game"), cancellationToken).ConfigureAwait(false);
        return JudgeCommands.Finish(context, run, "replay", findings, null, !args.Flag("no-register"));
    }

    /// <summary>custos determinism：決定性の等級を測る（第22.7節）。</summary>
    public static async Task<int> DeterminismAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("determinism", context.Config.Hash, context.Config.FileHashes);
        var runs = (int)args.Number("runs", 5);
        var result = await new DeterminismRunner(context.Lab, run).MeasureAsync(runs, cancellationToken).ConfigureAwait(false);
        run.WriteJson("determinism.json", result.ToJson());
        var findings = new List<Finding>();
        if (result.Grade == "statistical")
        {
            findings.Add(Finding.Create("determinism", "not_reproducible", Severity.Medium, 2, "reproducibility", $"同じスナップショット・同じ入力で {runs} 回実行すると、正規化しても状態が一致しません（等級：{result.GradeLabel}。食い違うキー：{string.Join(", ", result.DifferingKeys.Take(8))}）", null, [new Core.Conformance.Evidence("測定", "determinism.json")], "determinism"));
        }

        var summary = string.Create(CultureInfo.InvariantCulture, $"決定性の等級：{result.GradeLabel}（{runs} 回。除外したキー {result.VolatileCount} 個。食い違うキー {result.DifferingKeys.Count} 個）");
        return JudgeCommands.Finish(context, run, "determinism", findings, summary, false);
    }

    /// <summary>custos probe time：制限時間の検査（時間切れと着手を同時に。第25.8節）。テスト用ビルドで実行する。</summary>
    public static async Task<int> TimeAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("probe-time", context.Config.Hash, context.Config.FileHashes);
        var findings = await new TimeProber(context.Lab, run, CliContext.Log).RunAsync(cancellationToken).ConfigureAwait(false);
        return JudgeCommands.Finish(context, run, "time", findings, null, !args.Flag("no-register"));
    }

    /// <summary>custos probe disruption：切断・再接続・再起動の注入（第25.8節、第26.5節）。</summary>
    public static async Task<int> DisruptionAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("probe-disruption", context.Config.Hash, context.Config.FileHashes);
        var findings = await new DisruptionProber(context.Lab, run, CliContext.Log).RunAsync(cancellationToken).ConfigureAwait(false);
        return JudgeCommands.Finish(context, run, "disruption", findings, null, !args.Flag("no-register"));
    }
}
