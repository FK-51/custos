using System.Net.WebSockets;
using SocketIOClient.Protocol.WebSocket;

namespace Custos.Agent.Client;

/// <summary>
/// Socket.IO のパス（例：/socket.io/ と /socket.io）。コミュニティ製のクライアント（SocketIOClient）は、
/// パスの後ろに必ず「/」を付けて要求する。対象アプリの公式のクライアントが「/」を付けない（addTrailingSlash: false）場合に
/// 同じ要求を作れるよう、要求の直前でパスを書き換える。
/// </summary>
public sealed record SocketPath(string Path)
{
    /// <summary>末尾の「/」を除いたパス。</summary>
    public string Base => Path.TrimEnd('/');

    /// <summary>末尾に「/」を付けるか。</summary>
    public bool TrailingSlash => Path.EndsWith('/');

    /// <summary>SocketIOClient が作った URL を、対象アプリのクライアントと同じ形にする。</summary>
    public Uri Rewrite(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (TrailingSlash || uri.AbsolutePath != Base + "/")
        {
            return uri;
        }

        var builder = new UriBuilder(uri) { Path = Base };
        return builder.Uri;
    }

    /// <summary>Engine.IO の要求の URL（生のクライアント用）。</summary>
    public string Target(string query) => (TrailingSlash ? Base + "/" : Base) + "?" + query;
}

/// <summary>要求の URL を書き換える HTTP のハンドラ（ポーリング）。</summary>
public sealed class SocketPathHandler : DelegatingHandler
{
    private readonly SocketPath _path;

    /// <summary>作る。</summary>
    public SocketPathHandler(SocketPath path)
        : base(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        _path = path;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri != null)
        {
            request.RequestUri = _path.Rewrite(request.RequestUri);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>要求の URL を書き換える WebSocket のクライアント。</summary>
public sealed class SocketPathWebSocketClient : IWebSocketClient, IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SocketPath _path;

    /// <summary>作る。</summary>
    public SocketPathWebSocketClient(SocketPath path)
    {
        _path = path;
        _socket.Options.Proxy = null;
    }

    /// <inheritdoc />
    public WebSocketState State => _socket.State;

    /// <inheritdoc />
    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => _socket.ConnectAsync(_path.Rewrite(uri), cancellationToken);

    /// <inheritdoc />
    public Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    /// <inheritdoc />
    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => _socket.ReceiveAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public Task CloseAsync(WebSocketCloseStatus closeStatus, string desc, CancellationToken cancellationToken) => _socket.CloseAsync(closeStatus, desc, cancellationToken);

    /// <inheritdoc />
    public void SetDefaultHeader(string name, string value) => _socket.Options.SetRequestHeader(name, value);

    /// <inheritdoc />
    public void Dispose() => _socket.Dispose();
}
