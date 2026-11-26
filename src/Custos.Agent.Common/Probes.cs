using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Custos.Agent.Common;

/// <summary>
/// 到達性の検査（設計書 第17.3節 E：隔離）。コンテナの中から、ネットワークとファイルとして何に届くかを調べる。
/// </summary>
public static class Probes
{
    /// <summary>すべての部品に、検査の処理を登録する。</summary>
    public static void Register(RpcServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Register("probe.tcp", (p, ct) => TcpAsync(p, ct));
        server.Register("probe.dns", (p, ct) => DnsAsync(p, ct));
        server.Register("probe.files", Files);
        server.Register("probe.mounts", _ => Mounts());
        server.Register("probe.search", Search);
        server.Register("probe.whoami", _ => WhoAmI());
        server.Register("probe.http", (p, ct) => HttpAsync(p, ct));
    }

    private static async Task<JsonNode?> TcpAsync(JsonObject p, CancellationToken cancellationToken)
    {
        var host = Params.Str(p, "host");
        var port = (int)Params.OptLong(p, "port", 80);
        var timeout = TimeSpan.FromMilliseconds(Params.OptLong(p, "timeout_ms", 3000));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var result = new JsonObject { ["host"] = host, ["port"] = port, ["connected"] = true, ["ms"] = stopwatch.ElapsedMilliseconds };
            if (Params.OptStr(p, "send") is { } send)
            {
                // 送って、応答の先頭を読む（例：WebSocket の切り替えの要求に 101 が返るか）。時間内に応答がなければ空
                var stream = client.GetStream();
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(send), cts.Token).ConfigureAwait(false);
                var buffer = new byte[(int)Params.OptLong(p, "read_bytes", 512)];
                var total = 0;
                try
                {
                    while (total < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                        if (System.Text.Encoding.UTF8.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    result["timed_out"] = true;
                }

                result["response"] = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
                result["ms"] = stopwatch.ElapsedMilliseconds;
            }

            return result;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            return new JsonObject { ["host"] = host, ["port"] = port, ["connected"] = false, ["error"] = e.Message, ["ms"] = stopwatch.ElapsedMilliseconds };
        }
    }

    private static async Task<JsonNode?> DnsAsync(JsonObject p, CancellationToken cancellationToken)
    {
        var name = Params.Str(p, "name");
        var timeout = TimeSpan.FromMilliseconds(Params.OptLong(p, "timeout_ms", 3000));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, cts.Token).ConfigureAwait(false);
            return new JsonObject
            {
                ["name"] = name,
                ["resolved"] = true,
                ["addresses"] = new JsonArray(addresses.Select(a => (JsonNode?)JsonValue.Create(a.ToString())).ToArray()),
            };
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
        {
            return new JsonObject { ["name"] = name, ["resolved"] = false, ["error"] = e.Message };
        }
    }

    private static JsonNode? Files(JsonObject p)
    {
        var result = new JsonArray();
        foreach (var path in Params.StrList(p, "paths"))
        {
            var exists = File.Exists(path) || Directory.Exists(path);
            var readable = false;
            var writable = false;
            if (File.Exists(path))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    readable = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
            else if (Directory.Exists(path))
            {
                try
                {
                    _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
                    readable = true;
                    var probe = Path.Combine(path, $".probe-{Environment.ProcessId}");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                    writable = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            result.Add(new JsonObject { ["path"] = path, ["exists"] = exists, ["readable"] = readable, ["writable"] = writable });
        }

        return result;
    }

    private static JsonArray Mounts()
    {
        var mounts = new JsonArray();
        if (File.Exists("/proc/mounts"))
        {
            foreach (var line in File.ReadAllLines("/proc/mounts"))
            {
                mounts.Add(line);
            }
        }

        return mounts;
    }

    /// <summary>ファイルの中から、目印の文字列を探す（E-3：検証用リポジトリの目印が見えないこと）。</summary>
    private static JsonNode? Search(JsonObject p)
    {
        var needle = Params.Str(p, "needle");
        var roots = Params.StrList(p, "roots");
        var maxFiles = (int)Params.OptLong(p, "max_files", 200_000);
        var maxBytes = Params.OptLong(p, "max_file_bytes", 1_000_000);
        var found = new JsonArray();
        var scanned = 0;
        var skip = new[] { "/proc", "/sys", "/dev" };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && scanned < maxFiles)
            {
                var dir = stack.Pop();
                if (skip.Any(s => dir == s || dir.StartsWith(s + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var info = new DirectoryInfo(sub);
                        if (info.LinkTarget == null)
                        {
                            stack.Push(sub);
                        }
                    }

                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        scanned++;
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.Length > maxBytes || info.LinkTarget != null)
                            {
                                continue;
                            }

                            if (info.Name.Contains(needle, StringComparison.Ordinal) || File.ReadAllText(file).Contains(needle, StringComparison.Ordinal))
                            {
                                found.Add(file);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return new JsonObject { ["needle_found"] = found.Count > 0, ["files"] = found, ["scanned"] = scanned };
    }

    private static JsonObject WhoAmI() => new JsonObject
    {
        ["hostname"] = Environment.MachineName,
        ["user"] = Environment.UserName,
        ["addresses"] = new JsonArray(Dns.GetHostAddresses(Dns.GetHostName()).Select(a => (JsonNode?)JsonValue.Create(a.ToString())).ToArray()),
    };

    /// <summary>HTTP の要求を1つ送り、状態と本文のハッシュを返す（E-5：/__verify/ の遮断の確認）。</summary>
    private static async Task<JsonNode?> HttpAsync(JsonObject p, CancellationToken cancellationToken)
    {
        var url = Params.Str(p, "url");
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Params.OptLong(p, "timeout_ms", 5000)) };
        using var request = new HttpRequestMessage(new HttpMethod(Params.OptStr(p, "method") ?? "GET"), url);
        if (p["headers"] is JsonObject headers)
        {
            foreach (var pair in headers)
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value?.GetValue<string>());
            }
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new JsonObject { ["ok"] = true, ["status"] = (int)response.StatusCode, ["bytes"] = body.Length };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }
}
