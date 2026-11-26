namespace Custos.Statistics;

/// <summary>Mills のエラーシーディング法の結果（第7.2節）。</summary>
/// <param name="Estimable">推定できたか（仕込んだバグが一つも見つかっていなければ推定できない）。</param>
/// <param name="DetectionRate">検出率 s/S。</param>
/// <param name="DetectionLower">検出率の下限（Wilson）。</param>
/// <param name="TotalNatural">自然発生のバグの総数の推定 n × S / s。</param>
/// <param name="RemainingNatural">残っている自然発生のバグの推定 n × S / s − n。</param>
/// <param name="RemainingUpper">残りの最大値（検出率の下限を使う）n × (1/下限 − 1)。</param>
/// <param name="Note">補足。</param>
public sealed record SeedingEstimate(
    bool Estimable,
    double? DetectionRate,
    double? DetectionLower,
    double? TotalNatural,
    double? RemainingNatural,
    double? RemainingUpper,
    string Note);

/// <summary>捕獲再捕獲法の結果（第8章）。</summary>
/// <param name="Estimable">推定できたか（両方が見つけたものが0件なら推定できない）。</param>
/// <param name="LincolnPetersen">A × B / C。</param>
/// <param name="Chapman">(A+1)(B+1)/(C+1) − 1（偏りの小さい推定）。</param>
/// <param name="ChapmanStandardError">Chapman の推定の標準誤差。</param>
/// <param name="DistinctFound">どちらかが見つけた件数 A + B − C。</param>
/// <param name="RemainingEstimate">残りの推定（Chapman − 見つけた件数）。</param>
/// <param name="IsLowerBound">同じ系統のAIどうしの場合、総数を少なく見積もるので下限として扱う（第21.11節）。</param>
public sealed record CaptureRecaptureEstimate(
    bool Estimable,
    double? LincolnPetersen,
    double? Chapman,
    double? ChapmanStandardError,
    int DistinctFound,
    double? RemainingEstimate,
    bool IsLowerBound);

/// <summary>Goel-Okumoto モデルの当てはめの結果（第10.2節）。</summary>
/// <param name="Estimable">推定できたか。</param>
/// <param name="A">バグの総数の期待値 a。</param>
/// <param name="B">発見率 b（ループ1回あたり）。</param>
/// <param name="FoundSoFar">これまでに見つかった数。</param>
/// <param name="ExpectedRemaining">残りの期待値 a − m(T)。</param>
/// <param name="Note">推定できない理由など。</param>
public sealed record GoelOkumotoFit(bool Estimable, double? A, double? B, double FoundSoFar, double? ExpectedRemaining, string Note)
{
    /// <summary>平均値関数 m(t) = a(1 − e^{−bt})。</summary>
    public double? Mean(double t) => Estimable && A is { } a && B is { } b ? a * (1 - Math.Exp(-b * t)) : null;
}

