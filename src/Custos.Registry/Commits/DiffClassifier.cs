using System.Globalization;
using Custos.Registry.Git;

namespace Custos.Registry.Commits;

/// <summary>混在の検出の設定。</summary>
public sealed record MixedCheckOptions(int MaxAddedSourceFilesForFix, IReadOnlyList<string> AssertionTokens)
{
    /// <summary>既定値：fix で追加してよいテスト以外のファイルは1個まで。期待値とみなす書き方は一般的なテストの書き方。</summary>
    public static readonly MixedCheckOptions Default = new(
        1,
        ["expect(", "assert", "Assert.", ".should", "toBe(", "toEqual(", "toStrictEqual(", "toMatch", "toHaveBeenCalled", "toThrow", "toContain(", "Equal(", "True(", "False("]);
}

/// <summary>差分を分類した結果。</summary>
public sealed record DiffAnalysis(
    IReadOnlyList<DiffEntry> Entries,
    IReadOnlyList<DiffEntry> TestFiles,
    IReadOnlyList<DiffEntry> SourceFiles,
    bool HasRegressionTest,
    IReadOnlyList<string> MixedReasons,
    IReadOnlyList<string> ClassificationWarnings);

/// <summary>
/// 差分から、回帰テストの有無と、機能追加と修正の混在を判定する（設計書 第20.3〜20.4節）。
/// 混在の検出はヒューリスティックであり、最終的な確認は別のAIによる抜き取り（第20.6節）で行う。
/// </summary>
public static class DiffClassifier
{
    /// <summary>ステージ済みの差分を分類する。</summary>
    public static async Task<DiffAnalysis> AnalyzeStagedAsync(GitClient git, CommitType type, PathPatterns tests, MixedCheckOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        var entries = await git.StagedChangesAsync(cancellationToken).ConfigureAwait(false);
        return await AnalyzeAsync(type, entries, tests, options, path => git.StagedPatchAsync(path, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>コミットの差分を分類する。</summary>
    public static async Task<DiffAnalysis> AnalyzeCommitAsync(GitClient git, string commit, CommitType type, PathPatterns tests, MixedCheckOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        var entries = await git.CommitChangesAsync(commit, cancellationToken).ConfigureAwait(false);
        return await AnalyzeAsync(type, entries, tests, options, path => git.CommitPatchAsync(commit, path, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>差分の一覧と、ファイルごとの差分の本文を取る関数から分類する。</summary>
    public static async Task<DiffAnalysis> AnalyzeAsync(CommitType type, IReadOnlyList<DiffEntry> entries, PathPatterns tests, MixedCheckOptions options, Func<string, Task<string>> patchOf)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(patchOf);
        var testFiles = entries.Where(e => tests.Matches(e.Path)).ToList();
        var sourceFiles = entries.Where(e => !tests.Matches(e.Path)).ToList();
        var hasRegressionTest = testFiles.Any(e => e.Status != 'D');
        var mixed = new List<string>();
        var warnings = new List<string>();

        switch (type)
        {
            case CommitType.Fix:
            {
                var added = sourceFiles.Where(e => e.Status == 'A').ToList();
                if (added.Count > options.MaxAddedSourceFilesForFix)
                {
                    mixed.Add(string.Create(CultureInfo.InvariantCulture, $"fix なのにテスト以外の新しいファイルを {added.Count} 個追加している（機能追加が混ざっている可能性）：{string.Join("、", added.Select(a => a.Path))}"));
                }

                if (sourceFiles.Count == 0 && testFiles.Count > 0)
                {
                    warnings.Add("fix なのにテスト以外の変更がない（test に分類すべき可能性）");
                }

                break;
            }

            case CommitType.Feat:
            {
                foreach (var file in testFiles.Where(e => e.Status == 'M'))
                {
                    var patch = await patchOf(file.Path).ConfigureAwait(false);
                    var removed = ChangedLines(patch, '-').Where(l => ContainsAssertion(l, options)).ToList();
                    if (removed.Count > 0)
                    {
                        mixed.Add(string.Create(CultureInfo.InvariantCulture, $"feat なのに既存テストの期待値を変えている（修正が混ざっている可能性）：{file.Path}（{removed.Count} 行）"));
                    }
                }

                break;
            }

            case CommitType.Refactor:
            {
                foreach (var file in testFiles.Where(e => e.Status is 'M' or 'D'))
                {
                    var patch = await patchOf(file.Path).ConfigureAwait(false);
                    var changed = ChangedLines(patch, '-').Concat(ChangedLines(patch, '+')).Where(l => ContainsAssertion(l, options)).ToList();
                    if (changed.Count > 0 || file.Status == 'D')
                    {
                        mixed.Add($"refactor なのにテストの期待値を変えている（挙動を変えない整理のはず）：{file.Path}");
                    }
                }

                break;
            }

            case CommitType.Test:
            {
                if (sourceFiles.Count > 0)
                {
                    mixed.Add($"test なのにテスト以外のファイルを変えている：{string.Join("、", sourceFiles.Select(s => s.Path))}");
                }

                break;
            }

            default:
                break;
        }

        return new DiffAnalysis(entries, testFiles, sourceFiles, hasRegressionTest, mixed, warnings);
    }

    private static IEnumerable<string> ChangedLines(string patch, char sign)
    {
        foreach (var line in patch.Split('\n'))
        {
            if (line.Length == 0 || line[0] != sign)
            {
                continue;
            }

            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            yield return line[1..];
        }
    }

    private static bool ContainsAssertion(string line, MixedCheckOptions options) =>
        options.AssertionTokens.Any(token => line.Contains(token, StringComparison.Ordinal));
}
