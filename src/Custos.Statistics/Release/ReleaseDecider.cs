using System.Globalization;
using Custos.Core.Config;
using Custos.Statistics.Sampling;

namespace Custos.Statistics.Release;

/// <summary>リリース判定の結論。</summary>
public enum ReleaseVerdict
{
    /// <summary>リリースしてよい。</summary>
    Release,

    /// <summary>まだリリースしない。ループを続ける。</summary>
    Continue,

    /// <summary>リリース判定を出せない（検証環境の側に問題がある）。</summary>
    Blocked,
}

/// <summary>理由の重さ。</summary>
public enum ReasonKind
{
    /// <summary>参考。判定を変えない。</summary>
    Info,

    /// <summary>リリースを止める（ループを続ける）。</summary>
    Continue,

    /// <summary>判定そのものを出せない。</summary>
    Block,
}

/// <summary>判定の理由。</summary>
/// <param name="Code">機械が読む理由の名前。</param>
/// <param name="Message">人が読む説明。</param>
/// <param name="Kind">重さ。</param>
public sealed record ReleaseReason(string Code, string Message, ReasonKind Kind);

/// <summary>未解決の発見（重大度ごと）。</summary>
/// <param name="Severity">重大度。</param>
/// <param name="Security">セキュリティの発見か（第3.3節：重大度ごとにゼロを目指す）。</param>
/// <param name="Count">件数。</param>
public sealed record OpenFindings(Severity Severity, bool Security, int Count);

/// <summary>種類ごとの誤り率の上限と目標。</summary>
/// <param name="Category">種類。</param>
/// <param name="AchievedUpper">抜き取り検証で言えた誤り率の上限（まだなければ null）。</param>
/// <param name="Target">目標の ε（ε を使わない種類は null）。</param>
public sealed record CategoryBound(string Category, double? AchievedUpper, double? Target);

/// <summary>残存バグの推定（重大度ごと）。</summary>
/// <param name="Severity">重大度。</param>
/// <param name="UpperBound">残りの最大値。</param>
/// <param name="Estimable">推定できたか（推定不能な層がある場合は false）。</param>
public sealed record ResidualEstimate(Severity Severity, double? UpperBound, bool Estimable);

/// <summary>性能の判定の状態。</summary>
public enum PerformanceState
{
    /// <summary>性能は検証しない。</summary>
    NotApplicable,

    /// <summary>合格。</summary>
    Pass,

    /// <summary>SLO違反。</summary>
    Fail,

    /// <summary>ビルド間の比較で劣化あり。</summary>
    Regression,

    /// <summary>計測が無効（測り直す）。</summary>
    Invalid,

    /// <summary>3回続けて無効。環境の問題。</summary>
    EnvironmentProblem,
}

/// <summary>リリース判定の入力。</summary>
/// <param name="RequiredConformancePassed">適合テストの必須項目がすべて合格したか（隔離の項目を含む）。</param>
/// <param name="Maturity">成熟度のレベルと区分（適合テストの結果から自動で決めたもの）。</param>
/// <param name="RequiredMaturity">必要な成熟度。</param>
/// <param name="Criticality">アプリの重要度。</param>
/// <param name="CriticalMachineChecksPassed">致命的な項目が、機械判定ですべて合格しているか（第26.9節）。</param>
/// <param name="CanaryDetectionRate">累積したカナリアの検出率（運用していなければ null）。</param>
/// <param name="CanaryThreshold">カナリアの検出率の閾値。</param>
/// <param name="Findings">未解決の発見。</param>
/// <param name="Categories">種類ごとの誤り率の上限と目標。</param>
/// <param name="Residuals">残存バグの推定。</param>
/// <param name="LoopCost">ループ1回の費用。</param>
/// <param name="FixLoopCost">修正ループ1回の費用（損失の単位。第26.2節）。</param>
/// <param name="ExpectedFoundNextLoop">あと1回ループを回したときに見つかる数の期待値（Goel-Okumoto。なければ null）。</param>
/// <param name="Performance">性能の判定の状態。</param>
/// <param name="PerformanceRequired">性能の判定をリリースの条件にするか。</param>
/// <param name="UnknownMainlineCommits">本流に、把握していないコミットがあるか（第20.9節）。</param>
/// <param name="VisibleFailureRate">見える判定（修正ループで使う判定）での失敗率。</param>
/// <param name="HiddenFailureRate">隠しシナリオでの失敗率（第12.6節）。</param>
public sealed record ReleaseInput(
    bool RequiredConformancePassed,
    MaturityLevel Maturity,
    MaturityLevel RequiredMaturity,
    Criticality Criticality,
    bool CriticalMachineChecksPassed,
    double? CanaryDetectionRate,
    double CanaryThreshold,
    IReadOnlyList<OpenFindings> Findings,
    IReadOnlyList<CategoryBound> Categories,
    IReadOnlyList<ResidualEstimate> Residuals,
    double? LoopCost,
    double? FixLoopCost,
    double? ExpectedFoundNextLoop,
    PerformanceState Performance,
    bool PerformanceRequired,
    bool UnknownMainlineCommits,
    double? VisibleFailureRate,
    double? HiddenFailureRate);

