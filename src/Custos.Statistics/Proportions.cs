namespace Custos.Statistics;

/// <summary>比率の区間。</summary>
/// <param name="Estimate">点推定（k/n）。</param>
/// <param name="Lower">下限。</param>
/// <param name="Upper">上限。</param>
public sealed record ProportionInterval(double Estimate, double Lower, double Upper);

/// <summary>
/// 比率（誤り率、検出率、感度）の上限と区間（設計書 第5章、第19.6節、第21章）。
/// </summary>
public static class Proportions
{
    /// <summary>
    /// Clopper-Pearson の両側区間（厳密な二項分布による）。
    /// </summary>
    /// <param name="successes">成功（誤り、検出など）の件数 k。</param>
    /// <param name="trials">試行の件数 n。</param>
    /// <param name="confidence">信頼水準（例：0.95）。</param>
    public static ProportionInterval ClopperPearson(int successes, int trials, double confidence = 0.95)
    {
        Validate(successes, trials, confidence);
        var alpha = 1 - confidence;
        var k = successes;
        var n = trials;
        var lower = k == 0 ? 0 : SpecialFunctions.InverseRegularizedIncompleteBeta(alpha / 2, k, n - k + 1);
        var upper = k == n ? 1 : SpecialFunctions.InverseRegularizedIncompleteBeta(1 - (alpha / 2), k + 1, n - k);
        return new ProportionInterval((double)k / n, lower, upper);
    }

    /// <summary>
    /// Clopper-Pearson の片側上限。誤り率の上限に使う（誤りが k 件見つかったときに、信頼水準で言える上限）。
    /// k = 0 のときは 1 − (1−信頼水準)^(1/n) と同じ。
    /// </summary>
    public static double ClopperPearsonUpper(int successes, int trials, double confidence = 0.95)
    {
        Validate(successes, trials, confidence);
        if (successes == trials)
        {
            return 1;
        }

        return SpecialFunctions.InverseRegularizedIncompleteBeta(confidence, successes + 1, trials - successes);
    }

    /// <summary>Clopper-Pearson の片側下限。</summary>
    public static double ClopperPearsonLower(int successes, int trials, double confidence = 0.95)
    {
        Validate(successes, trials, confidence);
        if (successes == 0)
        {
            return 0;
        }

        return SpecialFunctions.InverseRegularizedIncompleteBeta(1 - confidence, successes, trials - successes + 1);
    }

    /// <summary>
    /// Wilson のスコア区間（両側）。検出率の区間（第19.6節の表）はこの方法で計算している。
    /// </summary>
    public static ProportionInterval Wilson(int successes, int trials, double confidence = 0.95)
    {
        Validate(successes, trials, confidence);
        var n = (double)trials;
        var p = successes / n;
        var z = SpecialFunctions.NormalQuantile(1 - ((1 - confidence) / 2));
        var z2 = z * z;
        var denominator = 1 + (z2 / n);
        var center = (p + (z2 / (2 * n))) / denominator;
        var half = z * Math.Sqrt((p * (1 - p) / n) + (z2 / (4 * n * n))) / denominator;
        return new ProportionInterval(p, Math.Max(0, center - half), Math.Min(1, center + half));
    }

    /// <summary>
    /// 誤りが一件も見つからなかったときの、誤り率の厳密な上限 1 − (1−信頼水準)^(1/n)。
    /// </summary>
    public static double ZeroFailureUpper(int trials, double confidence = 0.95)
    {
        Validate(0, trials, confidence);
        return 1 - Math.Pow(1 - confidence, 1.0 / trials);
    }

    /// <summary>
    /// 誤りが一件も見つからなかったときの上限の近似 −ln(1−信頼水準)/n。
    /// 95% なら約 3/n（3の法則）、99% なら約 4.6/n（第5.2節）。
    /// </summary>
    public static double ZeroFailureUpperApprox(int trials, double confidence = 0.95)
    {
        Validate(0, trials, confidence);
        return -Math.Log(1 - confidence) / trials;
    }

