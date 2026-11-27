using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Scenarios;
using Custos.Web.SocketIO.State;

namespace Custos.Web.SocketIO.Determinism;

/// <summary>決定性の測定の結果（設計書 第22.7節の等級）。</summary>
public sealed record DeterminismResult(
    int Runs,
    string Grade,
    IReadOnlyList<string> DifferingKeys,
    int VolatileCount,
    IReadOnlyList<string> RawHashes,
    IReadOnlyList<string> NormalizedHashes,
    JsonArray Details)
{
    /// <summary>等級の表示名。</summary>
    public string GradeLabel => Grade switch
    {
        "exact" => "完全一致",
        "normalized" => "正規化後に一致",
        _ => "統計的",
    };

    /// <summary>JSON にする。</summary>
    public JsonObject ToJson() => new()
    {
        ["runs"] = Runs,
        ["grade"] = Grade,
        ["grade_label"] = GradeLabel,
        ["differing_keys"] = new JsonArray(DifferingKeys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
        ["volatile_count"] = VolatileCount,
        ["raw_hashes"] = new JsonArray(RawHashes.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
        ["normalized_hashes"] = new JsonArray(NormalizedHashes.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
        ["details"] = Details.DeepClone(),
    };
}

/// <summary>
/// 決定性の測定（設計書 第14.8節「同じスナップショット、同じシード、同じ入力列なら、同じ時点での状態のハッシュが一致する」、第22章）。
/// 同じスナップショットから同じシナリオを runs 回実行し、状態のハッシュを比べる。
/// 比べ方は state.yaml の determinism（除外する項目、ID の振り直し、並べ替え）に従う。
/// </summary>
public sealed class DeterminismRunner
{
    private static int _invocations;
    private readonly Lab.Lab _lab;
    private readonly Run _run;

    /// <summary>作る。</summary>
    public DeterminismRunner(Lab.Lab lab, Run run)
    {
        _lab = lab;
        _run = run;
    }

    /// <summary>測る。scenarioPath を省くと、検証用リポジトリの scenarios/determinism.yaml を使う（相対パスは検証用リポジトリから）。</summary>
    public async Task<DeterminismResult> MeasureAsync(int runs, CancellationToken cancellationToken, string? scenarioPath = null)
    {
        scenarioPath = Path.GetFullPath(scenarioPath ?? Path.Combine("scenarios", "determinism.yaml"), _lab.Workspace.VerifyRepo);
        var scenario = File.Exists(scenarioPath) ? Scenario.Load(scenarioPath) : throw new CustosException($"シナリオがありません：{scenarioPath}");
        var determinism = _lab.Config.State.Determinism;
        var states = new List<SortedDictionary<string, JsonNode?>>();
        var orders = new List<IReadOnlyList<string>>();
        var details = new JsonArray();
        for (var i = 0; i < runs; i++)
        {
            var once = await RunOnceAsync(scenario, 1, cancellationToken, new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["run"] = JsonValue.Create("det") }).ConfigureAwait(false);
            states.Add(once.State);
            orders.Add(once.ConnectionOrder);
            details.Add(new JsonObject { ["run"] = i + 1, ["scenario_ok"] = once.Result.Ok, ["error"] = once.Result.Error });
        }

        var raw = states.Select(s => Hashing.Sha256Hex(Json.Canonical(ToNode(s)))[..16]).ToList();
        var normalizedStates = states.Select((s, i) => Normalize(s, determinism, orders[i])).ToList();

        // 同じ実行の中で何度測っても（B-4 と B-5 など）、証拠のファイルがぶつからないようにする（結果は上書きしない決まり）
        var prefix = "determinism/" + Interlocked.Increment(ref _invocations).ToString("D2", CultureInfo.InvariantCulture) + "-";
        for (var i = 0; i < normalizedStates.Count; i++)
        {
            // 証拠：正規化した後の状態（どのキーの値がどう違うかを後から確かめられるように）
            _run.WriteJson($"{prefix}run-{(i + 1).ToString(CultureInfo.InvariantCulture)}.json", new JsonObject { ["connection_order"] = new JsonArray(orders[i].Select(o => (JsonNode?)JsonValue.Create(o)).ToArray()), ["normalized"] = ToNode(normalizedStates[i]) });
        }

        var normalized = normalizedStates.Select(s => Hashing.Sha256Hex(Json.Canonical(ToNode(s)))[..16]).ToList();
        var differing = new List<string>();
        foreach (var key in normalizedStates.SelectMany(s => s.Keys).Distinct(StringComparer.Ordinal))
        {
            var texts = normalizedStates.Select(s => Json.Canonical(s.GetValueOrDefault(key))).Distinct(StringComparer.Ordinal).Count();
            if (texts > 1)
            {
                differing.Add(key);
            }
        }

        var grade = raw.Distinct(StringComparer.Ordinal).Count() == 1 ? "exact" : normalized.Distinct(StringComparer.Ordinal).Count() == 1 ? "normalized" : "statistical";
        return new DeterminismResult(runs, grade, differing, determinism.Volatile.Count, raw, normalized, details);
    }

    /// <summary>
    /// 1回分を実行して状態を読む：初期のスナップショットに戻し、シードと時計を固定し（テスト用APIがあれば）、シナリオを実行し、
    /// 完了を待って状態を読む。socket の値は、この回の記録だけを使う（前の回の通信を混ぜない）。
    /// 返り値の State は正規化する前の値（Normalize に ConnectionOrder と一緒に渡す）。
    /// 決定性の測定と、校正用バグの品質チェック（確認用シナリオのパッチあり・なしの比較）で同じ読み方をする。
    /// </summary>
    public async Task<(SortedDictionary<string, JsonNode?> State, IReadOnlyList<string> ConnectionOrder, ScenarioResult Result, long StartSeq)> RunOnceAsync(Scenario scenario, long seed, CancellationToken cancellationToken, IReadOnlyDictionary<string, JsonNode?>? variables = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var keys = _lab.Config.State.Keys.Values.Where(k => k.Source is SqlSource or SocketSource).ToList();
        await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
        var startSeq = _lab.Records.Activity().LastSeq;
        if (_lab.Config.App.TestApi == TestApiMode.Http)
        {
            await TestApiAsync("/__verify/seed", "{\"seed\":" + seed.ToString(CultureInfo.InvariantCulture) + "}", cancellationToken).ConfigureAwait(false);
            await TestApiAsync("/__verify/clock", "{\"action\":\"set\",\"epoch_ms\":" + FixedClockEpochMs.ToString(CultureInfo.InvariantCulture) + "}", cancellationToken).ConfigureAwait(false);
        }

        var runner = new ScenarioRunner(_lab, _run, _ => { });
        foreach (var pair in variables ?? new Dictionary<string, JsonNode?>(StringComparer.Ordinal))
        {
            runner.Variables[pair.Key] = pair.Value?.DeepClone();
        }

        var result = await runner.RunAsync(scenario, cancellationToken).ConfigureAwait(false);
        await _lab.SettleAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var values = await new StateReader(_lab).ReadAsync(keys, null, "main", cancellationToken).ConfigureAwait(false);
        var state = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var list = value.Source == "socket"
                ? SocketSince((SocketSource)keys.First(k => k.Name == value.Key).Source, startSeq)
                : value.Values.Select(v => (v.Entity, v.Value)).ToList();
            state[value.Key] = new JsonArray(list.Select(v => (JsonNode?)new JsonArray(JsonValue.Create(v.Entity), v.Value?.DeepClone())).ToArray());
        }

        return (state, ConnectionOrder(_lab, startSeq), result, startSeq);
    }

    /// <summary>1回分の実行で時計を合わせる時刻（2026-01-01T00:00:00Z）。時計は止めずに、ここから進む。</summary>
    public const long FixedClockEpochMs = 1767225600000;

    /// <summary>
    /// 送信された状態（サーバーからクライアントへのイベント）を、比べられる形にする（第19.11節の V2「送信された状態が変わる」）。
    /// 接続ごとに分け（接続は CONNECT の応答の順の番号）、接続の中では届いた順に並べる。値の中の ID は、接続の順の socket.id を先に、
    /// その後は出てきた順に番号を振り直す。接続をまたいだ届く順のぶれは、比べる対象にしない。
    /// 時計は合わせた時刻から進み続けるので、その近く（前1日〜後30日）の時刻（エポックからのミリ秒、ISO 8601 の文字列）は「&lt;time&gt;」にする
    /// （実際に食い違ったのは、手番の期限 deadline だった）。
    /// </summary>
    public static JsonObject SentNode(IEnumerable<Custos.SocketIO.Wire.RecordEntry> entries, IReadOnlyList<string> connectionOrder)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(connectionOrder);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var known in connectionOrder)
        {
            ids.TryAdd(known, "#" + (ids.Count + 1).ToString(CultureInfo.InvariantCulture));
        }

        var list = entries.OrderBy(e => e.Seq).ToList();

        // Engine.IO のセッション → socket.id（CONNECT の応答）
        var socketOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in list.Where(e => e.Kind == "eio" && e.Dir == "s2c" && e.Sio == "connect" && e.Sid != null))
        {
            var payload = entry.Args is JsonArray array && array.Count > 0 ? array[0] : entry.Args;
            if (payload?["sid"]?.GetValue<string>() is { } socketId && entry.Sid is { } sid)
            {
                socketOf.TryAdd(sid, socketId);
            }
        }

        const long Day = 24L * 60 * 60 * 1000;
        static bool NearClock(long ms) => ms >= FixedClockEpochMs - Day && ms <= FixedClockEpochMs + (30 * Day);
        static bool IsTime(JsonValue value)
        {
            if (value.TryGetValue<long>(out var ms))
            {
                return NearClock(ms);
            }

            if (value.TryGetValue<double>(out var d))
            {
                return d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue && NearClock((long)d);
            }

            return value.TryGetValue<string>(out var text) && text.Length >= 20 && text.Contains('T', StringComparison.Ordinal)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && NearClock(at.ToUnixTimeMilliseconds());
        }

        JsonNode? Walk(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Walk(p.Value)))),
            JsonArray array => new JsonArray(array.Select(Walk).ToArray()),
            JsonValue value when IsTime(value) => JsonValue.Create("<time>"),
            JsonValue value when value.TryGetValue<string>(out var text) && (ids.ContainsKey(text) || LooksLikeId(text)) => JsonValue.Create(Id(text)),
            _ => node?.DeepClone(),
        };

        string Id(string text)
        {
            if (!ids.TryGetValue(text, out var mapped))
            {
                mapped = "#" + (ids.Count + 1).ToString(CultureInfo.InvariantCulture);
                ids[text] = mapped;
            }

            return mapped;
        }

        var bySocket = new SortedDictionary<string, JsonArray>(StringComparer.Ordinal);
        foreach (var entry in list.Where(e => e.Dir == "s2c" && e.EventName != null))
        {
            var socket = entry.Sid != null && socketOf.TryGetValue(entry.Sid, out var socketId) && ids.TryGetValue(socketId, out var mapped) ? mapped : "?";
            if (!bySocket.TryGetValue(socket, out var events))
            {
                events = [];
                bySocket[socket] = events;
            }

            events.Add(new JsonArray(JsonValue.Create(entry.EventName), Walk(entry.Args)));
        }

        return new JsonObject(bySocket.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value)));
    }

    /// <summary>状態を JSON のオブジェクトにする（証拠のファイル用）。</summary>
    public static JsonObject StateNode(SortedDictionary<string, JsonNode?> state) => ToNode(state);

    /// <summary>記録の since より後に接続した順の socket.id（CONNECT の応答の順）。ID の正規化に使う。</summary>
    public static List<string> ConnectionOrder(Lab.Lab lab, long since)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var order = new List<string>();
        foreach (var entry in lab.Records.Read(since).Where(e => e.Kind == "eio" && e.Dir == "s2c" && e.Sio == "connect"))
        {
            var payload = entry.Args is JsonArray array && array.Count > 0 ? array[0] : entry.Args;
            if (payload?["sid"]?.GetValue<string>() is { } socketId && !order.Contains(socketId, StringComparer.Ordinal))
            {
                order.Add(socketId);
            }
        }

        return order;
    }

    private List<(string Entity, JsonNode? Value)> SocketSince(SocketSource source, long since) =>
        _lab.Records.LatestBySocket(source.Events, source.Path, since).Select(p => (p.Key, p.Value)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

    private static JsonObject ToNode(SortedDictionary<string, JsonNode?> state)
    {
        var obj = new JsonObject();
        foreach (var pair in state)
        {
            obj[pair.Key] = pair.Value?.DeepClone();
        }

        return obj;
    }

    /// <summary>
    /// 正規化：除外する項目を外し、ID を作成順の番号に振り直し（ids: creation_order）、順序に意味のない集合を並べ替える（第18.2節）。
    /// </summary>
    public static SortedDictionary<string, JsonNode?> Normalize(SortedDictionary<string, JsonNode?> state, DeterminismConfig config, IReadOnlyList<string>? creationOrder = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(config);
        var result = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);

        // 作られた順が分かる ID（接続の順の socket.id など）は、その順で先に番号を振る
        foreach (var known in creationOrder ?? [])
        {
            if (config.IdNormalization == "creation_order" && !ids.ContainsKey(known))
            {
                ids[known] = "#" + (ids.Count + 1).ToString(CultureInfo.InvariantCulture);
            }
        }

        string Id(string value)
        {
            if (config.IdNormalization != "creation_order")
            {
                return value;
            }

            if (!ids.TryGetValue(value, out var mapped))
            {
                mapped = "#" + (ids.Count + 1).ToString(CultureInfo.InvariantCulture);
                ids[value] = mapped;
            }

            return mapped;
        }

        // 接続の順から分かった ID（と、すでに番号を振った値）は、見た目に関係なく ID として扱う。
        // socket.id は数字を1文字も含まないことがあり（16 文字の乱数の部分で約 7%）、LooksLikeId だけでは取りこぼす
        bool IsId(string text) => ids.ContainsKey(text) || LooksLikeId(text);

        foreach (var pair in state)
        {
            if (config.Volatile.Contains(pair.Key, StringComparer.Ordinal))
            {
                continue;
            }

            var rows = new List<JsonArray>();
            foreach (var row in (pair.Value as JsonArray ?? []).OfType<JsonArray>())
            {
                var entity = row[0]?.GetValue<string>() ?? string.Empty;
                var value = row[1]?.DeepClone();
                if (value is JsonValue v && v.TryGetValue<string>(out var text) && IsId(text))
                {
                    value = JsonValue.Create(Id(text));
                }

                if (config.Sort.Contains(pair.Key, StringComparer.Ordinal) && value is JsonArray array)
                {
                    value = new JsonArray(array.Select(n => n?.DeepClone()).OrderBy(n => n?.ToJsonString(), StringComparer.Ordinal).ToArray());
                }

                rows.Add(new JsonArray(JsonValue.Create(IsId(entity) ? Id(entity) : entity), value));
            }

            result[pair.Key] = new JsonArray(rows.OrderBy(r => r.ToJsonString(), StringComparer.Ordinal).Select(r => (JsonNode?)r).ToArray());
        }

        return result;
    }

    /// <summary>自動採番の ID らしい文字列か（cuid、socket.id、部屋の ID、UUID）。</summary>
    private static bool LooksLikeId(string text) =>
        text.Length >= 12 && text.Length <= 64 && !text.Contains(' ', StringComparison.Ordinal) && text.Any(char.IsDigit) && text.Any(char.IsLetter);

    private async Task TestApiAsync(string path, string body, CancellationToken cancellationToken)
    {
        var ops = await _lab.OpsAsync(cancellationToken).ConfigureAwait(false);
        await ops.CallAsync("http.request", new JsonObject
        {
            ["url"] = $"http://app:{_lab.Config.App.TestApiPort.ToString(CultureInfo.InvariantCulture)}{path}",
            ["method"] = "POST",
            ["body"] = body,
        }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }
}
