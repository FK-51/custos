using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Util;
using Custos.Web.SocketIO.Docker;
using Custos.Web.SocketIO.Records;

namespace Custos.Web.SocketIO.Lab;

/// <summary>起動の選択。</summary>
public sealed record UpOptions(
    string BuildKind,
    IReadOnlySet<string> Breakage,
    string? Profile,
    bool Rebuild,
    bool Egress,
    string? AppRepoOverride = null);

/// <summary>スナップショットの情報。</summary>
public sealed record SnapshotInfo(
    string Name,
    string Path,
    DateTimeOffset CreatedAt,
    string SessionId,
    string AppImage,
    IReadOnlyDictionary<string, long> Tables,
    IReadOnlyDictionary<string, string> Content);

/// <summary>完了待ちの結果。</summary>
public sealed record SettleResult(bool Settled, double WaitedMs, string Detail, long LastSeq);

/// <summary>
/// 検証環境（ローカル検証、設計書 第16.2節）。起動、片付け、健康確認、スナップショット、リセット、完了待ち、部品との接続を持つ。
/// </summary>
public sealed class Lab : IAsyncDisposable
{
    private readonly Workspace _workspace;
    private readonly ConfigSet _config;
    private readonly DockerCli _docker;
    private readonly Action<string> _log;
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    /// <summary>
    /// リセットの後も有効にし直す異常の名前（テスト用APIの faults。カナリアの間だけ使う）。リセットはアプリを再起動するので、
    /// 有効にした異常はメモリから消える。以前は、局面の検査（局面ごとにリセットする）の間、カナリアの異常が一度も効いていなかった。
    /// </summary>
    public HashSet<string> StickyFaults { get; } = new(StringComparer.Ordinal);

    /// <summary>リセットで消えた分の、異常の箇所を通った回数（StickyFaults の hits の累計）。</summary>
    public Dictionary<string, int> StickyFaultHits { get; } = new(StringComparer.Ordinal);
    private static readonly string DockerStateKey = "State";
    private LabState? _state;

    /// <summary>作る。</summary>
    public Lab(Workspace workspace, ConfigSet config, DockerCli docker, Action<string> log)
    {
        _workspace = workspace;
        _config = config;
        _docker = docker;
        _log = log;
        _state = LabState.Load(workspace, config.Environment.Project);
    }

    /// <summary>場所。</summary>
    public Workspace Workspace => _workspace;

    /// <summary>設定。</summary>
    public ConfigSet Config => _config;

    /// <summary>Docker。</summary>
    public DockerCli Docker => _docker;

    /// <summary>プロジェクト名。</summary>
    public string Project => _config.Environment.Project;

    /// <summary>起動中の状態（起動していなければ例外）。</summary>
    public LabState State => _state ?? throw new CustosException("検証環境が起動していません。custos up を先に実行してください");

    /// <summary>起動中か（状態のファイルがあるか）。</summary>
    public bool HasState => _state != null;

