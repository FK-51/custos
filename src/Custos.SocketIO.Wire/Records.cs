using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Custos.SocketIO.Wire;

/// <summary>
/// 通信の記録の1件（docs/architecture.md 5.1）。kind は conn / http / eio / ws。
/// 記録は管理側だけが読む。利用者側のコンテナには見せない。
/// </summary>
public sealed class RecordEntry
{
    /// <summary>通し番号。</summary>
    public long Seq { get; set; }

    /// <summary>時刻（UTC）。</summary>
    public DateTimeOffset Ts { get; set; }

    /// <summary>種類：conn、http、eio、ws。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>接続の識別子（プロキシが振る番号）。</summary>
    public long Conn { get; set; }

    /// <summary>どのクライアントか（送信元のアドレス）。</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>Engine.IO のセッション（sid）。</summary>
    public string? Sid { get; set; }

    /// <summary>polling / websocket。</summary>
    public string? Transport { get; set; }

    /// <summary>向き：c2s（クライアントからサーバー）、s2c（サーバーからクライアント）。</summary>
    public string? Dir { get; set; }

    /// <summary>conn の出来事（open / close）。</summary>
    public string? Event { get; set; }

    /// <summary>HTTP のメソッド。</summary>
    public string? Method { get; set; }

    /// <summary>HTTP の要求の対象（パスとクエリ）。</summary>
    public string? Target { get; set; }

    /// <summary>HTTP の状態。</summary>
    public int? Status { get; set; }

    /// <summary>クライアントから受け取った要求のヘッダー（受け取ったまま）。</summary>
    public string? RequestHeaders { get; set; }

    /// <summary>アプリへ転送した要求のヘッダー。E-4 の照合に使う（受け取ったものと同じでなければならない）。</summary>
    public string? ForwardedRequestHeaders { get; set; }

    /// <summary>応答のヘッダー。</summary>
    public string? ResponseHeaders { get; set; }

    /// <summary>要求の本文の大きさ。</summary>
    public long? RequestBytes { get; set; }

    /// <summary>応答の本文の大きさ。</summary>
    public long? ResponseBytes { get; set; }

    /// <summary>応答の本文のハッシュ（Socket.IO 以外の通信は本文を残さず、ハッシュだけを残す）。</summary>
    public string? ResponseSha256 { get; set; }

    /// <summary>プロキシで遮断したか（/__verify/* など）。</summary>
    public bool? Blocked { get; set; }

    /// <summary>プロキシで注入した異常の名前。</summary>
    public string? Injected { get; set; }

    /// <summary>要求から応答の終わりまでの時間。</summary>
    public double? DurationMs { get; set; }

    /// <summary>Engine.IO のパケットの種類。</summary>
    public string? Eio { get; set; }

    /// <summary>Engine.IO のパケットの中身（message 以外：open の JSON、ping の probe など）。</summary>
    public string? EioData { get; set; }

    /// <summary>Socket.IO のパケットの種類。</summary>
    public string? Sio { get; set; }

    /// <summary>名前空間。</summary>
    public string? Nsp { get; set; }

    /// <summary>イベント名（EVENT のとき）。</summary>
    [JsonPropertyName("event_name")]
    public string? EventName { get; set; }

    /// <summary>引数（EVENT はイベント名を除いたもの、ACK は全体）。バイナリは {"_binary_b64": ...}。</summary>
    public JsonNode? Args { get; set; }

    /// <summary>確認応答の ID。</summary>
    public long? Ack { get; set; }

    /// <summary>バイナリの添付の数。</summary>
    public int? Attachments { get; set; }

    /// <summary>公式の実装が受け付ける形か。</summary>
    public bool? Valid { get; set; }

    /// <summary>読み解けなかった理由。</summary>
    public string? Error { get; set; }

    /// <summary>読み解く前の生の文字列（バイナリは base64）。</summary>
    public string? Raw { get; set; }

    /// <summary>WebSocket のフレームの種類（ws の記録）。</summary>
    public string? Opcode { get; set; }

    /// <summary>WebSocket のメッセージの大きさ。</summary>
    public int? Bytes { get; set; }
}

/// <summary>記録の JSON の書き方。</summary>
public static class RecordJson
{
    /// <summary>1行1件で書く設定。</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>1件を1行にする。</summary>
    public static string Serialize(RecordEntry entry) => JsonSerializer.Serialize(entry, Options);

    /// <summary>1行を読む。</summary>
    public static RecordEntry? Deserialize(string line) => JsonSerializer.Deserialize<RecordEntry>(line, Options);
}

/// <summary>
/// HTTP のポーリングと WebSocket の通信を、Engine.IO と Socket.IO のパケットに読み解き、記録の行にする。
/// 接続（sid）と向きごとに、バイナリの添付を待つ状態を持つ。
/// </summary>
public sealed class RecordDecoder
{
    private readonly ConcurrentDictionary<(string Sid, string Dir), SocketIoStreamDecoder> _decoders = new();

