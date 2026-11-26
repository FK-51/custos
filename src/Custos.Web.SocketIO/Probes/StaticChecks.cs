using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Util;

namespace Custos.Web.SocketIO.Probes;

/// <summary>静的解析の規則。</summary>
public sealed record StaticRule(string Id, Regex Pattern, Severity Severity, int Stage, string Category, string Description);

/// <summary>
/// 依存ライブラリの検査と静的解析（設計書 第26.7節。AIは使わない）。対象アプリのリポジトリは読むだけ。
/// - 静的解析：SQL の組み立て方、危険な関数の使用、秘密の値の埋め込み。パターンの一致は候補（段階2）。秘密の値の埋め込みだけは段階1とする。
/// - 依存ライブラリ：package-lock.json の版を、検証用リポジトリの rules/advisories.json（脆弱性の情報）と照らし合わせる。
///   検証環境は外部へ通信しないので（指示書 制約4）、脆弱性の情報は人間が外で更新して置く。ないときは「未確認」とする。
/// </summary>
public static partial class StaticChecks
{
    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];
    private static readonly string[] SkippedDirectories = ["node_modules", ".git", "dist", ".next", "build", "coverage", ".turbo", "__tests__", "__mocks__"];

    /// <summary>規則の一覧。</summary>
    public static IReadOnlyList<StaticRule> Rules { get; } =
    [
        new("raw_sql_unsafe", RawUnsafe(), Severity.High, 2, "sqli", "文字列から SQL を組み立てる関数（$queryRawUnsafe / $executeRawUnsafe）"),
        new("sql_template_concat", SqlConcat(), Severity.High, 2, "sqli", "SQL の文字列に値を埋め込んでいる（テンプレート文字列や + で連結）"),
        new("eval", Eval(), Severity.High, 2, "server_intrusion", "eval / new Function（文字列をコードとして実行）"),
        new("child_process", ChildProcess(), Severity.Medium, 2, "server_intrusion", "外部のコマンドの実行（child_process）"),
        new("inner_html", InnerHtml(), Severity.Medium, 2, "xss", "innerHTML などへの代入（空文字以外）"),
        new("dangerously_set_inner_html", DangerousHtml(), Severity.Medium, 2, "xss", "dangerouslySetInnerHTML"),
        new("document_write", DocumentWrite(), Severity.Medium, 2, "xss", "document.write"),
        new("private_key", PrivateKey(), Severity.Critical, 1, "secret", "秘密鍵の埋め込み"),
        new("cloud_key", CloudKey(), Severity.Critical, 1, "secret", "クラウドやサービスの鍵の埋め込み（AKIA…、sk-…、ghp_…、xox…）"),
        new("hardcoded_secret", HardcodedSecret(), Severity.High, 2, "secret", "秘密の値らしき文字列の埋め込み（key、secret、token、password への代入）"),
    ];

    /// <summary>実行する。</summary>
    public static IReadOnlyList<Finding> Run(Workspace workspace, Run run)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(run);
        var findings = new List<Finding>();
        var matches = new JsonArray();
        var files = 0;
        foreach (var file in SourceFiles(workspace.AppRepo))
        {
            files++;
            var relative = Path.GetRelativePath(workspace.AppRepo, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal) || line.TrimStart().StartsWith('*'))
                {
                    continue;
                }

                foreach (var rule in Rules.Where(r => r.Pattern.IsMatch(line)))
                {
                    var location = $"{relative}:{(i + 1).ToString(CultureInfo.InvariantCulture)}";
                    matches.Add(new JsonObject { ["rule"] = rule.Id, ["file"] = relative, ["line"] = i + 1, ["text"] = Mask(line.Trim(), rule) });
                    findings.Add(Finding.Create("static_analysis", rule.Id, rule.Severity, rule.Stage, rule.Category, $"{rule.Description}：{location}", Mask(line.Trim(), rule), [new Evidence("一致した行", "static-analysis.json")], location));
                }
            }
        }

        run.WriteJson("static-analysis.json", new JsonObject { ["files"] = files, ["rules"] = Rules.Count, ["matches"] = matches });
        findings.AddRange(Dependencies(workspace, run));
        return findings;
    }

    /// <summary>秘密の値は記録に残さない（先頭だけ）。</summary>
    private static string Mask(string text, StaticRule rule) =>
        rule.Category == "secret" ? rule.Pattern.Replace(text, m => m.Value.Length > 8 ? m.Value[..8] + "…（伏せた）" : m.Value) : (text.Length > 200 ? text[..200] + "…" : text);

    private static IEnumerable<string> SourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (!SkippedDirectories.Contains(name, StringComparer.Ordinal) && !name.StartsWith('.'))
                {
                    stack.Push(sub);
                }
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (SourceExtensions.Contains(Path.GetExtension(file), StringComparer.Ordinal) && !name.Contains(".test.", StringComparison.Ordinal) && !name.Contains(".spec.", StringComparison.Ordinal) && !name.EndsWith(".d.ts", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>依存ライブラリの版を、脆弱性の情報と照らし合わせる。</summary>
    private static List<Finding> Dependencies(Workspace workspace, Run run)
    {
        var lockFile = Path.Combine(workspace.AppRepo, "package-lock.json");
        var advisoriesFile = Path.Combine(workspace.VerifyRepo, "rules", "advisories.json");
        var findings = new List<Finding>();
        if (!File.Exists(lockFile))
        {
            return [Finding.Create("dependency_audit", "no_lockfile", Severity.Medium, 1, "dependency", "package-lock.json がないため、依存ライブラリの版を確かめられません（版が固定されていない）", null, [], "no_lockfile")];
        }

        var packages = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using (var document = JsonDocument.Parse(File.ReadAllText(lockFile)))
        {
            if (document.RootElement.TryGetProperty("packages", out var list))
            {
                foreach (var entry in list.EnumerateObject())
                {
                    var index = entry.Name.LastIndexOf("node_modules/", StringComparison.Ordinal);
                    if (index < 0 || !entry.Value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = entry.Name[(index + "node_modules/".Length)..];
                    if (!packages.TryGetValue(name, out var versions))
                    {
                        versions = new HashSet<string>(StringComparer.Ordinal);
                        packages[name] = versions;
                    }

                    versions.Add(version.GetString() ?? string.Empty);
                }
            }
        }

        var report = new JsonObject { ["packages"] = packages.Count, ["versions"] = packages.Sum(p => p.Value.Count) };
        if (!File.Exists(advisoriesFile))
        {
            report["advisories"] = "なし（rules/advisories.json がない）";
            run.WriteJson("dependency-audit.json", report);
            findings.Add(Finding.Create("dependency_audit", "no_advisories", Severity.Low, 1, "coverage", $"脆弱性の情報（rules/advisories.json）がないため、依存ライブラリ {packages.Count} 個の脆弱性は未確認です（検証環境は外部へ通信しない）", null, [new Evidence("依存ライブラリ", "dependency-audit.json")], "no_advisories"));
            return findings;
        }

        var advisories = JsonNode.Parse(File.ReadAllText(advisoriesFile)) as JsonArray ?? [];
        var hits = new JsonArray();
        foreach (var advisory in advisories.OfType<JsonObject>())
        {
            var name = advisory["package"]?.GetValue<string>() ?? string.Empty;
            var range = advisory["vulnerable"]?.GetValue<string>() ?? string.Empty;
            if (!packages.TryGetValue(name, out var versions))
            {
                continue;
            }

            foreach (var version in versions.Where(v => SemverRange.Matches(v, range)))
            {
                var severity = Enum.TryParse<Severity>(advisory["severity"]?.GetValue<string>(), true, out var s) ? s : Severity.High;
                hits.Add(new JsonObject { ["package"] = name, ["version"] = version, ["advisory"] = advisory.DeepClone() });
                findings.Add(Finding.Create("dependency_audit", "vulnerable_dependency", severity, 1, "dependency", $"脆弱性のある版の依存ライブラリ：{name}@{version}（{advisory["id"]}：{advisory["title"]}）", null, [new Evidence("依存ライブラリ", "dependency-audit.json")], $"{name}@{version}:{advisory["id"]}"));
            }
        }

        report["advisories"] = advisories.Count;
        report["hits"] = hits;
        run.WriteJson("dependency-audit.json", report);
        return findings;
    }

    [GeneratedRegex(@"\$(queryRawUnsafe|executeRawUnsafe)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex RawUnsafe();

    [GeneratedRegex(@"(?i)(`\s*(select|insert|update|delete)\b[^`]*\$\{)|(""\s*(select|insert|update|delete)\b[^""]*""\s*\+)|('\s*(select|insert|update|delete)\b[^']*'\s*\+)", RegexOptions.CultureInvariant)]
    private static partial Regex SqlConcat();

    [GeneratedRegex(@"(^|[^\w.])eval\s*\(|new\s+Function\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex Eval();

    [GeneratedRegex(@"require\(\s*['""](node:)?child_process['""]\s*\)|from\s+['""](node:)?child_process['""]", RegexOptions.CultureInvariant)]
    private static partial Regex ChildProcess();

    [GeneratedRegex(@"\.(innerHTML|outerHTML)\s*=\s*(?!\s*['""`]\s*['""`]\s*;?\s*$)|insertAdjacentHTML\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex InnerHtml();

    [GeneratedRegex(@"dangerouslySetInnerHTML", RegexOptions.CultureInvariant)]
    private static partial Regex DangerousHtml();

    [GeneratedRegex(@"document\.write(ln)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentWrite();

    [GeneratedRegex(@"-----BEGIN ([A-Z ]+ )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{10,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex CloudKey();

    [GeneratedRegex(@"(?i)\b(api[_-]?key|secret|token|password|passwd)\b\s*[:=]\s*['""][^'""\s]{12,}['""]", RegexOptions.CultureInvariant)]
    private static partial Regex HardcodedSecret();
}

/// <summary>npm の版の範囲の、簡単な照合（"&lt;1.2.3"、"&gt;=1.0.0 &lt;1.2.3"、"1.2.3"、"||" で複数）。</summary>
public static class SemverRange
{
    /// <summary>版が範囲に入るか。</summary>
    public static bool Matches(string version, string range)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(range);
        var v = Parse(version);
        if (v == null)
        {
            return false;
        }

        foreach (var alternative in range.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var all = true;
            foreach (var part in alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var op = new string(part.TakeWhile(c => c is '<' or '>' or '=').ToArray());
                var bound = Parse(part[op.Length..]);
                if (bound == null)
                {
                    all = false;
                    break;
                }

                var c = Compare(v, bound);
                var ok = op switch
                {
                    "<" => c < 0,
                    "<=" => c <= 0,
                    ">" => c > 0,
                    ">=" => c >= 0,
                    _ => c == 0,
                };
                if (!ok)
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    private static int[]? Parse(string text)
    {
        var core = text.Trim().TrimStart('v').Split('-', '+')[0].Split('.');
        if (core.Length is < 1 or > 3)
        {
            return null;
        }

        var numbers = new int[3];
        for (var i = 0; i < core.Length; i++)
        {
            if (!int.TryParse(core[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    private static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }

        return 0;
    }
}
