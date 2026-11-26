using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Scenarios;
using Custos.Core.Util;

namespace Custos.Web.SocketIO.Agents;

/// <summary>状況の型（状態に対する条件として書く。設計書 第21.10節）。</summary>
public sealed record SituationType(string Name, string Description, string? ConditionSql);

/// <summary>対象の種類（カード、イベント、画面など）。</summary>
public sealed record TargetKind(string Name, IReadOnlyList<string> Targets, IReadOnlyList<string> Situations, IReadOnlyList<string> Viewpoints, string Severity, IReadOnlyDictionary<string, string> Labels);

/// <summary>台帳の定義（検証用リポジトリの ledger/definition.yaml）。</summary>
public sealed record LedgerDefinition(IReadOnlyDictionary<string, TargetKind> Kinds, IReadOnlyDictionary<string, SituationType> Situations, IReadOnlyList<(string Kind, string? Target, string? Situation, string? Viewpoint)> Exclusions, double FreeShare)
{
    /// <summary>読む。</summary>
    public static LedgerDefinition Load(Workspace workspace, ConfigSet config)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(config);
        var path = Path.Combine(workspace.VerifyRepo, "ledger", "definition.yaml");
        if (!File.Exists(path))
        {
            throw new CustosException("台帳の定義がありません：ledger/definition.yaml");
        }

        var errors = new ConfigErrors();
        var root = YamlDocument.Parse(File.ReadAllText(path), "ledger/definition.yaml", errors);
        if (root == null || errors.Any || Scenario.ToJson(root) is not JsonObject json)
        {
            throw new CustosException("ledger/definition.yaml を読めません：" + string.Join("; ", errors.Items));
        }

        var situations = new Dictionary<string, SituationType>(StringComparer.Ordinal);
        foreach (var pair in json["situations"] as JsonObject ?? [])
        {
            var s = pair.Value as JsonObject ?? [];
            situations[pair.Key] = new SituationType(pair.Key, s["description"]?.ToString() ?? pair.Key, s["condition_sql"]?.ToString());
        }

        var kinds = new Dictionary<string, TargetKind>(StringComparer.Ordinal);
        foreach (var pair in json["kinds"] as JsonObject ?? [])
        {
            var k = pair.Value as JsonObject ?? [];
            var targets = new List<string>();
            if (k["targets"] is JsonArray list)
            {
                targets.AddRange(list.Select(t => t?.ToString() ?? string.Empty).Where(t => t.Length > 0));
            }

            if (k["from_authority_inputs"]?.GetValue<bool>() == true)
            {
                targets.AddRange(config.Authority.Inputs.Values.Where(i => i.Kind == "event").Select(i => i.Target));
            }

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var label in k["labels"] as JsonObject ?? [])
            {
                labels[label.Key] = label.Value?.ToString() ?? string.Empty;
            }

            List<string> Strings(string name) => (k[name] as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty).Where(n => n.Length > 0).ToList();
            var kindSituations = Strings("situations");
            foreach (var situation in kindSituations.Where(s => !situations.ContainsKey(s)))
            {
                throw new CustosException($"台帳の定義：種類 {pair.Key} の状況の型 {situation} が situations にありません");
            }

            kinds[pair.Key] = new TargetKind(pair.Key, targets.Distinct(StringComparer.Ordinal).ToList(), kindSituations, Strings("viewpoints"), k["severity"]?.ToString() ?? "medium", labels);
        }

        var exclusions = new List<(string, string?, string?, string?)>();
        foreach (var item in (json["exclude"] as JsonArray ?? []).OfType<JsonObject>())
        {
            exclusions.Add((item["kind"]?.ToString() ?? string.Empty, item["target"]?.ToString(), item["situation"]?.ToString(), item["viewpoint"]?.ToString()));
        }

        var free = json["free_exploration_share"] is JsonValue fv && double.TryParse(fv.ToString().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? (fv.ToString().EndsWith('%') ? f / 100 : f) : config.Sampling?.FreeExplorationShare ?? 0.2;
        return new LedgerDefinition(kinds, situations, exclusions, free);
    }

    /// <summary>当てはまらない組み合わせか。</summary>
    public bool Excluded(string kind, string target, string situation, string viewpoint) =>
        Exclusions.Any(e => e.Kind == kind && (e.Target == null || e.Target == target) && (e.Situation == null || e.Situation == situation) && (e.Viewpoint == null || e.Viewpoint == viewpoint));
}

