using Custos.Registry.Bugs;
using Custos.Registry.Commits;
using Custos.Registry.Git;

namespace Custos.Registry.Tests;

public class CommitGatewayTests
{
    private static async Task<(TempDir Temp, GitClient Git, RegistryPaths Paths)> SetupAsync()
    {
        var temp = new TempDir();
        var git = await TestGit.InitAsync(temp.Sub("app"));
        await TestGit.CommitFileAsync(git, "src/game.ts", "export const a = 1;\n", "init");
        return (temp, git, temp.Paths());
    }

    [Fact]
    public async Task RejectsCommitWithoutType()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            var before = await git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken);
            await TestGit.StageFileAsync(git, "src/game.ts", "export const a = 2;\n");
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths));
            var result = await gateway.CommitAsync(new CommitRequest(null, null, "種類のない変更"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.Committed);
            Assert.Contains("種類", Assert.Single(result.Problems), StringComparison.Ordinal);
            Assert.Equal(before, await git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken));

            var invalid = await gateway.CommitAsync(new CommitRequest("feature", null, "x"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(invalid.Committed);
            Assert.Equal(before, await git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task FixCommitIsLinkedToRegistryRecord()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            await TestGit.StageFileAsync(git, "src/game.ts", "export const a = 2;\n");
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths));
            var result = await gateway.CommitAsync(new CommitRequest("fix", "BUG-0142", "注文キャンセル後に支払いができる問題を修正"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Committed, string.Join("\n", result.Problems));
            Assert.Equal("BUG-0142", result.BugId);
            Assert.True(result.BugCreated);

            var message = await git.OutputAsync(["log", "-1", "--format=%B"], cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("fix: 注文キャンセル後に支払いができる問題を修正\n\nType: fix\nBug: BUG-0142", message);

            var admin = BugRegistry.OpenAdmin(paths.RegistryDb);
            var bug = admin.Get("BUG-0142");
            Assert.NotNull(bug);
            Assert.Equal(result.Commit, bug.FixedIn);
            Assert.Equal("dev_agent", bug.FoundBy);
            Assert.Equal("implementation", bug.FoundStage);
            Assert.Contains(admin.Links("BUG-0142"), l => l.Kind == "fix_commit" && l.Reference == result.Commit);
        }
    }

    [Fact]
    public async Task FixWithoutBugCreatesRecordAndExistingBugIsReused()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths));
            await TestGit.StageFileAsync(git, "src/game.ts", "export const a = 3;\n");
            var first = await gateway.CommitAsync(new CommitRequest("fix", null, "境界値のずれを修正", FoundBy: "human"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(first.Committed);
            Assert.Equal("BUG-0001", first.BugId);
            Assert.Equal("human", BugRegistry.OpenAdmin(paths.RegistryDb).Get("BUG-0001")?.FoundBy);

            await TestGit.StageFileAsync(git, "src/game.ts", "export const a = 4;\n");
            var second = await gateway.CommitAsync(new CommitRequest("fix", "BUG-0001", "同じバグの追加修正"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(second.Committed);
            Assert.False(second.BugCreated);
            Assert.Equal(second.Commit, BugRegistry.OpenAdmin(paths.RegistryDb).Get("BUG-0001")?.FixedIn);
        }
    }

    [Fact]
    public async Task OtherTypesAreRecordedWithoutBug()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths));
            await TestGit.StageFileAsync(git, "src/lobby.ts", "export const lobby = true;\n");
            var feat = await gateway.CommitAsync(new CommitRequest("feat", null, "ロビーを追加"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(feat.Committed);
            Assert.Equal(("feat", (string?)null), CommitTrailers.Parse(await git.OutputAsync(["log", "-1", "--format=%B"], cancellationToken: TestContext.Current.CancellationToken)));

            await TestGit.StageFileAsync(git, "src/lobby.ts", "export const lobby = false;\n");
            var withBug = await gateway.CommitAsync(new CommitRequest("feat", "BUG-0001", "x"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(withBug.Committed);

            var chore = await gateway.CommitAsync(new CommitRequest("chore", null, "片付け"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(chore.Committed, string.Join("\n", chore.Problems));
            Assert.Equal(("chore", (string?)null), CommitTrailers.Parse(await git.OutputAsync(["log", "-1", "--format=%B"], cancellationToken: TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task RejectsWhenNothingIsStaged()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths));
            var result = await gateway.CommitAsync(new CommitRequest("chore", null, "空のコミット"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.Committed);
            Assert.Contains("変更がありません", Assert.Single(result.Problems), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RequireRegressionTestRejectsFixWithoutTest()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths, RequireRegressionTest: true));
            await TestGit.StageFileAsync(git, "src/game.ts", "export const a = 5;\n");
            var rejected = await gateway.CommitAsync(new CommitRequest("fix", null, "回帰テストなし"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(rejected.Committed);
            Assert.Contains("回帰テスト", rejected.Problems[0], StringComparison.Ordinal);
            Assert.Null(BugRegistry.OpenAdmin(paths.RegistryDb).Get("BUG-0001"));

            await TestGit.StageFileAsync(git, "src/__tests__/game.test.ts", "expect(a).toBe(5);\n");
            var accepted = await gateway.CommitAsync(new CommitRequest("fix", null, "回帰テストあり"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(accepted.Committed, string.Join("\n", accepted.Problems));
        }
    }

    [Fact]
    public async Task DetectMixedRejectsFeatChangingExpectations()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            await TestGit.CommitFileAsync(git, "src/game.test.ts", "expect(total()).toBe(10);\n", "test");
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths, DetectMixed: true));
            await TestGit.StageFileAsync(git, "src/game.test.ts", "expect(total()).toBe(12);\n");
            await TestGit.StageFileAsync(git, "src/bonus.ts", "export const bonus = 2;\n");
            var result = await gateway.CommitAsync(new CommitRequest("feat", null, "ボーナスを追加"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.Committed);
            Assert.Contains("分割", result.Problems[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SignedCommitIsTrustedAsFramework()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            var key = await SigningKeys.GenerateAsync(paths.SecretsDir, "framework_ed25519", "custos-framework", cancellationToken: TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key.PrivateKeyPath));
            }
            await Assert.ThrowsAsync<Custos.Core.Util.CustosException>(() => SigningKeys.GenerateAsync(paths.SecretsDir, "framework_ed25519", "again", cancellationToken: TestContext.Current.CancellationToken));
            var allowed = AllowedSignersFile.Load(paths.AllowedSignersPath);
            allowed.Add("custos@framework", key.PublicKeyLine, "custos", SignerRole.Framework, new DateOnly(2020, 1, 1));
            allowed.Save(paths.AllowedSignersPath);

            await TestGit.StageFileAsync(git, "src/game.ts", "export const a = 6;\n");
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths, Sign: true, SigningKeyPath: key.PrivateKeyPath));
            var result = await gateway.CommitAsync(new CommitRequest("refactor", null, "名前を整理"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Committed, string.Join("\n", result.Problems));
            var info = await git.CommitAsync("HEAD", paths.AllowedSignersPath, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal('G', info.SignatureStatus);
            Assert.Equal(key.Fingerprint, info.KeyFingerprint);
            Assert.Equal(SignatureTrust.Framework, SignaturePolicy.Classify(info, AllowedSignersFile.Load(paths.AllowedSignersPath)));
            var verify = await git.RunAsync(["-c", "gpg.ssh.allowedSignersFile=" + paths.AllowedSignersPath, "verify-commit", "HEAD"], cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(verify.Succeeded, verify.StandardError);
        }
    }

    [Fact]
    public async Task CopiesRegressionTestsIntoVerifyRepository()
    {
        var (temp, git, paths) = await SetupAsync();
        using (temp)
        {
            var verifyRoot = Path.Combine(temp.Path, "verify");
            var gateway = new CommitGateway(git, new CommitGatewayOptions(paths, RequireRegressionTest: true, CopyRegressionTests: true, VerifyRepoRoot: verifyRoot));
            await TestGit.StageFileAsync(git, "src/game.ts", "export const a = 7;\n");
            await TestGit.StageFileAsync(git, "src/__tests__/game.test.ts", "expect(a).toBe(7);\n");
            var result = await gateway.CommitAsync(new CommitRequest("fix", "BUG-0007", "値の誤りを修正"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Committed, string.Join("\n", result.Problems));
            Assert.NotNull(result.ConfirmCopy);
            Assert.Equal("registry/confirm/BUG-0007", result.ConfirmCopy.RelativeRef);
            var copied = Path.Combine(paths.ConfirmDir, "BUG-0007", "src", "__tests__", "game.test.ts");
            Assert.Equal("expect(a).toBe(7);\n", File.ReadAllText(copied));
            Assert.Equal("registry/confirm/BUG-0007", BugRegistry.OpenAdmin(paths.RegistryDb).Get("BUG-0007")?.ConfirmRef);
        }
    }
}
