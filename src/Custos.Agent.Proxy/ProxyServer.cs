using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Custos.SocketIO.Wire;

namespace Custos.Agent.Proxy;

/// <summary>プロキシの設定。</summary>
public sealed record ProxyOptions(
    IPEndPoint Listen,
    string UpstreamHost,
    int UpstreamPort,
    string RecordsDir,
    string? ControlDir,
    IReadOnlyList<string> BlockPrefixes,
    string SocketIoPath,
    bool BreakAddHeader);

/// <summary>
/// 記録用のプロキシ（指示書 タスク5、設計書 第13.3節）。
/// - 利用者側からの通信を、アプリへそのまま中継する（内容もヘッダーも変えない。制約5）。
/// - 写しを Engine.IO / Socket.IO のパケットに読み解き、イベント単位で記録する。生の通信も残す。
/// - /__verify/ で始まるパスは遮断する（設計書 第12.6節）。
/// - 管理側が書いた規則で、遅延、エラーの応答、切断、CSS の差し込みを注入する。
/// </summary>
public sealed class ProxyServer : IDisposable
{
    private static readonly byte[] BlockedResponse = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    private readonly ProxyOptions _options;
    private readonly RecordSink _sink;
    private readonly RecordDecoder _decoder = new();
    private readonly RuleWatcher? _rules;
    private readonly HashSet<TcpClient> _clients = [];
    private readonly object _clientsGate = new();
    private long _connCounter;
    private string? _lastDropToken;

    /// <summary>設定を指定して作る。</summary>
    public ProxyServer(ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _sink = new RecordSink(options.RecordsDir);
        if (options.ControlDir != null)
        {
            _rules = new RuleWatcher(Path.Combine(options.ControlDir, "rules.json"));
            _rules.Changed += (_, set) => OnRulesChanged(set);
            _lastDropToken = _rules.Current.DropAllToken;
            _sink.SetRulesVersion(_rules.Current.Version);
        }
    }

    private ProxyRuleSet Rules => _rules?.Current ?? ProxyRuleSet.Empty;

    private void OnRulesChanged(ProxyRuleSet set)
    {
        _sink.SetRulesVersion(set.Version);
        if (set.DropAllToken != null && set.DropAllToken != _lastDropToken)
        {
            _lastDropToken = set.DropAllToken;
            List<TcpClient> clients;
            lock (_clientsGate)
            {
                clients = _clients.ToList();
            }

            foreach (var client in clients)
            {
                client.Dispose();
            }

            _sink.Write(new RecordEntry { Kind = "conn", Event = "drop_all", Client = "-", Injected = "drop_all" }, activity: true);
        }
    }

    /// <summary>止まるまで、接続を受け付ける。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(_options.Listen);
        listener.Start(512);
        Console.Error.WriteLine($"proxy listening on {_options.Listen} -> {_options.UpstreamHost}:{_options.UpstreamPort}");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var conn = Interlocked.Increment(ref _connCounter);
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var clientAddress = remote?.Address.MapToIPv4().ToString() ?? "?";
        lock (_clientsGate)
        {
            _clients.Add(client);
        }

