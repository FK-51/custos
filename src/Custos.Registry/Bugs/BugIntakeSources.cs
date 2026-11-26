using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Custos.Core.Util;

namespace Custos.Registry.Bugs;

/// <summary>検証ループで見つけた不具合（判定結果、レポート、再生手順、修正コミットを結ぶ）。</summary>
/// <param name="Explorer">見つけた探索AIの名前と版（found_by は loop:&lt;Explorer&gt; になる）。</param>
/// <param name="Title">一行の説明。</param>
/// <param name="JudgmentId">判定結果の ID。</param>
/// <param name="ReportId">レポートの ID。</param>
/// <param name="ReplayRef">再生手順の場所。</param>
/// <param name="Severity">重大度。</param>
/// <param name="Category">種類。</param>
/// <param name="FixCommit">修正コミット（分かっていれば）。</param>
/// <param name="Detail">詳しい説明。</param>
public sealed record LoopFinding(
    string Explorer,
    string Title,
    string? JudgmentId = null,
    string? ReportId = null,
    string? ReplayRef = null,
    string? Severity = null,
    string? Category = null,
    string? FixCommit = null,
    string? Detail = null);

/// <summary>本番・人間からの報告（課題管理ツールなどから JSON で取り込む）。</summary>
public sealed record ExternalReport(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("found_by")] string FoundBy,
    [property: JsonPropertyName("found_stage")] string FoundStage,
    [property: JsonPropertyName("severity")] string? Severity = null,
    [property: JsonPropertyName("category")] string? Category = null,
    [property: JsonPropertyName("fix_commit")] string? FixCommit = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("external_ref")] string? ExternalRef = null,
    [property: JsonPropertyName("found_at")] DateTimeOffset? FoundAt = null);

/// <summary>実装中のテスト失敗（開発AIのテストが挙動の誤りで失敗し、その後通ったときの前後の差分）。</summary>
/// <param name="TestName">失敗したテストの名前。</param>
/// <param name="FailureKind">失敗の種類（assertion、exception、timeout、type_error、compile_error など）。</param>
/// <param name="Diff">失敗してから通るまでの差分。</param>
/// <param name="Output">失敗したときの出力。</param>
public sealed record TestFailureEvent(string TestName, string FailureKind, string? Diff = null, string? Output = null);

/// <summary>
/// 記録の入口（設計書 第20.2節）。「実装中に登録する」というルールに頼らず、フレームワークの動作の副産物として記録する。
/// </summary>
public static class BugIntakeSources
{
    /// <summary>自明な失敗として除外する種類（型エラー、コンパイルエラーなど）。</summary>
    public static readonly IReadOnlyList<string> TrivialFailureKinds = ["type_error", "compile_error", "syntax_error", "lint_error", "build_error"];

    /// <summary>検証ループで見つけた不具合を記録し、判定結果・レポート・再生手順・修正コミットと結ぶ。</summary>
    public static string RecordLoopFinding(BugRegistryAdmin registry, LoopFinding finding)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(finding);
        if (string.IsNullOrWhiteSpace(finding.Explorer))
        {
            throw new CustosException("探索AIの名前と版がありません（found_by に必須。第20.8節）");
        }

        var id = registry.Add(new NewBug(
            "loop:" + finding.Explorer.Trim(),
            "loop",
            finding.Title,
            Severity: finding.Severity,
            Category: finding.Category,
            Detail: finding.Detail,
            Source: "verification_loop"));
        if (finding.JudgmentId != null)
        {
            registry.Link(id, "judgment", finding.JudgmentId);
        }

        if (finding.ReportId != null)
        {
            registry.Link(id, "report", finding.ReportId);
        }

        if (finding.ReplayRef != null)
        {
            registry.Link(id, "replay", finding.ReplayRef);
        }

        if (finding.FixCommit != null)
        {
            registry.Link(id, "fix_commit", finding.FixCommit);
            registry.Update(id, new BugUpdate(FixedIn: finding.FixCommit), "verification_loop", "修正コミット");
        }

        return id;
    }

    /// <summary>本番・人間からの報告（JSON の配列）を取り込み、振った ID を返す。</summary>
    public static IReadOnlyList<string> ImportReports(BugRegistryAdmin registry, string json)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(json);
        List<ExternalReport>? reports;
        try
        {
            reports = JsonSerializer.Deserialize<List<ExternalReport>>(json);
        }
        catch (JsonException e)
        {
            throw new CustosException("報告の JSON を読めません：" + e.Message, e);
        }

        if (reports == null)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var report in reports)
        {
            if (report.FoundBy is not ("human" or "production"))
            {
                throw new CustosException($"報告の found_by は human か production にしてください：{report.FoundBy}");
            }

            if (report.FoundStage is not ("staging" or "production" or "implementation"))
            {
                throw new CustosException($"報告の found_stage は staging、production、implementation のどれかにしてください：{report.FoundStage}");
            }

            var id = registry.Add(new NewBug(
                report.FoundBy,
                report.FoundStage,
                report.Title,
                Severity: report.Severity,
                Category: report.Category,
                Detail: report.Detail,
                Source: report.ExternalRef ?? "external_report",
                FoundAt: report.FoundAt));
            if (report.ExternalRef != null)
            {
                registry.Link(id, "external", report.ExternalRef);
            }

            if (report.FixCommit != null)
            {
                registry.Link(id, "fix_commit", report.FixCommit);
                registry.Update(id, new BugUpdate(FixedIn: report.FixCommit), "import", "修正コミット");
            }

            ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// 実装中のテスト失敗を記録する（任意の入口）。型エラーやコンパイルエラーのような自明なものは除外し、null を返す。
    /// </summary>
    public static string? RecordTestFailure(IBugIntake intake, TestFailureEvent failure)
    {
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(failure);
        if (TrivialFailureKinds.Contains(failure.FailureKind, StringComparer.Ordinal))
        {
            return null;
        }

        var detail = new StringBuilder();
        detail.Append("失敗したテスト：").Append(failure.TestName).Append('\n');
        detail.Append("失敗の種類：").Append(failure.FailureKind).Append('\n');
        if (failure.Output != null)
        {
            detail.Append("\n失敗したときの出力：\n").Append(failure.Output).Append('\n');
        }

        if (failure.Diff != null)
        {
            detail.Append("\n失敗してから通るまでの差分：\n").Append(failure.Diff).Append('\n');
        }

        return intake.Add(new NewBug(
            "dev_agent",
            "implementation",
            $"実装中のテスト失敗：{failure.TestName}",
            Detail: detail.ToString(),
            Source: "test_failure"));
    }
}
