using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Agent.Common;
using Custos.SocketIO.Wire;

namespace Custos.Agent.Client;

/// <summary>
/// 生の Engine.IO クライアント（WebSocket）。ファジングや攻撃で、公式のクライアントでは作れない崩れたパケットを送るために使う。
/// ping には自動で pong を返す。受け取ったものはすべて残す。
/// </summary>
public sealed class RawClient : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<JsonObject> _received = [];
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SocketIoStreamDecoder _pollingDecoder = new();
    private HttpClient? _http;
    private Uri? _pollingUrl;
    private Task? _loop;
    private long _seq;

    /// <summary>呼び名。</summary>
    public RawClient(string name)
    {
        Name = name;
    }

    /// <summary>呼び名。</summary>
    public string Name { get; }

    /// <summary>Engine.IO の sid。</summary>
    public string? Sid { get; private set; }

    /// <summary>接続している状態。</summary>
    public string State => _http != null ? (_cts.IsCancellationRequested ? "Closed" : "Polling") : _socket.State.ToString();

    /// <summary>
    /// ポーリングで接続する（WebSocket の切り替えを中継しない構成でも使える）。connectNamespace が true なら CONNECT（40）も送る。
    /// </summary>
    public async Task ConnectPollingAsync(Uri baseUrl, IReadOnlyDictionary<string, string> headers, bool connectNamespace, CancellationToken cancellationToken, SocketPath? path = null)
    {
        var socketPath = path ?? new SocketPath("/socket.io/");
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(headers);
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(60) };
        foreach (var header in headers)
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        var handshake = new Uri(baseUrl, socketPath.Target("EIO=4&transport=polling"));
        var body = await _http.GetStringAsync(handshake, cancellationToken).ConfigureAwait(false);
        foreach (var packet in EngineIo.DecodePayload(body))
        {
            Sid ??= RecordDecoder.SidFromOpen(packet);
            Add(new JsonObject { ["eio"] = packet.TypeName, ["data"] = packet.Text, ["transport"] = "polling" });
        }

        if (Sid == null)
        {
            throw Fail("ポーリングの握手で sid を受け取れませんでした：" + body);
        }

        _pollingUrl = new Uri(baseUrl, socketPath.Target($"EIO=4&transport=polling&sid={Uri.EscapeDataString(Sid)}"));
        _loop = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        if (connectNamespace)
        {
            await SendTextAsync("40", cancellationToken).ConfigureAwait(false);
            await WaitAsync(m => m["sio"]?.GetValue<string>() == "connect", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var http = _http;
        var url = _pollingUrl;
        if (http == null || url == null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Add(new JsonObject { ["eio"] = "error", ["error"] = $"poll {(int)response.StatusCode}" });
                    return;
                }

                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                foreach (var packet in EngineIo.DecodePayload(payload))
                {
                    var entry = new JsonObject { ["eio"] = packet.TypeName, ["transport"] = "polling" };
                    if (packet.Type == EngineIoPacketType.Ping)
                    {
                        await SendTextAsync("3", cancellationToken).ConfigureAwait(false);
                    }

                    if (packet.Type == EngineIoPacketType.Close)
                    {
                        Add(entry);
                        return;
                    }

                    if (packet.Type == EngineIoPacketType.Message && packet.Valid)
                    {
                        entry["raw"] = packet.Text;
                        Describe(entry, packet.Binary != null ? _pollingDecoder.OnBinary(packet.Binary) : _pollingDecoder.OnText(packet.Text ?? string.Empty));
                    }
                    else
                    {
                        entry["data"] = packet.Text;
                    }

                    Add(entry);
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            Add(new JsonObject { ["eio"] = "error", ["error"] = e.Message });
        }
    }

    /// <summary>接続する。connectNamespace が true なら、Socket.IO の CONNECT（40）も送る。</summary>
    public async Task ConnectAsync(Uri baseUrl, IReadOnlyDictionary<string, string> headers, bool connectNamespace, CancellationToken cancellationToken, SocketPath? path = null)
    {
        var socketPath = path ?? new SocketPath("/socket.io/");
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var header in headers)
        {
            _socket.Options.SetRequestHeader(header.Key, header.Value);
        }

        var scheme = baseUrl.Scheme == "https" ? "wss" : "ws";
        var url = new Uri($"{scheme}://{baseUrl.Authority}{socketPath.Target("EIO=4&transport=websocket")}");
        await _socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        var open = await WaitAsync(m => m["eio"]?.GetValue<string>() == "open", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (open != null)
        {
            Sid = RecordDecoder.SidFromOpen(new EngineIoPacket(EngineIoPacketType.Open, open["data"]?.GetValue<string>(), null));
        }

        if (connectNamespace)
        {
            await SendTextAsync("40", cancellationToken).ConfigureAwait(false);
            await WaitAsync(m => m["sio"]?.GetValue<string>() == "connect", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>テキストのフレーム（ポーリングなら POST の本文）をそのまま送る（例：42["playerChoice",{...}]、崩れた JSON）。</summary>
    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        if (_http != null && _pollingUrl != null)
        {
            using var content = new StringContent(text, Encoding.UTF8, "text/plain");
            using var response = await _http.PostAsync(_pollingUrl, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Add(new JsonObject { ["eio"] = "error", ["error"] = $"post {(int)response.StatusCode}" });
            }

            return;
        }

        await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>バイナリのフレームを送る。</summary>
    public async Task SendBinaryAsync(byte[] data, CancellationToken cancellationToken)
    {
        await _socket.SendAsync(data, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var decoder = new SocketIoStreamDecoder();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Add(new JsonObject { ["eio"] = "ws_close" });
                    return;
                }

                var entry = new JsonObject();
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(message.ToArray());
                    var packet = EngineIo.DecodeText(text);
                    entry["eio"] = packet.TypeName;
                    entry["raw"] = text;
                    if (packet.Type == EngineIoPacketType.Ping)
                    {
                        await SendTextAsync("3" + (packet.Text ?? string.Empty), cancellationToken).ConfigureAwait(false);
                    }

                    if (packet.Type == EngineIoPacketType.Message && packet.Valid)
                    {
                        Describe(entry, decoder.OnText(packet.Text ?? string.Empty));
                    }
                    else
                    {
                        entry["data"] = packet.Text;
                    }
                }
                else
                {
                    entry["eio"] = "message";
                    entry["binary_b64"] = Convert.ToBase64String(message.ToArray());
                    Describe(entry, decoder.OnBinary(message.ToArray()));
                }

                Add(entry);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            Add(new JsonObject { ["eio"] = "error", ["error"] = e.Message });
        }
    }

    private static void Describe(JsonObject entry, SocketIoPacket? packet)
    {
        if (packet == null)
        {
            entry["sio"] = "pending";
            return;
        }

        entry["sio"] = packet.TypeName;
        entry["event"] = packet.EventName;
        entry["args"] = packet.Arguments;
        entry["ack"] = packet.Id;
        entry["valid"] = packet.Valid;
    }

    private void Add(JsonObject entry)
    {
        lock (_gate)
        {
            entry["seq"] = ++_seq;
            entry["ts"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            _received.Add(entry);
        }
    }

    /// <summary>受け取ったもの（seq より後）。</summary>
    public JsonArray Since(long since)
    {
        lock (_gate)
        {
            return new JsonArray(_received.Where(e => e["seq"]?.GetValue<long>() > since).Select(e => (JsonNode?)e.DeepClone()).ToArray());
        }
    }

    /// <summary>条件に合うものが来るまで待つ。</summary>
    public async Task<JsonObject?> WaitAsync(Func<JsonObject, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_gate)
            {
                var found = _received.FirstOrDefault(predicate);
                if (found != null)
                {
                    return found;
                }
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException)
        {
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _http?.Dispose();
        if (_loop != null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException)
            {
            }
        }

        _socket.Dispose();
        _cts.Dispose();
    }

    /// <summary>失敗を RPC の例外にする。</summary>
    public static RpcException Fail(string message) => new(message);
}
