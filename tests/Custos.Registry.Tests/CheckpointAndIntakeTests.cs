using Custos.Core.Util;
using Custos.Registry.Commits;
using Custos.Registry.Git;

namespace Custos.Registry.Tests;

public class CheckpointAndIntakeTests
{
    private sealed record Fixture(TempDir Temp, GitClient Git, RegistryPaths Paths, SigningKey Framework, SigningKey Human, SigningKey Stranger) : IDisposable
    {
        public CheckpointGuard Guard() => new(new CheckpointStore(Paths.CheckpointsDir), new MergeRecords(Paths.MergesPath), Paths.AllowedSignersPath);

        public CommitGateway Gateway() => new(Git, new CommitGatewayOptions(Paths, Sign: true, SigningKeyPath: Framework.PrivateKeyPath, UseCheckpoint: true));

        public void Dispose() => Temp.Dispose();
    }

    private static async Task<Fixture> SetupAsync()
    {
        var temp = new TempDir();
        var paths = temp.Paths();
        var git = await TestGit.InitAsync(temp.Sub("app"));
        await TestGit.CommitFileAsync(git, "src/game.ts", "export const a = 1;\n", "init");
        var framework = await SigningKeys.GenerateAsync(paths.SecretsDir, "framework", "custos-framework");
        var human = await SigningKeys.GenerateAsync(paths.SecretsDir, "human", "developer");
        var stranger = await SigningKeys.GenerateAsync(paths.SecretsDir, "stranger", "stranger");
        var allowed = AllowedSignersFile.Load(paths.AllowedSignersPath);
        allowed.Add("custos@framework", framework.PublicKeyLine, "custos", SignerRole.Framework, new DateOnly(2020, 1, 1));
        allowed.Add("dev@example.invalid", human.PublicKeyLine, "developer", SignerRole.Human, new DateOnly(2020, 1, 1));
        allowed.Save(paths.AllowedSignersPath);
        return new Fixture(temp, git, paths, framework, human, stranger);
    }

    private static async Task<string> SignedCommitAsync(GitClient git, SigningKey key, string file, string content, string message)
    {
        await TestGit.StageFileAsync(git, file, content);
        await git.OutputAsync(["-c", "gpg.format=ssh", "-c", "user.signingkey=" + key.PrivateKeyPath, "commit", "-q", "-S", "-m", message]);
        return await git.HeadAsync() ?? throw new CustosException("HEAD がありません");
    }

