using System.Globalization;

namespace Custos.Statistics;

/// <summary>
/// 層（第19.11節）。到達の難しさ R1（浅い）〜R3（深い）と、目立ち度 V1（高）〜V3（低）の組。
/// </summary>
/// <param name="Reach">到達の難しさ（1〜3）。</param>
/// <param name="Visibility">目立ち度（1〜3）。1が最も目立つ。</param>
public readonly record struct StratumId(int Reach, int Visibility)
{
    /// <summary>9つの層をすべて返す。</summary>
    public static IReadOnlyList<StratumId> All { get; } =
        [.. from r in Enumerable.Range(1, 3) from v in Enumerable.Range(1, 3) select new StratumId(r, v)];

    /// <summary>「浅い」かつ「目立ち度が高い」層。自明なので検出率の推定から外す（第19.11節）。</summary>
    public bool IsTrivial => Reach == 1 && Visibility == 1;

    /// <summary>表示名（例：R2V3）。</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"R{Reach}V{Visibility}");

    /// <summary>"R2V3" の形から作る。</summary>
    public static StratumId Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 4 && text[0] == 'R' && text[2] == 'V'
            && text[1] is >= '1' and <= '3' && text[3] is >= '1' and <= '3')
        {
            return new StratumId(text[1] - '0', text[3] - '0');
        }

        throw new FormatException($"層の名前を読めません：{text}（例：R2V3）");
    }
}

/// <summary>層ごとの観測（仕込んだ数、見つかった数、発生条件に到達した数）。</summary>
/// <param name="Id">層。</param>
/// <param name="Seeded">仕込んだ数。</param>
/// <param name="Found">見つかった数。</param>
/// <param name="Reached">探索が発生条件に到達していた数（分からなければ null）。</param>
public sealed record StratumObservation(StratumId Id, int Seeded, int Found, int? Reached = null);

/// <summary>層の推定の状態。</summary>
public enum StratumStatus
{
    /// <summary>R1V1。推定から外し、仕組みが動いていることの確認にだけ使う。</summary>
    Excluded,

    /// <summary>推定できた。</summary>
    Estimated,

    /// <summary>検出率が低く、残りを推定できない（第19.11節「推定の限界」）。</summary>
    Inestimable,

    /// <summary>仕込んだバグがない。</summary>
    NoData,
}

/// <summary>層（またはまとめた層の組）の推定。</summary>
/// <param name="Id">層。</param>
/// <param name="Group">一緒に推定した層（20個に満たない層は隣とまとめる）。</param>
/// <param name="Seeded">組の仕込んだ数。</param>
/// <param name="Found">組の見つかった数。</param>
/// <param name="Rate">検出率。</param>
/// <param name="Lower">検出率の下限。</param>
/// <param name="Upper">検出率の上限。</param>
/// <param name="Status">状態。</param>
/// <param name="Coverage">カバレッジ（到達した数 ÷ 仕込んだ数）。</param>
/// <param name="ObservationAccuracy">観測精度（見つかった数 ÷ 到達した数）。</param>
/// <param name="MechanismGap">目立ち度が高い層で検出率が100%に届かない（探索ではなく検査の仕組みの抜けとして扱う）。</param>
/// <param name="RealFound">この層で実際に見つかった実バグの数。</param>
/// <param name="RemainingPoint">残りの点推定。</param>
/// <param name="RemainingUpper">残りの最大値（検出率の下限を使う）。</param>
public sealed record StratumEstimate(
    StratumId Id,
    IReadOnlyList<StratumId> Group,
    int Seeded,
    int Found,
    double? Rate,
    double? Lower,
    double? Upper,
    StratumStatus Status,
    double? Coverage,
    double? ObservationAccuracy,
    bool MechanismGap,
    int RealFound,
    double? RemainingPoint,
    double? RemainingUpper);

/// <summary>層別の推定の全体。</summary>
/// <param name="Strata">層ごとの推定。</param>
/// <param name="OverallRate">事後層別で重み付けした全体の検出率。</param>
/// <param name="OverallLower">各層の下限を同じ重みで合わせた、全体の検出率の控えめな下限。</param>
/// <param name="RemainingPoint">推定できた層の残りの合計（点推定）。</param>
/// <param name="RemainingUpper">推定できた層の残りの合計（最大値）。</param>
/// <param name="InestimableWithRealBugs">推定不能な層のうち、実バグが見つかっている層（残りの数は分からない）。</param>
/// <param name="Notes">補足。</param>
public sealed record StratifiedEstimate(
    IReadOnlyList<StratumEstimate> Strata,
    double? OverallRate,
    double? OverallLower,
    double RemainingPoint,
    double RemainingUpper,
    IReadOnlyList<StratumId> InestimableWithRealBugs,
    IReadOnlyList<string> Notes);