/// <summary>確認対象1件（対象 × 状況の型 × 観点）と、その状態（第21.10節）。</summary>
public sealed record LedgerEntry(
    string Id,
    string Kind,
    string Target,
    string Situation,
    string Viewpoint,
    string Severity,
    string Status,
    int ValidNoBugReports,
    IReadOnlyList<string> Reports,
    IReadOnlyList<string> Bugs,
    int Attempts = 0)
{
    /// <summary>確認済みに必要な「バグなし」のレポートの件数（高は別の盤面で2件、中と低は1件）。</summary>
    public int Required => Severity is "high" or "critical" ? 2 : 1;
}

/// <summary>
/// 確認対象の台帳（設計書 第21.10節）。抜き取りの母数 N と、探索の網羅の分母になる。
/// 台帳は判定用データなので、データの置き場所（data/ledger/）に置き、AIには見せない。
/// </summary>
public static class LedgerStore
{
    /// <summary>台帳のファイル。</summary>
    public static string PathOf(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Path.Combine(workspace.DataDir, "ledger", "ledger.json");
    }

    /// <summary>定義から台帳を作る。既存の台帳があれば、レポートと状態を引き継ぐ。</summary>
    public static IReadOnlyList<LedgerEntry> Build(LedgerDefinition definition, IReadOnlyList<LedgerEntry>? previous)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var old = (previous ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var entries = new List<LedgerEntry>();
        foreach (var kind in definition.Kinds.Values)
        {
            foreach (var target in kind.Targets)
            {
                foreach (var situation in kind.Situations)
                {
                    foreach (var viewpoint in kind.Viewpoints)
                    {
                        if (definition.Excluded(kind.Name, target, situation, viewpoint))
                        {
                            continue;
                        }

                        var id = $"{kind.Name}:{target}/{situation}/{viewpoint}";
                        entries.Add(old.TryGetValue(id, out var existing)
                            ? existing with { Severity = kind.Severity }
                            : new LedgerEntry(id, kind.Name, target, situation, viewpoint, kind.Severity, "unverified", 0, [], []));
                    }
                }
            }
        }

        return entries;
    }

    /// <summary>読む（なければ空）。</summary>
    public static IReadOnlyList<LedgerEntry> Load(Workspace workspace)
    {
        var path = PathOf(workspace);
        if (!File.Exists(path))
        {
            return [];
        }

        return System.Text.Json.JsonSerializer.Deserialize<List<LedgerEntry>>(File.ReadAllText(path), Json.Options) ?? [];
    }

    /// <summary>書く。</summary>
    public static void Save(Workspace workspace, IReadOnlyList<LedgerEntry> entries)
    {
        var path = PathOf(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? workspace.DataDir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Json.Serialize(entries));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>機械チェックを通ったレポートを反映する（第21.10節の状態の決め方）。</summary>
    public static IReadOnlyList<LedgerEntry> Apply(IReadOnlyList<LedgerEntry> entries, IEnumerable<(TrialReport Report, ReportCheck Check)> reports)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(reports);
        var map = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var (report, check) in reports)
        {
            if (!map.TryGetValue(report.TargetId, out var entry))
            {
                continue;
            }

            // 試した回数は、機械チェックに通らなかった試行も数える（割り当てで、到達できない対象ばかりを選ばないため）
            entry = entry with { Attempts = entry.Attempts + 1 };
            map[entry.Id] = entry;
            if (!check.Valid)
            {
                continue;
            }

            var reportIds = entry.Reports.Append(report.TrialId).Distinct(StringComparer.Ordinal).ToList();
            if (report.Result == "bug")
            {
                var bugs = entry.Bugs.Concat(report.BugIds).Distinct(StringComparer.Ordinal).ToList();
                map[entry.Id] = entry with { Status = "bug", Reports = reportIds, Bugs = bugs };
            }
            else
            {
                var noBug = entry.ValidNoBugReports + 1;
                var status = entry.Status == "bug" ? "bug" : noBug >= entry.Required ? "verified" : "unverified";
                map[entry.Id] = entry with { Status = status, ValidNoBugReports = noBug, Reports = reportIds };
            }
        }

        return entries.Select(e => map[e.Id]).ToList();
    }

    /// <summary>コードが変わったとき：AI判定の対象を未確認に戻す（修正に関係する対象。第21.10節）。</summary>
    public static IReadOnlyList<LedgerEntry> Invalidate(IReadOnlyList<LedgerEntry> entries, Func<LedgerEntry, bool> affected) =>
        entries.Select(e => affected(e) && e.Status != "bug" ? e with { Status = "unverified", ValidNoBugReports = 0 } : e).ToList();

    /// <summary>網羅：確認済みまたはバグありの割合。</summary>
    public static double Coverage(IReadOnlyList<LedgerEntry> entries) =>
        entries.Count == 0 ? 0 : (double)entries.Count(e => e.Status is "verified" or "bug") / entries.Count;
}
