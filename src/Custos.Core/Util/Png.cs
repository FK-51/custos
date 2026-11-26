using System.Buffers.Binary;
using System.IO.Compression;

namespace Custos.Core.Util;

/// <summary>読み取った画像（RGBA、1画素4バイト）。</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>画素の数。</summary>
    public int Count => Width * Height;
}

/// <summary>画像の統計（スクリーンショットの有効性の判定に使う）。</summary>
public sealed record ImageStats(int Width, int Height, double DominantShare, int DistinctColors, double LuminanceStdDev)
{
    /// <summary>真っ白（または一色）に近いか。</summary>
    public bool LooksBlank => DominantShare > 0.995 || LuminanceStdDev < 1.0;
}

/// <summary>
/// PNG の読み取り（8ビットの RGB / RGBA / グレー、インターレースなし）と、画素の計算。
/// スクリーンショットが真っ白でないか、前と変わったかの判定はフレームワーク本体で行う（設計書 第12.8節）。
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>PNG を読む。</summary>
    public static RgbaImage Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new CustosException("PNG ではありません");
        }

        var offset = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        using var idat = new MemoryStream();
        while (offset + 8 <= data.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);
            var body = data.AsSpan(offset + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    bitDepth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                case "IEND":
                    offset = data.Length;
                    continue;
            }

            offset += 12 + length;
        }

        if (bitDepth != 8 || interlace != 0 || colorType is not (0 or 2 or 4 or 6))
        {
            throw new CustosException($"対応していない PNG です（ビット深度 {bitDepth}、色 {colorType}、インターレース {interlace}）");
        }

        var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        var stride = width * channels;
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        var pixels = new byte[width * height * 4];
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (stride + 1);
            var filter = bytes[rowStart];
            Array.Copy(bytes, rowStart + 1, current, 0, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= channels ? current[x - channels] : 0;
                int b = previous[x];
                int c = x >= channels ? previous[x - channels] : 0;
                current[x] = filter switch
                {
                    1 => (byte)(current[x] + a),
                    2 => (byte)(current[x] + b),
                    3 => (byte)(current[x] + ((a + b) >> 1)),
                    4 => (byte)(current[x] + Paeth(a, b, c)),
                    _ => current[x],
                };
            }

            for (var x = 0; x < width; x++)
            {
                var p = (y * width + x) * 4;
                var s = x * channels;
                switch (channels)
                {
                    case 1:
                        pixels[p] = pixels[p + 1] = pixels[p + 2] = current[s];
                        pixels[p + 3] = 255;
                        break;
                    case 2:
                        pixels[p] = pixels[p + 1] = pixels[p + 2] = current[s];
                        pixels[p + 3] = current[s + 1];
                        break;
                    case 3:
                        pixels[p] = current[s];
                        pixels[p + 1] = current[s + 1];
                        pixels[p + 2] = current[s + 2];
                        pixels[p + 3] = 255;
                        break;
                    default:
                        pixels[p] = current[s];
                        pixels[p + 1] = current[s + 1];
                        pixels[p + 2] = current[s + 2];
                        pixels[p + 3] = current[s + 3];
                        break;
                }
            }

            (previous, current) = (current, previous);
        }

        return new RgbaImage(width, height, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>画像の統計。</summary>
    public static ImageStats Stats(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var counts = new Dictionary<int, int>();
        double sum = 0;
        double sumSquares = 0;
        for (var i = 0; i < image.Count; i++)
        {
            var p = i * 4;
            var key = (image.Pixels[p] << 16) | (image.Pixels[p + 1] << 8) | image.Pixels[p + 2];
            counts[key] = counts.GetValueOrDefault(key) + 1;
            var luminance = (0.299 * image.Pixels[p]) + (0.587 * image.Pixels[p + 1]) + (0.114 * image.Pixels[p + 2]);
            sum += luminance;
            sumSquares += luminance * luminance;
        }

        var n = Math.Max(1, image.Count);
        var mean = sum / n;
        var variance = Math.Max(0, (sumSquares / n) - (mean * mean));
        var dominant = counts.Count == 0 ? 0 : counts.Values.Max();
        return new ImageStats(image.Width, image.Height, (double)dominant / n, counts.Count, Math.Sqrt(variance));
    }

    /// <summary>2枚の画像で、色が変わった画素の割合（大きさが違えば 1）。</summary>
    public static double DifferenceRatio(RgbaImage a, RgbaImage b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return 1.0;
        }

        var changed = 0;
        for (var i = 0; i < a.Count; i++)
        {
            var p = i * 4;
            if (Math.Abs(a.Pixels[p] - b.Pixels[p]) + Math.Abs(a.Pixels[p + 1] - b.Pixels[p + 1]) + Math.Abs(a.Pixels[p + 2] - b.Pixels[p + 2]) > 24)
            {
                changed++;
            }
        }

        return (double)changed / Math.Max(1, a.Count);
    }

    /// <summary>真っ白な PNG を作る（わざと壊す確認 C-2 で、スクリーンショットを差し替えるため）。</summary>
    public static byte[] WhitePng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(output, "IHDR", header);
        using var raw = new MemoryStream();
        var row = new byte[1 + (width * 3)];
        Array.Fill(row, (byte)255);
        row[0] = 0;
        for (var y = 0; y < height; y++)
        {
            raw.Write(row);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);
        var crcInput = new byte[typeBytes.Length + body.Length];
        typeBytes.CopyTo(crcInput, 0);
        body.CopyTo(crcInput, typeBytes.Length);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        output.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
