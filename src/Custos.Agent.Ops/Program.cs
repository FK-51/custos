using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Agent.Common;
using Microsoft.Data.Sqlite;

// custos-ops：管理側の操作（設計書 第12.6節、指示書 タスク4・7）。
// アプリ側のネットワークにだけつなぎ、アプリのDBのボリュームとスナップショットの置き場所をマウントする。
// - DB は読み取り専用で開く（Mode=ReadOnly）。複数の読み取りは1つの読み取りトランザクションで行い、書き込み中でも壊れた内容を読まない。
// - スナップショットは SQLite のオンラインバックアップで取る（整合の取れた複製）。
// - 差し替えはアプリが止まっている間に行う（管理側が止めてから呼ぶ）。
return await AgentHost.RunAsync(args, Register).ConfigureAwait(false);

static void Register(RpcServer server)
{
    server.Register("db.query", (p, ct) => Task.FromResult<JsonNode?>(Query(p)));
    server.Register("db.query_many", (p, ct) => Task.FromResult<JsonNode?>(QueryMany(p)));
    server.Register("db.backup", (p, ct) => Task.FromResult<JsonNode?>(Backup(p)));
    server.Register("db.restore", (p, ct) => Task.FromResult<JsonNode?>(Restore(p)));
    server.Register("db.stat", (p, ct) => Task.FromResult<JsonNode?>(Stat(p)));
    server.Register("db.hash", (p, ct) => Task.FromResult<JsonNode?>(Hash(p)));
    server.Register("db.exec_copy", (p, ct) => Task.FromResult<JsonNode?>(ExecOnCopy(p)));
    server.Register("fs.remove", (p, ct) => Task.FromResult<JsonNode?>(Remove(p)));
    server.Register("http.request", HttpRequestAsync);
}

static string ReadOnlyConnectionString(string path) =>
    new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 30 }.ToString();

static SqliteConnection OpenReadOnly(string path)
{
    if (!File.Exists(path))
    {
        throw new RpcException($"DB のファイルがありません：{path}");
    }

    var connection = new SqliteConnection(ReadOnlyConnectionString(path));
    connection.Open();
    using var pragma = connection.CreateCommand();
    pragma.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA query_only = ON;";
    pragma.ExecuteNonQuery();
    return connection;
}

static JsonNode? ToJson(object? value) => value switch
{
    null or DBNull => null,
    long l => JsonValue.Create(l),
    double d => JsonValue.Create(d),
    string s => JsonValue.Create(s),
    byte[] bytes => new JsonObject { ["_blob_b64"] = Convert.ToBase64String(bytes) },
    _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
};

static JsonObject RunQuery(SqliteConnection connection, SqliteTransaction? transaction, string sql, JsonObject? parameters, int maxRows)
{
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    if (parameters != null)
    {
        foreach (var pair in parameters)
        {
            object? value = pair.Value switch
            {
                null => DBNull.Value,
                JsonValue v when v.TryGetValue<long>(out var l) => l,
                JsonValue v when v.TryGetValue<double>(out var d) => d,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1L : 0L,
                _ => pair.Value.ToJsonString(),
            };
            command.Parameters.AddWithValue(pair.Key.StartsWith('@') || pair.Key.StartsWith('$') || pair.Key.StartsWith(':') ? pair.Key : "@" + pair.Key, value);
        }
    }

    using var reader = command.ExecuteReader();
    var columns = new JsonArray();
    for (var i = 0; i < reader.FieldCount; i++)
    {
        columns.Add(reader.GetName(i));
    }

    var rows = new JsonArray();
    var truncated = false;
    while (reader.Read())
    {
        if (rows.Count >= maxRows)
        {
            truncated = true;
            break;
        }

        var row = new JsonArray();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row.Add(ToJson(reader.IsDBNull(i) ? null : reader.GetValue(i)));
        }

        rows.Add(row);
    }

    return new JsonObject { ["columns"] = columns, ["rows"] = rows, ["truncated"] = truncated };
}

static JsonNode Query(JsonObject p)
{
    var path = Params.Str(p, "db");
    var sql = Params.Str(p, "sql");
    using var connection = OpenReadOnly(path);
    return RunQuery(connection, null, sql, p["params"] as JsonObject, (int)Params.OptLong(p, "max_rows", 100_000));
}

static JsonNode QueryMany(JsonObject p)
{
    var path = Params.Str(p, "db");
    using var connection = OpenReadOnly(path);
    // 1つの読み取りトランザクションで読む（複数の問い合わせの間で、内容がずれない）
    using var transaction = connection.BeginTransaction(deferred: true);
    var results = new JsonArray();
    foreach (var item in (p["queries"] as JsonArray ?? []).OfType<JsonObject>())
    {
        var sql = Params.Str(item, "sql");
        try
        {
            var result = RunQuery(connection, transaction, sql, item["params"] as JsonObject, (int)Params.OptLong(p, "max_rows", 100_000));
            result["ok"] = true;
            results.Add(result);
        }
        catch (SqliteException e)
        {
            results.Add(new JsonObject { ["ok"] = false, ["error"] = e.Message, ["sqlite_error"] = e.SqliteErrorCode });
        }
    }

    transaction.Commit();
    return new JsonObject { ["results"] = results };
}

static JsonObject TableCounts(SqliteConnection connection)
{
    var counts = new JsonObject();
    foreach (var table in Tables(connection))
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        counts[table] = (long)(command.ExecuteScalar() ?? 0L);
    }

    return counts;
}

static List<string> Tables(SqliteConnection connection)
{
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
    using var reader = command.ExecuteReader();
    var tables = new List<string>();
    while (reader.Read())
    {
        tables.Add(reader.GetString(0));
    }

    return tables;
}

