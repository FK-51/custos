using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Docker;
using Custos.Web.SocketIO.State;

namespace Custos.Web.SocketIO.Scenarios;

/// <summary>手順1つの結果。</summary>
public sealed record StepResult(int Index, string Kind, bool Ok, double Ms, string Detail, JsonNode? Data);

/// <summary>シナリオの実行結果。</summary>
public sealed record ScenarioResult(string Name, bool Ok, string? Error, IReadOnlyList<StepResult> Steps, IReadOnlyDictionary<string, JsonNode?> Variables, double Ms);

/// <summary>
/// シナリオを検証環境で実行する。利用者の操作はスクリプトクライアントとブラウザで行い、管理側の操作（完了待ち、リセット）は Lab で行う。
/// </summary>
public sealed class ScenarioRunner
{
    private readonly Lab.Lab _lab;
    private readonly Run? _run;
    private readonly Action<string> _log;
    private readonly Dictionary<string, JsonNode?> _variables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _userVia = new(StringComparer.Ordinal);
    private static string? _userAgent;

    /// <summary>
    /// スクリプトクライアントの既定のトランスポート。ポーリングから始めて WebSocket に切り替える（ブラウザの既定と同じ）。
    /// 環境変数 CUSTOS_CLIENT_TRANSPORT（polling、websocket、upgrade）で変えられる。
    /// </summary>
    public static string DefaultTransport { get; } = Environment.GetEnvironmentVariable("CUSTOS_CLIENT_TRANSPORT") ?? "upgrade";

