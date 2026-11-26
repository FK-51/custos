using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Util;
using Custos.SocketIO.Wire;
using Custos.Web.SocketIO.Lab;

namespace Custos.Web.SocketIO.Judges;

/// <summary>
/// 設定不要の汎用判定（設計書 第12.3節）：クラッシュ（コンテナの終了・再起動）とハング（応答しない）。
/// </summary>
public sealed class AppHealthJudge : IJudge
{
    /// <inheritdoc />
    public string Name => "app_health";

    /// <inheritdoc />
    public async Task<JudgeReport> JudgeAsync(JudgeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stopwatch = Stopwatch.StartNew();
        var findings = new List<Finding>();
        var lab = context.Lab;
        var state = await lab.ContainerStateAsync(Containers.App(lab.Project), cancellationToken).ConfigureAwait(false);
        var evidence = context.Write("app-state", state);
        var running = state["running"]?.GetValue<bool>() == true;
        var started = state["started_at"]?.GetValue<string>();
        if (!running)
        {
            findings.Add(Finding.Create(Name, "crash", Severity.Critical, 1, "server_down", $"アプリのコンテナが止まっています（終了コード {state["exit_code"]}）", null, [Report.Evidence(context, "コンテナの状態", evidence)], "container-exited"));
        }
        else if (context.Baseline.AppStartedAt != null && started != context.Baseline.AppStartedAt)
        {
            findings.Add(Finding.Create(Name, "restart", Severity.Critical, 1, "server_down", "判定の間に、アプリのコンテナが再起動しました（クラッシュの疑い）", null, [Report.Evidence(context, "コンテナの状態", evidence)], "container-restarted"));
        }

        if (running)
        {
            // ハング：応答までの時間（イベントループの遅れの、外からの近似）
            var ops = await lab.OpsAsync(cancellationToken).ConfigureAwait(false);
            var app = context.Config.Environment.App;
            var latencies = new JsonArray();
            var slow = 0;
            for (var i = 0; i < 5; i++)
            {
                var probe = Stopwatch.StartNew();
                var response = await ops.CallObjectAsync("http.request", new JsonObject { ["url"] = $"http://app:{app.ServerPort}{app.SocketIoPath}?EIO=4&transport=polling", ["timeout_ms"] = 5000 }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                var ms = probe.Elapsed.TotalMilliseconds;
                latencies.Add(Math.Round(ms, 1));
                if (response["ok"]?.GetValue<bool>() != true || ms > 2000)
                {
                    slow++;
                }
            }

            if (slow >= 3)
            {
                var path = context.Write("hang-probe", latencies);
                findings.Add(Finding.Create(Name, "hang", Severity.Critical, 1, "server_down", $"アプリが応答しない、または応答が遅い（5回中 {slow} 回が2秒超か失敗）", null, [Report.Evidence(context, "応答時間", path)], "hang"));
            }
        }

        return Report.Of(Name, findings, stopwatch);
    }
}

/// <summary>
/// 例外のログ（設計書 第12.3節）。基準より後のアプリのログから、処理されない例外とエラーの行を探す。
/// 処理されない例外（TypeError など）は機械判定（段階1）、それ以外のエラーの行は候補（段階2）。
/// </summary>
public sealed partial class LogJudge : IJudge
{
    [GeneratedRegex(@"(Unhandled|uncaught|UnhandledPromiseRejection|TypeError|ReferenceError|RangeError|SyntaxError|FATAL|Segmentation fault|ERR_[A-Z_]+|command exited \(\d+\))", RegexOptions.IgnoreCase)]
    private static partial Regex FatalPattern();

    [GeneratedRegex(@"(Unhandled|uncaught|UnhandledPromiseRejection|FATAL|Segmentation fault|command exited \(\d+\))", RegexOptions.IgnoreCase)]
    private static partial Regex CrashPattern();

    [GeneratedRegex(@"\[ERROR\]|\bError:", RegexOptions.None)]
    private static partial Regex ErrorPattern();

    /// <inheritdoc />
    public string Name => "logs";

