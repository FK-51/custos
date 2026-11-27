using System.Text.RegularExpressions;
using Custos.Core.Util;

namespace Custos.Core.Tests;

/// <summary>フレームワークの版（結果の記録と、コンテナのイメージのタグに使う）。</summary>
public class FrameworkInfoTests
{
    [Fact]
    public void VersionHasNoBuildMetadata()
    {
        // コミットのある git のリポジトリでビルドすると、SDK は InformationalVersion に「+コミットのハッシュ」を付ける
        Assert.DoesNotContain('+', FrameworkInfo.Version);
    }

    [Fact]
    public void VersionCanBeUsedInImageTag()
    {
        // Docker のタグに使える文字だけ（ImageBuilder は custos-<部品>:<版>-<ハッシュ> のタグを作る）
        Assert.Matches(new Regex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,100}$", RegexOptions.None, TimeSpan.FromSeconds(1)), FrameworkInfo.Version);
    }

    [Theory]
    [InlineData("0.1.0+68a652255e4e56d9b2664ed7fa3986232221db67", "0.1.0")]
    [InlineData("1.2.3-beta.1+build.5", "1.2.3-beta.1")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("", "")]
    public void BuildMetadataIsRemoved(string version, string expected)
    {
        Assert.Equal(expected, FrameworkInfo.WithoutBuildMetadata(version));
    }
}
