using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Results;
using Custos.Core.Util;
using Custos.Web.SocketIO.Docker;

namespace Custos.Cli;

/// <summary>コマンドの実行に必要なもの（場所、設定、Docker、検証環境、結果の保存先）。</summary>
public sealed class CliContext : IAsyncDisposable
{
    private readonly CliArgs _args;
    private Workspace? _workspace;
    private ConfigLoadResult? _config;
    private DockerCli? _docker;
    private Web.SocketIO.Lab.Lab? _lab;

    /// <summary>作る。</summary>
    public CliContext(CliArgs args)
    {
        _args = args;
    }

    /// <summary>場所。</summary>
    public Workspace Workspace => _workspace ??= Workspace.Resolve(_args.Get("verify-repo"), _args.Get("data-dir"), _args.Get("app-repo"), _args.Get("framework-repo"));

    /// <summary>設定の読み込みの結果（誤りを含む）。</summary>
    public ConfigLoadResult ConfigResult => _config ??= ConfigLoader.Load(_args.Get("config-dir") ?? Workspace.ConfigDir);

    /// <summary>誤りのない設定。誤りがあれば例外。</summary>
    public ConfigSet Config => ConfigResult.Require();

    /// <summary>Docker。</summary>
    public DockerCli Docker => _docker ??= new DockerCli(Workspace.DockerConfigDir, _args.Get("docker-host") ?? DefaultDockerHost());

    /// <summary>検証環境。</summary>
    public Web.SocketIO.Lab.Lab Lab => _lab ??= new Web.SocketIO.Lab.Lab(Workspace, Config, Docker, Log);

    /// <summary>結果の保存先。</summary>
    public RunStore Results => new(Workspace.ResultsDir);

    /// <summary>JSON で出すか。</summary>
    public bool JsonOutput => _args.Flag("json");

    /// <summary>進み具合を標準エラー出力に書く。</summary>
    public static void Log(string message) => Console.Error.WriteLine(message);

    /// <summary>結果を標準出力に書く。</summary>
    public static void Out(string text) => Console.Out.WriteLine(text);

    /// <summary>JSON を整形して標準出力に書く。</summary>
    public static void OutJson(JsonNode? node) =>
        Console.Out.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null");

    /// <summary>colima の custos プロファイルのソケット（環境変数 DOCKER_HOST がなければ）。</summary>
    private static string? DefaultDockerHost()
    {
        var env = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        var colima = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colima", "custos", "docker.sock");
        return File.Exists(colima) ? "unix://" + colima : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_lab != null)
        {
            await _lab.DisposeAsync().ConfigureAwait(false);
        }
    }
}
