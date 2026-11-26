using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Conformance;
using Custos.Core.Config;
using Custos.Core.Util;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.Scenarios;
using Custos.Web.SocketIO.State;

namespace Custos.Web.SocketIO.Conformance;

/// <summary>B-1：状態を変えた後にリセットすると、元のスナップショットと同じ状態に戻ること（指示書 タスク4）。</summary>
public sealed class B1Reset : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("B-1", ConformanceCategory.B, ItemKind.Required, "共通", "リセットで元のスナップショットと同じ状態に戻る", "第17.3節 B、指示書 タスク4");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var evidence = new List<Evidence>();
        var snapshot = lab.LoadSnapshot("initial");

        // 利用者の登録、部屋の作成、数手の着手を行う（検証用リポジトリの scenarios/conformance/reset-probe.yaml）
        var scenario = LoadScenario(context, "conformance/reset-probe.yaml") ?? throw new CustosException("scenarios/conformance/reset-probe.yaml がありません");
        var probe = await RunScenarioAsync(context, scenario, "b1-reset-probe", cancellationToken).ConfigureAwait(false);
        evidence.Add(new Evidence("状態を変えるシナリオ", "evidence/b1-reset-probe.json"));
        if (!probe.Ok)
        {
            return Fail($"状態を変えるシナリオが失敗しました：{probe.Error}", evidence);
        }

        var before = await lab.DbHashAsync(cancellationToken).ConfigureAwait(false);
        var changed = (before["content"] as JsonObject ?? []).Any(p => snapshot.Content.GetValueOrDefault(p.Key) != p.Value?.GetValue<string>());
        var client = await lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var users = (await client.CallAsync("users.list", [], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)) as JsonArray ?? [];

        // リセットの前の接続（Engine.IO の sid）を控える
        var socketIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in users.Select(u => u?.GetValue<string>() ?? string.Empty))
        {
            var info = await client.CallObjectAsync("user.info", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (info["socket_id"]?.GetValue<string>() is { } socketId)
            {
                socketIds.Add(socketId);
            }
        }

        var oldSids = lab.Records.SocketIds().Where(p => socketIds.Contains(p.Value)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var reset = await lab.ResetAsync("initial", context.IsBroken(ComposeWriter.Breaks.ResetSkipDbReplace), cancellationToken).ConfigureAwait(false);
        var cursor = lab.Records.Activity().LastSeq;
        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);

        // 以前の接続が残っていないこと：リセットが終わった後に、以前の接続（sid）でのやり取りがない（切断の通知を除く）
        var stillConnected = lab.Records.Read(cursor)
            .Where(e => e.Kind == "eio" && e.Sid != null && oldSids.Contains(e.Sid) && e.Eio is "message" or "ping" or "pong" && e.Sio != "disconnect")
            .Select(e => e.Sid ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        evidence.Add(new Evidence("リセットの前後", Rel(context, Write(context, "b1-reset", new JsonObject
        {
            ["snapshot"] = new JsonObject(snapshot.Content.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))),
            ["before_reset"] = before,
            ["after_reset"] = reset,
            ["still_connected"] = new JsonArray(stillConnected.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()),
        }))));
        var matches = reset["matches"]?.GetValue<bool>() == true;
        if (!changed)
        {
            return Fail("シナリオで状態が変わらなかったので、リセットを確かめられません", evidence);
        }

        if (!matches)
        {
            return Fail($"リセットの後、スナップショットと一致しない表があります：{reset["mismatched_tables"]?.ToJsonString()}", evidence);
        }

        return stillConnected.Count == 0
            ? Pass($"全テーブルがスナップショットと一致し、以前の接続 {users.Count} 件は残っていません（{reset["seconds"]} 秒）", evidence)
            : Fail($"以前の接続が残っています：{string.Join(", ", stillConnected)}", evidence);
    }
}