        _sink.Connections(1);
        _sink.Write(new RecordEntry { Kind = "conn", Event = "open", Conn = conn, Client = clientAddress }, activity: false);
        TcpClient? upstream = null;
        string? closeReason = null;
        try
        {
            var clientStream = client.GetStream();
            var clientReader = new HttpMessageReader(clientStream);
            HttpMessageReader? upstreamReader = null;
            NetworkStream? upstreamStream = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await clientReader.ReadHeadAsync(isResponse: false, cancellationToken).ConfigureAwait(false);
                if (request == null)
                {
                    closeReason = "client closed";
                    break;
                }

                var stopwatch = Stopwatch.StartNew();
                var path = request.Path;
                // Socket.IO のパス：/socket.io/ と、末尾の / を外した /socket.io の両方（addTrailingSlash: false や、308 で / を外された要求）
                var socketIoBase = _options.SocketIoPath.TrimEnd('/');
                var isSocketIo = path == socketIoBase || path.StartsWith(socketIoBase + "/", StringComparison.Ordinal);
                var sid = request.Query("sid");
                var transport = request.Query("transport");
                var longPoll = isSocketIo && request.Method == "GET" && transport == "polling" && sid != null;
                if (!longPoll)
                {
                    _sink.Inflight(1);
                }

                try
                {
                    // 遮断：テスト用APIは利用者側から使わせない
                    if (_options.BlockPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal)))
                    {
                        await clientReader.ReadBodyAsync(HttpMessageReader.RequestFraming(request), request.ContentLength, _ => Task.CompletedTask, null, cancellationToken).ConfigureAwait(false);
                        await clientStream.WriteAsync(BlockedResponse, cancellationToken).ConfigureAwait(false);
                        _sink.Write(HttpEntry(conn, clientAddress, request, null, 404, string.Empty, 0, 0, null, stopwatch, blocked: true), activity: true);
                        closeReason = "blocked";
                        break;
                    }

                    // 規則：要求の段階で注入する異常
                    string? injected = null;
                    foreach (var rule in Rules.ForRequest(request.Method, path))
                    {
                        if (!Fires(rule))
                        {
                            continue;
                        }

                        switch (rule.Action)
                        {
                            case "delay":
                                injected = rule.Name;
                                await Task.Delay(rule.DelayMs, cancellationToken).ConfigureAwait(false);
                                break;
                            case "status":
                                injected = rule.Name;
                                await clientReader.ReadBodyAsync(HttpMessageReader.RequestFraming(request), request.ContentLength, _ => Task.CompletedTask, null, cancellationToken).ConfigureAwait(false);
                                var statusText = rule.Status.ToString(CultureInfo.InvariantCulture);
                                await clientStream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {statusText} Injected\r\nContent-Length: 0\r\n\r\n"), cancellationToken).ConfigureAwait(false);
                                _sink.Write(HttpEntry(conn, clientAddress, request, null, rule.Status, string.Empty, 0, 0, null, stopwatch, blocked: false, injected), activity: true);
                                continue;
                            case "drop_connection":
                                _sink.Write(HttpEntry(conn, clientAddress, request, null, 0, string.Empty, 0, 0, null, stopwatch, blocked: false, rule.Name), activity: true);
                                return;
                        }
                    }

                    if (upstream == null)
                    {
                        upstream = new TcpClient { NoDelay = true };
                        await upstream.ConnectAsync(_options.UpstreamHost, _options.UpstreamPort, cancellationToken).ConfigureAwait(false);
                        upstreamStream = upstream.GetStream();
                        upstreamReader = new HttpMessageReader(upstreamStream);
                    }

                    if (upstreamStream == null || upstreamReader == null)
                    {
                        throw new InvalidOperationException("上流の接続がありません");
                    }

                    var forwardedHead = _options.BreakAddHeader ? AddHeader(request.Raw, "X-Custos-Proxy", "1") : request.Raw;
                    await upstreamStream.WriteAsync(forwardedHead, cancellationToken).ConfigureAwait(false);
                    if (isSocketIo)
                    {
                        _sink.WriteRaw(conn, "c2s", request.Raw);
                    }

