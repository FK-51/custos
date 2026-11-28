using Custos.Core.Util;
using Custos.Web.SocketIO.Calibration;

namespace Custos.Web.SocketIO.Tests;

/// <summary>シード作成役（代役の機械的な変異）と、下書きの確認・保存（設計書 第19.4節、第19.8節）。</summary>
public sealed class SeedAuthoringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "custos-seed-author-" + Guid.NewGuid().ToString("N"));
    private readonly Workspace _workspace;

    public SeedAuthoringTests()
    {
        var app = Path.Combine(_root, "app");
        var verify = Path.Combine(_root, "verify");
        Directory.CreateDirectory(Path.Combine(app, "src", "domain"));
        Directory.CreateDirectory(Path.Combine(app, "src", "__tests__"));
        Directory.CreateDirectory(Path.Combine(verify, "scenarios"));
        File.WriteAllText(Path.Combine(app, "src", "domain", "rules.ts"), string.Join('\n',
            "import { x } from './x';",
            "// コメントの中の a < b は変えない",
            "export function canDraw(count: number, limit: number): boolean {",
            "  if (count < limit && limit > 0) {",
            "    return true;",
            "  }",
            "  const draw = { get: 3 };",
            "  return count === limit;",
            "}",
            ""));
        File.WriteAllText(Path.Combine(app, "src", "__tests__", "rules.test.ts"), "expect(1 < 2).toBe(true);\n");
        File.WriteAllText(Path.Combine(verify, "scenarios", "bot-game.yaml"), string.Join('\n',
            "# 1対局",
            "version: 1",
            "name: bot-game",
            "steps:",
            "  - connect: { user: \"a{game}\" }",
            "  - bot: { users: [\"a{game}\"], policy: random, seed: \"{seed}\", until: end }",
            ""));
        Git(app, "init", "-q");
        Git(app, "add", "-A");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-qm", "base");
        _workspace = new Workspace(verify, Path.Combine(_root, "data"), app, Path.Combine(_root, "framework"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Git(string repo, params string[] args)
    {
        var result = ProcessRunner.RunAsync("git", ["-C", repo, .. args], timeout: TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
        result.EnsureSuccess("git " + string.Join(' ', args));
    }

    [Fact]
    public async Task MutationsAreDeterministicApplyAndSkipCommentsAndTests()
    {
        var first = await MutationSeedAuthor.GenerateAsync(_workspace, _workspace.AppRepo, 20, 7, ["src/"], CancellationToken.None);
        var second = await MutationSeedAuthor.GenerateAsync(_workspace, _workspace.AppRepo, 20, 7, ["src/"], CancellationToken.None);
        Assert.Equal(first.Select(d => d.Patch), second.Select(d => d.Patch));
        Assert.NotEmpty(first);

        // テストのファイル、コメント、import には変異を入れない
        Assert.All(first, d => Assert.Contains("src/domain/rules.ts", d.Patch, StringComparison.Ordinal));
        Assert.DoesNotContain(first, d => d.Patch.Contains("+// コメント", StringComparison.Ordinal));

        // 種類：境界（<→<=）、論理（&&→||）、等しいか（===→!==）、数値（3→4）、真偽（true→false）
        var added = first.SelectMany(d => d.Patch.Split('\n').Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal))).ToList();
        Assert.Contains(added, l => l.Contains("count <= limit", StringComparison.Ordinal));
        Assert.Contains(added, l => l.Contains("|| limit", StringComparison.Ordinal));
        Assert.Contains(added, l => l.Contains("count !== limit", StringComparison.Ordinal));
        Assert.Contains(added, l => l.Contains("get: 4", StringComparison.Ordinal));
        Assert.Contains(added, l => l.Contains("return false", StringComparison.Ordinal));

        // どれも基準に単独で当たり、目印がない。確認用シナリオは bot-game.yaml から作る
        var checks = await SeedAuthoring.CheckAsync(_workspace.AppRepo, first, CancellationToken.None);
        Assert.All(checks, c => Assert.True(c.Ok, c.Detail));
        Assert.All(first, d => Assert.Contains("name: confirm-{id}", d.Confirm, StringComparison.Ordinal));
        Assert.All(first, d => Assert.DoesNotContain("{game}", d.Confirm, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DraftsWithMarkersOrBadPatchesAreRejected()
    {
        var good = (await MutationSeedAuthor.GenerateAsync(_workspace, _workspace.AppRepo, 1, 1, ["src/"], CancellationToken.None))[0];
        var marked = good with { Patch = good.Patch.Replace("+  ", "+  /* seed */ ", StringComparison.Ordinal) };
        var broken = good with { Patch = good.Patch.Replace("src/domain/rules.ts", "src/domain/none.ts", StringComparison.Ordinal) };
        var noConfirm = good with { Confirm = string.Empty };
        var checks = await SeedAuthoring.CheckAsync(_workspace.AppRepo, [good, marked, broken, noConfirm], CancellationToken.None);
        Assert.Equal([true, false, false, false], checks.Select(c => c.Ok));
        Assert.Contains("目印", checks[1].Detail, StringComparison.Ordinal);
        Assert.Contains("当たらない", checks[2].Detail, StringComparison.Ordinal);
        Assert.Contains("確認用シナリオ", checks[3].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedDraftsGetNextIds()
    {
        Directory.CreateDirectory(Path.Combine(_workspace.VerifyRepo, "seeds", "seed-018"));
        var drafts = await MutationSeedAuthor.GenerateAsync(_workspace, _workspace.AppRepo, 2, 3, ["src/"], CancellationToken.None);
        var ids = SeedAuthoring.Save(_workspace, drafts);
        Assert.Equal(["seed-019", "seed-020"], ids);
        var seeds = SeedStore.Load(_workspace);
        Assert.Equal(["seed-019", "seed-020"], seeds.Select(s => s.Id));
        Assert.All(seeds, s => Assert.True(File.Exists(s.PatchPath) && File.Exists(s.ConfirmPath)));
        Assert.Contains("name: confirm-seed-019", File.ReadAllText(seeds[0].ConfirmPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmIsBuiltFromTheDeterminismScenarioWhenPresent()
    {
        // 決定性の測定のシナリオ（準備の順序が決まり、返答の間を空ける）を写し、方針・シード・手数を変える
        File.WriteAllText(Path.Combine(_workspace.VerifyRepo, "scenarios", "determinism.yaml"), string.Join('\n',
            "# 決定性の測定",
            "version: 1",
            "name: determinism",
            "steps:",
            "  - include: { file: two-players-start.yaml }",
            "  - bot: { users: [alice, bob], policy: first, seed: 1, max_moves: 12, think_ms: 300, timeout_ms: 90000 }",
            "  - settle: { quiet_ms: 500 }",
            ""));
        var confirm = SeedAuthoring.Confirm(_workspace, 1007);
        Assert.Contains("name: confirm-{id}", confirm, StringComparison.Ordinal);
        Assert.Contains("include: { file: two-players-start.yaml }", confirm, StringComparison.Ordinal);
        Assert.Contains("policy: random, seed: 1007, until: end, think_ms: 300, timeout_ms: 300000", confirm, StringComparison.Ordinal);
        Assert.DoesNotContain("max_moves", confirm, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludedMeasurementsAreNotCumulated()
    {
        var dir = Path.Combine(_workspace.DataDir, "calibration");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "verifier.jsonl"),
        [
            """{"agent":"scripted","all_total":3,"all_flagged":3,"missed_total":2,"missed_flagged":2,"excluded":true,"note":"x"}""",
            """{"agent":"scripted","all_total":4,"all_flagged":1,"missed_total":2,"missed_flagged":0}""",
        ]);
        var c = VerifierSensitivity.Cumulative(_workspace, "scripted");
        Assert.NotNull(c);
        Assert.Equal(4, c.Value.AllTotal);
        Assert.Equal(0, c.Value.Used);
    }

    [Fact]
    public void CumulativeSensitivityUsesTheLowerOfMissedAndAll()
    {
        var dir = Path.Combine(_workspace.DataDir, "calibration");
        var path = Path.Combine(dir, "verifier.jsonl");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(path,
        [
            """{"agent":"scripted","all_total":10,"all_flagged":8,"missed_total":4,"missed_flagged":0}""",
            """{"agent":"scripted","all_total":4,"all_flagged":4,"missed_total":0,"missed_flagged":0}""",
            """{"agent":"claude-code","all_total":2,"all_flagged":2,"missed_total":2,"missed_flagged":2}""",
        ]);
        var scripted = VerifierSensitivity.Cumulative(_workspace, "scripted");
        Assert.NotNull(scripted);
        Assert.Equal(14, scripted.Value.AllTotal);
        Assert.Equal(4, scripted.Value.MissedTotal);
        Assert.Equal(0, scripted.Value.Used);       // 見落とした4件を1件も指摘できない：下限は 0
        Assert.True(scripted.Value.All > 0.5);
        Assert.Null(VerifierSensitivity.Cumulative(_workspace, "human"));
    }
}
