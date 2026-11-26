using Custos.Core.Config;
using Custos.Statistics.Sampling;

namespace Custos.Statistics.Tests;

public class SamplingTests
{
    [Fact]
    public void LossMultipliersFollowSection262()
    {
        Assert.Null(LossModel.Multiplier(Severity.Critical));
        Assert.Equal(10, LossModel.Multiplier(Severity.High));
        Assert.Equal(3, LossModel.Multiplier(Severity.Medium));
        Assert.Equal(1, LossModel.Multiplier(Severity.Low));

        // r = 倍率 × （修正ループ1回の費用 ÷ 検証1件の費用）
        Assert.Equal(10 * (5.0 / 0.05), LossModel.LossRatio(Severity.High, 5.0, 0.05));
        Assert.Null(LossModel.LossRatio(Severity.Critical, 5.0, 0.05));
        Assert.Null(LossModel.LossRatio(Severity.Low, null, 0.05));
        Assert.Equal(15, LossModel.LossPerBug(Severity.Medium, 5));
    }

    [Fact]
    public void EpsilonPolicyModes()
    {
        var none = EpsilonPolicy.Resolve(new SamplingCategory("security", null, ErrorUpperMode.None, null, null), 1000, 0.8);
        Assert.Null(none.Epsilon);
        Assert.Equal("none", none.Source);

        var fixedValue = EpsilonPolicy.Resolve(new SamplingCategory("visual", null, ErrorUpperMode.Fixed, 0.01, null), 1000, 0.8);
        Assert.Equal(0.01, fixedValue.Epsilon);
        Assert.Equal(375, fixedValue.SamplesNeeded);

        var auto = EpsilonPolicy.Resolve(new SamplingCategory("core_flow", 1000, ErrorUpperMode.Auto, null, 0.001), 10_000, 0.8);
        Assert.Equal("auto", auto.Source);
        Assert.Equal(Math.Sqrt(3 / (0.8 * 10_000 * 1000)), auto.Epsilon ?? 0, 12);

        var fallback = EpsilonPolicy.Resolve(new SamplingCategory("core_flow", null, ErrorUpperMode.Auto, null, 0.001), 10_000, 0.8);
        Assert.Equal("fallback", fallback.Source);
        Assert.Equal(0.001, fallback.Epsilon);

        var strict = EpsilonPolicy.Resolve(new SamplingCategory("core_flow", null, ErrorUpperMode.Auto, null, null), null, null);
        Assert.Equal(EpsilonPolicy.StrictDefault, strict.Epsilon);

        // 実測から計算した r は、設定の loss_ratio より優先する
        var measured = EpsilonPolicy.Resolve(new SamplingCategory("core_flow", 10, ErrorUpperMode.Auto, null, 0.001), 10_000, 0.8, lossRatioOverride: 100);
        Assert.Equal(100, measured.LossRatio);
    }
}
