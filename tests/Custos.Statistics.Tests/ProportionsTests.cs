namespace Custos.Statistics.Tests;

public class ProportionsTests
{
    /// <summary>
    /// 第19.6節の表（検出率90%の場合の95%信頼区間）。Wilson の区間で再現できる。
    /// ただし 90/100 の上限は Wilson では 94.48% で、表の「95%」とは丸めが合わない（設計書の表の丸めの食い違いとして報告する）。
    /// </summary>
    [Theory]
    [InlineData(18, 20, 70, 97, 0)]
    [InlineData(45, 50, 79, 96, 0)]
    [InlineData(90, 100, 83, 95, 1)]
    [InlineData(270, 300, 86, 93, 0)]
    public void WilsonReproducesDesignTable(int found, int seeded, int lowerPercent, int upperPercent, int upperTolerance)
    {
        var interval = Proportions.Wilson(found, seeded, 0.95);
        Assert.Equal(lowerPercent, (int)Math.Round(interval.Lower * 100));
        Assert.InRange((int)Math.Round(interval.Upper * 100), upperPercent - upperTolerance, upperPercent);
        Assert.Equal(0.9, interval.Estimate, 12);
    }

    [Fact]
    public void WilsonMatchesIndependentComputation()
    {
        var interval = Proportions.Wilson(90, 100, 0.95);
        Assert.Equal(0.8256343384950865, interval.Lower, 9);
        Assert.Equal(0.9447708629393249, interval.Upper, 9);
    }

    [Fact]
    public void ClopperPearsonMatchesKnownValues()
    {
        var interval = Proportions.ClopperPearson(18, 20, 0.95);
        Assert.Equal(0.6830, interval.Lower, 3);
        Assert.Equal(0.9877, interval.Upper, 3);

        var zero = Proportions.ClopperPearson(0, 10, 0.95);
        Assert.Equal(0, zero.Lower);
        Assert.Equal(1 - Math.Pow(0.025, 0.1), zero.Upper, 9);

        var all = Proportions.ClopperPearson(10, 10, 0.95);
        Assert.Equal(1, all.Upper);
        Assert.Equal(Math.Pow(0.025, 0.1), all.Lower, 9);
    }

    [Fact]
    public void OneSidedUpperWithZeroFailuresMatchesClosedForm()
    {
        foreach (var n in new[] { 10, 300, 3000 })
        {
            Assert.Equal(1 - Math.Pow(0.05, 1.0 / n), Proportions.ClopperPearsonUpper(0, n, 0.95), 10);
            Assert.Equal(Proportions.ZeroFailureUpper(n, 0.95), Proportions.ClopperPearsonUpper(0, n, 0.95), 10);
        }
    }

    [Fact]
    public void RuleOfThreeApproximation()
    {
        // 95%なら約 3/n、99%なら約 4.6/n（第5.2節）
        Assert.Equal(3.0 / 300, Proportions.ZeroFailureUpperApprox(300, 0.95), 3);
        Assert.Equal(4.6 / 1000, Proportions.ZeroFailureUpperApprox(1000, 0.99), 4);
        Assert.InRange(Proportions.ZeroFailureUpper(300, 0.95), 0.0099, 0.0100);
    }

    [Fact]
    public void ErrorRateUpperIncludesVerifierSensitivity()
    {
        var plain = Proportions.ErrorRateUpperBound(0, 375, 0.95, 1.0);
        var withSensitivity = Proportions.ErrorRateUpperBound(0, 375, 0.95, 0.8);
        Assert.Equal(plain / 0.8, withSensitivity, 12);

        // 3/(ε s) 件の検証で、誤り率の上限は約 ε になる（第21.3節）
        Assert.InRange(withSensitivity, 0.0095, 0.0101);
        Assert.Throws<ArgumentOutOfRangeException>(() => Proportions.ErrorRateUpperBound(0, 10, 0.95, 0));
    }

    [Fact]
    public void MissProbabilityMatchesSection51()
    {
        Assert.Equal(Math.Pow(0.97, 3), Proportions.MissProbabilityApprox(1000, 30, 3), 12);
        var exact = Proportions.MissProbabilityExact(1000, 30, 3);
        Assert.Equal((970.0 / 1000) * (969.0 / 999) * (968.0 / 998), exact, 12);
        Assert.Equal(0, Proportions.MissProbabilityExact(10, 9, 2));
    }

    [Theory]
    [InlineData(1_000_000, 0.05, 950_000)]
    [InlineData(1_000_000, 0.01, 990_000)]
    public void RequiredForZeroErrorsMatchesSection51Table(long population, double significance, long expected)
    {
        Assert.Equal(expected, Proportions.RequiredForZeroErrors(population, significance));
    }

    [Fact]
    public void BayesResidualProbability()
    {
        // N 件のうち N−M 件を検証して誤りがない。一様事前分布で、未検証の M 件に誤りが含まれる確率は M/(N+1)
        Assert.Equal(50_000.0 / 1_000_001, Proportions.BayesResidualErrorProbability(1_000_000, 50_000), 15);
    }

    /// <summary>第5.2節の表（N=100万、誤りが一件も見つからなかったときの、残りの誤りの上限）。</summary>
    [Theory]
    [InlineData(300, 10_000, 0.02)]
    [InlineData(3_000, 1_000, 0.02)]
    [InlineData(30_000, 100, 0.03)]
    [InlineData(260_000, 10, 0.11)]
    public void ResidualUpperBoundReproducesSection52Table(long sampled, long expected, double relativeTolerance)
    {
        var approx = Proportions.ResidualUpperBound(1_000_000, sampled, 0.95);
        var exact = Proportions.ResidualUpperBound(1_000_000, sampled, 0.95, exact: true);
        Assert.InRange(approx, expected * (1 - relativeTolerance), expected * (1 + relativeTolerance));
        Assert.InRange(exact, expected * (1 - relativeTolerance), expected * (1 + relativeTolerance));
    }

    [Fact]
    public void ResidualUpperBoundIsZeroAtNinetyFivePercentCoverage()
    {
        Assert.Equal(0, Proportions.ResidualUpperBound(1_000_000, 950_000, 0.95));
        Assert.Equal(0, Proportions.ResidualUpperBound(1_000_000, 950_000, 0.95, exact: true));
        Assert.Equal(0, Proportions.ResidualUpperBound(1_000_000, 1_000_000, 0.95));
    }
}
