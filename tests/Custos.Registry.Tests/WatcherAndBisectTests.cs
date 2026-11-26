using System.Text.Json;
using Custos.Core.Util;
using Custos.Registry.Commits;
using Custos.Registry.Git;

namespace Custos.Registry.Tests;

public class WatcherAndBisectTests
{
    /// <summary>gh だけを差し替え、ほかのコマンド（ssh-keygen）は本当に実行する。</summary>
    private sealed class FakeGh(string response) : ICommandRunner
    {
        public List<IReadOnlyList<string>> GhCalls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, string? standardInput, CancellationToken cancellationToken)
        {
            if (fileName == "gh")
            {
                GhCalls.Add(arguments);
                return Task.FromResult(new ProcessResult(0, response, string.Empty, TimeSpan.Zero, false));
            }

            return TestGit.Runner.RunAsync(fileName, arguments, workingDirectory, standardInput, cancellationToken);
        }
    }

    private static object ApiCommit(string sha, string message, string? signature, string? payload, params string[] parents) => new
    {
        sha,
        commit = new
        {
            message,
            verification = new { verified = signature != null, reason = signature != null ? "valid" : "unsigned", signature, payload },
        },
        parents = parents.Select(p => new { sha = p }).ToArray(),
    };

    [Fact]
    public async Task WatcherReportsCommitsThatAreNotKnown()
    {
        using var temp = new TempDir();
        var paths = temp.Paths();
        var git = await TestGit.InitAsync(temp.Sub("app"));
        var root = await TestGit.CommitFileAsync(git, "a.txt", "a\n", "init");
        var key = await SigningKeys.GenerateAsync(paths.SecretsDir, "framework", "custos-framework", cancellationToken: TestContext.Current.CancellationToken);
        var stranger = await SigningKeys.GenerateAsync(paths.SecretsDir, "stranger", "stranger", cancellationToken: TestContext.Current.CancellationToken);
        var allowed = AllowedSignersFile.Load(paths.AllowedSignersPath);
        allowed.Add("custos@framework", key.PublicKeyLine, "custos", SignerRole.Framework, new DateOnly(2020, 1, 1));
        allowed.Save(paths.AllowedSignersPath);

        async Task<(string Sha, SignedCommitParts Parts)> SignedAsync(SigningKey signer, string file, string message)
        {
            await TestGit.StageFileAsync(git, file, file + "\n");
            await git.OutputAsync(["-c", "gpg.format=ssh", "-c", "user.signingkey=" + signer.PrivateKeyPath, "commit", "-q", "-S", "-m", message]);
            var sha = await git.HeadAsync() ?? string.Empty;
            var raw = await git.RunAsync(["cat-file", "commit", sha]);
            return (sha, SignedCommitObject.Split(raw.StandardOutput));
        }

        var (signedSha, signedParts) = await SignedAsync(key, "b.txt", "feat: 署名あり（日本語）\n\nType: feat");
        var (strangerSha, strangerParts) = await SignedAsync(stranger, "c.txt", "feat: 知らない鍵");
        Assert.NotNull(signedParts.Signature);
        Assert.Contains("BEGIN SSH SIGNATURE", signedParts.Signature, StringComparison.Ordinal);
        Assert.DoesNotContain("gpgsig", signedParts.Payload, StringComparison.Ordinal);

        var merges = new MergeRecords(paths.MergesPath);
        merges.Add(new MergeRecord("mergedsha", DateTimeOffset.UtcNow, "custos", PullRequest: "#3"));
        var response = JsonSerializer.Serialize(new[]
        {
            ApiCommit("unsignedsha", "直接プッシュされた", null, null, strangerSha),
            ApiCommit(strangerSha, "feat: 知らない鍵", strangerParts.Signature, strangerParts.Payload, signedSha),
            ApiCommit("mergedsha", "Merge pull request #3", "-----BEGIN PGP SIGNATURE-----\nx\n-----END PGP SIGNATURE-----", "tree x", signedSha),
            ApiCommit(signedSha, "feat: 署名あり（日本語）", signedParts.Signature, signedParts.Payload, root),
            ApiCommit(root, "init", null, null),
        });
        var fake = new FakeGh(response);
        var watcher = new GitHubMainlineWatcher(fake, "owner/halpal", "main", paths.AllowedSignersPath, merges, temp.Sub("work"));
        var report = await watcher.CheckAsync(root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.ReachedKnownHead);
        Assert.Equal(4, report.Checked);
        Assert.False(report.Ok);
        Assert.Equal(["unsignedsha", strangerSha], report.Unknown.Select(u => u.Sha).ToList());
        Assert.Contains("repos/owner/halpal/commits?sha=main&per_page=100", Assert.Single(fake.GhCalls)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void CommitTimeIsReadFromCommitterLine()
    {
        var payload = "tree abc\nparent def\nauthor A <a@x> 1795588432 +0900\ncommitter A <a@x> 1795588432 +0900\n\nmessage\ncommitter fake 1 +0000\n";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795588432).UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), GitHubMainlineWatcher.CommitTime(payload));
        Assert.Null(GitHubMainlineWatcher.CommitTime("tree abc\n\nmessage"));
    }

    [Fact]
    public void AllowedSignersRoundTripKeepsRolesAndValidity()
    {
        var keyLine = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFWbfOKWAHMKTIaTitfG0eisZsVlNTj8xvluWYYu2JPV custos-framework";
        var file = AllowedSignersFile.Empty();
        var added = file.Add("custos@framework", keyLine, "custos", SignerRole.Framework, new DateOnly(2026, 11, 25));
        file.Retire(added.Fingerprint, new DateOnly(2027, 2, 18));
        var text = file.Render();
        Assert.Contains("valid-after=\"20261125\"", text, StringComparison.Ordinal);
        Assert.Contains("valid-before=\"20270219\"", text, StringComparison.Ordinal);
        var parsed = AllowedSignersFile.Parse(text);
        var entry = Assert.Single(parsed.Entries);
        Assert.Equal(SignerRole.Framework, entry.Role);
        Assert.Equal("custos", entry.Owner);
        Assert.Equal(new DateOnly(2027, 2, 18), entry.ValidBefore);
        Assert.Equal(parsed.Render(), text);
        Assert.Equal("SHA256:tFHUHR8qTeH6wKRfq6Aq0pSfOI29QIfOBYzoFa5By4I", entry.Fingerprint);
        Assert.True(entry.IsValidAt(new DateTimeOffset(2027, 2, 18, 23, 0, 0, TimeSpan.Zero)));
        Assert.False(entry.IsValidAt(new DateTimeOffset(2027, 2, 19, 0, 0, 0, TimeSpan.Zero)));
        Assert.Throws<CustosException>(() => file.Add("dup", keyLine, "x", SignerRole.Human, null));
        var removed = file.Remove(added.Fingerprint);
        Assert.Equal("custos@framework", removed.Principal);
        Assert.Empty(file.Entries);
    }

    [Fact]
    public void KeysWithoutRoleAreTreatedAsHuman()
    {
        var parsed = AllowedSignersFile.Parse("dev@example.invalid ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFWbfOKWAHMKTIaTitfG0eisZsVlNTj8xvluWYYu2JPV\n");
        Assert.Equal(SignerRole.Human, Assert.Single(parsed.Entries).Role);
    }

    [Fact]
    public async Task RetiredKeyStillVerifiesOlderCommitsButNotNewerOnes()
    {
        using var temp = new TempDir();
        var paths = temp.Paths();
        var git = await TestGit.InitAsync(temp.Sub("app"));
        await TestGit.CommitFileAsync(git, "a.txt", "a\n", "init");
        var key = await SigningKeys.GenerateAsync(paths.SecretsDir, "framework", "custos-framework", cancellationToken: TestContext.Current.CancellationToken);
        await TestGit.StageFileAsync(git, "b.txt", "b\n");
        await git.OutputAsync(["-c", "gpg.format=ssh", "-c", "user.signingkey=" + key.PrivateKeyPath, "commit", "-q", "-S", "-m", "signed"], cancellationToken: TestContext.Current.CancellationToken);

        var allowed = AllowedSignersFile.Empty();
        var entry = allowed.Add("custos@framework", key.PublicKeyLine, "custos", SignerRole.Framework, new DateOnly(2020, 1, 1));
        allowed.Retire(entry.Fingerprint, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30));
        allowed.Save(paths.AllowedSignersPath);
        Assert.Equal('G', (await git.CommitAsync("HEAD", paths.AllowedSignersPath, cancellationToken: TestContext.Current.CancellationToken)).SignatureStatus);

        allowed.Retire(entry.Fingerprint, new DateOnly(2021, 1, 1));
        allowed.Save(paths.AllowedSignersPath);
        var info = await git.CommitAsync("HEAD", paths.AllowedSignersPath, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SignatureTrust.UnknownKey, SignaturePolicy.Classify(info, AllowedSignersFile.Load(paths.AllowedSignersPath)));
    }

    [Fact]
    public async Task BisectFindsTheIntroducingCommit()
    {
        using var temp = new TempDir();
        var git = await TestGit.InitAsync(temp.Sub("app"));
        var good = await TestGit.CommitFileAsync(git, "app.txt", "ok 1\n", "c1");
        await TestGit.CommitFileAsync(git, "app.txt", "ok 2\n", "c2");
        var culprit = await TestGit.CommitFileAsync(git, "app.txt", "ok 3 BUG\n", "c3");
        await TestGit.CommitFileAsync(git, "other.txt", "x\n", "c4");
        await TestGit.CommitFileAsync(git, "app.txt", "ok 5 BUG\n", "c5");
        var before = await git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await BisectRunner.FindIntroducingCommitAsync(
            git,
            good,
            "HEAD",
            ["sh", "-c", "! grep -q BUG app.txt"],
            temp.Sub("worktrees"),
            TestGit.Runner, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(culprit, result.FirstBadCommit);
        Assert.Equal(before, await git.HeadAsync(cancellationToken: TestContext.Current.CancellationToken));
        var worktrees = await git.OutputAsync(["worktree", "list", "--porcelain"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain("custos-bisect-", worktrees, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temp.Path, "worktrees")));
    }
}
