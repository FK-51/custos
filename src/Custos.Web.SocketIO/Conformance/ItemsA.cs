using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Conformance;
using Custos.Core.Config;
using Custos.Core.Util;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.State;

namespace Custos.Web.SocketIO.Conformance;

/// <summary>A-1：能力の宣言が読めて、プロトコルのバージョンが互換であること。</summary>
public sealed class A1Capabilities : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("A-1", ConformanceCategory.A, ItemKind.Required, "共通", "能力の宣言が読めて、プロトコルのバージョンが互換", "第17.3節 A、第14.8節");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.Config.App;
        if (app.TestApi == TestApiMode.None)
        {
            // テスト用APIがないので、能力は app.yaml の宣言から読む（第14.8節）。宣言の版の形を確かめる
            var bad = app.DeclaredCapabilities.Where(c => !IsVersion(c.Value)).Select(c => $"{c.Key}={c.Value}").ToList();
            var path = Write(context, "a1-capabilities", new JsonObject
            {
                ["test_api"] = "none",
                ["declared"] = new JsonObject(app.DeclaredCapabilities.Select(c => new KeyValuePair<string, JsonNode?>(c.Key, c.Value))),
                ["protocol_version"] = FrameworkInfo.ProtocolVersion,
            });
            return bad.Count == 0
                ? Pass($"app.yaml の test_api: none を読み、テスト用APIなしとして動く（宣言した能力 {app.DeclaredCapabilities.Count} 件）", [new Evidence("宣言", Rel(context, path))])
                : Fail("能力の版の形が誤っています：" + string.Join(", ", bad));
        }

        var lab = LabOf(context);
        if (lab.State.BuildKind != "test")
        {
            // 本番用ビルドにはテスト用APIを入れない（第14.9節。E-5 で確かめる）。能力の宣言は、テスト用ビルドで読む
            var declared = Write(context, "a1-capabilities", new JsonObject { ["test_api"] = "http", ["build"] = lab.State.BuildKind, ["note"] = "本番用ビルドのため、テスト用APIの能力はテスト用ビルド（custos up --build test）で読む" });
            return Pass("app.yaml の test_api: http を読みました。本番用ビルドなので、テスト用APIの能力の宣言はテスト用ビルドで確かめます", [new Evidence("宣言", Rel(context, declared))]);
        }

        var ops = await lab.OpsAsync(cancellationToken).ConfigureAwait(false);
        var response = await ops.CallObjectAsync("http.request", new JsonObject { ["url"] = $"http://app:{app.TestApiPort.ToString(CultureInfo.InvariantCulture)}/__verify/capabilities" }, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var evidence = Write(context, "a1-capabilities", response);
        if (response["status"]?.GetValue<int>() != 200)
        {
            return Fail($"テスト用APIの能力の宣言を読めません（{response["status"] ?? response["error"]}）", [new Evidence("応答", Rel(context, evidence))]);
        }

        var body = JsonNode.Parse(response["body"]?.GetValue<string>() ?? "{}");
        var version = body?["protocol_version"]?.GetValue<string>() ?? string.Empty;
        var compatible = version.Split('.')[0] == FrameworkInfo.ProtocolVersion.Split('.')[0];
        return compatible
            ? Pass($"テスト用APIの能力の宣言を読みました（プロトコル {version}）", [new Evidence("応答", Rel(context, evidence))])
            : Fail($"プロトコルの版が互換ではありません（アダプタ {version}、フレームワーク {FrameworkInfo.ProtocolVersion}）", [new Evidence("応答", Rel(context, evidence))]);
    }

    private static bool IsVersion(string text) => text.Split('.').Length is 2 or 3 && text.Split('.').All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _));
}

/// <summary>A-2：設定ファイルが書式に適合していること。</summary>
public sealed class A2ConfigFormat : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("A-2", ConformanceCategory.A, ItemKind.Required, "共通", "設定ファイルが書式（第18章）に適合", "第17.3節 A、第18.1節");

    /// <inheritdoc />
    public override Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dir = context.Config.Directory;
        if (context.IsBroken(ComposeWriter.Breaks.ConfigMissingRequired))
        {
            // わざと壊す確認：設定の複製から、必須の項目（app.yaml の name）を消す
            dir = Path.Combine(Path.GetTempPath(), "custos-break-" + Guid.NewGuid().ToString("N"));
            CopyConfig(context.Config.Directory, dir);
            var appPath = Path.Combine(dir, "app.yaml");
            File.WriteAllLines(appPath, File.ReadAllLines(appPath).Where(l => !l.StartsWith("name:", StringComparison.Ordinal)));
        }

        var result = ConfigLoader.Load(dir);
        var lines = result.FormatErrors.Select(e => e.ToString()).ToList();
        var path = context.Run.WriteText("evidence/a2-format.txt", lines.Count == 0 ? "誤りなし\n" : string.Join("\n", lines) + "\n");
        var files = Directory.EnumerateFiles(dir, "*.yaml").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        return Task.FromResult(lines.Count == 0
            ? Pass($"{files.Count} ファイルが書式に合っています（{string.Join(", ", files)}）", [new Evidence("検査の結果", Rel(context, path))])
            : Fail($"書式の誤りが {lines.Count} 件あります：{lines[0]}", [new Evidence("検査の結果", Rel(context, path))]));
    }

    internal static void CopyConfig(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*.yaml"))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }
}

