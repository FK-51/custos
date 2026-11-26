namespace Custos.Statistics.Performance;

/// <summary>記述統計（平均、標準偏差、変動係数、パーセンタイル）。</summary>
public static class Descriptive
{
    /// <summary>平均。</summary>
    public static double Mean(IReadOnlyList<double> values)
    {
        RequireNonEmpty(values);
        return values.Average();
    }

    /// <summary>標本標準偏差（n − 1 で割る）。値が1つなら 0。</summary>
    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        RequireNonEmpty(values);
        if (values.Count < 2)
        {
            return 0;
        }

        var mean = values.Average();
        var sum = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sum / (values.Count - 1));
    }

    /// <summary>変動係数（標準偏差 ÷ 平均）。平均が 0 なら無限大。</summary>
    public static double CoefficientOfVariation(IReadOnlyList<double> values)
    {
        var mean = Mean(values);
        if (mean == 0)
        {
            return double.PositiveInfinity;
        }

        return StandardDeviation(values) / Math.Abs(mean);
    }

    /// <summary>中央値。</summary>
    public static double Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    /// <summary>
    /// パーセンタイル（線形補間。R の type 7 と同じ）。q は 0〜1（99パーセンタイルなら 0.99）。
    /// </summary>
    public static double Percentile(IReadOnlyList<double> values, double q)
    {
        RequireNonEmpty(values);
        if (q is < 0 or > 1 || double.IsNaN(q))
        {
            throw new ArgumentOutOfRangeException(nameof(q), q, "q は 0〜1 にしてください");
        }

        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private static void RequireNonEmpty(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("値が1つもありません", nameof(values));
        }
    }
}
