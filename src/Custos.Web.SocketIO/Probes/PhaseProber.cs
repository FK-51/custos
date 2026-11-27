using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Records;
using Custos.Web.SocketIO.Scenarios;
using Custos.Web.SocketIO.State;

namespace Custos.Web.SocketIO.Probes;

/// <summary>1つの組み合わせ（局面 × イベント × 送り手）の結果。</summary>
public sealed record PhaseProbeCase(string Phase, string Event, string Actor, string User, bool Allowed, string Outcome, IReadOnlyList<string> Changes, IReadOnlyList<string> Broadcasts);

/// <summary>
/// 状態遷移と選択肢の検査（設計書 第25.4節、第26.7節。AIは使わない）。
/// - 局面ごとに、authority.yaml の phases で許していない「イベント × 送り手」をすべて送り、拒否されて状態が変わらないことを確かめる。
/// - 提示されていない選択肢を送り、拒否されて状態が変わらないことを確かめる。
/// 局面の準備は検証用リポジトリの scenarios/phases/&lt;局面&gt;.yaml。準備のシナリオは
/// <c>set: { actors: { 送り手の種類: 利用者 } }</c> で、送り手の種類ごとの利用者を決める。
/// 送る値の見本は scenarios/fuzz/templates.yaml（イベントごとの emit）。
/// </summary>
public sealed class PhaseProber
{
    private static readonly string[] ChoiceSetupFiles = ["choice.yaml", "playing.yaml"];
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public PhaseProber(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>実行する。返り値は見つけたもの。</summary>
    public async Task<IReadOnlyList<Finding>> RunAsync(CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var authority = _lab.Config.Authority;
        var templates = FuzzTemplates.Load(_lab.Workspace);
        var events = authority.Inputs.Values.Where(i => i.Kind == "event").Select(i => i.Target).Distinct(StringComparer.Ordinal).ToList();
        var cases = new List<PhaseProbeCase>();
        foreach (var phase in authority.Phases.Values)
        {
            var setupPath = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "phases", phase.Name + ".yaml");
            if (!File.Exists(setupPath))
            {
                _log($"局面 {phase.Name}：準備のシナリオ（scenarios/phases/{phase.Name}.yaml）がないため、検査しません");
                findings.Add(Finding.Create("phase_prober", "phase_not_prepared", Severity.Low, 1, "coverage", $"局面 {phase.Name} の準備のシナリオがないため、この局面の操作の検査をしていません", null, [], phase.Name));
                continue;
            }

            Dictionary<string, JsonNode?>? variables = null;
            Dictionary<string, string> actors = new(StringComparer.Ordinal);
            foreach (var name in events)
            {
                foreach (var actor in ActorsFor(phase, authority))
                {
                    var allowed = Allowed(phase, actor, name);
                    if (allowed)
                    {
                        cases.Add(new PhaseProbeCase(phase.Name, name, actor, "-", true, "allowed", [], []));
                        continue;
                    }

                    if (variables == null)
                    {
                        (variables, actors) = await PrepareAsync(setupPath, cancellationToken).ConfigureAwait(false);
                        await RefreshSocketsAsync(actors.Values.Distinct(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
                    }

                    if (!actors.TryGetValue(actor, out var user))
                    {
                        cases.Add(new PhaseProbeCase(phase.Name, name, actor, "-", false, "no_user", [], []));
                        continue;
                    }

                    var result = await ProbeAsync(phase.Name, name, actor, user, templates, variables, actors, cancellationToken).ConfigureAwait(false);
                    cases.Add(result);
                    _run.AppendJsonLine("phase-cases.jsonl", result);
                    if (result.Outcome is "changed" or "crashed")
                    {
                        var summary = result.Outcome == "crashed"
                            ? $"局面 {phase.Name} で {actor}（{user}）が許されていない {name} を送ると、アプリが落ちました"
                            : $"局面 {phase.Name} で {actor} が許されていない {name} を送ると、状態が変わりました（{string.Join("、", result.Changes.Concat(result.Broadcasts).Take(4))}）";
                        findings.Add(Finding.Create("phase_prober", result.Outcome == "crashed" ? "crash_on_forbidden_input" : "forbidden_input_accepted", result.Outcome == "crashed" ? Severity.Critical : Severity.High, 1, "rule_break", summary, Json.SerializeCompact(result), [new Evidence("組み合わせの結果", "phase-cases.jsonl")], $"{phase.Name}:{name}:{actor}"));
                        variables = null;
                    }
                }
            }
        }

        // 提示されていない選択肢（第25.4節）
        findings.AddRange(await OutOfChoicesAsync(cancellationToken).ConfigureAwait(false));
        var table = new JsonArray(cases.Select(c => (JsonNode?)JsonNode.Parse(Json.SerializeCompact(c))).ToArray());
        // 同じ実行の中で何度も呼ばれる（校正の探索ごと）ので、名前がぶつかったら番号を付ける
        _run.WriteJson(_run.FreeName("phase-matrix.json"), table);
        var tested = cases.Count(c => !c.Allowed && c.Outcome is not "no_user");
        _log($"局面 × イベント × 送り手：{cases.Count} 通り（許されていない組み合わせを {tested} 通り試した）");
        return findings;
    }

    private static IEnumerable<string> ActorsFor(PhaseEntry phase, AuthorityConfig authority)
    {
        var actors = new HashSet<string>(phase.Allowed.Keys.Where(a => a != "anyone"), StringComparer.Ordinal);
        foreach (var actor in authority.Actors)
        {
            actors.Add(actor);
        }

        return actors.Order(StringComparer.Ordinal);
    }

    private static bool Allowed(PhaseEntry phase, string actor, string eventName) =>
        (phase.Allowed.TryGetValue("anyone", out var anyone) && anyone.Contains(eventName))
        || (phase.Allowed.TryGetValue(actor, out var list) && list.Contains(eventName));

    private async Task<(Dictionary<string, JsonNode?> Variables, Dictionary<string, string> Actors)> PrepareAsync(string setupPath, CancellationToken cancellationToken)
    {
        await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var runner = new ScenarioRunner(_lab, _run, _log);
        var result = await runner.RunAsync(Scenario.Load(setupPath), cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            throw new CustosException($"局面の準備のシナリオが失敗しました（{Path.GetFileName(setupPath)}）：{result.Error}");
        }

        await _lab.SettleAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var actors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (runner.Variables.TryGetValue("actors", out var node) && node is JsonObject map)
        {
            foreach (var pair in map)
            {
                if (pair.Value is JsonValue v && v.TryGetValue<string>(out var user))
                {
                    actors[pair.Key] = user;
                }
            }
        }

        return (runner.Variables.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.Ordinal), actors);
    }

    private async Task<PhaseProbeCase> ProbeAsync(string phase, string eventName, string actor, string user, Dictionary<string, JsonArray> templates, Dictionary<string, JsonNode?> variables, Dictionary<string, string> actors, CancellationToken cancellationToken)
    {
        var before = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var cursor = _lab.Records.Activity().LastSeq;
        var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
        var args = templates.TryGetValue(eventName, out var template) ? (JsonArray)template.DeepClone() : new JsonArray(new JsonObject { ["roomId"] = "{room}" });
        // 送り手ごとの値：{me} は送り手の利用者、{me_*} は準備で覚えた「<利用者>_*」（例：{me_token} は送り手自身のトークン）
        var mine = new Dictionary<string, JsonNode?>(variables, StringComparer.Ordinal) { ["me"] = JsonValue.Create(user) };
        foreach (var pair in variables.Where(p => p.Key.StartsWith(user + "_", StringComparison.Ordinal)))
        {
            mine["me_" + pair.Key[(user.Length + 1)..]] = pair.Value?.DeepClone();
        }

        var filled = Scenario.Substitute(args, mine) as JsonArray ?? [];
        var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.CallAsync("user.emit", new JsonObject { ["user"] = user, ["event"] = eventName, ["args"] = filled, ["ack"] = false }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (CustosException e)
        {
            return new PhaseProbeCase(phase, eventName, actor, user, false, "send_failed:" + e.Message, [], []);
        }

        await _lab.SettleAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var health = await new AppHealthJudge().JudgeAsync(new JudgeContext(_lab, _run, baseline), cancellationToken).ConfigureAwait(false);
        if (health.Findings.Any(f => f.Category == "server_down"))
        {
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            return new PhaseProbeCase(phase, eventName, actor, user, false, "crashed", [], []);
        }

        var after = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var changes = Diff(before, after);
        var broadcasts = Broadcasts(cursor, user, actors);
        return new PhaseProbeCase(phase, eventName, actor, user, false, changes.Count > 0 || broadcasts.Count > 0 ? "changed" : "rejected", changes, broadcasts);
    }

    /// <summary>状態（SQL のキーと、送信された状態のキー）を読む。揮発するキーは除く。</summary>
    private async Task<Dictionary<string, string>> SnapshotAsync(CancellationToken cancellationToken)
    {
        var volatileKeys = _lab.Config.State.Determinism.Volatile.ToHashSet(StringComparer.Ordinal);
        var keys = _lab.Config.State.Keys.Values.Where(k => k.Source is SqlSource or SocketSource && !volatileKeys.Contains(k.Name)).ToList();
        var results = await new StateReader(_lab).ReadAsync(keys, null, "main", cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            foreach (var value in result.Values)
            {
                map[$"{result.Key}[{value.Entity}]"] = value.Value?.ToJsonString() ?? "null";
            }
        }

        return map;
    }

    private static List<string> Diff(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var changes = new List<string>();
        foreach (var pair in after)
        {
            if (!before.TryGetValue(pair.Key, out var old))
            {
                changes.Add($"{pair.Key} が増えた（{Short(pair.Value)}）");
            }
            else if (old != pair.Value)
            {
                changes.Add($"{pair.Key}：{Short(old)} → {Short(pair.Value)}");
            }
        }

        changes.AddRange(before.Keys.Where(k => !after.ContainsKey(k)).Select(k => $"{k} が消えた"));
        return changes;
    }

    /// <summary>送り手以外の利用者に、状態を伝えるイベントが送られたか（authority.yaml の sent にあるイベント）。</summary>
    private List<string> Broadcasts(long cursor, string sender, Dictionary<string, string> actors)
    {
        var stateEvents = _lab.Config.Authority.Sent.Keys.ToHashSet(StringComparer.Ordinal);
        var socketIds = _lab.Records.SocketIds();
        var senderSockets = SocketsOf(sender);
        var list = new List<string>();
        foreach (var entry in _lab.Records.Read(cursor).Where(e => e.Kind == "eio" && e.Dir == "s2c" && e.Sio == "event" && e.EventName != null && stateEvents.Contains(e.EventName)))
        {
            var recipient = entry.Sid != null && socketIds.TryGetValue(entry.Sid, out var id) ? id : entry.Sid;
            if (recipient != null && senderSockets.Contains(recipient))
            {
                continue;
            }

            var who = actors.FirstOrDefault(a => SocketsOf(a.Value).Contains(recipient ?? string.Empty)).Value ?? recipient;
            list.Add($"{entry.EventName} が {who} に届いた");
        }

        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private HashSet<string> SocketsOf(string user)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (_userSockets.TryGetValue(user, out var known))
        {
            set.UnionWith(known);
        }

        return set;
    }

    private readonly Dictionary<string, HashSet<string>> _userSockets = new(StringComparer.Ordinal);

    /// <summary>利用者の socket.id を覚える（クライアントに問い合わせる）。</summary>
    private async Task RefreshSocketsAsync(IEnumerable<string> users, CancellationToken cancellationToken)
    {
        var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        foreach (var user in users)
        {
            try
            {
                var info = await client.CallObjectAsync("user.info", new JsonObject { ["user"] = user }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                if (info["socket_id"]?.GetValue<string>() is { } socketId)
                {
                    if (!_userSockets.TryGetValue(user, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        _userSockets[user] = set;
                    }

                    set.Add(socketId);
                }
            }
            catch (CustosException)
            {
            }
        }
    }

    /// <summary>
    /// 提示されていない選択肢を送る（第25.4節）。準備は scenarios/phases/choice.yaml（なければ playing.yaml）。
    /// 選択を求められている利用者が、選択肢にない値を返し、状態が変わらないことを確かめる。
    /// </summary>
    private async Task<IReadOnlyList<Finding>> OutOfChoicesAsync(CancellationToken cancellationToken)
    {
        var findings = new List<Finding>();
        var dir = Path.Combine(_lab.Workspace.VerifyRepo, "scenarios", "phases");
        var setup = ChoiceSetupFiles.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
        if (setup == null)
        {
            return findings;
        }

        var protocol = BotProtocolLoader.Load(_lab.Workspace);
        var choiceEvent = protocol["choice_event"]?.GetValue<string>() ?? string.Empty;
        var choicesPath = protocol["choices_path"]?.GetValue<string>() ?? "$.choices";
        var kindPath = protocol["kind_path"]?.GetValue<string>();
        var objectField = protocol["object_choice_field"]?.GetValue<string>();
        var replyEvent = protocol["reply_event"]?.GetValue<string>() ?? string.Empty;
        var replyTemplate = protocol["reply_template"]?.DeepClone();
        var attempts = new JsonArray();
        var attemptsFile = _run.FreeName("phase-unoffered.json");
        for (var round = 0; round < 3; round++)
        {
            var (variables, actors) = await PrepareAsync(setup, cancellationToken).ConfigureAwait(false);
            await RefreshSocketsAsync(actors.Values.Distinct(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            var client = await _lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);

            // 選択を求められている利用者を探す（最後に届いたイベントが選択のイベントの人）
            string? chooser = null;
            JsonNode? payload = null;
            foreach (var user in actors.Values.Distinct(StringComparer.Ordinal))
            {
                var events = await client.CallObjectAsync("user.events", new JsonObject { ["user"] = user, ["since"] = 0 }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                var last = (events["events"] as JsonArray ?? []).OfType<JsonObject>().LastOrDefault(e => e["event"]?.GetValue<string>() is { } n && n != "ping");
                if (last?["event"]?.GetValue<string>() == choiceEvent)
                {
                    chooser = user;
                    payload = (last["args"] as JsonArray)?.FirstOrDefault();
                }
            }

            if (chooser == null || payload == null)
            {
                attempts.Add(new JsonObject { ["round"] = round, ["outcome"] = "no_chooser" });
                continue;
            }

            var selectedChoices = Core.Util.Json.SelectPath(payload, choicesPath);
            var choices = (selectedChoices.Count > 0 ? selectedChoices[0] : null) as JsonArray ?? [];
            var selectedKind = kindPath == null ? [] : Core.Util.Json.SelectPath(payload, kindPath);
            var kind = selectedKind.Count > 0 ? selectedKind[0]?.ToString() : null;
            var offered = choices.Select(c => c is JsonObject o && objectField != null ? o[objectField]?.ToJsonString() : c?.ToJsonString()).ToHashSet(StringComparer.Ordinal);
            var invalid = InvalidChoices(choices, objectField).Where(v => !offered.Contains(v?.ToJsonString())).Skip(round).FirstOrDefault();
            var reply = Fill(replyTemplate, invalid, variables);
            var before = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
            var cursor = _lab.Records.Activity().LastSeq;
            await client.CallAsync("user.emit", new JsonObject { ["user"] = chooser, ["event"] = replyEvent, ["args"] = new JsonArray(reply), ["ack"] = false }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            await _lab.SettleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var after = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
            var changes = Diff(before, after);
            var broadcasts = Broadcasts(cursor, chooser, actors);
            var outcome = changes.Count > 0 || broadcasts.Count > 0 ? "changed" : "rejected";
            attempts.Add(new JsonObject { ["round"] = round, ["kind"] = kind, ["offered"] = choices.DeepClone(), ["sent"] = reply?.DeepClone(), ["outcome"] = outcome, ["changes"] = new JsonArray(changes.Concat(broadcasts).Take(10).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()) });
            if (outcome == "changed")
            {
                findings.Add(Finding.Create("phase_prober", "unoffered_choice_accepted", Severity.High, 1, "rule_break", $"提示されていない選択肢（{invalid?.ToJsonString()}、提示は {choices.ToJsonString()}、種類 {kind}）を送ると、拒否されずに状態が変わりました（{string.Join("、", changes.Concat(broadcasts).Take(3))}）", null, [new Evidence("試した記録", attemptsFile)], $"unoffered:{kind}"));
            }
        }

        _run.WriteJson(attemptsFile, attempts);
        return Findings.Group(findings).Select(g => g.Finding).ToList();
    }

    /// <summary>提示された選択肢にない値の候補（同じ型で、範囲の外と中の両方）。</summary>
    private static IEnumerable<JsonNode?> InvalidChoices(JsonArray choices, string? objectField)
    {
        var sample = choices.FirstOrDefault();
        var value = sample is JsonObject o && objectField != null ? o[objectField] : sample;
        if (value is JsonValue v && v.TryGetValue<double>(out _))
        {
            foreach (var n in new[] { 0, 7, 9, 10, 99, -1 }.Concat(Enumerable.Range(1, 12)))
            {
                yield return JsonValue.Create(n);
            }
        }
        else
        {
            yield return JsonValue.Create("custos-not-offered");
            yield return JsonValue.Create(string.Empty);
        }
    }

    private static JsonNode? Fill(JsonNode? template, JsonNode? choice, IReadOnlyDictionary<string, JsonNode?> variables)
    {
        var withChoice = new Dictionary<string, JsonNode?>(variables, StringComparer.Ordinal) { ["choice"] = choice?.DeepClone() };
        return Scenario.Substitute(template, withChoice);
    }

    private static string Short(string text) => text.Length > 60 ? text[..60] + "…" : text;

    /// <summary>文字列の数（ログ用）。</summary>
    public static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>イベントごとの値の見本（scenarios/fuzz/templates.yaml の emit）。</summary>
public static class FuzzTemplates
{
    /// <summary>読む。</summary>
    public static Dictionary<string, JsonArray> Load(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var path = Path.Combine(workspace.VerifyRepo, "scenarios", "fuzz", "templates.yaml");
        var map = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (var step in Scenario.Load(path).Steps.Where(s => s.Kind == "emit"))
        {
            if (step.Args["event"]?.GetValue<string>() is { } name)
            {
                map[name] = step.Args["args"] as JsonArray ?? [];
            }
        }

        return map;
    }
}