    [Fact]
    public async Task NotInitializedThenSame()
    {
        using var f = await SetupAsync();
        var guard = f.Guard();
        var first = await guard.CheckAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointState.NotInitialized, first.State);
        Assert.True(first.Ok);
        await guard.AdvanceAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        var same = await guard.CheckAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointState.Same, same.State);
        Assert.False(same.HasWorkingChanges);
    }

    [Fact]
    public async Task WorkingChangesAreNotAProblem()
    {
        using var f = await SetupAsync();
        var guard = f.Guard();
        await guard.AdvanceAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(f.Git.RepoPath, "src", "game.ts"), "export const a = 99;\n", cancellationToken: TestContext.Current.CancellationToken);
        var report = await guard.CheckAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointState.Same, report.State);
        Assert.True(report.Ok);
        Assert.True(report.HasWorkingChanges);
    }

    [Fact]
    public async Task SignedCommitsAndRecordedMergesAreKnown()
    {
        using var f = await SetupAsync();
        var guard = f.Guard();
        await guard.AdvanceAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        var gateway = f.Gateway();
        await TestGit.StageFileAsync(f.Git, "src/game.ts", "export const a = 2;\n");
        var viaGateway = await gateway.CommitAsync(new CommitRequest("feat", null, "フレームワーク経由"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(viaGateway.Committed, string.Join("\n", viaGateway.Problems));
        Assert.Equal(viaGateway.Commit, new CheckpointStore(f.Paths.CheckpointsDir).Load(f.Git.RepoPath));

        // 登録した人間の署名
        await SignedCommitAsync(f.Git, f.Human, "src/human.ts", "export const h = 1;\n", "feat: 人間が直接書いた\n\nType: feat");

        // 本流から取り込んだマージ（署名なしだが、取り込みの記録にある）
        var head = await f.Git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken) ?? string.Empty;
        await f.Git.OutputAsync(["checkout", "-q", "-b", "side", "HEAD~1"], cancellationToken: TestContext.Current.CancellationToken);
        await SignedCommitAsync(f.Git, f.Framework, "src/side.ts", "export const s = 1;\n", "feat: 別の作業\n\nType: feat");
        await f.Git.OutputAsync(["checkout", "-q", "main"], cancellationToken: TestContext.Current.CancellationToken);
        await f.Git.OutputAsync(["merge", "-q", "--no-ff", "--no-edit", "side"], cancellationToken: TestContext.Current.CancellationToken);
        var merge = await f.Git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken) ?? string.Empty;
        Assert.NotEqual(head, merge);
        new MergeRecords(f.Paths.MergesPath).Add(new MergeRecord(merge, DateTimeOffset.UtcNow, "custos", PullRequest: "#1"));

        var report = await guard.CheckAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointState.AdvancedKnown, report.State);
        Assert.True(report.Ok);
    }

    [Fact]
    public async Task DirectCommitIsDetectedAndRecovered()
    {
        using var f = await SetupAsync();
        var guard = f.Guard();
        var checkpoint = (await guard.AdvanceAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken)).Head;
        await TestGit.CommitFileAsync(f.Git, "src/game.ts", "export const a = 3;\n", "直接のコミット");
        await SignedCommitAsync(f.Git, f.Stranger, "src/x.ts", "x\n", "知らない鍵");

        var report = await guard.CheckAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointState.UnsignedCommits, report.State);
        Assert.False(report.Ok);
        Assert.Equal(2, report.Unknown.Count);
        Assert.Equal(SignaturePolicy.Describe(SignatureTrust.NotSigned), report.Unknown[0].Reason);
        Assert.Equal(SignaturePolicy.Describe(SignatureTrust.UnknownKey), report.Unknown[1].Reason);

        // 入口も、検知した状態ではコミットを受け付けない
        await TestGit.StageFileAsync(f.Git, "src/y.ts", "y\n");
        var refused = await f.Gateway().CommitAsync(new CommitRequest("feat", null, "続き"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(refused.Committed);

        var recovery = await guard.RecoverDirectCommitsAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(recovery.Done);
        Assert.Equal(2, recovery.UndoneCommits.Count);
        Assert.Equal(checkpoint, await f.Git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("export const a = 3;\n", File.ReadAllText(Path.Combine(f.Git.RepoPath, "src", "game.ts")));
        var staged = await f.Git.StagedChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(staged, e => e.Path == "src/game.ts");

        var recommit = await f.Gateway().CommitAsync(new CommitRequest("feat", null, "分類し直してコミット"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(recommit.Committed, string.Join("\n", recommit.Problems));
        Assert.Equal(CheckpointState.Same, (await guard.CheckAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task RewrittenHistoryIsDetectedAndPreparedForReimport()
    {
        using var f = await SetupAsync();
        var guard = f.Guard();
        await TestGit.StageFileAsync(f.Git, "src/game.ts", "export const a = 4;\n");
        var committed = await f.Gateway().CommitAsync(new CommitRequest("feat", null, "チェックポイントになるコミット"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(committed.Committed, string.Join("\n", committed.Problems));
        var checkpoint = committed.Commit ?? string.Empty;

        // 履歴を書き換える（チェックポイントを含まない履歴にする）
        await f.Git.OutputAsync(["reset", "-q", "--hard", "HEAD~1"], cancellationToken: TestContext.Current.CancellationToken);
        await SignedCommitAsync(f.Git, f.Framework, "src/game.ts", "export const a = 5;\n", "feat: 書き換えた後\n\nType: feat");

        var report = await guard.CheckAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(CheckpointState.HistoryRewritten, report.State);
        Assert.False(report.Ok);
        Assert.Equal(checkpoint, report.Checkpoint);

        var recovery = await guard.PrepareReimportAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(recovery.Done);
        Assert.Equal(checkpoint, await f.Git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("export const a = 5;\n", File.ReadAllText(Path.Combine(f.Git.RepoPath, "src", "game.ts")));
        Assert.Equal(CheckpointState.Same, (await guard.CheckAsync(f.Git, cancellationToken: TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task IntakeAcceptsSignedClassifiedCommits()
    {
        using var f = await SetupAsync();
        var start = await f.Git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken) ?? string.Empty;
        var gateway = new CommitGateway(f.Git, new CommitGatewayOptions(f.Paths, RequireRegressionTest: true, Sign: true, SigningKeyPath: f.Framework.PrivateKeyPath));
        await TestGit.StageFileAsync(f.Git, "src/game.ts", "export const a = 6;\n");
        await TestGit.StageFileAsync(f.Git, "src/__tests__/game.test.ts", "expect(a).toBe(6);\n");
        Assert.True((await gateway.CommitAsync(new CommitRequest("fix", null, "修正"), cancellationToken: TestContext.Current.CancellationToken)).Committed);
        await TestGit.StageFileAsync(f.Git, "src/lobby.ts", "export const l = 1;\n");
        Assert.True((await gateway.CommitAsync(new CommitRequest("feat", null, "機能"), cancellationToken: TestContext.Current.CancellationToken)).Committed);

        var report = await IntakeChecker.CheckAsync(f.Git, start, "HEAD", new IntakeOptions(f.Paths.AllowedSignersPath), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Ok, report.Summarize());
        Assert.Equal(2, report.Commits.Count);
        Assert.Equal(["fix", "feat"], report.Commits.Select(c => c.Type).ToList());
        Assert.All(report.Commits, c => Assert.Equal(SignatureTrust.Framework, c.Trust));
    }

    [Fact]
    public async Task IntakeRejectsUnsignedUnclassifiedAndIncompleteFixes()
    {
        using var f = await SetupAsync();
        var start = await f.Git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken) ?? string.Empty;
        await TestGit.CommitFileAsync(f.Git, "src/a.ts", "a\n", "feat: 署名なし\n\nType: feat");
        await SignedCommitAsync(f.Git, f.Framework, "src/b.ts", "b\n", "分類なし");
        await SignedCommitAsync(f.Git, f.Framework, "src/c.ts", "c\n", "fix: バグIDなし\n\nType: fix");
        await SignedCommitAsync(f.Git, f.Framework, "src/d.ts", "d\n", "fix: 回帰テストなし\n\nType: fix\nBug: BUG-0001");
        var report = await IntakeChecker.CheckAsync(f.Git, start, "HEAD", new IntakeOptions(f.Paths.AllowedSignersPath), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(report.Ok);
        Assert.Equal(4, report.Commits.Count);
        Assert.All(report.Commits, c => Assert.False(c.Ok));
        Assert.Contains("署名", report.Commits[0].Problems[0], StringComparison.Ordinal);
        Assert.Contains("分類", report.Commits[1].Problems[0], StringComparison.Ordinal);
        Assert.Contains(report.Commits[2].Problems, p => p.Contains("バグID", StringComparison.Ordinal));
        Assert.Contains(report.Commits[3].Problems, p => p.Contains("回帰テスト", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntakeRejectsForcePushAndBrokenCheckpoint()
    {
        using var f = await SetupAsync();
        var oldTip = await SignedCommitAsync(f.Git, f.Framework, "src/a.ts", "a\n", "feat: 元の先端\n\nType: feat");
        var store = new CheckpointStore(f.Paths.CheckpointsDir);
        store.Save(f.Git.RepoPath, oldTip);
        await f.Git.OutputAsync(["reset", "-q", "--hard", "HEAD~1"], cancellationToken: TestContext.Current.CancellationToken);
        await SignedCommitAsync(f.Git, f.Framework, "src/b.ts", "b\n", "feat: 書き換え\n\nType: feat");
        var report = await IntakeChecker.CheckAsync(f.Git, oldTip, "HEAD", new IntakeOptions(f.Paths.AllowedSignersPath, Checkpoints: store), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(report.Ok);
        Assert.Contains(report.Problems, p => p.Contains("強制プッシュ", StringComparison.Ordinal));
        Assert.Contains(report.Problems, p => p.Contains("チェックポイント", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntakeAcceptsRecordedMergeCommits()
    {
        using var f = await SetupAsync();
        var start = await f.Git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken) ?? string.Empty;
        await f.Git.OutputAsync(["checkout", "-q", "-b", "work"], cancellationToken: TestContext.Current.CancellationToken);
        await SignedCommitAsync(f.Git, f.Framework, "src/w.ts", "w\n", "feat: 作業\n\nType: feat");
        await f.Git.OutputAsync(["checkout", "-q", "main"], cancellationToken: TestContext.Current.CancellationToken);
        await SignedCommitAsync(f.Git, f.Human, "src/m.ts", "m\n", "chore: 本流\n\nType: chore");
        await f.Git.OutputAsync(["merge", "-q", "--no-ff", "--no-edit", "work"], cancellationToken: TestContext.Current.CancellationToken);
        var merge = await f.Git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken) ?? string.Empty;
        var merges = new MergeRecords(f.Paths.MergesPath);

        var before = await IntakeChecker.CheckAsync(f.Git, start, "HEAD", new IntakeOptions(f.Paths.AllowedSignersPath, Merges: merges), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(before.Ok);
        Assert.True(before.Commits.Single(c => c.IsMerge).Problems.Count > 0);

        merges.Add(new MergeRecord(merge, DateTimeOffset.UtcNow, "custos"));
        var after = await IntakeChecker.CheckAsync(f.Git, start, "HEAD", new IntakeOptions(f.Paths.AllowedSignersPath, Merges: merges), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(after.Ok, after.Summarize());
    }

    [Fact]
    public async Task PreReceiveHookRejectsPushesThatFailTheCheck()
    {
        using var temp = new TempDir();
        var bare = temp.Sub("main.git");
        await new GitClient(bare, TestGit.Runner).OutputAsync(["init", "-q", "--bare", "-b", "main"], cancellationToken: TestContext.Current.CancellationToken);

        // 本物の custos の代わりに、範囲の全コミットに Type: があるかだけを見る検査のコマンドを使う
        var fake = Path.Combine(temp.Path, "fake-custos");
        await File.WriteAllTextAsync(fake, """
            #!/bin/sh
            while [ $# -gt 0 ]; do
              case "$1" in
                --base) base="$2"; shift ;;
                --head) head="$2"; shift ;;
              esac
              shift
            done
            if [ "$base" = "0000000000000000000000000000000000000000" ]; then range="$head"; else range="$base..$head"; fi
            for c in $(git rev-list "$range"); do
              git log -1 --format=%B "$c" | grep -q '^Type: ' || { echo "missing Type in $c" >&2; exit 1; }
            done
            exit 0
            """.Replace("\r\n", "\n", StringComparison.Ordinal), cancellationToken: TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var script = PreReceiveHook.Generate([fake], ["refs/heads/main"], new Dictionary<string, string>(StringComparer.Ordinal) { ["CUSTOS_DATA_DIR"] = "/tmp/it's quoted" });
        Assert.Contains("export CUSTOS_DATA_DIR='/tmp/it'\\''s quoted'", script, StringComparison.Ordinal);
        PreReceiveHook.Install(bare, script);

        var work = await TestGit.InitAsync(temp.Sub("work"));
        await work.OutputAsync(["remote", "add", "origin", bare], cancellationToken: TestContext.Current.CancellationToken);
        await TestGit.CommitFileAsync(work, "a.txt", "a\n", "feat: 分類あり\n\nType: feat");
        var accepted = await work.RunAsync(["push", "-q", "origin", "main"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(accepted.Succeeded, accepted.StandardError);

        await TestGit.CommitFileAsync(work, "b.txt", "b\n", "分類なし");
        var rejected = await work.RunAsync(["push", "-q", "origin", "main"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(rejected.Succeeded);
        Assert.Contains("取り込みを拒否しました", rejected.StandardError, StringComparison.Ordinal);

        // 保護していないブランチは検査しない
        var other = await work.RunAsync(["push", "-q", "origin", "main:refs/heads/feature"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(other.Succeeded, other.StandardError);
    }
}
