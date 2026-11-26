using Custos.Core.Util;
using Custos.Registry.Bugs;
using Custos.Registry.Git;

namespace Custos.Registry.Commits;

/// <summary>
/// コミットの入口の設定。段階0では分類の記録だけを行い、段階3で回帰テストの確認、混在の検出、署名、チェックポイントを有効にする
/// （設計書 第27.1節「記録は最初から、強制は後から」）。
/// </summary>
public sealed record CommitGatewayOptions(
    RegistryPaths Paths,
    bool RequireRegressionTest = false,
    bool DetectMixed = false,
    bool Sign = false,
    string? SigningKeyPath = null,
    bool UseCheckpoint = false,
    bool CopyRegressionTests = false,
    string? VerifyRepoRoot = null,
    PathPatterns? TestPatterns = null,
    MixedCheckOptions? Mixed = null,
    string? CommitterName = null,
    string? CommitterEmail = null);

/// <summary>コミットの依頼。</summary>
/// <param name="Type">種類（feat / fix / refactor / test / chore）。ないと受け付けない。</param>
/// <param name="BugId">バグの ID。fix では省略すると新しく記録を作る。</param>
/// <param name="Message">コミットメッセージ（件名と本文）。</param>
/// <param name="FoundBy">fix で記録を作るときの found_by（dev_agent か human）。</param>
/// <param name="StageAll">すべての変更をステージしてからコミットする（git commit -a に近い）。</param>
/// <param name="BugTitle">記録を作るときの一行の説明（省略すると件名を使う）。</param>
/// <param name="Severity">記録を作るときの重大度。</param>
public sealed record CommitRequest(
    string? Type,
    string? BugId,
    string Message,
    string FoundBy = "dev_agent",
    bool StageAll = false,
    string? BugTitle = null,
    string? Severity = null);

/// <summary>コミットの結果。Committed が false なら、Problems に理由がある（何もコミットしていない）。</summary>
public sealed record CommitResult(
    bool Committed,
    string? Commit,
    string? BugId,
    bool BugCreated,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    string? FinalMessage,
    CheckpointReport? Checkpoint,
    ConfirmCopy? ConfirmCopy);

/// <summary>
/// コミットの入口（設計書 第20.4節）。種類の強制、バグIDの付与、回帰テストの有無の確認、混在の検出、署名を行い、最後に git でコミットする。
/// 署名するのは、フレームワーク自身が組み立てたコミットだけ（第20.9節）。
/// </summary>
public sealed class CommitGateway
{
    private readonly GitClient _git;
    private readonly CommitGatewayOptions _options;

    /// <summary>対象のリポジトリと設定を指定して作る。</summary>
    public CommitGateway(GitClient git, CommitGatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(options);
        if (options.UseCheckpoint && !options.Sign)
        {
            throw new CustosException("チェックポイントを使う場合は、署名も有効にしてください（署名のないコミットは把握していないコミットとして扱われる）");
        }

        _git = git;
        _options = options;
    }

