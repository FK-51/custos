using Custos.Core.Util;

namespace Custos.Registry;

/// <summary>
/// 登録簿とコミットの管理が使う場所。登録簿の実体、チェックポイント、取り込みの記録、署名の鍵は
/// データの置き場所（リポジトリの外）に置き、許可する鍵の一覧と書き出しは検証用リポジトリに置く（設計書 第20.9節）。
/// </summary>
public sealed record RegistryPaths(
    string RegistryDb,
    string CheckpointsDir,
    string MergesPath,
    string SecretsDir,
    string AllowedSignersPath,
    string ExportDir,
    string ConfirmDir)
{
    /// <summary>Workspace から作る。</summary>
    public static RegistryPaths From(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new RegistryPaths(
            workspace.RegistryPath,
            Path.Combine(workspace.DataDir, "checkpoints"),
            Path.Combine(workspace.DataDir, "merges.jsonl"),
            workspace.SecretsDir,
            Path.Combine(workspace.VerifyRepo, "keys", "allowed_signers"),
            Path.Combine(workspace.VerifyRepo, "registry"),
            Path.Combine(workspace.VerifyRepo, "registry", "confirm"));
    }
}