static JsonNode Backup(JsonObject p)
{
    var path = Params.Str(p, "db");
    var output = Params.Str(p, "out");
    Directory.CreateDirectory(Path.GetDirectoryName(output) ?? "/");
    var temp = output + ".partial";
    if (File.Exists(temp))
    {
        File.Delete(temp);
    }

    using (var source = OpenReadOnly(path))
    {
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    File.Move(temp, output, overwrite: Params.OptBool(p, "overwrite", false));
    using var check = OpenReadOnly(output);
    var bytes = File.ReadAllBytes(output);
    return new JsonObject
    {
        ["path"] = output,
        ["bytes"] = bytes.Length,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        ["tables"] = TableCounts(check),
        ["content"] = TableHashes(check),
    };
}

static JsonNode Restore(JsonObject p)
{
    var path = Params.Str(p, "db");
    var from = Params.Str(p, "from");
    if (!File.Exists(from))
    {
        throw new RpcException($"スナップショットがありません：{from}");
    }

    foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
    {
        if (File.Exists(path + suffix))
        {
            File.Delete(path + suffix);
        }
    }

    var temp = path + ".restore";
    File.Copy(from, temp, overwrite: true);
    File.Move(temp, path, overwrite: true);
    var uid = Params.OptLong(p, "uid", -1);
    if (uid >= 0)
    {
        RunChown(path, uid, Params.OptLong(p, "gid", uid));
    }

    using var check = OpenReadOnly(path);
    return new JsonObject { ["path"] = path, ["tables"] = TableCounts(check), ["content"] = TableHashes(check) };
}

static void RunChown(string path, long uid, long gid)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo("chown") { UseShellExecute = false };
    startInfo.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"{uid}:{gid}"));
    startInfo.ArgumentList.Add(path);
    using var process = System.Diagnostics.Process.Start(startInfo);
    process?.WaitForExit();
}

static JsonNode Stat(JsonObject p)
{
    var path = Params.Str(p, "db");
    var files = new JsonObject();
    foreach (var suffix in new[] { string.Empty, "-journal", "-wal", "-shm" })
    {
        var file = path + suffix;
        try
        {
            // ジャーナルは、アプリが書き込むたびに作られては消える。調べている間に消えてもよい
            var info = new FileInfo(file);
            if (info.Exists)
            {
                files[suffix.Length == 0 ? "db" : suffix.TrimStart('-')] = new JsonObject
                {
                    ["size"] = info.Length,
                    ["mtime_ns"] = (info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100,
                };
            }
        }
        catch (Exception e) when (e is FileNotFoundException or IOException)
        {
        }
    }

    return new JsonObject { ["files"] = files, ["now"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
}

static JsonObject TableHashes(SqliteConnection connection)
{
    var hashes = new JsonObject();
    foreach (var table in Tables(connection))
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            var row = new JsonArray();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row.Add(ToJson(reader.IsDBNull(i) ? null : reader.GetValue(i)));
            }

            lines.Add(row.ToJsonString());
        }

        lines.Sort(StringComparer.Ordinal);
        hashes[table] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    return hashes;
}

static JsonNode Hash(JsonObject p)
{
    var path = Params.Str(p, "db");
    using var connection = OpenReadOnly(path);
    using var transaction = connection.BeginTransaction(deferred: true);
    var result = new JsonObject { ["tables"] = TableCounts(connection), ["content"] = TableHashes(connection) };
    transaction.Commit();
    return result;
}

static JsonNode ExecOnCopy(JsonObject p)
{
    // 管理側から違反するデータを書き込む確認（適合テスト D）は、アプリのDBではなく複製に対して行う
    var path = Params.Str(p, "db");
    if (!path.StartsWith("/snapshots/", StringComparison.Ordinal))
    {
        throw new RpcException("書き込みは /snapshots/ の下の複製に対してだけ行えます");
    }

    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = Params.Str(p, "sql");
    var changed = command.ExecuteNonQuery();
    return new JsonObject { ["changed"] = changed };
}

static JsonNode Remove(JsonObject p)
{
    var path = Params.Str(p, "path");
    if (!path.StartsWith("/snapshots/", StringComparison.Ordinal))
    {
        throw new RpcException("削除できるのは /snapshots/ の下だけです");
    }

    var existed = File.Exists(path);
    if (existed)
    {
        File.Delete(path);
    }

    return new JsonObject { ["removed"] = existed };
}

static async Task<JsonNode?> HttpRequestAsync(JsonObject p, CancellationToken cancellationToken)
{
    var url = Params.Str(p, "url");
    var method = Params.OptStr(p, "method") ?? "GET";
    var timeout = TimeSpan.FromMilliseconds(Params.OptLong(p, "timeout_ms", 5000));
    using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None };
    using var client = new HttpClient(handler) { Timeout = timeout };
    using var request = new HttpRequestMessage(new HttpMethod(method), url);
    if (Params.OptStr(p, "body") is { } body)
    {
        request.Content = new StringContent(body, Encoding.UTF8, Params.OptStr(p, "content_type") ?? "application/json");
    }

    try
    {
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var headers = new JsonObject();
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }

        var text = Encoding.UTF8.GetString(bytes);
        return new JsonObject
        {
            ["ok"] = true,
            ["status"] = (int)response.StatusCode,
            ["headers"] = headers,
            ["bytes"] = bytes.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["body"] = text.Length > 200_000 ? text[..200_000] : text,
        };
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
    {
        return new JsonObject { ["ok"] = false, ["error"] = e.Message };
    }
}
