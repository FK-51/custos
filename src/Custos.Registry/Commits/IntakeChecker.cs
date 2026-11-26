using System.Globalization;
using System.Text;
using Custos.Core.Util;
using Custos.Registry.Bugs;
using Custos.Registry.Git;

namespace Custos.Registry.Commits;

/// <summary>取り込み口の検査の設定。</summary>
public sealed record IntakeOptions(
    string AllowedSignersPath,
    MergeRecords? Merges = null,
    CheckpointStore? Checkpoints = null,
    string? CheckpointRepoPath = null,
    PathPatterns? TestPatterns = null,
    bool RequireRegressionTest = true,
    bool DetectMixed = false,
    MixedCheckOptions? Mixed = null);

/// <summary>コミット1件の検査の結果。</summary>
public sealed record IntakeCommitResult(string Hash, string Subject, SignatureTrust Trust, string? Type, string? Bug, bool IsMerge, IReadOnlyList<string> Problems)
{
    /// <summary>問題がないか。</summary>
    public bool Ok => Problems.Count == 0;
}

/// <summary>範囲の検査の結果。</summary>
public sealed record IntakeReport(bool Ok, string BaseRef, string HeadRef, IReadOnlyList<IntakeCommitResult> Commits, IReadOnlyList<string> Problems)
{
    /// <summary>人が読む要約。</summary>
    public string Summarize()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"取り込みの検査：{(Ok ? "合格" : "不合格")}（{Commits.Count} 件のコミット）");
        foreach (var problem in Problems)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  - {problem}");
        }

        foreach (var commit in Commits.Where(c => !c.Ok))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {commit.Hash[..Math.Min(10, commit.Hash.Length)]} {commit.Subject}");
            foreach (var problem in commit.Problems)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    - {problem}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// 本流への取り込み口の検査（設計書 第20.4節、第20.9節「フレームワークの検査の内容」）。
/// すべてのコミットが許可した鍵で署名されていること、分類・バグID・回帰テストがそろっていること、
/// チェックポイントから途切れずにつながっていることを確かめる。
/// </summary>
public static class IntakeChecker
{
    /// <summary>空の参照（git の 0000…、新しいブランチを表す）。</summary>
    public const string ZeroHash = "0000000000000000000000000000000000000000";

    /// <summary>baseRef..headRef の範囲を検査する。baseRef が 0000… なら headRef までのすべて。</summary>
    public static async Task<IntakeReport> CheckAsync(GitClient git, string baseRef, string headRef, IntakeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(headRef);
        ArgumentNullException.ThrowIfNull(options);
        var problems = new List<string>();
        var head = await git.ResolveAsync(headRef, cancellationToken).ConfigureAwait(false);
        if (head == null)
        {
            return new IntakeReport(false, baseRef, headRef, [], [$"取り込むコミットがありません：{headRef}"]);
        }

        var isNewBranch = baseRef == ZeroHash;
        string? baseHash = null;
        if (!isNewBranch)
        {
            baseHash = await git.ResolveAsync(baseRef, cancellationToken).ConfigureAwait(false);
            if (baseHash == null)
            {
                problems.Add($"基準のコミットがありません：{baseRef}");
            }
            else if (!await git.IsAncestorAsync(baseHash, head, cancellationToken).ConfigureAwait(false))
            {
                problems.Add("基準のコミットが取り込む履歴に含まれていません（強制プッシュによる履歴の書き換え）");
            }
        }

        if (options.Checkpoints != null)
        {
            var checkpoint = options.Checkpoints.Load(options.CheckpointRepoPath ?? git.RepoPath);
            if (checkpoint != null
                && (!await git.CommitExistsAsync(checkpoint, cancellationToken).ConfigureAwait(false)
                    || !await git.IsAncestorAsync(checkpoint, head, cancellationToken).ConfigureAwait(false)))
            {
                problems.Add($"チェックポイント {checkpoint[..Math.Min(10, checkpoint.Length)]} から途切れています（履歴が書き換えられた）");
            }
        }

        var range = baseHash == null ? head : $"{baseHash}..{head}";
        var commits = await git.LogAsync(range, options.AllowedSignersPath, cancellationToken).ConfigureAwait(false);
        var allowed = AllowedSignersFile.Load(options.AllowedSignersPath);
        var tests = options.TestPatterns ?? PathPatterns.DefaultTests;
        var results = new List<IntakeCommitResult>();
        foreach (var commit in commits)
        {
            var commitProblems = new List<string>();
            var trust = SignaturePolicy.Classify(commit, allowed);
            var recordedMerge = commit.IsMerge && options.Merges != null && options.Merges.Contains(commit.Hash);
            if (!SignaturePolicy.IsTrusted(trust) && !recordedMerge)
            {
                commitProblems.Add(SignaturePolicy.Describe(trust) + "（フレームワークか登録した人間の署名が必要）");
            }

            var (typeText, bug) = CommitTrailers.Parse(commit.Message);
            if (!commit.IsMerge)
            {
                if (typeText == null)
                {
                    commitProblems.Add("分類（Type:）がありません");
                }
                else if (!CommitTypes.TryParse(typeText, out var type))
                {
                    commitProblems.Add($"分類が正しくありません：{typeText}");
                }
                else
                {
                    if (type == CommitType.Fix)
                    {
                        if (bug == null)
                        {
                            commitProblems.Add("fix にバグID（Bug:）がありません");
                        }
                        else if (!BugValues.IsValidId(bug))
                        {
                            commitProblems.Add($"バグIDの形が違います：{bug}");
                        }
                    }

                    if ((type == CommitType.Fix && options.RequireRegressionTest) || options.DetectMixed)
                    {
                        var analysis = await DiffClassifier.AnalyzeCommitAsync(git, commit.Hash, type, tests, options.Mixed ?? MixedCheckOptions.Default, cancellationToken).ConfigureAwait(false);
                        if (type == CommitType.Fix && options.RequireRegressionTest && !analysis.HasRegressionTest)
                        {
                            commitProblems.Add("fix に回帰テストが含まれていません");
                        }

                        if (options.DetectMixed)
                        {
                            commitProblems.AddRange(analysis.MixedReasons);
                        }
                    }
                }
            }

            results.Add(new IntakeCommitResult(commit.Hash, commit.Subject, trust, typeText, bug, commit.IsMerge, commitProblems));
        }

        var ok = problems.Count == 0 && results.All(r => r.Ok);
        return new IntakeReport(ok, baseRef, headRef, results, problems);
    }
}

