using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Util;

namespace Custos.Web.SocketIO.Agents;

/// <summary>エージェントの1回の実行の結果。</summary>
public sealed record AgentRunResult(bool Ok, string? Error, string? ResultText, JsonNode? Structured, TrialCost Cost, string? TranscriptPath);

/// <summary>
/// 役割ごとの AI エージェント（Claude Code）の起動（設計書 第12.6節「AIエージェントの起動」、第18.9節）。
/// - サンドボックスのコンテナの中で、非対話モード（-p）かつ --bare で起動する。作業ディレクトリのフックや指示（CLAUDE.md）、MCP は読まない。
/// - 役割ごとの指示、モデル、使えるツール、予算は、フレームワークが起動時に明示的に渡す。
/// - 結果は構造化した形（--output-format json、--json-schema）で受け取り、費用をレポートのコスト欄に記録する。
/// - モデルの API への通信は、出口のプロキシ（許可した宛先だけ）を通す。API キーは起動したプロセスにだけ環境変数で渡し、ファイルに残さない。
/// </summary>
public sealed class ClaudeCodeLauncher
{
    private readonly Lab.Lab _lab;

    /// <summary>作る。</summary>
    public ClaudeCodeLauncher(Lab.Lab lab)
    {
        _lab = lab;
    }

    /// <summary>使えるか（API キーがあり、出口のプロキシ付きで起動しているか）。使えなければ理由。</summary>
    public string? Unavailable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
        {
            return "ANTHROPIC_API_KEY がありません（--bare は API キー以外の認証を読まない。設計書 第12.6節）";
        }

        if (!_lab.Config.Environment.Nodes.Sandbox)
        {
            return "environment.yaml でサンドボックスを使っていません";
        }

        return null;
    }

    /// <summary>起動のコマンドライン（記録と確認のために、文字列としても返す）。</summary>
    public static IReadOnlyList<string> Arguments(string container, string? model, string systemPrompt, IReadOnlyList<string> allowedTools, double? maxBudgetUsd, string? jsonSchema, string prompt)
    {
        var args = new List<string>
        {
            "exec", "-i", "-e", "ANTHROPIC_API_KEY", "-e", "HTTPS_PROXY=http://egress:3128", "-e", "HTTP_PROXY=http://egress:3128", "-e", "NO_PROXY=localhost,127.0.0.1,edge",
            "-w", "/workspace", container,
            "claude", "-p", "--bare", "--output-format", "json", "--no-session-persistence", "--strict-mcp-config",
            "--permission-mode", "bypassPermissions",
            "--append-system-prompt", systemPrompt,
        };
        if (model != null)
        {
            args.Add("--model");
            args.Add(model);
        }

        if (allowedTools.Count > 0)
        {
            args.Add("--allowedTools");
            args.Add(string.Join(",", allowedTools));
        }

        if (maxBudgetUsd is { } budget)
        {
            args.Add("--max-budget-usd");
            args.Add(budget.ToString("0.##", CultureInfo.InvariantCulture));
        }

        if (jsonSchema != null)
        {
            args.Add("--json-schema");
            args.Add(jsonSchema);
        }

        args.Add(prompt);
        return args;
    }

    /// <summary>実行する。transcriptPath に出力を残す。</summary>
    public async Task<AgentRunResult> RunAsync(string role, string? model, string systemPrompt, IReadOnlyList<string> allowedTools, string? jsonSchema, string prompt, string transcriptPath, CancellationToken cancellationToken)
    {
        if (Unavailable() is { } reason)
        {
            return new AgentRunResult(false, reason, null, null, new TrialCost(0, 0, null, null), null);
        }

        var container = Lab.Containers.Sandbox(_lab.Project);
        var args = Arguments(container, model, systemPrompt, allowedTools, _lab.Config.Agents?.MaxBudgetUsdPerRun, jsonSchema, prompt);
        var watch = Stopwatch.StartNew();
        var result = await _lab.Docker.RunAsync(args, TimeSpan.FromMinutes(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath) ?? ".");
        await File.WriteAllTextAsync(transcriptPath, $"# role: {role}\n# model: {model}\n# exit: {result.ExitCode}\n{result.StandardOutput}\n# stderr\n{result.StandardError}", cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new AgentRunResult(false, $"エージェントが失敗しました（終了コード {result.ExitCode}）：{Tail(result.StandardError)}", null, null, new TrialCost(0, watch.Elapsed.TotalSeconds, null, null), transcriptPath);
        }

        var output = JsonNode.Parse(result.StandardOutput) as JsonObject ?? [];
        var cost = new TrialCost(
            output["total_cost_usd"]?.GetValue<double>() ?? 0,
            watch.Elapsed.TotalSeconds,
            output["usage"]?["input_tokens"]?.GetValue<long>(),
            output["usage"]?["output_tokens"]?.GetValue<long>());
        var text = output["result"]?.ToString();
        var structured = output["structured_output"]?.DeepClone() ?? TryParse(text);
        return new AgentRunResult(output["is_error"]?.GetValue<bool>() != true, output["is_error"]?.GetValue<bool>() == true ? text : null, text, structured, cost, transcriptPath);
    }

    private static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text[start..(end + 1)]);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Tail(string text) => text.Length > 400 ? text[^400..] : text;
}

