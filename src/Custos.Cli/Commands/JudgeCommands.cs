using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Registry;
using Custos.Registry.Bugs;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Probes;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Cli.Commands;

/// <summary>段階1：機械判定のコマンド（設計書 第27.2節）。</summary>
public static class JudgeCommands
{
    /// <summary>判定器の名前（登録簿の found_by に使う）。</summary>
    private static string Finder(string tool) => $"custos-{tool}@{FrameworkInfo.Version}";

    /// <summary>custos judge：いまの状態を、汎用の判定と不変条件で判定する。--scenario を付けると、実行してから判定する。</summary>
    public static async Task<int> JudgeAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var lab = context.Lab;
        var run = context.Results.Begin("judge", context.Config.Hash, context.Config.FileHashes);
        var since = args.Number("since", 0);
        var baseline = await JudgeContext.CaptureAsync(lab, cancellationToken).ConfigureAwait(false) with { RecordsSeq = since };
        if (since == 0)
        {
            baseline = baseline with { RecordsSeq = 0 };
        }

        if (args.Get("scenario") is { } file)
        {
            baseline = await JudgeContext.CaptureAsync(lab, cancellationToken).ConfigureAwait(false);
            var result = await new ScenarioRunner(lab, run, CliContext.Log).RunAsync(Scenario.Load(ResolveScenario(context, file)), cancellationToken).ConfigureAwait(false);
            run.WriteJson("scenario.json", JsonNode.Parse(Json.SerializeCompact(result)));
            await lab.SettleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }

