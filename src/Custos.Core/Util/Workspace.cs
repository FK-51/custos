namespace Custos.Core.Util;

/// <summary>
/// 3つのリポジトリとデータの置き場所（指示書 第3.1節、付録C）。
/// - 対象アプリのリポジトリ：読むだけ
/// - フレームワークのリポジトリ：このプログラム
/// - 検証用リポジトリ：設定、調査結果、登録簿の書き出し
/// - データの置き場所：結果、スナップショット、通信の記録、登録簿の実体（リポジトリの外）
/// </summary>
public sealed record Workspace(string VerifyRepo, string DataDir, string AppRepo, string FrameworkRepo)
{
    /// <summary>設定のディレクトリ。</summary>
    public string ConfigDir => Path.Combine(VerifyRepo, "config");

    /// <summary>コンテナの定義のディレクトリ（検証用リポジトリ）。</summary>
    public string EnvironmentDir => Path.Combine(VerifyRepo, "environment");

    /// <summary>結果の保存先。</summary>
    public string ResultsDir => Path.Combine(DataDir, "results");

    /// <summary>スナップショットの置き場所。</summary>
    public string SnapshotsDir => Path.Combine(DataDir, "snapshots");

    /// <summary>通信の記録の置き場所。</summary>
    public string RecordsDir => Path.Combine(DataDir, "records");

    /// <summary>プロキシへの指示（異常の注入など）の置き場所。</summary>
    public string ControlDir => Path.Combine(DataDir, "control");

    /// <summary>登録簿の実体。</summary>
    public string RegistryPath => Path.Combine(DataDir, "registry", "bugs.db");

    /// <summary>秘密の値（署名の鍵など）。リポジトリの外。</summary>
    public string SecretsDir => Path.Combine(DataDir, "secrets");

    /// <summary>コンテナのイメージを作るための作業場所。</summary>
    public string BuildDir => Path.Combine(DataDir, "build");

    /// <summary>生成した compose のファイルの置き場所。</summary>
    public string ComposeDir => Path.Combine(DataDir, "compose");

    /// <summary>AI用サンドボックスに見せる、対象アプリの複製。</summary>
    public string SandboxWorkspaceDir => Path.Combine(DataDir, "sandbox", "workspace");

    /// <summary>Docker の設定（利用者の ~/.docker は使わない）。</summary>
    public string DockerConfigDir => Path.Combine(DataDir, "docker-config");

    /// <summary>
    /// 環境変数と引数から作る。引数が優先。
    /// CUSTOS_VERIFY_REPO、CUSTOS_DATA_DIR、CUSTOS_APP_REPO、CUSTOS_FRAMEWORK_REPO。
    /// </summary>
    public static Workspace Resolve(string? verifyRepo, string? dataDir, string? appRepo, string? frameworkRepo)
    {
        string Pick(string? argument, string variable, string what)
        {
            var value = argument ?? Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CustosException($"{what} の場所が分かりません。--{what} か環境変数 {variable} で指定してください");
            }

            return Path.GetFullPath(value);
        }

        var verify = Pick(verifyRepo, "CUSTOS_VERIFY_REPO", "verify-repo");
        var data = Pick(dataDir, "CUSTOS_DATA_DIR", "data-dir");
        var app = Pick(appRepo, "CUSTOS_APP_REPO", "app-repo");
        var framework = frameworkRepo ?? Environment.GetEnvironmentVariable("CUSTOS_FRAMEWORK_REPO") ?? FindFrameworkRepo();
        var workspace = new Workspace(verify, data, app, Path.GetFullPath(framework));
        workspace.CheckSeparation();
        return workspace;
    }

    private static string FindFrameworkRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Custos.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new CustosException("フレームワークのリポジトリの場所が分かりません。環境変数 CUSTOS_FRAMEWORK_REPO で指定してください");
    }

    /// <summary>
    /// 3つのリポジトリとデータの置き場所が、互いの外にあることを確かめる（指示書 第3.1節）。
    /// 検査用のコード、設定、記録を対象アプリのリポジトリに置かないため（制約6）。
    /// </summary>
    public void CheckSeparation()
    {
        var places = new (string Name, string Path)[]
        {
            ("検証用リポジトリ", VerifyRepo),
            ("データの置き場所", DataDir),
            ("対象アプリのリポジトリ", AppRepo),
            ("フレームワークのリポジトリ", FrameworkRepo),
        };
        for (var i = 0; i < places.Length; i++)
        {
            for (var j = 0; j < places.Length; j++)
            {
                if (i != j && IsInside(places[i].Path, places[j].Path))
                {
                    throw new CustosException($"{places[i].Name}（{places[i].Path}）が {places[j].Name}（{places[j].Path}）の中にあります。互いの外に置いてください");
                }
            }
        }
    }

    /// <summary>child が parent の中（または同じ場所）にあるか。</summary>
    public static bool IsInside(string child, string parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(parent);
        var c = Path.GetFullPath(child).TrimEnd('/') + "/";
        var p = Path.GetFullPath(parent).TrimEnd('/') + "/";
        return c.StartsWith(p, StringComparison.Ordinal);
    }

    /// <summary>データの置き場所のディレクトリを作る。秘密の置き場所は所有者だけが読めるようにする。</summary>
    public void EnsureDataDirs()
    {
        foreach (var dir in new[] { ResultsDir, SnapshotsDir, RecordsDir, ControlDir, Path.GetDirectoryName(RegistryPath) ?? DataDir, BuildDir, ComposeDir, DockerConfigDir })
        {
            Directory.CreateDirectory(dir);
        }

        Directory.CreateDirectory(SecretsDir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(SecretsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
