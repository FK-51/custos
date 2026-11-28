using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Custos.Core.Results;
using Custos.Core.Util;
using Custos.Web.SocketIO.Agents;

namespace Custos.Web.SocketIO.Calibration;

/// <summary>校正用バグの下書き（ID を振る前）。</summary>
public sealed record SeedDraft(string Kind, string Description, string Patch, string Confirm, bool Crash, string Origin);

/// <summary>下書きを確かめた結果。</summary>
public sealed record SeedDraftCheck(SeedDraft Draft, bool Ok, string Detail);

/// <summary>
/// シード作成役（設計書 第19.8節、第12.6節の権限の表）。アプリのコードは読むだけで、シード置き場にだけ書く。
/// 探索の結果、判定器、検証の設定は見せない。作ったものはフレームワークが確かめてから、次の ID で検証用リポジトリの seeds/ に置く。
/// 使えるかどうか（効いているか、自明すぎないか、層）は、これまでどおり custos seeds check で決める。
/// </summary>
public static class SeedAuthoring
{
    /// <summary>目印になる言葉（パッチの足した行に入れてはいけない。第19.4節）。</summary>
    private static readonly Regex Marker = new(@"\b(bug|seed|calib\w*|fault|inject\w*|mutat\w*)\b|校正|仕込|バグ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// 下書きを確かめる：アプリの HEAD に単独で当たること、足した行に目印がないこと、種類と説明と確認用シナリオがあること。
    /// </summary>
    public static async Task<IReadOnlyList<SeedDraftCheck>> CheckAsync(string appRepo, IReadOnlyList<SeedDraft> drafts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var results = new List<SeedDraftCheck>();
        var tmp = Path.Combine(Path.GetTempPath(), "custos-seed-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var draft in drafts)
            {
                var problems = new List<string>();
                if (string.IsNullOrWhiteSpace(draft.Kind) || string.IsNullOrWhiteSpace(draft.Description))
                {
                    problems.Add("種類か説明がない");
                }

                if (string.IsNullOrWhiteSpace(draft.Confirm) || !draft.Confirm.Contains("steps:", StringComparison.Ordinal))
                {
                    problems.Add("確認用シナリオがない");
                }

                var added = draft.Patch.Split('\n').Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal)).ToList();
                if (added.Count == 0)
                {
                    problems.Add("パッチに足した行がない");
                }
                else if (added.FirstOrDefault(l => Marker.IsMatch(l)) is { } marked)
                {
                    problems.Add("足した行に目印の言葉がある：" + marked.Trim());
                }

                var patchFile = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".diff");
                await File.WriteAllTextAsync(patchFile, draft.Patch, cancellationToken).ConfigureAwait(false);
                var apply = await ProcessRunner.RunAsync("git", ["-C", appRepo, "apply", "--check", patchFile], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!apply.Succeeded)
                {
                    problems.Add("アプリの HEAD に当たらない：" + apply.StandardError.Trim());
                }

                results.Add(new SeedDraftCheck(draft, problems.Count == 0, problems.Count == 0 ? "使える" : string.Join("／", problems)));
            }
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }

        return results;
    }

    /// <summary>確かめた下書きを、次の ID（seed-NNN）で seeds/ に置く。返り値は振った ID。</summary>
    public static IReadOnlyList<string> Save(Workspace workspace, IEnumerable<SeedDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(drafts);
        var root = Path.Combine(workspace.VerifyRepo, "seeds");
        Directory.CreateDirectory(root);
        var next = Directory.GetDirectories(root).Select(Path.GetFileName)
            .Select(n => n != null && n.StartsWith("seed-", StringComparison.Ordinal) && int.TryParse(n[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .DefaultIfEmpty(0).Max() + 1;
        var ids = new List<string>();
        foreach (var draft in drafts)
        {
            var id = "seed-" + next.ToString("D3", CultureInfo.InvariantCulture);
            next++;
            var dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "patch.diff"), draft.Patch.EndsWith('\n') ? draft.Patch : draft.Patch + "\n");
            File.WriteAllText(Path.Combine(dir, "confirm.yaml"), draft.Confirm.Replace("{id}", id, StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(dir, "meta.yaml"), string.Create(CultureInfo.InvariantCulture, $"""
                # 対応表（検証用リポジトリだけに置く。パッチとシナリオには目印を書かない。第19.4節）
                version: 1
                kind: {draft.Kind}
                description: {Yaml(draft.Description)}
                confirm: confirm.yaml
                crash: {(draft.Crash ? "true" : "false")}
                origin: {draft.Origin}

                """));
            ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// 確認用シナリオの雛形：検証用リポジトリの scenarios/bot-game.yaml（1対局。段階1のボットの対局と同じ）を写し、{game} と {seed} を埋める。
    /// アプリごとの準備の手順を、フレームワークが知らずに済む。
    /// </summary>
    public static string ConfirmFromBotGame(Workspace workspace, int seed)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var path = Path.Combine(workspace.VerifyRepo, "scenarios", "bot-game.yaml");
        if (!File.Exists(path))
        {
            throw new CustosException("確認用シナリオの雛形がありません：scenarios/bot-game.yaml");
        }

        var text = File.ReadAllText(path)
            .Replace("{game}", "1", StringComparison.Ordinal)
            .Replace("\"{seed}\"", seed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{seed}", seed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var body = Regex.Replace(text, @"^name:.*$", "name: confirm-{id}", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        return "# 確認用シナリオ（探索役には見せない）。1対局を最後まで回す\n" + string.Join('\n', body.Split('\n').Where(l => !l.StartsWith('#')));
    }

    private static string Yaml(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "\"";
}

/// <summary>
/// 代役のシード作成役：アプリのコードに、機械的な変異（ミューテーション）を1か所ずつ入れたパッチを作る。
/// 変異の種類：比べ方の境界（&lt; と &lt;=、&gt; と &gt;=）、等しいかどうか（=== と !==）、論理（&amp;&amp; と ||）、小さな数（±1）、真偽（true と false）。
/// AI のシード作成役と違い、意味を考えずに作るので、効かないものや自明なものも多い。使えるかどうかは seeds check で決める。
/// 決定的（同じシードなら同じ下書き）。
/// </summary>
public static class MutationSeedAuthor
{
    private sealed record Operator(string Name, string Kind, Regex Pattern, Func<Match, string> Replace, string Description);

    private static readonly Operator[] Operators =
    [
        new("boundary", "condition", new Regex(@" (<|>|<=|>=) ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), m => m.Groups[1].Value switch { "<" => " <= ", ">" => " >= ", "<=" => " < ", _ => " > " }, "比べ方の境界"),
        new("equality", "condition", new Regex(" (===|!==) ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), m => m.Groups[1].Value == "===" ? " !== " : " === ", "等しいかどうかの判定"),
        new("logic", "condition", new Regex(@" (&&|\|\|) ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), m => m.Groups[1].Value == "&&" ? " || " : " && ", "条件のつなぎ方"),
        new("number", "effect", new Regex(@"((?::|=|\+|-) ?)([1-9])\b(?![.\d])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), m => m.Groups[1].Value + (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture), "数値"),
        new("boolean", "effect", new Regex(@"(?<=(?:[:=(,]|return) ?)\b(true|false)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), m => m.Value == "true" ? "false" : "true", "真偽の値"),
    ];

    /// <summary>変異を作る場所の候補（ファイル、行、変異の種類）。</summary>
    private sealed record Site(string File, int Line, Operator Op, int Index, string Before, string After);

    /// <summary>作る。paths はアプリのリポジトリの中の対象（前方一致）。</summary>
    public static async Task<IReadOnlyList<SeedDraft>> GenerateAsync(Workspace workspace, string appRepo, int count, int seed, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(paths);
        var files = (await ProcessRunner.RunAsync("git", ["-C", appRepo, "ls-files"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false))
            .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.EndsWith(".ts", StringComparison.Ordinal) && !f.EndsWith(".d.ts", StringComparison.Ordinal)
                && !f.Contains("__tests__", StringComparison.Ordinal) && !f.Contains(".test.", StringComparison.Ordinal) && !f.Contains("/test/", StringComparison.Ordinal)
                && paths.Any(p => f.StartsWith(p, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
        var sites = new List<Site>();
        foreach (var file in files)
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(appRepo, file), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var code = line.TrimStart();

                // コメント、import、文字列を含む行は避ける（文字列の中を変えても意味がなく、型も壊しやすい）
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith('*') || code.StartsWith("/*", StringComparison.Ordinal)
                    || code.StartsWith("import ", StringComparison.Ordinal) || code.StartsWith("export type", StringComparison.Ordinal) || code.StartsWith("type ", StringComparison.Ordinal)
                    || line.Contains('\'', StringComparison.Ordinal) || line.Contains('"', StringComparison.Ordinal) || line.Contains('`', StringComparison.Ordinal)
                    || line.Contains("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var op in Operators)
                {
                    foreach (Match m in op.Pattern.Matches(line))
                    {
                        var after = line[..m.Index] + op.Replace(m) + line[(m.Index + m.Length)..];
                        if (after != line)
                        {
                            sites.Add(new Site(file, i, op, m.Index, line, after));
                        }
                    }
                }
            }
        }

        // ファイルと変異の種類が偏らないよう、決まった順に混ぜてから、ファイルごとに順に取る
        var random = new Random(seed);
        var byFile = sites.GroupBy(s => s.File).Select(g => new Queue<Site>(g.OrderBy(_ => random.Next()))).OrderBy(_ => random.Next()).ToList();
        var chosen = new List<Site>();
        while (chosen.Count < count && byFile.Any(q => q.Count > 0))
        {
            foreach (var queue in byFile.Where(q => q.Count > 0))
            {
                if (chosen.Count >= count)
                {
                    break;
                }

                chosen.Add(queue.Dequeue());
            }
        }

        var tmp = Path.Combine(Path.GetTempPath(), "custos-mutation-" + Guid.NewGuid().ToString("N"));
        (await ProcessRunner.RunAsync("git", ["clone", "--quiet", "--no-hardlinks", appRepo, tmp], timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureSuccess("変異を作るための複製");
        try
        {
            var drafts = new List<SeedDraft>();
            foreach (var site in chosen)
            {
                var path = Path.Combine(tmp, site.File);
                var original = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var lines = original.Split('\n');
                lines[site.Line] = site.After;
                await File.WriteAllTextAsync(path, string.Join('\n', lines), cancellationToken).ConfigureAwait(false);
                var diff = await ProcessRunner.RunAsync("git", ["-C", tmp, "diff"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(path, original, cancellationToken).ConfigureAwait(false);
                var description = string.Create(CultureInfo.InvariantCulture, $"{site.File}:{site.Line + 1} の{site.Op.Description}を変えた（「{site.Before.Trim()}」→「{site.After.Trim()}」。機械的な変異）");
                drafts.Add(new SeedDraft(site.Op.Kind, description, diff.StandardOutput, SeedAuthoring.ConfirmFromBotGame(workspace, 1000 + drafts.Count), false, "generated"));
            }

            return drafts;
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}

/// <summary>
/// AI のシード作成役（Claude Code）。サンドボックスの /workspace（アプリの複製。探索役・修正役と同じ）で動かし、
/// /workspace/.custos-seeds/&lt;名前&gt;/ に patch.diff、confirm.yaml、meta.json（kind、description、crash）を書かせて回収する。
/// 検証用リポジトリ、判定器、探索の結果は見せない（サンドボックスには付けない。第12.6節）。回収した後、作業用の複製は元に戻す。
/// </summary>
public sealed class ClaudeSeedAuthor
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;

    /// <summary>作る。</summary>
    public ClaudeSeedAuthor(Lab.Lab lab, Run run)
    {
        _lab = lab;
        _run = run;
    }

    /// <summary>役割の指示。</summary>
    public static string Prompt(int count) => string.Create(CultureInfo.InvariantCulture, $$"""
        あなたはシード作成役です。/workspace のアプリのリポジトリのコードを読み、検証の仕組みの検出率を測るための「校正用バグ」を {{count}} 件作ってください。
        - 1件ごとに /workspace/.custos-seeds/<半角英数字の名前>/ を作り、次の3つを書く：
          - patch.diff：/workspace のリポジトリのルートで git apply できる差分（git diff の出力）。1か所の誤り。各件は単独で当たること。
          - meta.json：{"kind": "effect|target|condition|order|hidden_info|rule_break|persistence|crash", "description": "何が誤っているか（正しくはどうか）", "crash": false}
          - confirm.yaml：そのバグが起きる場面まで進めるシナリオ。書き方は /workspace/.custos-seeds/_template.yaml を写して、ボットの乱数（seed）と方針（policy: random / prefer と prefer: カードの番号など）を変える。
        - コード、コメント、名前に、バグだと分かる言葉（bug、seed、校正 など）を入れない。普通の変更に見えるものにする。
        - 作業の後、/workspace のアプリのファイルは元に戻す（git checkout -- . で戻してよい）。.custos-seeds の下だけを残す。
        - 種類と、届きやすさ（最初の数手で起きる、特定のカードのときだけ起きる、終盤だけ起きる）を散らす。
        - 検証用の設定、判定の仕組み、テスト用API、DB は使えません。探す必要もありません。
        """);

    /// <summary>実行して、下書きを回収する。</summary>
    public async Task<IReadOnlyList<SeedDraft>> RunAsync(int count, string? model, CancellationToken cancellationToken)
    {
        var launcher = new ClaudeCodeLauncher(_lab);
        if (launcher.Unavailable() is { } reason)
        {
            throw new CustosException("シード作成役（Claude Code）を起動できません：" + reason);
        }

        var workspace = _lab.Workspace.SandboxWorkspaceDir;
        var outDir = Path.Combine(workspace, ".custos-seeds");
        if (Directory.Exists(outDir))
        {
            Directory.Delete(outDir, recursive: true);
        }

        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "_template.yaml"), SeedAuthoring.ConfirmFromBotGame(_lab.Workspace, 1), cancellationToken).ConfigureAwait(false);
        var transcript = Path.Combine(_run.Dir, "seed-author", "transcript.txt");
        var result = await launcher.RunAsync("seed_author", model, Prompt(count), ["Bash", "Read", "Edit", "Write", "Glob", "Grep"], null, Prompt(count), transcript, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!result.Ok)
            {
                throw new CustosException("シード作成役が失敗しました：" + result.Error);
            }

            var drafts = new List<SeedDraft>();
            foreach (var dir in Directory.GetDirectories(outDir).Order(StringComparer.Ordinal))
            {
                var meta = Path.Combine(dir, "meta.json");
                var patch = Path.Combine(dir, "patch.diff");
                var confirm = Path.Combine(dir, "confirm.yaml");
                if (!File.Exists(meta) || !File.Exists(patch) || !File.Exists(confirm))
                {
                    continue;
                }

                var node = JsonNode.Parse(await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false)) as JsonObject ?? [];
                drafts.Add(new SeedDraft(
                    node["kind"]?.ToString() ?? string.Empty,
                    node["description"]?.ToString() ?? string.Empty,
                    await File.ReadAllTextAsync(patch, cancellationToken).ConfigureAwait(false),
                    await File.ReadAllTextAsync(confirm, cancellationToken).ConfigureAwait(false),
                    node["crash"]?.GetValue<bool>() == true,
                    "generated"));
            }

            return drafts;
        }
        finally
        {
            // 作業用の複製を元に戻す（シード作成役が残した変更を、次の探索役・修正役に見せない）
            Directory.Delete(outDir, recursive: true);
            await ProcessRunner.RunAsync("git", ["-C", workspace, "checkout", "--", "."], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
            await ProcessRunner.RunAsync("git", ["-C", workspace, "clean", "-fdq"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
