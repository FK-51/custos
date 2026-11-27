using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.SocketIO.Wire;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Web.SocketIO.Probes;

/// <summary>
/// 隠し情報となりすましの検査（設計書 第25.5〜25.7節、第26.7節。AIは使わない）。
/// - 秘密の値（再接続トークン）が推測できないこと：続けて登録した利用者の値に規則性がない
/// - 表示される入力：目印を付けたスクリプトが他の利用者のブラウザで実行されない。SQL の断片で DB が変わらない
/// - 席を奪えないこと：攻撃役が、被害者の手札（被害者あての状態）を受け取れない。被害者として手を打てない
/// アプリごとの操作は検証用リポジトリの scenarios/security/ に置く（register.yaml、display-inputs.yaml、hijack.yaml）。
/// </summary>
public sealed class SecurityProber
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    private string? _tokensFile;

    /// <summary>証拠のファイル名（同じ実行で何度呼ばれても、ぶつからない名前を最初に決めて使い続ける）。</summary>
    private string TokensFile => _tokensFile ??= _run.FreeName("security-tokens.json");

    private string? _inputsFile;

    /// <summary>証拠のファイル名（同じ実行で何度呼ばれても、ぶつからない名前を最初に決めて使い続ける）。</summary>
    private string InputsFile => _inputsFile ??= _run.FreeName("security-inputs.json");

    private string? _hijackScenarioFile;

    /// <summary>証拠のファイル名（同じ実行で何度呼ばれても、ぶつからない名前を最初に決めて使い続ける）。</summary>
    private string HijackScenarioFile => _hijackScenarioFile ??= _run.FreeName("security-hijack-scenario.json");

    private string? _hijackFile;

    /// <summary>証拠のファイル名（同じ実行で何度呼ばれても、ぶつからない名前を最初に決めて使い続ける）。</summary>
    private string HijackFile => _hijackFile ??= _run.FreeName("security-hijack.json");

    /// <summary>作る。</summary>
    public SecurityProber(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    private string ScenarioPath(string name) => Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "security", name);

    /// <summary>すべて実行する。</summary>
    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        findings.AddRange(await TokensAsync(20, cancellationToken).ConfigureAwait(false));
        findings.AddRange(await DisplayedInputsAsync(cancellationToken).ConfigureAwait(false));
        findings.AddRange(await HijackAsync(cancellationToken).ConfigureAwait(false));
        return findings;
    }

    private static Finding NotPrepared(string what, string file) =>
        Finding.Create("security_prober", "not_prepared", Severity.Low, 1, "coverage", $"{what}の準備（scenarios/security/{file}）がないため、検査していません（未確認）", null, [], file);

    /// <summary>秘密の値が推測できないこと（第25.6節）。</summary>
    public async Task<IReadOnlyList<Finding>> TokensAsync(int count, CancellationToken cancellationToken)
    {
        var path = ScenarioPath("register.yaml");
        if (!File.Exists(path))
        {
            return [NotPrepared("秘密の値の検査", "register.yaml")];
        }

        var keys = (_lab.Config.Security?.SecretStateKeys ?? []).Select(k => _lab.Config.State.Keys.GetValueOrDefault(k)).OfType<StateKey>().Where(k => k.Source is SqlSource).ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var scenario = Scenario.Load(path);
        var runner = new ScenarioRunner(_lab, _run, _log);
        for (var i = 0; i < count; i++)
        {
            runner.Variables["user"] = JsonValue.Create($"tok{i.ToString(CultureInfo.InvariantCulture)}");
            runner.Variables["name"] = JsonValue.Create($"tok{i.ToString(CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}");
            var result = await runner.RunAsync(scenario, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                throw new CustosException("利用者の登録のシナリオが失敗しました：" + result.Error);
            }
        }

        await _lab.SettleAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var findings = new List<Finding>();
        var report = new JsonArray();
        foreach (var key in keys)
        {
            var sql = ((SqlSource)key.Source).Sql;
            var rows = await _lab.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
            var pairs = (rows["rows"] as JsonArray ?? []).OfType<JsonArray>().Select(r => (Entity: r[0]?.ToString() ?? string.Empty, Value: r.Count > 1 ? r[1]?.ToString() : null)).Where(p => p.Value != null).ToList();
            var publicValues = new List<string>();
            publicValues.AddRange(pairs.Select(p => p.Entity));
            foreach (var other in _lab.Config.State.Keys.Values.Where(k => k.Source is SqlSource && k.Name != key.Name && k.Per == key.Per && _lab.Config.Authority.Resolve(k.Name).Visibility is not (Visibility.Owner or Visibility.None)))
            {
                var otherRows = await _lab.QueryAsync(((SqlSource)other.Source).Sql, cancellationToken).ConfigureAwait(false);
                publicValues.AddRange((otherRows["rows"] as JsonArray ?? []).OfType<JsonArray>().Select(r => r.Count > 1 ? r[1]?.ToString() : null).OfType<string>());
            }

            var analysis = TokenAnalysis.Analyze(pairs.Select(p => p.Value ?? string.Empty).ToList(), publicValues);
            report.Add(new JsonObject { ["key"] = key.Name, ["analysis"] = JsonNode.Parse(Json.SerializeCompact(analysis)) });
            foreach (var problem in analysis.Problems)
            {
                findings.Add(Finding.Create("security_prober", "predictable_secret", Severity.Critical, 1, "seat_hijack", $"秘密の値 {key.Name} が推測できる疑い：{problem}", null, [new Evidence("分析", TokensFile)], key.Name + ":" + problem[..Math.Min(20, problem.Length)]));
            }
        }

        _run.WriteJson(TokensFile, report);
        _log($"秘密の値の分析：{keys.Count} 種類、各 {count} 件（問題 {findings.Count} 件）");
        return findings;
    }

    /// <summary>目印を付けたスクリプト（XSS）と、SQL の断片（第25.6節）。</summary>
    public async Task<IReadOnlyList<Finding>> DisplayedInputsAsync(CancellationToken cancellationToken)
    {
        var path = ScenarioPath("display-inputs.yaml");
        if (!File.Exists(path))
        {
            return [NotPrepared("表示される入力の検査", "display-inputs.yaml")];
        }

        var findings = new List<Finding>();
        var results = new JsonArray();
        var scenario = Scenario.Load(path);
        var security = _lab.Config.Security;
        var payloads = new List<(string Kind, string Marker, string Payload)>();
        if (security?.XssMarkers ?? true)
        {
            foreach (var template in XssTemplates)
            {
                var marker = "cx" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
                payloads.Add(("xss", marker, template.Replace("{m}", marker, StringComparison.Ordinal)));
            }
        }

        if (security?.SqlMarkers ?? true)
        {
            foreach (var fragment in SqlFragments)
            {
                payloads.Add(("sql", string.Empty, fragment));
            }
        }

        foreach (var (kind, marker, payload) in payloads)
        {
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            var schemaBefore = await SchemaAsync(cancellationToken).ConfigureAwait(false);
            var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
            var runner = new ScenarioRunner(_lab, _run, _log);
            runner.Variables["payload"] = JsonValue.Create(payload);
            runner.Variables["marker"] = JsonValue.Create(marker);
            var result = await runner.RunAsync(scenario, cancellationToken).ConfigureAwait(false);
            await _lab.SettleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            var entry = new JsonObject { ["kind"] = kind, ["payload"] = payload, ["marker"] = marker, ["scenario_ok"] = result.Ok, ["scenario_error"] = result.Error };
            var stored = await StoredAsync(payload, cancellationToken).ConfigureAwait(false);
            entry["stored_verbatim_in"] = new JsonArray(stored.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            if (kind == "xss")
            {
                var pages = runner.Variables.TryGetValue("pages", out var p) && p is JsonArray list ? list.Select(n => n?.ToString() ?? "victim").ToList() : ["victim"];
                var browser = await _lab.BrowserAsync(1, false, cancellationToken).ConfigureAwait(false);
                var pageResults = new JsonArray();
                foreach (var page in pages)
                {
                    try
                    {
                        var script = $$"""
                            (() => {
                              const m = {{JsJsonString(marker)}};
                              const ran = (window.__custos_xss || []).includes(m) || window['__' + m] === 1;
                              const injected = document.querySelectorAll('img[onerror*="' + m + '"], svg[onload*="' + m + '"], [onload*="' + m + '"], [onerror*="' + m + '"], a[href^="javascript:"][href*="' + m + '"]').length
                                + [...document.scripts].filter(s => s.textContent.includes(m)).length;
                              const shown = (document.body ? document.body.innerText : '').includes(m);
                              return { ran, injected, shown, url: location.href };
                            })()
                            """;
                        var evaluated = await browser.CallObjectAsync("page.eval", new JsonObject { ["page"] = page, ["script"] = script }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                        var value = evaluated["result"] as JsonObject ?? [];
                        pageResults.Add(new JsonObject { ["page"] = page, ["result"] = value.DeepClone() });
                        if (value["ran"]?.GetValue<bool>() == true)
                        {
                            findings.Add(Finding.Create("security_prober", "xss_executed", Severity.Critical, 1, "xss", $"目印を付けたスクリプトが、別の利用者のブラウザ（{page}）で実行されました（入力：{payload}）", null, [new Evidence("試した記録", InputsFile)], "xss:" + page + ":" + XssTemplates.ToList().FindIndex(t => payload.StartsWith(t[..6], StringComparison.Ordinal))));
                        }
                        else if (value["injected"]?.GetValue<double>() > 0)
                        {
                            findings.Add(Finding.Create("security_prober", "html_injected", Severity.High, 1, "xss", $"入力がHTMLとして画面に入りました（スクリプトは実行されていない。ブラウザ {page}、入力：{payload}）", null, [new Evidence("試した記録", InputsFile)], "html:" + page));
                        }
                    }
                    catch (CustosException e)
                    {
                        pageResults.Add(new JsonObject { ["page"] = page, ["error"] = e.Message });
                    }
                }

                entry["pages"] = pageResults;
                entry["shown_somewhere"] = pageResults.Any(r => r?["result"]?["shown"]?.GetValue<bool>() == true);
            }
            else
            {
                var schemaAfter = await SchemaAsync(cancellationToken).ConfigureAwait(false);
                entry["schema_unchanged"] = schemaBefore == schemaAfter;
                if (schemaBefore != schemaAfter)
                {
                    findings.Add(Finding.Create("security_prober", "sql_injection_schema", Severity.Critical, 1, "sqli", $"SQL の断片を入力すると、DB の構造が変わりました（入力：{payload}）", null, [new Evidence("試した記録", InputsFile)], "sqli-schema"));
                }

                var logs = await new LogJudge().JudgeAsync(new JudgeContext(_lab, _run, baseline), cancellationToken).ConfigureAwait(false);
                var sqlErrors = logs.Findings.Where(f => (f.Detail ?? f.Summary).Contains("SQL", StringComparison.OrdinalIgnoreCase) || (f.Detail ?? f.Summary).Contains("syntax", StringComparison.OrdinalIgnoreCase)).ToList();
                entry["sql_errors_in_log"] = sqlErrors.Count;
                if (sqlErrors.Count > 0)
                {
                    findings.Add(Finding.Create("security_prober", "sql_injection_error", Severity.Critical, 1, "sqli", $"SQL の断片を入力すると、SQL のエラーがログに出ました（入力が SQL として解釈された疑い。入力：{payload}）", sqlErrors[0].Summary, [new Evidence("試した記録", InputsFile)], "sqli-error"));
                }
            }

            results.Add(entry);
        }

        // 秘密の値の照合（SQL の断片で読み出されていないか）
        _run.WriteJson(InputsFile, results);
        var notShown = results.OfType<JsonObject>().Count(r => r["kind"]?.GetValue<string>() == "xss" && r["shown_somewhere"]?.GetValue<bool>() != true);
        if (notShown > 0)
        {
            findings.Add(Finding.Create("security_prober", "xss_not_displayed", Severity.Low, 1, "coverage", $"目印を付けた入力のうち {notShown} 件は、被害者の画面に表示されなかったため、その表示箇所は確かめられていません（未確認。入力の長さの制限などで切られた可能性）", null, [new Evidence("試した記録", InputsFile)], "xss-not-displayed"));
        }

        _log($"表示される入力：{payloads.Count} 通り（問題 {findings.Count(f => f.Category != "coverage")} 件）");
        return findings;
    }

    /// <summary>席を奪えないこと（第25.6節）。準備は scenarios/security/hijack.yaml（hijack: { victim, attacker } を set する）。</summary>
    public async Task<IReadOnlyList<Finding>> HijackAsync(CancellationToken cancellationToken)
    {
        var path = ScenarioPath("hijack.yaml");
        if (!File.Exists(path))
        {
            return [NotPrepared("席の乗っ取りの検査", "hijack.yaml")];
        }

        await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
        var runner = new ScenarioRunner(_lab, _run, _log);
        var result = await runner.RunAsync(Scenario.Load(path), cancellationToken).ConfigureAwait(false);
        await _lab.SettleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        _run.WriteJson(HijackScenarioFile, JsonNode.Parse(Json.SerializeCompact(result)));
        var findings = new List<Finding>();
        if (!runner.Variables.TryGetValue("hijack", out var node) || node is not JsonObject hijack)
        {
            throw new CustosException("hijack.yaml は set: { hijack: { victim: <利用者>, attacker: <利用者> } } を含めてください");
        }

        var victim = hijack["victim"]?.ToString() ?? string.Empty;
        var attacker = hijack["attacker"]?.ToString() ?? string.Empty;
        var attackStart = runner.Variables.TryGetValue("attack_start_seq", out var s) && s is JsonValue sv && sv.TryGetValue<long>(out var seq) ? seq : baseline.RecordsSeq;
        var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var attackerAgent = await _lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false);
        async Task<string?> SocketOf(string user)
        {
            // 利用者は、利用者側のクライアントか攻撃役のコンテナのどちらかにいる
            foreach (var agent in new[] { client, attackerAgent })
            {
                try
                {
                    var info = await agent.CallObjectAsync("user.info", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    return info["socket_id"]?.GetValue<string>();
                }
                catch (CustosException)
                {
                }
            }

            return null;
        }

        var victimSocket = await SocketOf(victim).ConfigureAwait(false);
        var attackerSocket = await SocketOf(attacker).ConfigureAwait(false);
        var socketIds = _lab.Records.SocketIds();
        string? Recipient(RecordEntry entry) => entry.Sid != null && socketIds.TryGetValue(entry.Sid, out var id) ? id : null;

        // 被害者あての状態（本人にだけ送ってよいキー）の値を、攻撃役が受け取ったか
        var ownerKeys = _lab.Config.State.Keys.Values.Where(k => k.Source is SocketSource && _lab.Config.Authority.Resolve(k.Name).Visibility == Visibility.Owner).ToList();
        var turnKey = hijack["turn_key"]?.ToString();
        var evidence = new JsonObject { ["victim"] = victim, ["attacker"] = attacker, ["victim_socket"] = victimSocket, ["attacker_socket"] = attackerSocket, ["scenario_ok"] = result.Ok };
        if (turnKey != null && _lab.Config.State.Keys.TryGetValue(turnKey, out var turn) && turn.Source is SocketSource turnSource)
        {
            var entries = _lab.Records.Read(attackStart).Where(e => e.Kind == "eio" && e.Dir == "s2c" && e.Sio == "event" && e.EventName != null && turnSource.Events.Contains(e.EventName)).ToList();
            var victimTurns = entries.Where(e => Recipient(e) == victimSocket).Select(e => Value(e, turnSource.Path)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var stolen = entries.Where(e => Recipient(e) == attackerSocket && Value(e, turnSource.Path) is { } v && victimTurns.Contains(v)).ToList();
            evidence["victim_turn_numbers"] = new JsonArray(victimTurns.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            evidence["views_of_victim_received_by_attacker"] = stolen.Count;
            if (stolen.Count > 0)
            {
                findings.Add(Finding.Create("security_prober", "victim_view_received", Severity.Critical, 1, "seat_hijack", $"攻撃役（{attacker}）が、被害者（{victim}）あての状態を {stolen.Count} 回受け取りました（席の乗っ取り。{string.Join(", ", ownerKeys.Select(k => k.Name))} が見える）", null, [new Evidence("判定", HijackFile)], "victim_view"));
            }
        }

        // 被害者が何も送っていないのに、被害者の手が進んだか（被害者として手を打てた）
        var playedKey = hijack["played_key"]?.ToString();
        var replyEvent = BotProtocolLoader.Load(_lab.Workspace)["reply_event"]?.ToString();
        if (playedKey != null && _lab.Config.State.Keys.TryGetValue(playedKey, out var played) && played.Source is SocketSource playedSource && replyEvent != null)
        {
            var victimFrames = _lab.Records.Read(attackStart).Where(e => e.Kind == "eio" && e.Sio == "event").ToList();
            var lengths = new List<(long Seq, int Length)>();
            var victimReplies = new List<long>();
            foreach (var entry in victimFrames)
            {
                if (Recipient(entry) != victimSocket)
                {
                    continue;
                }

                if (entry.Dir == "c2s" && entry.EventName == replyEvent)
                {
                    victimReplies.Add(entry.Seq);
                }
                else if (entry.Dir == "s2c" && entry.EventName != null && playedSource.Events.Contains(entry.EventName) && Node(entry, playedSource.Path) is JsonArray list)
                {
                    lengths.Add((entry.Seq, list.Count));
                }
            }

            var forced = 0;
            for (var i = 1; i < lengths.Count; i++)
            {
                if (lengths[i].Length > lengths[i - 1].Length && !victimReplies.Any(r => r > lengths[i - 1].Seq && r < lengths[i].Seq))
                {
                    forced++;
                }
            }

            evidence["victim_moves_without_victim_reply"] = forced;
            if (forced > 0)
            {
                findings.Add(Finding.Create("security_prober", "acted_as_victim", Severity.Critical, 1, "seat_hijack", $"被害者（{victim}）が何も送っていないのに、被害者の場の札が {forced} 回増えました（被害者として手を打たれた疑い。時間切れの自動処理でないか確認が要る）", null, [new Evidence("判定", HijackFile)], "acted_as_victim"));
            }
        }

        // 秘密の値が攻撃役に届いていないか（汎用の判定器）
        var leaks = await new SecretLeakJudge().JudgeAsync(new JudgeContext(_lab, _run, baseline), cancellationToken).ConfigureAwait(false);
        findings.AddRange(leaks.Findings);
        _run.WriteJson(HijackFile, evidence);
        _log($"席の乗っ取り：シナリオ {(result.Ok ? "完了" : "途中で失敗：" + result.Error)}、問題 {findings.Count} 件");
        return findings;
    }

    private static string? Value(RecordEntry entry, string path) => Node(entry, path)?.ToJsonString();

    private static JsonNode? Node(RecordEntry entry, string path)
    {
        var first = entry.Args is JsonArray args && args.Count > 0 ? args[0] : null;
        var selected = Json.SelectPath(first, path);
        return selected.Count > 0 ? selected[0] : null;
    }

    private async Task<string> SchemaAsync(CancellationToken cancellationToken)
    {
        var result = await _lab.QueryAsync("SELECT type, name, sql FROM sqlite_master ORDER BY type, name", cancellationToken).ConfigureAwait(false);
        return Hashing.Sha256Hex(result["rows"]?.ToJsonString() ?? string.Empty);
    }

    /// <summary>入力がそのまま（データとして）保存された状態キー。</summary>
    private async Task<List<string>> StoredAsync(string payload, CancellationToken cancellationToken)
    {
        var keys = _lab.Config.State.Keys.Values.Where(k => k.Source is SqlSource && k.Type == StateType.Text).ToList();
        var stored = new List<string>();
        var values = await new State.StateReader(_lab).ReadAsync(keys, null, "main", cancellationToken).ConfigureAwait(false);
        foreach (var value in values)
        {
            if (value.Values.Any(v => v.Value?.ToString() == payload))
            {
                stored.Add(value.Key);
            }
        }

        return stored;
    }

    private static string JsJsonString(string text) => JsJson.Stringify(JsonValue.Create(text));

    private static readonly string[] XssTemplates =
    [
        "<svg onload=window.__{m}=1>",
        "<img src=x onerror=window.__{m}=1>",
        "\"><img src=x onerror=\"(window.__custos_xss=window.__custos_xss||[]).push('{m}')\">",
        "<script>(window.__custos_xss=window.__custos_xss||[]).push('{m}')</script>",
        "javascript:window.__{m}=1",
    ];

    private static readonly string[] SqlFragments =
    [
        "' OR '1'='1",
        "'); DROP TABLE \"Room\"; --",
        "\" OR \"\"=\"",
        "x' UNION SELECT reconnectToken FROM Player --",
        "1; SELECT sqlite_version(); --",
    ];
}
