using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Conformance;
using Custos.Core.Config;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Util;
using Custos.Web.SocketIO.Lab;
using Microsoft.Data.Sqlite;

namespace Custos.Web.SocketIO.Judges;

/// <summary>
/// 判定の起点（基準）。判定はこの時点より後の通信、ログ、コンテナの状態を見る。
/// </summary>
public sealed record JudgeBaseline(long RecordsSeq, DateTimeOffset At, string? AppStartedAt, int AppRestartCount);

/// <summary>判定に必要なもの。</summary>
public sealed class JudgeContext
{
    /// <summary>作る。</summary>
    public JudgeContext(Lab.Lab lab, Run run, JudgeBaseline baseline)
    {
        Lab = lab;
        Run = run;
        Baseline = baseline;
    }

    /// <summary>検証環境。</summary>
    public Lab.Lab Lab { get; }

    /// <summary>証拠の置き場所。</summary>
    public Run Run { get; }

    /// <summary>基準。</summary>
    public JudgeBaseline Baseline { get; }

    /// <summary>設定。</summary>
    public ConfigSet Config => Lab.Config;

    /// <summary>いまの状態を基準として取る。</summary>
    public static async Task<JudgeBaseline> CaptureAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var state = await lab.ContainerStateAsync(Containers.App(lab.Project), cancellationToken).ConfigureAwait(false);
        return new JudgeBaseline(lab.Records.Activity().LastSeq, DateTimeOffset.UtcNow, state["started_at"]?.GetValue<string>(), state["restart_count"]?.GetValue<int>() ?? 0);
    }

    /// <summary>証拠の JSON を書く。</summary>
    public string Write(string name, JsonNode? node) =>
        Run.WriteText($"evidence/{name}-{Guid.NewGuid().ToString("N")[..8]}.json", node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null");

    /// <summary>証拠の相対パス。</summary>
    public string Rel(string path) => Path.GetRelativePath(Run.Dir, path);
}

/// <summary>判定器。</summary>
public interface IJudge
{
    /// <summary>名前。</summary>
    string Name { get; }

