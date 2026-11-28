using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Results;
using Custos.Core.Util;
using Custos.Statistics;
using Custos.Web.SocketIO.Agents;
using Custos.Web.SocketIO.Judges;

namespace Custos.Web.SocketIO.Calibration;

/// <summary>検証役の判定（bug / no_bug / unknown）と理由。</summary>
public sealed record BlindVerdict(string Verdict, string Detail);

/// <summary>
/// 結論を伏せた判定し直し（抜き取り検証の検証役。設計書 第21章）。証拠（通信の記録のセッションと範囲）だけを見て、誤りがあるかを判定する。
/// 代役（scripted）は、通信の記録に対する機械判定（送信の不変条件、送ってよい項目）で判定し直す。探索と同じ機械判定なので独立性は低く、
/// 探索が見落としたものに対する感度は低く出る（それを測るのが目的）。Claude Code の検証役は API キーがあるときだけ使える。
/// </summary>
public static class BlindVerifier
{
    /// <summary>
    /// 判定する。ignore に入っている所見（キー）は数えない（感度の測定で、パッチなしのビルドの証拠でも出る所見を除くため）。
    /// </summary>
    public static async Task<BlindVerdict> JudgeAsync(Lab.Lab lab, Run run, string agent, string recordsSession, long from, long? to, CancellationToken cancellationToken, IReadOnlySet<string>? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(lab);
        if (agent == "claude-code")
        {
            return new ClaudeCodeLauncher(lab).Unavailable() is { } reason
                ? new BlindVerdict("unknown", "検証役（Claude Code）を起動できない：" + reason)
                : new BlindVerdict("unknown", "Claude Code の検証役による判定し直しは、まだ代役と同じ証拠の形に対応していない");
        }

        if (await FindingsAsync(lab, run, recordsSession, from, to, cancellationToken).ConfigureAwait(false) is not { } findings)
        {
            return new BlindVerdict("unknown", "証拠の記録が見つからない：" + recordsSession);
        }

        return findings.FirstOrDefault(f => ignore == null || !ignore.Contains(f.Key)) is { Key: not null } hit
            ? new BlindVerdict("bug", hit.Summary)
            : new BlindVerdict("no_bug", ignore is { Count: > 0 } && findings.Count > 0 ? "パッチなしの証拠でも出る所見のほかに、問題は見つからなかった" : "証拠の記録を判定し直して、問題は見つからなかった");
    }

    /// <summary>代役の検証役が証拠から見つける所見（キーと説明）。記録がなければ null。</summary>
    public static async Task<IReadOnlyList<(string Key, string Summary)>?> FindingsAsync(Lab.Lab lab, Run run, string recordsSession, long from, long? to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var sessionDir = Path.Combine(lab.Workspace.RecordsDir, recordsSession);
        if (!Directory.Exists(sessionDir))
        {
            return null;
        }

        var found = new List<(string Key, string Summary)>();
        var store = new Records.RecordStore(sessionDir);
        using (var db = await RecordsDatabase.BuildAsync(lab, store, from, to, false, cancellationToken).ConfigureAwait(false))
        {
            foreach (var invariant in (lab.Config.Invariants?.Invariants ?? []).Where(i => i.Kind == InvariantKind.SentSql && i.Query != null))
            {
                try
                {
                    if (db.Query(invariant.Query ?? string.Empty).Rows.Count > 0)
                    {
                        found.Add(("invariant:" + invariant.Id, $"証拠の記録で不変条件 {invariant.Id} が破れている"));
                    }
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                }
            }
        }

        // 送ってよい項目（記録のセッションの中だけを見る）
        var baseline = await JudgeContext.CaptureAsync(lab, cancellationToken).ConfigureAwait(false);
        var schema = await new SentSchemaJudge(store).JudgeAsync(new JudgeContext(lab, run, baseline), cancellationToken).ConfigureAwait(false);
        found.AddRange(schema.Findings.Where(f => f.Category != "coverage").Select(f => ("schema:" + f.Fingerprint, f.Summary)));
        return found;
    }
}

/// <summary>1回の測定の結果。</summary>
public sealed record VerifierMeasurement(
    string Calibration,
    DateTimeOffset At,
    string Agent,
    int AllTotal,
    int AllFlagged,
    int MissedTotal,
    int MissedFlagged,
    IReadOnlyList<JsonObject> Rows);

/// <summary>
/// 検証役の感度の測定（設計書 第21.2節）。校正で探索が見落とした校正用バグの証拠を、結論を伏せて検証役に見せ、何割を「誤り」と指摘できたかを測る。
/// すべての校正用バグの証拠で測った感度もあわせて出し、低い方（95%信頼区間の下限）を使う。測定値は校正のたびに累積する（data/calibration/verifier.jsonl）。
/// </summary>
public static class VerifierSensitivity
{
    /// <summary>感度の基準（第21.3節。これを下回る種類は、AI での検証をやめて人間の担当にする）。</summary>
    public const double Threshold = 0.7;

    private static string HistoryPath(Workspace workspace) => Path.Combine(workspace.DataDir, "calibration", "verifier.jsonl");

