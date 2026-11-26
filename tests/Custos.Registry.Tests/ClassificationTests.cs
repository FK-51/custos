using Custos.Registry.Commits;
using Custos.Registry.Git;

namespace Custos.Registry.Tests;

public class ClassificationTests
{
    [Fact]
    public void ComposeAddsTypePrefixAndTrailers()
    {
        var message = CommitTrailers.Compose("注文キャンセル後に支払いができる問題を修正", CommitType.Fix, "BUG-0142");
        Assert.Equal("fix: 注文キャンセル後に支払いができる問題を修正\n\nType: fix\nBug: BUG-0142\n", message);
    }

    [Fact]
    public void ComposeReplacesHandwrittenTrailersAndKeepsBody()
    {
        var message = CommitTrailers.Compose("feat(lobby): ルーム名を表示\n\n本文の説明。\n\nType: fix\nBug: BUG-9999\n", CommitType.Feat, null);
        Assert.Equal("feat(lobby): ルーム名を表示\n\n本文の説明。\n\nType: feat\n", message);
        Assert.Equal(("feat", (string?)null), CommitTrailers.Parse(message));
    }

    [Fact]
    public void ParseReadsTrailersFromLastParagraph()
    {
        Assert.Equal(("fix", "BUG-0001"), CommitTrailers.Parse("fix: x\n\nbody\n\nType: fix\nBug: BUG-0001\nCo-Authored-By: someone"));
        Assert.Equal(((string?)null, (string?)null), CommitTrailers.Parse("fix: x"));
        Assert.Equal(((string?)null, (string?)null), CommitTrailers.Parse("fix: x\n\nType: fix\n\nlater paragraph"));
    }

    [Theory]
    [InlineData("feat: x", "feat", true)]
    [InlineData("feat(scope): x", "feat", true)]
    [InlineData("feature: x", "feat", false)]
    [InlineData("fix x", "fix", false)]
    public void DetectsTypePrefix(string subject, string type, bool expected)
    {
        Assert.Equal(expected, CommitTrailers.HasTypePrefix(subject, type));
    }

    [Theory]
    [InlineData("packages/server/src/domain/__tests__/a.test.ts", true)]
    [InlineData("__tests__/x.ts", true)]
    [InlineData("src/card.spec.js", true)]
    [InlineData("src/app.test.ts", true)]
    [InlineData("tests/foo/bar.cs", true)]
    [InlineData("packages/server/src/domain/GameOrchestrator.ts", false)]
    [InlineData("src/latest.ts", false)]
    [InlineData("contests/a.ts", false)]
    public void DefaultTestPatternsMatchTestFiles(string path, bool expected)
    {
        Assert.Equal(expected, PathPatterns.DefaultTests.Matches(path));
    }

    [Fact]
    public void CustomPatternsSupportDirectoryWildcards()
    {
        var patterns = new PathPatterns(["src/**/fixtures/*.json", "**/regression/**"]);
        Assert.True(patterns.Matches("src/a/b/fixtures/x.json"));
        Assert.True(patterns.Matches("src/fixtures/x.json"));
        Assert.False(patterns.Matches("src/fixtures/sub/x.json"));
        Assert.True(patterns.Matches("deep/regression/case.ts"));
    }

    private static Task<string> NoPatch(string path) => Task.FromResult(string.Empty);

    [Fact]
    public async Task FixWithTestHasRegressionTest()
    {
        var analysis = await DiffClassifier.AnalyzeAsync(
            CommitType.Fix,
            [new DiffEntry('M', "src/game.ts", null), new DiffEntry('A', "src/__tests__/game.test.ts", null)],
            PathPatterns.DefaultTests,
            MixedCheckOptions.Default,
            NoPatch);
        Assert.True(analysis.HasRegressionTest);
        Assert.Empty(analysis.MixedReasons);
    }

    [Fact]
    public async Task FixAddingManySourceFilesLooksMixed()
    {
        var analysis = await DiffClassifier.AnalyzeAsync(
            CommitType.Fix,
            [new DiffEntry('A', "src/feature1.ts", null), new DiffEntry('A', "src/feature2.ts", null), new DiffEntry('M', "src/game.ts", null)],
            PathPatterns.DefaultTests,
            MixedCheckOptions.Default,
            NoPatch);
        Assert.False(analysis.HasRegressionTest);
        Assert.Single(analysis.MixedReasons);
    }

