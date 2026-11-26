using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Custos.Core.Conformance;
using Custos.Core.Util;
using Custos.Web.SocketIO.Lab;

namespace Custos.Web.SocketIO.Conformance;

/// <summary>
/// E-1：利用者側のネットワークから、DB、テスト用API、結果の保存先、判定器に届かないこと。
/// サンドボックスの枠、スクリプトクライアント、ブラウザの各コンテナから、ファイルとしてもネットワークとしても届かない。
/// アプリへも、プロキシを通さずには届かない。
/// </summary>
public sealed class E1UserSideIsolation : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("E-1", ConformanceCategory.E, ItemKind.Required, "共通", "利用者側から管理側に届かない（ファイルとしても、ネットワークとしても）", "第17.3節 E、第12.6節");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var env = context.Config.Environment;
        var app = env.App;
        var forbiddenHosts = new List<(string Host, int Port, string What)>
        {
            ("app", app.Port, "アプリ（プロキシを通さない）"),
            ("app", app.ServerPort, "ゲームサーバー（プロキシを通さない）"),
            ("app", context.Config.App.TestApiPort, "テスト用API"),
            (Containers.App(lab.Project), app.Port, "アプリ（コンテナ名）"),
            ("ops", 22, "管理側の操作"),
            (Containers.Ops(lab.Project), 80, "管理側の操作（コンテナ名）"),
            ("host.docker.internal", 2375, "Docker のデーモン"),
            ("host.lima.internal", 22, "VM のホスト"),
        };
        var forbiddenPaths = new List<string>
        {
            "/appdata", "/records", "/snapshots", "/control", "/results", "/custos-results",
            context.Workspace.DataDir, context.Workspace.ResultsDir, context.Workspace.RecordsDir, context.Workspace.SnapshotsDir,
            context.Workspace.VerifyRepo, context.Workspace.FrameworkRepo, context.Workspace.RegistryPath,
        };
        var report = new JsonObject();
        var problems = new List<string>();
        foreach (var role in new[] { "client", "attacker", "browser" })
        {
            var agent = await AgentAsync(context, role, cancellationToken).ConfigureAwait(false);
            var entry = new JsonObject();
            var reachable = new JsonArray();
            foreach (var (host, port, what) in forbiddenHosts)
            {
                var probe = await agent.CallObjectAsync("probe.tcp", new JsonObject { ["host"] = host, ["port"] = port, ["timeout_ms"] = 1500 }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                if (probe["connected"]?.GetValue<bool>() == true)
                {
                    reachable.Add($"{host}:{port}（{what}）");
                    problems.Add($"{role} から {host}:{port}（{what}）に届きます");
                }
            }

            var dns = new JsonObject();
            foreach (var name in new[] { "app", "ops", Containers.App(lab.Project), Containers.Ops(lab.Project) })
            {
                var resolved = await agent.CallObjectAsync("probe.dns", new JsonObject { ["name"] = name, ["timeout_ms"] = 1500 }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                dns[name] = resolved["resolved"]?.GetValue<bool>() == true;
            }

            var files = await agent.CallAsync("probe.files", new JsonObject { ["paths"] = new JsonArray(forbiddenPaths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            foreach (var file in files.OfType<JsonObject>().Where(f => f["exists"]?.GetValue<bool>() == true))
            {
                problems.Add($"{role} から {file["path"]} が見えます");
            }

            var allowed = await agent.CallObjectAsync("probe.tcp", new JsonObject { ["host"] = env.PublicHost, ["port"] = env.PublicPort, ["timeout_ms"] = 3000 }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            entry["forbidden_reachable"] = reachable;
            entry["dns"] = dns;
            entry["files"] = files.DeepClone();
            entry["proxy_reachable"] = allowed["connected"]?.DeepClone();
            report[role] = entry;
            if (allowed["connected"]?.GetValue<bool>() != true)
            {
                problems.Add($"{role} から記録用のプロキシ（{env.PublicHost}:{env.PublicPort}）に届きません");
            }
        }

        // サンドボックスの枠（判定器の部品を入れないので、シェルで調べる）
        if (env.Nodes.Sandbox)
        {
            var sandbox = new JsonObject();
            foreach (var (host, port, what) in forbiddenHosts.Take(4))
            {
                var result = await lab.Docker.RunAsync(["exec", Containers.Sandbox(lab.Project), "bash", "-c", $"timeout 2 bash -c '</dev/tcp/{host}/{port.ToString(CultureInfo.InvariantCulture)}' 2>/dev/null && echo open || echo closed"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
                var open = result.StandardOutput.Trim() == "open";
                sandbox[$"{host}:{port}"] = open;
                if (open)
                {
                    problems.Add($"sandbox から {host}:{port}（{what}）に届きます");
                }
            }

            var paths = string.Join(' ', forbiddenPaths.Select(p => "'" + p + "'"));
            var fileCheck = await lab.Docker.RunAsync(["exec", Containers.Sandbox(lab.Project), "bash", "-c", $"for p in {paths}; do [ -e \"$p\" ] && echo \"$p\"; done; true"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var line in fileCheck.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                problems.Add($"sandbox から {line} が見えます");
            }

            sandbox["visible_paths"] = fileCheck.StandardOutput.Trim();
            report["sandbox"] = sandbox;
        }

        // マウントの検査（docker inspect）：利用者側のコンテナに、管理側の場所をマウントしていない
        foreach (var container in UserSideContainers(lab))
        {
            var inspect = await lab.Docker.InspectAsync(container, cancellationToken).ConfigureAwait(false);
            foreach (var mount in inspect?["Mounts"] as JsonArray ?? [])
            {
                var source = mount?["Source"]?.GetValue<string>() ?? string.Empty;
                if (source.Length == 0)
                {
                    continue;
                }

                var allowedMount = Workspace.IsInside(source, context.Workspace.SandboxWorkspaceDir);
                if (!allowedMount && (Workspace.IsInside(source, context.Workspace.DataDir) || Workspace.IsInside(source, context.Workspace.VerifyRepo) || Workspace.IsInside(source, context.Workspace.FrameworkRepo) || mount?["Name"]?.GetValue<string>()?.EndsWith("appdata", StringComparison.Ordinal) == true))
                {
                    problems.Add($"{container} に管理側の場所がマウントされています：{source}");
                }
            }
        }

        var path = Write(context, "e1-isolation", report);
        return problems.Count == 0
            ? Pass("利用者側の各コンテナから、アプリ（プロキシを通さない経路）、テスト用API、管理側、DB、記録、結果、フレームワーク、検証用リポジトリのどれにも届きません。届くのは記録用のプロキシだけです", [new Evidence("検査の結果", Rel(context, path))])
            : Fail(string.Join("／", problems.Take(6)), [new Evidence("検査の結果", Rel(context, path))]);
    }

    internal static IEnumerable<string> UserSideContainers(Lab.Lab lab)
    {
        var nodes = lab.Config.Environment.Nodes;
        for (var i = 1; i <= nodes.HonestClients; i++)
        {
            yield return Containers.Client(lab.Project, i);
        }

        for (var i = 1; i <= nodes.AttackerClients; i++)
        {
            yield return Containers.Attacker(lab.Project, i);
        }

        for (var i = 1; i <= nodes.Browsers; i++)
        {
            yield return Containers.Browser(lab.Project, i);
        }

        if (nodes.Sandbox)
        {
            yield return Containers.Sandbox(lab.Project);
        }
    }
}

/// <summary>E-2：すべてのコンテナから、外部への通信（名前の解決を含む）が失敗すること。</summary>
public sealed class E2NoExternalEgress : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("E-2", ConformanceCategory.E, ItemKind.Required, "共通", "外部へ通信できない（名前の解決を含む）", "第17.3節 E、第13.6節");

    private static readonly string[] ExternalNames = ["example.com", "registry.npmjs.org", "api.anthropic.com"];
    private static readonly (string Host, int Port)[] ExternalAddresses = [("1.1.1.1", 443), ("8.8.8.8", 53), ("93.184.215.14", 80)];

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var report = new JsonObject();
        var problems = new List<string>();

        // 部品の入ったコンテナ（ops、client、attacker、browser）は部品の検査で調べる
        foreach (var role in new[] { "ops", "client", "attacker", "browser" })
        {
            var agent = await AgentAsync(context, role, cancellationToken).ConfigureAwait(false);
            var entry = new JsonObject();
            foreach (var name in ExternalNames)
            {
                var dns = await agent.CallObjectAsync("probe.dns", new JsonObject { ["name"] = name, ["timeout_ms"] = 2500 }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                entry["dns:" + name] = dns["resolved"]?.DeepClone();
                if (dns["resolved"]?.GetValue<bool>() == true)
                {
                    problems.Add($"{role} で {name} の名前が解決できます");
                }
            }

            foreach (var (host, port) in ExternalAddresses)
            {
                var tcp = await agent.CallObjectAsync("probe.tcp", new JsonObject { ["host"] = host, ["port"] = port, ["timeout_ms"] = 2500 }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                entry[$"tcp:{host}:{port}"] = tcp["connected"]?.DeepClone();
                if (tcp["connected"]?.GetValue<bool>() == true)
                {
                    problems.Add($"{role} から {host}:{port} に接続できます");
                }
            }

            report[role] = entry;
        }

        // アプリ、プロキシ、サンドボックスはシェルで調べる（アプリのコンテナには部品を入れない）
        foreach (var (container, shell) in new[] { (Containers.App(lab.Project), "sh"), (Containers.Sandbox(lab.Project), "bash") })
        {
            var script = shell == "bash"
                ? "for n in example.com registry.npmjs.org; do getent hosts $n >/dev/null && echo dns:$n; done; for t in 1.1.1.1/443 8.8.8.8/53; do timeout 2 bash -c \"</dev/tcp/$t\" 2>/dev/null && echo tcp:$t; done; true"
                : "node -e \"const dns=require('dns'),net=require('net');let left=4;const done=()=>{if(--left===0)process.exit(0)};for(const n of ['example.com','registry.npmjs.org'])dns.lookup(n,(e)=>{if(!e)console.log('dns:'+n);done()});for(const [h,p] of [['1.1.1.1',443],['8.8.8.8',53]]){const s=net.connect(p,h,()=>{console.log('tcp:'+h+'/'+p);s.destroy();done()});s.setTimeout(2000,()=>{s.destroy();done()});s.on('error',()=>done())}\"";
            var result = await lab.Docker.RunAsync(["exec", container, shell, "-c", script], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
            var hits = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            report[container] = new JsonArray(hits.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
            foreach (var hit in hits)
            {
                problems.Add($"{container} で外部に届きます（{hit}）");
            }
        }

        var edge = await lab.Docker.RunAsync(["exec", "-i", Containers.Edge(lab.Project), "/custos/custos-proxy", "serve"], TimeSpan.FromSeconds(30), standardInput: "{\"id\":1,\"method\":\"probe.dns\",\"params\":{\"name\":\"example.com\",\"timeout_ms\":2500}}\n{\"id\":2,\"method\":\"probe.tcp\",\"params\":{\"host\":\"1.1.1.1\",\"port\":443,\"timeout_ms\":2500}}\n", cancellationToken: cancellationToken).ConfigureAwait(false);
        report["edge"] = edge.StandardOutput.Trim();
        if (edge.StandardOutput.Contains("\"resolved\":true", StringComparison.Ordinal) || edge.StandardOutput.Contains("\"connected\":true", StringComparison.Ordinal))
        {
            problems.Add("記録用のプロキシから外部に届きます");
        }

        var path = Write(context, "e2-egress", report);
        return problems.Count == 0
            ? Pass("すべてのコンテナで、外部の名前の解決と外部への接続が失敗しました", [new Evidence("検査の結果", Rel(context, path))])
            : Fail(string.Join("／", problems.Take(6)), [new Evidence("検査の結果", Rel(context, path))]);
    }
}

/// <summary>E-3：AIの作業領域（サンドボックスの枠）から、検証用リポジトリとフレームワークのリポジトリが見えないこと。</summary>
public sealed class E3SandboxSeesNoRepos : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("E-3", ConformanceCategory.E, ItemKind.Required, "共通", "サンドボックスの枠から、検証用リポジトリとフレームワークのリポジトリが見えない", "第17.3節 E、第12.6節");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        if (!context.Config.Environment.Nodes.Sandbox)
        {
            return Fail("サンドボックスの枠がありません（environment.yaml の nodes.sandbox）");
        }

        var container = Containers.Sandbox(lab.Project);
        var problems = new List<string>();

        // 目印：検証用リポジトリの .custos-repo-id、フレームワークのリポジトリの Custos.slnx と設計書
        var repoIdPath = Path.Combine(context.Workspace.VerifyRepo, ".custos-repo-id");
        var repoId = File.Exists(repoIdPath) ? File.ReadAllText(repoIdPath).Trim() : string.Empty;
        var needles = new List<string> { "Custos.slnx", "authority.yaml", "invariants.yaml" };
        var search = await lab.Docker.RunAsync(
            ["exec", container, "bash", "-c",
             (repoId.Length > 0 ? $"grep -rIl --exclude-dir=proc --exclude-dir=sys --exclude-dir=dev --exclude-dir=node_modules -s '{repoId}' / 2>/dev/null | head -5; " : string.Empty)
             + "find / \\( -path /proc -o -path /sys -o -path /dev -o -path '*/node_modules' \\) -prune -o \\( -name Custos.slnx -o -name authority.yaml -o -name invariants.yaml -o -name '.custos-repo-id' \\) -print 2>/dev/null | head -5; true"],
            TimeSpan.FromMinutes(2), cancellationToken: cancellationToken).ConfigureAwait(false);
        var found = search.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var hit in found)
        {
            problems.Add($"サンドボックスから見えます：{hit}");
        }

        // マウントは、対象アプリの複製（作業領域）だけであること
        var inspect = await lab.Docker.InspectAsync(container, cancellationToken).ConfigureAwait(false);
        var mounts = new JsonArray();
        foreach (var mount in inspect?["Mounts"] as JsonArray ?? [])
        {
            var source = mount?["Source"]?.GetValue<string>() ?? string.Empty;
            mounts.Add(new JsonObject { ["source"] = source, ["destination"] = mount?["Destination"]?.DeepClone(), ["rw"] = mount?["RW"]?.DeepClone() });
            if (!Workspace.IsInside(source, context.Workspace.SandboxWorkspaceDir))
            {
                problems.Add($"作業領域以外がマウントされています：{source}");
            }
        }

        // 書き込めないことも確かめる（見えないので書けない）
        var write = await lab.Docker.RunAsync(["exec", container, "bash", "-c", $"touch '{context.Workspace.VerifyRepo}/x' 2>/dev/null && echo wrote || echo denied"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (write.StandardOutput.Trim() == "wrote")
        {
            problems.Add("サンドボックスから検証用リポジトリに書き込めます");
        }

        var path = Write(context, "e3-sandbox", new JsonObject { ["found"] = new JsonArray(found.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()), ["mounts"] = mounts, ["write_attempt"] = write.StandardOutput.Trim(), ["needles"] = new JsonArray(needles.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) });
        return problems.Count == 0
            ? Pass("サンドボックスの枠に見えるのは対象アプリの複製だけで、検証用リポジトリとフレームワークのリポジトリの目印は見つかりませんでした", [new Evidence("検査の結果", Rel(context, path))])
            : Fail(string.Join("／", problems.Take(5)), [new Evidence("検査の結果", Rel(context, path))]);
    }
}

/// <summary>E-4：アプリに届くリクエストに、検証環境の目印（独特のヘッダー、自動操作のユーザーエージェント、ホスト名、環境変数）が含まれないこと。</summary>
public sealed class E4NoMarkers : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("E-4", ConformanceCategory.E, ItemKind.Required, "共通", "アプリに届くリクエストと環境に、検証環境の目印がない", "第17.3節 E、第12.6節、指示書 制約5");

    private static readonly string[] UserAgentMarkers = ["HeadlessChrome", "Headless", "Playwright", "puppeteer", "Selenium", "webdriver", "python-requests", "curl/", "Go-http-client", "node-fetch", "axios", "okhttp", "SocketIOClient", "custos", ".NET"];
    private static readonly string[] HostMarkers = ["custos", "verify", "sandbox", "localhost", "127.0.0.1", "test", "staging"];
    private static readonly string[] EnvMarkers = ["CUSTOS", "VERIFY", "SANDBOX", "TEST_API", "PLAYWRIGHT"];

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var startSeq = lab.Records.Activity().LastSeq;

        // 調べるための通信を作る：ブラウザで画面を開き、スクリプトクライアントでも接続する
        var browser = await AgentAsync(context, "browser", cancellationToken).ConfigureAwait(false);
        await browser.CallAsync("page.open", new JsonObject { ["page"] = "e4", ["url"] = lab.PublicUrl.ToString() }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
        await browser.CallAsync("page.close", new JsonObject { ["page"] = "e4" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var client = await lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var runner = new Scenarios.ScenarioRunner(lab, context.Run, _ => { });
        var headers = await runner.BrowserLikeHeadersAsync(cancellationToken).ConfigureAwait(false);
        var user = "e4-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        try
        {
            await client.CallAsync("user.connect", new JsonObject { ["user"] = user, ["url"] = lab.PublicUrl.ToString(), ["transport"] = "upgrade", ["headers"] = headers, ["timeout_ms"] = 10000 }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await client.CallAsync("user.disconnect", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CustosException or TimeoutException)
        {
            // アプリにつながらなくても、届いたリクエストのヘッダーは調べられる
        }

        await lab.SettleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var requests = lab.Records.Http(startSeq).Where(e => e.Blocked != true).ToList();
        var problems = new List<string>();
        var report = new JsonObject { ["requests"] = requests.Count };
        var added = requests.Where(r => r.ForwardedRequestHeaders != null && r.ForwardedRequestHeaders != r.RequestHeaders).ToList();
        if (added.Count > 0)
        {
            problems.Add($"プロキシが要求のヘッダーを変えています（{added.Count} 件。例：{Diff(added[0])}）");
        }

        var userAgents = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            var parsed = Custos.SocketIO.Wire.HttpMessageReader.Parse(System.Text.Encoding.Latin1.GetBytes("GET / HTTP/1.1\r\n" + (request.ForwardedRequestHeaders ?? request.RequestHeaders ?? string.Empty)), false);
            var ua = parsed.Header("User-Agent") ?? "(なし)";
            userAgents.Add(ua);
            hosts.Add(parsed.Header("Host") ?? "(なし)");
            foreach (var header in parsed.Headers)
            {
                if (header.Name.Equals("sec-ch-ua", StringComparison.OrdinalIgnoreCase) && header.Value.Contains("Headless", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("sec-ch-ua にヘッドレスの印があります：" + header.Value);
                }

                if (header.Name.StartsWith("X-Custos", StringComparison.OrdinalIgnoreCase) || header.Name.Contains("verify", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("検証環境を示すヘッダーがあります：" + header.Name);
                }
            }
        }

        foreach (var ua in userAgents)
        {
            if (ua == "(なし)")
            {
                problems.Add("ユーザーエージェントのない要求があります（通常のブラウザは必ず送る）");
            }
            else if (UserAgentMarkers.FirstOrDefault(m => ua.Contains(m, StringComparison.OrdinalIgnoreCase)) is { } marker)
            {
                problems.Add($"自動操作を示すユーザーエージェントがあります（{marker}）：{ua}");
            }
        }

        foreach (var host in hosts)
        {
            if (HostMarkers.FirstOrDefault(m => host.Contains(m, StringComparison.OrdinalIgnoreCase)) is { } marker)
            {
                problems.Add($"検証環境を示すホスト名です（{marker}）：{host}");
            }
        }

        // アプリのコンテナの環境変数：本番（イメージ + environment.yaml の environment）と同じであること
        var container = await lab.Docker.InspectAsync(Containers.App(lab.Project), cancellationToken).ConfigureAwait(false);
        var image = await lab.Docker.InspectAsync(lab.State.AppImage, cancellationToken).ConfigureAwait(false);
        var containerEnv = (container?["Config"]?["Env"] as JsonArray ?? []).Select(e => e?.GetValue<string>() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        var expectedEnv = (image?["Config"]?["Env"] as JsonArray ?? []).Select(e => e?.GetValue<string>() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in context.Config.Environment.App.Environment)
        {
            expectedEnv.RemoveWhere(e => e.StartsWith(pair.Key + "=", StringComparison.Ordinal));
            expectedEnv.Add($"{pair.Key}={pair.Value}");
        }

        var extraEnv = containerEnv.Except(expectedEnv, StringComparer.Ordinal).ToList();
        foreach (var extra in extraEnv)
        {
            problems.Add("アプリのコンテナに、本番にない環境変数があります：" + extra.Split('=')[0]);
        }

        foreach (var env in containerEnv)
        {
            if (EnvMarkers.FirstOrDefault(m => env.Split('=')[0].Contains(m, StringComparison.OrdinalIgnoreCase)) is { } marker && lab.State.BuildKind == "production")
            {
                problems.Add($"アプリの環境変数に検証環境を示す名前があります（{marker}）");
            }
        }

        var hostname = container?["Config"]?["Hostname"]?.GetValue<string>() ?? string.Empty;
        if (HostMarkers.Any(m => hostname.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("アプリのコンテナのホスト名が検証環境を示しています：" + hostname);
        }

        report["user_agents"] = new JsonArray(userAgents.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray());
        report["hosts"] = new JsonArray(hosts.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
        report["headers_changed_by_proxy"] = added.Count;
        report["app_env_extra"] = new JsonArray(extraEnv.Select(e => (JsonNode?)JsonValue.Create(e.Split('=')[0])).ToArray());
        report["app_hostname"] = hostname;
        var path = Write(context, "e4-markers", report);
        if (requests.Count == 0)
        {
            return Fail("調べるための要求が記録にありません", [new Evidence("検査の結果", Rel(context, path))]);
        }

        return problems.Count == 0
            ? Pass($"アプリに届いた {requests.Count} 件の要求に、プロキシが足したヘッダー、自動操作のユーザーエージェント、検証環境を示すホスト名はなく、アプリのコンテナの環境変数も本番と同じでした", [new Evidence("検査の結果", Rel(context, path))])
            : Fail(string.Join("／", problems.Distinct(StringComparer.Ordinal).Take(6)), [new Evidence("検査の結果", Rel(context, path))]);
    }

    private static string Diff(Custos.SocketIO.Wire.RecordEntry entry)
    {
        var received = (entry.RequestHeaders ?? string.Empty).Split("\r\n");
        var forwarded = (entry.ForwardedRequestHeaders ?? string.Empty).Split("\r\n");
        return string.Join(", ", forwarded.Except(received, StringComparer.Ordinal).Where(l => l.Length > 0));
    }
}

/// <summary>E-5：本番用ビルドで /__verify/* が存在しないパスと同じ応答を返すこと（テスト用APIが応答しない）。</summary>
public sealed class E5TestApiAbsent : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("E-5", ConformanceCategory.E, ItemKind.Required, "Web", "本番用ビルドで /__verify/* が存在しないパスと同じ応答（テスト用APIが応答しない）", "第17.3節 E、第14.9節");

    private static readonly (string Method, string Path)[] VerifyPaths =
    [
        ("GET", "/__verify/capabilities"), ("POST", "/__verify/settle"), ("POST", "/__verify/clock"),
        ("POST", "/__verify/seed"), ("POST", "/__verify/reset"), ("GET", "/__verify/faults"),
    ];

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var ops = await lab.OpsAsync(cancellationToken).ConfigureAwait(false);
        var app = context.Config.Environment.App;
        var problems = new List<string>();
        var report = new JsonObject();
        foreach (var port in new[] { app.Port, app.ServerPort }.Distinct())
        {
            foreach (var (method, path) in VerifyPaths)
            {
                var random = "/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)) + "/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
                var verify = await RequestAsync(ops, method, port, path, cancellationToken).ConfigureAwait(false);
                var missing = await RequestAsync(ops, method, port, random, cancellationToken).ConfigureAwait(false);
                var same = verify["status"]?.GetValue<int>() == missing["status"]?.GetValue<int>()
                    && Normalize(verify["body"]?.GetValue<string>(), path) == Normalize(missing["body"]?.GetValue<string>(), random);
                report[$"{port}{path}"] = new JsonObject { ["verify_status"] = verify["status"]?.DeepClone(), ["missing_status"] = missing["status"]?.DeepClone(), ["same"] = same };
                if (!same)
                {
                    problems.Add($"{method} :{port}{path} の応答（{verify["status"]}）が、存在しないパス（{missing["status"]}）と違います");
                }
            }
        }

        // 本番用ビルドでは、テスト用APIの管理用のポートも開いていない
        var testPort = context.Config.App.TestApiPort;
        var tcp = await ops.CallObjectAsync("probe.tcp", new JsonObject { ["host"] = "app", ["port"] = testPort, ["timeout_ms"] = 1500 }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        report["test_api_port_open"] = tcp["connected"]?.DeepClone();
        if (lab.State.BuildKind == "production" && tcp["connected"]?.GetValue<bool>() == true)
        {
            problems.Add($"本番用ビルドなのに、テスト用APIのポート {testPort} が開いています");
        }

        // 利用者側からは、プロキシが /__verify/ を遮断する（アプリへ転送しない）
        var client = await lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var startSeq = lab.Records.Activity().LastSeq;
        var blocked = await client.CallObjectAsync("probe.http", new JsonObject { ["url"] = new Uri(lab.PublicUrl, "/__verify/capabilities").ToString() }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        var blockedRecord = lab.Records.Http(startSeq).Any(e => e.Target == "/__verify/capabilities" && e.Blocked == true);
        report["proxy_block"] = new JsonObject { ["status"] = blocked["status"]?.DeepClone(), ["recorded_as_blocked"] = blockedRecord };
        if (blocked["status"]?.GetValue<int>() != 404 || !blockedRecord)
        {
            problems.Add("記録用のプロキシが、利用者側からの /__verify/ を遮断していません");
        }

        var path2 = Write(context, "e5-test-api", report);
        return problems.Count == 0
            ? Pass($"アプリの {string.Join("、", new[] { app.Port, app.ServerPort }.Distinct())} 番で、/__verify/* の {VerifyPaths.Length} 通りの応答が、存在しないパスと同じでした（テスト用APIのポートは{(tcp["connected"]?.GetValue<bool>() == true ? "開いている（テスト用ビルド）" : "閉じている")}）", [new Evidence("比較", Rel(context, path2))])
            : Fail(string.Join("／", problems.Take(5)), [new Evidence("比較", Rel(context, path2))]);
    }

    private static async Task<JsonObject> RequestAsync(Docker.AgentSession ops, string method, int port, string path, CancellationToken cancellationToken) =>
        await ops.CallObjectAsync("http.request", new JsonObject
        {
            ["url"] = $"http://app:{port.ToString(CultureInfo.InvariantCulture)}{path}",
            ["method"] = method,
            ["body"] = method == "POST" ? "{}" : null,
        }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

    /// <summary>本文の中のパスを消して比べる（Express の「Cannot GET /パス」など）。</summary>
    private static string Normalize(string? body, string path) => (body ?? string.Empty).Replace(path, "<path>", StringComparison.Ordinal);
}
