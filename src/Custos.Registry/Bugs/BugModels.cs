using System.Globalization;
using System.Text.RegularExpressions;
using Custos.Core.Util;

namespace Custos.Registry.Bugs;

/// <summary>
/// 登録簿の1件（設計書 第20.7節の bugs 表の列と同じ）。
/// 一行の説明などの付帯情報は bugs 表を変えずに別表（bug_details）に置き、Title と Detail に入れて返す。
/// </summary>
public sealed record BugRecord(
    string Id,
    DateTimeOffset FoundAt,
    string FoundBy,
    string FoundStage,
    string? Category,
    int? Reach,
    string? Visibility,
    string? Severity,
    string? IntroducedIn,
    string? FixedIn,
    string? ConfirmRef,
    bool? Revivable,
    string? UsedIn,
    string? Title,
    string? Detail,
    string? Source);

/// <summary>
/// 新しく登録する不具合。ID は省略すると連番（BUG-0001 から）を振る。
/// 登録の時点で分かることだけを書く（混入したコミット、層、種類は後から自動で埋める）。
/// </summary>
public sealed record NewBug(
    string FoundBy,
    string FoundStage,
    string Title,
    string? Id = null,
    string? Severity = null,
    string? Category = null,
    string? Detail = null,
    string? Source = null,
    DateTimeOffset? FoundAt = null,
    int? Reach = null,
    string? Visibility = null);

/// <summary>自動で埋める項目の更新（フレームワーク専用）。null の項目は変えない。</summary>
public sealed record BugUpdate(
    string? IntroducedIn = null,
    string? FixedIn = null,
    int? Reach = null,
    string? Visibility = null,
    string? Category = null,
    bool? Revivable = null,
    string? ConfirmRef = null,
    string? Severity = null);

/// <summary>変更履歴の1件。</summary>
public sealed record BugChange(long Seq, string BugId, DateTimeOffset At, string Field, string? OldValue, string? NewValue, string Actor, string? Reason);

/// <summary>不具合と、判定結果・レポート・コミットなどの結び付き（第20.2節「検証ループ」）。</summary>
public sealed record BugLink(string BugId, string Kind, string Reference, DateTimeOffset At);

/// <summary>
/// 値の検査。found_by、found_stage、目立ち度、重大度、ID の形を決める（設計書 第20.7節、指示書 タスク11）。
/// </summary>
public static partial class BugValues
{
    /// <summary>found_stage の値。</summary>
    public static readonly IReadOnlyList<string> Stages = ["implementation", "loop", "staging", "production"];

    /// <summary>found_by の固定の値（loop:&lt;探索AIの名前と版&gt; のほかに使える値）。setup は環境を作る途中で見つけたもの。</summary>
    public static readonly IReadOnlyList<string> FixedFinders = ["dev_agent", "human", "production", "setup"];

    /// <summary>目立ち度の値。</summary>
    public static readonly IReadOnlyList<string> Visibilities = ["high", "medium", "low"];

    /// <summary>重大度の値（設計書 第26.2節）。</summary>
    public static readonly IReadOnlyList<string> Severities = ["critical", "high", "medium", "low"];

    [GeneratedRegex("^BUG-[0-9]{4,}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>found_by が正しいか。</summary>
    public static bool IsValidFinder(string? foundBy)
    {
        if (string.IsNullOrWhiteSpace(foundBy))
        {
            return false;
        }

        if (foundBy.StartsWith("loop:", StringComparison.Ordinal))
        {
            return foundBy.Length > "loop:".Length && !string.IsNullOrWhiteSpace(foundBy["loop:".Length..]);
        }

        return FixedFinders.Contains(foundBy, StringComparer.Ordinal);
    }

    /// <summary>ID の形（BUG-0001）が正しいか。</summary>
    public static bool IsValidId(string? id) => id != null && IdPattern().IsMatch(id);

    /// <summary>番号から ID を作る。</summary>
    public static string FormatId(int number) => "BUG-" + number.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>ID の番号部分。</summary>
    public static int NumberOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!IsValidId(id))
        {
            throw new CustosException($"バグの ID の形が違います（BUG-0001 の形）：{id}");
        }

        return int.Parse(id.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>新しく登録する内容を検査し、誤りの一覧を返す。</summary>
    public static IReadOnlyList<string> Validate(NewBug bug)
    {
        ArgumentNullException.ThrowIfNull(bug);
        var problems = new List<string>();
        if (!IsValidFinder(bug.FoundBy))
        {
            problems.Add($"found_by が正しくありません：{bug.FoundBy}（loop:<探索AIの名前と版>、{string.Join("、", FixedFinders)} のどれか）");
        }

        if (!Stages.Contains(bug.FoundStage, StringComparer.Ordinal))
        {
            problems.Add($"found_stage が正しくありません：{bug.FoundStage}（{string.Join("、", Stages)} のどれか）");
        }

        if (string.IsNullOrWhiteSpace(bug.Title))
        {
            problems.Add("一行の説明（title）がありません");
        }
        else if (bug.Title.Contains('\n', StringComparison.Ordinal))
        {
            problems.Add("一行の説明（title）に改行は入れられません");
        }

        if (bug.Id != null && !IsValidId(bug.Id))
        {
            problems.Add($"ID の形が違います（BUG-0001 の形）：{bug.Id}");
        }

        if (bug.Severity != null && !Severities.Contains(bug.Severity, StringComparer.Ordinal))
        {
            problems.Add($"severity が正しくありません：{bug.Severity}（{string.Join("、", Severities)} のどれか）");
        }

        if (bug.Visibility != null && !Visibilities.Contains(bug.Visibility, StringComparer.Ordinal))
        {
            problems.Add($"visibility が正しくありません：{bug.Visibility}（{string.Join("、", Visibilities)} のどれか）");
        }

        if (bug.Reach is < 0)
        {
            problems.Add("reach は 0 以上にしてください");
        }

        return problems;
    }
}
