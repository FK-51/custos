using System.Text;
using Custos.Core.Util;
using Custos.Registry.Git;

namespace Custos.Registry.Commits;

/// <summary>複製の結果。</summary>
public sealed record ConfirmCopy(string Directory, string RelativeRef, IReadOnlyList<string> Files);

/// <summary>
/// 修正コミットに含まれる回帰テストを、登録時に検証用リポジトリへ複製する（設計書 第20.7節「確認用シナリオ」）。
/// 再注入では修正コミットごと戻すので、アプリ側の回帰テストも消え、探索AIからはそのバグを指し示すテストが見えなくなる。
/// </summary>
public static class RegressionTestCopier
{
    /// <summary>
    /// コミットで追加・変更されたテストのファイルを confirmDir/&lt;bugId&gt;/ に複製する。テストがなければ null。
    /// relativeBase を渡すと、返す参照はそこからの相対パスになる（例：registry/confirm/BUG-0001）。
    /// </summary>
    public static async Task<ConfirmCopy?> CopyAsync(
        GitClient git,
        string commit,
        PathPatterns tests,
        string confirmDir,
        string bugId,
        string? relativeBase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(bugId);
        var entries = await git.CommitChangesAsync(commit, cancellationToken).ConfigureAwait(false);
        var testFiles = entries.Where(e => e.Status != 'D' && tests.Matches(e.Path)).Select(e => e.Path).ToList();
        if (testFiles.Count == 0)
        {
            return null;
        }

        var target = Path.Combine(confirmDir, bugId);
        Directory.CreateDirectory(target);
        var copied = new List<string>();
        foreach (var file in testFiles)
        {
            var content = await git.ShowFileAsync(commit, file, cancellationToken).ConfigureAwait(false);
            if (content == null)
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(target, file));
            if (!Workspace.IsInside(destination, target))
            {
                throw new CustosException($"複製先がディレクトリの外になります：{file}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? target);
            await File.WriteAllBytesAsync(destination, content, cancellationToken).ConfigureAwait(false);
            copied.Add(file);
        }

        var manifest = Json.Serialize(new { bug = bugId, commit, files = copied, copied_at = DateTimeOffset.UtcNow });
        await File.WriteAllTextAsync(Path.Combine(target, "confirm.json"), manifest, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var relative = relativeBase == null ? target : Path.GetRelativePath(relativeBase, target).Replace('\\', '/');
        return new ConfirmCopy(target, relative, copied);
    }
}
