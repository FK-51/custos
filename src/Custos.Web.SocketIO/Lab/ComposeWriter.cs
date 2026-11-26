using System.Globalization;
using System.Text;
using System.Text.Json;
using Custos.Core.Config;
using Custos.Core.Util;

namespace Custos.Web.SocketIO.Lab;

/// <summary>compose を作るときの選択。</summary>
public sealed record ComposeOptions(
    string SessionId,
    string RecordsDir,
    string AppImage,
    IReadOnlyDictionary<string, string> AgentImages,
    string SandboxImage,
    IReadOnlySet<string> Breakage,
    ResourceProfile? Profile,
    string? SecretFlagFile,
    bool Egress,
    string? EgressImage);

/// <summary>
/// environment.yaml（ノード構成）から compose のファイルを作る（設計書 第13.2節、第16.2節、指示書 付録B）。
/// - 利用者側（usernet）とアプリ側（appnet）のネットワークは internal（外部への出口なし）。
/// - 利用者側の通信は記録用のプロキシ（edge）を通る。アプリには直接届かない。
/// - 管理側の操作（ops）はアプリ側のネットワークにだけつなぎ、利用者側からは届かない。
/// - アプリのコンテナの環境変数とコマンドは本番と同じにする（検証用の値を足さない）。
/// </summary>
public static class ComposeWriter
{
    /// <summary>わざと壊す確認の名前（指示書 第5.2節）。</summary>
    public static class Breaks
    {
        /// <summary>ネットワークに外部への出口を付ける（E-2 が不合格になるべき）。</summary>
        public const string ExternalEgress = "external_egress";

        /// <summary>サンドボックスの枠に検証用リポジトリを見せる（E-3）。</summary>
        public const string SandboxSeesVerifyRepo = "sandbox_sees_verify_repo";

        /// <summary>プロキシで独自のヘッダーを足す（E-4）。</summary>
        public const string ProxyAddsHeader = "proxy_adds_header";

        /// <summary>プロキシを通さずにアプリへ接続する（C-4、E-1）。</summary>
        public const string BypassProxy = "bypass_proxy";

        /// <summary>ブラウザのユーザーエージェントを自動操作と分かる値にする（E-4）。</summary>
        public const string AutomationUserAgent = "automation_user_agent";

        /// <summary>リセットで DB の差し替えを省く（B-1）。</summary>
        public const string ResetSkipDbReplace = "reset_skip_db_replace";

        /// <summary>完了待ちをせずに状態を読む（B-2）。</summary>
        public const string ReadWithoutSettle = "read_without_settle";

        /// <summary>スクリーンショットを真っ白な画像に差し替える（C-2）。</summary>
        public const string BlankScreenshot = "blank_screenshot";

        /// <summary>設定ファイルの必須の項目を消す（A-2）。</summary>
        public const string ConfigMissingRequired = "config_missing_required";

        /// <summary>state.yaml に存在しない列を読むキーを足す（A-3）。</summary>
        public const string StateNonexistentColumn = "state_nonexistent_column";

        /// <summary>すべての壊し方。</summary>
        public static readonly IReadOnlyList<string> All =
        [
            ConfigMissingRequired, StateNonexistentColumn, ResetSkipDbReplace, ReadWithoutSettle, BlankScreenshot,
            BypassProxy, ExternalEgress, SandboxSeesVerifyRepo, ProxyAddsHeader, AutomationUserAgent,
        ];

        /// <summary>環境（compose）を作り直す必要がある壊し方。</summary>
        public static readonly IReadOnlyList<string> NeedRecreate = [BypassProxy, ExternalEgress, SandboxSeesVerifyRepo, ProxyAddsHeader];
    }

