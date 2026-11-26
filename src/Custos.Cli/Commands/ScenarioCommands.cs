using System.Text.Json.Nodes;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Cli.Commands;

/// <summary>シナリオのコマンド。</summary>
public static class ScenarioCommands
{
    /// <summary>custos scenario run。</summary>
    public static async Task<int> RunAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var file = args.Arg(0, "シナリオのファイル");
        if (!File.Exists(file))
        {
            var candidate = Path.Combine(context.Workspace.VerifyRepo, "scenarios", file);
            file = File.Exists(candidate) ? candidate : throw new CustosException($"シナリオがありません：{file}");
        }

        var scenario = Scenario.Load(file);
        var run = context.Results.Begin("scenario", context.Config.Hash, context.Config.FileHashes, new Dictionary<string, string>(StringComparer.Ordinal) { ["scenario"] = scenario.Name });
        var runner = new ScenarioRunner(context.Lab, run, CliContext.Log);
        var result = await runner.RunAsync(scenario, cancellationToken).ConfigureAwait(false);
        run.WriteJson("scenario-result.json", result);
        run.WriteText("scenario.yaml", scenario.ToYaml());
        run.Seal(result.Ok ? "passed" : "failed");
        foreach (var step in result.Steps)
        {
            CliContext.Out($"{(step.Ok ? "OK " : "NG ")} {step.Index + 1,3} {step.Kind,-16} {step.Ms,8:0} ms  {Short(step.Detail)}");
        }

        CliContext.Out(result.Ok ? $"シナリオ {scenario.Name} は成功しました（{result.Ms:0} ms、結果 {run.Id}）" : $"シナリオ {scenario.Name} は失敗しました：{result.Error}（結果 {run.Id}）");
        if (args.Flag("json"))
        {
            CliContext.OutJson(JsonNode.Parse(Json.Serialize(result)));
        }

        return result.Ok ? 0 : 1;
    }

    private static string Short(string text) => text.Length <= 160 ? text : text[..160] + "…";
}
