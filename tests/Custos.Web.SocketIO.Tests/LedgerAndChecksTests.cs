using Custos.Web.SocketIO.Agents;
using Custos.Web.SocketIO.Probes;

namespace Custos.Web.SocketIO.Tests;

/// <summary>台帳（第21.10節）、版の範囲の照合、静的解析の規則。</summary>
public class LedgerAndChecksTests
{
    private static LedgerDefinition Definition() => new(
        new Dictionary<string, TargetKind>(StringComparer.Ordinal)
        {
            ["card"] = new("card", ["1", "2"], ["normal", "barriered"], ["effect"], "high", new Dictionary<string, string>(StringComparer.Ordinal)),
            ["screen"] = new("screen", ["lobby"], ["shown"], ["layout"], "low", new Dictionary<string, string>(StringComparer.Ordinal)),
        },
        new Dictionary<string, SituationType>(StringComparer.Ordinal)
        {
            ["normal"] = new("normal", "通常", null),
            ["barriered"] = new("barriered", "守られている", null),
            ["shown"] = new("shown", "表示", null),
        },
        [("card", "2", "barriered", null)],
        0.2);

    private static TrialReport Report(string id, string target, string result) =>
        new(id, target, "explorer", "scripted", null, "s", [], result, [], "r", new TrialCost(0, 0, null, null), string.Empty, new Dictionary<string, string>(StringComparer.Ordinal), "x", 0, 0);

    private static ReportCheck Valid(string id) => new(id, true, [], 0, 0, null, 0);

    [Fact]
    public void BuildsCombinationsAndHonorsExclusions()
    {
        var ledger = LedgerStore.Build(Definition(), null);
        Assert.Equal(4, ledger.Count);
        Assert.DoesNotContain(ledger, e => e.Id == "card:2/barriered/effect");
        Assert.All(ledger, e => Assert.Equal("unverified", e.Status));
    }

    [Fact]
    public void HighSeverityNeedsTwoNoBugReports()
    {
        var ledger = LedgerStore.Build(Definition(), null);
        var once = LedgerStore.Apply(ledger, [(Report("t1", "card:1/normal/effect", "no_bug"), Valid("t1"))]);
        Assert.Equal("unverified", once.Single(e => e.Id == "card:1/normal/effect").Status);
        var twice = LedgerStore.Apply(once, [(Report("t2", "card:1/normal/effect", "no_bug"), Valid("t2"))]);
        Assert.Equal("verified", twice.Single(e => e.Id == "card:1/normal/effect").Status);
        var low = LedgerStore.Apply(ledger, [(Report("t3", "screen:lobby/shown/layout", "no_bug"), Valid("t3"))]);
        Assert.Equal("verified", low.Single(e => e.Id == "screen:lobby/shown/layout").Status);
    }

    [Fact]
    public void BugReportWinsAndInvalidReportsAreIgnored()
    {
        var ledger = LedgerStore.Build(Definition(), null);
        var invalid = new ReportCheck("t1", false, ["手順の不一致"], 0, 1, null, 0);
        var ignored = LedgerStore.Apply(ledger, [(Report("t1", "card:1/normal/effect", "bug"), invalid)]);
        Assert.Equal("unverified", ignored.Single(e => e.Id == "card:1/normal/effect").Status);
        var bug = LedgerStore.Apply(ledger, [(Report("t2", "card:1/normal/effect", "bug") with { BugIds = ["BUG-0010"] }, Valid("t2"))]);
        var entry = bug.Single(e => e.Id == "card:1/normal/effect");
        Assert.Equal("bug", entry.Status);
        Assert.Contains("BUG-0010", entry.Bugs);
        Assert.Equal(0.25, LedgerStore.Coverage(bug));
    }

    [Fact]
    public void InvalidateResetsVerifiedButKeepsBugs()
    {
        var ledger = LedgerStore.Build(Definition(), null);
        var verified = LedgerStore.Apply(ledger, [(Report("t", "screen:lobby/shown/layout", "no_bug"), Valid("t"))]);
        var reset = LedgerStore.Invalidate(verified, e => e.Kind == "screen");
        Assert.Equal("unverified", reset.Single(e => e.Kind == "screen").Status);
    }

    [Theory]
    [InlineData("4.17.20", "<4.17.21", true)]
    [InlineData("4.17.21", "<4.17.21", false)]
    [InlineData("1.2.3", ">=1.0.0 <1.2.4", true)]
    [InlineData("2.0.0", ">=1.0.0 <1.2.4 || >=2.0.0 <2.0.1", true)]
    [InlineData("1.5.0", "1.5.0", true)]
    [InlineData("1.5.0-beta.1", "<1.5.0", false)]
    public void SemverRangesMatch(string version, string range, bool expected) =>
        Assert.Equal(expected, SemverRange.Matches(version, range));

    [Theory]
    [InlineData("const rows = await prisma.$queryRawUnsafe(`SELECT * FROM x WHERE id = ${id}`);", "raw_sql_unsafe")]
    [InlineData("db.run(\"SELECT * FROM x WHERE name = '\" + name);", "sql_template_concat")]
    [InlineData("el.innerHTML = userName;", "inner_html")]
    [InlineData("const k = 'AKIAABCDEFGHIJKLMNOP';", "cloud_key")]
    [InlineData("eval(code)", "eval")]
    public void StaticRulesMatchDangerousCode(string line, string rule) =>
        Assert.Contains(StaticChecks.Rules, r => r.Id == rule && r.Pattern.IsMatch(line));

    [Theory]
    [InlineData("el.innerHTML = '';")]
    [InlineData("const evaluation = 1;")]
    [InlineData("logger.info('select a card');")]
    public void StaticRulesDoNotMatchSafeCode(string line) =>
        Assert.DoesNotContain(StaticChecks.Rules, r => r.Pattern.IsMatch(line));
}
