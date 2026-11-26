using Custos.Core.Util;

namespace Custos.Registry.Commits;

/// <summary>分類の監査の指摘。</summary>
public sealed record AuditFinding(string Commit, CommitType Type, string Kind, string Message, IReadOnlyList<string> Judgments);

/// <summary>
/// 分類の監査（設計書 第20.6節）。自己申告は間違えるので、フレームワーク側で監査する。
/// - feat や refactor の後に、それまで失敗していた判定が通るようになったら、実は修正だった可能性として見直しを求める。
/// - refactor は判定結果が変わっていないことを求める（第20.3節の表）。
/// - 差分の内容と分類の食い違いは、別のAIに抜き取りで確認させる（ここでは抜き取りの選び方を決める）。
/// </summary>
public static class ClassificationAudit
{
    /// <summary>コミットの前後の、失敗している判定の集合を比べる。</summary>
    public static IReadOnlyList<AuditFinding> CheckJudgmentChanges(string commit, CommitType type, IReadOnlySet<string> failingBefore, IReadOnlySet<string> failingAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(failingBefore);
        ArgumentNullException.ThrowIfNull(failingAfter);
        var findings = new List<AuditFinding>();
        var nowPassing = failingBefore.Where(j => !failingAfter.Contains(j)).OrderBy(j => j, StringComparer.Ordinal).ToList();
        var nowFailing = failingAfter.Where(j => !failingBefore.Contains(j)).OrderBy(j => j, StringComparer.Ordinal).ToList();
        if (type is CommitType.Feat or CommitType.Refactor && nowPassing.Count > 0)
        {
            findings.Add(new AuditFinding(
                commit,
                type,
                "possible_fix",
                $"{CommitTypes.Name(type)} の後に、それまで失敗していた判定が通るようになった。実は修正だった可能性があるので、分類を見直す（fix ならバグIDと回帰テストが要る）",
                nowPassing));
        }

        if (type == CommitType.Refactor && nowFailing.Count > 0)
        {
            findings.Add(new AuditFinding(
                commit,
                type,
                "behavior_changed",
                "refactor の後に、通っていた判定が失敗するようになった。挙動を変えない整理のはず",
                nowFailing));
        }

        if (type == CommitType.Fix && failingBefore.Count > 0 && nowPassing.Count == 0)
        {
            findings.Add(new AuditFinding(
                commit,
                type,
                "fix_without_effect",
                "fix の後も、失敗していた判定が一つも通るようになっていない（修正の対象が判定に現れていない可能性）",
                []));
        }

        return findings;
    }

    /// <summary>
    /// 別のAIに差分と分類の食い違いを確認させるコミットを、シードを指定して決定的に選ぶ。
    /// 並び順に依存しないよう、シードとハッシュから計算した値の小さい順に count 件を選ぶ。
    /// </summary>
    public static IReadOnlyList<string> SampleForReview(IEnumerable<string> commits, int count, string seed)
    {
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(seed);
        if (count <= 0)
        {
            return [];
        }

        return commits
            .Distinct(StringComparer.Ordinal)
            .Select(c => (Commit: c, Key: Hashing.Sha256Hex(seed + "\n" + c)))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Take(count)
            .Select(x => x.Commit)
            .ToList();
    }
}
