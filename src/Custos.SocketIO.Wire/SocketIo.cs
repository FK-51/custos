using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Custos.SocketIO.Wire;

/// <summary>Socket.IO v5 のパケットの種類（socket.io-parser 4.x）。</summary>
public enum SocketIoPacketType
{
    /// <summary>CONNECT（0）。</summary>
    Connect = 0,

    /// <summary>DISCONNECT（1）。</summary>
    Disconnect = 1,

    /// <summary>EVENT（2）。</summary>
    Event = 2,

    /// <summary>ACK（3）。</summary>
    Ack = 3,

    /// <summary>CONNECT_ERROR（4）。</summary>
    ConnectError = 4,

    /// <summary>BINARY_EVENT（5）。</summary>
    BinaryEvent = 5,

    /// <summary>BINARY_ACK（6）。</summary>
    BinaryAck = 6,
}

/// <summary>
/// Socket.IO のパケット。Data のバイナリは {"_binary_b64": "..."} で表す（正解のデータの形と同じ）。
/// Valid が false のものは、公式の実装が拒否するもの（攻撃の記録として残す）。
/// </summary>
public sealed record SocketIoPacket(
    SocketIoPacketType Type,
    string Nsp,
    long? Id,
    JsonNode? Data,
    int Attachments,
    bool Valid = true,
    string? Error = null)
{
    /// <summary>種類の名前（connect、disconnect、event、ack、connect_error、binary_event、binary_ack）。</summary>
    public string TypeName => SocketIo.TypeName(Type);

    /// <summary>イベント名（EVENT のとき、配列の先頭が文字列なら）。</summary>
    public string? EventName =>
        Type is SocketIoPacketType.Event or SocketIoPacketType.BinaryEvent && Data is JsonArray array && array.Count > 0 && array[0] is JsonValue value && value.TryGetValue<string>(out var name)
            ? name
            : null;

    /// <summary>イベントの引数（イベント名を除いた残り）。ACK のときは配列全体。</summary>
    public JsonArray Arguments
    {
        get
        {
            var result = new JsonArray();
            if (Data is not JsonArray array)
            {
                return result;
            }

            var skip = Type is SocketIoPacketType.Event or SocketIoPacketType.BinaryEvent ? 1 : 0;
            for (var i = skip; i < array.Count; i++)
            {
                result.Add(array[i]?.DeepClone());
            }

            return result;
        }
    }
}

/// <summary>Socket.IO v5 の符号化と読み解き。</summary>
public static class SocketIo
{
    private static readonly string[] ReservedEvents = ["connect", "connect_error", "disconnect", "disconnecting", "newListener", "removeListener"];

    /// <summary>バイナリを表すキー。</summary>
    public const string BinaryKey = "_binary_b64";

    /// <summary>種類の名前。</summary>
    public static string TypeName(SocketIoPacketType type) => type switch
    {
        SocketIoPacketType.Connect => "connect",
        SocketIoPacketType.Disconnect => "disconnect",
        SocketIoPacketType.Event => "event",
        SocketIoPacketType.Ack => "ack",
        SocketIoPacketType.ConnectError => "connect_error",
        SocketIoPacketType.BinaryEvent => "binary_event",
        _ => "binary_ack",
    };

    /// <summary>
    /// パケットの頭の文字列を読み解く（バイナリの添付はまだ差し込まない）。
    /// socket.io-parser 4.2.x の Decoder.decodeString を、JavaScript の緩い比較（Number(c) == c）まで含めて写したもの。
    /// 公式が例外にするものは Valid=false にして、読めたところまでを残す（攻撃の記録として使う）。
    /// </summary>
    public static SocketIoPacket DecodeString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var i = 0;
        var typeChar = CharAt(text, 0);
        var typeNumber = JsNumber(typeChar);
        if (typeNumber is not { } typeValue || typeValue is < 0 or > 6)
        {
            return Invalid(SocketIoPacketType.Event, text, "unknown packet type " + (typeNumber?.ToString(CultureInfo.InvariantCulture) ?? "NaN"));
        }

        var type = (SocketIoPacketType)typeValue;
        var attachments = 0;
        if (type is SocketIoPacketType.BinaryEvent or SocketIoPacketType.BinaryAck)
        {
            var start = i + 1;
            while (CharAt(text, ++i) != "-" && i != text.Length)
            {
            }

            var buf = Substring(text, start, i);
            var number = JsNumber(buf);
            if (number is not { } count || !JsLooseEquals(buf, count) || CharAt(text, i) != "-")
            {
                return Invalid(type, text, "Illegal attachments");
            }

            attachments = (int)count;
        }

        var nsp = "/";
        if (CharAt(text, i + 1) == "/")
        {
            var start = i + 1;
            while (++i > 0)
            {
                var c = CharAt(text, i);
                if (c == ",")
                {
                    break;
                }

                if (i == text.Length)
                {
                    break;
                }
            }

            nsp = Substring(text, start, i);
        }