    /// <summary>compose の YAML を作る。</summary>
    public static string Write(ConfigSet config, Workspace workspace, ComposeOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(options);
        var env = config.Environment;
        var project = env.Project;
        var app = env.App;
        var y = new StringBuilder();
        y.AppendLine("# custos が environment.yaml から生成した。手で書き換えない。");
        y.AppendLine(CultureInfo.InvariantCulture, $"name: {Q(project)}");
        y.AppendLine("services:");

        // 対象アプリ
        y.AppendLine("  app:");
        y.AppendLine(CultureInfo.InvariantCulture, $"    image: {Q(options.AppImage)}");
        y.AppendLine(CultureInfo.InvariantCulture, $"    container_name: {Q(Containers.App(project))}");
        if (app.Command.Count > 0)
        {
            y.AppendLine(CultureInfo.InvariantCulture, $"    command: {List(app.Command)}");
        }

        if (app.Environment.Count > 0)
        {
            y.AppendLine("    environment:");
            foreach (var pair in app.Environment.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                y.AppendLine(CultureInfo.InvariantCulture, $"      {Q(pair.Key)}: {Q(pair.Value)}");
            }
        }

        y.AppendLine("    networks:");
        y.AppendLine("      appnet: {}");
        y.AppendLine("    volumes:");
        y.AppendLine(CultureInfo.InvariantCulture, $"      - {Q("appdata:" + app.DataDir)}");
        if (options.SecretFlagFile != null && config.Security?.SecretFile is { } secretPath)
        {
            y.AppendLine(CultureInfo.InvariantCulture, $"      - {Q($"{options.SecretFlagFile}:{secretPath}:ro")}");
        }

        y.AppendLine("    restart: \"no\"");
        AppendResources(y, options.Profile, "app");

        // 記録用のプロキシ
        var proxyCommand = new List<string>
        {
            "run", "--listen", $"0.0.0.0:{env.PublicPort.ToString(CultureInfo.InvariantCulture)}",
            "--upstream", $"app:{app.Port.ToString(CultureInfo.InvariantCulture)}",
            "--records", "/records", "--control", "/control", "--block", "/__verify/",
            "--socketio-path", app.SocketIoPath,
        };
        if (options.Breakage.Contains(Breaks.ProxyAddsHeader))
        {
            proxyCommand.Add("--break-add-header");
        }

        var netem = Netem(options.Profile);
        if (netem != null)
        {
            proxyCommand.Add("--netem");
            proxyCommand.Add(netem);
        }

        y.AppendLine("  edge:");
        y.AppendLine(CultureInfo.InvariantCulture, $"    image: {Q(options.AgentImages["proxy"])}");
        y.AppendLine(CultureInfo.InvariantCulture, $"    container_name: {Q(Containers.Edge(project))}");
        y.AppendLine(CultureInfo.InvariantCulture, $"    command: {List(proxyCommand)}");
        y.AppendLine("    networks:");
        y.AppendLine("      usernet:");
        y.AppendLine(CultureInfo.InvariantCulture, $"        aliases: {List([env.PublicHost])}");
        y.AppendLine("      appnet: {}");
        y.AppendLine("    volumes:");
        y.AppendLine(CultureInfo.InvariantCulture, $"      - {Q(options.RecordsDir + ":/records")}");
        y.AppendLine(CultureInfo.InvariantCulture, $"      - {Q(workspace.ControlDir + ":/control:ro")}");
        if (netem != null)
        {
            y.AppendLine("    cap_add: [\"NET_ADMIN\"]");
        }

        y.AppendLine("    depends_on: [\"app\"]");

        // 管理側の操作
        y.AppendLine("  ops:");
        y.AppendLine(CultureInfo.InvariantCulture, $"    image: {Q(options.AgentImages["ops"])}");
        y.AppendLine(CultureInfo.InvariantCulture, $"    container_name: {Q(Containers.Ops(project))}");
        y.AppendLine("    command: [\"idle\"]");
        y.AppendLine("    networks:");
        y.AppendLine("      appnet: {}");
        y.AppendLine("    volumes:");
        y.AppendLine("      - \"appdata:/appdata\"");
        y.AppendLine(CultureInfo.InvariantCulture, $"      - {Q(workspace.SnapshotsDir + ":/snapshots")}");

        // 正直なクライアントと攻撃クライアント
        for (var i = 1; i <= env.Nodes.HonestClients; i++)
        {
            AppendClient(y, $"client-{i.ToString(CultureInfo.InvariantCulture)}", Containers.Client(project, i), options, i == 1);
        }

        for (var i = 1; i <= env.Nodes.AttackerClients; i++)
        {
            AppendClient(y, $"attacker-{i.ToString(CultureInfo.InvariantCulture)}", Containers.Attacker(project, i), options, false);
        }

        // ブラウザ
        for (var i = 1; i <= env.Nodes.Browsers; i++)
        {
            y.AppendLine(CultureInfo.InvariantCulture, $"  browser-{i.ToString(CultureInfo.InvariantCulture)}:");
            y.AppendLine(CultureInfo.InvariantCulture, $"    image: {Q(options.AgentImages["browser"])}");
            y.AppendLine(CultureInfo.InvariantCulture, $"    container_name: {Q(Containers.Browser(project, i))}");
            y.AppendLine("    command: [\"idle\"]");
            y.AppendLine("    shm_size: \"1gb\"");
            y.AppendLine("    networks:");
            y.AppendLine("      usernet: {}");
            if (options.Breakage.Contains(Breaks.ExternalEgress))
            {
                y.AppendLine("      outnet: {}");
            }
        }

        // AI用サンドボックスの枠（対象アプリの複製だけを見せる）
        if (env.Nodes.Sandbox)
        {
            y.AppendLine("  sandbox:");
            y.AppendLine(CultureInfo.InvariantCulture, $"    image: {Q(options.SandboxImage)}");
            y.AppendLine(CultureInfo.InvariantCulture, $"    container_name: {Q(Containers.Sandbox(project))}");
            y.AppendLine("    command: [\"sleep\", \"infinity\"]");
            y.AppendLine("    working_dir: \"/workspace\"");
            y.AppendLine("    networks:");
            y.AppendLine("      usernet: {}");
            if (options.Breakage.Contains(Breaks.ExternalEgress))
            {
                y.AppendLine("      outnet: {}");
            }

            y.AppendLine("    volumes:");
            y.AppendLine(CultureInfo.InvariantCulture, $"      - {Q(workspace.SandboxWorkspaceDir + ":/workspace")}");
            if (options.Breakage.Contains(Breaks.SandboxSeesVerifyRepo))
            {
                y.AppendLine(CultureInfo.InvariantCulture, $"      - {Q(workspace.VerifyRepo + ":/mnt/verify:ro")}");
            }

            if (options.Egress)
            {
                y.AppendLine("    environment:");
                y.AppendLine("      HTTPS_PROXY: \"http://egress:3128\"");
                y.AppendLine("      HTTP_PROXY: \"http://egress:3128\"");
                y.AppendLine("      NO_PROXY: \"localhost,127.0.0.1\"");
            }
        }

        // モデルのAPIへの出口（段階3。許可した宛先だけを通す）
        if (options.Egress && options.EgressImage != null)
        {
            y.AppendLine("  egress:");
            y.AppendLine(CultureInfo.InvariantCulture, $"    image: {Q(options.EgressImage)}");
            y.AppendLine(CultureInfo.InvariantCulture, $"    container_name: {Q(Containers.Egress(project))}");
            y.AppendLine(CultureInfo.InvariantCulture, $"    command: {List(["egress", "--listen", "0.0.0.0:3128", "--allow", string.Join(",", env.EgressAllow)])}");
            y.AppendLine("    networks:");
            y.AppendLine("      usernet: {}");
            y.AppendLine("      egressnet: {}");
        }

        y.AppendLine("networks:");
        y.AppendLine("  usernet:");
        y.AppendLine("    internal: true");
        y.AppendLine("  appnet:");
        y.AppendLine("    internal: true");
        if (options.Breakage.Contains(Breaks.ExternalEgress))
        {
            y.AppendLine("  outnet:");
            y.AppendLine("    internal: false");
        }

        if (options.Egress)
        {
            y.AppendLine("  egressnet:");
            y.AppendLine("    internal: false");
        }

        y.AppendLine("volumes:");
        y.AppendLine("  appdata: {}");
        return y.ToString();
    }

