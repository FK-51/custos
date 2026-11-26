using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.SocketIO.Wire;

namespace Custos.Agent.Proxy;

/// <summary>
/// 記録の書き出し。events.jsonl に1行1件で追記し、生の通信を raw/ に残し、
/// 最後に動きがあった時刻を activity.json に書く（管理側の完了待ちに使う）。
/// </summary>
public sealed class RecordSink : IDisposable
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly StreamWriter _events;
    private readonly Dictionary<string, FileStream> _raw = new(StringComparer.Ordinal);
    private long _seq;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastActivityWrite = DateTimeOffset.MinValue;
    private int _openConnections;
    private int _openWebSockets;
    private int _inflight;
    private readonly Timer _timer;

    /// <summary>記録のディレクトリを指定して作る。</summary>
    public RecordSink(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var path = Path.Combine(dir, "events.jsonl");
        if (File.Exists(path))
        {
            _seq = File.ReadLines(path).Count();
        }

        _events = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
        _timer = new Timer(_ => WriteActivity(force: true), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
    }

    /// <summary>1件を書く。activity が true なら「動きがあった」として時刻を進める。</summary>
    public void Write(RecordEntry entry, bool activity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            entry.Seq = ++_seq;
            if (entry.Ts == default)
            {
                entry.Ts = DateTimeOffset.UtcNow;
            }

            _events.WriteLine(RecordJson.Serialize(entry));
            if (activity)
            {
                _lastActivity = entry.Ts;
            }
        }

        WriteActivity(force: false);
    }

    /// <summary>動きがあったことだけを記録する（記録の行は書かない）。</summary>
    public void Touch()
    {
        lock (_gate)
        {
            _lastActivity = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>生の通信を追記する。</summary>
    public void WriteRaw(long conn, string dir, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var key = conn.ToString(CultureInfo.InvariantCulture) + "-" + dir;
            if (!_raw.TryGetValue(key, out var stream))
            {
                stream = new FileStream(Path.Combine(_dir, "raw", key + ".bin"), FileMode.Append, FileAccess.Write, FileShare.Read);
                _raw[key] = stream;
            }

            stream.Write(data);
            stream.Flush();
        }
    }

    /// <summary>生の通信のファイルを閉じる。</summary>
    public void CloseRaw(long conn)
    {
        lock (_gate)
        {
            foreach (var dir in new[] { "c2s", "s2c" })
            {
                var key = conn.ToString(CultureInfo.InvariantCulture) + "-" + dir;
                if (_raw.Remove(key, out var stream))
                {
                    stream.Dispose();
                }
            }
        }
    }

    /// <summary>接続の数を増減する。</summary>
    public void Connections(int delta)
    {
        Interlocked.Add(ref _openConnections, delta);
    }

    /// <summary>WebSocket の数を増減する。</summary>
    public void WebSockets(int delta)
    {
        Interlocked.Add(ref _openWebSockets, delta);
    }

    /// <summary>処理中の要求（長いポーリングを除く）の数を増減する。</summary>
    public void Inflight(int delta)
    {
        Interlocked.Add(ref _inflight, delta);
        Touch();
    }

    private int _rulesVersion;

    /// <summary>読み込んだ規則の版（管理側が、規則が効いたことを確かめるのに使う）。</summary>
    public void SetRulesVersion(int version)
    {
        Volatile.Write(ref _rulesVersion, version);
        WriteActivity(force: true);
    }

    private void WriteActivity(bool force)
    {
        JsonObject snapshot;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force && now - _lastActivityWrite < TimeSpan.FromMilliseconds(50))
            {
                return;
            }

            _lastActivityWrite = now;
            snapshot = new JsonObject
            {
                ["last_activity"] = _lastActivity.ToString("O", CultureInfo.InvariantCulture),
                ["last_seq"] = _seq,
                ["open_connections"] = Volatile.Read(ref _openConnections),
                ["open_websockets"] = Volatile.Read(ref _openWebSockets),
                ["inflight"] = Volatile.Read(ref _inflight),
                ["rules_version"] = Volatile.Read(ref _rulesVersion),
                ["written_at"] = now.ToString("O", CultureInfo.InvariantCulture),
            };
        }

        var temp = Path.Combine(_dir, $"activity.json.{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllText(temp, snapshot.ToJsonString());
            File.Move(temp, Path.Combine(_dir, "activity.json"), overwrite: true);
        }
        catch (IOException)
        {
            // 次の書き込みでやり直す
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Dispose();
        WriteActivity(force: true);
        lock (_gate)
        {
            _events.Dispose();
            foreach (var stream in _raw.Values)
            {
                stream.Dispose();
            }

            _raw.Clear();
        }
    }
}
