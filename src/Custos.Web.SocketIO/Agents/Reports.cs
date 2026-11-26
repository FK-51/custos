using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Custos.Core.Util;
using Custos.SocketIO.Wire;
using Custos.Web.SocketIO.Judges;

namespace Custos.Web.SocketIO.Agents;

/// <summary>レポートの「試した操作」1件。</summary>
public sealed record TrialAction(string User, string Event, JsonArray Args, string? Note = null);

/// <summary>レポートの証拠1件（ログ、スクリーンショット、通信記録）。path は結果のディレクトリからの相対パス。</summary>
public sealed record TrialEvidence(string Kind, string Path, string Description);

/// <summary>レポートのコスト（AIの利用料、時間）。</summary>
public sealed record TrialCost(double Usd, double Seconds, long? InputTokens, long? OutputTokens);

/// <summary>
/// 探索のレポートの標準形式（設計書 第12.4節）。自由文は notes だけ。
/// records_session、records_from、records_to は、この試行の通信の記録の範囲（再生と、手順の照合に使う）。
/// </summary>
public sealed record TrialReport(
    string TrialId,
    string TargetId,
    string Role,
    string Agent,
    string? Model,
    string Scenario,
    IReadOnlyList<TrialAction> Actions,
    string Result,
    IReadOnlyList<TrialEvidence> Evidence,
    string ReplayId,
    TrialCost Cost,
    string Notes,
    IReadOnlyDictionary<string, string> Users,
    string RecordsSession,
    long RecordsFrom,
    long RecordsTo)
{
    /// <summary>登録簿に入ったバグの ID（フレームワークが記入する）。</summary>
    [JsonPropertyName("bug_ids")]
    public IReadOnlyList<string> BugIds { get; init; } = [];
}

/// <summary>機械チェックの結果（第21.5節の1）。</summary>
public sealed record ReportCheck(string TrialId, bool Valid, IReadOnlyList<string> Problems, int StepsMatched, int StepsClaimed, bool? SituationReached, int EvidenceValid);

/// <summary>
/// レポートの機械チェック（設計書 第21.1節「手順の不一致」、第21.5節）。全件を自動で確認する。
/// - 形式：必須の項目、結果の値、確認対象が台帳にあるか
/// - 手順：書いてある操作が、通信の記録に実際にあるか（その利用者の接続から、同じイベントと値）
/// - 証拠：ファイルがあるか、スクリーンショットが真っ白でないか、通信の記録の範囲が空でないか
/// - 状況：状況の型に条件があれば、記録の範囲で本当にその状況に到達したか
/// - 再生ID：記録のセッションと範囲を指しているか
/// </summary>
public sealed class ReportChecker
{
    private readonly Lab.Lab _lab;
    private readonly LedgerDefinition _definition;
    private readonly Dictionary<string, LedgerEntry> _ledger;

    /// <summary>作る。</summary>
    public ReportChecker(Lab.Lab lab, LedgerDefinition definition, IReadOnlyList<LedgerEntry> ledger)
    {
        _lab = lab;
        _definition = definition;
        _ledger = ledger.ToDictionary(e => e.Id, StringComparer.Ordinal);
    }

    /// <summary>確かめる。runDir は証拠の相対パスの基準。</summary>
    public async Task<ReportCheck> CheckAsync(TrialReport report, string runDir, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(report.TrialId) || string.IsNullOrWhiteSpace(report.TargetId))
        {
            problems.Add("試行IDか確認対象のIDがない");
        }

        if (report.Result is not ("bug" or "no_bug"))
        {
            problems.Add($"結果が bug でも no_bug でもない：{report.Result}");
        }

        var free = report.TargetId.StartsWith("free:", StringComparison.Ordinal);
        _ledger.TryGetValue(report.TargetId, out var entry);
        if (!free && entry == null)
        {
            problems.Add($"確認対象が台帳にない：{report.TargetId}");
        }

        // 再生ID：記録のセッションと範囲
        var sessionDir = Path.Combine(_lab.Workspace.RecordsDir, report.RecordsSession);
        if (report.ReplayId != $"records:{report.RecordsSession}:{report.RecordsFrom.ToString(CultureInfo.InvariantCulture)}-{report.RecordsTo.ToString(CultureInfo.InvariantCulture)}" || !Directory.Exists(sessionDir))
        {
            problems.Add($"再生IDが記録を指していない：{report.ReplayId}");
        }

        // 手順：書いてある操作が通信の記録にあるか
        var records = Directory.Exists(sessionDir) ? new Records.RecordStore(sessionDir) : null;
        var matched = 0;
        if (report.Actions.Count == 0)
        {
            problems.Add("試した操作が書かれていない");
        }
        else if (records != null)
        {
            var socketToSids = records.SocketIds().GroupBy(p => p.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            var window = records.Read(report.RecordsFrom).Where(e => e.Seq <= report.RecordsTo && e.Kind == "eio" && e.Dir == "c2s" && e.Sio == "event").ToList();
            foreach (var action in report.Actions)
            {
                var sids = report.Users.TryGetValue(action.User, out var socketId) && socketToSids.TryGetValue(socketId, out var set) ? set : [];
                var wanted = JsJson.Stringify(action.Args);
                var found = window.Any(e => e.Sid != null && sids.Contains(e.Sid) && e.EventName == action.Event && JsJson.Stringify(e.Args ?? new JsonArray()) == wanted);
                if (found)
                {
                    matched++;
                }
            }

            if (matched < report.Actions.Count)
            {
                problems.Add($"手順の不一致：書いてある操作 {report.Actions.Count} 件のうち、記録にあるのは {matched} 件");
            }
        }

        // 証拠
        var evidenceValid = 0;
        foreach (var evidence in report.Evidence)
        {
            var path = Path.Combine(runDir, evidence.Path);
            if (!File.Exists(path))
            {
                problems.Add($"証拠のファイルがない：{evidence.Path}");
                continue;
            }

            if (evidence.Kind == "screenshot")
            {
                var stats = Png.Stats(Png.Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
                if (stats.LooksBlank)
                {
                    problems.Add($"スクリーンショットが真っ白：{evidence.Path}");
                    continue;
                }
            }

            evidenceValid++;
        }

        if (report.Evidence.Count == 0)
        {
            problems.Add("証拠がない");
        }

        // 状況：条件があれば、記録の範囲で到達したか
        bool? reached = null;
        if (entry != null && _definition.Situations.TryGetValue(entry.Situation, out var situation) && situation.ConditionSql != null)
        {
            using var db = await RecordsDatabase.BuildAsync(_lab, report.RecordsFrom, withApp: false, cancellationToken).ConfigureAwait(false);
            var condition = situation.ConditionSql
                .Replace("{from}", report.RecordsFrom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{to}", report.RecordsTo.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{target}", entry.Target.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal);
            try
            {
                reached = db.Query(condition).Rows.Count > 0;
            }
            catch (Microsoft.Data.Sqlite.SqliteException e)
            {
                problems.Add($"状況の条件を評価できない：{e.Message}");
            }

            if (reached == false)
            {
                problems.Add($"状況「{situation.Description}」に到達した記録がない");
            }
        }

        return new ReportCheck(report.TrialId, problems.Count == 0, problems, matched, report.Actions.Count, reached, evidenceValid);
    }
}
