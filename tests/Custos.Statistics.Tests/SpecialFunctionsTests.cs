namespace Custos.Statistics.Tests;

public class SpecialFunctionsTests
{
    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(2.0, 0.0)]
    [InlineData(5.0, 3.1780538303479458)]
    [InlineData(0.5, 0.57236494292470008)]
    [InlineData(10.5, 13.940625219403763)]
    public void LogGammaMatchesKnownValues(double x, double expected)
    {
        Assert.Equal(expected, SpecialFunctions.LogGamma(x), 10);
    }

    [Fact]
    public void IncompleteBetaMatchesClosedForm()
    {
        // Beta(2,3) の累積分布関数は 6x² − 8x³ + 3x⁴
        foreach (var x in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
        {
            var expected = (6 * x * x) - (8 * x * x * x) + (3 * x * x * x * x);
            Assert.Equal(expected, SpecialFunctions.RegularizedIncompleteBeta(x, 2, 3), 12);
        }
    }

    [Fact]
    public void InverseIncompleteBetaRoundTrips()
    {
        foreach (var p in new[] { 0.001, 0.05, 0.5, 0.95, 0.999 })
        {
            var x = SpecialFunctions.InverseRegularizedIncompleteBeta(p, 3.5, 7.25);
            Assert.Equal(p, SpecialFunctions.RegularizedIncompleteBeta(x, 3.5, 7.25), 10);
        }
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 0.84134474606854293)]
    [InlineData(-1.959963984540054, 0.025)]
    [InlineData(3.0, 0.99865010196836990)]
    [InlineData(-9.0, 1.1285884059538407e-19)]
    public void NormalCdfMatchesKnownValues(double x, double expected)
    {
        var actual = SpecialFunctions.NormalCdf(x);
        Assert.True(Math.Abs(actual - expected) <= Math.Max(1e-12, expected * 1e-8), $"Φ({x}) = {actual}, 期待値 {expected}");
    }

    [Theory]
    [InlineData(0.975, 1.959963984540054)]
    [InlineData(0.95, 1.6448536269514722)]
    [InlineData(0.8, 0.8416212335729143)]
    [InlineData(0.001, -3.090232306167813)]
    public void NormalQuantileMatchesKnownValues(double p, double expected)
    {
        Assert.Equal(expected, SpecialFunctions.NormalQuantile(p), 9);
    }

    [Theory]
    [InlineData(0.975, 8, 2.306004135204166)]
    [InlineData(0.975, 18, 2.100922040241038)]
    [InlineData(0.975, 38, 2.024394164321123)]
    [InlineData(0.8, 8, 0.8888895177670197)]
    [InlineData(0.8, 6, 0.9057032851805416)]
    [InlineData(0.95, 1, 6.313751514675043)]
    public void StudentTQuantileMatchesKnownValues(double p, double df, double expected)
    {
        Assert.Equal(expected, SpecialFunctions.StudentTQuantile(p, df), 6);
    }

    [Fact]
    public void StudentTCdfIsSymmetric()
    {
        Assert.Equal(0.5, SpecialFunctions.StudentTCdf(0, 5), 12);
        Assert.Equal(1, SpecialFunctions.StudentTCdf(2.0, 7) + SpecialFunctions.StudentTCdf(-2.0, 7), 12);
    }
}
