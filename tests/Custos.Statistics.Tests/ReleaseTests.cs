using Custos.Core.Config;
using Custos.Statistics.Release;

namespace Custos.Statistics.Tests;

public class ReleaseTests
{
    private static ReleaseInput Base(string maturity = "2") => new(
        RequiredConformancePassed: true,
        Maturity: Level(maturity),
        RequiredMaturity: Level("2"),
        Criticality: Criticality.Low,
        CriticalMachineChecksPassed: true,
        CanaryDetectionRate: 1.0,
        CanaryThreshold: 0.8,
        Findings: [],
        Categories: [new CategoryBound("core_flow", 0.0008, 0.001), new CategoryBound("security", null, null)],
        Residuals: [new ResidualEstimate(Severity.High, 0.5, true), new ResidualEstimate(Severity.Low, 3, true)],
        LoopCost: 10,
        FixLoopCost: 1,
        ExpectedFoundNextLoop: null,
        Performance: PerformanceState.NotApplicable,
        PerformanceRequired: false,
        UnknownMainlineCommits: false,
        VisibleFailureRate: null,
        HiddenFailureRate: null);

    private static MaturityLevel Level(string text)
    {
        Assert.True(MaturityLevel.TryParse(text, out var level));
        return level;
    }

    [Fact]
    public void FirstReleaseAtLevelTwoWithZeroCriticalAndEpsilonTargets()
    {
        var decision = ReleaseDecider.Decide(Base());
        Assert.Equal(ReleaseVerdict.Release, decision.Verdict);
        Assert.False(decision.UsedExpectedLoss);
        Assert.Contains(decision.Reasons, r => r.Code == "expected_loss_not_used");
    }

    [Fact]
    public void RequiredConformanceFailureBlocksTheDecision()
    {
        var decision = ReleaseDecider.Decide(Base() with { RequiredConformancePassed = false });
        Assert.Equal(ReleaseVerdict.Blocked, decision.Verdict);
    }

    [Fact]
    public void CanaryBelowThresholdAndUnknownCommitsBlock()
    {
        Assert.Equal(ReleaseVerdict.Blocked, ReleaseDecider.Decide(Base() with { CanaryDetectionRate = 0.5 }).Verdict);
        Assert.Equal(ReleaseVerdict.Blocked, ReleaseDecider.Decide(Base() with { UnknownMainlineCommits = true }).Verdict);
        Assert.Equal(
            ReleaseVerdict.Blocked,
            ReleaseDecider.Decide(Base() with { Performance = PerformanceState.EnvironmentProblem, PerformanceRequired = true }).Verdict);
    }

    [Fact]
    public void SecurityAndCriticalFindingsMustBeZero()
    {
        var security = ReleaseDecider.Decide(Base() with { Findings = [new OpenFindings(Severity.Low, true, 1)] });
        Assert.Equal(ReleaseVerdict.Continue, security.Verdict);
        Assert.Contains(security.Reasons, r => r.Code == "open_security");

        var critical = ReleaseDecider.Decide(Base() with { Findings = [new OpenFindings(Severity.Critical, false, 1)] });
        Assert.Equal(ReleaseVerdict.Continue, critical.Verdict);

        var low = ReleaseDecider.Decide(Base() with { Findings = [new OpenFindings(Severity.Low, false, 4)] });
        Assert.Equal(ReleaseVerdict.Release, low.Verdict);

        Assert.Equal(ReleaseVerdict.Continue, ReleaseDecider.Decide(Base() with { CriticalMachineChecksPassed = false }).Verdict);
    }

    [Fact]
    public void EpsilonAboveTargetOrMissingContinues()
    {
        var above = ReleaseDecider.Decide(Base() with { Categories = [new CategoryBound("core_flow", 0.002, 0.001)] });
        Assert.Equal(ReleaseVerdict.Continue, above.Verdict);
        var missing = ReleaseDecider.Decide(Base() with { Categories = [new CategoryBound("core_flow", null, 0.001)] });
        Assert.Contains(missing.Reasons, r => r.Code == "epsilon_not_measured");
    }

