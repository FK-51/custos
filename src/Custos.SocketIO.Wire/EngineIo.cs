using System.Text;

namespace Custos.SocketIO.Wire;

/// <summary>Engine.IO v4 のパケットの種類。</summary>
public enum EngineIoPacketType
{
    /// <summary>open（0）。</summary>
    Open = 0,

    /// <summary>close（1）。</summary>
    Close = 1,

    /// <summary>ping（2）。</summary>
    Ping = 2,

    /// <summary>pong（3）。</summary>
    Pong = 3,

    /// <summary>message（4）。</summary>
    Message = 4,

    /// <summary>upgrade（5）。</summary>
    Upgrade = 5,

    /// <summary>noop（6）。</summary>
    Noop = 6,
}

/// <summary>
/// Engine.IO のパケット。テキストのパケットは Text、バイナリのメッセージは Binary を持つ。
/// Valid が false のものは、公式の実装が「error」として扱うもの（読み解きの結果も残す）。
/// </summary>
public sealed record EngineIoPacket(EngineIoPacketType Type, string? Text, byte[]? Binary, bool Valid = true, string? Error = null)
{
    /// <summary>バイナリのメッセージか。</summary>
    public bool IsBinary => Binary != null;

    /// <summary>種類の名前（open、close、ping、pong、message、upgrade、noop、error）。</summary>
    public string TypeName => Valid ? EngineIo.TypeName(Type) : "error";
}

/// <summary>
/// Engine.IO v4（engine.io-parser 5.x）の符号化と読み解き。
/// 公式の実装（engine.io-parser 5.2.3）を正解としてテストする（tests/oracle-node）。
/// </summary>
public static class EngineIo
{
    /// <summary>ポーリングの本文で、パケットを区切る文字（0x1e）。</summary>
    public const char Separator = '\u001e';

    /// <summary>種類の名前。</summary>
    public static string TypeName(EngineIoPacketType type) => type switch
    {
        EngineIoPacketType.Open => "open",
        EngineIoPacketType.Close => "close",
        EngineIoPacketType.Ping => "ping",
        EngineIoPacketType.Pong => "pong",
        EngineIoPacketType.Message => "message",
        EngineIoPacketType.Upgrade => "upgrade",
        _ => "noop",
    };

    /// <summary>テキストの1パケットを読み解く（WebSocket のテキストのフレーム、またはポーリングの1要素）。</summary>
    public static EngineIoPacket DecodeText(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length == 0)
        {
            return new EngineIoPacket(EngineIoPacketType.Noop, null, null, false, "空のパケット");
        }

        if (encoded[0] == 'b')
        {
            try
            {
                return new EngineIoPacket(EngineIoPacketType.Message, null, Convert.FromBase64String(encoded[1..]));
            }
            catch (FormatException)
            {
                return new EngineIoPacket(EngineIoPacketType.Message, null, null, false, "base64 を読めません");
            }
        }

        var code = encoded[0] - '0';
        if (code is < 0 or > 6)
        {
            return new EngineIoPacket(EngineIoPacketType.Noop, encoded, null, false, $"知らない種類：{encoded[0]}");
        }

        return new EngineIoPacket((EngineIoPacketType)code, encoded.Length > 1 ? encoded[1..] : null, null);
    }

    /// <summary>バイナリの1メッセージ（WebSocket のバイナリのフレーム）。</summary>
    public static EngineIoPacket DecodeBinary(byte[] data) => new(EngineIoPacketType.Message, null, data);

    /// <summary>ポーリングの本文（0x1e で区切ったパケットの並び）を読み解く。</summary>
    public static IReadOnlyList<EngineIoPacket> DecodePayload(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var packets = new List<EngineIoPacket>();
        foreach (var part in payload.Split(Separator))
        {
            var packet = DecodeText(part);
            packets.Add(packet);
            if (!packet.Valid)
            {
                // 公式の実装は、読めないパケットが出たところで打ち切る
                break;
            }
        }

        return packets;
    }

    /// <summary>
    /// 1パケットを符号化する。supportsBinary が false（ポーリング）なら、バイナリは "b" + base64 にする。
    /// 返り値は、テキストか、バイナリのどちらか一方。
    /// </summary>
    public static (string? Text, byte[]? Binary) Encode(EngineIoPacket packet, bool supportsBinary)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Binary != null)
        {
            return supportsBinary ? (null, packet.Binary) : ("b" + Convert.ToBase64String(packet.Binary), null);
        }

        return (((int)packet.Type).ToString(System.Globalization.CultureInfo.InvariantCulture) + (packet.Text ?? string.Empty), null);
    }

    /// <summary>ポーリングの本文を作る。</summary>
    public static string EncodePayload(IEnumerable<EngineIoPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var builder = new StringBuilder();
        var first = true;
        foreach (var packet in packets)
        {
            if (!first)
            {
                builder.Append(Separator);
            }

            first = false;
            builder.Append(Encode(packet, supportsBinary: false).Text);
        }

        return builder.ToString();
    }
}
