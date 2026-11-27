using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Maturity;

namespace Custos.Core.Tests;

/// <summary>成熟度のレベル（設計書 第17.5節）。レベル3は、バグ注入（D-3）、カナリアの累積の検出率、校正の根拠で決まる。</summary>
public class MaturityCalculatorTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    private static ConfigSet Config()
    {
        // 参照アプリの設定（フレームワークのリポジトリにある）を使う
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Custos.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var loaded = ConfigLoader.Load(Path.Combine(directory.FullName, "reference-app", "verify", "config"));
        Assert.NotNull(loaded.Config);
        return loaded.Config;
    }

    private static ConformanceReport Report(bool d3)
    {
        var items = new List<ConformanceItemInfo>
        {
            new("A-1", ConformanceCategory.A, ItemKind.Required, "all", "起動", "17.3"),
            new("B-3", ConformanceCategory.B, ItemKind.Capability, "all", "時刻・シードの固定", "17.3"),
            new("B-4", ConformanceCategory.B, ItemKind.Capability, "all", "再実行の一致", "17.3"),
            new("D-3", ConformanceCategory.D, ItemKind.Capability, "all", "バグ注入", "17.3"),
        };
        var results = new List<ItemResult>
        {
            ItemResult.Passed("A-1", "ok"),
            ItemResult.Passed("B-3", "ok"),
            ItemResult.Passed("B-4", "ok"),
            d3 ? ItemResult.Passed("D-3", "ok") : new ItemResult("D-3", Verdict.Fail, "ng", [], new Dictionary<string, string>(), 0),
        };
        return new ConformanceReport("000001-conformance", At, "0.1.0", "judge", "config", [], items, results);
    }

    private static CalibrationEvidence Evidence(double? canary, DateTimeOffset? calibratedAt, string basis = "generated", double? seeds = null) =>
        new(canary, 0.9, calibratedAt, basis, null, null, null, null, seeds);

    [Fact]
    public void CanaryRateAndCalibrationGiveLevel3()
    {
        var maturity = MaturityCalculator.Compute(Config(), Report(d3: true), Evidence(0.95, At));
        Assert.Equal("3a", maturity.Level);
    }

    [Fact]
    public void SeedDetectionRateIsNotUsedAsCanaryRate()
    {
        // 校正用バグを全部見つけても、カナリアの累積の検出率が基準未満ならレベル2（以前は校正用バグの検出率で判定していた）
        var maturity = MaturityCalculator.Compute(Config(), Report(d3: true), Evidence(0.5, At, seeds: 1.0));
        Assert.Equal("2", maturity.Level);
        Assert.Contains("カナリアの累積の検出率 50%", maturity.Reason, StringComparison.Ordinal);
        Assert.Equal(1.0, maturity.Metrics["seed_detection_rate"]?.GetValue<double>());
    }

    [Fact]
    public void CanaryWithoutCalibrationStaysAtLevel2()
    {
        var maturity = MaturityCalculator.Compute(Config(), Report(d3: true), Evidence(0.95, null, "none"));
        Assert.Equal("2", maturity.Level);
        Assert.Contains("校正（custos calibrate）をしていない", maturity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedBugInjectionStaysAtLevel2()
    {
        var maturity = MaturityCalculator.Compute(Config(), Report(d3: false), Evidence(0.95, At));
        Assert.Equal("2", maturity.Level);
        Assert.Contains("D-3", maturity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BasisSelectsLevel3Division()
    {
        Assert.Equal("3c", MaturityCalculator.Compute(Config(), Report(d3: true), Evidence(0.95, At, "own_real_bugs")).Level);
        Assert.Equal("3b", MaturityCalculator.Compute(Config(), Report(d3: true), Evidence(0.95, At, "other_projects")).Level);
    }
}
