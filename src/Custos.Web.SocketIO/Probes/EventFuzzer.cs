using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.SocketIO.Wire;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Web.SocketIO.Probes;

/// <summary>ファジングの1件（再現できるよう、送ったものをそのまま残す）。</summary>
public sealed record FuzzCase(int Index, string Event, string Mutation, string Sender, JsonArray? Args, string? RawFrame);

/// <summary>
/// イベントのファジング（設計書 第26.7節）。宣言したイベントごとに、値を機械的に崩して送り、
/// クラッシュ、処理されない例外、イベントループの停止、不変条件で判定する。AIは使わない。
/// - 型を入れ替える（数値の代わりに文字列、配列、null）
/// - 項目を欠落させる、余分な項目を付ける
/// - 極端な値（負の数、巨大な数、非常に長い文字列、深い入れ子）
/// - __proto__ など、オブジェクトの原型を書き換える名前の項目
/// - 同じイベントを連続して送る、順序を入れ替える
/// - 公式のクライアントでは作れない、崩れたパケット（生のクライアント）
/// </summary>
public sealed class EventFuzzer
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;
    private readonly Random _random;
    private JsonObject? _headers;

    /// <summary>作る。</summary>
    public EventFuzzer(Lab.Lab lab, Run run, int seed, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
        _random = new Random(seed);
    }

    /// <summary>
    /// ファジングを行う。events が空なら authority.yaml の inputs のすべて。返り値は見つけたもの。
    /// 準備（対局中の2人と部外者1人）は scenarios/fuzz/context.yaml、値の見本は scenarios/fuzz/templates.yaml。
    /// </summary>
    public async Task<IReadOnlyList<Finding>> RunAsync(IReadOnlyList<string> events, int rounds, CancellationToken cancellationToken)
    {
        var templates = FuzzTemplates.Load(_lab.Workspace);
        var targets = _lab.Config.Authority.Inputs.Values.Where(i => i.Kind == "event").Select(i => i.Target).ToList();
        if (events.Count > 0)
        {
            targets = targets.Where(events.Contains).ToList();
        }

        var findings = new List<Finding>();
        var variables = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        var cases = GenerateCases(targets, templates, rounds).ToList();
        _log($"ファジング：{targets.Count} イベント、{cases.Count} 件");
        var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var attacker = await _lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false);
        var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
        var batch = new List<FuzzCase>();
        foreach (var fuzzCase in cases)
        {
            var filled = fuzzCase with
            {
                Sender = Scenario.Substitute(JsonValue.Create(fuzzCase.Sender), variables)?.GetValue<string>() ?? fuzzCase.Sender,
                Args = (JsonArray?)Scenario.Substitute(fuzzCase.Args, variables),
                RawFrame = fuzzCase.RawFrame == null ? null : Scenario.Substitute(JsonValue.Create(fuzzCase.RawFrame), variables)?.GetValue<string>(),
            };
            _run.AppendJsonLine("fuzz-cases.jsonl", filled);
            batch.Add(filled);
            try
            {
                await SendAsync(client, attacker, filled, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is CustosException or TimeoutException)
            {
                // アプリが落ちていると、送信が時間切れになる。判定はこの後の汎用の判定で行う
                _run.AppendJsonLine("fuzz-send-errors.jsonl", new { index = filled.Index, error = e.Message });
            }

            if (batch.Count < 25 && filled.Index != cases.Count - 1)
            {
                continue;
            }

            // 25件ごとに判定する（クラッシュ、処理されない例外、ハング）
            await _lab.SettleAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var context = new JudgeContext(_lab, _run, baseline);
            var reports = await JudgeRunner.RunAsync(context, [new AppHealthJudge(), new LogJudge()], cancellationToken).ConfigureAwait(false);
            var batchFindings = reports.SelectMany(r => r.Findings).Where(f => f.Stage == 1).ToList();
            if (batchFindings.Count > 0)
            {
                var path = _run.WriteText($"evidence/fuzz-batch-{filled.Index.ToString(CultureInfo.InvariantCulture)}.jsonl", string.Join("\n", batch.Select(b => Json.SerializeCompact(b))));
                foreach (var finding in batchFindings)
                {
                    findings.Add(finding with
                    {
                        Category = "input_validation",
                        Summary = $"ファジング中に検出：{finding.Summary}（直前の {batch.Count} 件のどれか。{string.Join(", ", batch.Select(b => b.Event).Distinct(StringComparer.Ordinal))}）",
                        Evidence = [.. finding.Evidence, new Core.Conformance.Evidence("直前に送った値", Path.GetRelativePath(_run.Dir, path))],
                    });
                }

                // 落ちた・止まった場合：直前の値を1件ずつ送り直して、落とした入力を特定する（再現手順）。その後、戻してから続ける
                if (batchFindings.Any(f => f.Category == "server_down"))
                {
                    if (_culprits < MaxCulpritSearches)
                    {
                        _culprits++;
                        var culprit = await FindCulpritAsync(batch, cases, cancellationToken).ConfigureAwait(false);
                        if (culprit != null)
                        {
                            var key = $"{culprit.Event}:{culprit.Mutation}";
                            findings.Add(Finding.Create("fuzzer", "crash_input", Severity.Critical, 1, "server_down", $"この1件のイベントでアプリ（サーバーのプロセス）が落ちる：{culprit.Event}（崩し方 {culprit.Mutation}、送り手 {culprit.Sender}）。値：{Short(culprit.Args?.ToJsonString() ?? culprit.RawFrame ?? string.Empty)}", Json.SerializeCompact(culprit), [new Core.Conformance.Evidence("落とした入力", Path.GetRelativePath(_run.Dir, _run.WriteJson($"evidence/fuzz-crash-{culprit.Index.ToString(CultureInfo.InvariantCulture)}.json", culprit)))], key));
                        }
                    }

                    await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
                    variables = await PrepareAsync(cancellationToken).ConfigureAwait(false);
                }

                baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
            }

            batch.Clear();
        }

        // 最後に不変条件も確かめる
        var final = await JudgeRunner.RunAsync(new JudgeContext(_lab, _run, baseline), [new InvariantJudge()], cancellationToken).ConfigureAwait(false);
        findings.AddRange(final.SelectMany(r => r.Findings).Select(f => f with { Summary = "ファジングの後：" + f.Summary }));
        return findings;
    }

    private const int MaxCulpritSearches = 5;
    private int _culprits;

    private async Task SendAsync(Docker.AgentSession client, Docker.AgentSession attacker, FuzzCase filled, CancellationToken cancellationToken)
    {
        if (filled.RawFrame != null)
        {
            // 崩れたパケットは、毎回つなぎ直した生のクライアントで送る（サーバーが切断しても次に影響しない）
            await attacker.CallAsync("raw.connect", new JsonObject { ["name"] = "fuzz", ["url"] = _lab.PublicUrl.ToString(), ["headers"] = _headers?.DeepClone(), ["connect_namespace"] = true, ["path"] = _lab.Config.Environment.App.SocketIoPath }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            await attacker.CallAsync("raw.send", new JsonObject { ["name"] = "fuzz", ["text"] = filled.RawFrame }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.CallAsync("user.emit", new JsonObject { ["user"] = filled.Sender, ["event"] = filled.Event, ["args"] = filled.Args?.DeepClone() ?? new JsonArray(), ["ack"] = false }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>落ちた直前の値を、戻してから1件ずつ送り直し、アプリを落とした入力を特定する。</summary>
    private async Task<FuzzCase?> FindCulpritAsync(List<FuzzCase> batch, List<FuzzCase> originals, CancellationToken cancellationToken)
    {
        _log($"アプリが落ちたので、直前の {batch.Count} 件を1件ずつ送り直して、原因の入力を探します");
        foreach (var candidate in batch)
        {
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            var variables = await PrepareAsync(cancellationToken).ConfigureAwait(false);
            // 準備し直したので、元の（値を埋める前の）ひな形から、新しい部屋や利用者で埋め直す
            var original = originals[candidate.Index];
            var replay = original with
            {
                Sender = Scenario.Substitute(JsonValue.Create(original.Sender), variables)?.GetValue<string>() ?? original.Sender,
                Args = (JsonArray?)Scenario.Substitute(original.Args, variables),
                RawFrame = original.RawFrame == null ? null : Scenario.Substitute(JsonValue.Create(original.RawFrame), variables)?.GetValue<string>(),
            };
            var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
            var attacker = await _lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false);
            try
            {
                await SendAsync(client, attacker, replay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is CustosException or TimeoutException)
            {
            }

            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            var state = await _lab.ContainerStateAsync(Lab.Containers.App(_lab.Project), cancellationToken).ConfigureAwait(false);
            if (state["running"]?.GetValue<bool>() != true)
            {
                _log($"原因の入力：{candidate.Event}（{candidate.Mutation}）");
                return candidate;
            }
        }

        return null;
    }

    private static string Short(string text) => text.Length > 160 ? text[..160] + "…" : text;

    private async Task<Dictionary<string, JsonNode?>> PrepareAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "fuzz", "context.yaml");
        var runner = new ScenarioRunner(_lab, _run, _log);
        if (File.Exists(path))
        {
            var result = await runner.RunAsync(Scenario.Load(path), cancellationToken).ConfigureAwait(false);
            _run.AppendJsonLine("fuzz-context.jsonl", result);
            if (!result.Ok)
            {
                throw new CustosException("ファジングの準備のシナリオが失敗しました：" + result.Error);
            }
        }

        _headers = await runner.BrowserLikeHeadersAsync(cancellationToken).ConfigureAwait(false);
        return runner.Variables.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.Ordinal);
    }

    /// <summary>崩した値の一覧を作る（シードで決まる）。</summary>
    public IEnumerable<FuzzCase> GenerateCases(IReadOnlyList<string> events, IReadOnlyDictionary<string, JsonArray> templates, int rounds)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(templates);
        var index = 0;
        foreach (var name in events)
        {
            var template = templates.TryGetValue(name, out var t) ? t : new JsonArray(new JsonObject { ["roomId"] = "{room}" });
            var senders = new[] { "{fuzz_member}", "{fuzz_outsider}" };
            foreach (var sender in senders)
            {
                foreach (var (mutation, args) in Mutate(template))
                {
                    yield return new FuzzCase(index++, name, mutation, sender, args, null);
                }
            }

            // 崩れたパケット（生のクライアント。公式のクライアントでは作れない）
            foreach (var (mutation, frame) in RawMutations(name, template))
            {
                yield return new FuzzCase(index++, name, mutation, "raw", null, frame);
            }

            // 同じイベントを連続して送る
            for (var i = 0; i < Math.Max(1, rounds) * 10; i++)
            {
                yield return new FuzzCase(index++, name, "repeat", "{fuzz_member}", (JsonArray)template.DeepClone(), null);
            }
        }

        // 順序を入れ替える：いろいろなイベントを乱数の順で送る
        for (var i = 0; i < Math.Max(1, rounds) * events.Count * 2; i++)
        {
            var name = events[_random.Next(events.Count)];
            var template = templates.TryGetValue(name, out var t) ? t : new JsonArray(new JsonObject { ["roomId"] = "{room}" });
            yield return new FuzzCase(index++, name, "shuffled_order", "{fuzz_member}", (JsonArray)template.DeepClone(), null);
        }
    }

    private static IEnumerable<(string Mutation, JsonArray Args)> Mutate(JsonArray template)
    {
        // 引数そのものの型を入れ替える
        yield return ("no_args", []);
        yield return ("arg_null", [null]);
        yield return ("arg_string", ["x"]);
        yield return ("arg_number", [12345]);
        yield return ("arg_array", [new JsonArray(1, 2, 3)]);
        yield return ("arg_bool", [true]);
        yield return ("many_args", new JsonArray(Enumerable.Range(0, 50).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()));
        yield return ("nesting_50", [Nest(50)]);
        if (template.Count == 0 || template[0] is not JsonObject obj)
        {
            yield break;
        }

        foreach (var field in obj.Select(p => p.Key).ToList())
        {
            foreach (var (label, value) in Values())
            {
                var copy = (JsonObject)obj.DeepClone();
                copy[field] = value?.DeepClone();
                yield return ($"{field}:{label}", [copy]);
            }

            var missing = (JsonObject)obj.DeepClone();
            missing.Remove(field);
            yield return ($"{field}:missing", [missing]);
        }

        var extra = (JsonObject)obj.DeepClone();
        extra["isAdmin"] = true;
        extra["playerId"] = "{fuzz_victim_id}";
        extra["vsCpu"] = true;
        yield return ("extra_fields", [extra]);
        var proto = JsonNode.Parse("{\"__proto__\":{\"polluted\":true,\"isAdmin\":true},\"constructor\":{\"prototype\":{\"polluted\":true}}}") as JsonObject ?? [];
        foreach (var pair in obj)
        {
            proto[pair.Key] = pair.Value?.DeepClone();
        }

        yield return ("prototype_pollution", [proto]);
    }

    private static IEnumerable<(string Label, JsonNode? Value)> Values()
    {
        yield return ("null", null);
        yield return ("string", "x");
        yield return ("empty_string", string.Empty);
        yield return ("number", 7);
        yield return ("negative", -1);
        yield return ("huge", 9_007_199_254_740_993d);
        yield return ("float", 1.5);
        yield return ("infinity_literal", JsonNode.Parse("1e309"));
        yield return ("array", new JsonArray(1, "2"));
        yield return ("object", new JsonObject { ["toString"] = "3" });
        yield return ("bool", false);
        yield return ("long_string", new string('Z', 5000));
        yield return ("control_chars", "a\u0000b\u001bc‮");
        yield return ("sql_fragment", "' OR '1'='1'; DROP TABLE \"Room\"; --");
        yield return ("script", "<img src=x onerror=alert(1)>");
    }

    private static IEnumerable<(string Mutation, string Frame)> RawMutations(string name, JsonArray template)
    {
        var json = JsJson.Stringify(template.Count > 0 ? template[0] : null);
        var escapedName = JsJson.Stringify(JsonValue.Create(name));
        yield return ("raw_truncated_json", $"42[{escapedName},{json[..Math.Max(1, json.Length / 2)]}");
        yield return ("raw_object_payload", "42{\"a\":1}");
        yield return ("raw_reserved_event", "42[\"disconnect\"]");
        yield return ("raw_numeric_event_name", "42[1,2,3]");
        yield return ("raw_huge_ack_id", $"4299999999999999999999[{escapedName},{json}]");
        yield return ("raw_binary_without_attachment", $"451-[{escapedName},{{\"_placeholder\":true,\"num\":0}}]");
        yield return ("raw_placeholder_out_of_range", $"451-[{escapedName},{{\"_placeholder\":true,\"num\":99}}]");
        yield return ("raw_unknown_namespace", $"42/admin,[{escapedName},{json}]");
        yield return ("raw_unknown_packet_type", "49");
        yield return ("raw_deep_nesting", $"42[{escapedName},{new string('[', 2000)}{new string(']', 2000)}]");
        yield return ("raw_huge_payload", $"42[{escapedName},\"{new string('A', 40_000)}\"]");
        yield return ("raw_engine_noop", "6");
    }

    private static JsonNode Nest(int depth)
    {
        var builder = new StringBuilder();
        builder.Append(new string('[', depth));
        builder.Append(new string(']', depth));
        return JsonNode.Parse(builder.ToString(), documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = depth + 8 }) ?? new JsonArray();
    }
}