    [Fact]
    public void MaturityBelowRequiredContinues()
    {
        var decision = ReleaseDecider.Decide(Base("1") with { RequiredMaturity = Level("2") });
        Assert.Equal(ReleaseVerdict.Continue, decision.Verdict);
    }

    [Fact]
    public void ExpectedLossRuleAppliesOnlyWhenCalibratedWithRealBugs()
    {
        // 3c：残存バグの期待損失 = 0.5×10 + 3×1 = 8（修正ループ1回を1とする）、ループ1回の費用 10 → リリース
        var calibrated = ReleaseDecider.Decide(Base("3c") with { RequiredMaturity = Level("3") });
        Assert.True(calibrated.UsedExpectedLoss);
        Assert.Equal(8, calibrated.ResidualExpectedLoss ?? 0, 9);
        Assert.Equal(ReleaseVerdict.Release, calibrated.Verdict);

        var costly = ReleaseDecider.Decide(Base("3c") with { RequiredMaturity = Level("3"), LoopCost = 5 });
        Assert.Equal(ReleaseVerdict.Continue, costly.Verdict);
        Assert.Contains(costly.Reasons, r => r.Code == "expected_loss_exceeds_loop_cost");

        // Goel-Okumoto の予測があれば、ループ1回で減る分だけを比べる
        var predicted = ReleaseDecider.Decide(Base("3c") with { RequiredMaturity = Level("3"), LoopCost = 5, ExpectedFoundNextLoop = 0.35 });
        Assert.Equal(0.35 * (8 / 3.5), predicted.ExpectedLossReductionNextLoop ?? 0, 9);
        Assert.Equal(ReleaseVerdict.Release, predicted.Verdict);

        // 3b は重要度が低いアプリだけ。3a は使わない
        Assert.True(ReleaseDecider.Decide(Base("3b") with { RequiredMaturity = Level("3") }).UsedExpectedLoss);
        Assert.False(ReleaseDecider.Decide(Base("3b") with { RequiredMaturity = Level("3"), Criticality = Criticality.High }).UsedExpectedLoss);
        Assert.False(ReleaseDecider.Decide(Base("3a") with { RequiredMaturity = Level("3") }).UsedExpectedLoss);

        var inestimable = ReleaseDecider.Decide(Base("3c") with
        {
            RequiredMaturity = Level("3"),
            Residuals = [new ResidualEstimate(Severity.High, null, false)],
        });
        Assert.Contains(inestimable.Reasons, r => r.Code == "residual_inestimable");
        Assert.Equal(ReleaseVerdict.Continue, inestimable.Verdict);
    }

    [Fact]
    public void HiddenScenarioGapIsTreatedAsOverfitting()
    {
        var decision = ReleaseDecider.Decide(Base() with { VisibleFailureRate = 0.01, HiddenFailureRate = 0.12 });
        Assert.Equal(ReleaseVerdict.Continue, decision.Verdict);
        Assert.Contains(decision.Reasons, r => r.Code == "hidden_scenario_gap");
        Assert.Equal(ReleaseVerdict.Release, ReleaseDecider.Decide(Base() with { VisibleFailureRate = 0.05, HiddenFailureRate = 0.06 }).Verdict);
    }

    [Fact]
    public void PerformanceFailureMattersOnlyWhenRequired()
    {
        Assert.Equal(ReleaseVerdict.Continue, ReleaseDecider.Decide(Base() with { Performance = PerformanceState.Regression, PerformanceRequired = true }).Verdict);
        Assert.Equal(ReleaseVerdict.Release, ReleaseDecider.Decide(Base() with { Performance = PerformanceState.Regression, PerformanceRequired = false }).Verdict);
    }
}
