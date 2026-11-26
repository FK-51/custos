using System.Diagnostics;
using System.Globalization;
using System.Text;
using Custos.Core.Config;
using Custos.Core.Results;
using Custos.Core.Util;

namespace Custos.Core.Conformance;

/// <summary>適合テストの区分（設計書 第17.3節）。</summary>
public enum ConformanceCategory
{
    /// <summary>接続と宣言。</summary>
    A,

    /// <summary>リセットと再現性。</summary>
    B,

    /// <summary>観測の正しさ。</summary>
    C,

    /// <summary>判定器が反応すること。</summary>
    D,

    /// <summary>隔離。</summary>
    E,

    /// <summary>計測。</summary>
    F,
}

/// <summary>必須か、能力依存か（設計書 第17.2節）。</summary>
public enum ItemKind
{
    /// <summary>一つでも失敗したら、その環境ではリリース判定を出さない。</summary>
    Required,

    /// <summary>宣言した能力についてだけ検査する。失敗したらその能力を使えない扱いにする。</summary>
    Capability,
}

/// <summary>判定。</summary>
public enum Verdict
{
    /// <summary>合格。</summary>
    Pass,

    /// <summary>不合格。</summary>
    Fail,

    /// <summary>対象外（能力を宣言していない、または段階の外）。</summary>
    Skip,

    /// <summary>検査そのものを実行できなかった。必須項目では不合格と同じに扱う。</summary>
    Error,
}

/// <summary>項目の情報。</summary>
public sealed record ConformanceItemInfo(
    string Id,
    ConformanceCategory Category,
    ItemKind Kind,
    string Target,
    string Title,
    string DesignRef,
    string? Capability = null);

/// <summary>根拠（記録や画像の場所）。</summary>
public sealed record Evidence(string Description, string? Path = null);

/// <summary>項目の結果。</summary>
public sealed record ItemResult(
    string Id,
    Verdict Verdict,
    string Summary,
    IReadOnlyList<Evidence> Evidence,
    IReadOnlyDictionary<string, string> Data,
    double DurationSeconds)
{
    /// <summary>合格の結果を作る。</summary>
    public static ItemResult Passed(string id, string summary, IEnumerable<Evidence>? evidence = null, IReadOnlyDictionary<string, string>? data = null) =>
        new(id, Verdict.Pass, summary, evidence?.ToList() ?? [], data ?? new Dictionary<string, string>(), 0);

    /// <summary>不合格の結果を作る。</summary>
    public static ItemResult Failed(string id, string summary, IEnumerable<Evidence>? evidence = null, IReadOnlyDictionary<string, string>? data = null) =>
        new(id, Verdict.Fail, summary, evidence?.ToList() ?? [], data ?? new Dictionary<string, string>(), 0);

    /// <summary>対象外の結果を作る。</summary>
    public static ItemResult Skipped(string id, string summary) =>
        new(id, Verdict.Skip, summary, [], new Dictionary<string, string>(), 0);
}

/// <summary>
/// 適合テストを走らせるときの文脈。アダプタは自分の部品を Services に入れて項目に渡す。
/// Breakage は「わざと壊す確認」（指示書 第5.2節）で使う、壊し方の名前。
/// </summary>
public sealed class ConformanceContext
{
    private readonly Dictionary<Type, object> _services = [];

    /// <summary>文脈を作る。</summary>
    public ConformanceContext(Run run, ConfigSet config, Workspace workspace, IReadOnlySet<string> breakage)
    {
        Run = run;
        Config = config;
        Workspace = workspace;
        Breakage = breakage;
    }

    /// <summary>この適合テストの実行（証拠の置き場所）。</summary>
    public Run Run { get; }

    /// <summary>設定。</summary>
    public ConfigSet Config { get; }

    /// <summary>場所。</summary>
    public Workspace Workspace { get; }

    /// <summary>わざと壊す確認で有効にしている壊し方。</summary>
    public IReadOnlySet<string> Breakage { get; }

    /// <summary>壊し方が有効か。</summary>
    public bool IsBroken(string name) => Breakage.Contains(name);

    /// <summary>部品を入れる。</summary>
    public void Add<T>(T service)
        where T : class => _services[typeof(T)] = service;

    /// <summary>部品を取り出す。</summary>
    public T Get<T>()
        where T : class
    {
        if (_services.TryGetValue(typeof(T), out var service) && service is T typed)
        {
            return typed;
        }

        throw new CustosException($"適合テストの文脈に {typeof(T).Name} がありません");
    }

    /// <summary>前の項目の結果（後の項目が前提にできる）。</summary>
    public Dictionary<string, ItemResult> Results { get; } = new(StringComparer.Ordinal);
}

/// <summary>適合テストの1項目。</summary>
public interface IConformanceItem
{
    /// <summary>項目の情報。</summary>
    ConformanceItemInfo Info { get; }

    /// <summary>実行する。</summary>
    Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken);
}

