using System.Globalization;
using System.Text;
using System.Text.Json;
using Custos.Core.Util;
using Custos.Registry.Git;

namespace Custos.Registry.Commits;

/// <summary>GitHub の本流で見つけた、把握していないコミット。</summary>
public sealed record UnknownCommit(string Sha, string Subject, string Reason);

/// <summary>監視の結果。把握していないコミットがあれば、解消されるまでリリース判定を出さない。</summary>
public sealed record WatchReport(IReadOnlyList<UnknownCommit> Unknown, int Checked, bool ReachedKnownHead)
{
    /// <summary>把握していないコミットがないか。</summary>
    public bool Ok => Unknown.Count == 0;
}

/// <summary>コミットのオブジェクトを、署名と署名の対象（payload）に分けたもの。</summary>
public sealed record SignedCommitParts(string Payload, string? Signature);

/// <summary>コミットのオブジェクト（git cat-file commit の出力）の分解。</summary>
public static class SignedCommitObject
{
    /// <summary>gpgsig の頭を取り除いた署名の対象と、署名に分ける。</summary>
    public static SignedCommitParts Split(string rawCommitObject)
    {
        ArgumentNullException.ThrowIfNull(rawCommitObject);
        var payload = new StringBuilder();
        var signature = new StringBuilder();
        var inHeader = true;
        var inSignature = false;
        var lines = rawCommitObject.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var last = i == lines.Length - 1;
            if (inHeader)
            {
                if (inSignature)
                {
                    if (line.StartsWith(' '))
                    {
                        signature.Append(line[1..]).Append('\n');
                        continue;
                    }

                    inSignature = false;
                }

                if (line.StartsWith("gpgsig ", StringComparison.Ordinal))
                {
                    inSignature = true;
                    signature.Append(line["gpgsig ".Length..]).Append('\n');
                    continue;
                }

                if (line.Length == 0)
                {
                    inHeader = false;
                }
            }

            payload.Append(line);
            if (!last)
            {
                payload.Append('\n');
            }
        }

        return new SignedCommitParts(payload.ToString(), signature.Length == 0 ? null : signature.ToString());
    }
}

/// <summary>
/// GitHub 上の本流を確認し、把握していないコミット（署名のないもの、許可した鍵で検証できないもの、取り込みの記録にないもの）を返す
/// （設計書 第20.9節「保護を使えない場合」：人間の誤操作は検知で補う）。
/// 署名は GitHub の verified の値に頼らず、許可する鍵の一覧で手元で検証する。
/// </summary>
public sealed class GitHubMainlineWatcher
{
    private readonly ICommandRunner _runner;
    private readonly string _repository;
    private readonly string _branch;
    private readonly string _allowedSignersPath;
    private readonly MergeRecords _merges;
    private readonly string _workDir;