    /// <summary>ポーリングの本文（要求なら c2s、応答なら s2c）を読み解く。</summary>
    public IReadOnlyList<RecordEntry> DecodePolling(long conn, string client, string? sid, string dir, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var entries = new List<RecordEntry>();
        if (body.Length == 0)
        {
            return entries;
        }

        foreach (var packet in EngineIo.DecodePayload(body))
        {
            var effectiveSid = sid ?? SidFromOpen(packet);
            entries.AddRange(DecodeEngineIo(conn, client, effectiveSid, "polling", dir, packet, packet.Binary == null ? EncodeText(packet) : null));
        }

        return entries;
    }

    /// <summary>WebSocket のメッセージを読み解く。</summary>
    public IReadOnlyList<RecordEntry> DecodeWebSocket(long conn, string client, string? sid, string dir, WebSocketMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Compressed)
        {
            return
            [
                new RecordEntry
                {
                    Kind = "ws", Conn = conn, Client = client, Sid = sid, Transport = "websocket", Dir = dir,
                    Opcode = message.Kind, Bytes = message.Payload.Length, Valid = false,
                    Error = "permessage-deflate で圧縮されたメッセージは読み解いていない", Raw = Convert.ToBase64String(message.Payload),
                },
            ];
        }

        if (message.IsText)
        {
            var text = message.Text;
            return DecodeEngineIo(conn, client, sid, "websocket", dir, EngineIo.DecodeText(text), text);
        }

        if (message.IsBinary)
        {
            return DecodeEngineIo(conn, client, sid, "websocket", dir, EngineIo.DecodeBinary(message.Payload), null);
        }

        return
        [
            new RecordEntry
            {
                Kind = "ws", Conn = conn, Client = client, Sid = sid, Transport = "websocket", Dir = dir,
                Opcode = message.Kind, Bytes = message.Payload.Length, Raw = Convert.ToBase64String(message.Payload),
            },
        ];
    }

    private static string EncodeText(EngineIoPacket packet) => EngineIo.Encode(packet, supportsBinary: false).Text ?? string.Empty;

    /// <summary>open のパケットから sid を取り出す。</summary>
    public static string? SidFromOpen(EngineIoPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != EngineIoPacketType.Open || packet.Text == null)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(packet.Text);
            return node?["sid"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private List<RecordEntry> DecodeEngineIo(long conn, string client, string? sid, string transport, string dir, EngineIoPacket packet, string? raw)
    {
        var entry = new RecordEntry
        {
            Kind = "eio",
            Conn = conn,
            Client = client,
            Sid = sid,
            Transport = transport,
            Dir = dir,
            Eio = packet.TypeName,
            Valid = packet.Valid ? null : false,
            Error = packet.Error,
            Raw = raw ?? (packet.Binary != null ? Convert.ToBase64String(packet.Binary) : null),
        };

        if (packet.Type != EngineIoPacketType.Message || !packet.Valid)
        {
            entry.EioData = packet.Text;
            return [entry];
        }

        var decoder = _decoders.GetOrAdd((sid ?? "?", dir), _ => new SocketIoStreamDecoder());
        SocketIoPacket? sio;
        if (packet.Binary != null)
        {
            entry.Bytes = packet.Binary.Length;
            sio = decoder.OnBinary(packet.Binary);
        }
        else
        {
            sio = decoder.OnText(packet.Text ?? string.Empty);
        }

        if (sio == null)
        {
            // バイナリの添付を待っている（完成したときに、まとめて1件にする）
            entry.Sio = "pending";
            return [entry];
        }

        entry.Sio = sio.TypeName;
        entry.Nsp = sio.Nsp;
        entry.EventName = sio.EventName;
        entry.Args = sio.Type is SocketIoPacketType.Event or SocketIoPacketType.BinaryEvent or SocketIoPacketType.Ack or SocketIoPacketType.BinaryAck
            ? sio.Arguments
            : sio.Data?.DeepClone();
        entry.Ack = sio.Id;
        entry.Attachments = sio.Attachments > 0 ? sio.Attachments : null;
        if (!sio.Valid)
        {
            entry.Valid = false;
            entry.Error = sio.Error;
            if (sio.Data is JsonValue)
            {
                entry.Args = sio.Data.DeepClone();
            }
        }

        return [entry];
    }

    /// <summary>接続が終わったときに、その sid の状態を捨てる。</summary>
    public void Forget(string sid)
    {
        _decoders.TryRemove((sid, "c2s"), out _);
        _decoders.TryRemove((sid, "s2c"), out _);
    }

    /// <summary>本文のバイト列を UTF-8 の文字列にする。</summary>
    public static string BodyText(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(body);
}
