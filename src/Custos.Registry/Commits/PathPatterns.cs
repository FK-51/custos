using System.Text;
using System.Text.RegularExpressions;

namespace Custos.Registry.Commits;

/// <summary>
/// パスのパターン（glob）。/ を含むパターンはパス全体と、含まないパターンはファイル名と照らし合わせる。
/// ** は / をまたいで、* と ? は / をまたがずに一致する。
/// </summary>
public sealed class PathPatterns
{
    /// <summary>回帰テストとみなす既定のパターン。</summary>
    public static readonly IReadOnlyList<string> DefaultTestPatterns = ["**/__tests__/**", "*.test.*", "*.spec.*", "tests/**"];

    private readonly List<(string Pattern, Regex Regex, bool WholePath)> _patterns = [];

    /// <summary>パターンの一覧から作る。</summary>
    public PathPatterns(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var trimmed = pattern.Trim().TrimStart('/');
            _patterns.Add((trimmed, new Regex(ToRegex(trimmed), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), trimmed.Contains('/', StringComparison.Ordinal)));
        }
    }

    /// <summary>既定のテストのパターン。</summary>
    public static PathPatterns DefaultTests { get; } = new(DefaultTestPatterns);

    /// <summary>パターンの一覧。</summary>
    public IReadOnlyList<string> Patterns => _patterns.Select(p => p.Pattern).ToList();

    /// <summary>どれかのパターンに一致するか。</summary>
    public bool Matches(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var fileName = normalized.Contains('/', StringComparison.Ordinal) ? normalized[(normalized.LastIndexOf('/') + 1)..] : normalized;
        foreach (var (_, regex, wholePath) in _patterns)
        {
            if (regex.IsMatch(wholePath ? normalized : fileName))
            {
                return true;
            }
        }

        return false;
    }

    private static string ToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '*')
            {
                var isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDouble)
                {
                    var atStart = i == 0;
                    var followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    var atEnd = i + 2 == pattern.Length;
                    var precededBySlash = i > 0 && pattern[i - 1] == '/';
                    if (atStart && followedBySlash)
                    {
                        // 先頭の **/ は、0個以上のディレクトリ
                        sb.Append("(?:.*/)?");
                        i += 3;
                        continue;
                    }

                    if (precededBySlash && atEnd)
                    {
                        // 末尾の /** は、その下のすべて。直前に書いた / を取り消して、まとめて書く
                        sb.Length -= 1;
                        sb.Append("(?:/.*)?");
                        i += 2;
                        continue;
                    }

                    if (precededBySlash && followedBySlash)
                    {
                        // 途中の /**/ は、0個以上のディレクトリ
                        sb.Append("(?:.*/)?");
                        i += 3;
                        continue;
                    }

                    sb.Append(".*");
                    i += 2;
                    continue;
                }

                sb.Append("[^/]*");
                i++;
                continue;
            }

            if (c == '?')
            {
                sb.Append("[^/]");
                i++;
                continue;
            }

            sb.Append(Regex.Escape(c.ToString()));
            i++;
        }

        sb.Append('$');
        return sb.ToString();
    }
}