    /// <inheritdoc />
    public async Task<JudgeReport> JudgeAsync(JudgeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stopwatch = Stopwatch.StartNew();
        var logs = await context.Lab.AppLogsAsync(context.Baseline.At.AddSeconds(-1), cancellationToken).ConfigureAwait(false);
        var findings = new List<Finding>();
        var lines = logs.Split('\n');
        var fatal = new List<string>();
        var handled = new List<string>();
        var errors = new List<string>();
        foreach (var line in lines)
        {
            var text = StripTimestamp(line);
            if (FatalPattern().IsMatch(text))
            {
                // アプリのロガーが出した [ERROR] の行で、落ちた印がないもの：アプリが捕まえた例外（入力の検証の漏れ）
                if (text.Contains("[ERROR]", StringComparison.Ordinal) && !CrashPattern().IsMatch(text))
                {
                    handled.Add(text);
                }
                else
                {
                    fatal.Add(text);
                }
            }
            else if (ErrorPattern().IsMatch(text))
            {
                errors.Add(text);
            }
        }

        if (fatal.Count > 0 || errors.Count > 0 || handled.Count > 0)
        {
            var path = context.Run.WriteText($"evidence/log-errors-{Guid.NewGuid().ToString("N")[..8]}.txt", string.Join("\n", fatal.Concat(handled).Concat(errors).Take(500)));
            foreach (var group in handled.GroupBy(Signature, StringComparer.Ordinal).Take(20))
            {
                findings.Add(Finding.Create(Name, "handled_exception", Severity.Medium, 1, "input_validation", $"アプリが捕まえた例外のログ（プロセスは落ちていない）：{Short(group.First())}（{group.Count()} 回）", null, [Report.Evidence(context, "ログ", path)], group.Key));
            }

            foreach (var group in fatal.GroupBy(Signature, StringComparer.Ordinal).Take(20))
            {
                findings.Add(Finding.Create(Name, "unhandled_exception", Severity.Critical, 1, "crash", $"処理されない例外のログ：{Short(group.First())}（{group.Count()} 回）", null, [Report.Evidence(context, "ログ", path)], group.Key));
            }

            foreach (var group in errors.GroupBy(Signature, StringComparer.Ordinal).Take(20))
            {
                findings.Add(Finding.Create(Name, "error_log", Severity.Medium, 2, "error_log", $"エラーのログ：{Short(group.First())}（{group.Count()} 回）", null, [Report.Evidence(context, "ログ", path)], group.Key));
            }
        }

        return Report.Of(Name, findings, stopwatch);
    }

    private static string StripTimestamp(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        return space > 20 && char.IsDigit(line[0]) ? line[(space + 1)..] : line;
    }

    /// <summary>まとめるための署名（数字、ID、時刻を消す）。</summary>
    private static string Signature(string line)
    {
        var text = Regex.Replace(line, @"\[\d{4}-\d{2}-\d{2}T[^\]]+\]", string.Empty);
        text = Regex.Replace(text, @"[0-9a-f]{8,}|room-[A-Za-z0-9_-]+|[A-Za-z0-9_-]{20}", "<id>");
        text = Regex.Replace(text, @"\d+", "<n>");
        return text.Trim();
    }

    private static string Short(string text) => text.Length <= 160 ? text.Trim() : text.Trim()[..160] + "…";
}

/// <summary>
/// 不変条件（設計書 第18.4節、第26.3節）。違反している行を返す SQL を実行し、1行でもあれば違反。
/// sql はアプリの DB（読み取り専用）、sent_sql は送信された状態、cross_sql はその両方（写しの突き合わせ。第25.3節）。
/// </summary>
public sealed class InvariantJudge : IJudge
{
    /// <inheritdoc />
    public string Name => "invariants";

