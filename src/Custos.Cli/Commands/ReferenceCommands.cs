using System.Globalization;
using Custos.Web.SocketIO.Repro;

namespace Custos.Cli.Commands;

/// <summary>段階6：参照モデル（設計書 第25.9節）。</summary>
public static class ReferenceCommands
{
    /// <summary>custos reference check：手のログを参照モデルで再生し、写しと比べる。</summary>
    public static async Task<int> CheckAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("reference", context.Config.Hash, context.Config.FileHashes);
        var (summary, findings) = await new ReferenceHarness(context.Lab, run, CliContext.Log).RunAsync(args.Get("game"), cancellationToken).ConfigureAwait(false);
        var text = string.Create(CultureInfo.InvariantCulture, $"参照モデル {summary["model_version"]}：対局 {summary["games"]}、比べた写し {summary["compared"]}、未検証の区間 {summary["unverified_spans"]}、対応しているカード {summary["supported_cards"]}/10、食い違い {summary["mismatches"]}");
        return JudgeCommands.Finish(context, run, "reference", findings, text, false);
    }
}
