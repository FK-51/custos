using Custos.Core.Util;

namespace Custos.Cli.Commands;

/// <summary>段階6：ひな形（設計書 第24.6節、第27.2節）。</summary>
public static class TemplateCommands
{
    /// <summary>custos template init &lt;dir&gt;：ひな形から検証用リポジトリを作る（既にあるファイルは上書きしない）。</summary>
    public static Task<int> InitAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var target = Path.GetFullPath(args.Arg(0, "作る検証用リポジトリのディレクトリ"));
        var name = args.Get("template") ?? "web-socketio";
        var framework = Environment.GetEnvironmentVariable("CUSTOS_FRAMEWORK_REPO") ?? AppContext.BaseDirectory;
        var source = Path.Combine(framework, "templates", name);
        if (!Directory.Exists(source))
        {
            throw new CustosException($"ひな形がありません：{source}");
        }

        var copied = 0;
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            if (File.Exists(destination))
            {
                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? target);
            File.Copy(file, destination);
            copied++;
        }

        foreach (var dir in new[] { "registry", "seeds", "hidden-scenarios", "keys", "reference-model", "docs" })
        {
            Directory.CreateDirectory(Path.Combine(target, dir));
        }

        var marker = Path.Combine(target, ".custos-repo-id");
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "custos-verify-repo:" + Guid.NewGuid().ToString("N") + "\n");
        }

        CliContext.Out($"ひな形 {name} から {copied} 個のファイルを写しました（既にあった {skipped} 個はそのまま）：{target}");
        CliContext.Out("TODO の箇所をアプリに合わせて書き換え、custos config check で書式と参照を確かめてください。");
        return Task.FromResult(0);
    }
}