    /// <summary>コミットする。</summary>
    public async Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var problems = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Type))
        {
            return Rejected("種類（--type）がありません。feat / fix / refactor / test / chore のどれかを指定してください。種類のないコミットは受け付けません");
        }

        if (!CommitTypes.TryParse(request.Type, out var type))
        {
            return Rejected($"種類が正しくありません：{request.Type}（feat / fix / refactor / test / chore のどれか）");
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Rejected("コミットメッセージ（-m）がありません");
        }

        if (request.BugId != null)
        {
            if (!BugValues.IsValidId(request.BugId))
            {
                return Rejected($"バグの ID の形が違います（BUG-0001 の形）：{request.BugId}");
            }

            if (type is not (CommitType.Fix or CommitType.Test))
            {
                return Rejected("--bug は fix（と、回帰テストを足す test）のときだけ指定できます");
            }
        }

        if (type == CommitType.Fix && request.FoundBy is not ("dev_agent" or "human"))
        {
            return Rejected($"fix で記録を作るときの found_by は dev_agent か human にしてください：{request.FoundBy}");
        }

        if (_options.Sign)
        {
            if (_options.SigningKeyPath == null || !File.Exists(_options.SigningKeyPath))
            {
                return Rejected($"署名の鍵がありません：{_options.SigningKeyPath ?? "（未指定）"}");
            }
        }

        CheckpointReport? checkpoint = null;
        CheckpointGuard? guard = null;
        if (_options.UseCheckpoint)
        {
            guard = new CheckpointGuard(new CheckpointStore(_options.Paths.CheckpointsDir), new MergeRecords(_options.Paths.MergesPath), _options.Paths.AllowedSignersPath);
            checkpoint = await guard.CheckAsync(_git, cancellationToken).ConfigureAwait(false);
            if (!checkpoint.Ok)
            {
                var details = checkpoint.Unknown.Select(u => $"{u.Hash[..Math.Min(10, u.Hash.Length)]} {u.Subject}：{u.Reason}");
                return Rejected(checkpoint.Message + (checkpoint.Unknown.Count > 0 ? "\n  " + string.Join("\n  ", details) : string.Empty), checkpoint);
            }

            if (checkpoint.State == CheckpointState.NotInitialized)
            {
                warnings.Add("チェックポイントがなかったので、現在の位置で作りました");
                await guard.AdvanceAsync(_git, cancellationToken).ConfigureAwait(false);
            }
        }

        if (request.StageAll)
        {
            await _git.OutputAsync(["add", "-A"], cancellationToken).ConfigureAwait(false);
        }

        var staged = await _git.StagedChangesAsync(cancellationToken).ConfigureAwait(false);
        if (staged.Count == 0)
        {
            return Rejected("コミットする変更がありません（git add でステージしてください）", checkpoint);
        }

        var tests = _options.TestPatterns ?? PathPatterns.DefaultTests;
        var analysis = await DiffClassifier.AnalyzeStagedAsync(_git, type, tests, _options.Mixed ?? MixedCheckOptions.Default, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(analysis.ClassificationWarnings);
        if (type == CommitType.Fix && _options.RequireRegressionTest && !analysis.HasRegressionTest)
        {
            problems.Add($"fix には回帰テストが必要です（テストとみなすパス：{string.Join("、", tests.Patterns)}）");
        }

        if (_options.DetectMixed && analysis.MixedReasons.Count > 0)
        {
            problems.Add("目的の違う変更が混ざっています。コミットを分割してください：\n  " + string.Join("\n  ", analysis.MixedReasons));
        }
        else if (analysis.MixedReasons.Count > 0)
        {
            warnings.AddRange(analysis.MixedReasons.Select(r => "（混在の検出は無効）" + r));
        }

        if (problems.Count > 0)
        {
            return new CommitResult(false, null, null, false, problems, warnings, null, checkpoint, null);
        }

        var admin = BugRegistry.OpenAdmin(_options.Paths.RegistryDb);
        string? bugId = null;
        var bugCreated = false;
        var subject = request.Message.Split('\n', 2)[0].Trim();
        if (type == CommitType.Fix)
        {
            (bugId, bugCreated) = admin.EnsureExists(
                request.BugId,
                new NewBug(request.FoundBy, "implementation", request.BugTitle ?? subject, Severity: request.Severity, Source: "custos commit"));
            if (bugCreated)
            {
                warnings.Add($"登録簿に記録がなかったので作りました：{bugId}");
            }
        }
        else if (type == CommitType.Test && request.BugId != null)
        {
            if (!admin.Exists(request.BugId))
            {
                return Rejected($"登録簿にないバグの ID です：{request.BugId}", checkpoint);
            }

            bugId = request.BugId;
        }

        var message = CommitTrailers.Compose(request.Message, type, bugId);
        var arguments = new List<string>();
        if (_options.CommitterName != null)
        {
            arguments.AddRange(["-c", "user.name=" + _options.CommitterName]);
        }

        if (_options.CommitterEmail != null)
        {
            arguments.AddRange(["-c", "user.email=" + _options.CommitterEmail]);
        }

        if (_options.Sign && _options.SigningKeyPath != null)
        {
            arguments.AddRange(["-c", "gpg.format=ssh", "-c", "user.signingkey=" + _options.SigningKeyPath, "-c", "commit.gpgsign=true"]);
        }
        else
        {
            arguments.AddRange(["-c", "commit.gpgsign=false"]);
        }

        arguments.AddRange(["commit", "--cleanup=whitespace", "-F", "-"]);
        if (_options.Sign)
        {
            arguments.Add("-S");
        }

        var result = await _git.RunAsync(arguments, message, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new CommitResult(false, null, bugId, bugCreated, ["git commit に失敗しました：" + result.StandardError.Trim()], warnings, message, checkpoint, null);
        }

        var commit = await _git.HeadAsync(cancellationToken).ConfigureAwait(false) ?? throw new CustosException("コミットの後に HEAD を読めませんでした");
        ConfirmCopy? confirm = null;
        if (bugId != null)
        {
            admin.Link(bugId, type == CommitType.Fix ? "fix_commit" : "test_commit", commit);
            if (type == CommitType.Fix)
            {
                admin.Update(bugId, new BugUpdate(FixedIn: commit), "custos commit", "修正コミット");
                if (_options.CopyRegressionTests)
                {
                    confirm = await RegressionTestCopier.CopyAsync(_git, commit, tests, _options.Paths.ConfirmDir, bugId, _options.VerifyRepoRoot, cancellationToken).ConfigureAwait(false);
                    if (confirm != null)
                    {
                        admin.Update(bugId, new BugUpdate(ConfirmRef: confirm.RelativeRef), "custos commit", "回帰テストを確認用シナリオとして複製");
                    }
                }
            }
        }

        if (guard != null)
        {
            checkpoint = await guard.AdvanceAsync(_git, cancellationToken).ConfigureAwait(false);
        }

        return new CommitResult(true, commit, bugId, bugCreated, [], warnings, message, checkpoint, confirm);

        CommitResult Rejected(string reason, CheckpointReport? report = null) =>
            new(false, null, null, false, [reason], warnings, null, report, null);
    }
}