    /// <summary>判定する。</summary>
    Task<JudgeReport> JudgeAsync(JudgeContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 通信の記録を、SQL で調べられる表にする（送信された状態の不変条件、写しの突き合わせ。設計書 第25.3節）。
/// 表：sent（サーバーからクライアント）、recv（クライアントからサーバー）、http。payload は最初の引数の JSON（json_extract で読む）。
/// cross_sql では、アプリの DB の複製を app として添付する。
/// </summary>
public sealed class RecordsDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string? _appCopy;

    private RecordsDatabase(SqliteConnection connection, string? appCopy)
    {
        _connection = connection;
        _appCopy = appCopy;
    }

    /// <summary>接続。</summary>
    public SqliteConnection Connection => _connection;

    /// <summary>記録から作る。withApp なら、アプリの DB の整合の取れた複製を app として添付する。</summary>
    public static async Task<RecordsDatabase> BuildAsync(Lab.Lab lab, long sinceSeq, bool withApp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE sent (seq INTEGER, ts TEXT, sid TEXT, socket_id TEXT, user TEXT, event TEXT, payload TEXT, args TEXT, transport TEXT);
                CREATE TABLE recv (seq INTEGER, ts TEXT, sid TEXT, socket_id TEXT, user TEXT, event TEXT, payload TEXT, args TEXT, ack INTEGER, valid INTEGER);
                CREATE TABLE acks (seq INTEGER, ts TEXT, sid TEXT, socket_id TEXT, dir TEXT, ack INTEGER, args TEXT);
                CREATE TABLE http (seq INTEGER, ts TEXT, client TEXT, method TEXT, target TEXT, status INTEGER, blocked INTEGER, request_headers TEXT, forwarded_headers TEXT);
                """;
            create.ExecuteNonQuery();
        }

        var socketIds = lab.Records.SocketIds();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in lab.Records.Read(sinceSeq))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            var socketId = entry.Sid != null && socketIds.TryGetValue(entry.Sid, out var s) ? s : null;
            if (entry.Kind == "eio" && entry.Sio is "event")
            {
                var payload = entry.Args is JsonArray args && args.Count > 0 ? args[0]?.ToJsonString() : null;
                if (entry.Dir == "s2c")
                {
                    insert.CommandText = "INSERT INTO sent VALUES ($seq,$ts,$sid,$socket,NULL,$event,$payload,$args,$transport)";
                }
                else
                {
                    insert.CommandText = "INSERT INTO recv VALUES ($seq,$ts,$sid,$socket,NULL,$event,$payload,$args,$ack,$valid)";
                    insert.Parameters.AddWithValue("$ack", (object?)entry.Ack ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$valid", entry.Valid == false ? 0 : 1);
                }

                insert.Parameters.AddWithValue("$seq", entry.Seq);
                insert.Parameters.AddWithValue("$ts", entry.Ts.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$sid", (object?)entry.Sid ?? DBNull.Value);
                insert.Parameters.AddWithValue("$socket", (object?)socketId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$event", (object?)entry.EventName ?? DBNull.Value);
                insert.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
                insert.Parameters.AddWithValue("$args", (object?)entry.Args?.ToJsonString() ?? DBNull.Value);
                if (entry.Dir == "s2c")
                {
                    insert.Parameters.AddWithValue("$transport", (object?)entry.Transport ?? DBNull.Value);
                }

                insert.ExecuteNonQuery();
            }
            else if (entry.Kind == "eio" && entry.Sio == "ack")
            {
                insert.CommandText = "INSERT INTO acks VALUES ($seq,$ts,$sid,$socket,$dir,$ack,$args)";
                insert.Parameters.AddWithValue("$seq", entry.Seq);
                insert.Parameters.AddWithValue("$ts", entry.Ts.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$sid", (object?)entry.Sid ?? DBNull.Value);
                insert.Parameters.AddWithValue("$socket", (object?)socketId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$dir", (object?)entry.Dir ?? DBNull.Value);
                insert.Parameters.AddWithValue("$ack", (object?)entry.Ack ?? DBNull.Value);
                insert.Parameters.AddWithValue("$args", (object?)entry.Args?.ToJsonString() ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
            else if (entry.Kind == "http")
            {
                insert.CommandText = "INSERT INTO http VALUES ($seq,$ts,$client,$method,$target,$status,$blocked,$rh,$fh)";
                insert.Parameters.AddWithValue("$seq", entry.Seq);
                insert.Parameters.AddWithValue("$ts", entry.Ts.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$client", entry.Client);
                insert.Parameters.AddWithValue("$method", (object?)entry.Method ?? DBNull.Value);
                insert.Parameters.AddWithValue("$target", (object?)entry.Target ?? DBNull.Value);
                insert.Parameters.AddWithValue("$status", (object?)entry.Status ?? DBNull.Value);
                insert.Parameters.AddWithValue("$blocked", entry.Blocked == true ? 1 : 0);
                insert.Parameters.AddWithValue("$rh", (object?)entry.RequestHeaders ?? DBNull.Value);
                insert.Parameters.AddWithValue("$fh", (object?)entry.ForwardedRequestHeaders ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        string? appCopy = null;
        if (withApp)
        {
            var name = "judge-" + Guid.NewGuid().ToString("N")[..12];
            var ops = await lab.OpsAsync(cancellationToken).ConfigureAwait(false);
            await ops.CallAsync("db.backup", new JsonObject { ["db"] = lab.DbPathInOps, ["out"] = $"/snapshots/tmp/{name}.db" }, TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            appCopy = Path.Combine(lab.Workspace.SnapshotsDir, "tmp", name + ".db");
            using var attach = connection.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $path AS app";
            attach.Parameters.AddWithValue("$path", new SqliteConnectionStringBuilder { DataSource = appCopy, Mode = SqliteOpenMode.ReadOnly }.DataSource);
            attach.ExecuteNonQuery();
        }

        return new RecordsDatabase(connection, appCopy);
    }

    /// <summary>違反している行を返す SQL を実行する。</summary>
    public (IReadOnlyList<string> Columns, IReadOnlyList<JsonArray> Rows) Query(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<JsonArray>();
        while (reader.Read() && rows.Count < 1000)
        {
            var row = new JsonArray();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row.Add(reader.IsDBNull(i) ? null : JsonValue.Create(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)));
            }

            rows.Add(row);
        }

        return (columns, rows);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        if (_appCopy != null && File.Exists(_appCopy))
        {
            File.Delete(_appCopy);
        }
    }
}

/// <summary>計時の補助。</summary>
internal static class Timing
{
    public static double Ms(Stopwatch stopwatch) => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1);
}

/// <summary>判定の結果を作る補助。</summary>
internal static class Report
{
    public static JudgeReport Of(string judge, IEnumerable<Finding> findings, Stopwatch stopwatch) =>
        new(judge, true, null, findings.ToList(), Timing.Ms(stopwatch));

    public static JudgeReport Skip(string judge, string reason) => new(judge, false, reason, [], 0);

    public static Evidence Evidence(JudgeContext context, string description, string path) => new(description, context.Rel(path));
}
