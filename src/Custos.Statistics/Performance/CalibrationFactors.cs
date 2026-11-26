using System.Globalization;
using Custos.Core.Config;

namespace Custos.Statistics.Performance;

/// <summary>換算係数の有効性。</summary>
/// <param name="Valid">使えるか。</param>
/// <param name="Reasons">使えない理由。</param>
public sealed record FactorValidity(bool Valid, IReadOnlyList<string> Reasons);

/// <summary>両方の環境で測った容量（SLOを守れる最大の負荷）とボトルネック。</summary>
/// <param name="Date">計測日。</param>
/// <param name="LocalCapacity">ローカル検証での容量。</param>
/// <param name="TargetCapacity">検証用環境（または本番）での容量。</param>
/// <param name="LocalBottleneck">ローカル検証で限界に達したときに詰まっていたもの。</param>
/// <param name="TargetBottleneck">検証用環境で詰まっていたもの。</param>
public sealed record CapacityPair(DateOnly Date, double LocalCapacity, double TargetCapacity, string LocalBottleneck, string TargetBottleneck);

/// <summary>係数を求め直した結果。</summary>
/// <param name="Ok">求められたか。</param>
/// <param name="Factor">係数（直近の比の中央値）。</param>
/// <param name="Low">幅の低い側。</param>
/// <param name="High">幅の高い側。</param>
/// <param name="Reasons">求められない理由。</param>
public sealed record FactorEstimate(bool Ok, double? Factor, double? Low, double? High, IReadOnlyList<string> Reasons);

/// <summary>規模を2倍にした確認の結果（第23.6節の5）。</summary>
/// <param name="Ratio">容量の比（2倍の資源 ÷ 1倍の資源）。</param>
/// <param name="SuitableForConversion">換算に向くか。</param>
/// <param name="Note">補足。</param>
public sealed record ScalingCheck(double Ratio, bool SuitableForConversion, string Note);

/// <summary>
/// 換算係数（第16.6節、第23.5〜23.7節）。換算値は見込みとして扱い、本番相当の限界値の判定には使わない。
/// </summary>
public static class CalibrationFactors
{
    /// <summary>有効期限の初期値（最後に答え合わせをしてから90日）。</summary>
    public const int DefaultValidDays = 90;

    /// <summary>有効期限の既定値。</summary>
    public static DateOnly DefaultValidUntil(DateOnly lastChecked, int days = DefaultValidDays) => lastChecked.AddDays(days);

    /// <summary>
    /// 係数が使えるかを判定する。
    /// </summary>
    /// <param name="spec">換算係数の宣言。</param>
    /// <param name="today">今日。</param>
    /// <param name="detectedChanges">前回の答え合わせ以降に検知した変更（db_migration、runtime_major_update など）。</param>
    /// <param name="currentLocalBottleneck">直近のローカル検証でのボトルネック（分からなければ null）。</param>
    /// <param name="latestPredicted">直近の検証用環境での計測に対する予測（分からなければ null）。</param>
    /// <param name="latestActual">その実測（分からなければ null）。</param>
    public static FactorValidity Check(
        CalibrationFactorSpec spec,
        DateOnly today,
        IReadOnlyCollection<string> detectedChanges,
        string? currentLocalBottleneck,
        double? latestPredicted,
        double? latestActual)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(detectedChanges);
        var reasons = new List<string>();
        var c = CultureInfo.InvariantCulture;
        if (today > spec.ValidUntil)
        {
            reasons.Add(string.Create(c, $"有効期限（{spec.ValidUntil:yyyy-MM-dd}）を過ぎている"));
        }

        var invalidating = detectedChanges.Where(change => spec.InvalidatedBy.Contains(change, StringComparer.Ordinal)).ToList();
        if (invalidating.Count > 0)
        {
            reasons.Add("係数を無効にする変更があった：" + string.Join(", ", invalidating));
        }

        if (currentLocalBottleneck != null && !string.Equals(currentLocalBottleneck, spec.Bottleneck, StringComparison.Ordinal))
        {
            reasons.Add($"ローカル検証でのボトルネックが変わった（校正時 {spec.Bottleneck}、現在 {currentLocalBottleneck}）");
        }

        if (latestPredicted is { } predicted && latestActual is { } actual && actual > 0)
        {
            var error = Math.Abs(predicted - actual) / actual;
            if (error > spec.MaxPredictionError)
            {
                reasons.Add(string.Create(c, $"予測と実測のずれ {error:P1} が上限 {spec.MaxPredictionError:P0} を超えた"));
            }
        }

        if (spec.FactorLow > spec.FactorHigh || spec.FactorLow <= 0)
        {
            reasons.Add("係数の幅が正しくない");
        }

        return new FactorValidity(reasons.Count == 0, reasons);
    }

    /// <summary>見込み（検証用環境での容量）を出す。範囲の低い側を使う。</summary>
    public static double PredictTargetCapacity(CalibrationFactorSpec spec, double localCapacity)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return localCapacity * spec.FactorLow;
    }

    /// <summary>
    /// 直近 window 回の両方の計測から、係数を求め直す。ボトルネックが両方の環境で同じでない組があれば、係数は無効とする（第23.6節の3）。
    /// </summary>
    public static FactorEstimate Recalibrate(IReadOnlyList<CapacityPair> history, int window = 3)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (window < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "1以上にしてください");
        }

        var recent = history.OrderBy(h => h.Date).TakeLast(window).ToList();
        var reasons = new List<string>();
        if (recent.Count == 0)
        {
            reasons.Add("計測がない");
            return new FactorEstimate(false, null, null, null, reasons);
        }

        foreach (var pair in recent)
        {
            if (!string.Equals(pair.LocalBottleneck, pair.TargetBottleneck, StringComparison.Ordinal))
            {
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{pair.Date:yyyy-MM-dd} の計測で、ボトルネックが両方の環境で違う（ローカル {pair.LocalBottleneck}、検証用環境 {pair.TargetBottleneck}）"));
            }

            if (pair.LocalCapacity <= 0 || pair.TargetCapacity <= 0)
            {
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{pair.Date:yyyy-MM-dd} の容量が 0 以下"));
            }
        }

        if (reasons.Count > 0)
        {
            return new FactorEstimate(false, null, null, null, reasons);
        }

        var ratios = recent.Select(p => p.TargetCapacity / p.LocalCapacity).ToList();
        return new FactorEstimate(true, Descriptive.Median(ratios), ratios.Min(), ratios.Max(), reasons);
    }

    /// <summary>
    /// 規模の違うプロファイルでの確認：資源を2倍にした小さな環境の容量が、ほぼ2倍になるか。
    /// </summary>
    public static ScalingCheck CheckScaling(double capacitySingle, double capacityDouble, double minimumRatio = 1.8)
    {
        if (capacitySingle <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacitySingle), capacitySingle, "容量は正の数にしてください");
        }

        var ratio = capacityDouble / capacitySingle;
        if (ratio >= minimumRatio)
        {
            return new ScalingCheck(ratio, true, "容量がほぼ2倍になった。換算を信頼しやすい");
        }

        return new ScalingCheck(ratio, false, "容量が2倍を大きく下回る。規模に比例しない要因（ロック競合など）があるので、このシナリオは換算に向かない");
    }
}
