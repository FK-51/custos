using System.Globalization;
using System.Numerics;

namespace Custos.Core.Judging;

/// <summary>秘密の値の推測しにくさの分析結果。</summary>
public sealed record TokenAnalysisResult(
    int Count,
    int Distinct,
    string Charset,
    int MinLength,
    double EstimatedBits,
    double MeanCommonPrefix,
    double? OneBitRatio,
    IReadOnlyList<string> Problems)
{
    /// <summary>問題がないか。</summary>
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// 続けて発行された秘密の値（再接続トークンなど）に、推測の手がかりになる規則性がないかを調べる（設計書 第25.6節「推測できないこと」）。
/// 機械判定。値は発行の順に渡す。
/// </summary>
public static class TokenAnalysis
{
    /// <summary>必要な強さ（ビット）。</summary>
    public const double MinimumBits = 128;

    /// <summary>分析する。publicValues は公開されている値（ID や名前）。秘密の値がそれと同じ、またはそれを含むなら問題にする。</summary>
    public static TokenAnalysisResult Analyze(IReadOnlyList<string> tokens, IReadOnlyCollection<string>? publicValues = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var problems = new List<string>();
        if (tokens.Count < 2)
        {
            return new TokenAnalysisResult(tokens.Count, tokens.Count, "unknown", tokens.Count == 0 ? 0 : tokens[0].Length, 0, 0, null, ["値が2つ未満のため分析できません"]);
        }

        var distinct = tokens.Distinct(StringComparer.Ordinal).Count();
        if (distinct < tokens.Count)
        {
            problems.Add($"同じ値が発行されています（{tokens.Count} 件中 {distinct} 種類）");
        }

        var charset = Charset(tokens);
        var alphabet = charset switch
        {
            "digits" => 10,
            "hex" => 16,
            "base64url" => 64,
            "base64" => 64,
            "alnum" => 62,
            _ => Math.Max(2, tokens.SelectMany(t => t).Distinct().Count()),
        };
        var minLength = tokens.Min(t => t.Length);
        var bits = minLength * Math.Log2(alphabet);
        if (bits < MinimumBits)
        {
            problems.Add($"値が短く、推測しやすい（約 {bits.ToString("F0", CultureInfo.InvariantCulture)} ビット。{MinimumBits.ToString(CultureInfo.InvariantCulture)} ビット以上が必要）");
        }

        // 続けて発行された値の、共通の先頭部分の長さ
        var prefixes = new List<int>();
        for (var i = 1; i < tokens.Count; i++)
        {
            prefixes.Add(CommonPrefix(tokens[i - 1], tokens[i]));
        }

        var meanPrefix = prefixes.Average();
        if (meanPrefix >= 3)
        {
            problems.Add($"続けて発行された値の先頭が共通しています（平均 {meanPrefix.ToString("F1", CultureInfo.InvariantCulture)} 文字。時刻や連番が入っている疑い）");
        }

        // 位置ごとの偏り（同じ文字が8割以上の位置がある）
        if (tokens.Count >= 10)
        {
            var fixedPositions = 0;
            for (var position = 0; position < minLength; position++)
            {
                var top = tokens.GroupBy(t => t[position]).Max(g => g.Count());
                if (top >= tokens.Count * 0.8)
                {
                    fixedPositions++;
                }
            }

            if (fixedPositions > 0)
            {
                problems.Add($"ほとんど変わらない位置が {fixedPositions} か所あります（固定の文字や時刻が入っている疑い）");
            }
        }

        // 数として読めるなら、増え方の規則性
        if (charset is "digits" or "hex")
        {
            var numbers = tokens.Select(t => Parse(t, charset)).ToList();
            var differences = new List<BigInteger>();
            for (var i = 1; i < numbers.Count; i++)
            {
                differences.Add(numbers[i] - numbers[i - 1]);
            }

            if (differences.Distinct().Count() == 1)
            {
                problems.Add($"値が一定の差（{differences[0]}）で増えています（連番）");
            }
            else if (differences.All(d => BigInteger.Abs(d) < 1_000_000))
            {
                problems.Add("続けて発行された値の差がすべて小さい（連番に近い）");
            }
        }

        // 16進なら、1 のビットの割合（乱数なら 0.5 前後）
        double? ones = null;
        if (charset == "hex")
        {
            long total = 0;
            long count = 0;
            foreach (var token in tokens)
            {
                foreach (var c in token)
                {
                    count += BitOperations.PopCount((uint)int.Parse(c.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    total += 4;
                }
            }

            ones = (double)count / total;
            if (total >= 1000 && Math.Abs(ones.Value - 0.5) > 0.06)
            {
                problems.Add($"ビットの偏りがあります（1 の割合 {ones.Value.ToString("F3", CultureInfo.InvariantCulture)}）");
            }
        }

        if (publicValues != null)
        {
            var leaked = tokens.Where(t => publicValues.Any(p => p.Length >= 6 && (t == p || t.Contains(p, StringComparison.Ordinal) || p.Contains(t, StringComparison.Ordinal)))).Count();
            if (leaked > 0)
            {
                problems.Add($"公開されている値（ID や名前）と同じ、または一部が同じ秘密の値が {leaked} 件あります");
            }
        }

        return new TokenAnalysisResult(tokens.Count, distinct, charset, minLength, bits, meanPrefix, ones, problems);
    }

    private static string Charset(IReadOnlyList<string> tokens)
    {
        bool All(Func<char, bool> predicate) => tokens.All(t => t.All(predicate));
        if (All(char.IsAsciiDigit))
        {
            return "digits";
        }

        if (All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)) || All(c => char.IsAsciiHexDigitUpper(c) || char.IsAsciiDigit(c)))
        {
            return "hex";
        }

        if (All(char.IsAsciiLetterOrDigit))
        {
            return "alnum";
        }

        if (All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return "base64url";
        }

        if (All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
        {
            return "base64";
        }

        return "other";
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    private static BigInteger Parse(string text, string charset) =>
        charset == "digits"
            ? BigInteger.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : BigInteger.Parse("0" + text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