    [Fact]
    public async Task FeatChangingExistingExpectationsLooksMixed()
    {
        var analysis = await DiffClassifier.AnalyzeAsync(
            CommitType.Feat,
            [new DiffEntry('M', "src/a.test.ts", null), new DiffEntry('A', "src/new-feature.ts", null)],
            PathPatterns.DefaultTests,
            MixedCheckOptions.Default,
            _ => Task.FromResult("--- a/src/a.test.ts\n+++ b/src/a.test.ts\n@@ -3 +3 @@\n-  expect(total).toBe(10);\n+  expect(total).toBe(12);\n"));
        Assert.Single(analysis.MixedReasons);
    }

    [Fact]
    public async Task FeatAddingNewTestsIsNotMixed()
    {
        var analysis = await DiffClassifier.AnalyzeAsync(
            CommitType.Feat,
            [new DiffEntry('M', "src/a.test.ts", null), new DiffEntry('A', "src/new-feature.ts", null)],
            PathPatterns.DefaultTests,
            MixedCheckOptions.Default,
            _ => Task.FromResult("@@ -10,0 +11,3 @@\n+it('new', () => {\n+  expect(feature()).toBe(1);\n+});\n"));
        Assert.Empty(analysis.MixedReasons);
    }

    [Fact]
    public async Task RefactorChangingExpectationsAndTestChangingSourceLookMixed()
    {
        var refactor = await DiffClassifier.AnalyzeAsync(
            CommitType.Refactor,
            [new DiffEntry('M', "src/a.spec.ts", null), new DiffEntry('M', "src/a.ts", null)],
            PathPatterns.DefaultTests,
            MixedCheckOptions.Default,
            _ => Task.FromResult("@@ -1 +1 @@\n+  assert.equal(x, 2);\n"));
        Assert.Single(refactor.MixedReasons);
        var test = await DiffClassifier.AnalyzeAsync(
            CommitType.Test,
            [new DiffEntry('M', "src/a.spec.ts", null), new DiffEntry('M', "src/a.ts", null)],
            PathPatterns.DefaultTests,
            MixedCheckOptions.Default,
            NoPatch);
        Assert.Single(test.MixedReasons);
    }

    [Fact]
    public void AuditFlagsPossibleFixAndBehaviorChange()
    {
        var before = new HashSet<string>(["inv-1", "inv-2"], StringComparer.Ordinal);
        var after = new HashSet<string>(["inv-2", "inv-3"], StringComparer.Ordinal);
        var feat = ClassificationAudit.CheckJudgmentChanges("c1", CommitType.Feat, before, after);
        Assert.Single(feat);
        Assert.Equal("possible_fix", feat[0].Kind);
        Assert.Equal(["inv-1"], feat[0].Judgments);
        var refactor = ClassificationAudit.CheckJudgmentChanges("c2", CommitType.Refactor, before, after);
        Assert.Equal(["possible_fix", "behavior_changed"], refactor.Select(f => f.Kind).ToList());
        var unchanged = ClassificationAudit.CheckJudgmentChanges("c3", CommitType.Refactor, before, before);
        Assert.Empty(unchanged);
        var fix = ClassificationAudit.CheckJudgmentChanges("c4", CommitType.Fix, before, before);
        Assert.Equal("fix_without_effect", Assert.Single(fix).Kind);
    }

    [Fact]
    public void SampleForReviewIsDeterministicAndOrderIndependent()
    {
        var commits = Enumerable.Range(0, 50).Select(i => $"commit-{i}").ToList();
        var a = ClassificationAudit.SampleForReview(commits, 5, "seed-1");
        var b = ClassificationAudit.SampleForReview(Enumerable.Reverse(commits), 5, "seed-1");
        var c = ClassificationAudit.SampleForReview(commits, 5, "seed-2");
        Assert.Equal(5, a.Count);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Empty(ClassificationAudit.SampleForReview(commits, 0, "x"));
    }

    [Fact]
    public void ParsesNameStatusWithRenames()
    {
        var entries = GitClient.ParseNameStatusZ("M\0src/a.ts\0R100\0old.ts\0new.ts\0A\0b c.ts\0");
        Assert.Equal(3, entries.Count);
        Assert.Equal(new DiffEntry('R', "new.ts", "old.ts"), entries[1]);
        Assert.Equal("b c.ts", entries[2].Path);
    }
}
