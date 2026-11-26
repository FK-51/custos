namespace Custos.Statistics.Tests;

public class DeterminismTests
{
    /// <summary>第22.5節の表（誤認の確率5%以下）。</summary>
    [Theory]
    [InlineData(0.9, 2)]
    [InlineData(0.5, 5)]
    [InlineData(0.3, 9)]
    [InlineData(0.1, 29)]
    public void CanaryRerunsReproduceSection225Table(double reproductionRate, int expected)
    {
        Assert.Equal(expected, Determinism.CanaryRerunsRequired(reproductionRate));
    }

    [Fact]
    public void CanaryWithoutReproductionCannotBeDiscriminated()
    {
        Assert.Null(Determinism.CanaryRerunsRequired(0));
        Assert.Equal(1, Determinism.CanaryRerunsRequired(1));

        // 判別できないときは本物のバグとして扱う
        Assert.False(Determinism.IsCanary(0, 100, 0));
        Assert.False(Determinism.IsCanary(0.5, 4, 0));
        Assert.True(Determinism.IsCanary(0.5, 5, 0));
        Assert.False(Determinism.IsCanary(0.5, 50, 1));
    }

    /// <summary>第22.4節の表。</summary>
    [Theory]
    [InlineData(19, 0.10)]
    [InlineData(39, 0.05)]
    [InlineData(199, 0.01)]
    public void EnvelopeFalsePositiveReproducesSection224Table(int runs, double rate)
    {
        Assert.Equal(rate, Determinism.EnvelopeFalsePositive(runs), 12);
        Assert.Equal(runs, Determinism.EnvelopeRunsRequired(rate));
    }

    [Fact]
    public void NoOccurrenceUpperBoundMatchesSection226()
    {
        // 30回で約10%、100回で約3%
        Assert.Equal(0.10, Determinism.NoOccurrenceUpperBound(30), 2);
        Assert.Equal(0.03, Determinism.NoOccurrenceUpperBound(100), 2);
        Assert.InRange(Determinism.NoOccurrenceUpperBound(100, exact: true), 0.029, 0.030);
    }

    [Fact]
    public void OutsideEnvelopeDetectsValuesOutsideHonestRange()
    {
        var honest = new List<double> { 10, 12, 11 };
        Assert.False(Determinism.OutsideEnvelope(honest, 11.5));
        Assert.True(Determinism.OutsideEnvelope(honest, 12.5));
        Assert.True(Determinism.OutsideEnvelope(honest, 9));
    }
}