    /// <summary>
    /// 誤りの上限。検証役の感度 s（誤りを指摘できる割合）を織り込む（第21.3節）。
    /// 見つかった誤り k 件、検証した件数 n のとき、真の誤り率の上限 = 上限(k, n) / s。
    /// </summary>
    public static double ErrorRateUpperBound(int errorsFound, int verified, double confidence = 0.95, double sensitivity = 1.0)
    {
        if (sensitivity is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sensitivity), sensitivity, "感度は 0 より大きく 1 以下にしてください");
        }

        return Math.Min(1, ClopperPearsonUpper(errorsFound, verified, confidence) / sensitivity);
    }

    /// <summary>
    /// 第5.1節：N件のうち D 件が誤りのとき、無作為に n 件を抜き取って誤りが一件も見つからない確率の近似 (1 − n/N)^D。
    /// </summary>
    public static double MissProbabilityApprox(long population, long sampled, long errors)
    {
        ValidatePopulation(population, sampled);
        if (errors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(errors), errors, "誤りの件数は 0 以上にしてください");
        }

        return Math.Pow(1 - ((double)sampled / population), errors);
    }

    /// <summary>
    /// 第5.1節：同じ確率の厳密値（超幾何分布）。C(N−D, n) / C(N, n)。
    /// </summary>
    public static double MissProbabilityExact(long population, long sampled, long errors)
    {
        ValidatePopulation(population, sampled);
        if (errors < 0 || errors > population)
        {
            throw new ArgumentOutOfRangeException(nameof(errors), errors, "誤りの件数は 0 以上 N 以下にしてください");
        }

        if (errors > population - sampled)
        {
            return 0;
        }

        var logP = 0.0;
        for (long i = 0; i < errors; i++)
        {
            logP += Math.Log((double)(population - sampled - i) / (population - i));
        }

        return Math.Exp(logP);
    }

    /// <summary>
    /// 第5.1節：「誤りが1件でも残っている」を有意水準 p で棄却するのに必要な検証件数 n ≥ (1 − p) × N。
    /// </summary>
    public static long RequiredForZeroErrors(long population, double significance)
    {
        if (population <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(population), population, "N は正の数にしてください");
        }

        if (significance is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(significance), significance, "有意水準は 0 より大きく 1 より小さくしてください");
        }

        return (long)Math.Ceiling(((1 - significance) * population) - 1e-9);
    }

    /// <summary>
    /// 第5.1節：一様事前分布を置いたベイズ推定で、未検証の M 件に誤りが含まれる確率 M / (N + 1)。
    /// （N 件のうち N − M 件を検証して誤りがなかった場合）
    /// </summary>
    public static double BayesResidualErrorProbability(long population, long unverified)
    {
        if (population <= 0 || unverified < 0 || unverified > population)
        {
            throw new ArgumentOutOfRangeException(nameof(unverified), unverified, "0 ≤ M ≤ N にしてください");
        }

        return (double)unverified / (population + 1);
    }

    /// <summary>
    /// 第5.2節：N 件から n 件を無作為に検証して誤りが一件も見つからなかったとき、信頼水準で言える残りの誤りの件数の上限。
    /// 「D 件の誤りがあっても見逃す確率が 1 − 信頼水準 を超える」最大の D を返す。
    /// </summary>
    /// <param name="population">母数 N。</param>
    /// <param name="sampled">検証した件数 n。</param>
    /// <param name="confidence">信頼水準。</param>
    /// <param name="exact">true なら超幾何分布で、false なら (1 − n/N)^D の近似で計算する。</param>
    public static long ResidualUpperBound(long population, long sampled, double confidence = 0.95, bool exact = false)
    {
        ValidatePopulation(population, sampled);
        var alpha = 1 - confidence;
        if (sampled == 0)
        {
            return population;
        }

        if (!exact)
        {
            var x = Math.Log(alpha) / Math.Log(1 - ((double)sampled / population));
            if (double.IsNaN(x) || double.IsInfinity(x))
            {
                return 0;
            }

            var rounded = Math.Round(x);
            var ceiling = Math.Abs(x - rounded) < 1e-9 ? rounded : Math.Ceiling(x);
            return Math.Max(0, (long)ceiling - 1);
        }

        // 厳密：P(見逃す | D) = Π_{i<D} (N−n−i)/(N−i) が alpha を超える最大の D
        var logP = 0.0;
        var logAlpha = Math.Log(alpha);
        long d = 0;
        while (d < population - sampled)
        {
            var next = logP + Math.Log((double)(population - sampled - d) / (population - d));
            if (next <= logAlpha)
            {
                break;
            }

            logP = next;
            d++;
        }

        return d;
    }

    private static void Validate(int successes, int trials, double confidence)
    {
        if (trials <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trials), trials, "試行の件数は正の数にしてください");
        }

        if (successes < 0 || successes > trials)
        {
            throw new ArgumentOutOfRangeException(nameof(successes), successes, "件数は 0 以上、試行の件数以下にしてください");
        }

        if (confidence is <= 0 or >= 1 || double.IsNaN(confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "信頼水準は 0 より大きく 1 より小さくしてください");
        }
    }

    private static void ValidatePopulation(long population, long sampled)
    {
        if (population <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(population), population, "N は正の数にしてください");
        }

        if (sampled < 0 || sampled > population)
        {
            throw new ArgumentOutOfRangeException(nameof(sampled), sampled, "n は 0 以上 N 以下にしてください");
        }
    }
}