                    // 要求の本文：そのまま転送し、Socket.IO なら写しを読み解く
                    var requestBody = new MemoryStream();
                    long requestBytes = 0;
                    var requestFraming = HttpMessageReader.RequestFraming(request);
                    if (isSocketIo && request.Method == "POST" && Rules.Rules.Any(r => r.EventName != null))
                    {
                        // イベント単位の規則があるときは、本文を読んでから判断する
                        var buffered = new MemoryStream();
                        await clientReader.ReadBodyAsync(requestFraming, request.ContentLength, raw => { buffered.Write(raw.Span); return Task.CompletedTask; }, d => requestBody.Write(d.Span), cancellationToken).ConfigureAwait(false);
                        var action = await ApplyEventRulesAsync("c2s", RecordDecoder.BodyText(requestBody.ToArray()), cancellationToken).ConfigureAwait(false);
                        if (action == "drop_connection")
                        {
                            return;
                        }

                        await upstreamStream.WriteAsync(buffered.ToArray(), cancellationToken).ConfigureAwait(false);
                        _sink.WriteRaw(conn, "c2s", buffered.ToArray());
                        requestBytes = requestBody.Length;
                    }
                    else
                    {
                        await clientReader.ReadBodyAsync(
                            requestFraming,
                            request.ContentLength,
                            async raw =>
                            {
                                await upstreamStream.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
                                if (isSocketIo)
                                {
                                    _sink.WriteRaw(conn, "c2s", raw.Span);
                                }
                            },
                            decoded =>
                            {
                                requestBytes += decoded.Length;
                                if (isSocketIo)
                                {
                                    requestBody.Write(decoded.Span);
                                }
                            },
                            cancellationToken).ConfigureAwait(false);
                    }

                    // 応答の頭（1xx は転送して読み直す）
                    HttpHead? response;
                    while (true)
                    {
                        response = await upstreamReader.ReadHeadAsync(isResponse: true, cancellationToken).ConfigureAwait(false);
                        if (response == null || response.Status is < 100 or >= 200 || response.Status == 101)
                        {
                            break;
                        }

                        await clientStream.WriteAsync(response.Raw, cancellationToken).ConfigureAwait(false);
                    }

                    if (response == null)
                    {
                        closeReason = "upstream closed";
                        break;
                    }

                    // 送った packet は、アプリが受け付けた（2xx）ときだけイベントとして記録する。
                    // 308 などで受け付けられなかった本文は、転送し直されたときに記録される（二重に数えない）
                    var accepted = response.Status is >= 200 and < 300;
                    if (isSocketIo && request.Method == "POST" && requestBody.Length > 0 && accepted)
                    {
                        WriteEntries(_decoder.DecodePolling(conn, clientAddress, sid, "c2s", RecordDecoder.BodyText(requestBody.ToArray())));
                    }

                    // WebSocket への切り替え
                    if (response.Status == 101 && request.IsWebSocketUpgrade)
                    {
                        await clientStream.WriteAsync(response.Raw, cancellationToken).ConfigureAwait(false);
                        if (isSocketIo)
                        {
                            _sink.WriteRaw(conn, "s2c", response.Raw);
                        }

                        _sink.Write(HttpEntry(conn, clientAddress, request, forwardedHead, 101, response.HeaderBlock, requestBytes, 0, null, stopwatch, blocked: false, injected), activity: true);
                        if (!longPoll)
                        {
                            _sink.Inflight(-1);
                            longPoll = true;
                        }

                        await TunnelAsync(conn, clientAddress, sid, isSocketIo, clientStream, clientReader.TakeBuffered(), upstreamStream, upstreamReader.TakeBuffered(), cancellationToken).ConfigureAwait(false);
                        closeReason = "websocket closed";
                        break;
                    }

