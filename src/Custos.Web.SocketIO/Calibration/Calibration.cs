using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Conformance;
using Custos.Core.Judging;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Statistics;
using Custos.Web.SocketIO.Judges;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.Scenarios;
using Custos.Web.SocketIO.State;

namespace Custos.Web.SocketIO.Calibration;

/// <summary>
/// 校正用バグ1件（設計書 第19章）。検証用リポジトリの seeds/&lt;ID&gt;/ に、目印のないパッチ（patch.diff）、
/// 確認用シナリオ（confirm.yaml。探索役には見せない）、対応表（meta.yaml：種類、説明、クラッシュ系か）を置く。
/// </summary>
public sealed record SeedDefinition(string Id, string Kind, string Description, string PatchPath, string ConfirmPath, bool Crash, string? Origin);

/// <summary>品質チェックの結果（第19.5節）と層（第19.11節）。</summary>
public sealed record SeedCheckResult(string Id, bool Applies, bool Effective, bool Trivial, string Reach, string Visibility, string Detail)
{
    /// <summary>層（到達の難しさ × 目立ち度）。</summary>
    public string Layer => $"{Reach}{Visibility}";

    /// <summary>使えるか（効いていて、自明すぎない）。</summary>
    public bool Usable => Applies && Effective && !Trivial;
}

/// <summary>校正用バグの置き場所と使用済みの記録（第19.8節）。</summary>
public static class SeedStore
{
    /// <summary>読む。</summary>
    public static IReadOnlyList<SeedDefinition> Load(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var root = Path.Combine(workspace.VerifyRepo, "seeds");
        var list = new List<SeedDefinition>();
        if (!Directory.Exists(root))
        {
            return list;
        }

        foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            var meta = Path.Combine(dir, "meta.yaml");
            if (!File.Exists(meta))
            {
                continue;
            }

            var errors = new ConfigErrors();
            var node = YamlDocument.Parse(File.ReadAllText(meta), Path.GetFileName(dir) + "/meta.yaml", errors);
            var json = node == null ? null : Scenario.ToJson(node) as JsonObject;
            if (json == null || errors.Any)
            {
                throw new CustosException($"{dir}/meta.yaml を読めません：{string.Join("; ", errors.Items)}");
            }

            list.Add(new SeedDefinition(
                Path.GetFileName(dir),
                json["kind"]?.ToString() ?? "unknown",
                json["description"]?.ToString() ?? string.Empty,
                Path.Combine(dir, "patch.diff"),
                Path.Combine(dir, json["confirm"]?.ToString() ?? "confirm.yaml"),
                json["crash"]?.GetValue<bool>() == true,
                json["origin"]?.ToString()));
        }

