using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Custos.Agent.Common;

/// <summary>RPC の処理の失敗（呼び出し元に message を返す）。</summary>
public sealed class RpcException : Exception
{
    /// <summary>空のメッセージで作る。</summary>
    public RpcException()
    {
    }

    /// <summary>メッセージを付けて作る。</summary>
    public RpcException(string message)
        : base(message)
    {
    }

    /// <summary>原因の例外を付けて作る。</summary>
    public RpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 標準入出力の JSON Lines で、管理側からの指示を受ける（docs/architecture.md 5.2）。
/// 管理側は docker exec -i でこのプロセスを起動する。ネットワーク上の待ち受けは作らないので、利用者側からは届かない。
/// 要求：{"id":1,"method":"...","params":{...}}、応答：{"id":1,"result":...} か {"id":1,"error":{"message":"..."}}、
/// 通知：{"event":"...","data":...}。
/// </summary>
public sealed class RpcServer : IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly ConcurrentDictionary<string, Func<JsonObject, CancellationToken, Task<JsonNode?>>> _handlers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Stream? _output;

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <summary>処理を登録する。</summary>
    public void Register(string method, Func<JsonObject, CancellationToken, Task<JsonNode?>> handler) => _handlers[method] = handler;

    /// <summary>同期の処理を登録する。</summary>
    public void Register(string method, Func<JsonObject, JsonNode?> handler) =>
        _handlers[method] = (p, _) => Task.FromResult(handler(p));

    /// <summary>標準入力が閉じるまで、要求を受けて処理する。要求ごとに並行して処理する。</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        _output = output;
        Register("ping", _ => new JsonObject { ["pong"] = true });
        Register("methods", _ => new JsonArray(_handlers.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()));
        using var reader = new StreamReader(input, Encoding.UTF8);
        var running = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            running.Add(HandleAsync(line, cancellationToken));
            running.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(line) as JsonObject ?? throw new RpcException("要求は JSON のオブジェクトにしてください");
            id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>() ?? throw new RpcException("method がありません");
            var parameters = request["params"] as JsonObject ?? [];
            if (!_handlers.TryGetValue(method, out var handler))
            {
                throw new RpcException($"知らない method です：{method}");
            }

            var result = await handler(parameters, cancellationToken).ConfigureAwait(false);
            await WriteAsync(new JsonObject { ["id"] = id, ["result"] = result }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is RpcException or JsonException or InvalidOperationException or IOException or TimeoutException or OperationCanceledException or ArgumentException or FormatException or HttpRequestException or System.Net.Sockets.SocketException or NotSupportedException or KeyNotFoundException)
        {
            await WriteAsync(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["message"] = e.Message, ["type"] = e.GetType().Name } }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>通知を送る。</summary>
    public Task NotifyAsync(string name, JsonNode? data, CancellationToken cancellationToken) =>
        WriteAsync(new JsonObject { ["event"] = name, ["data"] = data }, cancellationToken);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (_output == null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(WriteOptions) + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

/// <summary>引数の読み出しの補助。</summary>
public static class Params
{
    /// <summary>必須の文字列。</summary>
    public static string Str(JsonObject p, string name)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p[name]?.GetValue<string>() ?? throw new RpcException($"{name} がありません");
    }

    /// <summary>任意の文字列。</summary>
    public static string? OptStr(JsonObject p, string name)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    /// <summary>任意の整数。</summary>
    public static long OptLong(JsonObject p, string name, long fallback)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : fallback;
    }

    /// <summary>任意の真偽値。</summary>
    public static bool OptBool(JsonObject p, string name, bool fallback)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    }

    /// <summary>文字列の列。</summary>
    public static IReadOnlyList<string> StrList(JsonObject p, string name)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p[name] is not JsonArray array)
        {
            return [];
        }

        return array.Select(n => n?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToList();
    }
}
