namespace Custos.Statistics.Tests;

public class SampleSizesTests
{
    /// <summary>第21.3節の表（n ≈ 3/(ε s)）。</summary>
    [Theory]
    [InlineData(0.01, 1.0, 300)]
    [InlineData(0.01, 0.8, 375)]
    [InlineData(0.01, 0.5, 600)]
    [InlineData(0.001, 1.0, 3_000)]
    [InlineData(0.001, 0.8, 3_750)]
    [InlineData(0.001, 0.5, 6_000)]
    [InlineData(0.0001, 1.0, 30_000)]
    [InlineData(0.0001, 0.8, 37_500)]
    [InlineData(0.0001, 0.5, 60_000)]
    public void RuleOfThreeReproducesSection213Table(double epsilon, double sensitivity, long expected)
    {
        Assert.Equal(expected, SampleSizes.RuleOfThree(epsilon, sensitivity));
    }

    [Fact]
    public void ExactZeroFailureIsCloseToRuleOfThree()
    {
        var exact = SampleSizes.ZeroFailureExact(0.01, 1.0, 0.95);
        Assert.Equal(299, exact);
        Assert.Equal(459, SampleSizes.ZeroFailureExact(0.01, 1.0, 0.99));
        Assert.True(SampleSizes.ZeroFailureExact(0.01, 0.8, 0.95) is >= 370 and <= 375);
    }

    /// <summary>第21.8節の表（s = 0.8）。表は2桁に丸めているので、相対誤差の許容幅をとる。</summary>
    [Theory]
    [InlineData(10_000, 10, 0.006, 610, 61)]
    [InlineData(10_000, 100, 0.0019, 1_900, 19)]
    [InlineData(10_000, 1_000, 0.0006, 6_100, 6)]
    [InlineData(1_000_000, 10, 0.0006, 6_100, 610)]
    [InlineData(1_000_000, 100, 0.00019, 19_000, 190)]
    [InlineData(1_000_000, 1_000, 0.00006, 61_000, 61)]
    public void OptimalReproducesSection218Table(double population, double r, double epsilon, double samples, double residual)
    {
        var optimal = SampleSizes.Optimal(population, r, 0.8);
        AssertRelative(epsilon, optimal.EpsilonStar, 0.03);
        AssertRelative(samples, optimal.SamplesStar, 0.03);
        AssertRelative(residual, optimal.ResidualUpper, 0.05);

        // このとき、検証の費用と残存損失の上限は等しくなる
        Assert.Equal(optimal.VerificationCost, optimal.ResidualLossUpper, 6);
    }

    [Fact]
    public void LossEstimateErrorChangesEpsilonBySquareRoot()
    {
        // 損失の見積もりが10倍ずれても、ε* のずれは約3倍に収まる
        var a = SampleSizes.Optimal(10_000, 10, 0.8).EpsilonStar;
        var b = SampleSizes.Optimal(10_000, 100, 0.8).EpsilonStar;
        Assert.Equal(Math.Sqrt(10), a / b, 9);

        // 規模が100倍になっても、抜き取りは10倍で済む
        var small = SampleSizes.Optimal(10_000, 100, 0.8).SamplesStar;
        var large = SampleSizes.Optimal(1_000_000, 100, 0.8).SamplesStar;
        Assert.Equal(10, large / small, 9);
    }

    private static void AssertRelative(double expected, double actual, double tolerance)
    {
        Assert.True(Math.Abs(actual - expected) <= expected * tolerance, $"期待値 {expected}、実際 {actual}（許容 {tolerance:P0}）");
    }
}