    /// <inheritdoc />
    public async Task<JudgeReport> JudgeAsync(JudgeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var invariants = context.Config.Invariants?.Invariants ?? [];
        if (invariants.Count == 0)
        {
            return Report.Skip(Name, "invariants.yaml がない");
        }

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<Finding>();
        var needRecords = invariants.Any(i => i.Kind is InvariantKind.SentSql or InvariantKind.CrossSql);
        var needApp = invariants.Any(i => i.Kind is InvariantKind.Sql or InvariantKind.CrossSql);
        using var database = needRecords || needApp ? await RecordsDatabase.BuildAsync(context.Lab, 0, needApp, cancellationToken).ConfigureAwait(false) : null;
        var results = new JsonArray();
        foreach (var invariant in invariants)
        {
            if (invariant.Kind is InvariantKind.Delta or InvariantKind.Builtin || invariant.Query == null || database == null)
            {
                results.Add(new JsonObject { ["id"] = invariant.Id, ["skipped"] = invariant.Kind.ToString() });
                continue;
            }

            // 表名を修飾しなくても、SQLite は添付した app の表を探す（記録の表 sent・recv・acks・http と名前が重ならない限り）
            var sql = invariant.Query;
            try
            {
                var (columns, rows) = database.Query(sql);
                results.Add(new JsonObject { ["id"] = invariant.Id, ["violations"] = rows.Count });
                if (rows.Count > 0)
                {
                    var path = context.Write($"invariant-{invariant.Id}", new JsonObject
                    {
                        ["invariant"] = invariant.Id,
                        ["description"] = invariant.Description,
                        ["sql"] = invariant.Query,
                        ["columns"] = new JsonArray(columns.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                        ["rows"] = new JsonArray(rows.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
                    });
                    findings.Add(Finding.Create(Name, invariant.Id, invariant.Severity, invariant.Stage, invariant.Category ?? "invariant", $"不変条件 {invariant.Id} の違反（{rows.Count} 件）：{invariant.Description}", rows[0].ToJsonString(), [Report.Evidence(context, "違反した行", path)], invariant.Id));
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException e)
            {
                results.Add(new JsonObject { ["id"] = invariant.Id, ["error"] = e.Message });
                findings.Add(Finding.Create(Name, invariant.Id + ":error", Severity.High, 2, "judge_error", $"不変条件 {invariant.Id} の SQL を実行できません：{e.Message}", null, [], invariant.Id + ":error"));
            }
        }

        context.Write("invariants", results);
        return Report.Of(Name, findings, stopwatch);
    }
}

/// <summary>
/// 秘密の値の漏洩（設計書 第3.2節、第25.5節、第25.6節）。本人にだけ送ってよい値（再接続トークンなど）が、
/// 他の利用者への通信に現れたら漏洩とする。値は固有の文字列なので、一部やエンコードを変えたものも照合する。
/// 侵入のフラグ（サーバー上のファイル）が利用者側への通信に現れたら、侵入とする。
/// </summary>
public sealed class SecretLeakJudge : IJudge
{
    /// <inheritdoc />
    public string Name => "secret_leak";

    /// <inheritdoc />
    public async Task<JudgeReport> JudgeAsync(JudgeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stopwatch = Stopwatch.StartNew();
        var findings = new List<Finding>();
        var lab = context.Lab;
        var keys = context.Config.State.Keys.Values
            .Where(k => k.Source is SqlSource && context.Config.Authority.Resolve(k.Name).Visibility is Visibility.Owner or Visibility.None)
            .Where(k => k.Type is StateType.Text or StateType.Id)
            .ToList();

        // 秘密の値の正解は DB から読む（管理側だけが持つ）
        var reader = new State.StateReader(lab);
        var secrets = new List<(string Key, string Entity, string Value)>();
        foreach (var result in await reader.ReadAsync(keys, null, "main", cancellationToken).ConfigureAwait(false))
        {
            foreach (var value in result.Values)
            {
                if (value.Value is JsonValue v && v.TryGetValue<string>(out var text) && text.Length >= 12)
                {
                    secrets.Add((result.Key, value.Entity, text));
                }
            }
        }

        // 誰がどの秘密の持ち主か：持ち主（entity）の socket.id で送られた通信だけが許される
        var ownerSockets = await OwnerSocketsAsync(lab, cancellationToken).ConfigureAwait(false);
        var socketIds = lab.Records.SocketIds();
        var leaks = new JsonArray();
        foreach (var entry in lab.Records.Read(context.Baseline.RecordsSeq))
        {
            var text = entry.Kind == "eio" && entry.Dir == "s2c" ? entry.Raw ?? entry.Args?.ToJsonString() : null;
            if (text == null)
            {
                continue;
            }

            var recipient = entry.Sid != null && socketIds.TryGetValue(entry.Sid, out var socketId) ? socketId : entry.Sid;
            foreach (var (key, entity, value) in secrets)
            {
                if (!Contains(text, value))
                {
                    continue;
                }

                var owners = ownerSockets.TryGetValue(entity, out var list) ? list : [];
                var visibility = context.Config.Authority.Resolve(key).Visibility;
                var allowed = visibility == Visibility.Owner && recipient != null && owners.Contains(recipient);
                if (!allowed)
                {
                    leaks.Add(new JsonObject { ["key"] = key, ["entity"] = entity, ["recipient"] = recipient, ["seq"] = entry.Seq, ["event"] = entry.EventName ?? entry.Sio });
                    findings.Add(Finding.Create(Name, "secret_to_other", Severity.Critical, 1, "hidden_info", $"本人にだけ送ってよい値 {key} が、別の利用者への通信（seq {entry.Seq}、{entry.EventName ?? entry.Sio}）に現れました", null, [], $"{key}:{entry.EventName}"));
                }
            }
        }

        // 侵入のフラグ
        if (lab.SecretFlag() is { } flag)
        {
            foreach (var entry in lab.Records.Read(context.Baseline.RecordsSeq).Where(e => e.Dir == "s2c" || e.Kind == "http"))
            {
                var text = entry.Raw ?? entry.Args?.ToJsonString() ?? entry.ResponseHeaders ?? string.Empty;
                if (Contains(text, flag))
                {
                    findings.Add(Finding.Create(Name, "flag_read", Severity.Critical, 1, "server_intrusion", $"サーバー上のフラグファイルの中身が、利用者側への通信（seq {entry.Seq}）に現れました（侵入）", null, [], "flag"));
                }
            }
        }

        if (leaks.Count > 0)
        {
            var path = context.Write("secret-leaks", leaks);
            findings = findings.Select(f => f with { Evidence = [Report.Evidence(context, "漏洩の一覧", path)] }).ToList();
        }

        return Report.Of(Name, findings, stopwatch);
    }

    /// <summary>持ち主（state の entity）から、その人の socket.id（再接続で変わるので、記録の中のすべて）への対応。</summary>
    private static async Task<Dictionary<string, HashSet<string>>> OwnerSocketsAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var owner = lab.Config.Authority.Ownership.Values.FirstOrDefault(o => o.Entity == "player");
        if (owner == null || !lab.Config.State.Keys.TryGetValue(owner.OwnerKey, out var key))
        {
            return map;
        }

        var reader = new State.StateReader(lab);
        foreach (var value in (await reader.ReadAsync([key], null, "main", cancellationToken).ConfigureAwait(false))[0].Values)
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { value.Entity };
            if (value.Value is JsonValue v && v.TryGetValue<string>(out var socket))
            {
                set.Add(socket);
            }

            map[value.Entity] = set;
        }

        return map;
    }

    /// <summary>そのまま、一部（先頭16文字）、base64、URL エンコード、16進のどれかで含まれるか（第3.2節）。</summary>
    public static bool Contains(string haystack, string secret)
    {
        ArgumentNullException.ThrowIfNull(haystack);
        ArgumentNullException.ThrowIfNull(secret);
        if (haystack.Contains(secret, StringComparison.Ordinal))
        {
            return true;
        }

        if (secret.Length >= 32 && haystack.Contains(secret[..16], StringComparison.Ordinal))
        {
            return true;
        }

        var bytes = Encoding.UTF8.GetBytes(secret);
        return haystack.Contains(Convert.ToBase64String(bytes).TrimEnd('='), StringComparison.Ordinal)
            || haystack.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal)
            || (secret.All(char.IsAsciiLetterOrDigit) && haystack.Contains(Convert.ToHexStringLower(bytes), StringComparison.Ordinal));
    }
}

/// <summary>
/// 送ってよい項目の一覧（authority.yaml の sent）にない項目が、サーバーから送られていないか（新しい項目は漏洩の入口になりうる）。
/// 候補（段階2）として報告する。
/// </summary>
public sealed class SentSchemaJudge : IJudge
{
    private readonly Records.RecordStore? _store;

