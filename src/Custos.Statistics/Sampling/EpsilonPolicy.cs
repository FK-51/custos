using Custos.Core.Config;

namespace Custos.Statistics.Sampling;

/// <summary>実際に使う誤り率の上限 ε。</summary>
/// <param name="Epsilon">ε（使わない場合は null）。</param>
/// <param name="Source">決め方（fixed：設定の値、auto：r から計算、fallback：r が未定の間の仮の値、none：使わない）。</param>
/// <param name="LossRatio">計算に使った r。</param>
/// <param name="SamplesNeeded">ε を示すのに必要な抜き取り件数 3/(ε s)。</param>
/// <param name="Note">補足。</param>
public sealed record EffectiveEpsilon(double? Epsilon, string Source, double? LossRatio, long? SamplesNeeded, string Note);

/// <summary>
/// 損失の表し方（第26.2節）。損失は「本番で見つかった場合に、修正ループ何回分の手間になるか」で表す。
/// </summary>
public static class LossModel
{
    /// <summary>重大度ごとの倍率。致命的は ε を使わない（機械判定でゼロを目指す）ので null。</summary>
    public static double? Multiplier(Severity severity) => severity switch
    {
        Severity.Critical => null,
        Severity.High => 10,
        Severity.Medium => 3,
        _ => 1,
    };

    /// <summary>
    /// 損失の比 r = 倍率 × （修正ループ1回の費用 ÷ 検証1件の費用）。費用はレポートのコスト欄の実測値を使う。
    /// 致命的、または費用が分からなければ null。
    /// </summary>
    public static double? LossRatio(Severity severity, double? fixLoopCost, double? verificationCost)
    {
        if (Multiplier(severity) is not { } multiplier || fixLoopCost is not { } fix || verificationCost is not { } verify || verify <= 0 || fix <= 0)
        {
            return null;
        }

        return multiplier * (fix / verify);
    }

    /// <summary>残ったバグ1件の損失（修正ループ1回の費用を単位にした倍率 × 費用）。</summary>
    public static double? LossPerBug(Severity severity, double fixLoopCost) =>
        Multiplier(severity) is { } multiplier ? multiplier * fixLoopCost : null;
}

/// <summary>
/// 種類ごとの ε の決め方（第18.8節、第21.8節）。
/// </summary>
public static class EpsilonPolicy
{
    /// <summary>fallback もない場合に使う、厳しい側の値（0.1%）。</summary>
    public const double StrictDefault = 0.001;

    /// <summary>
    /// 設定（SamplingCategory）と、確認対象の数 N、検証役の感度 s、r から、使う ε を決める。
    /// </summary>
    /// <param name="category">種類の設定。</param>
    /// <param name="population">確認対象の数 N（分からなければ null）。</param>
    /// <param name="sensitivity">検証役の感度 s（分からなければ null）。</param>
    /// <param name="lossRatioOverride">実測から計算した r（あれば設定の loss_ratio より優先する）。</param>
    public static EffectiveEpsilon Resolve(SamplingCategory category, double? population, double? sensitivity, double? lossRatioOverride = null)
    {
        ArgumentNullException.ThrowIfNull(category);
        switch (category.Mode)
        {
            case ErrorUpperMode.None:
                return new EffectiveEpsilon(null, "none", null, null, "ε は使わない。プログラムで判定し、ゼロを目指す");
            case ErrorUpperMode.Fixed when category.ErrorUpper is { } fixedValue:
                return new EffectiveEpsilon(fixedValue, "fixed", category.LossRatio, Samples(fixedValue, sensitivity), "設定で指定した値");
        }

        var r = lossRatioOverride ?? category.LossRatio;
        if (r is { } ratio && ratio > 0 && population is { } n && n > 0 && sensitivity is { } s && s > 0)
        {
            var optimal = SampleSizes.Optimal(n, ratio, s);
            return new EffectiveEpsilon(optimal.EpsilonStar, "auto", ratio, (long)Math.Ceiling(optimal.SamplesStar), "損失との釣り合いから計算した ε*");
        }

        var fallback = category.FallbackErrorUpper ?? StrictDefault;
        var missing = new List<string>();
        if (r == null)
        {
            missing.Add("r");
        }

        if (population == null)
        {
            missing.Add("確認対象の数");
        }

        if (sensitivity == null)
        {
            missing.Add("検証役の感度");
        }

        if (missing.Count == 0)
        {
            missing.Add("r、確認対象の数、感度のいずれかが正の数でない");
        }

        return new EffectiveEpsilon(fallback, "fallback", r, Samples(fallback, sensitivity), "未定のもの（" + string.Join("、", missing) + "）があるため、仮の上限を使う");
    }

    private static long? Samples(double epsilon, double? sensitivity) =>
        epsilon > 0 ? SampleSizes.RuleOfThree(epsilon, sensitivity is { } s && s > 0 ? s : 1.0) : null;
}
