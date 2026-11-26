namespace Custos.Statistics;

/// <summary>最適な誤り率の上限と抜き取り件数（第21.8節）。</summary>
/// <param name="EpsilonStar">ε* = √(3c / (s N L)) = √(3 / (s N r))。</param>
/// <param name="SamplesStar">n* = √(3 N r / s)。</param>
/// <param name="ResidualUpper">残るかもしれない誤りの上限 ε* × N。</param>
/// <param name="VerificationCost">検証の費用（c × n*）。検証1件の費用を1とした単位。</param>
/// <param name="ResidualLossUpper">残存損失の上限（ε* × N × L）。検証1件の費用を1とした単位。</param>
public sealed record OptimalSampling(double EpsilonStar, double SamplesStar, double ResidualUpper, double VerificationCost, double ResidualLossUpper);

/// <summary>
/// 抜き取り検証に必要な件数（設計書 第5.2節、第21.3節、第21.8節）。
/// </summary>
public static class SampleSizes
{
    /// <summary>
    /// 3の法則による件数 n ≈ 3/(ε s)。第21.3節の表（ε=1%、s=0.8 → 375件）はこの式。
    /// 99% 信頼なら係数 4.6 を渡す。
    /// </summary>
    public static long RuleOfThree(double epsilon, double sensitivity = 1.0, double factor = 3.0)
    {
        ValidateEpsilon(epsilon);
        ValidateSensitivity(sensitivity);
        return (long)Math.Ceiling((factor / (epsilon * sensitivity)) - 1e-9);
    }

    /// <summary>
    /// 誤りが一件も見つからなかったときに「誤り率 ≤ ε」と言うための厳密な件数。
    /// 検証役の感度 s を織り込み、(1 − ε s)^n ≤ 1 − 信頼水準 となる最小の n。
    /// </summary>
    public static long ZeroFailureExact(double epsilon, double sensitivity = 1.0, double confidence = 0.95)
    {
        ValidateEpsilon(epsilon);
        ValidateSensitivity(sensitivity);
        if (confidence is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "信頼水準は 0 より大きく 1 より小さくしてください");
        }

        var effective = epsilon * sensitivity;
        if (effective >= 1)
        {
            return 1;
        }

        return (long)Math.Ceiling((Math.Log(1 - confidence) / Math.Log(1 - effective)) - 1e-9);
    }

    /// <summary>
    /// 損失からの逆算（第21.8節）。抜き取りの費用 c × 3/(ε s) と残存損失の上限 ε N L の合計を最小にする。
    /// </summary>
    /// <param name="population">確認対象の数 N。</param>
    /// <param name="lossRatio">r = L / c（残ったバグ1件の損失 ÷ 検証1件の費用）。</param>
    /// <param name="sensitivity">検証役の感度 s。</param>
    public static OptimalSampling Optimal(double population, double lossRatio, double sensitivity)
    {
        if (population <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(population), population, "N は正の数にしてください");
        }

        if (lossRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lossRatio), lossRatio, "r は正の数にしてください");
        }

        ValidateSensitivity(sensitivity);
        var epsilon = Math.Sqrt(3 / (sensitivity * population * lossRatio));
        var samples = Math.Sqrt(3 * population * lossRatio / sensitivity);
        var residual = epsilon * population;
        return new OptimalSampling(Math.Min(1, epsilon), samples, residual, samples, residual * lossRatio);
    }

    private static void ValidateEpsilon(double epsilon)
    {
        if (epsilon is <= 0 or > 1 || double.IsNaN(epsilon))
        {
            throw new ArgumentOutOfRangeException(nameof(epsilon), epsilon, "ε は 0 より大きく 1 以下にしてください");
        }
    }

    private static void ValidateSensitivity(double sensitivity)
    {
        if (sensitivity is <= 0 or > 1 || double.IsNaN(sensitivity))
        {
            throw new ArgumentOutOfRangeException(nameof(sensitivity), sensitivity, "感度は 0 より大きく 1 以下にしてください");
        }
    }
}
