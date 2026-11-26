using System.Globalization;
using System.Security.Cryptography;
using Custos.Core.Judging;

namespace Custos.Core.Tests;

/// <summary>秘密の値の推測しにくさの分析（設計書 第25.6節）。</summary>
public class TokenAnalysisTests
{
    private static List<string> RandomHex(int count, int bytes) =>
        Enumerable.Range(0, count).Select(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes))).ToList();

    [Fact]
    public void RandomTokensHaveNoProblems()
    {
        var result = TokenAnalysis.Analyze(RandomHex(20, 32));
        Assert.True(result.Ok, string.Join("／", result.Problems));
        Assert.Equal("hex", result.Charset);
        Assert.True(result.EstimatedBits >= 256);
    }

    [Fact]
    public void SequentialNumbersAreDetected()
    {
        var tokens = Enumerable.Range(1000, 20).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        var result = TokenAnalysis.Analyze(tokens);
        Assert.False(result.Ok);
        Assert.Contains(result.Problems, p => p.Contains("連番", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Contains("短く", StringComparison.Ordinal));
    }

    [Fact]
    public void TimestampPrefixIsDetected()
    {
        var tokens = RandomHex(20, 16).Select(t => "1795655" + t).ToList();
        var result = TokenAnalysis.Analyze(tokens);
        Assert.Contains(result.Problems, p => p.Contains("先頭が共通", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatesAreDetected()
    {
        var tokens = RandomHex(10, 32);
        tokens.Add(tokens[0]);
        Assert.Contains(TokenAnalysis.Analyze(tokens).Problems, p => p.Contains("同じ値", StringComparison.Ordinal));
    }

    [Fact]
    public void TokenEqualToPublicIdIsDetected()
    {
        var tokens = RandomHex(10, 32);
        var result = TokenAnalysis.Analyze(tokens, [tokens[3]]);
        Assert.Contains(result.Problems, p => p.Contains("公開されている値", StringComparison.Ordinal));
    }
}

/// <summary>正規化した JSON（決定性の比較に使う）。</summary>
public class CanonicalJsonTests
{
    [Fact]
    public void ValuesCreatedFromClrPrimitivesAreCanonicalized()
    {
        var a = new System.Text.Json.Nodes.JsonObject { ["b"] = System.Text.Json.Nodes.JsonValue.Create("det"), ["a"] = System.Text.Json.Nodes.JsonValue.Create(1) };
        var b = System.Text.Json.Nodes.JsonNode.Parse("{\"a\":1.0,\"b\":\"det\"}");
        Assert.Equal(Custos.Core.Util.Json.Canonical(a), Custos.Core.Util.Json.Canonical(b));
    }
}
