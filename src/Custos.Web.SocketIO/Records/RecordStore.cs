using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.SocketIO.Wire;

namespace Custos.Web.SocketIO.Records;

/// <summary>プロキシが書いた「最後に動きがあった時刻」。</summary>
public sealed record ProxyActivity(DateTimeOffset LastActivity, long LastSeq, int OpenConnections, int OpenWebSockets, int Inflight);

/// <summary>
/// 通信の記録を読む（管理側だけ）。events.jsonl を1行ずつ読み、イベント単位の記録として返す。
/// </summary>
public sealed class RecordStore
{
    private readonly string _dir;

    /// <summary>セッションの記録のディレクトリを指定して作る。</summary>
    public RecordStore(string dir)
    {
        _dir = dir;
    }

    /// <summary>ディレクトリ。</summary>
    public string Dir => _dir;

    /// <summary>events.jsonl の場所。</summary>
    public string EventsPath => Path.Combine(_dir, "events.jsonl");

    /// <summary>プロキシの動きの状態。</summary>
    public ProxyActivity Activity()
    {
        var path = Path.Combine(_dir, "activity.json");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new ProxyActivity(DateTimeOffset.MinValue, 0, 0, 0, 0);
                }

                var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (node == null)
                {
                    continue;
                }

                return new ProxyActivity(
                    DateTimeOffset.Parse(node["last_activity"]?.GetValue<string>() ?? "0001-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                    node["last_seq"]?.GetValue<long>() ?? 0,
                    node["open_connections"]?.GetValue<int>() ?? 0,
                    node["open_websockets"]?.GetValue<int>() ?? 0,
                    node["inflight"]?.GetValue<int>() ?? 0);
            }
            catch (Exception e) when (e is IOException or JsonException or FormatException)
            {
                Thread.Sleep(10);
            }
        }

        return new ProxyActivity(DateTimeOffset.MinValue, 0, 0, 0, 0);
    }

    /// <summary>すべての記録（seq より後）。書きかけの最後の行は読まない。</summary>
    public IReadOnlyList<RecordEntry> Read(long sinceSeq = 0)
    {
        var entries = new List<RecordEntry>();
        if (!File.Exists(EventsPath))
        {
            return entries;
        }

        using var stream = new FileStream(EventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return entries;
        }

        foreach (var line in text[..lastNewline].Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            RecordEntry? entry;
            try
            {
                entry = RecordJson.Deserialize(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry != null && entry.Seq > sinceSeq)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>Socket.IO のイベント（EVENT）の記録だけ。</summary>
    public IReadOnlyList<RecordEntry> Events(long sinceSeq = 0, string? dir = null, string? eventName = null) =>
        Read(sinceSeq).Where(e => e.Kind == "eio" && e.Sio is "event" or "binary_event"
            && (dir == null || e.Dir == dir)
            && (eventName == null || e.EventName == eventName)).ToList();

    /// <summary>
    /// Engine.IO の sid から Socket.IO の socket.id への対応（サーバーが送る CONNECT の {"sid": ...} から作る）。
    /// </summary>
    public IReadOnlyDictionary<string, string> SocketIds()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Read().Where(e => e.Kind == "eio" && e.Sio == "connect" && e.Dir == "s2c" && e.Sid != null))
        {
            if (entry.Args is JsonObject obj && obj["sid"] is JsonValue v && v.TryGetValue<string>(out var socketId) && entry.Sid is { } sid)
            {
                map[sid] = socketId;
            }
        }

        return map;
    }

    /// <summary>
    /// 利用者ごとの最後に届いた値（source.socket の読み方）。events のどれかで、path を最初の引数に当てて取り出す。
    /// 返り値は、受け取った socket.id から値。
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?> LatestBySocket(IReadOnlyList<string> events, string path, long sinceSeq = 0)
    {
        ArgumentNullException.ThrowIfNull(events);
        var socketIds = SocketIds();
        var latest = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var entry in Read(sinceSeq).Where(e => e.Kind == "eio" && e.Dir == "s2c" && e.Sio is "event" or "binary_event" && events.Contains(e.EventName ?? string.Empty, StringComparer.Ordinal)))
        {
            if (entry.Sid == null || entry.Args is not JsonArray args || args.Count == 0)
            {
                continue;
            }

            var values = Custos.Core.Util.Json.SelectPath(args[0], path);
            if (values.Count == 0)
            {
                continue;
            }

            var key = socketIds.TryGetValue(entry.Sid, out var socketId) ? socketId : entry.Sid;
            latest[key] = values[0]?.DeepClone();
        }

        return latest;
    }

    /// <summary>HTTP の記録だけ。</summary>
    public IReadOnlyList<RecordEntry> Http(long sinceSeq = 0) => Read(sinceSeq).Where(e => e.Kind == "http").ToList();
}
