using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;

namespace Custos.Core.Maturity;

/// <summary>
/// 校正の根拠（設計書 第17.5節のレベル3の区分、第19.10節）。
/// CanaryDetectionRate は毎ループのカナリアの累積の検出率（第19.7節。data/calibration/canaries.jsonl）。
/// MeasuredAt と Basis は校正（custos calibrate）の日時と根拠で、校正をしていなければ null と "none"。
/// SeedDetectionRate は校正で仕込んだバグの検出率（自明な層を除く。参考の数値で、レベルの判定には使わない）。
/// </summary>
public sealed record CalibrationEvidence(
    double? CanaryDetectionRate,
    double CanaryThreshold,
    DateTimeOffset? MeasuredAt,
    string Basis,
    double? VerifierSensitivity,
    double? EscapeRatio,
    double? ExplorationCoverage,
    double? NoisyKeyRatio,
    double? SeedDetectionRate = null);

/// <summary>成熟度のレベルと個別の数値（第17.5〜17.6節）。レベルは適合テストの結果から自動で決め、自己申告は認めない。</summary>
public sealed record MaturityReport(
    string Level,
    int LevelNumber,
    string Reason,
    bool PerformanceSupported,
    bool MeetsRequired,
    string Required,
    JsonObject Metrics);

/// <summary>成熟度を計算する。</summary>
public static class MaturityCalculator
{
    /// <summary>計算する。calibration は段階4の校正の結果（なければ null）。</summary>
    public static MaturityReport Compute(ConfigSet config, ConformanceReport conformance, CalibrationEvidence? calibration)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(conformance);
        bool Passed(string id) => conformance.Result(id)?.Verdict == Verdict.Pass;
        var required = conformance.Items.Where(i => i.Kind == ItemKind.Required).ToList();
        var failedRequired = required.Where(i => !Passed(i.Id)).Select(i => i.Id).ToList();

        string level;
        string reason;
        if (required.Count == 0)
        {
            level = "0";
            reason = "必須項目を実行していない";
        }
        else if (failedRequired.Count > 0)
        {
            level = "0";
            reason = "必須項目が失敗：" + string.Join(", ", failedRequired);
        }
        else if (!(Passed("B-3") && Passed("B-4")))
        {
            level = "1";
            reason = "必須項目はすべて合格。時刻・シードの固定（B-3）と再実行の一致（B-4）が未合格のためレベル2に届かない";
        }
        else if (!Passed("D-3") || calibration?.CanaryDetectionRate is not { } rate || rate < calibration.CanaryThreshold || calibration.MeasuredAt == null)
        {
            level = "2";
            var missing = new List<string>();
            if (!Passed("D-3"))
            {
                missing.Add("バグ注入（D-3）が未合格");
            }

            missing.Add(calibration?.CanaryDetectionRate is { } r
                ? (r < calibration.CanaryThreshold ? $"カナリアの累積の検出率 {r:P0} が基準 {calibration.CanaryThreshold:P0} 未満" : string.Empty)
                : "カナリアの記録がない");
            if (calibration?.MeasuredAt == null)
            {
                missing.Add("校正（custos calibrate）をしていない（レベル3の区分 a〜c が決まらない）");
            }

            reason = "再現可能（B-3、B-4 合格）。レベル3に届かない：" + string.Join("、", missing.Where(m => m.Length > 0));
        }
        else
        {
            level = "3" + calibration.Basis switch
            {
                "own_real_bugs" => "c",
                "other_projects" => "b",
                _ => "a",
            };
            reason = $"校正済み（カナリアの検出率 {rate:P0}、校正の根拠 {calibration.Basis}）";
        }

        if (!MaturityLevel.TryParse(level, out var achieved))
        {
            achieved = MaturityLevel.Zero;
        }

