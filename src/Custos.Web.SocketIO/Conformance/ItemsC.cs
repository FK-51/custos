using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Custos.Core.Conformance;
using Custos.Core.Util;
using Custos.SocketIO.Wire;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Web.SocketIO.Conformance;

/// <summary>C-1：UI から既知のデータを作り、状態の読み取り結果と一致すること（指示書 タスク8）。</summary>
public sealed class C1UiMatchesState : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("C-1", ConformanceCategory.C, ItemKind.Required, "共通", "画面で作ったデータと、状態の読み取り結果が一致", "第17.3節 C、指示書 タスク8");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scenario = LoadScenario(context, "conformance/ui-create.yaml") ?? throw new CustosException("scenarios/conformance/ui-create.yaml がありません");
        var result = await RunScenarioAsync(context, scenario, "c1-ui-create", cancellationToken).ConfigureAwait(false);
        var evidence = new List<Evidence> { new("画面の操作と照合", "evidence/c1-ui-create.json") };
        evidence.AddRange(result.Steps.Where(s => s.Kind == "browser_screenshot" && s.Data?["path"] is not null).Select(s => new Evidence("スクリーンショット", Rel(context, s.Data?["path"]?.GetValue<string>() ?? string.Empty))));
        var checks = result.Steps.Count(s => s.Kind is "expect" or "assert");
        return result.Ok
            ? Pass($"画面から登録して部屋を作り、状態の読み取り結果と {checks} 項目が一致しました", evidence)
            : Fail($"一致しません：{result.Error}", evidence);
    }
}