    /// <summary>作る。run を渡すと、スクリーンショットなどの証拠をそこに書く。</summary>
    public ScenarioRunner(Lab.Lab lab, Run? run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
        _variables["run"] = JsonValue.Create(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)));
        _variables["public_url"] = JsonValue.Create(lab.PublicUrl.ToString().TrimEnd('/'));
    }

    /// <summary>変数。</summary>
    public IDictionary<string, JsonNode?> Variables => _variables;

    /// <summary>
    /// 通常のブラウザと同じヘッダー（指示書 制約5）。ユーザーエージェントは、ブラウザの部品が使う値と同じにする。
    /// </summary>
    public async Task<JsonObject> BrowserLikeHeadersAsync(CancellationToken cancellationToken)
    {
        if (_userAgent == null)
        {
            var cache = Path.Combine(_lab.Workspace.ComposeDir, _lab.Project, "user-agent.txt");
            if (File.Exists(cache))
            {
                _userAgent = File.ReadAllText(cache).Trim();
            }
            else
            {
                var browser = await _lab.BrowserAsync(1, false, cancellationToken).ConfigureAwait(false);
                var info = await browser.CallObjectAsync("browser.info", [], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                _userAgent = info["user_agent"]?.GetValue<string>() ?? "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
                Directory.CreateDirectory(Path.GetDirectoryName(cache) ?? _lab.Workspace.ComposeDir);
                File.WriteAllText(cache, _userAgent);
            }
        }

        var origin = _lab.PublicUrl.GetLeftPart(UriPartial.Authority);
        return new JsonObject
        {
            ["User-Agent"] = _userAgent,
            ["Origin"] = origin,
            ["Accept-Language"] = "ja,en-US;q=0.9,en;q=0.8",
            ["Pragma"] = "no-cache",
            ["Cache-Control"] = "no-cache",
        };
    }

    /// <summary>シナリオを実行する。</summary>
    public async Task<ScenarioResult> RunAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        foreach (var pair in scenario.Variables)
        {
            _variables.TryAdd(pair.Key, pair.Value?.DeepClone());
        }

        var stopwatch = Stopwatch.StartNew();
        var results = new List<StepResult>();
        string? error = null;
        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            var stepWatch = Stopwatch.StartNew();
            try
            {
                var args = (JsonObject?)Scenario.Substitute(step.Args, _variables) ?? [];
                var (ok, detail, data) = await ExecuteAsync(step.Kind, args, cancellationToken).ConfigureAwait(false);
                results.Add(new StepResult(i, step.Kind, ok, Math.Round(stepWatch.Elapsed.TotalMilliseconds, 1), detail, data));
                if (!ok)
                {
                    error = $"手順 {i + 1}（{step.Kind}、{step.Line} 行目）：{detail}";
                    break;
                }
            }
            catch (Exception e) when (e is CustosException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or FormatException)
            {
                results.Add(new StepResult(i, step.Kind, false, Math.Round(stepWatch.Elapsed.TotalMilliseconds, 1), e.Message, null));
                error = $"手順 {i + 1}（{step.Kind}、{step.Line} 行目）：{e.Message}";
                break;
            }
        }

        var variables = _variables.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.Ordinal);
        return new ScenarioResult(scenario.Name, error == null, error, results, variables, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1));
    }

    private async Task<AgentSession> AgentForAsync(string user, string? via, CancellationToken cancellationToken)
    {
        var target = via ?? (_userVia.TryGetValue(user, out var known) ? known : "client");
        _userVia[user] = target;
        return target switch
        {
            "attacker" => await _lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false),
            _ => await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<(bool Ok, string Detail, JsonNode? Data)> ExecuteAsync(string kind, JsonObject args, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case "connect":
            case "reconnect":
                {
                    var user = Str(args, "user");
                    var agent = await AgentForAsync(user, OptStr(args, "via"), cancellationToken).ConfigureAwait(false);
                    var headers = await BrowserLikeHeadersAsync(cancellationToken).ConfigureAwait(false);
                    if (args["headers"] is JsonObject extra)
                    {
                        foreach (var pair in extra)
                        {
                            headers[pair.Key] = pair.Value?.DeepClone();
                        }
                    }

                    var url = OptStr(args, "url") ?? _lab.PublicUrl.ToString();
                    var result = await agent.CallObjectAsync("user.connect", new JsonObject
                    {
                        ["user"] = user,
                        ["url"] = url,
                        ["transport"] = OptStr(args, "transport") ?? DefaultTransport,
                        ["headers"] = headers,
                        ["path"] = _lab.Config.Environment.App.SocketIoPath,
                    }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    _cursors[user] = 0;
                    if (OptStr(args, "save_socket_id") is { } name)
                    {
                        _variables[name] = result["socket_id"]?.DeepClone();
                    }

                    return (true, $"{user} が接続しました（{result["socket_id"]}）", result);
                }

            case "emit":
                {
                    var user = Str(args, "user");
                    var agent = await AgentForAsync(user, OptStr(args, "via"), cancellationToken).ConfigureAwait(false);
                    var ack = args["ack"]?.GetValue<bool>() == true || args["save"] != null;
                    var result = await agent.CallObjectAsync("user.emit", new JsonObject
                    {
                        ["user"] = user,
                        ["event"] = Str(args, "event"),
                        ["args"] = args["args"]?.DeepClone() ?? new JsonArray(),
                        ["ack"] = ack,
                        ["timeout_ms"] = args["timeout_ms"]?.DeepClone() ?? 10000,
                        ["remember"] = args["remember"]?.DeepClone(),
                    }, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                    if (args["save"] is JsonObject save && result["ack"] is JsonArray ackArgs)
                    {
                        foreach (var pair in save)
                        {
                            var values = Json.SelectPath(ackArgs, pair.Value?.GetValue<string>() ?? "$");
                            _variables[pair.Key] = values.Count > 0 ? values[0]?.DeepClone() : null;
                        }
                    }

                    return (true, $"{user} → {Str(args, "event")}", result["ack"]?.DeepClone());
                }

            case "emit_concurrent":
                {
                    var items = args["items"] as JsonArray ?? [];
                    var agent = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
                    await agent.CallAsync("users.emit_concurrent", new JsonObject { ["items"] = items.DeepClone() }, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                    return (true, $"{items.Count} 件を同時に送りました", null);
                }

            case "remember":
                {
                    var user = Str(args, "user");
                    var agent = await AgentForAsync(user, OptStr(args, "via"), cancellationToken).ConfigureAwait(false);
                    var value = args["value"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : args["value"]?.ToJsonString() ?? string.Empty;
                    await agent.CallAsync("user.remember", new JsonObject { ["user"] = user, ["key"] = Str(args, "key"), ["value"] = value }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    return (true, $"{user} に {Str(args, "key")} を覚えさせました", null);
                }

            case "wait":
                {
                    var user = Str(args, "user");
                    var agent = await AgentForAsync(user, OptStr(args, "via"), cancellationToken).ConfigureAwait(false);
                    var since = args["since"]?.GetValue<long>() ?? _cursors.GetValueOrDefault(user);
                    var result = await agent.CallObjectAsync("user.wait", new JsonObject
                    {
                        ["user"] = user,
                        ["event"] = OptStr(args, "event"),
                        ["since"] = since,
                        ["timeout_ms"] = args["timeout_ms"]?.DeepClone() ?? 10000,
                        ["match_path"] = args["match_path"]?.DeepClone(),
                        ["match_value"] = args["match_value"]?.DeepClone(),
                    }, TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
                    var found = result["found"]?.GetValue<bool>() == true;
                    if (found && result["event"] is JsonObject received)
                    {
                        _cursors[user] = received["seq"]?.GetValue<long>() ?? since;
                        if (args["save"] is JsonObject save)
                        {
                            foreach (var pair in save)
                            {
                                var values = Json.SelectPath(received["args"], pair.Value?.GetValue<string>() ?? "$");
                                _variables[pair.Key] = values.Count > 0 ? values[0]?.DeepClone() : null;
                            }
                        }
                    }

                    var optional = args["optional"]?.GetValue<bool>() == true;
                    return (found || optional, found ? $"{user} が {OptStr(args, "event")} を受け取りました" : $"{user} に {OptStr(args, "event")} が届きませんでした", result["event"]?.DeepClone());
                }

            case "bot":
                {
                    var users = (args["users"] as JsonArray ?? []).Select(u => u?.GetValue<string>() ?? string.Empty).ToList();
                    var agent = await AgentForAsync(users.FirstOrDefault() ?? "bot", OptStr(args, "via"), cancellationToken).ConfigureAwait(false);
                    var protocol = BotProtocolLoader.Load(_lab.Workspace);
                    var result = await agent.CallObjectAsync("bot.play", new JsonObject
                    {
                        ["users"] = new JsonArray(users.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()),
                        ["protocol"] = protocol,
                        ["policy"] = OptStr(args, "policy") ?? "random",
                        ["seed"] = args["seed"]?.DeepClone() ?? 1,
                        ["max_moves"] = args["max_moves"]?.DeepClone() ?? 0,
                        ["timeout_ms"] = args["timeout_ms"]?.DeepClone() ?? 180000,
                        ["think_ms"] = args["think_ms"]?.DeepClone() ?? 0,
                        ["stall_ms"] = args["stall_ms"]?.DeepClone() ?? 0,
                        ["prefer"] = args["prefer"]?.DeepClone(),
                    }, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
                    if (OptStr(args, "save") is { } name)
                    {
                        _variables[name] = result.DeepClone();
                    }

                    var expectEnd = args["until"]?.GetValue<string>() == "end";
                    var stop = result["stop_reason"]?.GetValue<string>();
                    var ok = !expectEnd || stop == "all_finished";
                    return (ok, $"ボットが {result["moves"]} 手を打ちました（{stop}）", result);
                }

            case "settle":
                {
                    var result = await _lab.SettleAsync(
                        TimeSpan.FromMilliseconds(args["quiet_ms"]?.GetValue<long>() ?? 500),
                        TimeSpan.FromMilliseconds(args["timeout_ms"]?.GetValue<long>() ?? 15000),
                        cancellationToken).ConfigureAwait(false);
                    return (result.Settled || args["optional"]?.GetValue<bool>() == true, result.Detail, null);
                }

            case "sleep":
                await Task.Delay(TimeSpan.FromMilliseconds(args["ms"]?.GetValue<long>() ?? 100), cancellationToken).ConfigureAwait(false);
                return (true, "待ちました", null);

            case "expect":
                return await ExpectAsync(args, cancellationToken).ConfigureAwait(false);

            case "disconnect":
                {
                    var user = Str(args, "user");
                    var agent = await AgentForAsync(user, OptStr(args, "via"), cancellationToken).ConfigureAwait(false);
                    await agent.CallAsync("user.disconnect", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    return (true, $"{user} が切断しました", null);
                }

            case "raw_connect":
                {
                    var agent = await _lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false);
                    var headers = await BrowserLikeHeadersAsync(cancellationToken).ConfigureAwait(false);
                    var result = await agent.CallObjectAsync("raw.connect", new JsonObject { ["name"] = Str(args, "name"), ["url"] = _lab.PublicUrl.ToString(), ["headers"] = headers, ["connect_namespace"] = args["connect_namespace"]?.DeepClone() ?? true, ["transport"] = OptStr(args, "transport") ?? DefaultTransport, ["path"] = _lab.Config.Environment.App.SocketIoPath }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    return (true, $"生のクライアント {Str(args, "name")} が接続しました", result);
                }

            case "raw_send":
                {
                    var agent = await _lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false);
                    await agent.CallAsync("raw.send", new JsonObject { ["name"] = Str(args, "name"), ["text"] = OptStr(args, "text"), ["binary_b64"] = OptStr(args, "binary_b64") }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    return (true, "送りました", null);
                }

            case "raw_close":
                {
                    var agent = await _lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false);
                    await agent.CallAsync("raw.close", new JsonObject { ["name"] = Str(args, "name") }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    return (true, "閉じました", null);
                }

            case "browser_open":
                {
                    var browser = await _lab.BrowserAsync(1, false, cancellationToken).ConfigureAwait(false);
                    var result = await browser.CallObjectAsync("page.open", new JsonObject { ["page"] = OptStr(args, "page") ?? "main", ["url"] = OptStr(args, "url") ?? _lab.PublicUrl.ToString() }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                    return (true, $"画面を開きました（{result["url"]}）", result);
                }

            case "browser_fill":
            case "browser_click":
            case "browser_wait":
            case "browser_read":
            case "browser_eval":
                {
                    var browser = await _lab.BrowserAsync(1, false, cancellationToken).ConfigureAwait(false);
                    var method = kind switch
                    {
                        "browser_fill" => "page.fill",
                        "browser_click" => "page.click",
                        "browser_wait" => "page.wait_for",
                        "browser_read" => "page.read",
                        _ => "page.eval",
                    };
                    var parameters = (JsonObject)args.DeepClone();
                    parameters["page"] = OptStr(args, "page") ?? "main";
                    parameters.Remove("save");
                    var result = await browser.CallObjectAsync(method, parameters, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                    if (OptStr(args, "save") is { } name)
                    {
                        _variables[name] = (result["value"] ?? result["result"])?.DeepClone();
                    }

                    var ok = kind != "browser_wait" || result["found"]?.GetValue<bool>() == true || args["optional"]?.GetValue<bool>() == true;
                    return (ok, $"{method}：{result.ToJsonString()}", result);
                }

            case "browser_screenshot":
                {
                    var browser = await _lab.BrowserAsync(1, false, cancellationToken).ConfigureAwait(false);
                    var result = await browser.CallObjectAsync("page.screenshot", new JsonObject { ["page"] = OptStr(args, "page") ?? "main" }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                    var bytes = Convert.FromBase64String(result["png_b64"]?.GetValue<string>() ?? string.Empty);
                    var name = OptStr(args, "name") ?? "screenshot-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                    string? path = null;
                    if (_run != null)
                    {
                        path = _run.WriteBytes($"screenshots/{name}.png", bytes);
                    }

                    return (true, $"スクリーンショット {name}（{bytes.Length} バイト）", new JsonObject { ["path"] = path, ["bytes"] = bytes.Length });
                }

            case "set":
                foreach (var pair in args)
                {
                    _variables[pair.Key] = pair.Value?.DeepClone();
                }

                return (true, "変数を設定しました", null);

            case "include":
                {
                    var file = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", Str(args, "file"));
                    var inner = Scenario.Load(file);
                    var result = await RunAsync(inner, cancellationToken).ConfigureAwait(false);
                    return (result.Ok, result.Error ?? $"{inner.Name} を実行しました", null);
                }

            case "reset":
                {
                    var result = await _lab.ResetAsync(OptStr(args, "snapshot") ?? "initial", false, cancellationToken).ConfigureAwait(false);
                    _cursors.Clear();
                    return (result["matches"]?.GetValue<bool>() == true, result.ToJsonString(), result);
                }

            case "snapshot":
                {
                    var info = await _lab.SnapshotSaveAsync(Str(args, "name"), true, cancellationToken).ConfigureAwait(false);
                    return (true, $"スナップショット {info.Name}", null);
                }

            case "drop_connections":
                await _lab.DropConnectionsAsync().ConfigureAwait(false);
                return (true, "プロキシで接続を切りました", null);

            case "fault":
                {
                    var rules = _lab.ReadRules();
                    rules["rules"] = args["rules"]?.DeepClone() ?? new JsonArray();
                    await _lab.WriteRulesAsync(rules).ConfigureAwait(false);
                    await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                    return (true, "プロキシの規則を変えました", null);
                }

            case "restart_app":
                {
                    await _lab.Docker.RunCheckedAsync(["restart", "--timeout", "3", Lab.Containers.App(_lab.Project)], "アプリの再起動", TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
                    await _lab.WaitHealthyAsync(TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
                    return (true, "アプリを再起動しました", null);
                }

            case "clock":
                {
                    var ops = await _lab.OpsAsync(cancellationToken).ConfigureAwait(false);
                    var result = await ops.CallObjectAsync("http.request", new JsonObject
                    {
                        ["url"] = $"http://app:{_lab.Config.App.TestApiPort.ToString(CultureInfo.InvariantCulture)}/__verify/clock",
                        ["method"] = "POST",
                        ["body"] = args.ToJsonString(),
                    }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    return (result["status"]?.GetValue<int>() == 200, result["body"]?.GetValue<string>() ?? string.Empty, result);
                }

            case "whose_turn":
                {
                    // 選択を求められている利用者を探す（最後に届いたイベントが、bot.yaml の choice_event である人）。
                    // save_current と save_other に、選択する人とそれ以外の人を入れる（局面の準備で、送り手の種類を決めるのに使う）
                    var users = (args["users"] as JsonArray ?? []).Select(u => u?.GetValue<string>() ?? string.Empty).ToList();
                    var choiceEvent = BotProtocolLoader.Load(_lab.Workspace)["choice_event"]?.GetValue<string>() ?? string.Empty;
                    var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(args["timeout_ms"]?.GetValue<long>() ?? 15000);
                    while (DateTimeOffset.UtcNow < deadline)
                    {
                        var choosers = new List<string>();
                        foreach (var user in users)
                        {
                            var agent = await AgentForAsync(user, OptStr(args, "via"), cancellationToken).ConfigureAwait(false);
                            // まだ答えていない選択の要求がある人（最後に送った後に、選択の要求が届いている人）
                            var info = await agent.CallObjectAsync("user.info", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                            var lastEmit = info["last_emit_seq"]?.GetValue<long>() ?? 0;
                            var events = await agent.CallObjectAsync("user.events", new JsonObject { ["user"] = user, ["since"] = lastEmit }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                            if ((events["events"] as JsonArray ?? []).OfType<JsonObject>().Any(e => e["event"]?.GetValue<string>() == choiceEvent))
                            {
                                choosers.Add(user);
                            }
                        }

                        if (choosers.Count == 1)
                        {
                            _variables[OptStr(args, "save_current") ?? "current"] = JsonValue.Create(choosers[0]);
                            var others = users.Where(u => u != choosers[0]).ToList();
                            _variables[OptStr(args, "save_other") ?? "other"] = others.Count > 0 ? JsonValue.Create(others[0]) : null;
                            return (true, $"選択するのは {choosers[0]}", null);
                        }

                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    }

                    return (false, "選択を求められている利用者が1人に決まりませんでした", null);
                }

            case "query":
                {
                    // 管理側からの読み取り専用の問い合わせ（フレームワークのシナリオだけで使う。探索AIのレポートには使わせない）
                    var result = await _lab.QueryAsync(Str(args, "sql"), cancellationToken).ConfigureAwait(false);
                    var rows = result["rows"] as JsonArray ?? [];
                    if (args["save"] is JsonObject save)
                    {
                        foreach (var pair in save)
                        {
                            var values = Json.SelectPath(rows, pair.Value?.GetValue<string>() ?? "$");
                            _variables[pair.Key] = values.Count > 0 ? values[0]?.DeepClone() : null;
                        }
                    }

                    long? expectRows = args["rows"] is JsonValue rowsValue && double.TryParse(rowsValue.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rowsNumber) ? (long)rowsNumber : null;
                    var ok = expectRows == null || rows.Count == expectRows;
                    return (ok, $"{rows.Count} 行{(ok ? string.Empty : $"（期待 {expectRows} 行）")}", rows);
                }

            case "assert":
                {
                    if (args["equals"] is JsonArray pair && pair.Count == 2)
                    {
                        var ok = JsonNode.DeepEquals(Normalize(pair[0]), Normalize(pair[1]));
                        return (ok, $"{pair[0]?.ToJsonString()} {(ok ? "=" : "≠")} {pair[1]?.ToJsonString()}", null);
                    }

                    if (args["contains"] is JsonArray contains && contains.Count == 2)
                    {
                        var haystack = contains[0] is JsonValue hv && hv.TryGetValue<string>(out var h) ? h : contains[0]?.ToJsonString() ?? string.Empty;
                        var needle = contains[1] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : contains[1]?.ToJsonString() ?? string.Empty;
                        var ok = haystack.Contains(needle, StringComparison.Ordinal);
                        return (ok, $"{(ok ? "含む" : "含まない")}：{needle}", null);
                    }

                    if (args["not_null"] is { } value)
                    {
                        var ok = value is not null && !(value is JsonValue jv && jv.TryGetValue<string>(out var str) && str.Length == 0);
                        return (ok, ok ? "値があります" : "値がありません", null);
                    }

                    return (false, "assert には equals、contains、not_null のどれかを書いてください", null);
                }

            default:
                return (false, $"知らない手順です：{kind}", null);
        }
    }

    private async Task<(bool Ok, string Detail, JsonNode? Data)> ExpectAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var keyName = Str(args, "state");
        if (!_lab.Config.State.Keys.TryGetValue(keyName, out var key))
        {
            return (false, $"state.yaml にないキーです：{keyName}", null);
        }

        var reader = new StateReader(_lab);
        AgentSession? browser = key.Source is DomSource ? await _lab.BrowserAsync(1, false, cancellationToken).ConfigureAwait(false) : null;
        var results = await reader.ReadAsync([key], browser, OptStr(args, "page") ?? "main", cancellationToken).ConfigureAwait(false);
        var result = results[0];
        if (!result.Ok)
        {
            return (false, result.Error ?? "読めませんでした", null);
        }

        var entity = OptStr(args, "entity");
        var values = entity == null ? result.Values : result.Values.Where(v => v.Entity == entity).ToList();
        if (args["count"] is JsonValue countValue && countValue.TryGetValue<long>(out var count) && values.Count != count)
        {
            return (false, $"{keyName} の値の数が {values.Count} です（期待 {count}）", null);
        }

        if (args["equals"] is { } expected)
        {
            var ok = values.Count > 0 && values.All(v => JsonNode.DeepEquals(v.Value, expected));
            return (ok, $"{keyName} = {string.Join(", ", values.Select(v => v.Value?.ToJsonString()))}（期待 {expected.ToJsonString()}）", null);
        }

        if (args["min"] is JsonValue minValue && minValue.TryGetValue<double>(out var min))
        {
            var ok = values.Count > 0 && values.All(v => v.Value is JsonValue n && n.TryGetValue<double>(out var d) && d >= min);
            return (ok, $"{keyName} = {string.Join(", ", values.Select(v => v.Value?.ToJsonString()))}（期待 {min} 以上）", null);
        }

        if (args["exists"] is JsonValue existsValue && existsValue.TryGetValue<bool>(out var exists))
        {
            return (values.Count > 0 == exists, $"{keyName} の値は {values.Count} 件", null);
        }

        return (true, $"{keyName} を読みました（{values.Count} 件）", null);
    }

    /// <summary>比べるために、数値の文字列は数値にする（DB の値と画面の値を比べるため）。</summary>
    private static JsonNode? Normalize(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : node is JsonValue n && n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? JsonValue.Create(n.GetValue<double>()) : node?.DeepClone();

    private static string Str(JsonObject args, string name) =>
        OptStr(args, name) ?? throw new CustosException($"{name} がありません");

    private static string? OptStr(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>検証用リポジトリの scenarios/bot.yaml（ボットの約束事）を読む。</summary>
public static class BotProtocolLoader
{
    /// <summary>読んで、client の bot.play に渡す形にする。</summary>
    public static JsonObject Load(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var path = Path.Combine(workspace.VerifyRepo, "scenarios", "bot.yaml");
        if (!File.Exists(path))
        {
            throw new CustosException($"ボットの約束事がありません：{path}");
        }

        var errors = new ConfigErrors();
        var root = YamlDocument.Parse(File.ReadAllText(path), "bot.yaml", errors);
        if (root == null || errors.Any)
        {
            throw new CustosException("bot.yaml を読めません：" + string.Join("; ", errors.Items));
        }

        var node = Scenario.ToJson(root) as JsonObject ?? throw new CustosException("bot.yaml はマッピングで書いてください");
        node.Remove("version");
        return node;
    }
}