/// <summary>A-3：状態スキーマの各キーが実際に読めて、型が一致すること（Web なら各 SQL が実行できること）。</summary>
public sealed class A3StateReadable : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("A-3", ConformanceCategory.A, ItemKind.Required, "共通", "状態スキーマの各キーが読めて、型が一致", "第17.3節 A、指示書 タスク7");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = LabOf(context);
        var config = context.Config;
        if (context.IsBroken(ComposeWriter.Breaks.StateNonexistentColumn))
        {
            // わざと壊す確認：存在しない列を読むキーを足す
            var keys = new Dictionary<string, StateKey>(config.State.Keys, StringComparer.Ordinal)
            {
                ["zz.broken_column"] = new StateKey("zz.broken_column", StateType.Integral, "player", new SqlSource("SELECT id, no_such_column FROM Player"), null),
            };
            config = config with { State = config.State with { Keys = keys } };
        }

        // すべてのキーに値が出るように、状態を作るシナリオを先に流す（検証用リポジトリの scenarios/conformance/state-coverage.yaml）
        var evidence = new List<Evidence>();
        if (LoadScenario(context, "conformance/state-coverage.yaml") is { } scenario)
        {
            var run = await RunScenarioAsync(context, scenario, "a3-state-coverage", cancellationToken).ConfigureAwait(false);
            evidence.Add(new Evidence("状態を作るシナリオ", "evidence/a3-state-coverage.json"));
            if (!run.Ok)
            {
                return Fail($"状態を作るシナリオが失敗しました：{run.Error}", evidence);
            }
        }

        var reader = new StateReader(lab);
        var needsBrowser = config.State.Keys.Values.Any(k => k.Source is DomSource);
        var browser = needsBrowser ? await AgentAsync(context, "browser", cancellationToken).ConfigureAwait(false) : null;
        var results = await reader.ReadAsync(config.State.Keys.Values.ToList(), browser, "main", cancellationToken).ConfigureAwait(false);
        var report = new JsonArray(results.Select(r => (JsonNode?)StateReader.ToJson(r)).ToArray());
        evidence.Add(new Evidence("読み取りの結果", Rel(context, Write(context, "a3-state", report))));
        var problems = new List<string>();
        foreach (var result in results)
        {
            if (!result.Ok)
            {
                problems.Add($"{result.Key}：読めません（{result.Error}）");
            }
            else if (result.Values.Count == 0)
            {
                problems.Add($"{result.Key}：値が1つもありません");
            }
            else if (result.TypeErrors.Count > 0)
            {
                problems.Add($"{result.Key}：{result.TypeErrors[0].TypeError}");
            }
        }

        return problems.Count == 0
            ? Pass($"{results.Count} キーすべてを読め、型が一致しました（sql {results.Count(r => r.Source == "sql")}、socket {results.Count(r => r.Source == "socket")}、dom {results.Count(r => r.Source == "dom")}）", evidence)
            : Fail($"{problems.Count} キーで問題：{string.Join("／", problems.Take(5))}", evidence);
    }
}

/// <summary>A-4：権威マップや不変条件で参照しているキーが、状態スキーマに実在すること。</summary>
public sealed class A4References : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("A-4", ConformanceCategory.A, ItemKind.Required, "共通", "参照しているキーが状態スキーマに実在", "第17.3節 A、第14.8節");

    /// <inheritdoc />
    public override Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var warnings = new List<string>();
        var errors = ConfigLoader.CheckReferences(context.Config, warnings);
        var path = context.Run.WriteText("evidence/a4-references.txt", string.Join("\n", errors.Select(e => "誤り：" + e).Concat(warnings.Select(w => "警告：" + w))) + "\n");
        var referenced = context.Config.Authority.State.Count + context.Config.Authority.Ownership.Count + (context.Config.Invariants?.Invariants.Count(i => i.DeltaKey != null) ?? 0);
        return Task.FromResult(errors.Count == 0
            ? Pass($"参照 {referenced} 件がすべて state.yaml に実在します（警告 {warnings.Count} 件）", [new Evidence("照合の結果", Rel(context, path))])
            : Fail($"実在しないキーの参照が {errors.Count} 件：{errors[0]}", [new Evidence("照合の結果", Rel(context, path))]));
    }
}

/// <summary>A-5（能力依存）：宣言した能力ごとに、実際に動くかの確認。</summary>
public sealed class A5DeclaredCapabilities : WebItem
{
    /// <inheritdoc />
    public override ConformanceItemInfo Info { get; } = new("A-5", ConformanceCategory.A, ItemKind.Capability, "共通", "宣言した能力ごとに、実際に動く", "第17.3節 A", "test_api");

    /// <inheritdoc />
    public override async Task<ItemResult> RunAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.Config.App;
        if (app.TestApi == TestApiMode.None)
        {
            return Skip("テスト用APIを宣言していない（test_api: none）");
        }

        var ops = await LabOf(context).OpsAsync(cancellationToken).ConfigureAwait(false);
        var results = new JsonObject();
        var failed = new List<string>();
        foreach (var (method, path, body) in new[] { ("POST", "/__verify/settle", "{\"timeout_ms\":5000}"), ("POST", "/__verify/reset", "{}"), ("GET", "/__verify/faults", (string?)null) })
        {
            var response = await ops.CallObjectAsync("http.request", new JsonObject
            {
                ["url"] = $"http://app:{app.TestApiPort.ToString(CultureInfo.InvariantCulture)}{path}",
                ["method"] = method,
                ["body"] = body,
            }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            results[path] = response;
            if (response["status"]?.GetValue<int>() != 200)
            {
                failed.Add($"{method} {path} → {response["status"] ?? response["error"]}");
            }
        }

        var evidence = Write(context, "a5-capabilities", results);
        return failed.Count == 0
            ? Pass("テスト用APIの settle、reset、faults が応答しました", [new Evidence("応答", Rel(context, evidence))])
            : Fail("動かない能力があります：" + string.Join("、", failed), [new Evidence("応答", Rel(context, evidence))]);
    }
}
