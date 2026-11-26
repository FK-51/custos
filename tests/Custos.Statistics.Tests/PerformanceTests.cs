using Custos.Core.Config;
using Custos.Statistics.Performance;

namespace Custos.Statistics.Tests;

public class PerformanceTests
{
    [Fact]
    public void DescriptiveStatistics()
    {
        var values = new List<double> { 2, 4, 4, 4, 5, 5, 7, 9 };
        Assert.Equal(5, Descriptive.Mean(values), 12);
        Assert.Equal(2.1380899352993952, Descriptive.StandardDeviation(values), 12);
        Assert.Equal(2.1380899352993952 / 5, Descriptive.CoefficientOfVariation(values), 12);
        Assert.Equal(4.5, Descriptive.Median(values), 12);
        Assert.Equal(8.86, Descriptive.Percentile(values, 0.99), 12);
        Assert.Equal(2, Descriptive.Percentile(values, 0), 12);
    }

    [Fact]
    public void ExactMannWhitneyDistributionSumsToBinomial()
    {
        var distribution = RankTests.ExactDistribution(5, 5);
        Assert.Equal(252, distribution.Sum(), 9);
        Assert.Equal([1, 1, 2, 3, 5], distribution.Take(5).ToArray());
        Assert.Equal(distribution, distribution.Reverse().ToArray());
        Assert.Equal(184_756, RankTests.ExactDistribution(10, 10).Sum(), 6);
    }

    [Fact]
    public void MannWhitneyExactPValues()
    {
        double[] baseline = [1, 2, 3, 4, 5];
        double[] candidate = [6, 7, 8, 9, 10];
        var greater = RankTests.MannWhitney(baseline, candidate, Alternative.Greater);
        Assert.True(greater.Exact);
        Assert.Equal(25, greater.U);
        Assert.Equal(1.0 / 252, greater.PValue, 12);
        Assert.Equal(2.0 / 252, RankTests.MannWhitney(baseline, candidate, Alternative.TwoSided).PValue, 12);

        // U = 4 の左側の確率は (1 + 1 + 2 + 3 + 5) / 252
        double[] low = [1, 2, 3, 6, 7];
        double[] high = [4, 5, 8, 9, 10];
        var less = RankTests.MannWhitney(high, low, Alternative.Less);
        Assert.Equal(4, less.U);
        Assert.Equal((1 + 1 + 2 + 3 + 5) / 252.0, less.PValue, 12);
    }

    [Fact]
    public void MannWhitneyApproximationAgreesWithExact()
    {
        var baseline = Enumerable.Range(0, 20).Select(i => 100.0 + (i * 1.3)).ToList();
        var candidate = Enumerable.Range(0, 20).Select(i => 103.05 + (i * 1.1)).ToList();
        var exact = RankTests.MannWhitney(baseline, candidate, Alternative.Greater);
        var approx = RankTests.MannWhitney(baseline, candidate, Alternative.Greater, forceApproximation: true);
        Assert.True(exact.Exact);
        Assert.False(approx.Exact);
        Assert.InRange(approx.PValue, exact.PValue * 0.8, exact.PValue * 1.25);
    }

    [Fact]
    public void MannWhitneyHandlesTies()
    {
        double[] same = [5, 5, 5, 5, 5];
        var result = RankTests.MannWhitney(same, same, Alternative.TwoSided);
        Assert.False(result.Exact);
        Assert.Equal(1, result.PValue, 12);
        Assert.Equal(0.5, result.ProbabilityOfSuperiority, 12);
    }

    [Fact]
    public void BootstrapIsDeterministicForSameSeed()
    {
        var baseline = Enumerable.Range(0, 30).Select(i => 100.0 + (i % 7)).ToList();
        var candidate = Enumerable.Range(0, 30).Select(i => 130.0 + (i % 5)).ToList();
        var a = Bootstrap.Difference(baseline, candidate, Bootstrap.Median, seed: 42);
        var b = Bootstrap.Difference(baseline, candidate, Bootstrap.Median, seed: 42);
        Assert.Equal(a, b);
        Assert.True(a.Lower > 0, "劣化は区間が 0 を含まない");

        var same = Bootstrap.Difference(baseline, baseline, Bootstrap.P99, relative: true, seed: 7);
        Assert.True(same.Lower <= 0 && same.Upper >= 0);
    }