    private static void AppendClient(StringBuilder y, string service, string container, ComposeOptions options, bool first)
    {
        y.AppendLine(CultureInfo.InvariantCulture, $"  {service}:");
        y.AppendLine(CultureInfo.InvariantCulture, $"    image: {Q(options.AgentImages["client"])}");
        y.AppendLine(CultureInfo.InvariantCulture, $"    container_name: {Q(container)}");
        y.AppendLine("    command: [\"idle\"]");
        y.AppendLine("    networks:");
        y.AppendLine("      usernet: {}");
        if (first && options.Breakage.Contains(Breaks.BypassProxy))
        {
            // わざと壊す確認：アプリ側のネットワークにもつなぎ、プロキシを通さずに届くようにする
            y.AppendLine("      appnet: {}");
        }

        if (first && options.Breakage.Contains(Breaks.ExternalEgress))
        {
            y.AppendLine("      outnet: {}");
        }

        AppendResources(y, options.Profile, "load");
    }

    private static void AppendResources(StringBuilder y, ResourceProfile? profile, string node)
    {
        if (profile == null || !profile.Nodes.TryGetValue(node, out var resources))
        {
            return;
        }

        // CPU の制限は、時間の割り当て（クォータ）ではなくコアの固定で行う（第23.1節）
        if (resources.Cpuset != null)
        {
            y.AppendLine(CultureInfo.InvariantCulture, $"    cpuset: {Q(resources.Cpuset)}");
        }
        else if (resources.Cpus is { } cpus)
        {
            y.AppendLine(CultureInfo.InvariantCulture, $"    cpus: {cpus.ToString(CultureInfo.InvariantCulture)}");
        }

        if (resources.MemoryBytes is { } memory)
        {
            y.AppendLine(CultureInfo.InvariantCulture, $"    mem_limit: {memory.ToString(CultureInfo.InvariantCulture)}");
            y.AppendLine(CultureInfo.InvariantCulture, $"    memswap_limit: {memory.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>tc netem の指定を作る。</summary>
    public static string? Netem(ResourceProfile? profile)
    {
        if (profile?.Network is not { } network)
        {
            return null;
        }

        var parts = new List<string>();
        if (network.Latency is { } latency)
        {
            parts.Add("delay");
            parts.Add(((int)latency.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms");
            if (network.Jitter is { } jitter)
            {
                parts.Add(((int)jitter.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms");
            }
        }

        if (network.Loss is { } loss && loss > 0)
        {
            parts.Add("loss");
            parts.Add((loss * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%");
        }

        if (network.BandwidthBps is { } bps)
        {
            parts.Add("rate");
            parts.Add((bps / 1000).ToString(CultureInfo.InvariantCulture) + "kbit");
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static string Q(string value) => JsonSerializer.Serialize(value);

    private static string List(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Q)) + "]";
}
