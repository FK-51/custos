using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Util;

namespace Custos.Core.Judging;

/// <summary>
/// 判定器が見つけたもの（違反、または候補）。
/// Stage は判定の段階（第2.2節）：1 は機械判定で確定、2 は候補（AIか人が確定する）、3 は AI 判定。
/// Fingerprint が同じものは同じ不具合としてまとめる（第21.10節「バグのまとめ方」）。
/// </summary>
public sealed record Finding(
    string Judge,
    string Rule,
    Severity Severity,
    int Stage,
    string Category,
    string Summary,
    string? Detail,
    IReadOnlyList<Evidence> Evidence,
    string Fingerprint,
    DateTimeOffset At)
{
    /// <summary>作る。指紋は判定器・規則・場所から作る。</summary>
    public static Finding Create(string judge, string rule, Severity severity, int stage, string category, string summary, string? detail, IEnumerable<Evidence>? evidence, string location) =>
        new(judge, rule, severity, stage, category, summary, detail, evidence?.ToList() ?? [], Hashing.Sha256Hex($"{judge}\n{rule}\n{location}")[..16], DateTimeOffset.UtcNow);
}

/// <summary>判定の結果。</summary>
public sealed record JudgeReport(string Judge, bool Ran, string? SkippedReason, IReadOnlyList<Finding> Findings, double Ms);

/// <summary>判定の結果をまとめる。</summary>
public static class Findings
{
    /// <summary>同じ指紋のものを1つにまとめる（最初のものを残し、件数を数える）。</summary>
    public static IReadOnlyList<(Finding Finding, int Count)> Group(IEnumerable<Finding> findings) =>
        findings.GroupBy(f => f.Fingerprint, StringComparer.Ordinal).Select(g => (g.First(), g.Count())).ToList();

    /// <summary>重大度ごとの件数。</summary>
    public static IReadOnlyDictionary<Severity, int> CountBySeverity(IEnumerable<Finding> findings) =>
        findings.GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.Select(f => f.Fingerprint).Distinct(StringComparer.Ordinal).Count());
}
