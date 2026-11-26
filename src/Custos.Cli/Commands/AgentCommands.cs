using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Judging;
using Custos.Core.Util;
using Custos.Registry;
using Custos.Registry.Bugs;
using Custos.Registry.Commits;
using Custos.Registry.Git;
using Custos.Web.SocketIO.Agents;
using Custos.Web.SocketIO.Probes;

namespace Custos.Cli.Commands;

/// <summary>段階3：AIによる探索と修正のコマンド（設計書 第27.2節）。</summary>
public static class AgentCommands
{
    /// <summary>custos ledger build：確認対象の台帳を作る（第21.10節）。</summary>
    public static Task<int> LedgerBuildAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var definition = LedgerDefinition.Load(context.Workspace, context.Config);
        var ledger = LedgerStore.Build(definition, LedgerStore.Load(context.Workspace));
        LedgerStore.Save(context.Workspace, ledger);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"台帳：{ledger.Count} 件（{LedgerStore.PathOf(context.Workspace)}）");
        foreach (var group in ledger.GroupBy(e => e.Kind))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key}：{group.Count()} 件（未確認 {group.Count(e => e.Status == "unverified")}、確認済み {group.Count(e => e.Status == "verified")}、バグあり {group.Count(e => e.Status == "bug")}）");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"網羅：{LedgerStore.Coverage(ledger):P1}、自由な探索の割合 {definition.FreeShare:P0}");
        CliContext.Out(text.ToString());
        return Task.FromResult(0);
    }

    /// <summary>custos explore：台帳から確認対象を割り当てて探索する。</summary>
    public static async Task<int> ExploreAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("explore", context.Config.Hash, context.Config.FileHashes);
        var role = args.Get("role") ?? "explorer";
        var agent = args.Get("agent") ?? "scripted";
        var definition = LedgerDefinition.Load(context.Workspace, context.Config);
        var ledger = LedgerStore.Build(definition, LedgerStore.Load(context.Workspace));
        var assignment = context.Config.Agents?.Roles.GetValueOrDefault(role);
        IExplorer explorer = agent == "claude-code" ? new ClaudeCodeExplorer(context.Lab, run, role, assignment?.Model) : new ScriptedExplorer(context.Lab, run, CliContext.Log);
        if (agent == "claude-code" && new ClaudeCodeLauncher(context.Lab).Unavailable() is { } reason)
        {
            CliContext.Log($"Claude Code を起動できません：{reason}");
        }

        var checker = new ReportChecker(context.Lab, definition, ledger);
        var targets = ledger.Where(e => e.Status == "unverified").Take((int)args.Number("targets", 3)).ToList();
        var results = new List<(TrialReport, ReportCheck)>();
        var index = 0;
        foreach (var entry in targets)
        {
            index++;
            var trialId = $"E{index.ToString("D3", CultureInfo.InvariantCulture)}";
            var report = await explorer.ExploreAsync(entry, trialId, (int)args.Number("seed", 1) + index, cancellationToken).ConfigureAwait(false);
            var check = await checker.CheckAsync(report, run.Dir, cancellationToken).ConfigureAwait(false);
            run.WriteJson($"trials/{trialId}/report.json", report);
            run.WriteJson($"trials/{trialId}/check.json", check);
            results.Add((report, check));
            CliContext.Out($"{trialId} {entry.Id}：{report.Result}、機械チェック {(check.Valid ? "合格" : "不合格（" + string.Join("／", check.Problems) + "）")}");
        }

        ledger = LedgerStore.Apply(ledger, results);
        LedgerStore.Save(context.Workspace, ledger);
        run.Seal(results.All(r => r.Item2.Valid) ? "passed" : "failed");
        CliContext.Out($"網羅：{LedgerStore.Coverage(ledger):P1}（結果 {run.Dir}）");
        return 0;
    }

    /// <summary>custos loop：探索・判定・修正のループを回す（第2.4節）。</summary>
    public static async Task<int> LoopAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("loop", context.Config.Hash, context.Config.FileHashes);
        var agent = args.Get("agent") ?? "scripted";
        var paths = RegistryPaths.From(context.Workspace);
        var registry = BugRegistry.OpenAdmin(paths.RegistryDb);
        var sign = File.Exists(Path.Combine(context.Workspace.SecretsDir, "framework_signing_ed25519"));

        IReadOnlyList<(string, string, bool)> Register(IEnumerable<Finding> findings, string finder)
        {
            var result = FindingRegistrar.Register(registry, findings, finder);
            RegistryExporter.Export(registry, paths.ExportDir);
            return result;
        }

        async Task<string?> CommitAsync(string bugId, string detail, CancellationToken token)
        {
            // コミットの入口（第20.4節）：作業用の複製の差分を、対象アプリのリポジトリに、種類と署名を付けてコミットする
            var repo = context.Workspace.AppRepo;
            RegistryCommands.RefuseReadOnly(repo);
            var diff = await ProcessRunner.RunAsync("git", ["-C", context.Workspace.SandboxWorkspaceDir, "diff", "HEAD", "--binary"], timeout: TimeSpan.FromMinutes(1), cancellationToken: token).ConfigureAwait(false);
            var patch = run.WriteText($"fixes/{bugId}/fix.patch", diff.StandardOutput);
            var apply = await ProcessRunner.RunAsync("git", ["-C", repo, "apply", "--index", patch], timeout: TimeSpan.FromMinutes(1), cancellationToken: token).ConfigureAwait(false);
            if (!apply.Succeeded)
            {
                CliContext.Log($"修正を対象アプリのリポジトリに当てられません：{apply.StandardError}");
                return null;
            }

            var gateway = new CommitGateway(new GitClient(repo), new CommitGatewayOptions(paths, RequireRegressionTest: false, DetectMixed: true, Sign: sign, SigningKeyPath: sign ? Path.Combine(context.Workspace.SecretsDir, "framework_signing_ed25519") : null, UseCheckpoint: false, CopyRegressionTests: true, VerifyRepoRoot: context.Workspace.VerifyRepo, CommitterName: null, CommitterEmail: null));
            var result = await gateway.CommitAsync(new CommitRequest("fix", bugId, $"fix: {bugId} の修正（修正役 {agent}）\n\n{detail}", "dev_agent", false, null, null), token).ConfigureAwait(false);
            if (!result.Committed)
            {
                CliContext.Log("コミットの入口で止められました：" + string.Join("／", result.Problems));
            }

            return result.Committed ? result.Commit : null;
        }

        var iterations = await new LoopRunner(context.Lab, run, CliContext.Log).RunAsync((int)args.Number("iterations", 1), (int)args.Number("targets", 4), agent, CommitAsync, Register, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# ループ（{run.Id}、探索役 {agent}）");
        text.AppendLine();
        text.AppendLine("| 周 | 割り当て | レポート | 機械チェック合格 | バグあり | 機械判定 | 新しいバグ | 修正 | 網羅 |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var i in iterations)
        {
            var fixes = i.Fixes.Count == 0 ? "-" : string.Join("<br>", i.Fixes.Select(f => $"{f.BugId}：{(f.Committed ? "コミット " + f.Commit?[..Math.Min(8, f.Commit.Length)] : f.Detail)}"));
            text.AppendLine(CultureInfo.InvariantCulture, $"| {i.Iteration} | {i.Assigned} | {i.Reports} | {i.ValidReports} | {i.BugReports} | {i.MachineFindings} | {string.Join(", ", i.NewBugs)} | {fixes} | {i.Coverage:P1} |");
        }

        var sandbox = iterations.Count > 0 ? iterations[0].SandboxCheck : null;
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"修正役のサンドボックスから判定器と設定に書き込めない：{(sandbox?["ok"]?.GetValue<bool>() == true ? "確認した" : "確認できなかった")}（sandbox-write-check.json）");
        run.WriteText("loop.md", text.ToString());
        run.Seal(sandbox?["ok"]?.GetValue<bool>() == true ? "passed" : "failed");
        CliContext.Out(text.ToString());
        return sandbox?["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    /// <summary>custos hidden run：隠しシナリオを回す（リリース判定のときだけ）。</summary>
    public static async Task<int> HiddenAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("hidden", context.Config.Hash, context.Config.FileHashes);
        var (summary, findings) = await new HiddenRunner(context.Lab, run, CliContext.Log).RunAsync((int)args.Number("visible-runs", 3), cancellationToken).ConfigureAwait(false);
        var text = string.Create(CultureInfo.InvariantCulture, $"隠しシナリオ：失敗 {summary["hidden_failures"]}/{summary["hidden_runs"]}。見えるシナリオ：失敗 {summary["visible_failures"]}/{summary["visible_runs"]}。合わせ込みの兆候：{(summary["overfitting_sign"]?.GetValue<bool>() == true ? "あり" : "なし")}");
        return JudgeCommands.Finish(context, run, "hidden", findings, text, !args.Flag("no-register"));
    }
}
