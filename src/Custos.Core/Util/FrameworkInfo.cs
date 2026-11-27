using System.Reflection;

namespace Custos.Core.Util;

/// <summary>
/// フレームワークの版。結果には、フレームワークの版と判定器のハッシュを記録する（設計書 第12.6節、第17.4節）。
/// </summary>
public static class FrameworkInfo
{
    /// <summary>フレームワークの版（Directory.Build.props の Version）。ビルドの付記（+ の後ろ）は含めない。</summary>
    public static string Version { get; } = WithoutBuildMetadata(
        typeof(FrameworkInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(FrameworkInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0");

    /// <summary>プロトコル（アダプタとの約束事）の版。</summary>
    public const string ProtocolVersion = "0.1";

    /// <summary>
    /// 判定器の版のハッシュ。読み込んでいる Custos.* のアセンブリの内容から計算する。
    /// 判定器のコードが1バイトでも変われば値が変わる。
    /// </summary>
    public static string JudgeHash { get; } = ComputeJudgeHash();

    /// <summary>
    /// 版からビルドの付記（SemVer の + の後ろ）を除く。
    /// .NET SDK は、コミットのある git のリポジトリでビルドすると InformationalVersion に「+コミットのハッシュ」を付ける。
    /// 付記が残ると、Docker のタグに使えない文字（+）が入り、結果や登録簿に記録する版の値もコミットごとに変わってしまう。
    /// </summary>
    public static string WithoutBuildMetadata(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    private static string ComputeJudgeHash()
    {
        var directory = AppContext.BaseDirectory;
        var files = Directory.EnumerateFiles(directory, "Custos.*.dll")
            .Concat(Directory.EnumerateFiles(directory, "custos.dll"))
            .Select(f => new KeyValuePair<string, string>(Path.GetFileName(f), Hashing.Sha256File(f)));
        return Hashing.Combine(files);
    }
}