        return list;
    }

    private static string UsedPath(Workspace workspace) => Path.Combine(workspace.DataDir, "calibration", "used.json");

    /// <summary>使用済みの ID。</summary>
    public static HashSet<string> Used(Workspace workspace)
    {
        var path = UsedPath(workspace);
        return File.Exists(path) ? (JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? []).Select(n => n?.ToString() ?? string.Empty).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>使用済みにする（再利用しない）。</summary>
    public static void MarkUsed(Workspace workspace, IEnumerable<string> ids)
    {
        var used = Used(workspace);
        used.UnionWith(ids);
        var path = UsedPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? workspace.DataDir);
        File.WriteAllText(path, new JsonArray(used.Order(StringComparer.Ordinal).Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()).ToJsonString());
    }

    /// <summary>パッチを当てた複製を作り、コミットする（イメージのタグが変わるように）。返り値は複製のディレクトリ。</summary>
    public static async Task<string> PrepareCloneAsync(Workspace workspace, IReadOnlyList<SeedDefinition> seeds, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(seeds);
        var dir = Path.Combine(workspace.DataDir, "calibration", "builds", name);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dir) ?? workspace.DataDir);
        (await ProcessRunner.RunAsync("git", ["clone", "--quiet", "--no-hardlinks", workspace.AppRepo, dir], timeout: TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureSuccess("校正用の複製");
        await ProcessRunner.RunAsync("git", ["-C", dir, "remote", "remove", "origin"], timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var seed in seeds)
        {
            (await ProcessRunner.RunAsync("git", ["-C", dir, "apply", "--index", seed.PatchPath], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureSuccess($"校正用バグ {seed.Id} のパッチ");
        }

        // コミットのメッセージにもバグの ID や目印を書かない（第19.4節）
        (await ProcessRunner.RunAsync("git", ["-C", dir, "-c", "user.name=custos", "-c", "user.email=custos@localhost", "commit", "--quiet", "-m", "update"], timeout: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false)).EnsureSuccess("校正用の複製のコミット");
        return dir;
    }
}

/// <summary>
/// 校正用バグの品質チェック（第19.5節）と層の自動割り当て（第19.11節）。
/// 1. 効いているか：確認用シナリオを、パッチありとなしのビルドで同じシードで実行し、結果が変わる。
///    パッチなしは2回実行し、状態が一致しなければ（確認用シナリオが決定的でない）、状態の変化は効き目の根拠にしない
/// 2. 自明すぎないか：起動直後（何もしない）に汎用の判定器が反応しない
/// 3. 層：目立ち度は、機械判定が反応する（V1）／手のログや送信された状態が変わる（V2）／見た目だけ（V3）。
///    到達の難しさは、確認用シナリオの手順数で代用する（R1：3以内、R2：4〜10、R3：11以上）
/// テスト用ビルド（シードの固定）で実行する。
/// </summary>
public sealed class SeedChecker
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public SeedChecker(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>確かめる。</summary>
    public async Task<IReadOnlyList<SeedCheckResult>> CheckAsync(IReadOnlyList<SeedDefinition> seeds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        var results = new List<SeedCheckResult>();

        // パッチなし（基準）の結果を、確認用シナリオごとに2回取る（再現するかを確かめる）
        await _lab.UpAsync(new UpOptions("test", new HashSet<string>(StringComparer.Ordinal), null, false, false), cancellationToken).ConfigureAwait(false);
        var baseline = new Dictionary<string, (Outcome First, Outcome Second)>(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            baseline[seed.Id] = (await RunConfirmAsync(seed, "base-1", cancellationToken).ConfigureAwait(false), await RunConfirmAsync(seed, "base-2", cancellationToken).ConfigureAwait(false));
        }

        foreach (var seed in seeds)
        {
            _log($"== 校正用バグ {seed.Id}（{seed.Kind}）");
            string clone;
            try
            {
                clone = await SeedStore.PrepareCloneAsync(_lab.Workspace, [seed], "check-" + seed.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (CustosException e)
            {
                results.Add(new SeedCheckResult(seed.Id, false, false, false, "-", "-", "パッチが当たらない：" + e.Message));
                continue;
            }

            await _lab.UpAsync(new UpOptions("test", new HashSet<string>(StringComparer.Ordinal), null, false, false, clone), cancellationToken).ConfigureAwait(false);

            // 自明すぎないか：起動直後に何もしないで判定する
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            var idleBaseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            var idle = await JudgeRunner.RunAsync(new JudgeContext(_lab, _run, idleBaseline), JudgeRunner.Stage1(), cancellationToken).ConfigureAwait(false);
            var trivial = idle.SelectMany(r => r.Findings).Any(f => f.Stage == 1 && f.Category != "coverage");

            var patched = await RunConfirmAsync(seed, "patched", cancellationToken).ConfigureAwait(false);
            var (basis, again) = baseline[seed.Id];
            var newFindings = patched.Findings.Except(basis.Findings.Concat(again.Findings), StringComparer.Ordinal).ToList();

            // 状態と画面の比較は、パッチなしの2回が一致するとき（確認用シナリオが決定的なとき）だけ根拠にする。
            // 一致しなければ、パッチと関係なく毎回変わるので、「変化あり」が効き目の証拠にならない
            var stateReproducible = basis.StateHash == again.StateHash && basis.ScenarioOk == again.ScenarioOk;
            var sentReproducible = basis.SentHash == again.SentHash;
            var screenReproducible = basis.ScreenHash == again.ScreenHash;
            var stateChanged = stateReproducible && (patched.StateHash != basis.StateHash || patched.ScenarioOk != basis.ScenarioOk);
            var sentChanged = sentReproducible && patched.SentHash != basis.SentHash;
            var screenChanged = screenReproducible && patched.ScreenHash != basis.ScreenHash;

            // V2：状態（スキーマのキー）か、送信された状態が変わる（第19.11節）
            var visibility = newFindings.Count > 0 ? "V1" : stateChanged || sentChanged ? "V2" : screenChanged ? "V3" : "-";
            var effective = visibility != "-";
            var reach = patched.Steps <= 3 ? "R1" : patched.Steps <= 10 ? "R2" : "R3";
            const string NotComparable = "比べられない（パッチなしの2回で一致しない。確認用シナリオを決定的にする：ボットに think_ms を付けるなど）";
            var state = !stateReproducible ? NotComparable : stateChanged ? "変化あり" : "同じ";
            var sentText = !sentReproducible ? NotComparable : sentChanged ? "変化あり" : "同じ";
            var detail = $"確認用シナリオ：パッチあり {(patched.ScenarioOk ? "成功" : "失敗")}／なし {(basis.ScenarioOk ? "成功" : "失敗")}。新しい機械判定 {newFindings.Count} 件{(newFindings.Count > 0 ? "（" + newFindings[0] + "）" : string.Empty)}。状態 {state}。送信された状態 {sentText}";
            results.Add(new SeedCheckResult(seed.Id, true, effective, trivial, reach, visibility, detail));
            _log($"{seed.Id}：効いている {effective}、自明 {trivial}、層 {reach}{visibility}");
        }

        // 元のビルドに戻し、校正用ビルドのイメージを消す（校正用バグ1件につき約 1.6GB 増えていた）
        await _lab.UpAsync(new UpOptions("test", new HashSet<string>(StringComparer.Ordinal), null, false, false), cancellationToken).ConfigureAwait(false);
        var removed = await ImageBuilder.RemoveCalibrationImagesAsync(_lab.Docker, cancellationToken).ConfigureAwait(false);
        _log($"校正用ビルドのイメージを {removed} 個消しました");
        _run.WriteJson("seeds-check.json", results);
        return results;
    }

    private sealed record Outcome(bool ScenarioOk, int Steps, string StateHash, string ScreenHash, IReadOnlyList<string> Findings, string SentHash);

    private async Task<Outcome> RunConfirmAsync(SeedDefinition seed, string label, CancellationToken cancellationToken)
    {
        // 決定性の測定と同じ読み方（シードと時計の固定、この回の通信の記録だけ、{run} の固定）で状態を読む。
        // 以前は時計も {run} も固定せず、socket の値に検証環境の起動からの通信をすべて混ぜていたので、同じビルドでも毎回ハッシュが変わっていた
        // （実際に食い違ったのは、利用者の名前「bob-<乱数>」だった）
        var baseline = await JudgeContext.CaptureAsync(_lab, cancellationToken).ConfigureAwait(false);
        // {run}（利用者の名前などに使う）は、ScenarioRunner の既定では毎回の乱数なので、決定性の測定と同じく固定する
        var once = await new Determinism.DeterminismRunner(_lab, _run).RunOnceAsync(Scenario.Load(seed.ConfirmPath), 20261126, cancellationToken, new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["run"] = JsonValue.Create("seed") }).ConfigureAwait(false);
        var judges = await JudgeRunner.RunAsync(new JudgeContext(_lab, _run, baseline), JudgeRunner.Stage1(), cancellationToken).ConfigureAwait(false);
        var findings = judges.SelectMany(r => r.Findings).Where(f => f.Stage == 1 && f.Category != "coverage").Select(f => $"{f.Judge}:{f.Rule}").Distinct(StringComparer.Ordinal).ToList();
        var normalized = Determinism.DeterminismRunner.Normalize(once.State, _lab.Config.State.Determinism, once.ConnectionOrder);
        var stateHash = Hashing.Sha256Hex(Json.Canonical(Determinism.DeterminismRunner.StateNode(normalized)));

        // 送信された状態（状態のスキーマにない項目も含む。例：公開前の札を送る校正用バグ）
        var sent = Determinism.DeterminismRunner.SentNode(_lab.Records.Read(once.StartSeq), once.ConnectionOrder);
        var sentHash = Hashing.Sha256Hex(Json.Canonical(sent));
        var screens = once.Result.Steps.Where(s => s.Kind == "browser_screenshot").Select(s => s.Data?.ToJsonString() ?? string.Empty);

        // 証拠：正規化した後の状態（パッチあり・なし、なし同士で、どのキーが違うかを後から確かめられるように）
        _run.WriteJson($"seeds/{seed.Id}-{label}.json", new JsonObject
        {
            ["scenario_ok"] = once.Result.Ok,
            ["error"] = once.Result.Error,
            ["findings"] = new JsonArray(findings.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["state_hash"] = stateHash,
            ["sent_hash"] = sentHash,
            ["normalized"] = Determinism.DeterminismRunner.StateNode(normalized),
            ["sent"] = sent,
        });
        return new Outcome(once.Result.Ok, once.Result.Steps.Count, stateHash, Hashing.Sha256Hex(string.Join("|", screens)), findings, sentHash);
    }
}

/// <summary>層ごとの検出率（第7章、第19.6節、第19.11節）。</summary>
public sealed record LayerDetection(string Layer, int Seeded, int Detected, double Rate, double Lower, double Upper, bool Estimable);

/// <summary>校正の結果。</summary>
public sealed record CalibrationResult(string Id, DateTimeOffset At, IReadOnlyList<string> Seeds, IReadOnlyList<string> Detected, IReadOnlyList<LayerDetection> Layers, double? OverallLower, int RealBugs, double? ResidualUpper, string Basis);

/// <summary>
/// 校正（第7章「バグ埋め込みによる検出率の測定」）。校正用ビルド（パッチを当てた、配備できないビルド）で、
/// 探索（段階1の機械判定と、探索役のループ）を回し、仕込んだバグのうち見つかった割合を層ごとに出す。
/// 見つかったかどうかは、パッチなしのビルドでの同じ判定と比べて、パッチありでだけ出たものがあるかで決める（第22.5節）。
/// 残存バグは、実バグの数と、検出率の信頼区間の下限から出す（第19.6節、Mills の方法）。
/// </summary>
public sealed class Calibrator
{
    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public Calibrator(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>校正する。checks は品質チェックの結果（層の割り当てに使う）。explore は探索の処理（パッチありのビルドで呼ぶ）。</summary>
    public async Task<CalibrationResult> RunAsync(IReadOnlyList<SeedDefinition> seeds, IReadOnlyDictionary<string, SeedCheckResult> checks, int realBugs, Func<CancellationToken, Task<IReadOnlyList<Finding>>> explore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(explore);

        // 基準（パッチなし）での探索の結果
        await _lab.UpAsync(new UpOptions("test", new HashSet<string>(StringComparer.Ordinal), null, false, false), cancellationToken).ConfigureAwait(false);
        var baseFindings = (await explore(cancellationToken).ConfigureAwait(false)).Select(f => f.Fingerprint).ToHashSet(StringComparer.Ordinal);

        // クラッシュ系は他のバグと同じビルドに入れない（第19.6節）。1件ずつ校正用ビルドを作る
        var detected = new List<string>();
        foreach (var seed in seeds)
        {
            var clone = await SeedStore.PrepareCloneAsync(_lab.Workspace, [seed], "calib-" + seed.Id, cancellationToken).ConfigureAwait(false);
            await _lab.UpAsync(new UpOptions("test", new HashSet<string>(StringComparer.Ordinal), null, false, false, clone), cancellationToken).ConfigureAwait(false);
            var found = await explore(cancellationToken).ConfigureAwait(false);
            var onlyPatched = found.Where(f => !baseFindings.Contains(f.Fingerprint) && f.Category != "coverage").ToList();
            _run.WriteJson($"calibration/{seed.Id}.json", new JsonObject
            {
                ["seed"] = seed.Id,
                ["detected"] = onlyPatched.Count > 0,
                ["findings"] = new JsonArray(onlyPatched.Select(f => (JsonNode?)JsonValue.Create(f.Summary)).ToArray()),
            });
            if (onlyPatched.Count > 0)
            {
                detected.Add(seed.Id);
            }

            _log($"校正 {seed.Id}：{(onlyPatched.Count > 0 ? "見つかった" : "見つからなかった")}");
        }

        await _lab.UpAsync(new UpOptions("test", new HashSet<string>(StringComparer.Ordinal), null, false, false), cancellationToken).ConfigureAwait(false);
        var removedImages = await ImageBuilder.RemoveCalibrationImagesAsync(_lab.Docker, cancellationToken).ConfigureAwait(false);
        _log($"校正用ビルドのイメージを {removedImages} 個消しました");

        // 層ごとの検出率（自明な層 R1V1 は推定から外す。第19.11節）
        var layers = new List<LayerDetection>();
        foreach (var group in seeds.GroupBy(s => checks.TryGetValue(s.Id, out var c) ? c.Layer : "unknown"))
        {
            var seeded = group.Count();
            var hit = group.Count(s => detected.Contains(s.Id));
            var interval = Proportions.ClopperPearson(hit, seeded, 0.95);
            layers.Add(new LayerDetection(group.Key, seeded, hit, (double)hit / seeded, interval.Lower, interval.Upper, group.Key != "R1V1" && seeded >= 1 && (double)hit / seeded >= 0.3));
        }

        var estimable = layers.Where(l => l.Layer != "R1V1").ToList();
        var total = estimable.Sum(l => l.Seeded);
        var hits = estimable.Sum(l => l.Detected);
        double? overallLower = total == 0 ? null : Proportions.ClopperPearson(hits, total, 0.95).Lower;
        double? residual = overallLower is > 0 ? realBugs * (1 / overallLower.Value - 1) : null;
        var result = new CalibrationResult(_run.Id, DateTimeOffset.UtcNow, seeds.Select(s => s.Id).ToList(), detected, layers, overallLower, realBugs, residual, "generated");
        _run.WriteJson("calibration.json", result);
        SeedStore.MarkUsed(_lab.Workspace, seeds.Select(s => s.Id));
        var summary = Path.Combine(_lab.Workspace.DataDir, "calibration", "latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(summary) ?? _lab.Workspace.DataDir);
        File.WriteAllText(summary, Json.Serialize(result));
        return result;
    }
}

/// <summary>
/// カナリア1件の結果。Exercised は、異常の箇所を実際に通ったか（テスト用APIの hits。分からなければ null）。
/// 通らなかった試行は見逃しではないので、検出率に数えない。
/// </summary>
public sealed record CanaryResult(string Name, string Method, bool Applied, bool Detected, string Detail, bool? Exercised = null, int Games = 0);

/// <summary>
/// 毎ループのカナリア（第7.3節、第19.7節）。3〜5個を有効にし、すべて見つかるかを確かめる（死活監視）。
/// - プロキシ側の注入（アプリのコードを触らない）：遅延、500 の応答
/// - テスト用ビルドの異常（/__verify/faults。切り替えが要る場面に限る）
/// 判定は、段階1の機械判定（ボットの対局と汎用の判定・不変条件）で行う。結果は data/calibration/canaries.jsonl に累積する。
/// </summary>
public sealed class CanaryRunner
{
    /// <summary>異常の箇所を通らなかったときに、シードを変えて回す対局の上限（1カナリアあたり）。</summary>
    public const int MaxGames = 5;

    private readonly Lab.Lab _lab;
    private readonly Run _run;
    private readonly Action<string> _log;

    /// <summary>作る。</summary>
    public CanaryRunner(Lab.Lab lab, Run run, Action<string> log)
    {
        _lab = lab;
        _run = run;
        _log = log;
    }

    /// <summary>回す。</summary>
    public async Task<(IReadOnlyList<CanaryResult> Results, double CumulativeRate)> RunAsync(int count, int seed, CancellationToken cancellationToken)
    {
        var candidates = new List<(string Name, string Method)>();
        if (await _lab.HasTestApiAsync(cancellationToken).ConfigureAwait(false))
        {
            var faults = await _lab.TestApiAsync("GET", "/__verify/faults", null, cancellationToken).ConfigureAwait(false);
            candidates.AddRange((faults["faults"] as JsonArray ?? []).OfType<JsonObject>().Select(f => (f["name"]?.ToString() ?? string.Empty, "test_api")));
        }

        candidates.Add(("proxy-500-on-socket", "proxy"));
        var history = HistoryPath();
        var used = File.Exists(history) ? File.ReadAllLines(history).Select(l => JsonNode.Parse(l)?["name"]?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);

        // 使ったことのないものを優先し、毎回違う組み合わせにする
        var random = new Random(seed);
        var chosen = candidates.OrderBy(c => used.Contains(c.Name) ? 1 : 0).ThenBy(_ => random.Next()).Take(count).ToList();
        var results = new List<CanaryResult>();
        foreach (var (name, method) in chosen)
        {
            _log($"カナリア {name}（{method}）");
            await _lab.ResetAsync("initial", false, cancellationToken).ConfigureAwait(false);
            var applied = true;
            if (method == "test_api")
            {
                // テスト用APIは enable の後に disable_all を処理するので、2回に分けて送る
                await _lab.TestApiAsync("POST", "/__verify/faults", new JsonObject { ["disable_all"] = true }, cancellationToken).ConfigureAwait(false);
                var set = await _lab.TestApiAsync("POST", "/__verify/faults", new JsonObject { ["enable"] = new JsonArray(name) }, cancellationToken).ConfigureAwait(false);
                applied = (set["faults"] as JsonArray ?? []).OfType<JsonObject>().Any(f => f["name"]?.ToString() == name && f["enabled"]?.GetValue<bool>() == true && f["loaded"]?.GetValue<bool>() != false);

                // 局面の検査などがリセット（アプリの再起動）をはさんでも、異常を有効にし直し、通った回数を足し合わせる
                _lab.StickyFaults.Add(name);
                _lab.StickyFaultHits.Remove(name);
            }
            else
            {
                var rules = _lab.ReadRules();
                rules["rules"] = new JsonArray(new JsonObject { ["name"] = name, ["action"] = "status", ["status"] = 500, ["probability"] = 0.2, ["match"] = new JsonObject { ["path_prefix"] = "/socket.io", ["method"] = "POST" } });
                await _lab.WriteRulesAsync(rules).ConfigureAwait(false);
                await Task.Delay(700, cancellationToken).ConfigureAwait(false);
            }

            List<Finding> findings = [];
            string detail;
            int? hits = null;
            var played = 0;
            var startSeq = _lab.Records.Activity().LastSeq;
            try
            {
                // 異常の箇所を通らなかったら（例：効果の誤りを仕込んだカードが一度も出なかった、500 を返す対象の通信がなかった）、
                // シードを変えて回し直す。通ったかどうかは、テスト用APIの hits（異常の定義が fault.hit() を呼ぶもの）か、
                // プロキシが注入した記録（injected にカナリアの名前）の数で見る
                for (var attempt = 0; attempt < MaxGames; attempt++)
                {
                    var (games, gameFindings, _) = await new Probes.BotPlayRunner(_lab, _run, _log).RunAsync(1, seed + attempt, false, cancellationToken).ConfigureAwait(false);
                    played += games.Count;
                    findings.AddRange(gameFindings.Where(f => f.Category != "coverage"));
                    if (findings.Count > 0 || name == "accept-any-choice")
                    {
                        break;
                    }

                    hits = await ExercisedCountAsync(name, method, startSeq, cancellationToken).ConfigureAwait(false);
                    if (hits != 0)
                    {
                        break;
                    }

                    _log($"異常の箇所を通らなかったので、シードを変えてもう1対局回します（{attempt + 1}/{MaxGames}）");
                }

                if (name == "accept-any-choice")
                {
                    findings = [.. findings, .. (await new Probes.PhaseProber(_lab, _run, _log).RunAsync(cancellationToken).ConfigureAwait(false)).Where(f => f.Rule == "unoffered_choice_accepted")];
                }

                hits = await ExercisedCountAsync(name, method, startSeq, cancellationToken).ConfigureAwait(false);

                var through = hits is { } h ? $"、異常の箇所を {h} 回通った" : string.Empty;
                detail = findings.Count > 0 && hits == 0
                    ? $"所見はあったが、異常の箇所を一度も通っていない（{played} 対局）ので、カナリアの検出ではない：{findings[0].Summary}"
                    : findings.Count > 0
                        ? findings[0].Summary
                        : hits == 0 ? $"異常の箇所を通らなかった（{played} 対局）。見逃しとしては数えない" : $"見つからなかった（{played} 対局{through}）";
            }
            catch (CustosException e)
            {
                detail = "実行できなかった：" + e.Message;
            }
            finally
            {
                if (method == "test_api")
                {
                    _lab.StickyFaults.Remove(name);
                    await _lab.TestApiAsync("POST", "/__verify/faults", new JsonObject { ["disable_all"] = true }, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var rules = _lab.ReadRules();
                    rules["rules"] = new JsonArray();
                    await _lab.WriteRulesAsync(rules).ConfigureAwait(false);
                }
            }

            bool? exercised = hits is { } hitCount ? hitCount > 0 : null;
            var result = new CanaryResult(name, method, applied, applied && exercised != false && findings.Count > 0, detail, exercised, played);
            results.Add(result);
            Directory.CreateDirectory(Path.GetDirectoryName(history) ?? _lab.Workspace.DataDir);
            await File.AppendAllTextAsync(history, Json.SerializeCompact(new { name, method, applied = result.Applied, exercised, detected = result.Detected, games = played, at = DateTimeOffset.UtcNow }) + "\n", cancellationToken).ConfigureAwait(false);
        }

        _run.WriteJson("canaries.json", results);
        return (results, CumulativeRate(history) ?? 0);
    }

    /// <summary>
    /// 累積の検出率に数える試行か。数えないもの：異常が有効にならなかった（applied=false）、
    /// 異常の箇所を通らなかった（exercised=false）、後からフレームワークの誤りが分かり判定が当てにならない（excluded=true。note に理由）。
    /// </summary>
    public static bool Counts(JsonNode entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry["applied"]?.GetValue<bool>() != false && entry["exercised"]?.GetValue<bool>() != false && entry["excluded"]?.GetValue<bool>() != true;
    }

    /// <summary>累積の検出率（data/calibration/canaries.jsonl）。数える試行がなければ null。カナリアの表示・成熟度・リリース判定で同じものを使う。</summary>
    public static double? CumulativeRate(string historyPath)
    {
        if (!File.Exists(historyPath))
        {
            return null;
        }

        var all = File.ReadAllLines(historyPath).Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)).OfType<JsonNode>().Where(Counts).ToList();
        return all.Count == 0 ? null : (double)all.Count(n => n["detected"]?.GetValue<bool>() == true) / all.Count;
    }

    /// <summary>異常の箇所を通った回数。テスト用APIは hits（定義が数えていなければ null）、プロキシは注入した記録の数。</summary>
    private async Task<int?> ExercisedCountAsync(string name, string method, long sinceSeq, CancellationToken cancellationToken) =>
        method == "test_api"
            ? await FaultHitsAsync(name, cancellationToken).ConfigureAwait(false)
            : _lab.Records.Read(sinceSeq).Count(e => e.Injected == name);

    /// <summary>テスト用APIの hits（異常の箇所を通った回数）。定義が数えていなければ null。</summary>
    private async Task<int?> FaultHitsAsync(string name, CancellationToken cancellationToken)
    {
        var faults = await _lab.TestApiAsync("GET", "/__verify/faults", null, cancellationToken).ConfigureAwait(false);
        var fault = (faults["faults"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(f => f["name"]?.ToString() == name);
        return fault?["hits"] is JsonValue v && v.TryGetValue<int>(out var hits) ? hits + _lab.StickyFaultHits.GetValueOrDefault(name) : null;
    }

    private string HistoryPath() => Path.Combine(_lab.Workspace.DataDir, "calibration", "canaries.jsonl");
}

/// <summary>文字列化の補助。</summary>
internal static class CalibrationText
{
    public static string P(double value) => value.ToString("P0", CultureInfo.InvariantCulture);
}
