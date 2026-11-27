using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Results;
using Custos.Core.Scenarios;
using Custos.Core.Util;
using Custos.Statistics.Performance;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.Scenarios;

namespace Custos.Web.SocketIO.Performance;

/// <summary>計測1回の結果。</summary>
public sealed record Measurement(
    int Games,
    int Completed,
    int Moves,
    double ErrorRate,
    IReadOnlyList<double> LatenciesMs,
    double P50Ms,
    double P99Ms,
    double SendLagP99Ms,
    double LoadCpuRatio,
    double ReferenceRatio,
    double AppMemoryBeforeMb,
    double AppMemoryAfterMb,
    double ProbeP99Ms,
    double CpuStealRatio,
    bool Swap,
    MeasurementState State,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    double WallMs)
{
    /// <summary>JSON にする。</summary>
    public JsonObject ToJson() => new()
    {
        ["games"] = Games,
        ["completed"] = Completed,
        ["moves"] = Moves,
        ["error_rate"] = ErrorRate,
        ["latency_p50_ms"] = P50Ms,
        ["latency_p99_ms"] = P99Ms,
        ["send_lag_p99_ms"] = SendLagP99Ms,
        ["load_cpu_ratio"] = LoadCpuRatio,
        ["reference_ratio"] = ReferenceRatio,
        ["app_memory_before_mb"] = AppMemoryBeforeMb,
        ["app_memory_after_mb"] = AppMemoryAfterMb,
        ["probe_p99_ms"] = ProbeP99Ms,
        ["cpu_steal_ratio"] = CpuStealRatio,
        ["swap"] = Swap,
        ["state"] = State.ToString(),
        ["reasons"] = new JsonArray(Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        ["warnings"] = new JsonArray(Warnings.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        ["wall_ms"] = WallMs,
    };
}

/// <summary>
/// 性能の計測（設計書 第13章、第16.5〜16.7節、第23章、第26.4節）。負荷は LLM を使わないボットで出す。
/// 計測ごとに妥当性を確かめ（負荷側の CPU、送信の遅れ、基準ワークロードのずれ、スワップ、CPU の横取り、定常状態）、無効なものは使わない。
/// </summary>
public static class PerfRunner
{
    /// <summary>負荷をかけて1回計測する。</summary>
    public static async Task<Measurement> MeasureAsync(Lab.Lab lab, int games, int concurrency, int seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var client = await lab.ClientAsync(1, cancellationToken).ConfigureAwait(false);
        var runner = new ScenarioRunner(lab, null, _ => { });
        var headers = await runner.BrowserLikeHeadersAsync(cancellationToken).ConfigureAwait(false);
        var protocol = BotProtocolLoader.Load(lab.Workspace);
        var setup = LoadSetup(lab.Workspace);
        var referenceBefore = await ReferenceWorkloadAsync(lab, cancellationToken).ConfigureAwait(false);
        var memoryBefore = await AppMemoryMbAsync(lab, cancellationToken).ConfigureAwait(false);
        var stealBefore = await ReadCpuStatAsync(lab, cancellationToken).ConfigureAwait(false);
        var swapBefore = await SwapActivityAsync(lab, cancellationToken).ConfigureAwait(false);
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeTask = ProbeLoopAsync(lab, probeCts.Token);
        var stopwatch = Stopwatch.StartNew();
        var load = await client.CallObjectAsync("load.run", new JsonObject
        {
            ["url"] = lab.PublicUrl.ToString(),
            ["games"] = games,
            ["concurrency"] = concurrency,
            ["seed"] = seed,
            ["transport"] = ScenarioRunner.DefaultTransport,
            ["path"] = lab.Config.Environment.App.SocketIoPath,
            ["headers"] = headers,
            ["protocol"] = protocol,
            ["setup"] = setup,
            ["game_timeout_ms"] = 120000,
        }, TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        await probeCts.CancelAsync().ConfigureAwait(false);
        var probes = await probeTask.ConfigureAwait(false);
        var memoryAfter = await AppMemoryMbAsync(lab, cancellationToken).ConfigureAwait(false);
        var referenceAfter = await ReferenceWorkloadAsync(lab, cancellationToken).ConfigureAwait(false);
        var stealAfter = await ReadCpuStatAsync(lab, cancellationToken).ConfigureAwait(false);
        var swapAfter = await SwapActivityAsync(lab, cancellationToken).ConfigureAwait(false);

        // スワップ：計測の間にページの出し入れがあったか（以前は「使用中のスワップが 0 より大きいか」で、
        // 計測と関係なく前に1ページ出ただけのホストでは、すべての計測が無効になっていた）
        var swap = swapAfter > swapBefore;

        var latencies = (load["latency_ms"] as JsonArray ?? []).Select(v => v?.GetValue<double>() ?? 0).ToList();
        var lags = (load["send_lag_ms"] as JsonArray ?? []).Select(v => v?.GetValue<double>() ?? 0).ToList();
        var wall = load["wall_ms"]?.GetValue<long>() ?? (long)stopwatch.Elapsed.TotalMilliseconds;
        var cpuMs = load["cpu_ms"]?.GetValue<double>() ?? 0;
        var cores = LoadCores(lab);
        var loadCpu = wall <= 0 ? 0 : cpuMs / (wall * cores);
        var errorRate = load["error_rate"]?.GetValue<double>() ?? 0;
        var steal = stealAfter.Total > stealBefore.Total ? (double)(stealAfter.Steal - stealBefore.Steal) / (stealAfter.Total - stealBefore.Total) : 0;
        var slo = SloThreshold(lab);
        var half = latencies.Count / 2;
        var warm = Math.Min(latencies.Count / 10, 20);
        var steady = latencies.Skip(warm).ToList();
        var observation = new MeasurementObservation(
            loadCpu,
            lags.Count > 0 ? Descriptive.Percentile(lags, 0.99) : null,
            slo,
            referenceBefore > 0 ? referenceAfter / referenceBefore : null,
            swap,
            steal,
            steady.Take(steady.Count / 2).ToList(),
            steady.Skip(steady.Count / 2).ToList(),
            errorRate,
            warm > 0);
        var profile = lab.State.Profile != null && lab.Config.Environment.ResourceProfiles.TryGetValue(lab.State.Profile, out var p) ? p : null;
        var verdict = MeasurementValidity.Evaluate(observation, profile?.Validity ?? ValidityCriteria.Default);
        _ = half;
        return new Measurement(
            games,
            load["completed"]?.GetValue<int>() ?? 0,
            load["moves"]?.GetValue<int>() ?? 0,
            errorRate,
            latencies,
            latencies.Count > 0 ? Descriptive.Percentile(latencies, 0.5) : 0,
            latencies.Count > 0 ? Descriptive.Percentile(latencies, 0.99) : 0,
            lags.Count > 0 ? Descriptive.Percentile(lags, 0.99) : 0,
            Math.Round(loadCpu, 4),
            referenceBefore > 0 ? Math.Round(referenceAfter / referenceBefore, 4) : 1,
            memoryBefore,
            memoryAfter,
            probes.Count > 0 ? Descriptive.Percentile(probes, 0.99) : 0,
            Math.Round(steal, 4),
            swap,
            verdict.State,
            verdict.Reasons,
            verdict.Warnings,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 計測を runs 回行う（無効な計測は測り直す。3回続けて無効なら環境の問題として止める。第23.4節）。
    /// </summary>
    public static async Task<(List<Measurement> Valid, List<Measurement> All, bool EnvironmentProblem)> SeriesAsync(Lab.Lab lab, int runs, int games, int concurrency, int seed, Run? run, string label, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var valid = new List<Measurement>();
        var all = new List<Measurement>();
        var consecutiveInvalid = 0;
        await WarmUpAsync(lab, cancellationToken).ConfigureAwait(false);
        while (valid.Count < runs)
        {
            var measurement = await MeasureAsync(lab, games, concurrency, seed + all.Count, cancellationToken).ConfigureAwait(false);
            all.Add(measurement);
            run?.AppendJsonLine($"perf/{label}.jsonl", measurement.ToJson());
            if (measurement.State == MeasurementState.Invalid)
            {
                consecutiveInvalid++;
                if (consecutiveInvalid >= 3)
                {
                    return (valid, all, true);
                }

                continue;
            }

            consecutiveInvalid = 0;
            valid.Add(measurement);
        }

        return (valid, all, false);
    }

    /// <summary>F-1：プロファイルの制限が、アプリのコンテナから見えるか。</summary>
    public static async Task<JsonObject> CheckProfileAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var profile = lab.Config.Environment.ResourceProfiles[lab.State.Profile ?? string.Empty];
        var result = await lab.Docker.RunAsync(["exec", Containers.App(lab.Project), "sh", "-c", "cat /sys/fs/cgroup/cpuset.cpus.effective 2>/dev/null; echo '|'; cat /sys/fs/cgroup/memory.max 2>/dev/null; echo '|'; cat /sys/fs/cgroup/cpu.max 2>/dev/null"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        var parts = result.StandardOutput.Split('|').Select(s => s.Trim()).ToList();
        var problems = new List<string>();
        if (profile.Nodes.TryGetValue("app", out var app))
        {
            if (app.Cpuset != null && parts.Count > 0 && parts[0] != app.Cpuset)
            {
                problems.Add($"cpuset {parts[0]}（期待 {app.Cpuset}）");
            }

            if (app.MemoryBytes is { } memory && parts.Count > 1 && parts[1] != memory.ToString(CultureInfo.InvariantCulture))
            {
                problems.Add($"memory.max {parts[1]}（期待 {memory}）");
            }
        }

        var netem = await lab.Docker.RunAsync(["exec", Containers.Edge(lab.Project), "sh", "-c", "tc qdisc show 2>/dev/null"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (profile.Network != null && !netem.StandardOutput.Contains("netem", StringComparison.Ordinal))
        {
            problems.Add("仮想ネットワークの条件（netem）が適用されていない");
        }

        return new JsonObject
        {
            ["ok"] = problems.Count == 0,
            ["summary"] = problems.Count == 0 ? $"cpuset={parts.ElementAtOrDefault(0)}、memory.max={parts.ElementAtOrDefault(1)}、netem={(netem.StandardOutput.Contains("netem", StringComparison.Ordinal) ? "あり" : "なし")}" : string.Join("、", problems),
            ["cgroup"] = result.StandardOutput.Trim(),
            ["tc"] = netem.StandardOutput.Trim(),
        };
    }

    /// <summary>F-2：負荷を出す側の資源を2倍にしても、結果が変わらないこと。</summary>
    public static async Task<JsonObject> HeadroomAsync(Lab.Lab lab, Run run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var container = Containers.Client(lab.Project, 1);
        var inspect = await lab.Docker.InspectAsync(container, cancellationToken).ConfigureAwait(false);
        var original = inspect?["HostConfig"]?["CpusetCpus"]?.GetValue<string>() ?? string.Empty;
        var normal = await SeriesAsync(lab, 3, 4, 2, 11, run, "headroom-normal", cancellationToken).ConfigureAwait(false);
        var cpus = await lab.Docker.RunCheckedAsync(["info", "--format", "{{.NCPU}}"], "docker info", TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        var all = int.Parse(cpus.Trim(), CultureInfo.InvariantCulture);
        await lab.Docker.RunAsync(["update", "--cpuset-cpus", $"0-{all - 1}", container], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        var doubled = await SeriesAsync(lab, 3, 4, 2, 11, run, "headroom-doubled", cancellationToken).ConfigureAwait(false);
        await lab.Docker.RunAsync(["update", "--cpuset-cpus", string.IsNullOrEmpty(original) ? $"0-{all - 1}" : original, container], TimeSpan.FromSeconds(30), cancellationToken: cancellationToken).ConfigureAwait(false);
        var a = normal.Valid.Select(m => m.P99Ms).ToList();
        var b = doubled.Valid.Select(m => m.P99Ms).ToList();
        var comparison = Comparison.Compare(a, b, 0.20, 3, 0.5);
        var maxLoad = normal.All.Select(m => m.LoadCpuRatio).DefaultIfEmpty(0).Max();
        var ok = comparison.Outcome == ComparisonOutcome.NoDifference && maxLoad < 0.70;
        return new JsonObject
        {
            ["ok"] = ok,
            ["summary"] = $"負荷側の CPU 使用率の最大 {maxLoad:P0}。資源を2倍にしたときの99パーセンタイルの比較：{comparison.Outcome}（変化 {comparison.RelativeChange:P1}）",
            ["normal"] = new JsonArray(normal.All.Select(m => (JsonNode?)m.ToJson()).ToArray()),
            ["doubled"] = new JsonArray(doubled.All.Select(m => (JsonNode?)m.ToJson()).ToArray()),
        };
    }

    /// <summary>F-3：同じビルド同士で「差なし」、性能のカナリア（プロキシで遅延を注入）で「劣化あり」（第23.3節）。</summary>
    public static async Task<JsonObject> NegativePositiveAsync(Lab.Lab lab, Run run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var runs = 5;
        var baseline = await SeriesAsync(lab, runs, 4, 2, 21, run, "negative-a", cancellationToken).ConfigureAwait(false);
        var same = await SeriesAsync(lab, runs, 4, 2, 21, run, "negative-b", cancellationToken).ConfigureAwait(false);
        var rules = lab.ReadRules();
        rules["rules"] = new JsonArray(new JsonObject { ["name"] = "perf-canary-delay", ["action"] = "delay", ["ms"] = 120, ["match"] = new JsonObject { ["event"] = "*", ["dir"] = "s2c" } });
        await lab.WriteRulesAsync(rules).ConfigureAwait(false);
        await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        var canary = await SeriesAsync(lab, runs, 4, 2, 21, run, "positive-canary", cancellationToken).ConfigureAwait(false);
        rules["rules"] = new JsonArray();
        await lab.WriteRulesAsync(rules).ConfigureAwait(false);
        var detect = Detectable(lab);
        var maxCv = Comparison.MaxCoefficientOfVariation(detect, runs);
        var negative = Comparison.Compare(baseline.Valid.Select(m => m.P99Ms).ToList(), same.Valid.Select(m => m.P99Ms).ToList(), detect, runs, Math.Max(maxCv, 0.5));
        var positive = Comparison.Compare(baseline.Valid.Select(m => m.P99Ms).ToList(), canary.Valid.Select(m => m.P99Ms).ToList(), detect, runs, Math.Max(maxCv, 0.5));
        var ok = negative.Outcome == ComparisonOutcome.NoDifference && positive.Outcome == ComparisonOutcome.Regression;
        return new JsonObject
        {
            ["ok"] = ok,
            ["summary"] = $"同じビルド同士：{negative.Outcome}（変化 {negative.RelativeChange:P1}）、性能のカナリア：{positive.Outcome}（変化 {positive.RelativeChange:P1}）",
            ["negative"] = Describe(negative),
            ["positive"] = Describe(positive),
        };
    }

    /// <summary>F-4：同じ条件で複数回計測し、ばらつき（変動係数）が許容範囲内であること。</summary>
    public static async Task<JsonObject> VarianceAsync(Lab.Lab lab, Run run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var profile = lab.Config.Environment.ResourceProfiles[lab.State.Profile ?? string.Empty];
        var series = await SeriesAsync(lab, profile.Validity.Runs, 4, 2, 31, run, "variance", cancellationToken).ConfigureAwait(false);
        var values = series.Valid.Select(m => m.P99Ms).ToList();
        var verdict = MeasurementValidity.EvaluateSeries(values, profile.Validity);
        return new JsonObject
        {
            ["ok"] = verdict.Comparable && !series.EnvironmentProblem,
            ["summary"] = $"{values.Count} 回の99パーセンタイルの変動係数 {verdict.CoefficientOfVariation:P1}（上限 {profile.Validity.MaxCv:P1}）{(series.EnvironmentProblem ? "。3回続けて無効になった（環境の問題）" : string.Empty)}",
            ["reasons"] = new JsonArray(verdict.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        };
    }

    /// <summary>容量：基準を守れる最大の同時対局数（第23.6節の「換算するのは容量」、第26.4節）。</summary>
    public static async Task<JsonObject> CapacityAsync(Lab.Lab lab, Run run, int maxConcurrency, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var slo = SloThreshold(lab) ?? 300;
        var steps = new JsonArray();
        await WarmUpAsync(lab, cancellationToken).ConfigureAwait(false);
        var capacity = 0;
        var bottleneck = "unknown";
        for (var concurrency = 1; concurrency <= maxConcurrency; concurrency *= 2)
        {
            // 無効な計測（負荷側の遅れ、基準ワークロードのずれなど）は「基準を守れない」ではなく「分からない」なので、
            // 測り直す（3回続けて無効なら環境の問題として止める。第23.4節。以前は1回目の無効で容量 0 になっていた）
            Measurement? measurement = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                // 対局数は、同時数の2倍と、perf run の既定（10 対局）の大きい方。同時1〜4のときに2〜8対局（数十手）しかないと、
                // 99パーセンタイルが最大値とほぼ同じになり、1回の再送（損失 0.5 % のプロファイル）で基準を超えて容量が決まってしまう
                measurement = await MeasureAsync(lab, Math.Max(10, concurrency * 2), concurrency, 41 + concurrency + (attempt * 1000), cancellationToken).ConfigureAwait(false);
                run.AppendJsonLine("perf/capacity.jsonl", measurement.ToJson());
                if (measurement.State != MeasurementState.Invalid)
                {
                    break;
                }
            }

            if (measurement == null || measurement.State == MeasurementState.Invalid)
            {
                steps.Add(new JsonObject { ["concurrency"] = concurrency, ["state"] = "Invalid", ["reasons"] = new JsonArray((measurement?.Reasons ?? []).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()) });
                bottleneck = "environment";
                break;
            }

            var within = measurement.P99Ms <= slo && measurement.ErrorRate <= 0.001;
            steps.Add(new JsonObject { ["concurrency"] = concurrency, ["p99_ms"] = measurement.P99Ms, ["error_rate"] = measurement.ErrorRate, ["state"] = measurement.State.ToString(), ["within_slo"] = within });
            if (!within)
            {
                bottleneck = measurement.LoadCpuRatio > 0.7 ? "load_generator" : measurement.ProbeP99Ms > 50 ? "app_cpu" : "latency";
                break;
            }

            capacity = concurrency;
        }

        return new JsonObject { ["capacity_concurrent_games"] = capacity, ["slo_ms"] = slo, ["bottleneck"] = bottleneck, ["steps"] = steps };
    }

    private static JsonObject Describe(ComparisonResult result) => new()
    {
        ["outcome"] = result.Outcome.ToString(),
        ["relative_change"] = double.IsNaN(result.RelativeChange) ? null : result.RelativeChange,
        ["p_worse"] = double.IsNaN(result.PValueWorse) ? null : result.PValueWorse,
        ["reasons"] = new JsonArray(result.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
    };

    private static double Detectable(Lab.Lab lab) =>
        lab.State.Profile != null && lab.Config.Environment.ResourceProfiles.TryGetValue(lab.State.Profile, out var p) ? p.Validity.DetectRegression : 0.2;

    private static double? SloThreshold(Lab.Lab lab) =>
        lab.Config.Performance?.Slo.FirstOrDefault(s => s.Profile == lab.State.Profile && s.Unit == "ms")?.Threshold;

    private static int LoadCores(Lab.Lab lab)
    {
        if (lab.State.Profile != null && lab.Config.Environment.ResourceProfiles.TryGetValue(lab.State.Profile, out var p) && p.Nodes.TryGetValue("load", out var load))
        {
            if (load.Cpus is { } cpus)
            {
                return Math.Max(1, (int)cpus);
            }
        }

        return 1;
    }

    /// <summary>検証用リポジトリの scenarios/perf/setup.yaml（1対局の準備：登録、部屋、参加、準備完了）。</summary>
    private static JsonArray LoadSetup(Workspace workspace)
    {
        var path = Path.Combine(workspace.VerifyRepo, "scenarios", "perf", "setup.yaml");
        if (!File.Exists(path))
        {
            throw new CustosException($"負荷のボットの準備の手順がありません：{path}");
        }

        var scenario = Scenario.Load(path);
        return new JsonArray(scenario.Steps.Where(s => s.Kind == "emit").Select(s => (JsonNode?)s.Args.DeepClone()).ToArray());
    }

    /// <summary>基準ワークロード（決まった小さな計算）の所要時間（第23.1節）。</summary>
    /// <summary>
    /// 計測の前に基準ワークロードを一度回して捨てる。起動直後の1回目は遅く（実測で約3倍）、最初の計測が
    /// 「基準ワークロードがずれた」で毎回無効になっていた。
    /// </summary>
    private static Task<double> WarmUpAsync(Lab.Lab lab, CancellationToken cancellationToken) => ReferenceWorkloadAsync(lab, cancellationToken);

    private static async Task<double> ReferenceWorkloadAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var result = await lab.Docker.RunAsync(["exec", Containers.Ops(lab.Project), "sh", "-c", "i=0; while [ $i -lt 30000 ]; do i=$((i+1)); done"], TimeSpan.FromMinutes(1), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? watch.Elapsed.TotalMilliseconds : 0;
    }

    private static async Task<double> AppMemoryMbAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        var result = await lab.Docker.RunAsync(["exec", Containers.App(lab.Project), "sh", "-c", "cat /sys/fs/cgroup/memory.current"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        return long.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) ? Math.Round(bytes / 1048576.0, 1) : 0;
    }

    private static async Task<(long Total, long Steal)> ReadCpuStatAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        var result = await lab.Docker.RunAsync(["exec", Containers.Ops(lab.Project), "sh", "-c", "head -1 /proc/stat"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        var parts = result.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(p => long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();
        return parts.Count >= 8 ? (parts.Sum(), parts[7]) : (0, 0);
    }

    /// <summary>ホストのスワップの出し入れの累計（/proc/vmstat の pswpin + pswpout。ページ数）。</summary>
    private static async Task<long> SwapActivityAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        var result = await lab.Docker.RunAsync(["exec", Containers.Ops(lab.Project), "sh", "-c", "grep -E '^(pswpin|pswpout) ' /proc/vmstat"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2)
            .Sum(p => long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0);
    }

    /// <summary>計測の間、アプリの応答時間を外から測る（イベントループの遅れの近似。第25.2節）。</summary>
    private static async Task<List<double>> ProbeLoopAsync(Lab.Lab lab, CancellationToken cancellationToken)
    {
        var values = new List<double>();
        var ops = await lab.OpsAsync(CancellationToken.None).ConfigureAwait(false);
        var app = lab.Config.Environment.App;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var watch = Stopwatch.StartNew();
                await ops.CallAsync("http.request", new JsonObject { ["url"] = $"http://app:{app.ServerPort}{app.SocketIoPath}?EIO=4&transport=polling", ["timeout_ms"] = 5000 }, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                values.Add(watch.Elapsed.TotalMilliseconds);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return values;
    }
}
