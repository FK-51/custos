using System.Text;

namespace Custos.SocketIO.Wire;

/// <summary>WebSocket のメッセージ（断片を組み立てた後）または制御フレーム。</summary>
public sealed record WebSocketMessage(int Opcode, byte[] Payload, bool Compressed)
{
    /// <summary>このメッセージを運んだフレームの生のバイト列（受け取ったまま。プロキシはこれを転送する）。</summary>
    public IReadOnlyList<byte[]> RawFrames { get; init; } = [];

    /// <summary>テキスト（opcode 1）。</summary>
    public bool IsText => Opcode == 1;

    /// <summary>バイナリ（opcode 2）。</summary>
    public bool IsBinary => Opcode == 2;

    /// <summary>close（opcode 8）。</summary>
    public bool IsClose => Opcode == 8;

    /// <summary>種類の名前。</summary>
    public string Kind => Opcode switch
    {
        1 => "text",
        2 => "binary",
        8 => "close",
        9 => "ping",
        10 => "pong",
        _ => "opcode-" + Opcode.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>テキストとして読む（UTF-8）。</summary>
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// WebSocket のフレームを、受け取った順に少しずつ読み解く（RFC 6455）。
/// プロキシはバイト列をそのまま転送し、写しをここに渡して記録だけに使う。
/// </summary>
public sealed class WebSocketFrameParser
{
    private readonly List<byte> _pending = [];
    private readonly List<byte> _fragments = [];
    private readonly List<byte[]> _fragmentRaw = [];
    private int _fragmentOpcode;
    private bool _fragmentCompressed;

    /// <summary>受け取ったバイト列を渡す。組み上がったメッセージを返す。</summary>
    public IReadOnlyList<WebSocketMessage> Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _pending.Add(b);
        }

        var messages = new List<WebSocketMessage>();
        while (TryReadFrame(out var fin, out var rsv1, out var opcode, out var payload, out var raw))
        {
            if (opcode >= 8)
            {
                messages.Add(new WebSocketMessage(opcode, payload, false) { RawFrames = [raw] });
                continue;
            }

            if (opcode != 0)
            {
                _fragments.Clear();
                _fragmentRaw.Clear();
                _fragmentOpcode = opcode;
                _fragmentCompressed = rsv1;
            }

            _fragments.AddRange(payload);
            _fragmentRaw.Add(raw);
            if (fin)
            {
                messages.Add(new WebSocketMessage(_fragmentOpcode, _fragments.ToArray(), _fragmentCompressed) { RawFrames = _fragmentRaw.ToList() });
                _fragments.Clear();
                _fragmentRaw.Clear();
            }
        }

        return messages;
    }

    private bool TryReadFrame(out bool fin, out bool rsv1, out int opcode, out byte[] payload, out byte[] raw)
    {
        fin = false;
        rsv1 = false;
        opcode = 0;
        payload = [];
        raw = [];
        if (_pending.Count < 2)
        {
            return false;
        }

        var b0 = _pending[0];
        var b1 = _pending[1];
        fin = (b0 & 0x80) != 0;
        rsv1 = (b0 & 0x40) != 0;
        opcode = b0 & 0x0f;
        var masked = (b1 & 0x80) != 0;
        long length = b1 & 0x7f;
        var offset = 2;
        if (length == 126)
        {
            if (_pending.Count < 4)
            {
                return false;
            }

            length = (_pending[2] << 8) | _pending[3];
            offset = 4;
        }
        else if (length == 127)
        {
            if (_pending.Count < 10)
            {
                return false;
            }

            length = 0;
            for (var i = 0; i < 8; i++)
            {
                length = (length << 8) | _pending[2 + i];
            }

            offset = 10;
        }

        var maskOffset = offset;
        if (masked)
        {
            offset += 4;
        }

        if (length > int.MaxValue - offset || _pending.Count < offset + length)
        {
            return false;
        }

        payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var value = _pending[offset + i];
            if (masked)
            {
                value ^= _pending[maskOffset + (i % 4)];
            }

            payload[i] = value;
        }

        raw = _pending.GetRange(0, offset + (int)length).ToArray();
        _pending.RemoveRange(0, offset + (int)length);
        return true;
    }

    /// <summary>
    /// フレームを作る（生のクライアントが送るときに使う）。クライアントからのフレームは必ずマスクする。
    /// </summary>
    public static byte[] BuildFrame(int opcode, ReadOnlySpan<byte> payload, bool mask, bool fin = true)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)((fin ? 0x80 : 0) | (opcode & 0x0f)));
        var maskBit = mask ? 0x80 : 0;
        if (payload.Length < 126)
        {
            stream.WriteByte((byte)(maskBit | payload.Length));
        }
        else if (payload.Length <= 0xffff)
        {
            stream.WriteByte((byte)(maskBit | 126));
            stream.WriteByte((byte)(payload.Length >> 8));
            stream.WriteByte((byte)(payload.Length & 0xff));
        }
        else
        {
            stream.WriteByte((byte)(maskBit | 127));
            var length = (long)payload.Length;
            for (var i = 7; i >= 0; i--)
            {
                stream.WriteByte((byte)((length >> (8 * i)) & 0xff));
            }
        }

        if (mask)
        {
            var key = new byte[4];
            System.Security.Cryptography.RandomNumberGenerator.Fill(key);
            stream.Write(key);
            var masked = payload.ToArray();
            for (var i = 0; i < masked.Length; i++)
            {
                masked[i] ^= key[i % 4];
            }

            stream.Write(masked);
        }
        else
        {
            stream.Write(payload);
        }

        return stream.ToArray();
    }
}
