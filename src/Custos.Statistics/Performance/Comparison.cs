namespace Custos.Statistics.Performance;

/// <summary>ビルド間の比較の結論。</summary>
public enum ComparisonOutcome
{
    /// <summary>差なし。</summary>
    NoDifference,

    /// <summary>劣化あり。</summary>
    Regression,

    /// <summary>改善。</summary>
    Improvement,

    /// <summary>計測の回数やばらつきのため、結論が出せない。</summary>
    Inconclusive,
}

/// <summary>ビルド間の比較の結果。</summary>
/// <param name="Outcome">結論。</param>
/// <param name="RelativeChange">中央値の比 − 1（劣化の方向を正にする）。</param>
/// <param name="PValueWorse">「比べる側が悪い」の片側 p 値。</param>
/// <param name="PValueBetter">「比べる側が良い」の片側 p 値。</param>
/// <param name="Reasons">結論の理由。</param>
public sealed record ComparisonResult(ComparisonOutcome Outcome, double RelativeChange, double PValueWorse, double PValueBetter, IReadOnlyList<string> Reasons);

/// <summary>
/// ビルド間の比較（第13.1節の比較判定、第23.2節、第23.3節）と、ばらつきの許容幅。
/// </summary>
public static class Comparison
{
    /// <summary>
    /// 第23.2節の表：検出したい劣化の大きさ δ と各ビルドの計測回数 n から、許容できる変動係数の上限。
    /// 二標本 t 検定（両側 α、検出力 power、自由度 2n−2）で δ を検出するための条件
    /// δ / CV ≥ (t_{1−α/2} + t_{power}) × √(2/n) から求める。
    /// </summary>
    public static double MaxCoefficientOfVariation(double detectableRegression, int runsPerBuild, double alpha = 0.05, double power = 0.80)
    {
        if (detectableRegression <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(detectableRegression), detectableRegression, "検出したい劣化は正の数にしてください");
        }

        if (runsPerBuild < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(runsPerBuild), runsPerBuild, "計測回数は2回以上にしてください");
        }

        var df = (2.0 * runsPerBuild) - 2;
        var tAlpha = SpecialFunctions.StudentTQuantile(1 - (alpha / 2), df);
        var tPower = SpecialFunctions.StudentTQuantile(power, df);
        return detectableRegression / ((tAlpha + tPower) * Math.Sqrt(2.0 / runsPerBuild));
    }

    /// <summary>変動係数 cv のもとで δ を検出するのに必要な各ビルドの計測回数（上限 maxRuns まで探す。見つからなければ null）。</summary>
    public static int? RunsRequired(double detectableRegression, double coefficientOfVariation, double alpha = 0.05, double power = 0.80, int maxRuns = 1000)
    {
        for (var n = 2; n <= maxRuns; n++)
        {
            if (MaxCoefficientOfVariation(detectableRegression, n, alpha, power) >= coefficientOfVariation)
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>
    /// 基準のビルドと比べるビルドの、計測ごとの値（例：計測ごとの99パーセンタイル）を比べる。
    /// 値が大きいほど悪い指標（応答時間など）を想定する。容量のように大きいほど良い指標は higherIsWorse を false にする。
    /// 片側の Mann-Whitney 検定で有意（p &lt; α）かつ、中央値の変化が minimumRelativeChange 以上のときに「劣化あり」とする。
    /// </summary>
    public static ComparisonResult Compare(
        IReadOnlyList<double> baselineRuns,
        IReadOnlyList<double> candidateRuns,
        double detectableRegression,
        int minimumRuns,
        double maxCoefficientOfVariation,
        bool higherIsWorse = true,
        double alpha = 0.05,
        double? minimumRelativeChange = null)
    {
        ArgumentNullException.ThrowIfNull(baselineRuns);
        ArgumentNullException.ThrowIfNull(candidateRuns);
        var reasons = new List<string>();
        if (baselineRuns.Count < minimumRuns || candidateRuns.Count < minimumRuns)
        {
            reasons.Add($"計測回数が足りない（基準 {baselineRuns.Count} 回、比べる側 {candidateRuns.Count} 回、必要 {minimumRuns} 回）");
            return new ComparisonResult(ComparisonOutcome.Inconclusive, double.NaN, double.NaN, double.NaN, reasons);
        }

        var cvBaseline = Descriptive.CoefficientOfVariation(baselineRuns);
        var cvCandidate = Descriptive.CoefficientOfVariation(candidateRuns);
        if (cvBaseline > maxCoefficientOfVariation || cvCandidate > maxCoefficientOfVariation)
        {
            reasons.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ばらつきが許容幅を超えている（変動係数 基準 {cvBaseline:P1}、比べる側 {cvCandidate:P1}、上限 {maxCoefficientOfVariation:P1}）"));
            return new ComparisonResult(ComparisonOutcome.Inconclusive, double.NaN, double.NaN, double.NaN, reasons);
        }

        var medianBaseline = Descriptive.Median(baselineRuns);
        var medianCandidate = Descriptive.Median(candidateRuns);
        var change = medianBaseline == 0 ? double.NaN : (medianCandidate / medianBaseline) - 1;
        var worseChange = higherIsWorse ? change : -change;
        var worseAlternative = higherIsWorse ? Alternative.Greater : Alternative.Less;
        var betterAlternative = higherIsWorse ? Alternative.Less : Alternative.Greater;
        var pWorse = RankTests.MannWhitney(baselineRuns, candidateRuns, worseAlternative).PValue;
        var pBetter = RankTests.MannWhitney(baselineRuns, candidateRuns, betterAlternative).PValue;
        var practical = minimumRelativeChange ?? (detectableRegression / 2);
        if (pWorse < alpha && worseChange >= practical)
        {
            reasons.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"劣化あり（中央値の変化 {worseChange:P1}、p = {pWorse:G3}）"));
            return new ComparisonResult(ComparisonOutcome.Regression, worseChange, pWorse, pBetter, reasons);
        }

        if (pBetter < alpha && -worseChange >= practical)
        {
            reasons.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"改善（中央値の変化 {worseChange:P1}、p = {pBetter:G3}）"));
            return new ComparisonResult(ComparisonOutcome.Improvement, worseChange, pWorse, pBetter, reasons);
        }

        reasons.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"差なし（中央値の変化 {worseChange:P1}、p = {pWorse:G3}）"));
        return new ComparisonResult(ComparisonOutcome.NoDifference, worseChange, pWorse, pBetter, reasons);
    }
}