        long? id = null;
        var next = CharAt(text, i + 1);
        if (next.Length > 0 && JsNumber(next) is { } nextNumber && JsLooseEquals(next, nextNumber))
        {
            var start = i + 1;
            while (++i > 0)
            {
                var c = CharAt(text, i);
                if (JsNumber(c) is not { } cn || !JsLooseEquals(c, cn))
                {
                    --i;
                    break;
                }

                if (i == text.Length)
                {
                    break;
                }
            }

            var digits = Substring(text, start, i + 1);
            if (JsNumber(digits) is { } idValue && idValue >= 0 && idValue <= long.MaxValue && Math.Floor(idValue) == idValue)
            {
                id = (long)idValue;
            }
        }

        JsonNode? data = null;
        var hasData = false;
        if (CharAt(text, ++i).Length > 0)
        {
            var json = text[i..];
            if (!JsJson.TryParse(json, out data))
            {
                return new SocketIoPacket(type, nsp, id, JsonValue.Create(json), attachments, false, "invalid payload");
            }

            hasData = true;

            if (!IsPayloadValid(type, data, hasData))
            {
                return new SocketIoPacket(type, nsp, id, data, attachments, false, "invalid payload");
            }
        }

        return new SocketIoPacket(type, nsp, id, data, attachments);
    }

    private static string CharAt(string text, int index) =>
        index >= 0 && index < text.Length ? text[index].ToString() : string.Empty;

    private static string Substring(string text, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        return text[start..end];
    }

    /// <summary>JavaScript の Number(s)。数値にならなければ null（NaN）。</summary>
    private static double? JsNumber(string s)
    {
        var trimmed = s.Trim(JsWhitespace);
        if (trimmed.Length == 0)
        {
            return 0;
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }

        if (trimmed is "Infinity" or "+Infinity")
        {
            return double.PositiveInfinity;
        }

        if (trimmed == "-Infinity")
        {
            return double.NegativeInfinity;
        }

        foreach (var c in trimmed)
        {
            if (!(char.IsAsciiDigit(c) || c is '.' or 'e' or 'E' or '+' or '-'))
            {
                return null;
            }
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>JavaScript の緩い比較 number == string（文字列を数値に変えて比べる）。</summary>
    private static bool JsLooseEquals(string s, double number) => JsNumber(s) is { } converted && converted.Equals(number);

    private static readonly char[] JsWhitespace =
    [
        ' ', '\t', '\n', '\v', '\f', '\r', '\u00a0', '\ufeff', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003',
        '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000',
    ];

    private static SocketIoPacket Invalid(SocketIoPacketType type, string text, string error) =>
        new(type, "/", null, JsonValue.Create(text), 0, false, error);

    /// <summary>socket.io-parser の isPayloadValid と同じ規則。</summary>
    public static bool IsPayloadValid(SocketIoPacketType type, JsonNode? payload, bool hasData)
    {
        switch (type)
        {
            case SocketIoPacketType.Connect:
                return !hasData || payload is JsonObject;
            case SocketIoPacketType.Disconnect:
                return !hasData;
            case SocketIoPacketType.ConnectError:
                return payload is JsonObject || (payload is JsonValue v && v.GetValueKind() == JsonValueKind.String);
            case SocketIoPacketType.Event:
            case SocketIoPacketType.BinaryEvent:
                if (payload is not JsonArray array || array.Count == 0)
                {
                    return false;
                }

                if (array[0] is JsonValue first)
                {
                    if (first.GetValueKind() == JsonValueKind.Number)
                    {
                        return true;
                    }

                    if (first.GetValueKind() == JsonValueKind.String && first.TryGetValue<string>(out var name))
                    {
                        return !ReservedEvents.Contains(name, StringComparer.Ordinal);
                    }
                }

                return false;
            default:
                return payload is JsonArray;
        }
    }

    /// <summary>添付のバイナリを、プレースホルダーの位置に差し込む。</summary>
    public static JsonNode? Reconstruct(JsonNode? data, IReadOnlyList<byte[]> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        switch (data)
        {
            case JsonObject obj when obj.TryGetPropertyValue("_placeholder", out var placeholder)
                && placeholder is JsonValue pv && pv.GetValueKind() == JsonValueKind.True
                && obj.TryGetPropertyValue("num", out var numNode) && numNode is JsonValue nv && nv.TryGetValue<int>(out var num):
                if (num >= 0 && num < attachments.Count)
                {
                    return new JsonObject { [BinaryKey] = Convert.ToBase64String(attachments[num]) };
                }

                return data.DeepClone();
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var pair in obj)
                {
                    copy[pair.Key] = Reconstruct(pair.Value, attachments);
                }

                return copy;
            case JsonArray array:
                var list = new JsonArray();
                foreach (var item in array)
                {
                    list.Add(Reconstruct(item, attachments));
                }

                return list;
            default:
                return data?.DeepClone();
        }
    }

    /// <summary>
    /// パケットを符号化する。Data の中の {"_binary_b64": ...} はバイナリの添付にし、プレースホルダーに置き換える。
    /// 返り値の先頭が文字列のパケット、残りが添付のバイナリ。
    /// </summary>
    public static (string Header, IReadOnlyList<byte[]> Attachments) Encode(SocketIoPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var attachments = new List<byte[]>();
        var data = Deconstruct(packet.Data, attachments);
        var type = packet.Type;
        if (attachments.Count > 0)
        {
            type = type switch
            {
                SocketIoPacketType.Event => SocketIoPacketType.BinaryEvent,
                SocketIoPacketType.Ack => SocketIoPacketType.BinaryAck,
                _ => type,
            };
        }

        var builder = new StringBuilder();
        builder.Append((int)type);
        if (type is SocketIoPacketType.BinaryEvent or SocketIoPacketType.BinaryAck)
        {
            builder.Append(attachments.Count).Append('-');
        }

        if (!string.IsNullOrEmpty(packet.Nsp) && packet.Nsp != "/")
        {
            builder.Append(packet.Nsp).Append(',');
        }

        if (packet.Id is { } id)
        {
            builder.Append(id.ToString(CultureInfo.InvariantCulture));
        }

        if (data != null)
        {
            builder.Append(JsJson.Stringify(data));
        }

        return (builder.ToString(), attachments);
    }

    private static JsonNode? Deconstruct(JsonNode? data, List<byte[]> attachments)
    {
        switch (data)
        {
            case JsonObject obj when obj.Count == 1 && obj.TryGetPropertyValue(BinaryKey, out var b64) && b64 is JsonValue value && value.TryGetValue<string>(out var text):
                attachments.Add(Convert.FromBase64String(text));
                return new JsonObject { ["_placeholder"] = true, ["num"] = attachments.Count - 1 };
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var pair in obj)
                {
                    copy[pair.Key] = Deconstruct(pair.Value, attachments);
                }

                return copy;
            case JsonArray array:
                var list = new JsonArray();
                foreach (var item in array)
                {
                    list.Add(Deconstruct(item, attachments));
                }

                return list;
            default:
                return data?.DeepClone();
        }
    }
}