    [Fact]
    public void DeterministicRandomIsStable()
    {
        var random = new DeterministicRandom(12345);
        var first = Enumerable.Range(0, 5).Select(_ => random.NextInt(1000)).ToList();
        var again = new DeterministicRandom(12345);
        Assert.Equal(first, Enumerable.Range(0, 5).Select(_ => again.NextInt(1000)).ToList());
        var d = new DeterministicRandom(1).NextDouble();
        Assert.InRange(d, 0, 1);
    }

    /// <summary>第23.2節の表（誤検出5%、見逃し20%で劣化を検出するための、変動係数の上限）。</summary>
    [Theory]
    [InlineData(0.05, 5, 0.025)]
    [InlineData(0.05, 10, 0.038)]
    [InlineData(0.05, 20, 0.055)]
    [InlineData(0.10, 5, 0.05)]
    [InlineData(0.10, 10, 0.075)]
    [InlineData(0.10, 20, 0.11)]
    [InlineData(0.20, 5, 0.10)]
    [InlineData(0.20, 10, 0.15)]
    [InlineData(0.20, 20, 0.22)]
    public void CvToleranceReproducesSection232Table(double regression, int runs, double expected)
    {
        var actual = Comparison.MaxCoefficientOfVariation(regression, runs);
        Assert.True(Math.Abs(actual - expected) <= expected * 0.03, $"δ={regression}, n={runs}: {actual:P2}（表は {expected:P1}）");
    }

    [Fact]
    public void RunsRequiredIsConsistentWithTable()
    {
        // 26.4：検出したい劣化 20%、各ビルド5回なら変動係数は10%まで許容できる
        Assert.Equal(5, Comparison.RunsRequired(0.20, 0.098));
        Assert.True(Comparison.RunsRequired(0.10, 0.11) is >= 19 and <= 21);
    }

    [Fact]
    public void CompareSameBuildIsNoDifferenceAndCanaryIsRegression()
    {
        double[] baseline = [200, 204, 198, 202, 201];
        double[] same = [201, 199, 203, 200, 202];
        double[] canary = [250, 255, 248, 252, 251];
        var none = Comparison.Compare(baseline, same, 0.20, 5, 0.10);
        Assert.Equal(ComparisonOutcome.NoDifference, none.Outcome);
        var regression = Comparison.Compare(baseline, canary, 0.20, 5, 0.10);
        Assert.Equal(ComparisonOutcome.Regression, regression.Outcome);
        var tooFew = Comparison.Compare(baseline, [200.0, 201], 0.20, 5, 0.10);
        Assert.Equal(ComparisonOutcome.Inconclusive, tooFew.Outcome);
        double[] noisy = [100, 300, 150, 250, 200];
        Assert.Equal(ComparisonOutcome.Inconclusive, Comparison.Compare(baseline, noisy, 0.20, 5, 0.10).Outcome);

        // 容量は大きいほど良い
        double[] capacity = [500, 505, 498, 502, 501];
        double[] lowerCapacity = [380, 385, 379, 382, 381];
        Assert.Equal(ComparisonOutcome.Regression, Comparison.Compare(capacity, lowerCapacity, 0.20, 5, 0.10, higherIsWorse: false).Outcome);
    }

    [Fact]
    public void MeasurementValidityRules()
    {
        var criteria = ValidityCriteria.Default;
        var good = new MeasurementObservation(0.5, 2, 200, 1.01, false, 0.01, [100, 101, 99], [100, 102, 98], 0.001);
        Assert.Equal(MeasurementState.Valid, MeasurementValidity.Evaluate(good, criteria).State);

        var saturated = good with { LoadGeneratorCpuMax = 0.8 };
        Assert.Equal(MeasurementState.Invalid, MeasurementValidity.Evaluate(saturated, criteria).State);

        // 送信の遅れ：SLOの閾値 200ms の 2% = 4ms を超えたら無効
        var lagging = good with { SendLagP99Ms = 5 };
        Assert.Equal(MeasurementState.Invalid, MeasurementValidity.Evaluate(lagging, criteria).State);

        var drifted = good with { ReferenceWorkloadRatio = 1.04 };
        Assert.Equal(MeasurementState.Invalid, MeasurementValidity.Evaluate(drifted, criteria).State);

        var swapped = good with { SwapOccurred = true };
        Assert.Equal(MeasurementState.Invalid, MeasurementValidity.Evaluate(swapped, criteria).State);

        var stolen = good with { CpuStealRatio = 0.08 };
        Assert.Equal(MeasurementState.Invalid, MeasurementValidity.Evaluate(stolen, criteria).State);

        var leaking = good with { SecondHalf = [120, 122, 125] };
        var leakVerdict = MeasurementValidity.Evaluate(leaking, criteria);
        Assert.Equal(MeasurementState.Invalid, leakVerdict.State);
        Assert.Contains(leakVerdict.Warnings, w => w.Contains("メモリリーク", StringComparison.Ordinal));

        var errors = good with { ErrorRate = 0.02 };
        Assert.Equal(MeasurementState.SloViolation, MeasurementValidity.Evaluate(errors, criteria).State);

        var noWarmup = good with { WarmupExcluded = false };
        Assert.Equal(MeasurementState.Invalid, MeasurementValidity.Evaluate(noWarmup, criteria).State);
    }

