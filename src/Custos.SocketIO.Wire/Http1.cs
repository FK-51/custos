using System.Globalization;
using System.Text;

namespace Custos.SocketIO.Wire;

/// <summary>HTTP/1.1 のヘッダー1つ（受け取ったままの名前と値）。</summary>
public sealed record HttpHeader(string Name, string Value);

/// <summary>
/// HTTP/1.1 のメッセージの頭（要求行または状態行と、ヘッダー）。
/// Raw は受け取ったバイト列そのもので、プロキシはこれを変えずに転送する。
/// </summary>
public sealed record HttpHead(
    byte[] Raw,
    string StartLine,
    bool IsResponse,
    string Method,
    string Target,
    string Version,
    int Status,
    string Reason,
    IReadOnlyList<HttpHeader> Headers)
{
    /// <summary>ヘッダーの値（同じ名前が複数あれば , でつなぐ）。大文字小文字は区別しない。</summary>
    public string? Header(string name)
    {
        var values = Headers.Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        return values.Count == 0 ? null : string.Join(", ", values);
    }

    /// <summary>パス（クエリを除く）。</summary>
    public string Path
    {
        get
        {
            var q = Target.IndexOf('?', StringComparison.Ordinal);
            return q < 0 ? Target : Target[..q];
        }
    }

    /// <summary>クエリの値。</summary>
    public string? Query(string name)
    {
        var q = Target.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return null;
        }

        foreach (var part in Target[(q + 1)..].Split('&'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? part : part[..eq];
            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal))
            {
                return eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return null;
    }

    /// <summary>本文が chunked か。</summary>
    public bool IsChunked => Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Content-Length（なければ null）。</summary>
    public long? ContentLength =>
        long.TryParse(Header("Content-Length")?.Split(',')[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : null;

    /// <summary>この要求または応答の後で、接続を閉じるか。</summary>
    public bool WantsClose =>
        Header("Connection")?.Contains("close", StringComparison.OrdinalIgnoreCase) == true
        || (Version == "HTTP/1.0" && Header("Connection")?.Contains("keep-alive", StringComparison.OrdinalIgnoreCase) != true);

    /// <summary>WebSocket への切り替えの要求か。</summary>
    public bool IsWebSocketUpgrade =>
        Header("Upgrade")?.Contains("websocket", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// ヘッダーの部分だけのバイト列（要求行・状態行を除く）。E-4（プロキシが足したヘッダーがない）の照合に使う。
    /// </summary>
    public string HeaderBlock
    {
        get
        {
            var text = Encoding.Latin1.GetString(Raw);
            var first = text.IndexOf("\r\n", StringComparison.Ordinal);
            return first < 0 ? string.Empty : text[(first + 2)..];
        }
    }
}

/// <summary>本文の区切り方。</summary>
public enum BodyFraming
{
    /// <summary>本文なし。</summary>
    None,

    /// <summary>Content-Length のバイト数。</summary>
    Length,

    /// <summary>chunked。</summary>
    Chunked,

    /// <summary>接続が閉じるまで（応答のみ）。</summary>
    UntilClose,
}

/// <summary>
/// ストリームから HTTP/1.1 のメッセージを読む。読んだバイト列は「受け取ったまま」返すので、プロキシはそれをそのまま転送できる。
/// </summary>
public sealed class HttpMessageReader
{
    private const int MaxHeadBytes = 64 * 1024;
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    /// <summary>ストリームを包む。</summary>
    public HttpMessageReader(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>まだ読んでいない、バッファに残っているバイト列（WebSocket に切り替えた後に使う）。</summary>
    public ReadOnlyMemory<byte> TakeBuffered()
    {
        var data = _buffer.AsMemory(_start, _end - _start).ToArray();
        _start = _end = 0;
        return data;
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0 && _start == _end)
        {
            _start = _end = 0;
        }

        if (_end == _buffer.Length)
        {
            if (_start == 0)
            {
                return false;
            }

            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        if (read <= 0)
        {
            return false;
        }

        _end += read;
        return true;
    }

    /// <summary>メッセージの頭を読む。接続が閉じていれば null。</summary>
    public async Task<HttpHead?> ReadHeadAsync(bool isResponse, CancellationToken cancellationToken)
    {
        var collected = new MemoryStream();
        while (true)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var index = span.IndexOf("\r\n\r\n"u8);
            if (index >= 0)
            {
                collected.Write(span[..(index + 4)]);
                _start += index + 4;
                return Parse(collected.ToArray(), isResponse);
            }

            // 区切りが、前回と今回の境目にまたがっている場合に備えて、末尾の3バイトは残す
            var keep = Math.Min(3, span.Length);
            collected.Write(span[..(span.Length - keep)]);
            _start += span.Length - keep;
            if (collected.Length > MaxHeadBytes)
            {
                throw new InvalidDataException("HTTP のヘッダーが大きすぎます");
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                if (collected.Length == 0 && _end == _start)
                {
                    return null;
                }

                throw new EndOfStreamException("HTTP のヘッダーの途中で接続が閉じました");
            }

            // 残しておいた3バイトを含めて探し直すため、collected に入れた分は前に戻さない
        }
    }

    /// <summary>頭のバイト列を読み解く。</summary>
    public static HttpHead Parse(byte[] raw, bool isResponse)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var text = Encoding.Latin1.GetString(raw);
        var lines = text.Split("\r\n");
        var startLine = lines[0];
        var headers = new List<HttpHeader>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            headers.Add(new HttpHeader(line[..colon], line[(colon + 1)..].Trim()));
        }

        if (isResponse)
        {
            var parts = startLine.Split(' ', 3);
            var status = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 0;
            return new HttpHead(raw, startLine, true, string.Empty, string.Empty, parts[0], status, parts.Length > 2 ? parts[2] : string.Empty, headers);
        }
        else
        {
            var parts = startLine.Split(' ', 3);
            return new HttpHead(raw, startLine, false, parts[0], parts.Length > 1 ? parts[1] : string.Empty, parts.Length > 2 ? parts[2] : string.Empty, 0, string.Empty, headers);
        }
    }

    /// <summary>要求の本文の区切り方。</summary>
    public static BodyFraming RequestFraming(HttpHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        if (head.IsChunked)
        {
            return BodyFraming.Chunked;
        }

        return head.ContentLength is > 0 ? BodyFraming.Length : BodyFraming.None;
    }

    /// <summary>応答の本文の区切り方。</summary>
    public static BodyFraming ResponseFraming(HttpHead response, string requestMethod)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
            || response.Status is >= 100 and < 200 or 204 or 304)
        {
            return BodyFraming.None;
        }

        if (response.IsChunked)
        {
            return BodyFraming.Chunked;
        }

        if (response.ContentLength is { } length)
        {
            return length > 0 ? BodyFraming.Length : BodyFraming.None;
        }

        return BodyFraming.UntilClose;
    }

    /// <summary>
    /// 本文を読む。onRaw には受け取ったままのバイト列（chunked の区切りを含む）を順に渡し、
    /// onDecoded には区切りを外した中身を渡す。
    /// </summary>
    public async Task ReadBodyAsync(
        BodyFraming framing,
        long? contentLength,
        Func<ReadOnlyMemory<byte>, Task> onRaw,
        Action<ReadOnlyMemory<byte>>? onDecoded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onRaw);
        switch (framing)
        {
            case BodyFraming.None:
                return;
            case BodyFraming.Length:
                await ReadExactAsync(contentLength ?? 0, onRaw, onDecoded, cancellationToken).ConfigureAwait(false);
                return;
            case BodyFraming.UntilClose:
                while (true)
                {
                    if (_end > _start)
                    {
                        var chunk = _buffer.AsMemory(_start, _end - _start).ToArray();
                        _start = _end;
                        await onRaw(chunk).ConfigureAwait(false);
                        onDecoded?.Invoke(chunk);
                    }

                    if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }

            case BodyFraming.Chunked:
                while (true)
                {
                    var sizeLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    await onRaw(sizeLine).ConfigureAwait(false);
                    var lineText = Encoding.Latin1.GetString(sizeLine.Span).TrimEnd('\r', '\n');
                    var semicolon = lineText.IndexOf(';', StringComparison.Ordinal);
                    var hex = (semicolon < 0 ? lineText : lineText[..semicolon]).Trim();
                    if (!long.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
                    {
                        throw new InvalidDataException($"chunked の大きさを読めません：{lineText}");
                    }

                    if (size == 0)
                    {
                        // 後ろのヘッダー（trailer）を空行まで読む
                        while (true)
                        {
                            var trailer = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                            await onRaw(trailer).ConfigureAwait(false);
                            if (trailer.Length <= 2)
                            {
                                return;
                            }
                        }
                    }

                    await ReadExactAsync(size, onRaw, onDecoded, cancellationToken).ConfigureAwait(false);
                    var crlf = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    await onRaw(crlf).ConfigureAwait(false);
                }

            default:
                throw new InvalidOperationException($"知らない区切り方：{framing}");
        }
    }

    private async Task ReadExactAsync(long count, Func<ReadOnlyMemory<byte>, Task> onRaw, Action<ReadOnlyMemory<byte>>? onDecoded, CancellationToken cancellationToken)
    {
        var remaining = count;
        while (remaining > 0)
        {
            if (_end == _start && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new EndOfStreamException("本文の途中で接続が閉じました");
            }

            var take = (int)Math.Min(remaining, _end - _start);
            var chunk = _buffer.AsMemory(_start, take).ToArray();
            _start += take;
            remaining -= take;
            await onRaw(chunk).ConfigureAwait(false);
            onDecoded?.Invoke(chunk);
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadLineAsync(CancellationToken cancellationToken)
    {
        var collected = new MemoryStream();
        while (true)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var index = span.IndexOf((byte)'\n');
            if (index >= 0)
            {
                collected.Write(span[..(index + 1)]);
                _start += index + 1;
                return collected.ToArray();
            }

            collected.Write(span);
            _start = _end;
            if (collected.Length > MaxHeadBytes)
            {
                throw new InvalidDataException("行が長すぎます");
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new EndOfStreamException("行の途中で接続が閉じました");
            }
        }
    }
}
