using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Web.SocketIO.Probes;

/// <summary>1対局の結果。</summary>
public sealed record BotGameResult(int Game, int Seed, bool ScenarioOk, string? Error, string StopReason, int Moves, double Seconds);

/// <summary>
/// 相手役のボットで対局を最後まで回し、汎用の判定と不変条件で判定する（設計書 第25.9節、第26.6節の「毎ループ」）。
/// 1対局のシナリオは検証用リポジトリの scenarios/bot-game.yaml（{seed} と {game} を使える）。
/// 詰み（誰にも選択が来ないまま終局しない）も、ここで検出する。
/// </summary>
public sealed class BotPlayRunner
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;
    private static int _invocations;

    /// <summary>作る。</summary>
    public BotPlayRunner(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>対局を回して判定する。</summary>
    public async Task<(IReadOnlyList<BotGameResult> Games, IReadOnlyList<Finding> Findings, IReadOnlyList<JudgeReport> Reports)> RunAsync(int games, int seed, bool reset, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "bot-game.yaml");
        if (!File.Exists(path))
        {
            throw new CustosException("1対局のシナリオがありません：scenarios/bot-game.yaml");
        }

        if (reset)
        {
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        }

        var scenario = Scenario.Load(path);
        var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);

        // 同じ実行の中で何度呼んでも、結果のファイルがぶつからないようにする（結果は上書きしない決まり）
        var prefix = "bot/" + Interlocked.Increment(ref _invocations).ToString("D2", CultureInfo.InvariantCulture) + "-";
        var results = new List<BotGameResult>();
        var findings = new List<Finding>();
        for (var game = 1; game <= games; game++)
        {
            var gameSeed = seed + game - 1;
            var runner = new ScenarioRunner(_lab, _run, _log);
            runner.Variables["seed"] = JsonValue.Create(gameSeed);
            runner.Variables["game"] = JsonValue.Create(game);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await runner.RunAsync(scenario, cancellationToken).ConfigureAwait(false);
            var bot = result.Steps.LastOrDefault(s => s.Kind == "bot")?.Data as JsonObject;
            var stop = bot?["stop_reason"]?.GetValue<string>() ?? "no_bot_step";
            var moves = bot?["moves"]?.GetValue<int>() ?? 0;
            var gameResult = new BotGameResult(game, gameSeed, result.Ok, result.Error, stop, moves, Math.Round(watch.Elapsed.TotalSeconds, 1));
            results.Add(gameResult);
            _run.AppendJsonLine("bot-games.jsonl", gameResult);
            var gameFile = $"{prefix}game-{game.ToString("D3", CultureInfo.InvariantCulture)}.json";
            if (bot != null)
            {
                _run.WriteJson(gameFile, bot);
            }

            _log($"対局 {game}/{games}（シード {gameSeed}）：{moves} 手、{stop}{(result.Ok ? string.Empty : "、失敗：" + result.Error)}");
            if (stop == "stalled")
            {
                findings.Add(Finding.Create("bot_play", "stalled", Severity.High, 1, "game_stall", $"対局が止まりました（シード {gameSeed}、{moves} 手目の後、誰にも選択が来ないまま終局しない。第25.4節の詰み）", null, [new Evidence("ボットの記録", gameFile)], "stalled"));
            }
            else if (bot?["trace"] is JsonArray trace && trace.OfType<JsonObject>().Any(t => t["stuck"]?.GetValue<bool>() == true))
            {
                findings.Add(Finding.Create("bot_play", "empty_choices", Severity.High, 1, "game_stall", $"選択を求められたのに、選択肢が1つもありませんでした（シード {gameSeed}。第25.4節の詰み）", null, [new Evidence("ボットの記録", gameFile)], "empty_choices"));
            }
            else if (!result.Ok)
            {
                findings.Add(Finding.Create("bot_play", "game_failed", Severity.Medium, 1, "progress", $"ボットの対局が最後まで進みませんでした（シード {gameSeed}、{stop}）：{result.Error}", null, [], "game_failed:" + stop));
            }
        }

        await _lab.SettleAsync(TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var context = new JudgeContext(_lab, _run, baseline);
        var reports = await JudgeRunner.RunAsync(context, JudgeRunner.Stage1(), cancellationToken).ConfigureAwait(false);
        findings.AddRange(reports.SelectMany(r => r.Findings));
        _run.WriteText(prefix + "judges.txt", JudgeRunner.Summarize(reports));
        return (results, findings, reports);
    }
}

/// <summary>
/// 機械判定で見つけたものを、バグの登録簿に入れる（設計書 第20.7節）。段階1の判定は確定なので、そのまま登録する。
/// 同じ指紋のものは二重に登録しない（登録簿の link に指紋を残す）。
/// </summary>
public static class FindingRegistrar
{
    /// <summary>登録する。返り値は（指紋、バグの ID、新規か）。</summary>
    public static IReadOnlyList<(string Fingerprint, string BugId, bool Created)> Register(Registry.Bugs.BugRegistryAdmin registry, IEnumerable<Finding> findings, string finder)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(findings);
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bug in registry.List())
        {
            foreach (var link in registry.Links(bug.Id).Where(l => l.Kind == "fingerprint"))
            {
                known[link.Reference] = bug.Id;
            }
        }

        var result = new List<(string, string, bool)>();
        foreach (var (finding, count) in Findings.Group(findings.Where(f => f.Stage == 1 && f.Category != "coverage")))
        {
            if (known.TryGetValue(finding.Fingerprint, out var existing))
            {
                result.Add((finding.Fingerprint, existing, false));
                continue;
            }

            var title = finding.Summary.Replace('\n', ' ');
            if (title.Length > 200)
            {
                title = title[..200] + "…";
            }

            var id = registry.Add(new Registry.Bugs.NewBug(
                $"loop:{finder}",
                "loop",
                title,
                Severity: finding.Severity.ToString().ToLowerInvariant(),
                Category: finding.Category,
                Detail: $"判定器 {finding.Judge}、規則 {finding.Rule}、{count} 回検出。{finding.Detail}\n証拠：{string.Join("、", finding.Evidence.Select(e => $"{e.Description}（{e.Path}）"))}",
                Source: "verification_loop"));
            registry.Link(id, "fingerprint", finding.Fingerprint);
            known[finding.Fingerprint] = id;
            result.Add((finding.Fingerprint, id, true));
        }

        return result;
    }
}