/// <summary>
/// 層別の検出率（第19.2節、第19.11節）。
/// - R1V1 は推定から外す。
/// - 仕込んだ数が minimumPerStratum に満たない層は、隣の層とまとめる。
/// - 検出率の下限が inestimableBelow 未満の層は「推定不能」。
/// - 全体の検出率は、実際に見つかったバグの層の分布で重み付けする（事後層別）。実バグがなければ均等にする。
/// </summary>
public static class StratifiedDetection
{
    /// <summary>層別に推定する。</summary>
    public static StratifiedEstimate Estimate(
        IReadOnlyList<StratumObservation> observations,
        IReadOnlyDictionary<StratumId, int> realFoundByStratum,
        int minimumPerStratum = 20,
        double inestimableBelow = 0.30,
        double confidence = 0.95,
        bool useLowerBoundForInestimable = true)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(realFoundByStratum);
        var byId = new Dictionary<StratumId, StratumObservation>();
        foreach (var observation in observations)
        {
            if (observation.Seeded < 0 || observation.Found < 0 || observation.Found > observation.Seeded
                || observation.Reached is < 0 || (observation.Reached is { } reached && (reached > observation.Seeded || observation.Found > reached)))
            {
                throw new ArgumentException($"層 {observation.Id} の数が矛盾しています（0 ≤ 見つかった数 ≤ 到達した数 ≤ 仕込んだ数）", nameof(observations));
            }

            if (byId.TryGetValue(observation.Id, out var existing))
            {
                byId[observation.Id] = new StratumObservation(
                    observation.Id,
                    existing.Seeded + observation.Seeded,
                    existing.Found + observation.Found,
                    existing.Reached is { } r1 && observation.Reached is { } r2 ? r1 + r2 : null);
            }
            else
            {
                byId[observation.Id] = observation;
            }
        }

        var notes = new List<string>();
        var groups = BuildGroups(byId, minimumPerStratum, notes);
        var estimates = new List<StratumEstimate>();
        foreach (var id in StratumId.All)
        {
            var real = realFoundByStratum.TryGetValue(id, out var count) ? count : 0;
            var own = byId.TryGetValue(id, out var o) ? o : new StratumObservation(id, 0, 0);
            if (id.IsTrivial)
            {
                double? trivialRate = own.Seeded > 0 ? (double)own.Found / own.Seeded : null;
                var gap = own.Seeded > 0 && own.Found < own.Seeded;
                if (gap)
                {
                    notes.Add("R1V1（自明な層）で見逃しがある。検査の仕組みが動いていない疑い");
                }

                estimates.Add(new StratumEstimate(id, [id], own.Seeded, own.Found, trivialRate, null, null, StratumStatus.Excluded, Coverage(own), Accuracy(own), gap, real, null, null));
                continue;
            }

            var group = groups[id];
            var seeded = group.Sum(g => byId.TryGetValue(g, out var x) ? x.Seeded : 0);
            var found = group.Sum(g => byId.TryGetValue(g, out var x) ? x.Found : 0);
            if (seeded == 0)
            {
                estimates.Add(new StratumEstimate(id, group, 0, 0, null, null, null, StratumStatus.NoData, null, null, false, real, null, null));
                continue;
            }

            var interval = Proportions.Wilson(found, seeded, confidence);
            var judged = useLowerBoundForInestimable ? interval.Lower : interval.Estimate;
            var status = judged < inestimableBelow || found == 0 ? StratumStatus.Inestimable : StratumStatus.Estimated;
            var mechanismGap = id.Visibility == 1 && found < seeded;
            double? remainingPoint = null;
            double? remainingUpper = null;
            if (status == StratumStatus.Estimated)
            {
                remainingPoint = Estimation.RemainingFromRate(real, interval.Estimate);
                remainingUpper = interval.Lower > 0 ? Estimation.RemainingFromRate(real, interval.Lower) : double.PositiveInfinity;
            }

            var groupObservation = new StratumObservation(
                id,
                seeded,
                found,
                group.All(g => byId.TryGetValue(g, out var x) && x.Reached != null) ? group.Sum(g => byId[g].Reached ?? 0) : null);
            estimates.Add(new StratumEstimate(
                id,
                group,
                seeded,
                found,
                interval.Estimate,
                interval.Lower,
                interval.Upper,
                status,
                Coverage(groupObservation),
                Accuracy(groupObservation),
                mechanismGap,
                real,
                remainingPoint,
                remainingUpper));
        }

