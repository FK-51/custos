namespace Custos.Statistics.Performance;

/// <summary>ブートストラップによる差の区間。</summary>
/// <param name="Observed">観測した差（比べる側 − 基準、または比 − 1）。</param>
/// <param name="Lower">区間の下限。</param>
/// <param name="Upper">区間の上限。</param>
/// <param name="Resamples">再標本化の回数。</param>
/// <param name="Seed">乱数のシード（同じシードなら同じ結果）。</param>
public sealed record BootstrapInterval(double Observed, double Lower, double Upper, int Resamples, ulong Seed);

/// <summary>
/// 決定的な擬似乱数（xoshiro256**、SplitMix64 で初期化）。
/// 実行環境の版に左右されず、同じシードで同じ列を返す。
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <summary>シードを指定して作る。</summary>
    public DeterministicRandom(ulong seed)
    {
        var x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        var z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>64ビットの値を返す。</summary>
    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>0 以上 maxExclusive 未満の整数を返す（偏りのない棄却法）。</summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "正の数にしてください");
        }

        var bound = (ulong)maxExclusive;
        var threshold = (0UL - bound) % bound;
        while (true)
        {
            var r = NextUInt64();
            if (r >= threshold)
            {
                return (int)(r % bound);
            }
        }
    }

    /// <summary>[0, 1) の小数を返す。</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));
}

/// <summary>ブートストラップ（第23.2節：分布の形に依存しない比較）。</summary>
public static class Bootstrap
{
    /// <summary>中央値。</summary>
    public static double Median(IReadOnlyList<double> values) => Descriptive.Median(values);

    /// <summary>99パーセンタイル。</summary>
    public static double P99(IReadOnlyList<double> values) => Descriptive.Percentile(values, 0.99);

    /// <summary>
    /// 統計量の差（比べる側 − 基準）の区間をパーセンタイル法で求める。
    /// relative が true なら、比（比べる側 ÷ 基準 − 1）の区間を求める。
    /// </summary>
    public static BootstrapInterval Difference(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        Func<IReadOnlyList<double>, double> statistic,
        bool relative = false,
        double confidence = 0.95,
        int resamples = 2000,
        ulong seed = 1)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(statistic);
        if (baseline.Count == 0 || candidate.Count == 0)
        {
            throw new ArgumentException("どちらの群にも値が必要です");
        }

        if (resamples < 100)
        {
            throw new ArgumentOutOfRangeException(nameof(resamples), resamples, "再標本化は100回以上にしてください");
        }

        double Compare(double b, double c) => relative ? (b == 0 ? double.NaN : (c / b) - 1) : c - b;

        var observed = Compare(statistic(baseline), statistic(candidate));
        var random = new DeterministicRandom(seed);
        var results = new double[resamples];
        var bufferB = new double[baseline.Count];
        var bufferC = new double[candidate.Count];
        for (var r = 0; r < resamples; r++)
        {
            for (var i = 0; i < bufferB.Length; i++)
            {
                bufferB[i] = baseline[random.NextInt(baseline.Count)];
            }

            for (var i = 0; i < bufferC.Length; i++)
            {
                bufferC[i] = candidate[random.NextInt(candidate.Count)];
            }

            results[r] = Compare(statistic(bufferB), statistic(bufferC));
        }

        var valid = results.Where(v => !double.IsNaN(v)).ToList();
        if (valid.Count == 0)
        {
            return new BootstrapInterval(observed, double.NaN, double.NaN, resamples, seed);
        }

        var alpha = 1 - confidence;
        return new BootstrapInterval(
            observed,
            Descriptive.Percentile(valid, alpha / 2),
            Descriptive.Percentile(valid, 1 - (alpha / 2)),
            resamples,
            seed);
    }
}