/// <summary>C-2：スクリーンショットが真っ白でない、エラーページでない、連続して同じ画像でないこと。</summary>
public sealed class C2Screenshots : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("C-2", ConformanceCategory.C, ItemKind.Required, "共通", "スクリーンショットが有効（真っ白でない、エラーページでない、画面が変われば画像も変わる）", "第17.3節 C、第12.8節");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var browser = await AgentAsync(context, "browser", cancellationToken).ConfigureAwait(false);
        await browser.CallAsync("page.open", new JsonObject { ["page"] = "c2", ["url"] = lab.PublicUrl.ToString() }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        await browser.CallAsync("page.wait_for", new JsonObject { ["page"] = "c2", ["selector"] = "body", ["timeout_ms"] = 15000 }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var before = await ShotAsync(context, browser, "c2", "c2-before", cancellationToken).ConfigureAwait(false);

        // 画面を変える操作（検証用リポジトリの scenarios/conformance/screen-change.yaml。なければ画面を少し動かす）
        if (LoadScenario(context, "conformance/screen-change.yaml") is { } change)
        {
            var runner = new ScenarioRunner(lab, context.Run, _ => { });
            runner.Variables["page"] = JsonValue.Create("c2");
            var result = await runner.RunAsync(change, cancellationToken).ConfigureAwait(false);
            context.Run.WriteJson("evidence/c2-screen-change.json", result);
        }

        var after = await ShotAsync(context, browser, "c2", "c2-after", cancellationToken).ConfigureAwait(false);
        var text = await browser.CallObjectAsync("page.eval", new JsonObject { ["page"] = "c2", ["script"] = "() => ({ title: document.title, text: (document.body && document.body.innerText || '').slice(0, 2000) })" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var pageText = text["result"]?["text"]?.GetValue<string>() ?? string.Empty;
        var title = text["result"]?["title"]?.GetValue<string>() ?? string.Empty;
        var errorPage = ErrorMarkers.Any(m => pageText.Contains(m, StringComparison.OrdinalIgnoreCase) || title.Contains(m, StringComparison.OrdinalIgnoreCase));
        var difference = Png.DifferenceRatio(before.Image, after.Image);
        var evidence = new List<Evidence> { new("画面が変わる前", Rel(context, before.Path)), new("画面が変わった後", Rel(context, after.Path)) };
        evidence.Add(new Evidence("判定", Rel(context, Write(context, "c2-screenshots", new JsonObject
        {
            ["before"] = Stats(before.Stats),
            ["after"] = Stats(after.Stats),
            ["difference_ratio"] = difference,
            ["title"] = title,
            ["error_page"] = errorPage,
        }))));
        await browser.CallAsync("page.close", new JsonObject { ["page"] = "c2" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (before.Stats.LooksBlank || after.Stats.LooksBlank)
        {
            return Fail($"スクリーンショットが真っ白です（一色の割合 {N(Math.Max(before.Stats.DominantShare, after.Stats.DominantShare))}）", evidence);
        }

        if (errorPage)
        {
            return Fail($"エラーページが表示されています（{title}）", evidence);
        }

        return difference > 0.002
            ? Pass($"スクリーンショットは有効です（色 {before.Stats.DistinctColors} 種類、画面が変わる前後で {N(difference * 100)}% の画素が変化）", evidence)
            : Fail($"画面が変わったのに、画像がほとんど変わりません（{N(difference * 100)}%）", evidence);
    }

    private static readonly string[] ErrorMarkers = ["Application error", "Internal Server Error", "502 Bad Gateway", "This page could not be found", "404: This page"];

    private static JsonObject Stats(ImageStats stats) => new()
    {
        ["width"] = stats.Width,
        ["height"] = stats.Height,
        ["dominant_share"] = stats.DominantShare,
        ["distinct_colors"] = stats.DistinctColors,
        ["luminance_stddev"] = stats.LuminanceStdDev,
    };

    private static async Task<(string Path, RgbaImage Image, ImageStats Stats)> ShotAsync(ConformanceContext context, Docker.AgentSession browser, string page, string name, CancellationToken cancellationToken)
    {
        var result = await browser.CallObjectAsync("page.screenshot", new JsonObject { ["page"] = page }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        var bytes = Convert.FromBase64String(result["png_b64"]?.GetValue<string>() ?? string.Empty);
        if (context.IsBroken(ComposeWriter.Breaks.BlankScreenshot))
        {
            // わざと壊す確認：スクリーンショットを真っ白な画像に差し替える
            var original = Png.Decode(bytes);
            bytes = Png.WhitePng(original.Width, original.Height);
        }

        var path = context.Run.WriteBytes($"screenshots/{name}.png", bytes);
        var image = Png.Decode(bytes);
        return (path, image, Png.Stats(image));
    }
}

/// <summary>C-3：わざと出したログ行が収集されていること。ブラウザのコンソールエラーも収集されていること。</summary>
public sealed class C3Logs : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("C-3", ConformanceCategory.C, ItemKind.Required, "共通", "わざと出したログ行と、ブラウザのコンソールエラーが集まる", "第17.3節 C、指示書 タスク8");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);

        // 目印には検証環境を示す語を入れない（アプリに見える場所に書くため）
        var marker = "trace-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var exec = await lab.Docker.RunAsync(["exec", Containers.App(lab.Project), "sh", "-c", $"echo {marker} > /proc/1/fd/1"], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        var logs = await lab.AppLogsAsync(DateTimeOffset.UtcNow.AddMinutes(-2), cancellationToken).ConfigureAwait(false);
        var appCollected = logs.Contains(marker, StringComparison.Ordinal);

        var browser = await AgentAsync(context, "browser", cancellationToken).ConfigureAwait(false);
        await browser.CallAsync("page.open", new JsonObject { ["page"] = "c3", ["url"] = lab.PublicUrl.ToString() }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        var browserMarker = "trace-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        await browser.CallAsync("page.eval", new JsonObject { ["page"] = "c3", ["script"] = $"() => {{ console.error('{browserMarker}'); return true; }}" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        var console = await browser.CallObjectAsync("page.console", new JsonObject { ["page"] = "c3" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var messages = console["messages"] as JsonArray ?? [];
        var browserCollected = messages.Any(m => m?["text"]?.GetValue<string>()?.Contains(browserMarker, StringComparison.Ordinal) == true && m?["level"]?.GetValue<string>() == "error");
        await browser.CallAsync("page.close", new JsonObject { ["page"] = "c3" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var path = context.Run.WriteText("evidence/c3-app-log-tail.txt", logs.Length > 20000 ? logs[^20000..] : logs);
        var consolePath = Write(context, "c3-browser-console", console);
        var evidence = new List<Evidence> { new("アプリのログ（末尾）", Rel(context, path)), new("ブラウザのコンソール", Rel(context, consolePath)) };
        if (!exec.Succeeded)
        {
            return Fail("アプリのコンテナの標準出力に書き込めませんでした：" + exec.StandardError.Trim(), evidence);
        }

        return appCollected && browserCollected
            ? Pass("アプリの標準出力の目印の行と、ブラウザのコンソールエラーの両方が集まりました", evidence)
            : Fail($"集まっていません（アプリのログ：{appCollected}、ブラウザのコンソール：{browserCollected}）", evidence);
    }
}

/// <summary>
/// C-4：既知の通信が記録に残ること（指示書 タスク5）。スクリプトクライアントで送ったイベントと受け取ったイベントが、過不足なく記録にある。
/// ポーリングだけ、WebSocket だけ、切り替える場合の3通り。
/// </summary>
public sealed class C4Records : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("C-4", ConformanceCategory.C, ItemKind.Required, "共通", "既知の通信が過不足なく記録に残る（ポーリング、WebSocket、切り替えの3通り）", "第17.3節 C、指示書 タスク5");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var client = await lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var runner = new ScenarioRunner(lab, context.Run, _ => { });
        var headers = await runner.BrowserLikeHeadersAsync(cancellationToken).ConfigureAwait(false);
        var url = context.IsBroken(ComposeWriter.Breaks.BypassProxy)
            ? $"http://app:{context.Config.Environment.App.Port.ToString(CultureInfo.InvariantCulture)}"
            : lab.PublicUrl.ToString();
        var probe = LoadProbeEvent(context);
        var results = new JsonArray();
        var problems = new List<string>();
        var notApplicable = new List<string>();
        foreach (var transport in new[] { "polling", "websocket", "upgrade" })
        {
            var user = $"c4-{transport}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            var startSeq = lab.Records.Activity().LastSeq;
            JsonObject connect;
            try
            {
                connect = await client.CallObjectAsync("user.connect", new JsonObject { ["user"] = user, ["url"] = url, ["transport"] = transport, ["headers"] = headers.DeepClone(), ["timeout_ms"] = 15000, ["path"] = context.Config.Environment.App.SocketIoPath }, TimeSpan.FromSeconds(40), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is CustosException or TimeoutException)
            {
                // プロキシを通さずに、管理側からアプリの公開ポートへ直接確かめる。直接でも使えないなら、
                // アプリがこの通信方式に対応していない（記録の部品の問題ではない）ので、この通信方式は対象外にする
                var direct = transport == "polling" ? null : await DirectWebSocketAsync(lab, context, cancellationToken).ConfigureAwait(false);
                if (direct is { Supported: false })
                {
                    notApplicable.Add($"{transport}（アプリの公開ポートが WebSocket の切り替えに応答しない：{direct.Value.Detail}）");
                    results.Add(new JsonObject { ["transport"] = transport, ["error"] = e.Message, ["direct_check"] = direct.Value.Detail, ["not_applicable"] = true });
                    continue;
                }

                problems.Add($"{transport}：接続できません（{e.Message}{(direct is { } d ? $"。アプリへ直接なら応答する：{d.Detail}" : string.Empty)}）");
                results.Add(new JsonObject { ["transport"] = transport, ["error"] = e.Message, ["direct_check"] = direct?.Detail });
                continue;
            }

            var socketId = connect["socket_id"]?.GetValue<string>();
            var acks = new JsonArray();
            for (var i = 0; i < 3; i++)
            {
                var ack = await client.CallObjectAsync("user.emit", new JsonObject { ["user"] = user, ["event"] = probe.Event, ["args"] = probe.Args.DeepClone(), ["ack"] = true }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                acks.Add(ack["ack"]?.DeepClone());
            }

            if (transport == "upgrade")
            {
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            }

            var received = await client.CallObjectAsync("user.events", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            await client.CallAsync("user.disconnect", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            await client.CallAsync("user.remove", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            await lab.SettleAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

            var records = lab.Records.Read(startSeq).Where(e => e.Kind == "eio").ToList();
            var sids = lab.Records.SocketIds().Where(p => p.Value == socketId).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var mine = records.Where(e => e.Sid != null && sids.Contains(e.Sid)).ToList();
            var sentEvents = mine.Where(e => e.Dir == "c2s" && e.Sio == "event" && e.EventName == probe.Event).ToList();
            var ackRecords = mine.Where(e => e.Dir == "s2c" && e.Sio == "ack").ToList();
            var transports = mine.Select(e => e.Transport).Distinct(StringComparer.Ordinal).ToList();
            var expectTransports = transport == "upgrade" ? new[] { "polling", "websocket" } : [transport];
            var ackMatches = acks.Count == ackRecords.Count
                && acks.Select(a => JsJson.Stringify(a)).Order(StringComparer.Ordinal).SequenceEqual(ackRecords.Select(r => JsJson.Stringify(r.Args)).Order(StringComparer.Ordinal), StringComparer.Ordinal);
            var argsMatch = sentEvents.All(e => JsonNode.DeepEquals(e.Args, probe.Args));
            var receivedEvents = (received["events"] as JsonArray ?? []).Where(e => e?["event"]?.GetValue<string>() is { } n && !n.StartsWith("__", StringComparison.Ordinal)).Count();
            var recordedS2CEvents = mine.Count(e => e.Dir == "s2c" && e.Sio == "event");
            var entry = new JsonObject
            {
                ["transport"] = transport,
                ["socket_id"] = socketId,
                ["engine_sids"] = new JsonArray(sids.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                ["sent"] = 3,
                ["recorded_sent"] = sentEvents.Count,
                ["acks_received"] = acks.Count,
                ["recorded_acks"] = ackRecords.Count,
                ["ack_payloads_match"] = ackMatches,
                ["args_match"] = argsMatch,
                ["events_received_by_client"] = receivedEvents,
                ["events_recorded_s2c"] = recordedS2CEvents,
                ["transports_recorded"] = new JsonArray(transports.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            };
            results.Add(entry);
            if (sids.Count == 0)
            {
                problems.Add($"{transport}：この接続の記録がありません（プロキシを通っていない）");
                continue;
            }

            if (sentEvents.Count != 3 || !argsMatch)
            {
                problems.Add($"{transport}：送ったイベント 3 件のうち、記録にあるのは {sentEvents.Count} 件");
            }

            if (!ackMatches)
            {
                problems.Add($"{transport}：受け取った確認応答と記録が一致しません（受信 {acks.Count}、記録 {ackRecords.Count}）");
            }

            if (receivedEvents != recordedS2CEvents)
            {
                problems.Add($"{transport}：受け取ったイベント {receivedEvents} 件と、記録 {recordedS2CEvents} 件が一致しません");
            }

            if (!expectTransports.All(t => transports.Contains(t)))
            {
                problems.Add($"{transport}：記録されたトランスポートが {string.Join("/", transports)}（期待 {string.Join("/", expectTransports)}）");
            }
        }

        var path = Write(context, "c4-records", results);
        var checkedCount = 3 - notApplicable.Count;
        var note = notApplicable.Count == 0 ? string.Empty : $"。対象外：{string.Join("、", notApplicable)}";
        if (problems.Count == 0 && checkedCount == 0)
        {
            return Fail("どのトランスポートでも接続できませんでした" + note, [new Evidence("照合", Rel(context, path))]);
        }

        return problems.Count == 0
            ? Pass($"{checkedCount} 通りのトランスポートで、送ったイベント・受け取った確認応答とイベントが過不足なく記録にありました{note}", [new Evidence("照合", Rel(context, path)), new Evidence("通信の記録", lab.Records.EventsPath)])
            : Fail(string.Join("／", problems) + note, [new Evidence("照合", Rel(context, path))]);
    }

    /// <summary>管理側から、アプリの公開ポートに WebSocket の切り替えを直接要求し、101 が返るかを確かめる（プロキシを通さない）。</summary>
    private static async Task<(bool Supported, string Detail)?> DirectWebSocketAsync(Lab.Lab lab, ConformanceContext context, CancellationToken cancellationToken)
    {
        var ops = await lab.OpsAsync(cancellationToken).ConfigureAwait(false);
        var port = context.Config.Environment.App.Port;
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var details = new List<string>();
        foreach (var target in new[] { "/socket.io/?EIO=4&transport=websocket", "/socket.io?EIO=4&transport=websocket" })
        {
            var request = $"GET {target} HTTP/1.1\r\nHost: {lab.PublicUrl.Authority}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
            var result = await ops.CallObjectAsync("probe.tcp", new JsonObject { ["host"] = "app", ["port"] = port, ["send"] = request, ["timeout_ms"] = 5000 }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            var response = result["response"]?.GetValue<string>() ?? string.Empty;
            var status = response.Split("\r\n")[0];
            if (status.Contains(" 101 ", StringComparison.Ordinal))
            {
                return (true, $"{target} に 101");
            }

            details.Add($"{target} → {(status.Length > 0 ? status : "5 秒応答なし")}");
        }

        return (false, string.Join("、", details));
    }

    /// <summary>記録の確認に使う、副作用のないイベント（検証用リポジトリの scenarios/conformance/probe-event.yaml。なければ showRooms）。</summary>
    private static (string Event, JsonArray Args) LoadProbeEvent(ConformanceContext context)
    {
        var path = Path.Combine(context.Workspace.VerifyRepo, "scenarios", "conformance", "probe-event.yaml");
        if (File.Exists(path))
        {
            var scenario = Core.Scenarios.Scenario.Load(path);
            var step = scenario.Steps.FirstOrDefault(s => s.Kind == "emit");
            if (step != null)
            {
                return (step.Args["event"]?.GetValue<string>() ?? "showRooms", step.Args["args"] as JsonArray ?? []);
            }
        }

        return ("showRooms", [new JsonObject { ["maxPlayers"] = 4 }]);
    }
}