/// <summary>
/// 一方向の Engine.IO のメッセージの流れから、Socket.IO のパケットを組み立てる（バイナリの添付を待つ）。
/// 接続（sid）と向きごとに1つ持つ。
/// </summary>
public sealed class SocketIoStreamDecoder
{
    private SocketIoPacket? _pending;
    private readonly List<byte[]> _attachments = [];

    /// <summary>テキストのメッセージを渡す。完成したパケットを返す（添付待ちなら null）。</summary>
    public SocketIoPacket? OnText(string text)
    {
        if (_pending != null)
        {
            // 添付を待っている間にテキストが来たら、公式の実装は "got plaintext data when reconstructing a packet" のエラーにする
            var broken = _pending with { Valid = false, Error = "添付の途中でテキストが来た" };
            _pending = null;
            _attachments.Clear();
            _ = broken;
        }

        var packet = SocketIo.DecodeString(text);
        if (packet.Valid && packet.Type is SocketIoPacketType.BinaryEvent or SocketIoPacketType.BinaryAck)
        {
            // 公式の Decoder（4.2.x）は、BINARY_EVENT / BINARY_ACK を EVENT / ACK に直してから添付を待つ
            packet = packet with { Type = packet.Type == SocketIoPacketType.BinaryEvent ? SocketIoPacketType.Event : SocketIoPacketType.Ack };
            if (packet.Attachments == 0)
            {
                return packet;
            }

            _pending = packet;
            _attachments.Clear();
            return null;
        }

        return packet;
    }

    /// <summary>バイナリのメッセージを渡す。完成したパケットを返す。</summary>
    public SocketIoPacket? OnBinary(byte[] data)
    {
        if (_pending == null)
        {
            return new SocketIoPacket(SocketIoPacketType.BinaryEvent, "/", null, new JsonObject { [SocketIo.BinaryKey] = Convert.ToBase64String(data) }, 1, false, "got binary data when not reconstructing a packet");
        }

        _attachments.Add(data);
        if (_attachments.Count < _pending.Attachments)
        {
            return null;
        }

        var complete = _pending with { Data = SocketIo.Reconstruct(_pending.Data, _attachments) };
        _pending = null;
        _attachments.Clear();
        return complete;
    }

    /// <summary>添付を待っているか。</summary>
    public bool Waiting => _pending != null;
}
