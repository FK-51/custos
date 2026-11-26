namespace Custos.Statistics.Performance;

/// <summary>対立仮説。</summary>
public enum Alternative
{
    /// <summary>両側（差がある）。</summary>
    TwoSided,

    /// <summary>比べる側（candidate）が基準（baseline）より大きい。応答時間なら「劣化」。</summary>
    Greater,

    /// <summary>比べる側が基準より小さい。</summary>
    Less,
}

/// <summary>Mann-Whitney の U 検定の結果。</summary>
/// <param name="U">比べる側の U（比べる側の値が基準の値より大きい組の数。同順位は 0.5）。</param>
/// <param name="PValue">p 値。</param>
/// <param name="Z">正規近似の z（厳密に計算した場合は null）。</param>
/// <param name="Exact">厳密な分布で計算したか。</param>
/// <param name="ProbabilityOfSuperiority">比べる側が基準より大きい確率の推定 U / (m n)。</param>
public sealed record MannWhitneyResult(double U, double PValue, double? Z, bool Exact, double ProbabilityOfSuperiority);

/// <summary>
/// 分布の形に依存しない比較（第23.2節：応答時間の分布は偏っているので、順位に基づく検定を使う）。
/// </summary>
public static class RankTests
{
    /// <summary>厳密な分布を使う、合計の標本数の上限（これを超えるか同順位があれば正規近似）。</summary>
    public const int ExactLimit = 50;

    /// <summary>Mann-Whitney の U 検定。</summary>
    /// <param name="baseline">基準のビルドの値。</param>
    /// <param name="candidate">比べるビルドの値。</param>
    /// <param name="alternative">対立仮説。</param>
    /// <param name="forceApproximation">true なら常に正規近似にする。</param>
    public static MannWhitneyResult MannWhitney(IReadOnlyList<double> baseline, IReadOnlyList<double> candidate, Alternative alternative, bool forceApproximation = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (baseline.Count == 0 || candidate.Count == 0)
        {
            throw new ArgumentException("どちらの群にも値が必要です");
        }

        var m = baseline.Count;
        var n = candidate.Count;
        var all = baseline.Select(v => (Value: v, Candidate: false))
            .Concat(candidate.Select(v => (Value: v, Candidate: true)))
            .OrderBy(x => x.Value)
            .ToList();
        var ranks = new double[all.Count];
        var tieCorrection = 0.0;
        var hasTies = false;
        var i = 0;
        while (i < all.Count)
        {
            var j = i;
            while (j + 1 < all.Count && all[j + 1].Value.Equals(all[i].Value))
            {
                j++;
            }

            var averageRank = ((i + 1) + (j + 1)) / 2.0;
            for (var k = i; k <= j; k++)
            {
                ranks[k] = averageRank;
            }

            var t = j - i + 1;
            if (t > 1)
            {
                hasTies = true;
                tieCorrection += ((double)t * t * t) - t;
            }

            i = j + 1;
        }

        var candidateRankSum = 0.0;
        for (var k = 0; k < all.Count; k++)
        {
            if (all[k].Candidate)
            {
                candidateRankSum += ranks[k];
            }
        }

        var u = candidateRankSum - (n * (n + 1) / 2.0);
        var superiority = u / ((double)m * n);
        if (!forceApproximation && !hasTies && m + n <= ExactLimit)
        {
            var distribution = ExactDistribution(m, n);
            var total = distribution.Sum();
            var uInt = (int)Math.Round(u);
            var upper = 0.0;
            var lower = 0.0;
            for (var k = 0; k < distribution.Length; k++)
            {
                if (k >= uInt)
                {
                    upper += distribution[k];
                }

                if (k <= uInt)
                {
                    lower += distribution[k];
                }
            }

            upper /= total;
            lower /= total;
            var p = alternative switch
            {
                Alternative.Greater => upper,
                Alternative.Less => lower,
                _ => Math.Min(1, 2 * Math.Min(upper, lower)),
            };
            return new MannWhitneyResult(u, p, null, true, superiority);
        }

        var nTotal = m + n;
        var mean = m * n / 2.0;
        var variance = m * n / 12.0 * ((nTotal + 1) - (tieCorrection / (nTotal * (nTotal - 1.0))));
        if (variance <= 0)
        {
            return new MannWhitneyResult(u, 1, 0, false, superiority);
        }

        var sigma = Math.Sqrt(variance);
        var zGreater = (u - mean - 0.5) / sigma;
        var zLess = (u - mean + 0.5) / sigma;
        var pGreater = 1 - SpecialFunctions.NormalCdf(zGreater);
        var pLess = SpecialFunctions.NormalCdf(zLess);
        var pValue = alternative switch
        {
            Alternative.Greater => pGreater,
            Alternative.Less => pLess,
            _ => Math.Min(1, 2 * Math.Min(pGreater, pLess)),
        };
        return new MannWhitneyResult(u, pValue, (u - mean) / sigma, false, superiority);
    }

    /// <summary>
    /// 同順位がない場合の U の厳密な分布（各 U の場合の数）。
    /// ガウスの二項係数 [m+n, m]_q = Π_{i=1}^{m} (1 − q^{n+i}) / (1 − q^i) の係数として計算する。
    /// </summary>
    public static double[] ExactDistribution(int m, int n)
    {
        if (m < 0 || n < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(m), "m と n は 0 以上にしてください");
        }

        var degree = m * n;
        var coefficients = new double[degree + 1];
        coefficients[0] = 1;
        var currentDegree = 0;
        for (var i = 1; i <= m; i++)
        {
            // (1 − q^{n+i}) を掛ける
            var shift = n + i;
            for (var k = Math.Min(degree, currentDegree + shift); k >= shift; k--)
            {
                coefficients[k] -= coefficients[k - shift];
            }

            currentDegree += shift;

            // (1 − q^i) で割る（割り切れる）
            for (var k = i; k <= Math.Min(degree, currentDegree); k++)
            {
                coefficients[k] += coefficients[k - i];
            }

            currentDegree -= i;
        }

        return coefficients;
    }
}
