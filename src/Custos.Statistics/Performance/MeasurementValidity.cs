using System.Globalization;
using Custos.Core.Config;

namespace Custos.Statistics.Performance;

/// <summary>計測1回の判定。</summary>
public enum MeasurementState
{
    /// <summary>有効。</summary>
    Valid,

    /// <summary>無効。合格でも不合格でもない。測り直す（第23.4節）。</summary>
    Invalid,

    /// <summary>エラー率が1%を超えた。応答時間は評価せず、SLO違反として扱う（第23.1節）。</summary>
    SloViolation,
}

/// <summary>計測1回の観測値（第23.1節の各項目）。</summary>
/// <param name="LoadGeneratorCpuMax">負荷を出す側のCPU使用率の最大（0〜1）。</param>
/// <param name="SendLagP99Ms">予定した送信時刻からの遅れの99パーセンタイル（ミリ秒）。</param>
/// <param name="SloThresholdMs">この計測で評価するSLOの閾値（ミリ秒）。送信の遅れの上限の基準になる。</param>
/// <param name="ReferenceWorkloadRatio">基準ワークロードの結果 ÷ そのホストの基準値（1.0 ならずれなし）。</param>
/// <param name="SwapOccurred">スワップが発生したか。</param>
/// <param name="CpuStealRatio">他の利用者にCPU時間を取られた割合（0〜1。計算機を共有しない場合は null）。</param>
/// <param name="FirstHalf">計測区間の前半の値（立ち上がりを除いた後）。</param>
/// <param name="SecondHalf">計測区間の後半の値。</param>
/// <param name="ErrorRate">エラー率（0〜1）。</param>
/// <param name="WarmupExcluded">立ち上がりを集計から除いたか。</param>
public sealed record MeasurementObservation(
    double LoadGeneratorCpuMax,
    double? SendLagP99Ms,
    double? SloThresholdMs,
    double? ReferenceWorkloadRatio,
    bool SwapOccurred,
    double? CpuStealRatio,
    IReadOnlyList<double>? FirstHalf,
    IReadOnlyList<double>? SecondHalf,
    double ErrorRate,
    bool WarmupExcluded = true);

/// <summary>計測1回の判定の結果。</summary>
/// <param name="State">判定。</param>
/// <param name="Reasons">無効やSLO違反の理由。</param>
/// <param name="Warnings">無効ではないが報告すること（メモリリークの疑いなど）。</param>
public sealed record MeasurementVerdict(MeasurementState State, IReadOnlyList<string> Reasons, IReadOnlyList<string> Warnings);

/// <summary>複数回の計測の判定。</summary>
/// <param name="Comparable">比較の結論を出せるか。</param>
/// <param name="CoefficientOfVariation">変動係数。</param>
/// <param name="Reasons">出せない理由。</param>
public sealed record SeriesVerdict(bool Comparable, double? CoefficientOfVariation, IReadOnlyList<string> Reasons);

/// <summary>
/// 計測の妥当性（第16.7節、第23.1節、第23.4節）。
/// </summary>
public static class MeasurementValidity
{
    /// <summary>この割合を超えるエラー率の計測は、応答時間を評価しない（第23.1節）。</summary>
    public const double ErrorRateLimit = 0.01;

    /// <summary>この回数続けて無効なら、環境の問題として扱う（第23.4節）。</summary>
    public const int EnvironmentProblemStreak = 3;

