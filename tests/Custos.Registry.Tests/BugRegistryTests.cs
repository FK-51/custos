using Custos.Core.Util;
using Custos.Registry.Bugs;
using Microsoft.Data.Sqlite;

namespace Custos.Registry.Tests;

public class BugRegistryTests
{
    [Fact]
    public void AddAssignsSequentialIds()
    {
        using var temp = new TempDir();
        var intake = BugRegistry.OpenIntake(temp.Paths().RegistryDb);
        var first = intake.Add(new NewBug("setup", "implementation", "一つ目"));
        var second = intake.Add(new NewBug("human", "implementation", "二つ目"));
        Assert.Equal("BUG-0001", first);
        Assert.Equal("BUG-0002", second);
    }

    [Fact]
    public void ExplicitIdIsAcceptedAndNextIdContinuesAfterIt()
    {
        using var temp = new TempDir();
        var admin = BugRegistry.OpenAdmin(temp.Paths().RegistryDb);
        Assert.Equal("BUG-0142", admin.Add(new NewBug("dev_agent", "implementation", "指定の ID", Id: "BUG-0142")));
        Assert.Equal("BUG-0143", admin.Add(new NewBug("dev_agent", "implementation", "次の ID")));
        Assert.Throws<CustosException>(() => admin.Add(new NewBug("dev_agent", "implementation", "重複", Id: "BUG-0142")));
    }

    [Theory]
    [InlineData("loop:claude-code/model-a@2026-11", "loop", true)]
    [InlineData("dev_agent", "implementation", true)]
    [InlineData("human", "staging", true)]
    [InlineData("production", "production", true)]
    [InlineData("setup", "implementation", true)]
    [InlineData("loop:", "loop", false)]
    [InlineData("robot", "implementation", false)]
    [InlineData("setup", "setup", false)]
    [InlineData("human", "qa", false)]
    public void ValidatesFoundByAndStage(string foundBy, string stage, bool ok)
    {
        var problems = BugValues.Validate(new NewBug(foundBy, stage, "説明"));
        Assert.Equal(ok, problems.Count == 0);
    }

    [Fact]
    public void RejectsInvalidValues()
    {
        using var temp = new TempDir();
        var intake = BugRegistry.OpenIntake(temp.Paths().RegistryDb);
        Assert.Throws<CustosException>(() => intake.Add(new NewBug("setup", "implementation", "説明", Severity: "huge")));
        Assert.Throws<CustosException>(() => intake.Add(new NewBug("setup", "implementation", "説明", Id: "BUG-1")));
        Assert.Throws<CustosException>(() => intake.Add(new NewBug("setup", "implementation", "二行\nの説明")));
        Assert.Throws<CustosException>(() => intake.Add(new NewBug("setup", "implementation", " ")));
    }

    [Fact]
    public void IntakeInterfaceOnlyAllowsAppending()
    {
        // 第20.7節：開発AIと修正AIには追記だけを許し、読ませない
        var methods = typeof(IBugIntake).GetMethods().Select(m => m.Name).ToList();
        Assert.Equal(["Add"], methods);
        Assert.Empty(typeof(IBugIntake).GetProperties());
    }

    [Fact]
    public void AdminUpdateRecordsHistoryOnlyForChanges()
    {
        using var temp = new TempDir();
        var admin = BugRegistry.OpenAdmin(temp.Paths().RegistryDb);
        var id = admin.Add(new NewBug("setup", "implementation", "説明", Severity: "high"));
        admin.Update(id, new BugUpdate(IntroducedIn: "abc123", Reach: 3, Visibility: "medium", Category: "calc", Revivable: true), "bisect", "二分探索");
        admin.Update(id, new BugUpdate(IntroducedIn: "abc123"), "bisect", "同じ値");
        var bug = admin.Get(id);
        Assert.NotNull(bug);
        Assert.Equal("abc123", bug.IntroducedIn);
        Assert.Equal(3, bug.Reach);
        Assert.Equal("medium", bug.Visibility);
        Assert.Equal("calc", bug.Category);
        Assert.True(bug.Revivable);
        var history = admin.History(id);
        Assert.Equal(["introduced_in", "reach", "visibility", "category", "revivable"], history.Select(h => h.Field).ToList());
        Assert.All(history, h => Assert.Equal("bisect", h.Actor));
        Assert.Throws<CustosException>(() => admin.Update(id, new BugUpdate(Visibility: "extreme"), "x"));
        Assert.Throws<CustosException>(() => admin.Update("BUG-9999", new BugUpdate(FixedIn: "x"), "x"));
    }