/// <summary>適合テスト全体の結果。</summary>
public sealed record ConformanceReport(
    string RunId,
    DateTimeOffset At,
    string FrameworkVersion,
    string JudgeHash,
    string ConfigHash,
    IReadOnlyList<string> Breakage,
    IReadOnlyList<ConformanceItemInfo> Items,
    IReadOnlyList<ItemResult> Results)
{
    /// <summary>必須項目がすべて合格したか。</summary>
    public bool RequiredAllPassed => Items
        .Where(i => i.Kind == ItemKind.Required)
        .All(i => Results.FirstOrDefault(r => r.Id == i.Id)?.Verdict == Verdict.Pass);

    /// <summary>使える能力（能力依存の項目に合格した能力）。</summary>
    public IReadOnlyList<string> UsableCapabilities => Items
        .Where(i => i.Kind == ItemKind.Capability && i.Capability != null)
        .Where(i => Results.FirstOrDefault(r => r.Id == i.Id)?.Verdict == Verdict.Pass)
        .Select(i => i.Capability ?? string.Empty)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>項目の結果を引く。</summary>
    public ItemResult? Result(string id) => Results.FirstOrDefault(r => r.Id == id);

    /// <summary>保存した結果（conformance.json）を読む。</summary>
    public static ConformanceReport Load(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<ConformanceReport>(File.ReadAllText(path), Util.Json.Options)
        ?? throw new Util.CustosException($"適合テストの結果を読めません：{path}");
}

/// <summary>適合テストを走らせ、JSON と人が読む要約を書く（指示書 タスク10）。</summary>
public static class ConformanceRunner
{
    /// <summary>項目を順に実行する。selected が空でなければ、その ID だけを実行する。</summary>
    public static async Task<ConformanceReport> RunAsync(
        ConformanceContext context,
        IReadOnlyList<IConformanceItem> items,
        IReadOnlySet<string>? selected,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(items);
        var results = new List<ItemResult>();
        var infos = new List<ConformanceItemInfo>();
        foreach (var item in items)
        {
            if (selected is { Count: > 0 } && !selected.Contains(item.Info.Id))
            {
                continue;
            }

            infos.Add(item.Info);
            log?.Invoke($"[{item.Info.Id}] {item.Info.Title} …");
            var stopwatch = Stopwatch.StartNew();
            ItemResult result;
            try
            {
                result = await item.RunAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is CustosException or IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException or UnauthorizedAccessException or OperationCanceledException or HttpRequestException)
            {
                result = new ItemResult(item.Info.Id, Verdict.Error, $"検査を実行できませんでした：{e.Message}", [], new Dictionary<string, string>(), 0);
                context.Run.AppendJsonLine("conformance-errors.jsonl", new { id = item.Info.Id, error = e.ToString() });
            }

            stopwatch.Stop();
            result = result with { DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3) };
            context.Results[item.Info.Id] = result;
            results.Add(result);
            log?.Invoke($"[{item.Info.Id}] {VerdictLabel(result.Verdict)}：{result.Summary}");
        }

        var report = new ConformanceReport(
            context.Run.Id,
            DateTimeOffset.UtcNow,
            FrameworkInfo.Version,
            FrameworkInfo.JudgeHash,
            context.Config.Hash,
            context.Breakage.OrderBy(b => b, StringComparer.Ordinal).ToList(),
            infos,
            results);
        context.Run.WriteJson("conformance.json", report);
        context.Run.WriteText("conformance.md", Summarize(report));
        return report;
    }

    /// <summary>判定の表示名。</summary>
    public static string VerdictLabel(Verdict verdict) => verdict switch
    {
        Verdict.Pass => "合格",
        Verdict.Fail => "不合格",
        Verdict.Skip => "対象外",
        _ => "実行できず",
    };

    /// <summary>人が読む要約（Markdown）。</summary>
    public static string Summarize(ConformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# 適合テストの結果（{report.RunId}）");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 日時：{report.At:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- フレームワークの版：{report.FrameworkVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 判定器のハッシュ：`{report.JudgeHash}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 設定のハッシュ：`{report.ConfigHash}`");
        if (report.Breakage.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- わざと壊した状態：{string.Join(", ", report.Breakage)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"- 必須項目：{(report.RequiredAllPassed ? "すべて合格" : "不合格あり（この環境ではリリース判定を出さない）")}");
        sb.AppendLine();
        sb.AppendLine("| 項目 | 区分 | 内容 | 判定 | 要約 | 根拠 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var info in report.Items)
        {
            var result = report.Result(info.Id);
            var evidence = result == null ? string.Empty : string.Join("<br>", result.Evidence.Select(e => e.Path == null ? e.Description : $"{e.Description}（`{e.Path}`）"));
            var kind = info.Kind == ItemKind.Required ? "必須" : $"能力依存（{info.Capability}）";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {info.Id} | {kind} | {Escape(info.Title)} | {(result == null ? "-" : VerdictLabel(result.Verdict))} | {Escape(result?.Summary ?? string.Empty)} | {Escape(evidence)} |");
        }

        return sb.ToString();
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
}