        // 事後層別：推定できた層を、実バグの分布で重み付けする（組でまとめた層は重複して数えない）
        var included = estimates.Where(e => e.Status == StratumStatus.Estimated).ToList();
        var totalReal = included.Sum(e => e.RealFound);
        double? overall = null;
        double? overallLower = null;
        if (included.Count > 0)
        {
            double weightSum = 0;
            double rate = 0;
            double lower = 0;
            foreach (var estimate in included)
            {
                var weight = totalReal > 0 ? estimate.RealFound : 1.0;
                weightSum += weight;
                rate += weight * (estimate.Rate ?? 0);
                lower += weight * (estimate.Lower ?? 0);
            }

            if (weightSum > 0)
            {
                overall = rate / weightSum;
                overallLower = lower / weightSum;
            }

            if (totalReal == 0)
            {
                notes.Add("実バグがまだないため、推定できた層を均等に重み付けした");
            }
        }

        var inestimableWithReal = estimates
            .Where(e => e.Status == StratumStatus.Inestimable && e.RealFound > 0)
            .Select(e => e.Id)
            .ToList();
        if (inestimableWithReal.Count > 0)
        {
            notes.Add("推定不能な層で実バグが見つかっている。探索を強化するか、捕獲再捕獲法で補う：" + string.Join(", ", inestimableWithReal));
        }

        return new StratifiedEstimate(
            estimates,
            overall,
            overallLower,
            included.Sum(e => e.RemainingPoint ?? 0),
            included.Sum(e => e.RemainingUpper ?? 0),
            inestimableWithReal,
            notes);
    }

    private static double? Coverage(StratumObservation observation) =>
        observation.Reached is { } reached && observation.Seeded > 0 ? (double)reached / observation.Seeded : null;

    private static double? Accuracy(StratumObservation observation) =>
        observation.Reached is { } reached && reached > 0 ? (double)observation.Found / reached : null;

    /// <summary>
    /// 仕込んだ数が足りない層を隣の層とまとめる。隣は、目立ち度が一つ下、一つ上、到達の難しさが一つ下、一つ上の順で探す。
    /// R1V1 はまとめる相手にしない。
    /// </summary>
    private static Dictionary<StratumId, IReadOnlyList<StratumId>> BuildGroups(
        Dictionary<StratumId, StratumObservation> byId,
        int minimum,
        List<string> notes)
    {
        var parent = new Dictionary<StratumId, StratumId>();
        foreach (var id in StratumId.All)
        {
            parent[id] = id;
        }

        StratumId Find(StratumId id)
        {
            while (parent[id] != id)
            {
                id = parent[id];
            }

            return id;
        }

        int GroupSeeded(StratumId root) => StratumId.All
            .Where(s => !s.IsTrivial && Find(s) == root)
            .Sum(s => byId.TryGetValue(s, out var o) ? o.Seeded : 0);

        foreach (var id in StratumId.All.Where(s => !s.IsTrivial))
        {
            var own = byId.TryGetValue(id, out var o) ? o.Seeded : 0;
            if (own == 0)
            {
                continue;
            }

            var neighbors = new[]
            {
                new StratumId(id.Reach, id.Visibility + 1),
                new StratumId(id.Reach, id.Visibility - 1),
                new StratumId(id.Reach + 1, id.Visibility),
                new StratumId(id.Reach - 1, id.Visibility),
            };
            foreach (var neighbor in neighbors)
            {
                if (GroupSeeded(Find(id)) >= minimum)
                {
                    break;
                }

                if (neighbor.Reach is < 1 or > 3 || neighbor.Visibility is < 1 or > 3 || neighbor.IsTrivial)
                {
                    continue;
                }

                if (!byId.TryGetValue(neighbor, out var n) || n.Seeded == 0)
                {
                    continue;
                }

                var a = Find(id);
                var b = Find(neighbor);
                if (a != b)
                {
                    parent[b] = a;
                    notes.Add($"{id} は仕込んだ数が {minimum} 個に満たないため、{neighbor} とまとめて推定した");
                }
            }
        }

        var groups = new Dictionary<StratumId, IReadOnlyList<StratumId>>();
        foreach (var id in StratumId.All)
        {
            if (id.IsTrivial)
            {
                groups[id] = [id];
                continue;
            }

            var root = Find(id);
            groups[id] = StratumId.All.Where(s => !s.IsTrivial && Find(s) == root).ToList();
        }

        return groups;
    }
}
