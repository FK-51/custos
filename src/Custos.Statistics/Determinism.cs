namespace Custos.Statistics;

/// <summary>
/// 決定性がない場合の計算（第22章）。
/// </summary>
public static class Determinism
{
    /// <summary>
    /// カナリアの判別（第22.5節）：パッチありでの再現率 p のとき、パッチなしで何回出なければ
    /// 「カナリアだ」と言えるか（本物のバグを誤認する確率を maxMisidentification 以下にする）。
    /// k = ceil(ln(誤認の上限) / ln(1 − p))。p ≤ 0 なら判別できない（null）。
    /// </summary>
    public static int? CanaryRerunsRequired(double reproductionRate, double maxMisidentification = 0.05)
    {
        if (maxMisidentification is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMisidentification), maxMisidentification, "誤認の上限は 0 より大きく 1 より小さくしてください");
        }

        if (reproductionRate <= 0 || double.IsNaN(reproductionRate))
        {
            return null;
        }

        if (reproductionRate >= 1)
        {
            return 1;
        }

        return (int)Math.Ceiling((Math.Log(maxMisidentification) / Math.Log(1 - reproductionRate)) - 1e-9);
    }

    /// <summary>
    /// カナリアの判別の結果：パッチなしで reruns 回実行して一度も出なかった場合に、カナリアと言えるか。
    /// 判別できない（再現率が 0、回数が足りない、パッチなしでも出た）ときは、本物のバグとして扱う（第22.5節）。
    /// </summary>
    public static bool IsCanary(double reproductionRateWithPatch, int rerunsWithoutPatch, int occurrencesWithoutPatch, double maxMisidentification = 0.05)
    {
        if (occurrencesWithoutPatch > 0)
        {
            return false;
        }

        var required = CanaryRerunsRequired(reproductionRateWithPatch, maxMisidentification);
        return required is { } k && rerunsWithoutPatch >= k;
    }

    /// <summary>
    /// 範囲で比べる場合の誤検出の確率の上限 2/(N+1)（第22.4節）。
    /// 正直なクライアントで N 回実行した最小値〜最大値の外に、正常な値が出る確率。
    /// </summary>
    public static double EnvelopeFalsePositive(int honestRuns)
    {
        if (honestRuns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(honestRuns), honestRuns, "回数は1以上にしてください");
        }

        return 2.0 / (honestRuns + 1);
    }

    /// <summary>誤検出の確率を target 以下にするのに必要な回数 N = ceil(2/target − 1)（19回→10%、39回→5%、199回→1%）。</summary>
    public static int EnvelopeRunsRequired(double targetFalsePositive)
    {
        if (targetFalsePositive is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFalsePositive), targetFalsePositive, "誤検出の確率は 0 より大きく 1 以下にしてください");
        }

        return Math.Max(1, (int)Math.Ceiling((2 / targetFalsePositive) - 1 - 1e-9));
    }

    /// <summary>
    /// k回試して一度も起きなかったときの発生率の上限（第22.6節）。近似は 3/k（95%）、厳密値は 1 − (1−信頼水準)^(1/k)。
    /// </summary>
    public static double NoOccurrenceUpperBound(int trials, double confidence = 0.95, bool exact = false) =>
        exact ? Proportions.ZeroFailureUpper(trials, confidence) : Math.Min(1, Proportions.ZeroFailureUpperApprox(trials, confidence));

    /// <summary>
    /// 範囲での比較（第22.4節）：honestValues の最小値〜最大値の外に value が出たか。候補検出（段階2）として扱う。
    /// </summary>
    public static bool OutsideEnvelope(IReadOnlyList<double> honestValues, double value)
    {
        ArgumentNullException.ThrowIfNull(honestValues);
        if (honestValues.Count == 0)
        {
            throw new ArgumentException("正直なクライアントでの値が必要です", nameof(honestValues));
        }

        return value < honestValues.Min() || value > honestValues.Max();
    }
}
