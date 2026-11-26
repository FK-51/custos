using System.Text;
using Custos.Core.Util;
using Custos.Registry.Git;

namespace Custos.Registry.Commits;

/// <summary>二分探索の結果。</summary>
public sealed record BisectResult(string? FirstBadCommit, string Log);

/// <summary>
/// 混入したコミットを、確認用シナリオ（コマンド）を使った git の二分探索で特定する（設計書 第20.7節「自動で埋める項目」）。
/// 利用者の作業ディレクトリを乱さないよう、一時的な worktree の中で行う。
/// </summary>
public static class BisectRunner
{
    /// <summary>
    /// goodRef（バグがない）から badRef（バグがある）の間で、最初にバグが入ったコミットを探す。
    /// testCommand は worktree の中で実行され、終了コード 0 ならバグなし、1〜124 と 126〜127 ならバグあり、125 なら判定できない（飛ばす）。
    /// </summary>
    public static async Task<BisectResult> FindIntroducingCommitAsync(
        GitClient git,
        string goodRef,
        string badRef,
        IReadOnlyList<string> testCommand,
        string worktreeParent,
        ICommandRunner? runner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(goodRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(badRef);
        ArgumentNullException.ThrowIfNull(testCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeParent);
        if (testCommand.Count == 0)
        {
            throw new CustosException("確認用のコマンドを指定してください");
        }

        var good = await git.ResolveAsync(goodRef, cancellationToken).ConfigureAwait(false) ?? throw new CustosException($"コミットがありません：{goodRef}");
        var bad = await git.ResolveAsync(badRef, cancellationToken).ConfigureAwait(false) ?? throw new CustosException($"コミットがありません：{badRef}");
        if (!await git.IsAncestorAsync(good, bad, cancellationToken).ConfigureAwait(false))
        {
            throw new CustosException($"{goodRef} は {badRef} の祖先ではありません");
        }

        Directory.CreateDirectory(worktreeParent);
        var worktree = Path.Combine(worktreeParent, "custos-bisect-" + Guid.NewGuid().ToString("N")[..12]);
        var log = new StringBuilder();
        await git.OutputAsync(["worktree", "add", "--detach", worktree, bad], cancellationToken).ConfigureAwait(false);
        var inner = new GitClient(worktree, runner);
        try
        {
            var start = await inner.RunAsync(["bisect", "start", bad, good], null, cancellationToken).ConfigureAwait(false);
            log.Append(start.StandardOutput).Append(start.StandardError);
            start.EnsureSuccess("git bisect start");
            var run = await inner.RunAsync(["bisect", "run", .. testCommand], null, cancellationToken).ConfigureAwait(false);
            log.Append(run.StandardOutput).Append(run.StandardError);
            string? first = null;
            foreach (var line in run.StandardOutput.Split('\n'))
            {
                var marker = line.IndexOf(" is the first bad commit", StringComparison.Ordinal);
                if (marker > 0)
                {
                    first = line[..marker].Trim();
                    break;
                }
            }

            return new BisectResult(first, log.ToString());
        }
        finally
        {
            await inner.RunAsync(["bisect", "reset"], null, CancellationToken.None).ConfigureAwait(false);
            await git.RunAsync(["worktree", "remove", "--force", worktree], null, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
