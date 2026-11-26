using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Conformance;
using Custos.Core.Config;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Web.SocketIO.Docker;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Web.SocketIO.Conformance;

/// <summary>Web（Socket.IO）の適合テストの項目の一覧（設計書 第17.3節、指示書 第5章）。</summary>
public static class WebConformance
{
    /// <summary>段階0の項目（A、B、C、E）。段階1以降の項目（D、F、能力依存）も加える。</summary>
    public static IReadOnlyList<IConformanceItem> Items() =>
    [
        new A1Capabilities(),
        new A2ConfigFormat(),
        new A3StateReadable(),
        new A4References(),
        new A5DeclaredCapabilities(),
        new B1Reset(),
        new B2Settle(),
        new B3ClockAndSeed(),
        new B4Reproducible(),
        new B5HonestNoise(),
        new C1UiMatchesState(),
        new C2Screenshots(),
        new C3Logs(),
        new C4Records(),
        new D1JudgesDetect(),
        new D2NoFalsePositive(),
        new D3FaultInjection(),
        new D4Analyzers(),
        new E1UserSideIsolation(),
        new E2NoExternalEgress(),
        new E3SandboxSeesNoRepos(),
        new E4NoMarkers(),
        new E5TestApiAbsent(),
        new F1ProfileApplied(),
        new F2LoadHeadroom(),
        new F3CompareCanaries(),
        new F4Variance(),
    ];

    /// <summary>わざと壊す確認（指示書 第5.2節）：壊し方と、不合格になるべき項目。</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> BreakageExpectations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [ComposeWriter.Breaks.ConfigMissingRequired] = ["A-2"],
        [ComposeWriter.Breaks.StateNonexistentColumn] = ["A-3"],
        [ComposeWriter.Breaks.ResetSkipDbReplace] = ["B-1"],
        [ComposeWriter.Breaks.ReadWithoutSettle] = ["B-2"],
        [ComposeWriter.Breaks.BlankScreenshot] = ["C-2"],
        [ComposeWriter.Breaks.BypassProxy] = ["C-4", "E-1"],
        [ComposeWriter.Breaks.ExternalEgress] = ["E-2"],
        [ComposeWriter.Breaks.SandboxSeesVerifyRepo] = ["E-3"],
        [ComposeWriter.Breaks.ProxyAddsHeader] = ["E-4"],
        [ComposeWriter.Breaks.AutomationUserAgent] = ["E-4"],
    };
}

/// <summary>Web の項目の共通部分。</summary>
public abstract class WebItem : IConformanceItem
{
    /// <inheritdoc />
    public abstract ConformanceItemInfo Info { get; }

    /// <inheritdoc />
    public abstract Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken);

    /// <summary>Lab。</summary>
    protected static Lab.Lab LabOf(ConformanceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Get<Lab.Lab>();
    }

    /// <summary>合格。</summary>
    protected ItemResult Pass(string summary, IEnumerable<Evidence>? evidence = null, IReadOnlyDictionary<string, string>? data = null) =>
        ItemResult.Passed(Info.Id, summary, evidence, data);

    /// <summary>不合格。</summary>
    protected ItemResult Fail(string summary, IEnumerable<Evidence>? evidence = null, IReadOnlyDictionary<string, string>? data = null) =>
        ItemResult.Failed(Info.Id, summary, evidence, data);

    /// <summary>対象外。</summary>
    protected ItemResult Skip(string summary) => ItemResult.Skipped(Info.Id, summary);

    /// <summary>検証用リポジトリのシナリオを読む（なければ null）。</summary>
    protected static Scenario? LoadScenario(ConformanceContext context, string relative)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Path.Combine(context.Workspace.VerifyRepo, "scenarios", relative);
        return File.Exists(path) ? Scenario.Load(path) : null;
    }

    /// <summary>シナリオを実行し、結果を証拠として残す。</summary>
    protected static async Task<ScenarioResult> RunScenarioAsync(ConformanceContext context, Scenario scenario, string evidenceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scenario);
        var runner = new ScenarioRunner(LabOf(context), context.Run, _ => { });
        var result = await runner.RunAsync(scenario, cancellationToken).ConfigureAwait(false);
        context.Run.WriteJson($"evidence/{evidenceName}.json", result);
        return result;
    }

    /// <summary>証拠の JSON を書いて、その場所を返す。</summary>
    protected static string Write(ConformanceContext context, string name, JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Run.WriteText($"evidence/{name}.json", node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null");
    }

    /// <summary>証拠の場所（実行のディレクトリからの相対パス）。</summary>
    protected static string Rel(ConformanceContext context, string path)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Path.GetRelativePath(context.Run.Dir, path);
    }

    /// <summary>コンテナの中で部品を呼ぶ（client、attacker、browser、ops、edge）。</summary>
    protected static async Task<AgentSession> AgentAsync(ConformanceContext context, string role, CancellationToken cancellationToken)
    {
        var lab = LabOf(context);
        return role switch
        {
            "client" => await lab.ClientAsync(1, cancellationToken).ConfigureAwait(false),
            "attacker" => await lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false),
            "browser" => await lab.BrowserAsync(1, context.IsBroken(ComposeWriter.Breaks.AutomationUserAgent), cancellationToken).ConfigureAwait(false),
            "ops" => await lab.OpsAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new CustosException($"知らない部品です：{role}"),
        };
    }

    /// <summary>数を文字列にする。</summary>
    protected static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