    [Fact]
    public void SeriesValidityAndEnvironmentProblem()
    {
        var criteria = ValidityCriteria.Default;
        Assert.True(MeasurementValidity.EvaluateSeries([100.0, 101, 99, 100, 102], criteria).Comparable);
        Assert.False(MeasurementValidity.EvaluateSeries([100.0, 101, 99], criteria).Comparable);
        Assert.False(MeasurementValidity.EvaluateSeries([100.0, 130, 80, 100, 120], criteria).Comparable);

        Assert.False(MeasurementValidity.IsEnvironmentProblem([MeasurementState.Invalid, MeasurementState.Invalid]));
        Assert.True(MeasurementValidity.IsEnvironmentProblem([MeasurementState.Valid, MeasurementState.Invalid, MeasurementState.Invalid, MeasurementState.Invalid]));
        Assert.False(MeasurementValidity.IsEnvironmentProblem([MeasurementState.Invalid, MeasurementState.Invalid, MeasurementState.Valid]));
    }

    [Fact]
    public void CalibrationFactorValidity()
    {
        var spec = new CalibrationFactorSpec(
            "mini-1",
            "staging-full",
            "checkout_flow",
            "max_users_within_slo",
            6.2,
            5.4,
            7.1,
            "db_cpu",
            new DateOnly(2026, 11, 20),
            new DateOnly(2027, 2, 18),
            0.20,
            ["db_migration", "runtime_major_update", "node_layout_change", "bottleneck_change"]);
        var today = new DateOnly(2026, 12, 1);
        Assert.True(CalibrationFactors.Check(spec, today, [], "db_cpu", 1000, 1050).Valid);
        Assert.False(CalibrationFactors.Check(spec, new DateOnly(2027, 3, 1), [], "db_cpu", null, null).Valid);
        Assert.False(CalibrationFactors.Check(spec, today, ["db_migration"], "db_cpu", null, null).Valid);
        Assert.True(CalibrationFactors.Check(spec, today, ["unrelated_change"], "db_cpu", null, null).Valid);
        Assert.False(CalibrationFactors.Check(spec, today, [], "app_cpu", null, null).Valid);
        Assert.False(CalibrationFactors.Check(spec, today, [], "db_cpu", 1000, 1300).Valid);

        // 見込みは範囲の低い側を使う
        Assert.Equal(100 * 5.4, CalibrationFactors.PredictTargetCapacity(spec, 100), 9);
        Assert.Equal(new DateOnly(2027, 2, 18), CalibrationFactors.DefaultValidUntil(new DateOnly(2026, 11, 20)));
    }

    [Fact]
    public void RecalibrationAndScaling()
    {
        var history = new List<CapacityPair>
        {
            new(new DateOnly(2026, 11, 1), 100, 600, "db_cpu", "db_cpu"),
            new(new DateOnly(2026, 11, 8), 100, 650, "db_cpu", "db_cpu"),
            new(new DateOnly(2026, 11, 15), 100, 620, "db_cpu", "db_cpu"),
            new(new DateOnly(2026, 11, 22), 110, 660, "db_cpu", "db_cpu"),
        };
        var estimate = CalibrationFactors.Recalibrate(history, 3);
        Assert.True(estimate.Ok);
        Assert.Equal(6.2, estimate.Factor ?? 0, 9);
        Assert.Equal(6.0, estimate.Low ?? 0, 9);
        Assert.Equal(6.5, estimate.High ?? 0, 9);

        var mismatch = history.Append(new CapacityPair(new DateOnly(2026, 11, 29), 100, 400, "app_cpu", "lock_wait")).ToList();
        Assert.False(CalibrationFactors.Recalibrate(mismatch, 3).Ok);

        Assert.True(CalibrationFactors.CheckScaling(100, 195).SuitableForConversion);
        Assert.False(CalibrationFactors.CheckScaling(100, 140).SuitableForConversion);
    }
}