/// <summary>リリース判定の結果。</summary>
/// <param name="Verdict">結論。</param>
/// <param name="Reasons">理由。</param>
/// <param name="UsedExpectedLoss">期待損失による判断を使ったか。</param>
/// <param name="ResidualExpectedLoss">残存バグ全体の期待損失（信頼上限で計算）。</param>
/// <param name="ExpectedLossReductionNextLoop">ループ1回で減らせる期待損失。</param>
public sealed record ReleaseDecision(
    ReleaseVerdict Verdict,
    IReadOnlyList<ReleaseReason> Reasons,
    bool UsedExpectedLoss,
    double? ResidualExpectedLoss,
    double? ExpectedLossReductionNextLoop);

/// <summary>
/// リリース判定（停止規則）。第10章、第15.6節、第17.5節、第26.9節、第3.3節、第12.5節、第20.9節。
/// - 判定を出せない（Blocked）：適合テストの必須項目の失敗（レベル0）、本流の把握していないコミット、カナリアの検出率が閾値未満、性能の環境の問題（性能を条件にする場合）。
/// - 続ける（Continue）：成熟度が足りない、致命的やセキュリティの未解決、ε の目標に届かない、性能の不合格、隠しシナリオでの合わせ込みの兆候、期待損失がループの費用を上回る。
/// - 期待損失による判断は、検出率が実バグで校正されている 3c（重要度が低ければ 3b）のときだけ使う。
/// </summary>
public static class ReleaseDecider
{
    /// <summary>判定する。</summary>
    public static ReleaseDecision Decide(ReleaseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var reasons = new List<ReleaseReason>();
        var c = CultureInfo.InvariantCulture;

        // 判定そのものを出せない条件
        if (!input.RequiredConformancePassed || input.Maturity.Level == 0)
        {
            reasons.Add(new ReleaseReason("conformance_required_failed", "適合テストの必須項目に不合格がある（成熟度レベル0）。この環境ではリリース判定を出さない", ReasonKind.Block));
        }

        if (input.UnknownMainlineCommits)
        {
            reasons.Add(new ReleaseReason("unknown_mainline_commits", "本流に把握していないコミット（署名がない、取り込みの記録にない）がある。解消されるまでリリース判定を出さない", ReasonKind.Block));
        }

        if (input.CanaryDetectionRate is { } canary)
        {
            if (canary < input.CanaryThreshold)
            {
                reasons.Add(new ReleaseReason(
                    "canary_below_threshold",
                    string.Create(c, $"カナリアの検出率 {canary:P0} が閾値 {input.CanaryThreshold:P0} を下回る。「バグなし」のレポートに意味があると言えない"),
                    ReasonKind.Block));
            }
        }
        else if (input.Maturity.Level >= 2)
        {
            reasons.Add(new ReleaseReason("canary_not_running", "カナリアの検出率がまだない（毎ループのカナリアを運用していない）", ReasonKind.Info));
        }

        if (input.Performance == PerformanceState.EnvironmentProblem && input.PerformanceRequired)
        {
            reasons.Add(new ReleaseReason("performance_environment_problem", "性能の計測が3回続けて無効。環境の問題として扱い、性能についてのリリース判定を出さない", ReasonKind.Block));
        }

        // リリースを止める条件
        if (!input.Maturity.Satisfies(input.RequiredMaturity))
        {
            reasons.Add(new ReleaseReason("maturity_below_required", $"成熟度 {input.Maturity} が、必要なレベル {input.RequiredMaturity} に届かない", ReasonKind.Continue));
        }

        if (!input.CriticalMachineChecksPassed)
        {
            reasons.Add(new ReleaseReason("critical_machine_checks_failed", "致命的な項目の機械判定に、合格していないものがある", ReasonKind.Continue));
        }

        foreach (var group in input.Findings.Where(f => f.Count > 0))
        {
            if (group.Security)
            {
                reasons.Add(new ReleaseReason("open_security", string.Create(c, $"未解決のセキュリティの発見が {group.Count} 件（重大度 {group.Severity}）。セキュリティは重大度ごとにゼロを目指す"), ReasonKind.Continue));
            }
            else if (group.Severity == Severity.Critical)
            {
                reasons.Add(new ReleaseReason("open_critical", string.Create(c, $"未解決の致命的な発見が {group.Count} 件"), ReasonKind.Continue));
            }
        }

        foreach (var category in input.Categories)
        {
            if (category.Target is not { } target)
            {
                continue;
            }

            if (category.AchievedUpper is not { } achieved)
            {
                reasons.Add(new ReleaseReason("epsilon_not_measured", $"種類 {category.Category} の誤り率の上限がまだ出ていない", ReasonKind.Continue));
            }
            else if (achieved > target)
            {
                reasons.Add(new ReleaseReason(
                    "epsilon_above_target",
                    string.Create(c, $"種類 {category.Category} の誤り率の上限 {achieved:P3} が目標 {target:P3} を上回る"),
                    ReasonKind.Continue));
            }
        }

        switch (input.Performance)
        {
            case PerformanceState.Fail:
                reasons.Add(new ReleaseReason("performance_slo_violation", "性能のSLO違反がある", input.PerformanceRequired ? ReasonKind.Continue : ReasonKind.Info));
                break;
            case PerformanceState.Regression:
                reasons.Add(new ReleaseReason("performance_regression", "ビルド間の比較で性能の劣化がある", input.PerformanceRequired ? ReasonKind.Continue : ReasonKind.Info));
                break;
            case PerformanceState.Invalid:
                reasons.Add(new ReleaseReason("performance_invalid", "性能の計測が無効。測り直す", input.PerformanceRequired ? ReasonKind.Continue : ReasonKind.Info));
                break;
            case PerformanceState.EnvironmentProblem when !input.PerformanceRequired:
                reasons.Add(new ReleaseReason("performance_environment_problem", "性能の計測が3回続けて無効（性能はリリースの条件にしていない）", ReasonKind.Info));
                break;
        }

        if (input.HiddenFailureRate is { } hidden && input.VisibleFailureRate is { } visible
            && hidden - visible >= 0.05 && hidden > 2 * visible)
        {
            reasons.Add(new ReleaseReason(
                "hidden_scenario_gap",
                string.Create(c, $"隠しシナリオでの失敗率 {hidden:P1} が、見える判定での失敗率 {visible:P1} より明らかに高い。見える判定への合わせ込みの兆候"),
                ReasonKind.Continue));
        }

        // 期待損失による判断
        var usesExpectedLoss = input.Maturity.Level == 3
            && (input.Maturity.Sub == 'c' || (input.Maturity.Sub == 'b' && input.Criticality == Criticality.Low));
        double? residualLoss = null;
        double? reduction = null;
        if (usesExpectedLoss)
        {
            var inestimable = input.Residuals.Where(r => !r.Estimable || r.UpperBound is null).ToList();
            if (inestimable.Count > 0)
            {
                reasons.Add(new ReleaseReason("residual_inestimable", "残存バグを推定できない重大度がある（推定不能な層など）。期待損失による判断ができない", ReasonKind.Continue));
            }
            else if (input.FixLoopCost is not { } fixCost || input.LoopCost is not { } loopCost)
            {
                reasons.Add(new ReleaseReason("loop_cost_unknown", "ループの費用が分からない。期待損失による判断ができない", ReasonKind.Continue));
            }
            else
            {
                double totalBugs = 0;
                double loss = 0;
                foreach (var residual in input.Residuals)
                {
                    if (LossModel.LossPerBug(residual.Severity, fixCost) is { } perBug && residual.UpperBound is { } upper)
                    {
                        loss += upper * perBug;
                        totalBugs += upper;
                    }
                }

                residualLoss = loss;
                reduction = input.ExpectedFoundNextLoop is { } expectedFound && totalBugs > 0
                    ? Math.Min(loss, expectedFound * (loss / totalBugs))
                    : loss;
                if (loopCost > reduction)
                {
                    reasons.Add(new ReleaseReason(
                        "expected_loss_below_loop_cost",
                        string.Create(c, $"ループ1回の費用 {loopCost:G4} が、ループ1回で減らせる期待損失 {reduction:G4} を上回る"),
                        ReasonKind.Info));
                }
                else
                {
                    reasons.Add(new ReleaseReason(
                        "expected_loss_exceeds_loop_cost",
                        string.Create(c, $"ループ1回で減らせる期待損失 {reduction:G4} が、ループ1回の費用 {loopCost:G4} 以上。ループを続ける"),
                        ReasonKind.Continue));
                }
            }
        }
        else
        {
            reasons.Add(new ReleaseReason(
                "expected_loss_not_used",
                $"成熟度 {input.Maturity} では期待損失による判断を使わない（3c、重要度が低ければ 3b が必要）。致命的ゼロと ε の目標で判定する",
                ReasonKind.Info));
        }

        var verdict = reasons.Any(r => r.Kind == ReasonKind.Block)
            ? ReleaseVerdict.Blocked
            : reasons.Any(r => r.Kind == ReasonKind.Continue) ? ReleaseVerdict.Continue : ReleaseVerdict.Release;
        return new ReleaseDecision(verdict, reasons, usesExpectedLoss, residualLoss, reduction);
    }
}