        var reports = await JudgeRunner.RunAsync(new JudgeContext(lab, run, baseline), JudgeRunner.Stage1(), cancellationToken).ConfigureAwait(false);
        var findings = reports.SelectMany(r => r.Findings).ToList();
        return Finish(context, run, "judge", findings, JudgeRunner.Summarize(reports), args.Flag("register"));
    }

    /// <summary>custos fuzz：イベントのファジング（第26.7節）。</summary>
    public static async Task<int> FuzzAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("fuzz", context.Config.Hash, context.Config.FileHashes);
        var fuzzer = new EventFuzzer(context.Lab, run, (int)args.Number("seed", 1), CliContext.Log);
        await context.Lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var findings = await fuzzer.RunAsync(args.All("event"), (int)args.Number("rounds", 1), cancellationToken).ConfigureAwait(false);
        return Finish(context, run, "fuzz", findings, null, !args.Flag("no-register"));
    }

    /// <summary>custos probe phases：状態遷移と選択肢の検査（第25.4節）。</summary>
    public static async Task<int> PhasesAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("probe-phases", context.Config.Hash, context.Config.FileHashes);
        var findings = await new PhaseProber(context.Lab, run, CliContext.Log).RunAsync(cancellationToken).ConfigureAwait(false);
        return Finish(context, run, "phases", findings, null, !args.Flag("no-register"));
    }

    /// <summary>custos probe authority：宣言と実態のずれの検査（第15.4節）。</summary>
    public static async Task<int> AuthorityAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("probe-authority", context.Config.Hash, context.Config.FileHashes);
        var findings = await new AuthorityProber(context.Lab, run, CliContext.Log).RunAsync(cancellationToken).ConfigureAwait(false);
        return Finish(context, run, "authority", findings, null, !args.Flag("no-register"));
    }

    /// <summary>custos probe secrets：隠し情報と秘密の値、表示される入力、席の乗っ取りの検査（第25.5〜25.7節）。</summary>
    public static async Task<int> SecretsAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("probe-secrets", context.Config.Hash, context.Config.FileHashes);
        var prober = new SecurityProber(context.Lab, run, CliContext.Log);
        var findings = new List<Finding>();
        findings.AddRange(await prober.RunAsync(cancellationToken).ConfigureAwait(false));
        var games = (int)args.Number("games", 0);
        if (games > 0)
        {
            var (_, gameFindings, _) = await new BotPlayRunner(context.Lab, run, CliContext.Log).RunAsync(games, (int)args.Number("seed", 100), true, cancellationToken).ConfigureAwait(false);
            findings.AddRange(gameFindings);
        }

        return Finish(context, run, "secrets", findings, null, !args.Flag("no-register"));
    }

    /// <summary>custos bot play：相手役のボットで対局を回し、判定する。</summary>
    public static async Task<int> BotPlayAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var run = context.Results.Begin("bot-play", context.Config.Hash, context.Config.FileHashes);
        var (games, findings, reports) = await new BotPlayRunner(context.Lab, run, CliContext.Log).RunAsync((int)args.Number("games", 3), (int)args.Number("seed", 1), !args.Flag("no-reset"), cancellationToken).ConfigureAwait(false);
        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture, $"対局 {games.Count} 回：最後まで {games.Count(g => g.StopReason == "all_finished")} 回、平均 {(games.Count == 0 ? 0 : games.Average(g => g.Moves)):F1} 手、平均 {(games.Count == 0 ? 0 : games.Average(g => g.Seconds)):F1} 秒");
        summary.Append(JudgeRunner.Summarize(reports));
        return Finish(context, run, "bot", findings, summary.ToString(), !args.Flag("no-register"));
    }

    /// <summary>custos stage1：段階1の機械判定をまとめて実行する（ボットの対局、ファジング、局面の検査、隠し情報となりすまし）。</summary>
    public static async Task<int> Stage1Async(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var lab = context.Lab;
        var run = context.Results.Begin("stage1", context.Config.Hash, context.Config.FileHashes);
        var findings = new List<Finding>();
        var sections = new StringBuilder();
        async Task Section(string name, Func<Task<IReadOnlyList<Finding>>> body)
        {
            CliContext.Log($"== {name}");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var found = await body().ConfigureAwait(false);
                findings.AddRange(found);
                sections.AppendLine(CultureInfo.InvariantCulture, $"- {name}：{found.Count(f => f.Category != "coverage")} 件（{watch.Elapsed.TotalSeconds:F0} 秒）");
            }
            catch (Exception e) when (e is CustosException or TimeoutException or IOException)
            {
                sections.AppendLine(CultureInfo.InvariantCulture, $"- {name}：実行できませんでした（{e.Message}）");
                findings.Add(Finding.Create("stage1", "section_failed", Severity.Low, 1, "coverage", $"{name}を実行できませんでした（未確認）：{e.Message}", null, [], name));
            }
        }

        await Section("ボットの対局と汎用の判定・不変条件", async () => (await new BotPlayRunner(lab, run, CliContext.Log).RunAsync((int)args.Number("games", 3), (int)args.Number("seed", 1), true, cancellationToken).ConfigureAwait(false)).Findings).ConfigureAwait(false);
        await Section("状態遷移と選択肢の検査", () => new PhaseProber(lab, run, CliContext.Log).RunAsync(cancellationToken)).ConfigureAwait(false);
        await Section("宣言と実態のずれの検査（サーバー権威）", () => new AuthorityProber(lab, run, CliContext.Log).RunAsync(cancellationToken)).ConfigureAwait(false);
        await Section("隠し情報となりすましの検査", () => new SecurityProber(lab, run, CliContext.Log).RunAsync(cancellationToken)).ConfigureAwait(false);
        await Section("イベントのファジング", async () =>
        {
            await lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            return await new EventFuzzer(lab, run, (int)args.Number("seed", 1), CliContext.Log).RunAsync([], (int)args.Number("rounds", 1), cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await Section("依存ライブラリと静的解析", () => Task.FromResult(StaticChecks.Run(context.Workspace, run))).ConfigureAwait(false);
        await lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        return Finish(context, run, "stage1", findings, sections.ToString(), !args.Flag("no-register"));
    }

    private static string ResolveScenario(CliContext context, string file) =>
        File.Exists(file) ? file : Path.Combine(context.Workspace.VerifyRepo, "scenarios", file);

    /// <summary>見つけたものを書き出し、登録簿に入れ、表示する。</summary>
    internal static int Finish(CliContext context, Run run, string tool, IReadOnlyList<Finding> findings, string? summary, bool register)
    {
        var grouped = Findings.Group(findings);
        run.WriteJson("findings.json", new JsonArray(grouped.Select(g => (JsonNode?)new JsonObject
        {
            ["count"] = g.Count,
            ["finding"] = JsonNode.Parse(Json.SerializeCompact(g.Finding)),
        }).ToArray()));
        var registered = new JsonArray();
        if (register)
        {
            var paths = RegistryPaths.From(context.Workspace);
            var registry = BugRegistry.OpenAdmin(paths.RegistryDb);
            foreach (var (fingerprint, bugId, created) in FindingRegistrar.Register(registry, findings, Finder(tool)))
            {
                registered.Add(new JsonObject { ["fingerprint"] = fingerprint, ["bug"] = bugId, ["created"] = created });
            }

            run.WriteJson("registered.json", registered);
            RegistryExporter.Export(registry, paths.ExportDir);
        }

        var markdown = new StringBuilder();
        markdown.AppendLine(CultureInfo.InvariantCulture, $"# {tool}（{run.Id}）");
        markdown.AppendLine();
        if (summary != null)
        {
            markdown.AppendLine(summary);
            markdown.AppendLine();
        }

        var counts = Findings.CountBySeverity(findings.Where(f => f.Category != "coverage"));
        markdown.AppendLine(CultureInfo.InvariantCulture, $"見つけたもの：致命的 {counts.GetValueOrDefault(Severity.Critical)}、高 {counts.GetValueOrDefault(Severity.High)}、中 {counts.GetValueOrDefault(Severity.Medium)}、低 {counts.GetValueOrDefault(Severity.Low)}（未確認・網羅の不足 {findings.Count(f => f.Category == "coverage")} 件）");
        markdown.AppendLine();
        markdown.AppendLine("| 重大度 | 段階 | 分類 | 内容 | 回数 | 登録簿 |");
        markdown.AppendLine("|---|---|---|---|---|---|");
        foreach (var (finding, count) in grouped.OrderBy(g => g.Finding.Severity))
        {
            var bug = registered.OfType<JsonObject>().FirstOrDefault(r => r["fingerprint"]?.GetValue<string>() == finding.Fingerprint)?["bug"]?.GetValue<string>() ?? "-";
            markdown.AppendLine(CultureInfo.InvariantCulture, $"| {finding.Severity} | {finding.Stage} | {finding.Category} | {finding.Summary.Replace("|", "\\|", StringComparison.Ordinal)} | {count} | {bug} |");
        }

        run.WriteText("findings.md", markdown.ToString());
        var critical = counts.GetValueOrDefault(Severity.Critical) + counts.GetValueOrDefault(Severity.High);
        run.Seal(critical == 0 ? "passed" : "failed");
        CliContext.Out(markdown.ToString());
        CliContext.Log($"結果：{run.Dir}");
        return critical == 0 ? 0 : 1;
    }
}
