using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Agent.Common;
using Custos.SocketIO.Wire;
using SocketIOClient;
using Microsoft.Extensions.DependencyInjection;
using SocketIOClient.Common;

namespace Custos.Agent.Client;

/// <summary>受け取ったイベント1件。</summary>
public sealed record ReceivedEvent(long Seq, DateTimeOffset Ts, string Event, JsonArray Args)
{
    /// <summary>JSON にする。</summary>
    public JsonObject ToJson() => new()
    {
        ["seq"] = Seq,
        ["ts"] = Ts.ToString("O", CultureInfo.InvariantCulture),
        ["event"] = Event,
        ["args"] = Args.DeepClone(),
    };
}

/// <summary>
/// 1人の利用者の接続（SocketIOClient）。受け取ったイベントをすべて記録し、待ち合わせに使う。
/// 通信は通常のブラウザと見分けがつかないよう、ブラウザと同じヘッダーを付ける（指示書 制約5）。
/// </summary>
public sealed class UserSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<ReceivedEvent> _events = [];
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SocketIOClient.SocketIO? _socket;
    private long _seq;

    /// <summary>利用者の名前（エージェント内の呼び名）。</summary>
    public UserSession(string name)
    {
        Name = name;
    }

    /// <summary>呼び名。</summary>
    public string Name { get; }

    /// <summary>接続中か。</summary>
    public bool Connected => _socket?.Connected == true;

    /// <summary>Socket.IO の socket.id。</summary>
    public string? SocketId => _socket?.Id;

    /// <summary>覚えておく値（playerId、再接続トークン、部屋のIDなど）。</summary>
    public Dictionary<string, string> Memory { get; } = new(StringComparer.Ordinal);

    /// <summary>切断の理由（最後のもの）。</summary>
    public string? LastDisconnectReason { get; private set; }

    /// <summary>接続する。</summary>
    public async Task ConnectAsync(Uri url, string transport, IReadOnlyDictionary<string, string> headers, NameValueCollection? query, TimeSpan timeout, CancellationToken cancellationToken, SocketPath? path = null)
    {
        var socketPath = path ?? new SocketPath("/socket.io/");
        if (_socket != null)
        {
            await DisposeSocketAsync().ConfigureAwait(false);
        }

        var options = new SocketIOOptions
        {
            EIO = EngineIO.V4,
            Path = socketPath.Base,
            Reconnection = false,
            ConnectionTimeout = timeout,
            Transport = transport == "websocket" ? TransportProtocol.WebSocket : TransportProtocol.Polling,
            AutoUpgrade = transport == "upgrade",
            ExtraHeaders = headers,
            Query = query,
        };
        var socket = new SocketIOClient.SocketIO(url, options, services =>
        {
            // 対象アプリのクライアントと同じパスで要求する（末尾の「/」の有無）
            services.AddSingleton(_ => new HttpClient(new SocketPathHandler(socketPath)));
            services.AddTransient<SocketIOClient.Protocol.WebSocket.IWebSocketClient>(_ => new SocketPathWebSocketClient(socketPath));
        });
        socket.OnAny((name, context) =>
        {
            Record(name, context.RawText);
            return Task.CompletedTask;
        });
        socket.OnDisconnected += (_, reason) =>
        {
            LastDisconnectReason = reason;
            Record("__disconnected", JsonSerializer.Serialize(new[] { "__disconnected", reason }));
        };
        _socket = socket;
        await socket.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Record(string name, string? rawText)
    {
        var args = new JsonArray();
        if (rawText != null && JsJson.TryParse(rawText, out var node) && node is JsonArray array)
        {
            for (var i = 1; i < array.Count; i++)
            {
                args.Add(array[i]?.DeepClone());
            }
        }

        TaskCompletionSource arrived;
        lock (_gate)
        {
            _events.Add(new ReceivedEvent(++_seq, DateTimeOffset.UtcNow, name, args));
            arrived = _next;
            _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _signal.Release();
        arrived.TrySetResult();
    }

    /// <summary>
    /// 次のイベントが届いたら完了するタスク。取り出した後に届いたイベントで完了する（取り出す前に届いたものでは完了しない）。
    /// ボットは、受け取ったイベントを調べる前にこれを取っておき、何もなければ待つ（一定時間ごとに見に行くと、その分だけ返答が遅れる）。
    /// _signal（WaitForAsync が数を消費する）とは別にしてあり、待つ人どうしで知らせを取り合わない。
    /// </summary>
    public Task NextEvent
    {
        get
        {
            lock (_gate)
            {
                return _next.Task;
            }
        }
    }

    /// <summary>イベントを送る。ack が true なら確認応答を待って、その値を返す。</summary>
    public async Task<JsonArray?> EmitAsync(string eventName, JsonArray args, bool ack, TimeSpan timeout, CancellationToken cancellationToken)
    {
        LastEmitSeq = LastSeq;
        var socket = _socket ?? throw new RpcException($"{Name} は接続していません");

        // JSON の null も「値」として送れるよう、null は JsonElement の null にする
        var values = args.Select(a => a == null ? (object)NullElement : a.DeepClone()).ToList();
        if (!ack)
        {
            await socket.EmitAsync(eventName, values, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var completion = new TaskCompletionSource<JsonArray>(TaskCreationOptions.RunContinuationsAsynchronously);
        await socket.EmitAsync(
            eventName,
            values,
            message =>
            {
                var result = new JsonArray();
                if (message.RawText != null && JsJson.TryParse(message.RawText, out var node) && node is JsonArray array)
                {
                    foreach (var item in array)
                    {
                        result.Add(item?.DeepClone());
                    }
                }

                completion.TrySetResult(result);
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await completion.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{eventName} の確認応答が {timeout.TotalMilliseconds} ms 以内に来ませんでした");
        }
    }

    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>最後に送ったときに、受け取り済みだったイベントの番号（それより後の選択の要求は、まだ答えていない）。</summary>
    public long LastEmitSeq { get; private set; }

    /// <summary>受け取ったイベント（since より後）。</summary>
    public IReadOnlyList<ReceivedEvent> EventsSince(long since)
    {
        lock (_gate)
        {
            return _events.Where(e => e.Seq > since).ToList();
        }
    }

    /// <summary>最後の seq。</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>条件に合うイベントが来るまで待つ（since より後のもの）。</summary>
    public async Task<ReceivedEvent?> WaitForAsync(Func<ReceivedEvent, bool> predicate, long since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var cursor = since;
        while (true)
        {
            foreach (var e in EventsSince(cursor))
            {
                cursor = e.Seq;
                if (predicate(e))
                {
                    return e;
                }
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await _signal.WaitAsync(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>切断する。</summary>
    public async Task DisconnectAsync()
    {
        if (_socket != null)
        {
            await _socket.DisconnectAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeSocketAsync()
    {
        if (_socket != null)
        {
            try
            {
                if (_socket.Connected)
                {
                    await _socket.DisconnectAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
            {
            }

            _socket.Dispose();
            _socket = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeSocketAsync().ConfigureAwait(false);
        _signal.Dispose();
    }
}