                    var contentType = response.Header("Content-Type");
                    var responseFraming = HttpMessageReader.ResponseFraming(response, request.Method);
                    var cssRule = Rules.ForResponse(request.Method, path, contentType).FirstOrDefault(r => r.Action == "inject_css" && Fires(r));
                    var statusRule = Rules.ForResponse(request.Method, path, contentType).FirstOrDefault(r => r.Action == "status" && Fires(r));
                    var delayRule = Rules.ForResponse(request.Method, path, contentType).FirstOrDefault(r => r.Action == "delay" && Fires(r));
                    var responseBody = new MemoryStream();
                    long responseBytes = 0;
                    string? responseHash;
                    var needBuffer = isSocketIo || cssRule != null || statusRule != null || delayRule != null;
                    if (needBuffer)
                    {
                        var rawBody = new MemoryStream();
                        await upstreamReader.ReadBodyAsync(responseFraming, response.ContentLength, raw => { rawBody.Write(raw.Span); return Task.CompletedTask; }, d => responseBody.Write(d.Span), cancellationToken).ConfigureAwait(false);
                        responseBytes = responseBody.Length;
                        responseHash = Convert.ToHexStringLower(SHA256.HashData(responseBody.ToArray()));
                        if (isSocketIo && request.Method == "GET")
                        {
                            var action = await ApplyEventRulesAsync("s2c", RecordDecoder.BodyText(responseBody.ToArray()), cancellationToken).ConfigureAwait(false);
                            if (action == "drop_connection")
                            {
                                return;
                            }
                        }

                        if (delayRule != null)
                        {
                            injected = delayRule.Name;
                            await Task.Delay(delayRule.DelayMs, cancellationToken).ConfigureAwait(false);
                        }

                        if (statusRule != null)
                        {
                            injected = statusRule.Name;
                            var statusText = statusRule.Status.ToString(CultureInfo.InvariantCulture);
                            await clientStream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {statusText} Injected\r\nContent-Length: 0\r\n\r\n"), cancellationToken).ConfigureAwait(false);
                        }
                        else if (cssRule != null && (contentType ?? string.Empty).Contains("text/html", StringComparison.OrdinalIgnoreCase))
                        {
                            injected = cssRule.Name;
                            var (head, body) = InjectCss(response, responseBody.ToArray(), cssRule.Css ?? string.Empty);
                            await clientStream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
                            await clientStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await clientStream.WriteAsync(response.Raw, cancellationToken).ConfigureAwait(false);
                            await clientStream.WriteAsync(rawBody.ToArray(), cancellationToken).ConfigureAwait(false);
                        }

                        if (isSocketIo)
                        {
                            _sink.WriteRaw(conn, "s2c", response.Raw);
                            _sink.WriteRaw(conn, "s2c", rawBody.ToArray());
                        }
                    }
                    else
                    {
                        await clientStream.WriteAsync(response.Raw, cancellationToken).ConfigureAwait(false);
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        await upstreamReader.ReadBodyAsync(
                            responseFraming,
                            response.ContentLength,
                            raw => clientStream.WriteAsync(raw, cancellationToken).AsTask(),
                            decoded =>
                            {
                                responseBytes += decoded.Length;
                                hash.AppendData(decoded.Span);
                            },
                            cancellationToken).ConfigureAwait(false);
                        responseHash = Convert.ToHexStringLower(hash.GetHashAndReset());
                    }

