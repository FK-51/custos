using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Util;

namespace Custos.Cli.Commands;

/// <summary>
/// 段階6：参照アプリによるフレームワーク自体の回帰テスト（設計書 第12.5節）。
/// 既知のバグを埋め込んだ参照アプリ（フレームワークの reference-app/）を、段階1の機械判定にかけ、
/// known-bugs.json の各バグを、決められた判定器が見つけたかを照らし合わせる。フレームワークを変えたら回す。
/// </summary>
public static class SelftestCommands
{
    private static void Copy(string source, string destination, string? skip)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var first = relative.Split(Path.DirectorySeparatorChar)[0];
            if ((skip != null && first == skip) || first == "node_modules")
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>custos selftest [--data-dir-selftest &lt;dir&gt;] [--keep]。</summary>
    public static async Task<int> RunAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var framework = context.Workspace.FrameworkRepo;
        var source = Path.Combine(framework, "reference-app");
        var known = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(source, "known-bugs.json"), cancellationToken).ConfigureAwait(false))?["bugs"] as JsonArray ?? [];

        // 対象アプリ・検証用リポジトリ・データの置き場所は、互いの外に置く決まり（制約6）。参照アプリと検証設定を、別々の場所に写して使う
        var root = Path.GetFullPath(args.Get("data-dir-selftest") ?? Path.Combine(Path.GetDirectoryName(context.Workspace.DataDir) ?? context.Workspace.DataDir, "selftest"));
        var app = Path.Combine(root, "app");
        var verify = Path.Combine(root, "verify");
        var data = Path.Combine(root, "data");
        foreach (var dir in new[] { app, verify })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        Copy(source, app, skip: "verify");
        Copy(Path.Combine(source, "verify"), verify, skip: null);
        File.Delete(Path.Combine(app, "known-bugs.json"));
        (await ProcessRunner.RunAsync("git", ["-C", app, "init", "--quiet"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureSuccess("参照アプリの git init");
        (await ProcessRunner.RunAsync("git", ["-C", app, "add", "-A"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureSuccess("参照アプリの git add");
        (await ProcessRunner.RunAsync("git", ["-C", app, "-c", "user.name=custos", "-c", "user.email=custos@localhost", "commit", "--quiet", "-m", "reference app"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureSuccess("参照アプリの git commit");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["CUSTOS_VERIFY_REPO"] = verify,
            ["CUSTOS_APP_REPO"] = app,
            ["CUSTOS_DATA_DIR"] = data,
            ["CUSTOS_FRAMEWORK_REPO"] = framework,
        };
        var dotnet = Environment.ProcessPath ?? "dotnet";
        var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new CustosException("custos の場所がわかりません");

        async Task<ProcessResult> Custos(params string[] arguments)
        {
            CliContext.Log("selftest: custos " + string.Join(' ', arguments));
            var result = await ProcessRunner.RunAsync(dotnet, [dll, .. arguments], environment, null, null, TimeSpan.FromHours(2), cancellationToken).ConfigureAwait(false);
            CliContext.Log(result.StandardError.Length > 2000 ? result.StandardError[^2000..] : result.StandardError);
            return result;
        }

        var up = await Custos("up").ConfigureAwait(false);
        if (!up.Succeeded)
        {
            throw new CustosException("参照アプリの検証環境を起動できません");
        }

        await Custos("stage1", "--games", "2", "--no-register").ConfigureAwait(false);
        var runs = Path.Combine(data, "results", "runs");
        var latest = Directory.GetDirectories(runs, "*-stage1").OrderDescending(StringComparer.Ordinal).FirstOrDefault() ?? throw new CustosException("段階1の結果がありません");
        var findings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(latest, "findings.json"), cancellationToken).ConfigureAwait(false)) as JsonArray ?? [];
        var found = findings.OfType<JsonObject>().Select(f => f["finding"] as JsonObject).OfType<JsonObject>().Select(f => (Judge: f["judge"]?.ToString(), Rule: f["rule"]?.ToString(), Summary: f["summary"]?.ToString())).ToList();
        if (!args.Flag("keep"))
        {
            await Custos("down").ConfigureAwait(false);
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# 参照アプリの回帰テスト（{Path.GetFileName(latest)}）");
        text.AppendLine();
        text.AppendLine("| バグ | 内容 | 見つけるべき判定器 | 結果 |");
        text.AppendLine("|---|---|---|---|");
        var missed = 0;
        foreach (var bug in known.OfType<JsonObject>())
        {
            var judge = bug["judge"]?.ToString();
            var rule = bug["rule"]?.ToString();
            var hit = found.FirstOrDefault(f => f.Rule == rule && (f.Judge == judge || judge == null));
            if (hit.Rule == null)
            {
                missed++;
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"| {bug["id"]} | {bug["description"]} | {judge}:{rule} | {(hit.Rule != null ? "検出した" : "**見逃した**")} |");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{known.Count - missed}/{known.Count} 件を検出。判定器が見つけたもの全体：{found.Count} 件（{latest}）");
        var report = Path.Combine(latest, "selftest.md");
        await File.WriteAllTextAsync(report, text.ToString(), cancellationToken).ConfigureAwait(false);
        CliContext.Out(text.ToString());
        return missed == 0 ? 0 : 1;
    }
}
