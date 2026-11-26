namespace Custos.Statistics.Tests;

public class StrataTests
{
    private static Dictionary<StratumId, int> NoReal() => [];

    [Fact]
    public void TrivialStratumIsExcludedFromEstimation()
    {
        var observations = StratumId.All.Select(id => new StratumObservation(id, 30, 27)).ToList();
        var result = StratifiedDetection.Estimate(observations, NoReal());
        var trivial = result.Strata.Single(s => s.Id == new StratumId(1, 1));
        Assert.Equal(StratumStatus.Excluded, trivial.Status);
        Assert.True(trivial.MechanismGap);
        Assert.Equal(8, result.Strata.Count(s => s.Status == StratumStatus.Estimated));
        Assert.Equal(0.9, result.OverallRate ?? 0, 9);
    }

    [Fact]
    public void SmallStrataAreMergedWithNeighbors()
    {
        var observations = new List<StratumObservation>
        {
            new(new StratumId(2, 2), 10, 9),
            new(new StratumId(2, 3), 12, 10),
            new(new StratumId(3, 3), 30, 24),
        };
        var result = StratifiedDetection.Estimate(observations, NoReal());
        var r2v2 = result.Strata.Single(s => s.Id == new StratumId(2, 2));
        Assert.Contains(new StratumId(2, 3), r2v2.Group);
        Assert.Equal(22, r2v2.Seeded);
        Assert.Equal(19, r2v2.Found);
        var r3v3 = result.Strata.Single(s => s.Id == new StratumId(3, 3));
        Assert.Single(r3v3.Group);
    }

    [Fact]
    public void LowDetectionStratumIsInestimable()
    {
        var observations = new List<StratumObservation>
        {
            new(new StratumId(3, 3), 30, 6),
            new(new StratumId(2, 2), 30, 27),
        };
        var real = new Dictionary<StratumId, int> { [new StratumId(3, 3)] = 2, [new StratumId(2, 2)] = 10 };
        var result = StratifiedDetection.Estimate(observations, real);
        Assert.Equal(StratumStatus.Inestimable, result.Strata.Single(s => s.Id == new StratumId(3, 3)).Status);
        Assert.Contains(new StratumId(3, 3), result.InestimableWithRealBugs);
        Assert.Equal(StratumStatus.Estimated, result.Strata.Single(s => s.Id == new StratumId(2, 2)).Status);
    }

    [Fact]
    public void PostStratificationWeightsByRealBugDistribution()
    {
        var observations = new List<StratumObservation>
        {
            new(new StratumId(2, 1), 30, 30),
            new(new StratumId(3, 3), 30, 15),
        };
        var real = new Dictionary<StratumId, int> { [new StratumId(2, 1)] = 3, [new StratumId(3, 3)] = 1 };
        var result = StratifiedDetection.Estimate(observations, real);
        Assert.Equal((0.75 * 1.0) + (0.25 * 0.5), result.OverallRate ?? 0, 9);

        // 残りの点推定：R3V3 は 1 × (1/0.5 − 1) = 1
        Assert.Equal(1, result.RemainingPoint, 9);
        Assert.True(result.RemainingUpper > result.RemainingPoint);
    }

    [Fact]
    public void CoverageAndObservationAccuracyAreSeparated()
    {
        var observations = new List<StratumObservation> { new(new StratumId(2, 2), 40, 20, Reached: 25) };
        var result = StratifiedDetection.Estimate(observations, NoReal());
        var stratum = result.Strata.Single(s => s.Id == new StratumId(2, 2));
        Assert.Equal(25.0 / 40, stratum.Coverage ?? 0, 9);
        Assert.Equal(20.0 / 25, stratum.ObservationAccuracy ?? 0, 9);
    }

    [Fact]
    public void HighVisibilityMissesAreMechanismGaps()
    {
        var observations = new List<StratumObservation> { new(new StratumId(3, 1), 30, 28) };
        var result = StratifiedDetection.Estimate(observations, NoReal());
        Assert.True(result.Strata.Single(s => s.Id == new StratumId(3, 1)).MechanismGap);
    }

    [Fact]
    public void StratumIdParsesAndFormats()
    {
        Assert.Equal(new StratumId(2, 3), StratumId.Parse("R2V3"));
        Assert.Equal("R1V2", new StratumId(1, 2).ToString());
        Assert.Throws<FormatException>(() => StratumId.Parse("R4V1"));
    }
}