/// <summary>
/// 役割ごとの指示（第12.6節の「役割ごとに見せる範囲」に従う）。判定器、検証設定、判定用データの中身は書かない。
/// </summary>
public static class RolePrompts
{
    /// <summary>探索役。</summary>
    public static string Explorer(string publicUrl) => $$"""
        あなたは Web アプリの探索役です。割り当てられた確認対象を、利用者として実際に操作して確かめます。
        - アプリは {{publicUrl}} で動いています。Socket.IO のクライアントは require('socket.io-client') で使えます（{ path: '/socket.io', addTrailingSlash: false }）。
        - /workspace はアプリのリポジトリの複製です（読み取りのみ。変更しないでください）。
        - テスト用API（/__verify/*）、DB、判定の仕組みは使えません。使おうとしないでください。
        - 試した操作（送ったイベントと値）は、すべてレポートの actions に正確に書いてください。書いた操作は通信の記録と機械的に照合されます。
        - 結論（bug / no_bug）は、観察した事実に基づいて書いてください。確かめられなかったことは notes に「未確認」と書いてください。
        """;

    /// <summary>攻撃役。</summary>
    public static string Attacker(string publicUrl) => $$"""
        あなたは Web アプリの攻撃役です。利用者として、規則の抜け道や、複数の手順を組み合わせた攻撃を探します（設計書 第26.7節）。
        - アプリは {{publicUrl}} で動いています。外部への通信はできません。
        - 目標：相手の手札を知る、他人の席を使う、手番や規則を破る、サーバーを落とす、他人の画面でスクリプトを動かす、対局を止める。
        - テスト用API、DB、判定の仕組みは使えません。試した操作はすべて actions に正確に書いてください。
        """;

    /// <summary>修正役。何が失敗したかは渡すが、どう判定しているかは渡さない（第12.6節）。</summary>
    public const string Fixer = """
        あなたはアプリの修正役です。/workspace のアプリのリポジトリで、報告された不具合を直してください。
        - 渡すのは、再現手順と証拠だけです。判定の仕組みや検証の設定は見せません。
        - 検証中かどうかを見分けて振る舞いを変えるコード（ヘッダー、ユーザーエージェント、ホスト名、環境変数での分岐）は書かないでください。
        - 直したら、変更の要約を notes に書いてください。コミットはしないでください（フレームワークが種類を付けてコミットします）。
        """;

    /// <summary>探索のレポートの JSON スキーマ（第12.4節）。</summary>
    public const string ReportSchema = """
        {"type":"object","required":["result","actions","notes"],"properties":{
          "result":{"type":"string","enum":["bug","no_bug"]},
          "actions":{"type":"array","items":{"type":"object","required":["user","event","args"],"properties":{"user":{"type":"string"},"event":{"type":"string"},"args":{"type":"array"},"note":{"type":"string"}}}},
          "users":{"type":"object","additionalProperties":{"type":"string"}},
          "notes":{"type":"string"}}}
        """;
}

/// <summary>
/// 修正役のサンドボックスから、判定器と検証設定に書き込めないことの確認（設計書 第27.2節の段階3の完了の条件）。
/// サンドボックスのコンテナに付いているマウントが作業用の複製だけであること、検証用リポジトリやフレームワークへの書き込みが失敗することを調べる。
/// </summary>
public static class SandboxWriteCheck
{
    /// <summary>確かめる。</summary>
    public static async Task<JsonObject> RunAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var container = Lab.Containers.Sandbox(lab.Project);
        var inspect = await lab.Docker.InspectAsync(container, cancellationToken).ConfigureAwait(false);
        var mounts = (inspect?["Mounts"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new JsonObject
        {
            ["source"] = m["Source"]?.DeepClone(),
            ["destination"] = m["Destination"]?.DeepClone(),
            ["rw"] = m["RW"]?.DeepClone(),
        }).ToList();
        var protectedRoots = new[] { lab.Workspace.VerifyRepo, lab.Workspace.FrameworkRepo, lab.Workspace.ResultsDir, lab.Workspace.RecordsDir, lab.Workspace.RegistryPath, lab.Workspace.SecretsDir };
        // パスとして比べる（文字列の前方一致だと、/…/custos_test が /…/custos の中にあると誤って判定する）
        var exposed = mounts.Where(m => m["source"]?.ToString() is { } source && protectedRoots.Any(r => Workspace.IsInside(source, r) || Workspace.IsInside(r, source))).ToList();
        var attempts = new JsonArray();
        foreach (var path in new[] { lab.Workspace.VerifyRepo, lab.Workspace.FrameworkRepo, lab.Workspace.ResultsDir, "/mnt/verify", "/records", "/results" })
        {
            var probe = await lab.Docker.RunAsync(["exec", container, "sh", "-c", $"touch '{path}/.custos-write-probe' 2>&1 && echo WROTE"], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
            attempts.Add(new JsonObject { ["path"] = path, ["written"] = probe.StandardOutput.Contains("WROTE", StringComparison.Ordinal) });
        }

        var workspace = await lab.Docker.RunAsync(["exec", container, "sh", "-c", "touch /workspace/.custos-write-probe && rm /workspace/.custos-write-probe && echo OK"], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        var ok = exposed.Count == 0 && attempts.All(a => a?["written"]?.GetValue<bool>() != true);
        return new JsonObject
        {
            ["ok"] = ok,
            ["workspace_writable"] = workspace.StandardOutput.Contains("OK", StringComparison.Ordinal),
            ["mounts"] = new JsonArray(mounts.Select(m => (JsonNode?)m).ToArray()),
            ["exposed_protected_mounts"] = exposed.Count,
            ["write_attempts"] = attempts,
        };
    }
}
