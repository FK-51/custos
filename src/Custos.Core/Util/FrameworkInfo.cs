using System.Reflection;

namespace Custos.Core.Util;

/// <summary>
/// フレームワークの版。結果には、フレームワークの版と判定器のハッシュを記録する（設計書 第12.6節、第17.4節）。
/// </summary>
public static class FrameworkInfo
{
    /// <summary>フレームワークの版（Directory.Build.props の Version）。</summary>
    public static string Version { get; } =
        typeof(FrameworkInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(FrameworkInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>プロトコル（アダプタとの約束事）の版。</summary>
    public const string ProtocolVersion = "0.1";

    /// <summary>
    /// 判定器の版のハッシュ。読み込んでいる Custos.* のアセンブリの内容から計算する。
    /// 判定器のコードが1バイトでも変われば値が変わる。
    /// </summary>
    public static string JudgeHash { get; } = ComputeJudgeHash();

    private static string ComputeJudgeHash()
    {
        var directory = AppContext.BaseDirectory;
        var files = Directory.EnumerateFiles(directory, "Custos.*.dll")
            .Concat(Directory.EnumerateFiles(directory, "custos.dll"))
            .Select(f => new KeyValuePair<string, string>(Path.GetFileName(f), Hashing.Sha256File(f)));
        return Hashing.Combine(files);
    }
}