    [Fact]
    public void DatabaseRefusesDeletionAndChangesToFixedColumns()
    {
        using var temp = new TempDir();
        var path = temp.Paths().RegistryDb;
        var admin = BugRegistry.OpenAdmin(path);
        var id = admin.Add(new NewBug("setup", "implementation", "説明"));
        admin.Update(id, new BugUpdate(FixedIn: "deadbeef"), "test");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();

        void Expect(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }

        Expect("DELETE FROM bugs");
        Expect("UPDATE bugs SET found_by = 'human'");
        Expect("UPDATE bugs SET found_stage = 'loop'");
        Expect("DELETE FROM bug_changes");
        Expect("UPDATE bug_changes SET actor = 'x'");
        Expect("UPDATE bug_details SET title = 'x'");
        Assert.NotNull(admin.Get(id));
    }

    [Fact]
    public void MarkUsedInAppendsWithoutDuplicates()
    {
        using var temp = new TempDir();
        var admin = BugRegistry.OpenAdmin(temp.Paths().RegistryDb);
        var id = admin.Add(new NewBug("human", "production", "本番の不具合"));
        admin.MarkUsedIn(id, "cal-001", "calibration");
        admin.MarkUsedIn(id, "cal-002", "calibration");
        admin.MarkUsedIn(id, "cal-001", "calibration");
        Assert.Equal("cal-001,cal-002", admin.Get(id)?.UsedIn);
        Assert.Equal(2, admin.History(id).Count(h => h.Field == "used_in"));
    }

    [Fact]
    public void EnsureExistsCreatesOnlyWhenMissing()
    {
        using var temp = new TempDir();
        var admin = BugRegistry.OpenAdmin(temp.Paths().RegistryDb);
        var template = new NewBug("dev_agent", "implementation", "修正の対象");
        var (first, created) = admin.EnsureExists("BUG-0142", template);
        var (again, createdAgain) = admin.EnsureExists("BUG-0142", template);
        var (fresh, createdFresh) = admin.EnsureExists(null, template);
        Assert.Equal("BUG-0142", first);
        Assert.True(created);
        Assert.Equal("BUG-0142", again);
        Assert.False(createdAgain);
        Assert.Equal("BUG-0143", fresh);
        Assert.True(createdFresh);
    }

