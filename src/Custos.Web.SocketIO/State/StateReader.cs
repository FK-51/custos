using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Util;
using Custos.Web.SocketIO.Docker;

namespace Custos.Web.SocketIO.State;

/// <summary>状態の1つの値（対象ごと）。</summary>
public sealed record StateValue(string Entity, JsonNode? Value, bool TypeOk, string? TypeError);

/// <summary>1つのキーの読み取り結果。</summary>
public sealed record StateReadResult(string Key, string Source, bool Ok, string? Error, IReadOnlyList<StateValue> Values, DateTimeOffset ObservedAt)
{
    /// <summary>型が合わない値。</summary>
    public IReadOnlyList<StateValue> TypeErrors => Values.Where(v => !v.TypeOk).ToList();
}

/// <summary>
/// state.yaml の定義どおりに状態を読む（指示書 タスク7、設計書 第18.2節、第25.3節）。
/// - sql：ops の中で、読み取り専用で開いた SQLite に対して実行する。複数のキーは1つの読み取りトランザクションで読む。
/// - socket：通信の記録から、利用者ごとに最後に届いた値を読む。
/// - dom：ブラウザの画面から読む。
/// 観測には時刻を付ける（第14.8節）。
/// </summary>
public sealed class StateReader
{
    private readonly Lab.Lab _lab;

    /// <summary>作る。</summary>
    public StateReader(Lab.Lab lab)
    {
        _lab = lab;
    }