/// <summary>
/// 自前の取り込み口（受け取り時のフック）。フレームワークを動かすホストに本流のリポジトリを置き、
/// 受け取り時に署名と分類を検査する（設計書 第20.9節「取り込み口を自前で持つ」）。
/// </summary>
public static class PreReceiveHook
{
    /// <summary>
    /// pre-receive フックのスクリプトを作る。保護する参照への更新ごとに
    /// `&lt;custosCommand&gt; intake check --repo &lt;dir&gt; --base &lt;old&gt; --head &lt;new&gt;` を呼び、失敗したら拒否する。
    /// </summary>
    public static string Generate(IReadOnlyList<string> custosCommand, IReadOnlyList<string> protectedRefs, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(custosCommand);
        ArgumentNullException.ThrowIfNull(protectedRefs);
        if (custosCommand.Count == 0)
        {
            throw new CustosException("custos のコマンドを指定してください");
        }

        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n");
        sb.Append("# Custos の取り込み口（設計書 第20.4節、第20.9節）。custos が生成した。手で書き換えない。\n");
        sb.Append("set -u\n");
        if (environment != null)
        {
            foreach (var pair in environment.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append("export ").Append(pair.Key).Append('=').Append(Quote(pair.Value)).Append('\n');
            }
        }

        sb.Append("zero=").Append(IntakeChecker.ZeroHash).Append('\n');
        sb.Append("repo=$(pwd)\n");
        sb.Append("status=0\n");
        sb.Append("while read old new ref; do\n");
        sb.Append("  case \"$ref\" in\n");
        sb.Append("    ").Append(string.Join("|", protectedRefs.Select(Quote))).Append(") ;;\n");
        sb.Append("    *) continue ;;\n");
        sb.Append("  esac\n");
        sb.Append("  if [ \"$new\" = \"$zero\" ]; then\n");
        sb.Append("    echo \"custos: 本流は削除できません: $ref\" >&2\n");
        sb.Append("    status=1\n");
        sb.Append("    continue\n");
        sb.Append("  fi\n");
        sb.Append("  if ! ").Append(string.Join(' ', custosCommand.Select(Quote))).Append(" intake check --repo \"$repo\" --base \"$old\" --head \"$new\"; then\n");
        sb.Append("    echo \"custos: 取り込みを拒否しました（署名・分類・バグID・回帰テストを確かめてください）: $ref\" >&2\n");
        sb.Append("    status=1\n");
        sb.Append("  fi\n");
        sb.Append("done\n");
        sb.Append("exit $status\n");
        return sb.ToString();
    }

    /// <summary>bare リポジトリの hooks/pre-receive に書き、実行できるようにする。</summary>
    public static string Install(string bareRepoPath, string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bareRepoPath);
        ArgumentNullException.ThrowIfNull(script);
        var hooks = Path.Combine(bareRepoPath, "hooks");
        Directory.CreateDirectory(hooks);
        var path = Path.Combine(hooks, "pre-receive");
        File.WriteAllText(path, script, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return path;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
