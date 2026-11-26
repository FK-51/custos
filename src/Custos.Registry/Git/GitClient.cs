using System.Globalization;
using Custos.Core.Util;

namespace Custos.Registry.Git;

/// <summary>外部コマンドの実行。GitHub の監視などのテストで差し替えられるようにする。</summary>
public interface ICommandRunner
{
    /// <summary>コマンドを実行する。</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, string? standardInput, CancellationToken cancellationToken);
}

/// <summary>実際にプロセスを起動する実装。</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly TimeSpan _timeout;

    /// <summary>環境変数の上書きと時間の上限を指定して作る。</summary>
    public ProcessCommandRunner(IReadOnlyDictionary<string, string?>? environment = null, TimeSpan? timeout = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" };
        if (environment != null)
        {
            foreach (var pair in environment)
            {
                env[pair.Key] = pair.Value;
            }
        }

        _environment = env;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    /// <inheritdoc />
    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, string? standardInput, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(fileName, arguments, _environment, workingDirectory, standardInput, _timeout, cancellationToken);
}

/// <summary>差分の1項目（git diff --name-status）。</summary>
public sealed record DiffEntry(char Status, string Path, string? OldPath);

/// <summary>コミットの情報（署名の検証結果を含む）。</summary>
public sealed record CommitInfo(
    string Hash,
    IReadOnlyList<string> Parents,
    char SignatureStatus,
    string KeyFingerprint,
    string Signer,
    DateTimeOffset CommitterDate,
    string Message)
{
    /// <summary>マージコミットか。</summary>
    public bool IsMerge => Parents.Count > 1;

    /// <summary>件名（メッセージの1行目）。</summary>
    public string Subject => Message.Split('\n', 2)[0];
}

/// <summary>git の呼び出し。</summary>
public sealed class GitClient
{
    private readonly ICommandRunner _runner;

    /// <summary>リポジトリの場所と実行の仕組みを指定して作る。</summary>
    public GitClient(string repoPath, ICommandRunner? runner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        RepoPath = Path.GetFullPath(repoPath);
        _runner = runner ?? new ProcessCommandRunner();
    }

    /// <summary>リポジトリの場所。</summary>
    public string RepoPath { get; }