    /// <summary>すべてのキーを読む。</summary>
    public async Task<IReadOnlyList<StateReadResult>> ReadAllAsync(AgentSession? browser, string page, CancellationToken cancellationToken)
    {
        var keys = _lab.Config.State.Keys.Values.ToList();
        return await ReadAsync(keys, browser, page, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>指定したキーを読む。</summary>
    public async Task<IReadOnlyList<StateReadResult>> ReadAsync(IReadOnlyList<StateKey> keys, AgentSession? browser, string page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var results = new List<StateReadResult>();
        var sqlKeys = keys.Where(k => k.Source is SqlSource).ToList();
        if (sqlKeys.Count > 0)
        {
            results.AddRange(await ReadSqlAsync(sqlKeys, cancellationToken).ConfigureAwait(false));
        }

        foreach (var key in keys.Where(k => k.Source is SocketSource))
        {
            results.Add(ReadSocket(key));
        }

        foreach (var key in keys.Where(k => k.Source is DomSource))
        {
            results.Add(await ReadDomAsync(key, browser, page, cancellationToken).ConfigureAwait(false));
        }

        foreach (var key in keys.Where(k => k.Source is AdapterSource))
        {
            results.Add(new StateReadResult(key.Name, "adapter", false, "Web のアダプタは source.adapter に対応していません", [], DateTimeOffset.UtcNow));
        }

        return keys.Select(k => results.First(r => r.Key == k.Name)).ToList();
    }

    private async Task<IReadOnlyList<StateReadResult>> ReadSqlAsync(List<StateKey> keys, CancellationToken cancellationToken)
    {
        var ops = await _lab.OpsAsync(cancellationToken).ConfigureAwait(false);
        var queries = new JsonArray(keys.Select(k => (JsonNode?)new JsonObject { ["sql"] = ((SqlSource)k.Source).Sql }).ToArray());
        var response = await ops.CallObjectAsync("db.query_many", new JsonObject { ["db"] = _lab.DbPathInOps, ["queries"] = queries }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
        var observed = DateTimeOffset.UtcNow;
        var list = response["results"] as JsonArray ?? [];
        var results = new List<StateReadResult>();
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            if (list[i] is not JsonObject result || result["ok"]?.GetValue<bool>() != true)
            {
                results.Add(new StateReadResult(key.Name, "sql", false, list[i]?["error"]?.GetValue<string>() ?? "読めませんでした", [], observed));
                continue;
            }

            var columns = result["columns"] as JsonArray ?? [];
            if (columns.Count != 2)
            {
                results.Add(new StateReadResult(key.Name, "sql", false, $"SQL は「ID と値」の2列を返してください（{columns.Count} 列）", [], observed));
                continue;
            }

            var values = new List<StateValue>();
            foreach (var row in (result["rows"] as JsonArray ?? []).OfType<JsonArray>())
            {
                var entity = row[0]?.ToString() ?? "null";
                var value = row[1]?.DeepClone();
                var (ok, error) = CheckType(key.Type, value);
                values.Add(new StateValue(entity, value, ok, error));
            }

            results.Add(new StateReadResult(key.Name, "sql", true, null, values, observed));
        }

        return results;
    }

    private StateReadResult ReadSocket(StateKey key)
    {
        var source = (SocketSource)key.Source;
        var latest = _lab.Records.LatestBySocket(source.Events, source.Path);
        var values = new List<StateValue>();
        foreach (var pair in latest.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var (ok, error) = CheckType(key.Type, pair.Value);
            values.Add(new StateValue(pair.Key, pair.Value?.DeepClone(), ok, error));
        }

        return new StateReadResult(key.Name, "socket", true, null, values, DateTimeOffset.UtcNow);
    }

    private static async Task<StateReadResult> ReadDomAsync(StateKey key, AgentSession? browser, string page, CancellationToken cancellationToken)
    {
        var source = (DomSource)key.Source;
        if (browser == null)
        {
            return new StateReadResult(key.Name, "dom", false, "ブラウザが開いていません", [], DateTimeOffset.UtcNow);
        }

        var pageName = source.Page ?? page;
        try
        {
            var result = await browser.CallObjectAsync("page.read", new JsonObject { ["page"] = pageName, ["selector"] = source.Selector, ["read"] = source.Read }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var value = result["value"]?.DeepClone();
            if ((result["count"]?.GetValue<int>() ?? 0) == 0 && source.Read is not ("count" or "exists"))
            {
                return new StateReadResult(key.Name, "dom", true, null, [], DateTimeOffset.UtcNow);
            }

            var (ok, error) = CheckType(key.Type, value);
            return new StateReadResult(key.Name, "dom", true, null, [new StateValue(pageName, value, ok, error)], DateTimeOffset.UtcNow);
        }
        catch (CustosException e)
        {
            return new StateReadResult(key.Name, "dom", false, e.Message, [], DateTimeOffset.UtcNow);
        }
    }

    /// <summary>値が宣言した型に合うか（設計書 第17.3節 A-3）。</summary>
    public static (bool Ok, string? Error) CheckType(StateType type, JsonNode? value)
    {
        if (type == StateType.Json)
        {
            return (true, null);
        }

        if (value == null)
        {
            return (false, "null です");
        }

        var kind = value.GetValueKind();
        bool ok = type switch
        {
            StateType.Integral => kind == JsonValueKind.Number && value is JsonValue iv && iv.TryGetValue<long>(out _),
            StateType.Real => kind == JsonValueKind.Number,
            StateType.Bool => kind is JsonValueKind.True or JsonValueKind.False
                || (kind == JsonValueKind.Number && value is JsonValue bv && bv.TryGetValue<long>(out var b) && b is 0 or 1),
            StateType.Text => kind == JsonValueKind.String,
            StateType.Id => kind == JsonValueKind.String || (kind == JsonValueKind.Number && value is JsonValue idv && idv.TryGetValue<long>(out _)),
            StateType.List => kind == JsonValueKind.Array,
            StateType.Map => kind == JsonValueKind.Object,
            StateType.Vec3 => (value is JsonArray a && a.Count == 3 && a.All(n => n?.GetValueKind() == JsonValueKind.Number))
                || (value is JsonObject o && o.ContainsKey("x") && o.ContainsKey("y") && o.ContainsKey("z")),
            StateType.Timestamp => kind == JsonValueKind.Number
                || (kind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _)),
            _ => true,
        };
        return ok ? (true, null) : (false, $"型が合いません（宣言 {type}、値 {Truncate(value.ToJsonString())}）");
    }

    private static string Truncate(string text) => text.Length <= 80 ? text : text[..80] + "…";

    /// <summary>読み取り結果を JSON にする。</summary>
    public static JsonObject ToJson(StateReadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var values = new JsonObject();
        foreach (var value in result.Values)
        {
            values[value.Entity] = value.Value?.DeepClone();
        }

        return new JsonObject
        {
            ["key"] = result.Key,
            ["source"] = result.Source,
            ["ok"] = result.Ok,
            ["error"] = result.Error,
            ["observed_at"] = result.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
            ["count"] = result.Values.Count,
            ["type_errors"] = new JsonArray(result.TypeErrors.Select(e => (JsonNode?)JsonValue.Create($"{e.Entity}: {e.TypeError}")).ToArray()),
            ["values"] = values,
        };
    }

    /// <summary>JSON にして文字列にする。</summary>
    public static string Format(IReadOnlyList<StateReadResult> results) =>
        new JsonArray(results.Select(r => (JsonNode?)ToJson(r)).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
