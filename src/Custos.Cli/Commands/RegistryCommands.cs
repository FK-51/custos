using Custos.Core.Util;
using Custos.Registry;
using Custos.Registry.Bugs;
using Custos.Registry.Commits;
using Custos.Registry.Git;

namespace Custos.Cli.Commands;

/// <summary>バグの登録簿とコミットの分類（設計書 第20章、指示書 タスク11）。</summary>
public static class RegistryCommands
{
    /// <summary>custos bug add（追記だけの入口。一覧や検索は付けない）。</summary>
    public static Task<int> BugAddAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        context.Workspace.EnsureDataDirs();
        var intake = BugRegistry.OpenIntake(context.Workspace.RegistryPath);
        var foundBy = args.Get("found-by") ?? "human";
        var stage = args.Get("stage") ?? (foundBy == "setup" ? "implementation" : "implementation");
        var bug = new NewBug(
            foundBy,
            stage,
            args.Get("title") ?? throw new CustosException("--title（一行の説明）を指定してください"),
            args.Get("id"),
            args.Get("severity"),
            args.Get("category"),
            args.Get("detail"),
            args.Get("source"),
            null,
            args.Get("reach") is { } reach ? int.Parse(reach, System.Globalization.CultureInfo.InvariantCulture) : null,
            args.Get("visibility"));
        var id = intake.Add(bug);
        CliContext.Out($"登録しました：{id}");
        return Task.FromResult(0);
    }

    /// <summary>custos bug export。</summary>
    public static Task<int> BugExportAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admin = BugRegistry.OpenAdmin(context.Workspace.RegistryPath);
        var export = RegistryExporter.Export(admin, Path.Combine(context.Workspace.VerifyRepo, "registry"));
        CliContext.Out($"{export.Count} 件を書き出しました：{export.MarkdownPath}、{export.JsonLinesPath}");
        return Task.FromResult(0);
    }

    /// <summary>
    /// custos bug update --id &lt;ID&gt; [--severity ...] [--category ...] [--duplicate-of &lt;ID&gt;] --reason &lt;理由&gt;。
    /// 登録簿の値を直す（変更の履歴が残る。設計書 第20.7節）。重複は link（duplicate_of）で表す。
    /// </summary>
    public static Task<int> BugUpdateAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var paths = RegistryPaths.From(context.Workspace);
        var registry = BugRegistry.OpenAdmin(paths.RegistryDb);
        var reason = args.Get("reason") ?? throw new CustosException("--reason（直す理由）を指定してください");
        var ids = (args.Get("id") ?? throw new CustosException("--id を指定してください")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var id in ids)
        {
            if (!registry.Exists(id))
            {
                throw new CustosException($"登録簿にありません：{id}");
            }

            if (args.Get("severity") != null || args.Get("category") != null)
            {
                registry.Update(id, new BugUpdate(Severity: args.Get("severity"), Category: args.Get("category")), args.Get("actor") ?? "human", reason);
            }

            if (args.Get("duplicate-of") is { } original)
            {
                registry.Link(id, "duplicate_of", original);
            }

            CliContext.Out($"{id} を直しました（{reason}）");
        }

        RegistryExporter.Export(registry, paths.ExportDir);
        return Task.FromResult(0);
    }

    /// <summary>
    /// 書き込んではいけないリポジトリ（利用者の元のリポジトリ。指示書 制約1）。環境変数 CUSTOS_READONLY_REPOS（: 区切り）で指定する。
    /// 対象アプリの作業用の複製（CUSTOS_APP_REPO）には、修正をコミットしてよい（設計書 第12.6節「アプリのリポジトリには、修正AIが書き込む」）。
    /// </summary>
    public static void RefuseReadOnly(string repo)
    {
        foreach (var readOnly in (Environment.GetEnvironmentVariable("CUSTOS_READONLY_REPOS") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Workspace.IsInside(repo, Path.GetFullPath(readOnly)))
            {
                throw new CustosException($"このリポジトリ（{readOnly}）にはコミットしません（読むだけ）。作業用の複製を --repo で指定してください");
            }
        }
    }

    /// <summary>custos commit --type ... [--bug ...] -m ...。</summary>
    public static async Task<int> CommitAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var repo = Path.GetFullPath(args.Get("repo") ?? Environment.GetEnvironmentVariable("CUSTOS_WORK_REPO") ?? Directory.GetCurrentDirectory());
        RefuseReadOnly(repo);

        context.Workspace.EnsureDataDirs();
        var paths = RegistryPaths.From(context.Workspace);
        var sign = args.Flag("sign");
        var strict = args.Flag("strict");
        string? keyPath = null;
        if (sign)
        {
            keyPath = Path.Combine(context.Workspace.SecretsDir, "framework_signing_ed25519");
            if (!File.Exists(keyPath))
            {
                throw new CustosException("署名の鍵がありません。custos keys init を先に実行してください");
            }
        }

        var gateway = new CommitGateway(
            new GitClient(repo),
            new CommitGatewayOptions(
                paths,
                RequireRegressionTest: strict,
                DetectMixed: strict,
                Sign: sign,
                SigningKeyPath: keyPath,
                UseCheckpoint: sign && strict,
                CopyRegressionTests: true,
                VerifyRepoRoot: context.Workspace.VerifyRepo,
                CommitterName: args.Get("committer-name"),
                CommitterEmail: args.Get("committer-email")));
        var result = await gateway.CommitAsync(
            new CommitRequest(
                args.Get("type"),
                args.Get("bug"),
                args.Get("message") ?? throw new CustosException("-m（説明）を指定してください"),
                args.Get("found-by") ?? "dev_agent",
                args.Flag("all"),
                args.Get("bug-title"),
                args.Get("severity")),
            cancellationToken).ConfigureAwait(false);
        foreach (var warning in result.Warnings)
        {
            CliContext.Out("警告：" + warning);
        }

        if (!result.Committed)
        {
            foreach (var problem in result.Problems)
            {
                CliContext.Out("拒否：" + problem);
            }

            return 1;
        }

        CliContext.Out($"コミットしました：{result.Commit}{(result.BugId != null ? $"（{result.BugId}{(result.BugCreated ? "、登録簿に記録を作成" : string.Empty)}）" : string.Empty)}");
        return 0;
    }

    /// <summary>custos keys init（フレームワークの署名鍵を作り、許可する鍵の一覧に足す）。</summary>
    public static async Task<int> KeysInitAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        context.Workspace.EnsureDataDirs();
        var key = await SigningKeys.GenerateAsync(context.Workspace.SecretsDir, "framework_signing_ed25519", "custos-framework", null, cancellationToken).ConfigureAwait(false);
        var paths = RegistryPaths.From(context.Workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AllowedSignersPath) ?? context.Workspace.VerifyRepo);
        var allowed = File.Exists(paths.AllowedSignersPath) ? AllowedSignersFile.Load(paths.AllowedSignersPath) : AllowedSignersFile.Empty();
        if (allowed.FindByFingerprint(key.Fingerprint) == null)
        {
            allowed.Add("custos-framework@custos.invalid", key.PublicKeyLine, args.Get("owner") ?? "custos", SignerRole.Framework, DateOnly.FromDateTime(DateTime.UtcNow), "フレームワークの署名鍵");
            allowed.Save(paths.AllowedSignersPath);
        }

        CliContext.Out($"署名鍵：{key.Fingerprint}（秘密鍵は {key.PrivateKeyPath}、許可する鍵の一覧は {paths.AllowedSignersPath}）");
        return 0;
    }

    /// <summary>custos intake check（取り込み口の検査）。</summary>
    public static async Task<int> IntakeCheckAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var repo = args.Get("repo") ?? throw new CustosException("--repo を指定してください");
        var paths = RegistryPaths.From(context.Workspace);
        var report = await IntakeChecker.CheckAsync(
            new GitClient(repo),
            args.Get("base") ?? throw new CustosException("--base を指定してください"),
            args.Get("head") ?? "HEAD",
            new IntakeOptions(paths.AllowedSignersPath, new MergeRecords(paths.MergesPath)),
            cancellationToken).ConfigureAwait(false);
        CliContext.Out(report.Summarize());
        return report.Ok ? 0 : 1;
    }
}
