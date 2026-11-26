using Custos.Core.Util;

namespace Custos.Web.SocketIO.Lab;

/// <summary>
/// 起動中の検証環境の状態（custos up が書き、他のコマンドが読む）。データの置き場所の compose/&lt;project&gt;/state.json。
/// </summary>
public sealed record LabState(
    string Project,
    string SessionId,
    string ComposeFile,
    string RecordsDir,
    string AppImage,
    string AppImageId,
    string BuildKind,
    IReadOnlyDictionary<string, string> AgentImages,
    string SandboxImage,
    int AppUid,
    int AppGid,
    IReadOnlyList<string> Breakage,
    string? Profile,
    DateTimeOffset StartedAt)
{
    /// <summary>状態のファイルの場所。</summary>
    public static string PathFor(Workspace workspace, string project)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return System.IO.Path.Combine(workspace.ComposeDir, project, "state.json");
    }

    /// <summary>読む。なければ null。</summary>
    public static LabState? Load(Workspace workspace, string project)
    {
        var path = PathFor(workspace, project);
        return File.Exists(path) ? Json.Deserialize<LabState>(File.ReadAllText(path)) : null;
    }

    /// <summary>書く。</summary>
    public void Save(Workspace workspace)
    {
        var path = PathFor(workspace, Project);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? workspace.ComposeDir);
        File.WriteAllText(path, Json.Serialize(this));
    }
}

/// <summary>コンテナの名前（compose の container_name）。</summary>
public static class Containers
{
    /// <summary>対象アプリ。</summary>
    public static string App(string project) => $"{project}-app";

    /// <summary>記録用のプロキシ。</summary>
    public static string Edge(string project) => $"{project}-edge";

    /// <summary>管理側の操作。</summary>
    public static string Ops(string project) => $"{project}-ops";

    /// <summary>正直なクライアント。</summary>
    public static string Client(string project, int index) => $"{project}-client-{index}";

    /// <summary>攻撃クライアント。</summary>
    public static string Attacker(string project, int index) => $"{project}-attacker-{index}";

    /// <summary>ブラウザ。</summary>
    public static string Browser(string project, int index) => $"{project}-browser-{index}";

    /// <summary>AI用サンドボックスの枠。</summary>
    public static string Sandbox(string project) => $"{project}-sandbox";

    /// <summary>モデルのAPIへの出口（段階3）。</summary>
    public static string Egress(string project) => $"{project}-egress";
}
