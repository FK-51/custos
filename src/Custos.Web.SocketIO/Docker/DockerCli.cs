using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Util;

namespace Custos.Web.SocketIO.Docker;

/// <summary>
/// docker のコマンドを呼ぶ（指示書 第3.2節「操作は docker compose のコマンドを呼ぶ形でよい」）。
/// 利用者の ~/.docker は使わず、データの置き場所の docker-config と、colima の専用プロファイルのソケットを使う。
/// </summary>
public sealed class DockerCli
{
    private readonly Dictionary<string, string?> _environment;

    /// <summary>設定を指定して作る。</summary>
    public DockerCli(string dockerConfigDir, string? dockerHost)
    {
        _environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DOCKER_CONFIG"] = dockerConfigDir,
            ["DOCKER_HOST"] = dockerHost ?? System.Environment.GetEnvironmentVariable("DOCKER_HOST"),
            ["DOCKER_CLI_HINTS"] = "false",
            ["BUILDKIT_PROGRESS"] = "plain",
        };
        Directory.CreateDirectory(dockerConfigDir);
        var configPath = Path.Combine(dockerConfigDir, "config.json");
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, "{\n  \"cliPluginsExtraDirs\": [\"/opt/homebrew/lib/docker/cli-plugins\", \"/usr/local/lib/docker/cli-plugins\"]\n}\n");
        }
    }

    /// <summary>docker のコマンドの場所。</summary>
    public static string Executable { get; } = FindDocker();

    private static string FindDocker()
    {
        foreach (var candidate in new[] { "/opt/homebrew/bin/docker", "/usr/local/bin/docker", "/usr/bin/docker" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "docker";
    }

    /// <summary>docker のコマンドに渡す環境変数。</summary>
    public IReadOnlyDictionary<string, string?> Environment => _environment;

    /// <summary>docker を実行する。</summary>
    public Task<ProcessResult> RunAsync(IEnumerable<string> arguments, TimeSpan? timeout = null, string? standardInput = null, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        ProcessRunner.RunAsync(Executable, arguments, _environment, workingDirectory, standardInput, timeout ?? TimeSpan.FromMinutes(10), cancellationToken);

    /// <summary>docker を実行し、失敗したら例外にする。</summary>
    public async Task<string> RunCheckedAsync(IEnumerable<string> arguments, string what, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(arguments, timeout, null, null, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess(what);
        return result.StandardOutput;
    }

    /// <summary>docker inspect の結果（1件目）。</summary>
    public async Task<JsonObject?> InspectAsync(string target, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["inspect", target], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        try
        {
            return (JsonNode.Parse(result.StandardOutput) as JsonArray)?.FirstOrDefault() as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Docker が動いているか、とサーバーの版。</summary>
    public async Task<(bool Ok, string Detail)> PingAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["version", "--format", "{{.Server.Version}} {{.Server.Arch}}"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        return (result.Succeeded, result.Succeeded ? result.StandardOutput.Trim() : result.StandardError.Trim());
    }

    /// <summary>Docker が動く機械のアーキテクチャ（arm64 / amd64）。</summary>
    public async Task<string> ArchitectureAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunCheckedAsync(["info", "--format", "{{.Architecture}}"], "docker info", TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var arch = output.Trim();
        return arch is "aarch64" or "arm64" ? "arm64" : "amd64";
    }
}
