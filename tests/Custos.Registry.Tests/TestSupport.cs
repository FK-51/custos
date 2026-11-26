using Custos.Core.Util;
using Custos.Registry.Git;

namespace Custos.Registry.Tests;

/// <summary>テストごとの一時ディレクトリ。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "custos-registry-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name)
    {
        var dir = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public RegistryPaths Paths() => new(
        System.IO.Path.Combine(Path, "data", "registry", "bugs.db"),
        System.IO.Path.Combine(Path, "data", "checkpoints"),
        System.IO.Path.Combine(Path, "data", "merges.jsonl"),
        System.IO.Path.Combine(Path, "data", "secrets"),
        System.IO.Path.Combine(Path, "verify", "keys", "allowed_signers"),
        System.IO.Path.Combine(Path, "verify", "registry"),
        System.IO.Path.Combine(Path, "verify", "registry", "confirm"));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 片付けに失敗してもテストの結果には影響しない
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}

/// <summary>利用者の git の設定に影響されない git の呼び出し。</summary>
internal static class TestGit
{
    public static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_CONFIG_NOSYSTEM"] = "1",
    };

    public static ProcessCommandRunner Runner { get; } = new(Environment);

    public static async Task<GitClient> InitAsync(string dir, string branch = "main")
    {
        Directory.CreateDirectory(dir);
        var git = new GitClient(dir, Runner);
        await git.OutputAsync(["init", "-q", "-b", branch]);
        await git.OutputAsync(["config", "user.name", "Test User"]);
        await git.OutputAsync(["config", "user.email", "test@example.invalid"]);
        await git.OutputAsync(["config", "commit.gpgsign", "false"]);
        return git;
    }

    public static async Task<string> CommitFileAsync(GitClient git, string relativePath, string content, string message)
    {
        var path = System.IO.Path.Combine(git.RepoPath, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? git.RepoPath);
        await File.WriteAllTextAsync(path, content);
        await git.OutputAsync(["add", "--", relativePath]);
        await git.OutputAsync(["commit", "-q", "-m", message]);
        return await git.HeadAsync() ?? throw new CustosException("HEAD がありません");
    }

    public static async Task StageFileAsync(GitClient git, string relativePath, string content)
    {
        var path = System.IO.Path.Combine(git.RepoPath, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? git.RepoPath);
        await File.WriteAllTextAsync(path, content);
        await git.OutputAsync(["add", "--", relativePath]);
    }
}
