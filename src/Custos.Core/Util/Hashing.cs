using System.Security.Cryptography;
using System.Text;

namespace Custos.Core.Util;

/// <summary>ハッシュ（SHA-256）の計算。結果の改ざん検知、設定や判定器の版の記録に使う。</summary>
public static class Hashing
{
    /// <summary>バイト列の SHA-256 を16進の小文字で返す。</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>文字列（UTF-8）の SHA-256。</summary>
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>ファイルの SHA-256。</summary>
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// 名前と内容の組の一覧から、一つのハッシュを作る。名前の順に並べてから計算するので、列挙の順序に依存しない。
    /// </summary>
    public static string Combine(IEnumerable<KeyValuePair<string, string>> namedHashes)
    {
        ArgumentNullException.ThrowIfNull(namedHashes);
        var builder = new StringBuilder();
        foreach (var pair in namedHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('\0').Append(pair.Value).Append('\n');
        }

        return Sha256Hex(builder.ToString());
    }

    /// <summary>ディレクトリ以下のファイル（相対パスの順）から一つのハッシュを作る。</summary>
    public static string HashDirectory(string directory, Func<string, bool>? include = null)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => include == null || include(f))
            .Select(f => new KeyValuePair<string, string>(Path.GetRelativePath(directory, f).Replace('\\', '/'), Sha256File(f)));
        return Combine(files);
    }
}