    /// <summary>作る。repository は owner/name。</summary>
    public GitHubMainlineWatcher(ICommandRunner runner, string repository, string branch, string allowedSignersPath, MergeRecords merges, string workDir)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedSignersPath);
        ArgumentNullException.ThrowIfNull(merges);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        if (!repository.Contains('/', StringComparison.Ordinal))
        {
            throw new CustosException("repository は owner/name の形で指定してください：" + repository);
        }

        _runner = runner;
        _repository = repository;
        _branch = branch;
        _allowedSignersPath = allowedSignersPath;
        _merges = merges;
        _workDir = workDir;
    }

    /// <summary>
    /// 本流の新しい順にコミットを確かめる。knownHead（最後に確かめた位置）に着いたら止める。
    /// </summary>
    public async Task<WatchReport> CheckAsync(string? knownHead, int perPage = 100, CancellationToken cancellationToken = default)
    {
        var endpoint = string.Create(CultureInfo.InvariantCulture, $"repos/{_repository}/commits?sha={Uri.EscapeDataString(_branch)}&per_page={perPage}");
        var result = await _runner.RunAsync("gh", ["api", "-H", "Accept: application/vnd.github+json", endpoint], _workDir, null, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("gh api（本流のコミットの一覧）");
        using var document = JsonDocument.Parse(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new CustosException("gh api の応答が配列ではありません");
        }

        var allowed = AllowedSignersFile.Load(_allowedSignersPath);
        var unknown = new List<UnknownCommit>();
        var checkedCount = 0;
        var reached = false;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var sha = item.GetProperty("sha").GetString() ?? string.Empty;
            if (knownHead != null && string.Equals(sha, knownHead, StringComparison.Ordinal))
            {
                reached = true;
                break;
            }

            checkedCount++;
            var commit = item.GetProperty("commit");
            var message = commit.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var subject = message.Split('\n', 2)[0];
            if (_merges.Contains(sha))
            {
                continue;
            }

            string? signature = null;
            string? payload = null;
            if (commit.TryGetProperty("verification", out var verification) && verification.ValueKind == JsonValueKind.Object)
            {
                signature = verification.TryGetProperty("signature", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                payload = verification.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            }

            if (signature == null || payload == null)
            {
                unknown.Add(new UnknownCommit(sha, subject, "署名がない（取り込みの記録にもない）"));
                continue;
            }

            if (!signature.Contains("BEGIN SSH SIGNATURE", StringComparison.Ordinal))
            {
                unknown.Add(new UnknownCommit(sha, subject, "SSH の署名ではない（許可した鍵で検証できない）"));
                continue;
            }

            var fingerprint = await VerifyAsync(signature, payload, cancellationToken).ConfigureAwait(false);
            if (fingerprint == null)
            {
                unknown.Add(new UnknownCommit(sha, subject, "許可した鍵で検証できない署名"));
                continue;
            }

            if (allowed.FindByFingerprint(fingerprint) == null)
            {
                unknown.Add(new UnknownCommit(sha, subject, "一覧にない鍵の署名"));
            }
        }

        return new WatchReport(unknown, checkedCount, reached);
    }

    /// <summary>ssh-keygen で署名を検証し、成功したら鍵の指紋を返す。</summary>
    private async Task<string?> VerifyAsync(string signature, string payload, CancellationToken cancellationToken)
    {
        if (!File.Exists(_allowedSignersPath))
        {
            return null;
        }

        Directory.CreateDirectory(_workDir);
        var sigPath = Path.Combine(_workDir, "custos-sig-" + Guid.NewGuid().ToString("N") + ".sig");
        try
        {
            await File.WriteAllTextAsync(sigPath, signature, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var verifyTime = CommitTime(payload);
            var timeOption = verifyTime == null ? [] : new[] { "-Overify-time=" + verifyTime };
            var find = await _runner.RunAsync(
                "ssh-keygen",
                ["-Y", "find-principals", "-s", sigPath, "-f", _allowedSignersPath, .. timeOption],
                _workDir,
                null,
                cancellationToken).ConfigureAwait(false);
            if (!find.Succeeded)
            {
                return null;
            }

            foreach (var principal in find.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var verify = await _runner.RunAsync(
                    "ssh-keygen",
                    ["-Y", "verify", "-f", _allowedSignersPath, "-I", principal, "-n", "git", "-s", sigPath, .. timeOption],
                    _workDir,
                    payload,
                    cancellationToken).ConfigureAwait(false);
                if (verify.Succeeded)
                {
                    var text = verify.StandardOutput + verify.StandardError;
                    var index = text.IndexOf("SHA256:", StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        var end = index;
                        while (end < text.Length && !char.IsWhiteSpace(text[end]))
                        {
                            end++;
                        }

                        return text[index..end];
                    }
                }
            }

            return null;
        }
        finally
        {
            File.Delete(sigPath);
        }
    }

    /// <summary>署名の対象の committer 行から、検証に使う日時（UTC、yyyyMMddHHmmss）を取る。</summary>
    internal static string? CommitTime(string payload)
    {
        foreach (var line in payload.Split('\n'))
        {
            if (line.Length == 0)
            {
                break;
            }

            if (!line.StartsWith("committer ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            }
        }

        return null;
    }
}
