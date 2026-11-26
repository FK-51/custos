using System.Globalization;
using System.Text;
using Custos.Core.Util;

namespace Custos.Registry.Bugs;

/// <summary>書き出しの結果。</summary>
public sealed record RegistryExport(string JsonLinesPath, string MarkdownPath, int Count);

/// <summary>
/// 登録簿をテキストに書き出し、検証用リポジトリに記録する（設計書 第20.9節、指示書 タスク11 `custos bug export`）。
/// 実体は SQLite のまま、書き出しは差分が読めるように ID の順に並べる。
/// </summary>
public static class RegistryExporter
{
    /// <summary>JSON Lines（bugs.jsonl）と Markdown の表（bugs.md）を書く。</summary>
    public static RegistryExport Export(BugRegistryAdmin registry, string exportDir)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDir);
        Directory.CreateDirectory(exportDir);
        var bugs = registry.List();
        var jsonPath = Path.Combine(exportDir, "bugs.jsonl");
        var mdPath = Path.Combine(exportDir, "bugs.md");
        var json = new StringBuilder();
        foreach (var bug in bugs)
        {
            json.Append(Json.SerializeCompact(new
            {
                id = bug.Id,
                found_at = bug.FoundAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                found_by = bug.FoundBy,
                found_stage = bug.FoundStage,
                category = bug.Category,
                reach = bug.Reach,
                visibility = bug.Visibility,
                severity = bug.Severity,
                introduced_in = bug.IntroducedIn,
                fixed_in = bug.FixedIn,
                confirm_ref = bug.ConfirmRef,
                revivable = bug.Revivable,
                used_in = bug.UsedIn,
                title = bug.Title,
                detail = bug.Detail,
                source = bug.Source,
                links = registry.Links(bug.Id).Select(l => new { kind = l.Kind, reference = l.Reference }).ToList(),
            }));
            json.Append('\n');
        }

        File.WriteAllText(jsonPath, json.ToString(), new UTF8Encoding(false));
        File.WriteAllText(mdPath, ToMarkdown(bugs), new UTF8Encoding(false));
        return new RegistryExport(jsonPath, mdPath, bugs.Count);
    }

    /// <summary>Markdown の表にする。</summary>
    public static string ToMarkdown(IReadOnlyList<BugRecord> bugs)
    {
        ArgumentNullException.ThrowIfNull(bugs);
        var sb = new StringBuilder();
        sb.AppendLine("# バグの登録簿（書き出し）");
        sb.AppendLine();
        sb.AppendLine("実体は SQLite（データの置き場所）。この表は `custos bug export` で書き出したもの。");
        sb.AppendLine();
        sb.AppendLine("| ID | 説明 | found_by | found_stage | 重大度 | 種類 | 到達 | 目立ち度 | 混入 | 修正 | 確認用シナリオ | 再注入 | 使用済み |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var bug in bugs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {bug.Id} | {Cell(bug.Title)} | {Cell(bug.FoundBy)} | {bug.FoundStage} | {Cell(bug.Severity)} | {Cell(bug.Category)} | {bug.Reach?.ToString(CultureInfo.InvariantCulture) ?? "-"} | {Cell(bug.Visibility)} | {Short(bug.IntroducedIn)} | {Short(bug.FixedIn)} | {Cell(bug.ConfirmRef)} | {(bug.Revivable is { } r ? (r ? "可" : "不可") : "-")} | {Cell(bug.UsedIn)} |");
        }

        return sb.ToString();
    }

    private static string Cell(string? text) =>
        string.IsNullOrEmpty(text) ? "-" : text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "-" : (hash.Length > 10 ? hash[..10] : hash);
}