    [Fact]
    public void ExportWritesJsonLinesAndMarkdownInIdOrder()
    {
        using var temp = new TempDir();
        var paths = temp.Paths();
        var admin = BugRegistry.OpenAdmin(paths.RegistryDb);
        admin.Add(new NewBug("setup", "implementation", "二つ目の説明", Id: "BUG-0010"));
        admin.Add(new NewBug("setup", "implementation", "一つ目の説明 | 縦棒あり", Id: "BUG-0002", Severity: "high"));
        admin.Link("BUG-0002", "judgment", "run-000001/inv-1");
        var export = RegistryExporter.Export(admin, paths.ExportDir);
        Assert.Equal(2, export.Count);
        var lines = File.ReadAllLines(export.JsonLinesPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"id\":\"BUG-0002\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("run-000001/inv-1", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"id\":\"BUG-0010\"", lines[1], StringComparison.Ordinal);
        var markdown = File.ReadAllText(export.MarkdownPath);
        Assert.Contains("| BUG-0002 | 一つ目の説明 \\| 縦棒あり |", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("loop:explorer-a@1", "loop", "explorer-a@1", CalibrationUse.Excluded)]
    [InlineData("loop:explorer-b@1", "loop", "explorer-a@1", CalibrationUse.UsableBiasedTowardAi)]
    [InlineData("dev_agent", "implementation", "explorer-a@1", CalibrationUse.UsableBiasedShallow)]
    [InlineData("human", "implementation", "explorer-a@1", CalibrationUse.UsableBiasedShallow)]
    [InlineData("setup", "implementation", "explorer-a@1", CalibrationUse.UsableBiasedShallow)]
    [InlineData("production", "production", "explorer-a@1", CalibrationUse.MostValuable)]
    [InlineData("human", "production", "explorer-a@1", CalibrationUse.MostValuable)]
    public void CalibrationUseFollowsFindingPath(string foundBy, string stage, string explorer, CalibrationUse expected)
    {
        Assert.Equal(expected, CalibrationUsePolicy.Decide(foundBy, stage, explorer).Use);
    }

    [Fact]
    public void LoopFindingIsLinkedToJudgmentReportAndFix()
    {
        using var temp = new TempDir();
        var admin = BugRegistry.OpenAdmin(temp.Paths().RegistryDb);
        var id = BugIntakeSources.RecordLoopFinding(admin, new LoopFinding(
            "claude-code/model-a@2026-11",
            "相手の手番に着手できる",
            JudgmentId: "run-000003/phase-check-7",
            ReportId: "report-42",
            ReplayRef: "replays/42.json",
            Severity: "high",
            FixCommit: "cafebabe"));
        var bug = admin.Get(id);
        Assert.NotNull(bug);
        Assert.Equal("loop:claude-code/model-a@2026-11", bug.FoundBy);
        Assert.Equal("loop", bug.FoundStage);
        Assert.Equal("cafebabe", bug.FixedIn);
        Assert.Equal(["judgment", "report", "replay", "fix_commit"], admin.Links(id).Select(l => l.Kind).ToList());
    }

    [Fact]
    public void ImportsExternalReports()
    {
        using var temp = new TempDir();
        var admin = BugRegistry.OpenAdmin(temp.Paths().RegistryDb);
        var ids = BugIntakeSources.ImportReports(admin, """
            [
              {"title": "本番で対局が止まった", "found_by": "production", "found_stage": "production", "severity": "high", "external_ref": "github#12"},
              {"title": "表示が崩れる", "found_by": "human", "found_stage": "staging", "fix_commit": "abc"}
            ]
            """);
        Assert.Equal(["BUG-0001", "BUG-0002"], ids);
        Assert.Equal("production", admin.Get("BUG-0001")?.FoundBy);
        Assert.Equal("abc", admin.Get("BUG-0002")?.FixedIn);
        Assert.Throws<CustosException>(() => BugIntakeSources.ImportReports(admin, """[{"title": "x", "found_by": "loop:a", "found_stage": "loop"}]"""));
        Assert.Throws<CustosException>(() => BugIntakeSources.ImportReports(admin, "{not json"));
    }

    [Fact]
    public void TestFailuresExcludeTrivialKinds()
    {
        using var temp = new TempDir();
        var intake = BugRegistry.OpenIntake(temp.Paths().RegistryDb);
        Assert.Null(BugIntakeSources.RecordTestFailure(intake, new TestFailureEvent("build", "compile_error")));
        Assert.Null(BugIntakeSources.RecordTestFailure(intake, new TestFailureEvent("types", "type_error")));
        var id = BugIntakeSources.RecordTestFailure(intake, new TestFailureEvent("deals two cards", "assertion", Diff: "- a\n+ b"));
        Assert.Equal("BUG-0001", id);
        var bug = BugRegistry.OpenAdmin(temp.Paths().RegistryDb).Get("BUG-0001");
        Assert.NotNull(bug);
        Assert.Equal("dev_agent", bug.FoundBy);
        Assert.Contains("deals two cards", bug.Detail, StringComparison.Ordinal);
    }
}
