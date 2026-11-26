using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Custos.Agent.Proxy;

/// <summary>
/// 異常の注入の規則（設計書 第13.3節、第19.4節）。管理側が control/rules.json に書き、プロキシが読み直す。
/// 管理側からプロキシへの一方向の指示で、利用者側からは書けない（control は読み取り専用でマウントする）。
/// </summary>
public sealed record ProxyRule(
    string Name,
    string Action,
    string? PathPrefix,
    string? Method,
    string? Dir,
    string? EventName,
    string? ContentType,
    int DelayMs,
    int Status,
    string? Css,
    double Probability);

/// <summary>規則の一式。</summary>
public sealed record ProxyRuleSet(int Version, IReadOnlyList<ProxyRule> Rules, string? DropAllToken)
{
    /// <summary>規則なし。</summary>
    public static readonly ProxyRuleSet Empty = new(0, [], null);

    /// <summary>JSON から読む。</summary>
    public static ProxyRuleSet Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("rules.json はオブジェクトにしてください");
        var rules = new List<ProxyRule>();
        if (root["rules"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var match = item["match"] as JsonObject ?? [];
                rules.Add(new ProxyRule(
                    Str(item, "name") ?? "rule",
                    Str(item, "action") ?? "none",
                    Str(match, "path_prefix"),
                    Str(match, "method"),
                    Str(match, "dir"),
                    Str(match, "event"),
                    Str(match, "content_type"),
                    (int)Num(item, "ms", 0),
                    (int)Num(item, "status", 500),
                    Str(item, "css"),
                    Num(item, "probability", 1.0)));
            }
        }

        return new ProxyRuleSet((int)Num(root, "version", 0), rules, Str(root, "drop_all_token"));
    }

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Num(JsonObject obj, string name, double fallback) =>
        obj[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;

    /// <summary>HTTP の要求に当てはまる規則。</summary>
    public IEnumerable<ProxyRule> ForRequest(string method, string path) =>
        Rules.Where(r => r.EventName == null
            && r.Dir is null or "c2s"
            && (r.PathPrefix == null || path.StartsWith(r.PathPrefix, StringComparison.Ordinal))
            && (r.Method == null || string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase)));

    /// <summary>HTTP の応答に当てはまる規則。</summary>
    public IEnumerable<ProxyRule> ForResponse(string method, string path, string? contentType) =>
        Rules.Where(r => r.EventName == null
            && r.Dir is "s2c"
            && (r.PathPrefix == null || path.StartsWith(r.PathPrefix, StringComparison.Ordinal))
            && (r.Method == null || string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase))
            && (r.ContentType == null || (contentType ?? string.Empty).Contains(r.ContentType, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Socket.IO のイベントに当てはまる規則。</summary>
    public IEnumerable<ProxyRule> ForEvent(string dir, string? eventName) =>
        Rules.Where(r => r.EventName != null
            && (r.Dir == null || r.Dir == dir)
            && (r.EventName == "*" || string.Equals(r.EventName, eventName, StringComparison.Ordinal)));
}

/// <summary>規則のファイルを見張って読み直す。</summary>
public sealed class RuleWatcher : IDisposable
{
    private readonly string _path;
    private readonly Timer _timer;
    private string? _lastText;
    private volatile ProxyRuleSet _current = ProxyRuleSet.Empty;

    /// <summary>規則のファイルを指定して作る。</summary>
    public RuleWatcher(string path)
    {
        _path = path;
        Reload();
        _timer = new Timer(_ => Reload(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    /// <summary>今の規則。</summary>
    public ProxyRuleSet Current => _current;

    /// <summary>規則が変わったときに呼ばれる。</summary>
    public event EventHandler<ProxyRuleSet>? Changed;

    private void Reload()
    {
        try
        {
            if (!File.Exists(_path))
            {
                if (_current.Version != 0 || _current.Rules.Count > 0)
                {
                    _current = ProxyRuleSet.Empty;
                    Changed?.Invoke(this, _current);
                }

                return;
            }

            // 更新時刻ではなく中身で比べる（ホストから共有したファイルは、コンテナの中で更新時刻が古いまま見えることがある）
            var text = File.ReadAllText(_path);
            if (text == _lastText)
            {
                return;
            }

            _lastText = text;
            var rules = ProxyRuleSet.Parse(text);
            _current = rules;
            Changed?.Invoke(this, rules);
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"rules reloaded: version={rules.Version} count={rules.Rules.Count}"));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("rules reload failed: " + e.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();
}