    /// <summary>利用者側から見たアプリの URL（記録用のプロキシ）。</summary>
    public Uri PublicUrl => new($"http://{_config.Environment.PublicHost}:{_config.Environment.PublicPort.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>通信の記録。</summary>
    public RecordStore Records => new(State.RecordsDir);

    /// <summary>DB のファイルの、ops の中での場所。</summary>
    public string DbPathInOps => "/appdata/" + _config.Environment.App.DbFile;

    /// <summary>
    /// 検証環境を起動する（custos up）。イメージを作り、compose を生成して起動し、アプリが応答するまで待つ。
    /// 初期のスナップショット（initial）がなければ作る。
    /// </summary>
    public async Task<LabState> UpAsync(UpOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _workspace.EnsureDataDirs();
        var ping = await _docker.PingAsync(cancellationToken).ConfigureAwait(false);
        if (!ping.Ok)
        {
            throw new CustosException($"Docker に接続できません：{ping.Detail}");
        }

        if (_state != null)
        {
            _log("前回の検証環境を片付けます");
            await DownAsync(cancellationToken).ConfigureAwait(false);
        }

        var builder = new ImageBuilder(_workspace, _docker, _log);
        var agents = await builder.BuildAgentsAsync(cancellationToken).ConfigureAwait(false);
        var sandbox = await builder.BuildSandboxAsync(cancellationToken).ConfigureAwait(false);
        await builder.BuildAnalyzerAsync(cancellationToken).ConfigureAwait(false);
        // 校正用ビルド（段階4）は、パッチを当てた複製から作る（配備できないラベルを付ける。第19.4節）
        var appRepo = options.AppRepoOverride ?? _workspace.AppRepo;
        var (appImage, appImageId) = await builder.BuildAppAsync(_config, appRepo, options.BuildKind, cancellationToken, options.AppRepoOverride != null).ConfigureAwait(false);
        var (uid, gid) = await AppUserAsync(appImage, cancellationToken).ConfigureAwait(false);

        var sessionId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        var recordsDir = Path.Combine(_workspace.RecordsDir, sessionId);
        Directory.CreateDirectory(recordsDir);
        Directory.CreateDirectory(_workspace.ControlDir);
        File.WriteAllText(Path.Combine(_workspace.ControlDir, "rules.json"), "{\"version\":0,\"rules\":[]}");
        await PrepareSandboxWorkspaceAsync(appRepo, cancellationToken).ConfigureAwait(false);
        var flagFile = PrepareFlags(sessionId);

        ResourceProfile? profile = null;
        if (options.Profile != null && !_config.Environment.ResourceProfiles.TryGetValue(options.Profile, out profile))
        {
            throw new CustosException($"environment.yaml にないプロファイルです：{options.Profile}");
        }

        var compose = ComposeWriter.Write(_config, _workspace, new ComposeOptions(sessionId, recordsDir, appImage, agents, sandbox, options.Breakage, profile, flagFile, options.Egress, options.Egress ? agents["proxy"] : null));
        var composeDir = Path.Combine(_workspace.ComposeDir, Project);
        Directory.CreateDirectory(composeDir);
        var composeFile = Path.Combine(composeDir, "compose.yaml");
        File.WriteAllText(composeFile, compose);
        File.WriteAllText(Path.Combine(recordsDir, "session.json"), Json.Serialize(new { session = sessionId, app_image = appImage, app_image_id = appImageId, build = options.BuildKind, breakage = options.Breakage.Order(StringComparer.Ordinal).ToList(), profile = options.Profile }));
        _log($"検証環境を起動しています（{Project}、セッション {sessionId}）");
        await _docker.RunCheckedAsync(["compose", "-f", composeFile, "-p", Project, "up", "-d", "--force-recreate", "--remove-orphans"], "docker compose up", TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        _state = new LabState(Project, sessionId, composeFile, recordsDir, appImage, appImageId, options.BuildKind, agents, sandbox, uid, gid, options.Breakage.Order(StringComparer.Ordinal).ToList(), options.Profile, DateTimeOffset.UtcNow);
        _state.Save(_workspace);
        await WaitHealthyAsync(TimeSpan.FromMinutes(4), cancellationToken).ConfigureAwait(false);
        var initial = Path.Combine(_workspace.SnapshotsDir, "initial.db");
        if (!File.Exists(initial))
        {
            await SettleAsync(TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await SnapshotSaveAsync("initial", overwrite: false, cancellationToken).ConfigureAwait(false);
            _log("初期のスナップショット initial を作りました");
        }

        return _state;
    }

    private async Task<(int Uid, int Gid)> AppUserAsync(string image, CancellationToken cancellationToken)
    {
        var result = await _docker.RunAsync(["run", "--rm", "--network", "none", "--entrypoint", "sh", image, "-c", "id -u; id -g"], TimeSpan.FromMinutes(2), cancellationToken: cancellationToken).ConfigureAwait(false);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (result.Succeeded && lines.Length >= 2
            && int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)
            && int.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gid))
        {
            return (uid, gid);
        }

        return (0, 0);
    }

    /// <summary>サンドボックスに見せる、対象アプリの複製を作る（.git と node_modules を除く作業用の複製）。</summary>
    private async Task PrepareSandboxWorkspaceAsync(string appRepo, CancellationToken cancellationToken)
    {
        var target = _workspace.SandboxWorkspaceDir;
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? _workspace.DataDir);
        var clone = await ProcessRunner.RunAsync("git", ["clone", "--quiet", "--no-hardlinks", appRepo, target], timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);
        clone.EnsureSuccess("対象アプリの複製");

        // 複製から元のリポジトリへの参照（origin）を外す。サンドボックスからリモートへ書き込む手段を持たせない（第20.9節）
        await ProcessRunner.RunAsync("git", ["-C", target, "remote", "remove", "origin"], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>侵入の目標のフラグ（設計書 第3.2節）。セッションごとに固有の目印を作る。</summary>
    private string? PrepareFlags(string sessionId)
    {
        if (_config.Security?.SecretFile == null)
        {
            return null;
        }

        var dir = Path.Combine(_workspace.DataDir, "flags", sessionId);
        Directory.CreateDirectory(dir);
        var token = "FLAG{" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "}";
        var path = Path.Combine(dir, "secret_flag.txt");
        File.WriteAllText(path, token + "\n");
        return path;
    }

    /// <summary>今のセッションの侵入のフラグ（なければ null）。</summary>
    public string? SecretFlag()
    {
        var path = Path.Combine(_workspace.DataDir, "flags", State.SessionId, "secret_flag.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>片付ける（custos down）。コンテナ、ネットワーク、ボリュームを消す。</summary>
    public async Task DownAsync(CancellationToken cancellationToken)
    {
        await CloseSessionsAsync().ConfigureAwait(false);
        if (_state != null && File.Exists(_state.ComposeFile))
        {
            await _docker.RunAsync(["compose", "-f", _state.ComposeFile, "-p", Project, "down", "-v", "--remove-orphans", "--timeout", "5"], TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var path = LabState.PathFor(_workspace, Project);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _state = null;
    }

    /// <summary>アプリが応答するまで待つ（Next.js の画面と、Socket.IO の握手の両方）。</summary>
    public async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var ops = await OpsAsync(cancellationToken).ConfigureAwait(false);
        var app = _config.Environment.App;
        var stopwatch = Stopwatch.StartNew();
        var last = string.Empty;
        while (stopwatch.Elapsed < timeout)
        {
            var page = await ops.CallObjectAsync("http.request", new JsonObject { ["url"] = $"http://app:{app.Port}{app.HealthPath}", ["timeout_ms"] = 5000 }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            var handshake = await ops.CallObjectAsync("http.request", new JsonObject { ["url"] = $"http://app:{app.ServerPort}{app.SocketIoPath}?EIO=4&transport=polling", ["timeout_ms"] = 5000 }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            var pageOk = page["ok"]?.GetValue<bool>() == true && page["status"]?.GetValue<int>() == 200;
            var handshakeOk = handshake["ok"]?.GetValue<bool>() == true && handshake["status"]?.GetValue<int>() == 200
                && (handshake["body"]?.GetValue<string>() ?? string.Empty).StartsWith('0');
            if (pageOk && handshakeOk)
            {
                _log($"アプリが応答しました（{stopwatch.Elapsed.TotalSeconds:0.0} 秒）");
                return;
            }

            last = $"画面={page["status"] ?? page["error"]}, 握手={handshake["status"] ?? handshake["error"]}";
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        var logs = await AppLogsAsync(null, cancellationToken).ConfigureAwait(false);
        throw new CustosException($"アプリが {timeout.TotalSeconds} 秒以内に応答しませんでした（{last}）。\nアプリのログの末尾:\n{Tail(logs, 3000)}");
    }

    private static string Tail(string text, int length) => text.Length <= length ? text : text[^length..];

    /// <summary>ops（管理側の操作）との接続。</summary>
    public Task<AgentSession> OpsAsync(CancellationToken cancellationToken) => SessionAsync(Containers.Ops(Project), "/custos/custos-ops", cancellationToken);

    /// <summary>スクリプトクライアントとの接続。</summary>
    public Task<AgentSession> ClientAsync(int index, CancellationToken cancellationToken) => SessionAsync(Containers.Client(Project, index), "/custos/custos-client", cancellationToken);

    /// <summary>攻撃クライアントとの接続。</summary>
    public Task<AgentSession> AttackerAsync(int index, CancellationToken cancellationToken) => SessionAsync(Containers.Attacker(Project, index), "/custos/custos-client", cancellationToken);

    /// <summary>ブラウザとの接続（起動もする）。</summary>
    public async Task<AgentSession> BrowserAsync(int index, bool automationUserAgent, CancellationToken cancellationToken)
    {
        var session = await SessionAsync(Containers.Browser(Project, index), "/custos/custos-browser", cancellationToken).ConfigureAwait(false);
        var launch = new JsonObject { ["locale"] = "ja-JP", ["timezone"] = "Asia/Tokyo" };
        if (automationUserAgent)
        {
            // わざと壊す確認（E-4）：自動操作と分かるユーザーエージェント
            launch["user_agent"] = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/141.0.0.0 Safari/537.36 Playwright";
        }

        await session.CallAsync("browser.launch", launch, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task<AgentSession> SessionAsync(string container, string executable, CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(container, out var existing))
            {
                return existing;
            }

            var session = await AgentSession.StartAsync(_docker, container, executable, cancellationToken).ConfigureAwait(false);
            _sessions[container] = session;
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>部品との接続を閉じる（ブラウザやクライアントの状態も消える）。</summary>
    public async Task CloseSessionsAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    /// <summary>1つの部品との接続を閉じる。</summary>
    public async Task CloseSessionAsync(string container)
    {
        if (_sessions.Remove(container, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// スナップショットを取る（custos snapshot save）。通信が止まっている時点で、DB の整合の取れた複製を取る（SQLite のオンラインバックアップ）。
    /// </summary>
    public async Task<SnapshotInfo> SnapshotSaveAsync(string name, bool overwrite, CancellationToken cancellationToken)
    {
        CheckName(name);
        var ops = await OpsAsync(cancellationToken).ConfigureAwait(false);
        var result = await ops.CallObjectAsync("db.backup", new JsonObject { ["db"] = DbPathInOps, ["out"] = $"/snapshots/{name}.db", ["overwrite"] = overwrite }, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        var info = new SnapshotInfo(
            name,
            Path.Combine(_workspace.SnapshotsDir, name + ".db"),
            DateTimeOffset.UtcNow,
            State.SessionId,
            State.AppImage,
            (result["tables"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value?.GetValue<long>() ?? 0, StringComparer.Ordinal),
            (result["content"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal));
        File.WriteAllText(Path.Combine(_workspace.SnapshotsDir, name + ".json"), Json.Serialize(info));
        return info;
    }

    /// <summary>スナップショットの情報を読む。</summary>
    public SnapshotInfo LoadSnapshot(string name)
    {
        CheckName(name);
        var path = Path.Combine(_workspace.SnapshotsDir, name + ".json");
        return File.Exists(path) ? Json.Deserialize<SnapshotInfo>(File.ReadAllText(path)) : throw new CustosException($"スナップショットがありません：{name}");
    }

    private static void CheckName(string name)
    {
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || name.StartsWith('.'))
        {
            throw new CustosException($"スナップショットの名前は英数字と - _ . で書いてください：{name}");
        }
    }

    /// <summary>
    /// テスト用API を管理側から呼ぶ（テスト用ビルドだけ。設計書 第14.6節）。利用者側からは届かない（プロキシが /__verify/ を遮断する）。
    /// 返り値は応答の JSON。テスト用APIが応答しなければ例外。
    /// </summary>
    public async Task<JsonObject> TestApiAsync(string method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var ops = await OpsAsync(cancellationToken).ConfigureAwait(false);
        var request = new JsonObject
        {
            ["url"] = $"http://app:{_config.App.TestApiPort.ToString(CultureInfo.InvariantCulture)}{path}",
            ["method"] = method,
            ["timeout_ms"] = 60000,
        };
        if (body != null)
        {
            request["body"] = body.ToJsonString();
        }

        var response = await ops.CallObjectAsync("http.request", request, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        if (response["ok"]?.GetValue<bool>() != true)
        {
            throw new CustosException($"テスト用APIに届きません（{path}）：{response["error"]}");
        }

        var status = response["status"]?.GetValue<int>() ?? 0;
        var text = response["body"]?.GetValue<string>() ?? "{}";
        var json = JsonNode.Parse(text) as JsonObject ?? [];
        if (status is < 200 or >= 300 and not 408)
        {
            throw new CustosException($"テスト用APIが {status} を返しました（{path}）：{text}");
        }

        json["_status"] = status;
        return json;
    }

    /// <summary>テスト用ビルドで動いているか（テスト用APIが能力の宣言を返すか）。</summary>
    public async Task<bool> HasTestApiAsync(CancellationToken cancellationToken)
    {
        try
        {
            var caps = await TestApiAsync("GET", "/__verify/capabilities", null, cancellationToken).ConfigureAwait(false);
            return caps["capabilities"] is JsonObject;
        }
        catch (CustosException)
        {
            return false;
        }
    }

    /// <summary>
    /// 利用者側を片付ける（リセットの一部）：スクリプトクライアントの利用者と生のクライアントを消し、ブラウザのページを閉じる。
    /// 残すと、アプリの再起動の後に再接続して状態を変え、リセット後の状態が決まらなくなる。
    /// </summary>
    public async Task ClearUserSideAsync(CancellationToken cancellationToken)
    {
        var layout = _config.Environment.Nodes;
        var calls = new List<(Func<Task<Docker.AgentSession>> Agent, string Method)>();
        for (var i = 1; i <= layout.HonestClients; i++)
        {
            var index = i;
            calls.Add((() => ClientAsync(index, cancellationToken), "users.clear"));
        }

        for (var i = 1; i <= layout.AttackerClients; i++)
        {
            var index = i;
            calls.Add((() => AttackerAsync(index, cancellationToken), "users.clear"));
        }

        for (var i = 1; i <= layout.Browsers; i++)
        {
            var index = i;
            calls.Add((() => BrowserAsync(index, false, cancellationToken), "pages.close_all"));
        }

        foreach (var (agent, method) in calls)
        {
            try
            {
                var session = await agent().ConfigureAwait(false);
                await session.CallAsync(method, new JsonObject(), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (CustosException e)
            {
                _log($"利用者側の片付けに失敗しました（{method}）：{e.Message}");
            }
        }
    }

    /// <summary>
    /// リセットする（custos reset）。利用者側を片付け、アプリを止め、DB のファイルを差し替え、アプリを起動し直し、応答するまで待つ。
    /// 以前の接続はプロキシで切る。メモリ上の状態は、再起動で元に戻る。
    /// </summary>
    public async Task<JsonObject> ResetAsync(string name, bool skipDbReplace, CancellationToken cancellationToken)
    {
        var snapshot = LoadSnapshot(name);
        var stopwatch = Stopwatch.StartNew();
        _log($"リセットします（{name}）");
        await KeepStickyFaultHitsAsync(cancellationToken).ConfigureAwait(false);
        await ClearUserSideAsync(cancellationToken).ConfigureAwait(false);
        await _docker.RunCheckedAsync(["compose", "-f", State.ComposeFile, "-p", Project, "stop", "--timeout", "5", "app"], "アプリの停止", TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        await DropConnectionsAsync().ConfigureAwait(false);
        var ops = await OpsAsync(cancellationToken).ConfigureAwait(false);
        if (!skipDbReplace)
        {
            await ops.CallAsync("db.restore", new JsonObject { ["db"] = DbPathInOps, ["from"] = $"/snapshots/{name}.db", ["uid"] = State.AppUid, ["gid"] = State.AppGid }, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }

        await _docker.RunCheckedAsync(["compose", "-f", State.ComposeFile, "-p", Project, "start", "app"], "アプリの起動", TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        await WaitHealthyAsync(TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        if (StickyFaults.Count > 0)
        {
            await TestApiAsync("POST", "/__verify/faults", new JsonObject { ["enable"] = new JsonArray(StickyFaults.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()) }, cancellationToken).ConfigureAwait(false);
        }

        await SettleAsync(TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var hash = await ops.CallObjectAsync("db.hash", new JsonObject { ["db"] = DbPathInOps }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        var content = hash["content"] as JsonObject ?? [];
        var mismatched = new JsonArray();
        foreach (var pair in snapshot.Content)
        {
            if (content[pair.Key]?.GetValue<string>() != pair.Value)
            {
                mismatched.Add(pair.Key);
            }
        }

        foreach (var pair in content)
        {
            if (!snapshot.Content.ContainsKey(pair.Key))
            {
                mismatched.Add(pair.Key);
            }
        }

        return new JsonObject
        {
            ["snapshot"] = name,
            ["matches"] = mismatched.Count == 0,
            ["mismatched_tables"] = mismatched,
            ["tables"] = hash["tables"]?.DeepClone(),
            ["seconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
        };
    }

    /// <summary>再起動で消える前に、StickyFaults の hits を累計に足す。</summary>
    private async Task KeepStickyFaultHitsAsync(CancellationToken cancellationToken)
    {
        if (StickyFaults.Count == 0)
        {
            return;
        }

        var faults = await TestApiAsync("GET", "/__verify/faults", null, cancellationToken).ConfigureAwait(false);
        foreach (var fault in (faults["faults"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (fault["name"]?.ToString() is { } faultName && StickyFaults.Contains(faultName) && fault["hits"] is JsonValue v && v.TryGetValue<int>(out var hits))
            {
                StickyFaultHits[faultName] = StickyFaultHits.GetValueOrDefault(faultName) + hits;
            }
        }
    }

    /// <summary>プロキシに、いまの接続をすべて切らせる。</summary>
    public async Task DropConnectionsAsync()
    {
        var rules = ReadRules();
        rules["drop_all_token"] = Guid.NewGuid().ToString("N");
        await WriteRulesAsync(rules).ConfigureAwait(false);
        await Task.Delay(600).ConfigureAwait(false);
    }

    /// <summary>プロキシの規則（異常の注入）を読む。</summary>
    public JsonObject ReadRules()
    {
        var path = Path.Combine(_workspace.ControlDir, "rules.json");
        return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj ? obj : new JsonObject { ["version"] = 0, ["rules"] = new JsonArray() };
    }

    /// <summary>プロキシの規則を書く（管理側からプロキシへの一方向の指示）。</summary>
    public async Task WriteRulesAsync(JsonObject rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var version = (rules["version"]?.GetValue<int>() ?? 0) + 1;
        rules["version"] = version;
        var path = Path.Combine(_workspace.ControlDir, "rules.json");
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, rules.ToJsonString()).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);

        // プロキシがこの版を読み込んだことを、記録の activity.json で確かめる（共有したファイルの変更は、遅れて見えることがある）
        var activityPath = Path.Combine(State.RecordsDir, "activity.json");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(activityPath) && JsonNode.Parse(await File.ReadAllTextAsync(activityPath).ConfigureAwait(false))?["rules_version"]?.GetValue<int>() >= version)
                {
                    return;
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        _log($"プロキシが規則の版 {version} を読み込んだことを確かめられませんでした（15 秒）");
    }

    /// <summary>
    /// 外からの完了待ち（custos settle、指示書 タスク7）。通信（ping/pong を除く）と DB のファイルの更新が、
    /// quiet の間止まるまで待つ。上限の時間を過ぎたら失敗として返す。
    /// </summary>
    public async Task<SettleResult> SettleAsync(TimeSpan quiet, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var ops = await OpsAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        string? lastDb = null;
        var dbStableSince = stopwatch.Elapsed;
        var detail = string.Empty;
        long lastSeq = 0;
        while (stopwatch.Elapsed < timeout)
        {
            var activity = Records.Activity();
            lastSeq = activity.LastSeq;
            var stat = await ops.CallObjectAsync("db.stat", new JsonObject { ["db"] = DbPathInOps }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var db = stat["files"]?.ToJsonString() ?? string.Empty;
            if (db != lastDb)
            {
                lastDb = db;
                dbStableSince = stopwatch.Elapsed;
            }

            var now = DateTimeOffset.UtcNow;
            var networkQuietFor = now - activity.LastActivity;
            var dbQuietFor = stopwatch.Elapsed - dbStableSince;
            detail = $"通信の静止 {networkQuietFor.TotalMilliseconds:0} ms、DB の静止 {dbQuietFor.TotalMilliseconds:0} ms、処理中の要求 {activity.Inflight}";
            if (activity.Inflight == 0 && networkQuietFor >= quiet && dbQuietFor >= quiet)
            {
                return new SettleResult(true, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), detail, lastSeq);
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return new SettleResult(false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), "上限の時間を過ぎました：" + detail, lastSeq);
    }

    /// <summary>DB に SQL を発行して読む（読み取り専用）。</summary>
    public async Task<JsonObject> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        var ops = await OpsAsync(cancellationToken).ConfigureAwait(false);
        return await ops.CallObjectAsync("db.query", new JsonObject { ["db"] = DbPathInOps, ["sql"] = sql }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DB の表ごとの件数と内容のハッシュ。</summary>
    public async Task<JsonObject> DbHashAsync(CancellationToken cancellationToken)
    {
        var ops = await OpsAsync(cancellationToken).ConfigureAwait(false);
        return await ops.CallObjectAsync("db.hash", new JsonObject { ["db"] = DbPathInOps }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>アプリのコンテナの標準出力と標準エラー出力（指示書 タスク8）。</summary>
    public async Task<string> AppLogsAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var args = new List<string> { "logs", "--timestamps" };
        if (since is { } s)
        {
            args.Add("--since");
            args.Add(s.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        args.Add(Containers.App(Project));
        var result = await _docker.RunAsync(args, TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.StandardOutput + result.StandardError;
    }

    /// <summary>コンテナの状態（起動中か、再起動の回数、終了コード）。クラッシュの検知に使う。</summary>
    public async Task<JsonObject> ContainerStateAsync(string container, CancellationToken cancellationToken)
    {
        var inspect = await _docker.InspectAsync(container, cancellationToken).ConfigureAwait(false);
        var state = inspect?[DockerStateKey] as JsonObject;
        return new JsonObject
        {
            ["exists"] = inspect != null,
            ["running"] = state?["Running"]?.GetValue<bool>() ?? false,
            ["status"] = state?["Status"]?.GetValue<string>(),
            ["exit_code"] = state?["ExitCode"]?.GetValue<int>(),
            ["started_at"] = state?["StartedAt"]?.GetValue<string>(),
            ["finished_at"] = state?["FinishedAt"]?.GetValue<string>(),
            ["restart_count"] = inspect?["RestartCount"]?.GetValue<int>(),
            ["oom_killed"] = state?["OOMKilled"]?.GetValue<bool>(),
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseSessionsAsync().ConfigureAwait(false);
        _sessionLock.Dispose();
    }
}