        var meets = achieved.Satisfies(config.App.RequiredMaturity);
        var performance = conformance.Items.Where(i => i.Category == ConformanceCategory.F).Any(i => Passed(i.Id));
        return new MaturityReport(level, achieved.Level, reason, performance, meets, config.App.RequiredMaturity.ToString(), Metrics(config, conformance, calibration));
    }

    /// <summary>個別の数値（第17.6節）。</summary>
    public static JsonObject Metrics(ConfigSet config, ConformanceReport conformance, CalibrationEvidence? calibration)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(conformance);
        var capabilityItems = conformance.Items.Where(i => i.Kind == ItemKind.Capability && conformance.Result(i.Id)?.Verdict is Verdict.Pass or Verdict.Fail or Verdict.Error).ToList();
        var capabilityPassed = capabilityItems.Count(i => conformance.Result(i.Id)?.Verdict == Verdict.Pass);
        var keys = config.State.Keys.Values.ToList();
        var invariants = config.Invariants?.Invariants ?? [];
        var covered = keys.Count(k => invariants.Any(i => Mentions(i, k)));
        var declared = keys.Count(k => config.Authority.State.ContainsKey(k.Name));
        var stageCounts = invariants.GroupBy(i => i.Stage).ToDictionary(g => g.Key, g => g.Count());
        var inputs = config.Authority.Inputs.Count;
        var ownership = config.Authority.Ownership.Count;
        return new JsonObject
        {
            ["capability_fulfillment"] = capabilityItems.Count == 0 ? null : (double)capabilityPassed / capabilityItems.Count,
            ["invariant_coverage"] = keys.Count == 0 ? null : (double)covered / keys.Count,
            ["authority_coverage"] = keys.Count == 0 ? null : (double)declared / keys.Count,
            ["exploration_coverage"] = calibration?.ExplorationCoverage,
            ["authorization_coverage"] = inputs == 0 ? null : Math.Min(1.0, (double)config.Authority.Phases.Sum(p => p.Value.Allowed.Count) / Math.Max(1, inputs)),
            ["judgment_stage_ratio"] = new JsonObject
            {
                ["stage1"] = invariants.Count == 0 ? null : (double)stageCounts.GetValueOrDefault(1) / invariants.Count,
                ["stage2"] = invariants.Count == 0 ? null : (double)stageCounts.GetValueOrDefault(2) / invariants.Count,
                ["stage3"] = invariants.Count == 0 ? null : (double)stageCounts.GetValueOrDefault(3) / invariants.Count,
            },
            ["canary_detection_rate"] = calibration?.CanaryDetectionRate,
            ["calibrated_at"] = calibration?.MeasuredAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["calibration_basis"] = calibration?.Basis,
            ["seed_detection_rate"] = calibration?.SeedDetectionRate,
            ["excluded_keys"] = config.State.Determinism.Volatile.Count,
            ["noisy_key_ratio"] = calibration?.NoisyKeyRatio,
            ["verifier_sensitivity"] = calibration?.VerifierSensitivity,
            ["escape_ratio"] = calibration?.EscapeRatio,
            ["substitute_services"] = 0,
            ["invariants"] = invariants.Count,
            ["ownership_declarations"] = ownership,
        };
    }

    /// <summary>不変条件がそのキーを見ているか（SQL の中の表名・列名、または params.keys）。</summary>
    private static bool Mentions(InvariantSpec invariant, StateKey key)
    {
        if (invariant.Parameters.TryGetValue("keys", out var list) && list.Split(',').Select(s => s.Trim()).Contains(key.Name, StringComparer.Ordinal))
        {
            return true;
        }

        if (invariant.DeltaKey == key.Name)
        {
            return true;
        }

        var query = invariant.Query ?? string.Empty;
        return key.Source switch
        {
            SqlSource sql => TableOf(sql.Sql) is { } table && query.Contains(table, StringComparison.OrdinalIgnoreCase) && query.Contains(ColumnOf(sql.Sql) ?? "\u0000", StringComparison.OrdinalIgnoreCase),
            SocketSource socket => socket.Events.Any(e => query.Contains(e, StringComparison.Ordinal)) && query.Contains(socket.Path.TrimStart('$'), StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string? TableOf(string sql)
    {
        var tokens = sql.Split([' ', '\n', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (tokens[i].Equals("FROM", StringComparison.OrdinalIgnoreCase))
            {
                return tokens[i + 1].Trim('"');
            }
        }

        return null;
    }

    private static string? ColumnOf(string sql)
    {
        var select = sql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        var from = sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
        if (select < 0 || from < 0 || from <= select)
        {
            return null;
        }

        var columns = sql[(select + 6)..from].Split(',');
        if (columns.Length < 2)
        {
            return null;
        }

        var last = columns[^1].Trim();
        var dot = last.LastIndexOf('.');
        return (dot >= 0 ? last[(dot + 1)..] : last).Trim('"', ' ', ')');
    }
}