    /// <summary>git を実行する。</summary>
    public Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput = null, CancellationToken cancellationToken = default) =>
        _runner.RunAsync("git", arguments, RepoPath, standardInput, cancellationToken);

    /// <summary>git を実行し、成功しなければ例外にする。標準出力の末尾の改行を落として返す。</summary>
    public async Task<string> OutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(arguments, null, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("git " + string.Join(' ', arguments));
        return result.StandardOutput.TrimEnd('\n', '\r');
    }

    /// <summary>現在の位置（コミットがなければ null）。</summary>
    public async Task<string?> HeadAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    /// <summary>参照をコミットのハッシュにする（なければ null）。</summary>
    public async Task<string?> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["rev-parse", "--verify", "--quiet", reference + "^{commit}"], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    /// <summary>ancestor が descendant の祖先（または同じ）か。</summary>
    public async Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["merge-base", "--is-ancestor", ancestor, descendant], null, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>オブジェクトがリポジトリにあるか。</summary>
    public async Task<bool> CommitExistsAsync(string hash, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["cat-file", "-e", hash + "^{commit}"], null, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <summary>作業中の変更（ステージ済みと未ステージ、追跡していないファイル）があるか。</summary>
    public async Task<bool> HasWorkingChangesAsync(CancellationToken cancellationToken = default)
    {
        var output = await OutputAsync(["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
        return output.Length > 0;
    }

    /// <summary>ステージ済みの差分の一覧。</summary>
    public async Task<IReadOnlyList<DiffEntry>> StagedChangesAsync(CancellationToken cancellationToken = default)
    {
        var output = await OutputAsync(["diff", "--cached", "--name-status", "-M", "-z"], cancellationToken).ConfigureAwait(false);
        return ParseNameStatusZ(output);
    }

    /// <summary>コミットの差分の一覧（最初の親との比較。親がなければ空の木との比較）。</summary>
    public async Task<IReadOnlyList<DiffEntry>> CommitChangesAsync(string commit, CancellationToken cancellationToken = default)
    {
        var output = await OutputAsync(["diff-tree", "--root", "--no-commit-id", "-r", "-M", "--name-status", "-z", "-m", "--first-parent", commit], cancellationToken).ConfigureAwait(false);
        return ParseNameStatusZ(output);
    }

    /// <summary>ステージ済みの差分の本文（1ファイル、文脈なし）。</summary>
    public Task<string> StagedPatchAsync(string path, CancellationToken cancellationToken = default) =>
        OutputAsync(["diff", "--cached", "-U0", "--no-color", "--", path], cancellationToken);

    /// <summary>コミットの差分の本文（1ファイル、文脈なし）。</summary>
    public Task<string> CommitPatchAsync(string commit, string path, CancellationToken cancellationToken = default) =>
        OutputAsync(["show", "-U0", "--no-color", "--format=", "--first-parent", commit, "--", path], cancellationToken);

    /// <summary>コミットの時点のファイルの内容。</summary>
    public async Task<byte[]?> ShowFileAsync(string commit, string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["show", $"{commit}:{path}"], null, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? System.Text.Encoding.UTF8.GetBytes(result.StandardOutput) : null;
    }

    /// <summary>
    /// 範囲（例：base..head）のコミットを、古い順に返す。署名は allowedSignersFile で検証する
    /// （ファイルがなければ、許可する鍵が一つもないものとして扱う）。
    /// </summary>
    public Task<IReadOnlyList<CommitInfo>> LogAsync(string range, string? allowedSignersFile, CancellationToken cancellationToken = default) =>
        LogCoreAsync(["--reverse", range], allowedSignersFile, cancellationToken);

    /// <summary>1件のコミットの情報。</summary>
    public async Task<CommitInfo> CommitAsync(string commit, string? allowedSignersFile, CancellationToken cancellationToken = default)
    {
        var list = await LogCoreAsync(["-1", commit], allowedSignersFile, cancellationToken).ConfigureAwait(false);
        return list.Count == 1 ? list[0] : throw new CustosException($"コミットを読めません：{commit}");
    }

    private async Task<IReadOnlyList<CommitInfo>> LogCoreAsync(IReadOnlyList<string> revisions, string? allowedSignersFile, CancellationToken cancellationToken)
    {
        string? temporary = null;
        try
        {
            var signers = allowedSignersFile;
            if (signers == null || !File.Exists(signers))
            {
                temporary = Path.GetTempFileName();
                signers = temporary;
            }

            var arguments = new List<string> { "-c", "gpg.ssh.allowedSignersFile=" + signers, "log", "--format=%H%x1f%P%x1f%G?%x1f%GK%x1f%GS%x1f%cI%x1f%B%x1e" };
            arguments.AddRange(revisions);
            arguments.Add("--");
            var output = await OutputAsync(arguments, cancellationToken).ConfigureAwait(false);
            return ParseLog(output);
        }
        finally
        {
            if (temporary != null)
            {
                File.Delete(temporary);
            }
        }
    }

    internal static IReadOnlyList<CommitInfo> ParseLog(string output)
    {
        var list = new List<CommitInfo>();
        foreach (var record in output.Split('\x1e'))
        {
            var text = record.TrimStart('\n', '\r');
            if (text.Length == 0)
            {
                continue;
            }

            var fields = text.Split('\x1f');
            if (fields.Length < 7)
            {
                continue;
            }

            var parents = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var status = fields[2].Length > 0 ? fields[2][0] : 'N';
            var date = DateTimeOffset.Parse(fields[5], CultureInfo.InvariantCulture);
            list.Add(new CommitInfo(fields[0], parents, status, fields[3], fields[4], date, fields[6].TrimEnd('\n')));
        }

        return list;
    }

    internal static IReadOnlyList<DiffEntry> ParseNameStatusZ(string output)
    {
        var parts = output.Split('\0');
        var list = new List<DiffEntry>();
        var i = 0;
        while (i < parts.Length)
        {
            var statusText = parts[i];
            if (statusText.Length == 0)
            {
                i++;
                continue;
            }

            var status = statusText[0];
            if (status is 'R' or 'C')
            {
                var oldPath = i + 1 < parts.Length ? parts[i + 1] : string.Empty;
                var newPath = i + 2 < parts.Length ? parts[i + 2] : string.Empty;
                list.Add(new DiffEntry(status, newPath, oldPath));
                i += 3;
            }
            else
            {
                var path = i + 1 < parts.Length ? parts[i + 1] : string.Empty;
                list.Add(new DiffEntry(status, path, null));
                i += 2;
            }
        }

        return list;
    }
}