    /// <summary>校正の結果（runs/&lt;ID&gt;-calibrate/calibration/&lt;seed&gt;.json の evidence）から測る。</summary>
    public static async Task<VerifierMeasurement> MeasureAsync(Lab.Lab lab, Run run, string calibrationDir, string agent, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(log);
        // 基準（パッチなし）の証拠でも出る所見は、校正用バグの指摘に数えない（検証役が何にでも「誤り」と言うと、感度が見かけだけ高くなる）
        var basePath = Path.Combine(calibrationDir, "calibration", "base.json");
        if (!File.Exists(basePath) || (JsonNode.Parse(await File.ReadAllTextAsync(basePath, cancellationToken).ConfigureAwait(false)) as JsonObject)?["evidence"] is not JsonObject baseEvidence
            || baseEvidence["session"]?.ToString() is not { } baseSession)
        {
            throw new CustosException("この校正には基準（パッチなし）の証拠の場所がありません（この版より前の校正）。calibrate をやり直してください");
        }

        var ignore = (await BlindVerifier.FindingsAsync(lab, run, baseSession, baseEvidence["from"]?.GetValue<long>() ?? 0, baseEvidence["to"]?.GetValue<long?>(), cancellationToken).ConfigureAwait(false) ?? [])
            .Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        log($"基準（パッチなし）の証拠で出る所見：{ignore.Count} 種類（校正用バグの指摘に数えない）");
        var rows = new List<JsonObject>();
        int allTotal = 0, allFlagged = 0, missedTotal = 0, missedFlagged = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(calibrationDir, "calibration"), "seed-*.json").Order(StringComparer.Ordinal))
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)) as JsonObject ?? [];
            var seed = node["seed"]?.ToString() ?? Path.GetFileNameWithoutExtension(file);
            if (node["evidence"] is not JsonObject evidence || evidence["session"]?.ToString() is not { } session)
            {
                rows.Add(new JsonObject { ["seed"] = seed, ["verdict"] = "unknown", ["detail"] = "証拠の記録の場所がない（この版より前の校正）" });
                continue;
            }

            var detected = node["detected"]?.GetValue<bool>() == true;
            var verdict = await BlindVerifier.JudgeAsync(lab, run, agent, session, evidence["from"]?.GetValue<long>() ?? 0, evidence["to"]?.GetValue<long?>(), cancellationToken, ignore).ConfigureAwait(false);
            rows.Add(new JsonObject { ["seed"] = seed, ["explore_detected"] = detected, ["verdict"] = verdict.Verdict, ["detail"] = verdict.Detail });
            log($"{seed}（探索で{(detected ? "見つかった" : "見落とした")}）：検証役 {verdict.Verdict}（{verdict.Detail}）");
            if (verdict.Verdict == "unknown")
            {
                continue;
            }

            allTotal++;
            var flagged = verdict.Verdict == "bug";
            allFlagged += flagged ? 1 : 0;
            if (!detected)
            {
                missedTotal++;
                missedFlagged += flagged ? 1 : 0;
            }
        }

        var measurement = new VerifierMeasurement(run.Id, DateTimeOffset.UtcNow, agent, allTotal, allFlagged, missedTotal, missedFlagged, rows);
        if (allTotal > 0)
        {
            var path = HistoryPath(lab.Workspace);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? lab.Workspace.DataDir);
            await File.AppendAllTextAsync(path, Json.SerializeCompact(new { calibration = Path.GetFileName(calibrationDir), run = run.Id, at = measurement.At, agent, all_total = allTotal, all_flagged = allFlagged, missed_total = missedTotal, missed_flagged = missedFlagged }) + "\n", cancellationToken).ConfigureAwait(false);
        }

        return measurement;
    }

    /// <summary>
    /// 累積した感度（その検証役の、すべての測定の合計）。見落としたものと、すべてのものの、それぞれの 95% 信頼区間の下限のうち低い方を使う。
    /// 見落としたものが1件もなければ、すべてのもので出す。測定がなければ null。
    /// </summary>
    public static (double Used, double? Missed, double? All, int MissedTotal, int AllTotal)? Cumulative(Workspace workspace, string agent)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var path = HistoryPath(workspace);
        if (!File.Exists(path))
        {
            return null;
        }

        int allTotal = 0, allFlagged = 0, missedTotal = 0, missedFlagged = 0;
        // excluded：後から測り方の誤りが分かった測定（note に理由）。数えない
        foreach (var node in File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)).OfType<JsonNode>()
            .Where(n => n["agent"]?.ToString() == agent && n["excluded"]?.GetValue<bool>() != true))
        {
            allTotal += node["all_total"]?.GetValue<int>() ?? 0;
            allFlagged += node["all_flagged"]?.GetValue<int>() ?? 0;
            missedTotal += node["missed_total"]?.GetValue<int>() ?? 0;
            missedFlagged += node["missed_flagged"]?.GetValue<int>() ?? 0;
        }

        if (allTotal == 0)
        {
            return null;
        }

        double? all = Proportions.ClopperPearson(allFlagged, allTotal, 0.95).Lower;
        double? missed = missedTotal == 0 ? null : Proportions.ClopperPearson(missedFlagged, missedTotal, 0.95).Lower;
        var used = missed is { } m ? Math.Min(m, all.Value) : all.Value;
        return (used, missed, all, missedTotal, allTotal);
    }

    /// <summary>表示用の文字列。</summary>
    public static string Describe(VerifierMeasurement m)
    {
        ArgumentNullException.ThrowIfNull(m);
        static string Rate(int hit, int total) => total == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{hit}/{total}（{(double)hit / total:P0}）");
        return $"検証役（{m.Agent}）：探索が見落とした校正用バグの証拠で {Rate(m.MissedFlagged, m.MissedTotal)}、すべての校正用バグの証拠で {Rate(m.AllFlagged, m.AllTotal)} を「誤り」と指摘した";
    }
}