    /// <summary>
    /// 計測1回を判定する。定常状態の許容幅は、指定がなければ「検出したい劣化の半分」とする（初期値。第23.3節の陰性の確認で調整する）。
    /// </summary>
    public static MeasurementVerdict Evaluate(MeasurementObservation observation, ValidityCriteria criteria, double? steadyStateTolerance = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(criteria);
        var reasons = new List<string>();
        var warnings = new List<string>();
        var c = CultureInfo.InvariantCulture;

        if (!observation.WarmupExcluded)
        {
            reasons.Add("立ち上がりを集計から除いていない");
        }

        if (observation.LoadGeneratorCpuMax > criteria.LoadCpuMax)
        {
            reasons.Add(string.Create(c, $"負荷を出す側のCPU使用率が {observation.LoadGeneratorCpuMax:P0} で、上限 {criteria.LoadCpuMax:P0} を超えた"));
        }

        if (observation.SendLagP99Ms is { } lag)
        {
            if (observation.SloThresholdMs is { } slo && slo > 0)
            {
                var limit = slo * criteria.SendLagP99Max;
                if (lag > limit)
                {
                    reasons.Add(string.Create(c, $"送信の遅れの99パーセンタイル {lag:F1}ms が、SLOの閾値の {criteria.SendLagP99Max:P0}（{limit:F1}ms）を超えた"));
                }
            }
            else
            {
                warnings.Add("SLOの閾値が分からないため、送信の遅れを判定できない");
            }
        }

        if (observation.ReferenceWorkloadRatio is { } ratio)
        {
            var drift = Math.Abs(ratio - 1);
            if (drift >= criteria.ReferenceDriftMax)
            {
                reasons.Add(string.Create(c, $"基準ワークロードがホストの基準値から {drift:P1} ずれた（上限 {criteria.ReferenceDriftMax:P1}）"));
            }
        }

        if (observation.SwapOccurred && !criteria.SwapAllowed)
        {
            reasons.Add("スワップが発生した");
        }

        if (observation.CpuStealRatio is { } steal && steal > criteria.CpuStealMax)
        {
            reasons.Add(string.Create(c, $"CPUの横取りが {steal:P1} で、上限 {criteria.CpuStealMax:P1} を超えた"));
        }

        if (criteria.SteadyStateCheck && observation.FirstHalf is { Count: > 0 } first && observation.SecondHalf is { Count: > 0 } second)
        {
            var m1 = Descriptive.Median(first);
            var m2 = Descriptive.Median(second);
            var tolerance = steadyStateTolerance ?? (criteria.DetectRegression / 2);
            if (m1 > 0)
            {
                var change = (m2 - m1) / m1;
                if (Math.Abs(change) > tolerance)
                {
                    reasons.Add(string.Create(c, $"計測区間の前半と後半で値が {change:P1} 違う（許容幅 {tolerance:P1}）。立ち上がりの途中か、値が流れている"));
                    if (change > 0)
                    {
                        warnings.Add("後半が悪化し続けている。メモリリークなどの疑い");
                    }
                }
            }
        }

        if (reasons.Count > 0)
        {
            return new MeasurementVerdict(MeasurementState.Invalid, reasons, warnings);
        }

        if (observation.ErrorRate > ErrorRateLimit)
        {
            return new MeasurementVerdict(
                MeasurementState.SloViolation,
                [string.Create(c, $"エラー率 {observation.ErrorRate:P2} が {ErrorRateLimit:P0} を超えた。失敗は速く返るので応答時間は評価しない")],
                warnings);
        }

        return new MeasurementVerdict(MeasurementState.Valid, [], warnings);
    }

    /// <summary>
    /// 複数回の計測の値（例：計測ごとの99パーセンタイル）が、比較に使える回数とばらつきかを判定する。
    /// </summary>
    public static SeriesVerdict EvaluateSeries(IReadOnlyList<double> runValues, ValidityCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(runValues);
        ArgumentNullException.ThrowIfNull(criteria);
        var reasons = new List<string>();
        var c = CultureInfo.InvariantCulture;
        if (runValues.Count < criteria.Runs)
        {
            reasons.Add(string.Create(c, $"計測回数が {runValues.Count} 回で、必要な {criteria.Runs} 回に満たない"));
        }

        double? cv = null;
        if (runValues.Count >= 2)
        {
            cv = Descriptive.CoefficientOfVariation(runValues);
            if (cv > criteria.MaxCv)
            {
                reasons.Add(string.Create(c, $"変動係数が {cv:P1} で、許容幅 {criteria.MaxCv:P1} を超えた"));
            }
        }

        return new SeriesVerdict(reasons.Count == 0, cv, reasons);
    }

    /// <summary>直近の判定が、続けて無効になっているか（3回続けて無効なら、性能についてのリリース判定を出さない）。</summary>
    public static bool IsEnvironmentProblem(IReadOnlyList<MeasurementState> chronologicalStates)
    {
        ArgumentNullException.ThrowIfNull(chronologicalStates);
        if (chronologicalStates.Count < EnvironmentProblemStreak)
        {
            return false;
        }

        return chronologicalStates.Skip(chronologicalStates.Count - EnvironmentProblemStreak).All(s => s == MeasurementState.Invalid);
    }
}
