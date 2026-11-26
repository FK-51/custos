namespace Custos.Statistics.Tests;

public class EstimationTests
{
    [Fact]
    public void MillsMatchesSection72Formula()
    {
        // S=50 を仕込んで s=40 が見つかり、自然発生のバグが n=20 見つかった
        var estimate = Estimation.Mills(50, 40, 20);
        Assert.True(estimate.Estimable);
        Assert.Equal(20 * 50 / 40.0, estimate.TotalNatural);
        Assert.Equal((20 * 50 / 40.0) - 20, estimate.RemainingNatural);
        Assert.NotNull(estimate.RemainingUpper);
        Assert.True(estimate.RemainingUpper > estimate.RemainingNatural);
    }

    [Fact]
    public void MillsWithoutDetectedSeedsIsNotEstimable()
    {
        var estimate = Estimation.Mills(10, 0, 3);
        Assert.False(estimate.Estimable);
        Assert.Null(estimate.TotalNatural);
    }

    [Fact]
    public void RemainingFromLowerBoundMatchesSection196Example()
    {
        // 実バグが20件見つかり、検出率の下限が83%なら、残りは最大で約4件
        Assert.Equal(4, Math.Round(Estimation.RemainingFromRate(20, 0.83)));
    }

    [Fact]
    public void CaptureRecaptureMatchesLincolnPetersenAndChapman()
    {
        var estimate = Estimation.CaptureRecapture(30, 20, 10, sameFamily: false);
        Assert.True(estimate.Estimable);
        Assert.Equal(60, estimate.LincolnPetersen);
        Assert.Equal((31.0 * 21 / 11) - 1, estimate.Chapman ?? double.NaN, 9);
        Assert.Equal(40, estimate.DistinctFound);
        Assert.False(estimate.IsLowerBound);
        Assert.True(estimate.ChapmanStandardError > 0);
    }

    [Fact]
    public void CaptureRecaptureSameFamilyIsLowerBoundAndZeroOverlapIsNotEstimable()
    {
        Assert.True(Estimation.CaptureRecapture(10, 10, 5, sameFamily: true).IsLowerBound);
        var none = Estimation.CaptureRecapture(10, 10, 0, sameFamily: false);
        Assert.False(none.Estimable);
        Assert.Null(none.LincolnPetersen);
    }

    [Fact]
    public void GoelOkumotoRecoversParametersFromExactData()
    {
        const double a = 100;
        const double b = 0.3;
        var counts = Enumerable.Range(1, 8)
            .Select(i => a * (Math.Exp(-b * (i - 1)) - Math.Exp(-b * i)))
            .ToList();
        var fit = Estimation.GoelOkumoto(counts);
        Assert.True(fit.Estimable, fit.Note);
        Assert.Equal(a, fit.A ?? 0, 4);
        Assert.Equal(b, fit.B ?? 0, 6);
        Assert.Equal(a * Math.Exp(-b * 8), fit.ExpectedRemaining ?? 0, 4);

        var next = Estimation.ExpectedFoundInNextLoops(fit, 8, 1);
        Assert.Equal(a * (Math.Exp(-b * 8) - Math.Exp(-b * 9)), next ?? 0, 4);

        // 残りの期待値が1件以下になるまでのループの回数
        var loops = Estimation.LoopsUntilRemaining(fit, 8, 1);
        Assert.Equal(Math.Ceiling((Math.Log(a) / b) - 8), loops);
    }

    [Fact]
    public void GoelOkumotoFitsDecreasingIntegerCounts()
    {
        var fit = Estimation.GoelOkumoto([12.0, 9, 6, 5, 3, 2, 1]);
        Assert.True(fit.Estimable, fit.Note);
        Assert.InRange(fit.A ?? 0, 38, 50);
        Assert.InRange(fit.ExpectedRemaining ?? -1, 0, 10);
        var next = Estimation.ExpectedFoundInNextLoops(fit, 7, 1) ?? -1;
        Assert.InRange(next, 0, 2);
    }

    [Fact]
    public void GoelOkumotoWithoutGrowthIsNotEstimable()
    {
        var flat = Estimation.GoelOkumoto([5.0, 5, 5, 5, 5, 5]);
        Assert.False(flat.Estimable);
        var increasing = Estimation.GoelOkumoto([1.0, 2, 4, 8]);
        Assert.False(increasing.Estimable);
        var tooShort = Estimation.GoelOkumoto([10.0, 5]);
        Assert.False(tooShort.Estimable);
    }
}
