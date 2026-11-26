using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Util;
using Custos.Web.SocketIO.Docker;

namespace Custos.Web.SocketIO.Lab;

/// <summary>
/// コンテナのイメージを作る。
/// - フレームワークの部品（proxy、ops、client、browser）は、フレームワークのリポジトリから発行して作る。
/// - 対象アプリは、アプリのリポジトリの Dockerfile をそのまま使う（変更しない）。土台のイメージはダイジェストで固定する。
/// </summary>
public sealed class ImageBuilder
{
    private static readonly IReadOnlyDictionary<string, (string Project, string Base)> Agents = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["proxy"] = ("Custos.Agent.Proxy", "runtime_deps"),
        ["ops"] = ("Custos.Agent.Ops", "runtime_deps"),
        ["client"] = ("Custos.Agent.Client", "runtime_deps"),
        ["browser"] = ("Custos.Agent.Browser", "playwright"),
    };

    private readonly Workspace _workspace;
    private readonly DockerCli _docker;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public ImageBuilder(Workspace workspace, DockerCli docker, Action<string> log)
    {
        _workspace = workspace;
        _docker = docker;
        _log = log;
    }

    /// <summary>フレームワークの土台のイメージ（deploy/base-images.json）。</summary>
    public IReadOnlyDictionary<string, string> BaseImages()
    {
        var path = Path.Combine(_workspace.FrameworkRepo, "deploy", "base-images.json");
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new CustosException("deploy/base-images.json を読めません");
        return node.Where(p => !p.Key.StartsWith('_')).ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal);
    }

    /// <summary>部品のイメージを作る（内容が変わっていなければ作り直さない）。返り値は部品の名前からイメージの名前。</summary>
    public async Task<IReadOnlyDictionary<string, string>> BuildAgentsAsync(CancellationToken cancellationToken)
    {
        var arch = await _docker.ArchitectureAsync(cancellationToken).ConfigureAwait(false);
        var rid = arch == "arm64" ? "linux-arm64" : "linux-x64";
        var bases = BaseImages();
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, (project, baseKey)) in Agents)
        {
            var context = Path.Combine(_workspace.BuildDir, name);
            var publish = Path.Combine(context, "publish");
            if (Directory.Exists(publish))
            {
                Directory.Delete(publish, recursive: true);
            }

            Directory.CreateDirectory(context);
            _log($"部品を発行しています：{name}（{rid}）");
            var dotnet = DotnetExecutable();
            var result = await ProcessRunner.RunAsync(
                dotnet,
                [
                    "publish", Path.Combine(_workspace.FrameworkRepo, "src", project, project + ".csproj"),
                    "-c", "Release", "-r", rid, "--self-contained", "true", "-o", publish,
                    "--artifacts-path", Path.Combine(_workspace.BuildDir, "artifacts"),
                    "-p:DebugType=none", "-p:GenerateDocumentationFile=false",
                ],
                new Dictionary<string, string?> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1" },
                _workspace.FrameworkRepo,
                timeout: TimeSpan.FromMinutes(15),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            result.EnsureSuccess($"{project} の発行");
            File.Copy(Path.Combine(_workspace.FrameworkRepo, "deploy", name + ".Dockerfile"), Path.Combine(context, "Dockerfile"), overwrite: true);
            var hash = Hashing.HashDirectory(context)[..16];
            var tag = $"custos-{name}:{FrameworkInfo.Version}-{hash}";
            if (!await ImageExistsAsync(tag, cancellationToken).ConfigureAwait(false))
            {
                _log($"イメージを作っています：{tag}");
                await _docker.RunCheckedAsync(["build", "-t", tag, "--build-arg", "BASE=" + bases[baseKey], "--label", "custos.component=" + name, context], $"{name} のイメージの作成", TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
            }

            images[name] = tag;
        }

        return images;
    }

    /// <summary>サンドボックスの枠のイメージを作る。</summary>
    public async Task<string> BuildSandboxAsync(CancellationToken cancellationToken)
    {
        var context = Path.Combine(_workspace.BuildDir, "sandbox");
        Directory.CreateDirectory(context);
        File.Copy(Path.Combine(_workspace.FrameworkRepo, "deploy", "sandbox.Dockerfile"), Path.Combine(context, "Dockerfile"), overwrite: true);
        var bases = BaseImages();
        var hash = Hashing.Sha256Hex(File.ReadAllText(Path.Combine(context, "Dockerfile")) + bases["node"])[..16];
        var tag = $"custos-sandbox:{hash}";
        if (!await ImageExistsAsync(tag, cancellationToken).ConfigureAwait(false))
        {
            _log($"イメージを作っています：{tag}");
            await _docker.RunCheckedAsync(["build", "-t", tag, "--build-arg", "BASE=" + bases["node"], context], "サンドボックスのイメージの作成", TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
        }

        return tag;
    }

    /// <summary>
    /// 解析の外部コンポーネント（画像。設計書 第12.8節）のイメージを作る。フレームワークの analyzers/image がなければ作らない。
    /// タグは中身のハッシュなので、同じ中身なら作り直さない。
    /// </summary>
    public async Task<string?> BuildAnalyzerAsync(CancellationToken cancellationToken)
    {
        var source = Path.Combine(_workspace.FrameworkRepo, "analyzers", "image");
        if (!Directory.Exists(source))
        {
            return null;
        }

        var bases = BaseImages();
        var hash = Hashing.Sha256Hex(string.Concat(Directory.GetFiles(source).Order(StringComparer.Ordinal).Select(File.ReadAllText)) + bases.GetValueOrDefault("python"))[..16];
        var tag = $"{Analysis.AnalyzerComponent.Image}:{hash}";
        if (!await ImageExistsAsync(tag, cancellationToken).ConfigureAwait(false))
        {
            _log($"イメージを作っています：{tag}");
            await _docker.RunCheckedAsync(["build", "-t", tag, "--build-arg", "BASE=" + bases["python"], source], "解析の外部コンポーネントのイメージの作成", TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
        }

        return tag;
    }

    /// <summary>
    /// 対象アプリのイメージを作る。アプリの Dockerfile は変えない。土台のイメージは、ダイジェストで取得した物にタグを付け直して固定する。
    /// 返り値はイメージの名前と ID。
    /// </summary>
    public async Task<(string Image, string Id)> BuildAppAsync(ConfigSet config, string appRepo, string kind, CancellationToken cancellationToken, bool calibration = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        var app = config.Environment.App;
        foreach (var (reference, digest) in app.ImagePins)
        {
            var repository = reference.Contains(':', StringComparison.Ordinal) ? reference[..reference.LastIndexOf(':')] : reference;
            var pinned = $"{repository}@{digest}";
            if (!await ImageExistsAsync(pinned, cancellationToken).ConfigureAwait(false))
            {
                _log($"土台のイメージを取得しています：{pinned}");
                await _docker.RunCheckedAsync(["pull", pinned], $"{pinned} の取得", TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
            }

            await _docker.RunCheckedAsync(["tag", pinned, reference], $"{reference} のタグ付け", TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        }

        var commit = await GitHeadAsync(appRepo, cancellationToken).ConfigureAwait(false);
        var context = Path.GetFullPath(Path.Combine(appRepo, app.BuildContext == "${APP_REPO}" ? "." : app.BuildContext));
        var tag = calibration ? $"{config.Environment.Project}-app:calib-{commit}" : $"{config.Environment.Project}-app:{commit}";
        _log($"対象アプリのイメージを作っています：{tag}（{context}）");
        string[] labels = calibration ? ["--label", "custos.kind=calibration", "--label", "custos.deployable=false"] : ["--label", "custos.kind=production"];
        await _docker.RunCheckedAsync(["build", "-t", tag, "-f", Path.Combine(context, app.Dockerfile), .. labels, context], "対象アプリのイメージの作成", TimeSpan.FromMinutes(40), cancellationToken).ConfigureAwait(false);
        if (kind == "test")
        {
            tag = await BuildTestBuildAsync(config, tag, cancellationToken).ConfigureAwait(false);
        }

        var inspect = await _docker.InspectAsync(tag, cancellationToken).ConfigureAwait(false);
        return (tag, inspect?["Id"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>
    /// テスト用ビルド（段階2）：本番のイメージの上にテスト用APIを preload で組み込む。
    /// 定義は検証用リポジトリの environment/testbuild/（アプリのリポジトリには置かない）。
    /// </summary>
    private async Task<string> BuildTestBuildAsync(ConfigSet config, string productionTag, CancellationToken cancellationToken)
    {
        var dockerfile = config.Environment.App.TestBuildDockerfile ?? "environment/testbuild/Dockerfile";
        var dockerfilePath = Path.Combine(_workspace.VerifyRepo, dockerfile);
        if (!File.Exists(dockerfilePath))
        {
            throw new CustosException($"テスト用ビルドの定義がありません：{dockerfilePath}");
        }

        var context = Path.Combine(_workspace.BuildDir, "testbuild");
        if (Directory.Exists(context))
        {
            Directory.Delete(context, recursive: true);
        }

        Directory.CreateDirectory(context);
        CopyDirectory(Path.Combine(_workspace.FrameworkRepo, "adapters", "node-testapi"), Path.Combine(context, "testapi"), skip: "node_modules");
        CopyDirectory(Path.GetDirectoryName(dockerfilePath) ?? _workspace.VerifyRepo, Path.Combine(context, "app"), skip: null);
        var tag = productionTag + "-test";
        _log($"テスト用ビルドを作っています：{tag}");
        await _docker.RunCheckedAsync(["build", "-t", tag, "-f", Path.Combine(context, "app", Path.GetFileName(dockerfilePath)), "--build-arg", "APP_IMAGE=" + productionTag, "--label", "custos.kind=test", context], "テスト用ビルドの作成", TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
        return tag;
    }

    /// <summary>
    /// 校正用ビルド（段階4）：パッチを当てたアプリの複製からイメージを作る。配備できないようにラベルで止める（第19.4節）。
    /// </summary>
    public async Task<(string Image, string Id)> BuildCalibrationAsync(ConfigSet config, string patchedRepo, string buildId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var app = config.Environment.App;
        var tag = $"{config.Environment.Project}-app:calib-{buildId}";
        _log($"校正用ビルドを作っています：{tag}");
        await _docker.RunCheckedAsync(["build", "-t", tag, "-f", Path.Combine(patchedRepo, app.Dockerfile), "--label", "custos.kind=calibration", "--label", "custos.deployable=false", patchedRepo], "校正用ビルドの作成", TimeSpan.FromMinutes(40), cancellationToken).ConfigureAwait(false);
        var inspect = await _docker.InspectAsync(tag, cancellationToken).ConfigureAwait(false);
        return (tag, inspect?["Id"]?.GetValue<string>() ?? string.Empty);
    }

    private static void CopyDirectory(string source, string destination, string? skip)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (skip != null && relative.Split(Path.DirectorySeparatorChar).Contains(skip, StringComparer.Ordinal))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Copy(file, target, overwrite: true);
        }
    }

    private async Task<bool> ImageExistsAsync(string reference, CancellationToken cancellationToken)
    {
        var result = await _docker.RunAsync(["image", "inspect", reference], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>リポジトリの HEAD（短い形）。作業中の変更があれば -dirty を付ける。</summary>
    public static async Task<string> GitHeadAsync(string repo, CancellationToken cancellationToken)
    {
        var head = await ProcessRunner.RunAsync("git", ["-C", repo, "rev-parse", "--short=12", "HEAD"], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!head.Succeeded)
        {
            return "nogit";
        }

        var status = await ProcessRunner.RunAsync("git", ["-C", repo, "status", "--porcelain"], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        return head.StandardOutput.Trim() + (status.StandardOutput.Trim().Length > 0 ? "-dirty" : string.Empty);
    }

    /// <summary>dotnet のコマンドの場所。</summary>
    public static string DotnetExecutable()
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (root != null && File.Exists(Path.Combine(root, "dotnet")))
        {
            return Path.Combine(root, "dotnet");
        }

        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        return File.Exists(home) ? home : "dotnet";
    }
}