                    var responseEntries = isSocketIo && request.Method == "GET" && accepted
                        ? _decoder.DecodePolling(conn, clientAddress, sid, "s2c", RecordDecoder.BodyText(responseBody.ToArray()))
                        : [];
                    var sidFromOpen = responseEntries.Select(e => e.Sid).FirstOrDefault(s => s != null);
                    _sink.Write(HttpEntry(conn, clientAddress, request, forwardedHead, response.Status, response.HeaderBlock, requestBytes, responseBytes, responseHash, stopwatch, blocked: false, injected, sid ?? sidFromOpen), activity: !longPoll || responseEntries.Any(IsActivity));
                    WriteEntries(responseEntries);
                    if (responseFraming == BodyFraming.UntilClose || request.WantsClose || response.WantsClose)
                    {
                        closeReason = "connection: close";
                        break;
                    }
                }
                finally
                {
                    if (!longPoll)
                    {
                        _sink.Inflight(-1);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            closeReason = e.GetType().Name + ": " + e.Message;
        }
        finally
        {
            upstream?.Dispose();
            client.Dispose();
            lock (_clientsGate)
            {
                _clients.Remove(client);
            }

            _sink.Connections(-1);
            _sink.CloseRaw(conn);
            _sink.Write(new RecordEntry { Kind = "conn", Event = "close", Conn = conn, Client = clientAddress, Error = closeReason }, activity: false);
        }
    }

    private static bool Fires(ProxyRule rule) => rule.Probability >= 1.0 || Random.Shared.NextDouble() < rule.Probability;

    private async Task<string?> ApplyEventRulesAsync(string dir, string body, CancellationToken cancellationToken)
    {
        if (!Rules.Rules.Any(r => r.EventName != null) || body.Length == 0)
        {
            return null;
        }

        var events = new RecordDecoder().DecodePolling(0, "-", "rule", dir, body).Select(e => e.EventName).Where(n => n != null).ToList();
        foreach (var name in events)
        {
            foreach (var rule in Rules.ForEvent(dir, name))
            {
                if (!Fires(rule))
                {
                    continue;
                }

                if (rule.Action == "delay")
                {
                    await Task.Delay(rule.DelayMs, cancellationToken).ConfigureAwait(false);
                }
                else if (rule.Action == "drop_connection")
                {
                    _sink.Write(new RecordEntry { Kind = "conn", Event = "drop", Dir = dir, EventName = name, Client = "-", Injected = rule.Name }, activity: true);
                    return "drop_connection";
                }
            }
        }

        return null;
    }

    private async Task TunnelAsync(
        long conn,
        string clientAddress,
        string? initialSid,
        bool isSocketIo,
        NetworkStream clientStream,
        ReadOnlyMemory<byte> clientBuffered,
        NetworkStream upstreamStream,
        ReadOnlyMemory<byte> upstreamBuffered,
        CancellationToken cancellationToken)
    {
        _sink.WebSockets(1);
        var sidHolder = new string?[] { initialSid };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var c2s = PumpAsync(conn, clientAddress, sidHolder, isSocketIo, "c2s", clientStream, clientBuffered, upstreamStream, linked);
        var s2c = PumpAsync(conn, clientAddress, sidHolder, isSocketIo, "s2c", upstreamStream, upstreamBuffered, clientStream, linked);
        await Task.WhenAny(c2s, s2c).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(c2s, s2c).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }

        _sink.WebSockets(-1);
        if (sidHolder[0] is { } sid)
        {
            _decoder.Forget(sid);
        }
    }

    private async Task PumpAsync(
        long conn,
        string clientAddress,
        string?[] sidHolder,
        bool isSocketIo,
        string dir,
        NetworkStream from,
        ReadOnlyMemory<byte> buffered,
        NetworkStream to,
        CancellationTokenSource linked)
    {
        var parser = new WebSocketFrameParser();
        var buffer = new byte[64 * 1024];
        var pending = buffered;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                ReadOnlyMemory<byte> chunk;
                if (pending.Length > 0)
                {
                    chunk = pending;
                    pending = ReadOnlyMemory<byte>.Empty;
                }
                else
                {
                    var read = await from.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        return;
                    }

                    chunk = buffer.AsMemory(0, read).ToArray();
                }

                foreach (var message in parser.Feed(chunk.Span))
                {
                    var entries = isSocketIo ? _decoder.DecodeWebSocket(conn, clientAddress, sidHolder[0], dir, message) : [];
                    if (sidHolder[0] == null)
                    {
                        foreach (var entry in entries.Where(e => e.Eio == "open" && e.EioData != null))
                        {
                            sidHolder[0] = RecordDecoder.SidFromOpen(new EngineIoPacket(EngineIoPacketType.Open, entry.EioData, null));
                            entry.Sid = sidHolder[0];
                        }
                    }

                    // イベント単位の規則（遅延、切断）
                    foreach (var name in entries.Select(e => e.EventName).Where(n => n != null))
                    {
                        foreach (var rule in Rules.ForEvent(dir, name))
                        {
                            if (!Fires(rule))
                            {
                                continue;
                            }

                            if (rule.Action == "delay")
                            {
                                await Task.Delay(rule.DelayMs, linked.Token).ConfigureAwait(false);
                            }
                            else if (rule.Action == "drop_connection")
                            {
                                _sink.Write(new RecordEntry { Kind = "conn", Event = "drop", Conn = conn, Dir = dir, EventName = name, Client = clientAddress, Injected = rule.Name }, activity: true);
                                return;
                            }
                        }
                    }

                    foreach (var frame in message.RawFrames)
                    {
                        await to.WriteAsync(frame, linked.Token).ConfigureAwait(false);
                        if (isSocketIo)
                        {
                            _sink.WriteRaw(conn, dir, frame);
                        }
                    }

                    WriteEntries(entries);
                    if (message.IsClose)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private void WriteEntries(IEnumerable<RecordEntry> entries)
    {
        foreach (var entry in entries)
        {
            _sink.Write(entry, IsActivity(entry));
        }
    }

    /// <summary>ping/pong と probe は「動き」に数えない（完了待ちのため）。</summary>
    private static bool IsActivity(RecordEntry entry) =>
        entry.Kind != "eio" || entry.Eio is not ("ping" or "pong" or "noop");

    private static RecordEntry HttpEntry(
        long conn,
        string client,
        HttpHead request,
        byte[]? forwardedHead,
        int status,
        string responseHeaders,
        long requestBytes,
        long responseBytes,
        string? responseHash,
        Stopwatch stopwatch,
        bool blocked,
        string? injected = null,
        string? sid = null) => new()
        {
            Kind = "http",
            Conn = conn,
            Client = client,
            Sid = sid ?? request.Query("sid"),
            Transport = request.Query("transport"),
            Method = request.Method,
            Target = request.Target,
            Status = status,
            RequestHeaders = request.HeaderBlock,
            ForwardedRequestHeaders = forwardedHead == null ? null : HttpMessageReader.Parse(forwardedHead, false).HeaderBlock,
            ResponseHeaders = responseHeaders,
            RequestBytes = requestBytes,
            ResponseBytes = responseBytes,
            ResponseSha256 = responseHash,
            Blocked = blocked ? true : null,
            Injected = injected,
            DurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
        };

    /// <summary>わざと壊す確認（E-4）のため、転送する要求にヘッダーを足す。</summary>
    private static byte[] AddHeader(byte[] head, string name, string value)
    {
        var text = Encoding.Latin1.GetString(head);
        var index = text.LastIndexOf("\r\n\r\n", StringComparison.Ordinal);
        var modified = text[..index] + $"\r\n{name}: {value}" + text[index..];
        return Encoding.Latin1.GetBytes(modified);
    }

    /// <summary>HTML の応答に CSS を差し込む（レイアウト崩れの注入。設計書 第19.4節）。</summary>
    private static (byte[] Head, byte[] Body) InjectCss(HttpHead response, byte[] body, string css)
    {
        var encoding = response.Header("Content-Encoding")?.Trim().ToLowerInvariant();
        byte[] plain = encoding switch
        {
            "gzip" => Decompress(new GZipStream(new MemoryStream(body), CompressionMode.Decompress)),
            "br" => Decompress(new BrotliStream(new MemoryStream(body), CompressionMode.Decompress)),
            "deflate" => Decompress(new ZLibStream(new MemoryStream(body), CompressionMode.Decompress)),
            _ => body,
        };
        var html = Encoding.UTF8.GetString(plain);
        var style = $"<style>{css}</style>";
        var at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        html = at >= 0 ? html.Insert(at, style) : style + html;
        var newBody = Encoding.UTF8.GetBytes(html);
        var lines = new List<string> { response.StartLine };
        foreach (var header in response.Headers)
        {
            if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add($"{header.Name}: {header.Value}");
        }

        lines.Add("Content-Length: " + newBody.Length.ToString(CultureInfo.InvariantCulture));
        var head = Encoding.Latin1.GetBytes(string.Join("\r\n", lines) + "\r\n\r\n");
        return (head, newBody);
    }

    private static byte[] Decompress(Stream stream)
    {
        using (stream)
        {
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _rules?.Dispose();
        _sink.Dispose();
    }
}
