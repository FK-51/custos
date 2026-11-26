using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Custos.Agent.Proxy;

/// <summary>
/// モデルの API への出口（設計書 第12.6節「AIエージェントの起動」、第13.6節）。HTTP の CONNECT だけを受け付け、
/// 許可した宛先（ホスト名:ポート）にだけトンネルをつなぐ。それ以外の要求と宛先は 403 で断る。中身は暗号化されたまま読まない。
/// 試みはすべて標準エラー出力に JSON の行で残す（管理側が docker logs で読む）。
/// </summary>
public sealed class EgressProxy
{
    private readonly IPEndPoint _listen;
    private readonly HashSet<string> _allow;

    /// <summary>作る。allow は "host:port" の一覧。</summary>
    public EgressProxy(IPEndPoint listen, IEnumerable<string> allow)
    {
        _listen = listen;
        _allow = new HashSet<string>(allow.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0), StringComparer.Ordinal);
    }

    /// <summary>許可するか（ホスト名の大文字小文字は区別しない。IP アドレスでの指定は、許可一覧に IP がない限り断る）。</summary>
    public bool Allowed(string target) => _allow.Contains(target.Trim().ToLowerInvariant());

    /// <summary>動かす。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(_listen);
        listener.Start();
        Log(new JsonObject { ["event"] = "listening", ["listen"] = _listen.ToString(), ["allow"] = new JsonArray(_allow.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()) });
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client, cancellationToken), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        var stream = client.GetStream();
        var head = await ReadHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (head == null)
        {
            return;
        }

        var firstLine = head.Split("\r\n")[0];
        var parts = firstLine.Split(' ');
        var method = parts.Length > 0 ? parts[0] : string.Empty;
        var target = parts.Length > 1 ? parts[1] : string.Empty;
        var from = client.Client.RemoteEndPoint?.ToString();
        if (method != "CONNECT" || !Allowed(target))
        {
            Log(new JsonObject { ["event"] = "denied", ["method"] = method, ["target"] = target, ["from"] = from });
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var colon = target.LastIndexOf(':');
        var host = target[..colon];
        var port = int.Parse(target[(colon + 1)..], CultureInfo.InvariantCulture);
        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException e)
        {
            Log(new JsonObject { ["event"] = "upstream_failed", ["target"] = target, ["error"] = e.Message, ["from"] = from });
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken).ConfigureAwait(false);
            return;
        }

        Log(new JsonObject { ["event"] = "tunnel", ["target"] = target, ["from"] = from });
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), cancellationToken).ConfigureAwait(false);
        var remote = upstream.GetStream();
        long up = 0;
        long down = 0;
        var a = PumpAsync(stream, remote, n => up += n, cancellationToken);
        var b = PumpAsync(remote, stream, n => down += n, cancellationToken);
        await Task.WhenAny(a, b).ConfigureAwait(false);
        Log(new JsonObject { ["event"] = "closed", ["target"] = target, ["bytes_up"] = up, ["bytes_down"] = down, ["from"] = from });
    }

    private static async Task PumpAsync(Stream from, Stream to, Action<int> count, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                count(read);
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text;
            }
        }

        return null;
    }

    private static void Log(JsonObject entry)
    {
        entry["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Console.Error.WriteLine(entry.ToJsonString());
    }
}