/// <summary>残存バグの推定（第7章、第8章、第10章）。</summary>
public static class Estimation
{
    /// <summary>
    /// Mills のエラーシーディング法。仕込んだ S 個のうち s 個が見つかり、自然発生のバグが n 個見つかったとき。
    /// 残りの最大値は、検出率の区間の下限（Wilson）を使う（第19.6節）。
    /// </summary>
    public static SeedingEstimate Mills(int seeded, int seededFound, int naturalFound, double confidence = 0.95)
    {
        if (seeded <= 0 || seededFound < 0 || seededFound > seeded || naturalFound < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seeded), "0 ≤ s ≤ S、S > 0、n ≥ 0 にしてください");
        }

        if (seededFound == 0)
        {
            return new SeedingEstimate(false, 0, 0, null, null, null, "仕込んだバグが一つも見つかっていないため、推定できない");
        }

        var rate = (double)seededFound / seeded;
        var lower = Proportions.Wilson(seededFound, seeded, confidence).Lower;
        var total = naturalFound / rate;
        var remaining = total - naturalFound;
        var remainingUpper = lower > 0 ? RemainingFromRate(naturalFound, lower) : double.PositiveInfinity;
        return new SeedingEstimate(true, rate, lower, total, remaining, remainingUpper, string.Empty);
    }

    /// <summary>
    /// 見つかった数と検出率から、残りの数を出す：見つかった数 × (1/検出率 − 1)。
    /// 第19.6節の例：20件、下限 83% → 約4件。
    /// </summary>
    public static double RemainingFromRate(double found, double detectionRate)
    {
        if (detectionRate is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(detectionRate), detectionRate, "検出率は 0 より大きく 1 以下にしてください");
        }

        return found * ((1 / detectionRate) - 1);
    }

    /// <summary>
    /// 捕獲再捕獲法。一方が A 件、もう一方が B 件のバグを見つけ、両方が見つけたものが C 件。
    /// </summary>
    /// <param name="foundByFirst">A。</param>
    /// <param name="foundBySecond">B。</param>
    /// <param name="foundByBoth">C。</param>
    /// <param name="sameFamily">二つの探索が同じ系統（同じモデル、似たプロンプト）か。</param>
    public static CaptureRecaptureEstimate CaptureRecapture(int foundByFirst, int foundBySecond, int foundByBoth, bool sameFamily)
    {
        if (foundByFirst < 0 || foundBySecond < 0 || foundByBoth < 0 || foundByBoth > Math.Min(foundByFirst, foundBySecond))
        {
            throw new ArgumentOutOfRangeException(nameof(foundByBoth), "0 ≤ C ≤ min(A, B) にしてください");
        }

        var distinct = foundByFirst + foundBySecond - foundByBoth;
        double a = foundByFirst;
        double b = foundBySecond;
        double c = foundByBoth;
        var chapman = (((a + 1) * (b + 1)) / (c + 1)) - 1;
        var variance = (a + 1) * (b + 1) * (a - c) * (b - c) / (((c + 1) * (c + 1)) * (c + 2));
        var standardError = Math.Sqrt(Math.Max(0, variance));
        if (foundByBoth == 0)
        {
            return new CaptureRecaptureEstimate(false, null, chapman, standardError, distinct, null, sameFamily);
        }

        var lincoln = a * b / c;
        return new CaptureRecaptureEstimate(true, lincoln, chapman, standardError, distinct, Math.Max(0, chapman - distinct), sameFamily);
    }

    /// <summary>
    /// Goel-Okumoto モデル m(t) = a(1 − e^{−bt}) を、ループごとの発見数に最尤推定で当てはめる。
    /// ループ i の時刻は i（1, 2, …）とする。b は対数の尺度で黄金分割探索する。
    /// 発見数が減っていない（境界で最大になる）場合は、推定できないとする。
    /// </summary>
    public static GoelOkumotoFit GoelOkumoto(IReadOnlyList<double> countsPerLoop)
    {
        ArgumentNullException.ThrowIfNull(countsPerLoop);
        var times = Enumerable.Range(1, countsPerLoop.Count).Select(i => (double)i).ToList();
        return GoelOkumoto(times, countsPerLoop);
    }

    /// <summary>時刻（累積の労力）を指定して当てはめる。times は増加列、counts は各区間の発見数。</summary>
    public static GoelOkumotoFit GoelOkumoto(IReadOnlyList<double> times, IReadOnlyList<double> counts)
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(counts);
        if (times.Count != counts.Count)
        {
            throw new ArgumentException("times と counts の長さをそろえてください", nameof(counts));
        }

        var total = counts.Sum();
        if (counts.Count < 3)
        {
            return new GoelOkumotoFit(false, null, null, total, null, "ループが3回未満のため推定できない");
        }

        if (total <= 0)
        {
            return new GoelOkumotoFit(false, null, null, total, null, "バグが一つも見つかっていないため推定できない");
        }

        for (var i = 0; i < times.Count; i++)
        {
            if (counts[i] < 0 || (i > 0 && times[i] <= times[i - 1]) || times[i] <= 0)
            {
                throw new ArgumentException("times は正の増加列、counts は 0 以上にしてください", nameof(times));
            }
        }

        var tEnd = times[^1];

        // 発見の重心（区間の中点を発見数で重み付けした平均）が T/2 以上なら、発見数が減る傾向がない。
        // このとき最尤推定の b は 0 に張り付き、a は発散する（ログの時刻が一様なら HPP の極限）。
        var centroid = 0.0;
        var start = 0.0;
        for (var i = 0; i < times.Count; i++)
        {
            centroid += counts[i] * (start + times[i]) / 2;
            start = times[i];
        }

        centroid /= total;
        if (centroid >= (tEnd / 2) * (1 - 1e-9))
        {
            return new GoelOkumotoFit(false, null, null, total, null, "発見数が減る傾向が見られないため推定できない（発見の重心が観測期間の中央以降）");
        }

        double ProfileLogLikelihood(double b)
        {
            var sum = 0.0;
            var previous = 0.0;
            for (var i = 0; i < times.Count; i++)
            {
                var delta = Math.Exp(-b * previous) - Math.Exp(-b * times[i]);
                if (counts[i] > 0)
                {
                    if (delta <= 0)
                    {
                        return double.NegativeInfinity;
                    }

                    sum += counts[i] * Math.Log(delta);
                }

                previous = times[i];
            }

            return sum - (total * Math.Log(-double.ExpM1(-b * tEnd)));
        }

        // b を ln b の尺度で探す
        var lo = Math.Log(1e-6 / tEnd);
        var hi = Math.Log(50.0 / times[0]);
        var phi = (Math.Sqrt(5) - 1) / 2;
        var x1 = hi - (phi * (hi - lo));
        var x2 = lo + (phi * (hi - lo));
        var f1 = ProfileLogLikelihood(Math.Exp(x1));
        var f2 = ProfileLogLikelihood(Math.Exp(x2));
        for (var i = 0; i < 300 && hi - lo > 1e-12; i++)
        {
            if (f1 < f2)
            {
                lo = x1;
                x1 = x2;
                f1 = f2;
                x2 = lo + (phi * (hi - lo));
                f2 = ProfileLogLikelihood(Math.Exp(x2));
            }
            else
            {
                hi = x2;
                x2 = x1;
                f2 = f1;
                x1 = hi - (phi * (hi - lo));
                f1 = ProfileLogLikelihood(Math.Exp(x1));
            }
        }

        var logB = 0.5 * (lo + hi);
        var lowerEdge = Math.Log(1e-6 / tEnd);
        if (logB - lowerEdge < 1e-3)
        {
            return new GoelOkumotoFit(false, null, null, total, null, "発見数が減る傾向が見られないため推定できない（b が 0 に近づき、a が発散する）");
        }

        var bHat = Math.Exp(logB);
        var aHat = total / -double.ExpM1(-bHat * tEnd);
        return new GoelOkumotoFit(true, aHat, bHat, total, aHat - total, string.Empty);
    }

    /// <summary>あと k 回のループで見つかる数の期待値 a(e^{−bT} − e^{−b(T+k)})。</summary>
    public static double? ExpectedFoundInNextLoops(GoelOkumotoFit fit, double currentTime, double loops)
    {
        ArgumentNullException.ThrowIfNull(fit);
        if (!fit.Estimable || fit.A is not { } a || fit.B is not { } b)
        {
            return null;
        }

        return a * (Math.Exp(-b * currentTime) - Math.Exp(-b * (currentTime + loops)));
    }

    /// <summary>残りの期待値が target 以下になるまでに必要なループの回数（すでに達していれば 0）。</summary>
    public static double? LoopsUntilRemaining(GoelOkumotoFit fit, double currentTime, double target)
    {
        ArgumentNullException.ThrowIfNull(fit);
        if (!fit.Estimable || fit.A is not { } a || fit.B is not { } b || target <= 0)
        {
            return null;
        }

        var needed = (Math.Log(a / target) / b) - currentTime;
        return Math.Max(0, Math.Ceiling(needed));
    }
}
