namespace Custos.Registry.Bugs;

/// <summary>校正への使い方（設計書 第20.8節）。</summary>
public enum CalibrationUse
{
    /// <summary>使わない（測定対象と同じ探索AIが見つけたもの）。</summary>
    Excluded,

    /// <summary>使える。ただし、AIにとって見つけやすいバグに偏る（別系統の探索AIが見つけたもの）。</summary>
    UsableBiasedTowardAi,

    /// <summary>使える。ただし、早い段階で捕まる浅いバグに偏る（実装中、環境を作る途中）。</summary>
    UsableBiasedShallow,

    /// <summary>最も価値が高い。検証が見逃したものそのもの（本番ですり抜けたバグ）。答え合わせにも使う。</summary>
    MostValuable,
}

/// <summary>判定と理由。</summary>
public sealed record CalibrationUseDecision(CalibrationUse Use, string Reason);

/// <summary>
/// 見つけた経路による偏りを判定する。found_by を必須にしているのはこのため（第20.8節）。
/// </summary>
public static class CalibrationUsePolicy
{
    /// <summary>
    /// 校正に使えるかを決める。measuredExplorer は、検出率を測る対象の探索AI（loop: の後ろと同じ書き方。例：claude-code/model-a@2026-11）。
    /// </summary>
    public static CalibrationUseDecision Decide(BugRecord bug, string measuredExplorer)
    {
        ArgumentNullException.ThrowIfNull(bug);
        return Decide(bug.FoundBy, bug.FoundStage, measuredExplorer);
    }

    /// <summary>found_by と found_stage から決める。</summary>
    public static CalibrationUseDecision Decide(string foundBy, string foundStage, string measuredExplorer)
    {
        ArgumentNullException.ThrowIfNull(foundBy);
        ArgumentNullException.ThrowIfNull(foundStage);
        ArgumentException.ThrowIfNullOrWhiteSpace(measuredExplorer);
        if (foundBy == "production" || foundStage == "production")
        {
            return new CalibrationUseDecision(CalibrationUse.MostValuable, "本番ですり抜けたバグ。検証が見逃したものそのもので、答え合わせにも使う");
        }

        if (foundBy.StartsWith("loop:", StringComparison.Ordinal))
        {
            var finder = foundBy["loop:".Length..].Trim();
            if (string.Equals(finder, measuredExplorer.Trim(), StringComparison.Ordinal))
            {
                return new CalibrationUseDecision(CalibrationUse.Excluded, "測定対象と同じ探索AIが見つけたバグ。再注入すれば見つかるのは当然で、検出率が高く出る");
            }

            return new CalibrationUseDecision(CalibrationUse.UsableBiasedTowardAi, $"別系統の探索AI（{finder}）が見つけたバグ。AIにとって見つけやすいバグに偏る");
        }

        if (foundBy == "setup")
        {
            return new CalibrationUseDecision(CalibrationUse.UsableBiasedShallow, "環境を作る途中で、コードを読んで見つけたバグ。早い段階で捕まる浅いバグに偏る");
        }

        return new CalibrationUseDecision(CalibrationUse.UsableBiasedShallow, "実装中に開発AIか人間が見つけたバグ。早い段階で捕まる浅いバグに偏る");
    }
}