/// <summary>
/// B-2：外からの完了待ち。着手を1つ送って完了待ちをすると、手のログと送信された状態の両方に反映されている（続けて20回）。
/// アプリが書き込んでいる最中の読み取りを100回行い、エラーも不整合も出ない（指示書 タスク7）。
/// </summary>
public sealed class B2Settle : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("B-2", ConformanceCategory.B, ItemKind.Required, "Web", "完了待ち：着手が手のログと送信された状態に反映される（20回）、書き込み中の読み取り（100回）", "第17.3節 B、指示書 タスク7");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var evidence = new List<Evidence>();
        var breakSettle = context.IsBroken(ComposeWriter.Breaks.ReadWithoutSettle);
        await lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var scenario = LoadScenario(context, "conformance/game-start.yaml") ?? throw new CustosException("scenarios/conformance/game-start.yaml がありません");
        var runner = new ScenarioRunner(lab, context.Run, _ => { });
        var start = await runner.RunAsync(scenario, cancellationToken).ConfigureAwait(false);
        context.Run.WriteJson("evidence/b2-game-start.json", start);
        evidence.Add(new Evidence("対局を始めるシナリオ", "evidence/b2-game-start.json"));
        if (!start.Ok)
        {
            return Fail($"対局を始めるシナリオが失敗しました：{start.Error}", evidence);
        }

        var players = (start.Variables.GetValueOrDefault("players") as JsonArray ?? []).Select(p => p?.GetValue<string>() ?? string.Empty).Where(p => p.Length > 0).ToList();
        if (players.Count == 0)
        {
            return Fail("シナリオが players（対局する利用者の一覧）を設定していません", evidence);
        }

        var client = await lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var protocol = BotProtocolLoader.Load(lab.Workspace);
        var sqlKeys = context.Config.State.Keys.Values.Where(k => k.Source is SqlSource).ToList();
        var socketKeys = context.Config.State.Keys.Values.Where(k => k.Source is SocketSource).ToList();
        var reader = new StateReader(lab);
        var log = new JsonArray();
        var consecutive = 0;
        var firstFailure = (string?)null;
        var games = 1;
        for (var i = 0; i < 40 && consecutive < 20; i++)
        {
            var beforeDb = await lab.DbHashAsync(cancellationToken).ConfigureAwait(false);
            var beforeSeq = lab.Records.Activity().LastSeq;
            var beforeSent = lab.Records.Events(beforeSeq).Count;
            var bot = await client.CallObjectAsync("bot.play", new JsonObject
            {
                ["users"] = new JsonArray(players.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["protocol"] = protocol.DeepClone(),
                ["policy"] = "random",
                ["seed"] = 100 + i,
                ["max_moves"] = 1,
                ["timeout_ms"] = 30000,
            }, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            if ((bot["moves"]?.GetValue<int>() ?? 0) == 0)
            {
                // 対局が終わったら、新しい対局を始めて続ける（20回に届くまで。最大5対局）
                log.Add(new JsonObject { ["i"] = i, ["note"] = "着手できる手がない（対局が終わった）", ["bot"] = bot });
                if (bot["stop_reason"]?.GetValue<string>() != "all_finished" || games >= 5)
                {
                    break;
                }

                var again = await new ScenarioRunner(lab, context.Run, _ => { }).RunAsync(scenario, cancellationToken).ConfigureAwait(false);
                if (!again.Ok)
                {
                    firstFailure ??= $"{games + 1} 対局目を始められませんでした：{again.Error}";
                    break;
                }

                games++;
                continue;
            }

            var waited = 0.0;
            if (!breakSettle)
            {
                var settle = await lab.SettleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                waited = settle.WaitedMs;
                if (!settle.Settled)
                {
                    firstFailure ??= $"{i + 1} 回目：完了待ちが時間内に終わりませんでした";
                    consecutive = 0;
                    continue;
                }
            }

            // 完了待ちの直後に読んだ値と、さらに待ってから読んだ値が同じなら、処理中のものは残っていない
            var read1Db = await lab.DbHashAsync(cancellationToken).ConfigureAwait(false);
            var read1Seq = lab.Records.Read().Where(e => e.Kind == "eio" && e.Dir == "s2c" && e.Eio is not ("ping" or "pong")).Select(e => e.Seq).DefaultIfEmpty(0).Max();
            var socketValues = (await reader.ReadAsync(socketKeys, null, "main", cancellationToken).ConfigureAwait(false)).Select(StateCompare.ValuesOnly).ToList();
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            var read2Db = await lab.DbHashAsync(cancellationToken).ConfigureAwait(false);
            var read2Seq = lab.Records.Read().Where(e => e.Kind == "eio" && e.Dir == "s2c" && e.Eio is not ("ping" or "pong")).Select(e => e.Seq).DefaultIfEmpty(0).Max();
            var socketValues2 = (await reader.ReadAsync(socketKeys, null, "main", cancellationToken).ConfigureAwait(false)).Select(StateCompare.ValuesOnly).ToList();

            var sentReflected = lab.Records.Events(beforeSeq).Any(e => e.Dir == "s2c");
            var dbChanged = beforeDb["content"]?.ToJsonString() != read1Db["content"]?.ToJsonString();
            var stable = read1Db["content"]?.ToJsonString() == read2Db["content"]?.ToJsonString() && read1Seq == read2Seq && socketValues.SequenceEqual(socketValues2, StringComparer.Ordinal);
            var ok = sentReflected && stable;
            var unstable = new JsonObject();
            if (!stable)
            {
                // 何が変わり続けたか（証拠に残す）
                var db1 = read1Db["content"] as JsonObject ?? [];
                var db2 = read2Db["content"] as JsonObject ?? [];
                unstable["tables_changed"] = new JsonArray(db2.Where(p => db1[p.Key]?.ToJsonString() != p.Value?.ToJsonString()).Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
                unstable["events_after_settle"] = new JsonArray(lab.Records.Read(read1Seq).Where(e => e.Kind == "eio" && e.Dir == "s2c" && e.Eio is not ("ping" or "pong")).Take(10).Select(e => (JsonNode?)JsonValue.Create($"{e.Seq} {e.Transport} {e.Eio} {e.Sio} {e.EventName}")).ToArray());
                unstable["socket_keys_changed"] = new JsonArray(socketValues.Zip(socketValues2).Where(p => p.First != p.Second).Take(5).Select(p => (JsonNode?)JsonValue.Create(p.Second.Length > 200 ? p.Second[..200] : p.Second)).ToArray());
            }

            log.Add(new JsonObject
            {
                ["unstable"] = unstable,
                ["i"] = i,
                ["move"] = bot["trace"]?.AsArray().FirstOrDefault()?.DeepClone(),
                ["settle_ms"] = waited,
                ["sent_state_reflected"] = sentReflected,
                ["move_log_changed"] = dbChanged,
                ["stable_after_settle"] = stable,
                ["ok"] = ok,
                ["sent_events_before"] = beforeSent,
            });
            if (ok)
            {
                consecutive++;
            }
            else
            {
                firstFailure ??= $"{i + 1} 回目：{(sentReflected ? string.Empty : "送信された状態に反映されていない。")}{(stable ? string.Empty : "完了待ちの後も状態が変わり続けた（処理中のものが残っていた）")}";
                consecutive = 0;
            }
        }

        var totalLogChanged = log.OfType<JsonObject>().Count(e => e["move_log_changed"]?.GetValue<bool>() == true);
        evidence.Add(new Evidence("20回の着手と完了待ち", Rel(context, Write(context, "b2-moves", log))));

        // アプリが書き込んでいる最中の読み取り（100回）
        var concurrent = await ConcurrentReadsAsync(context, lab, client, protocol, players, sqlKeys, cancellationToken).ConfigureAwait(false);
        evidence.Add(new Evidence("書き込み中の読み取り", Rel(context, Write(context, "b2-concurrent-reads", concurrent))));
        var readErrors = concurrent["errors"]?.GetValue<int>() ?? 0;
        var readCount = concurrent["reads"]?.GetValue<int>() ?? 0;
        if (consecutive < 20)
        {
            return Fail($"続けて20回の成功に届きません（{consecutive} 回）。{firstFailure}", evidence);
        }

        if (totalLogChanged == 0)
        {
            return Fail("20回の着手で、手のログが一度も変わりませんでした", evidence);
        }

        var transientNote = concurrent["transient_violations"] is JsonArray t && t.Count > 0
            ? $"（アプリの書き込みの途中で一時的に破れて見えた不変条件：{string.Join(", ", t.Select(n => n?.ToString()))}。完了待ちの後は解消。アプリの所見として扱う）"
            : string.Empty;
        return readErrors == 0 && readCount >= 100
            ? Pass($"続けて20回、着手が送信された状態に反映され、完了待ちの後に処理が残っていませんでした（手のログの変化 {totalLogChanged} 回）。書き込み中の読み取り {readCount} 回で読み取りのエラーなし{transientNote}", evidence)
            : Fail($"書き込み中の読み取りで、読み取りのエラーが {readErrors} 件（{readCount} 回中）", evidence);
    }

    private static async Task<JsonObject> ConcurrentReadsAsync(ConformanceContext context, Lab.Lab lab, Docker.AgentSession client, JsonObject protocol, IReadOnlyList<string> players, List<StateKey> sqlKeys, CancellationToken cancellationToken)
    {
        // ボットに対局を最後まで打たせ、その間に読み取りを繰り返す
        var botTask = client.CallObjectAsync("bot.play", new JsonObject
        {
            ["users"] = new JsonArray(players.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["protocol"] = protocol.DeepClone(),
            ["policy"] = "random",
            ["seed"] = 4242,
            ["timeout_ms"] = 120000,
        }, TimeSpan.FromMinutes(3), cancellationToken);
        var ops = await lab.OpsAsync(cancellationToken).ConfigureAwait(false);
        var queries = new JsonArray(sqlKeys.Select(k => (JsonNode?)new JsonObject { ["sql"] = ((SqlSource)k.Source).Sql }).ToArray());
        var invariants = context.Config.Invariants?.Invariants.Where(i => i.Kind == InvariantKind.Sql && i.Query != null).ToList() ?? [];
        foreach (var invariant in invariants)
        {
            queries.Add(new JsonObject { ["sql"] = invariant.Query });
        }

        var errors = 0;
        var reads = 0;
        var violations = new JsonArray();
        var stopwatch = Stopwatch.StartNew();
        while (reads < 100 || !botTask.IsCompleted)
        {
            var result = await ops.CallObjectAsync("db.query_many", new JsonObject { ["db"] = lab.DbPathInOps, ["queries"] = queries.DeepClone() }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            reads++;
            var list = result["results"] as JsonArray ?? [];
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i]?["ok"]?.GetValue<bool>() != true)
                {
                    errors++;
                    violations.Add(new JsonObject { ["read"] = reads, ["query"] = i, ["error"] = list[i]?["error"]?.DeepClone() });
                }
                else if (i >= sqlKeys.Count && (list[i]?["rows"] as JsonArray)?.Count > 0)
                {
                    // 書き込みの途中で不変条件が破れて見える：読み取りの誤りではなく、アプリの書き込みが
                    // 複数のトランザクションに分かれている（途中の状態が見える）。後で、完了待ちの後も残るかを調べる
                    violations.Add(new JsonObject { ["read"] = reads, ["invariant"] = invariants[i - sqlKeys.Count].Id, ["rows"] = list[i]?["rows"]?.DeepClone() });
                }
            }

            if (reads >= 100 && botTask.IsCompleted)
            {
                break;
            }

            if (reads >= 1000)
            {
                break;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        var bot = await botTask.ConfigureAwait(false);

        // 途中で見えた不変条件の破れが、完了待ちの後も残るか（残るなら本当の違反。残らないなら途中の状態）
        await lab.SettleAsync(TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var transient = new JsonArray();
        var persistent = new JsonArray();
        foreach (var id in violations.OfType<JsonObject>().Select(v => v["invariant"]?.GetValue<string>()).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var invariant = invariants.First(v => v.Id == id);
            var after = await ops.CallObjectAsync("db.query", new JsonObject { ["db"] = lab.DbPathInOps, ["sql"] = invariant.Query }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            ((after["rows"] as JsonArray)?.Count > 0 ? persistent : transient).Add(id);
        }

        return new JsonObject
        {
            ["reads"] = reads,
            ["errors"] = errors,
            ["violations"] = violations,
            ["transient_violations"] = transient,
            ["persistent_violations"] = persistent,
            ["bot_moves"] = bot["moves"]?.DeepClone(),
            ["bot_stop"] = bot["stop_reason"]?.DeepClone(),
            ["seconds"] = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
        };
    }
}

/// <summary>B-3（能力依存）：時刻とシードを固定すると、同じ値が返ること。</summary>
public sealed class B3ClockAndSeed : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("B-3", ConformanceCategory.B, ItemKind.Capability, "共通", "時刻とシードを固定すると、同じ値が返る", "第17.3節 B", "clock_seed");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Config.App.TestApi == TestApiMode.None)
        {
            return Skip("テスト用API（時刻、乱数）がない");
        }

        if (LabOf(context).State.BuildKind != "test")
        {
            return Skip("本番用ビルドで動いている（時刻と乱数の固定は、テスト用ビルド custos up --build test で確かめる）");
        }

        var lab = LabOf(context);
        var ops = await lab.OpsAsync(cancellationToken).ConfigureAwait(false);
        var port = context.Config.App.TestApiPort.ToString(CultureInfo.InvariantCulture);
        async Task<JsonObject> Post(string path, string body) =>
            await ops.CallObjectAsync("http.request", new JsonObject { ["url"] = $"http://app:{port}{path}", ["method"] = "POST", ["body"] = body }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Get(string path) =>
            await ops.CallObjectAsync("http.request", new JsonObject { ["url"] = $"http://app:{port}{path}", ["method"] = "GET" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        static JsonNode? Body(JsonObject response) => JsonNode.Parse(response["body"]?.GetValue<string>() ?? "null");

        // 2回とも：時刻を決めた値にして止め、シードを入れて乱数の見本を取り、時刻を読む。応答の本文（見本と時刻）だけを比べる
        var samples = new JsonArray();
        for (var i = 0; i < 2; i++)
        {
            // 止めてから時刻を決める（決めてから止めると、その間に時計が進む）
            var freeze = await Post("/__verify/clock", "{\"action\":\"freeze\"}").ConfigureAwait(false);
            var set = await Post("/__verify/clock", "{\"action\":\"set\",\"epoch_ms\":1767225600000}").ConfigureAwait(false);
            var seed = await Post("/__verify/seed", "{\"seed\":12345,\"sample\":5}").ConfigureAwait(false);
            var clock = await Get("/__verify/clock").ConfigureAwait(false);
            samples.Add(new JsonObject
            {
                ["statuses"] = new JsonArray(set["status"]?.DeepClone(), freeze["status"]?.DeepClone(), seed["status"]?.DeepClone(), clock["status"]?.DeepClone()),
                ["samples"] = Body(seed)?["samples"]?.DeepClone(),
                ["now"] = Body(clock)?["now"]?.DeepClone(),
            });
        }

        await Post("/__verify/clock", "{\"action\":\"resume\"}").ConfigureAwait(false);
        await Post("/__verify/seed", "{\"seed\":null}").ConfigureAwait(false);
        var path2 = Write(context, "b3-clock-seed", samples);
        var a = samples[0]?["samples"]?.ToJsonString() + "|" + samples[0]?["now"]?.ToJsonString();
        var b = samples[1]?["samples"]?.ToJsonString() + "|" + samples[1]?["now"]?.ToJsonString();
        var ok = a == b
            && samples.All(s => (s?["statuses"] as JsonArray ?? []).All(st => st?.GetValue<int>() == 200))
            && (samples[0]?["samples"] as JsonArray)?.Count > 0
            && samples[0]?["now"]?.GetValue<double>() == 1767225600000;
        return ok ? Pass("時刻とシードを固定すると、2回とも同じ値が返りました", [new Evidence("応答", Rel(context, path2))]) : Fail("時刻とシードを固定しても、同じ値になりませんでした", [new Evidence("応答", Rel(context, path2))]);
    }
}

/// <summary>B-4（能力依存）：同じシナリオを5回実行し、状態のハッシュがすべて一致すること（決定性の等級。第22.7節）。</summary>
public sealed class B4Reproducible : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("B-4", ConformanceCategory.B, ItemKind.Capability, "共通", "同じシナリオを5回実行し、状態のハッシュがすべて一致", "第17.3節 B、第22.7節", "determinism");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Config.App.TestApi == TestApiMode.None && !context.Config.App.DeclaredCapabilities.ContainsKey("determinism"))
        {
            return Skip("決定性（シードの固定）の能力を宣言していない");
        }

        if (context.Config.App.TestApi == TestApiMode.Http && LabOf(context).State.BuildKind != "test")
        {
            return Skip("本番用ビルドで動いている（再実行の一致は、テスト用ビルド custos up --build test で確かめる）");
        }

        var lab = LabOf(context);
        var determinism = new Determinism.DeterminismRunner(lab, context.Run);
        var result = await determinism.MeasureAsync(5, cancellationToken).ConfigureAwait(false);
        var path = Write(context, "b4-determinism", result.ToJson());
        return result.Grade is "exact" or "normalized"
            ? Pass($"5回の実行の状態のハッシュが一致しました（等級：{result.GradeLabel}、除外 {result.VolatileCount} 件）", [new Evidence("5回の比較", Rel(context, path))])
            : Fail($"5回の実行で状態が一致しませんでした（等級：{result.GradeLabel}）：{string.Join(", ", result.DifferingKeys.Take(5))}", [new Evidence("5回の比較", Rel(context, path))]);
    }
}

/// <summary>B-5（能力依存）：宣言していないキーが、正直なクライアント同士の実行で食い違わないこと（第22.4節）。</summary>
public sealed class B5HonestNoise : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("B-5", ConformanceCategory.B, ItemKind.Capability, "共通", "宣言していないキーが、正直なクライアント同士で食い違わない", "第17.3節 B、第22.4節", "determinism");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Config.App.TestApi == TestApiMode.None && !context.Config.App.DeclaredCapabilities.ContainsKey("determinism"))
        {
            return Skip("決定性（シードの固定）の能力を宣言していない");
        }

        if (context.Config.App.TestApi == TestApiMode.Http && LabOf(context).State.BuildKind != "test")
        {
            return Skip("本番用ビルドで動いている（再実行の一致は、テスト用ビルド custos up --build test で確かめる）");
        }

        var lab = LabOf(context);
        var determinism = new Determinism.DeterminismRunner(lab, context.Run);
        var result = await determinism.MeasureAsync(context.Config.State.Determinism.HonestRuns, cancellationToken).ConfigureAwait(false);
        var undeclared = result.DifferingKeys.Where(k => !context.Config.State.Determinism.Volatile.Contains(k, StringComparer.Ordinal)).ToList();
        var path = Write(context, "b5-honest-noise", result.ToJson());
        return undeclared.Count == 0
            ? Pass($"正直なクライアント同士の {result.Runs} 回の実行で、宣言していないキーは食い違いませんでした", [new Evidence("比較", Rel(context, path))])
            : Fail($"宣言していないキーが食い違いました（競合状態の疑い）：{string.Join(", ", undeclared)}", [new Evidence("比較", Rel(context, path))]);
    }
}

/// <summary>状態の読み取り結果の比べ方（読んだ時刻などを除き、キーと値だけ）。</summary>
internal static class StateCompare
{
    public static string ValuesOnly(StateReadResult result) =>
        result.Key + "=" + string.Join(";", result.Values.OrderBy(v => v.Entity, StringComparer.Ordinal).Select(v => v.Entity + ":" + (v.Value?.ToJsonString() ?? "null")));
}