    /// <summary>作る。store を渡すと、その記録を調べる（適合テスト D の陽性の確認）。</summary>
    public SentSchemaJudge(Records.RecordStore? store = null)
    {
        _store = store;
    }

    /// <inheritdoc />
    public string Name => "sent_schema";

    /// <inheritdoc />
    public Task<JudgeReport> JudgeAsync(JudgeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var schemas = context.Config.Authority.Sent;
        if (schemas.Count == 0)
        {
            return Task.FromResult(Report.Skip(Name, "authority.yaml に sent の宣言がない"));
        }

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<Finding>();
        var unknownEvents = new HashSet<string>(StringComparer.Ordinal);
        var unknownPaths = new HashSet<string>(StringComparer.Ordinal);
        var store = _store ?? context.Lab.Records;
        foreach (var entry in store.Events(_store == null ? context.Baseline.RecordsSeq : 0, "s2c"))
        {
            var name = entry.EventName ?? string.Empty;
            if (!schemas.TryGetValue(name, out var schema))
            {
                unknownEvents.Add(name);
                continue;
            }

            if (entry.Args is not JsonArray args || args.Count == 0 || args[0] is not JsonObject payload)
            {
                continue;
            }

            // 入れ子までたどる：宣言にちょうどある位置なら、その下はすべて許す。宣言がその下を細かく書いていれば、中に入って調べる
            void Walk(JsonNode? node, string path)
            {
                if (schema.Paths.Contains(path, StringComparer.Ordinal))
                {
                    return;
                }

                if (node is JsonObject obj && schema.Paths.Any(p => p.StartsWith(path + ".", StringComparison.Ordinal)))
                {
                    foreach (var child in obj)
                    {
                        Walk(child.Value, path + "." + child.Key);
                    }

                    return;
                }

                if (node is JsonArray array && schema.Paths.Any(p => p.StartsWith(path + "[*]", StringComparison.Ordinal)))
                {
                    foreach (var item in array)
                    {
                        Walk(item, path + "[*]");
                    }

                    return;
                }

                unknownPaths.Add($"{name} {path}");
            }

            foreach (var pair in payload)
            {
                Walk(pair.Value, "$." + pair.Key);
            }
        }

        foreach (var name in unknownEvents)
        {
            findings.Add(Finding.Create(Name, "unknown_event", Severity.Medium, 2, "hidden_info", $"送ってよい一覧にないイベントが送られています：{name}", null, [], "event:" + name));
        }

        foreach (var path in unknownPaths)
        {
            findings.Add(Finding.Create(Name, "unknown_path", Severity.High, 2, "hidden_info", $"送ってよい一覧にない項目が送られています：{path}", null, [], "path:" + path));
        }

        return Task.FromResult(Report.Of(Name, findings, stopwatch));
    }
}

/// <summary>判定器をまとめて実行する。</summary>
public static class JudgeRunner
{
    /// <summary>段階1の判定器の一覧。</summary>
    public static IReadOnlyList<IJudge> Stage1() => [new AppHealthJudge(), new LogJudge(), new InvariantJudge(), new SecretLeakJudge(), new SentSchemaJudge()];

    /// <summary>すべて実行し、結果を run に書く。</summary>
    public static async Task<IReadOnlyList<JudgeReport>> RunAsync(JudgeContext context, IReadOnlyList<IJudge> judges, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(judges);
        var reports = new List<JudgeReport>();
        foreach (var judge in judges)
        {
            try
            {
                reports.Add(await judge.JudgeAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is CustosException or TimeoutException or InvalidOperationException or IOException)
            {
                reports.Add(new JudgeReport(judge.Name, false, "判定器が失敗しました：" + e.Message, [], 0));
            }
        }

        return reports;
    }

    /// <summary>結果を表にする。</summary>
    public static string Summarize(IReadOnlyList<JudgeReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var builder = new StringBuilder();
        foreach (var report in reports)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"[{report.Judge}] {(report.Ran ? $"{report.Findings.Count} 件" : "未実行：" + report.SkippedReason)}");
            foreach (var (finding, count) in Findings.Group(report.Findings))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - {finding.Severity} 段階{finding.Stage} {finding.Summary}{(count > 1 ? $"（×{count}）" : string.Empty)}");
            }
        }

        return builder.ToString();
    }
}
