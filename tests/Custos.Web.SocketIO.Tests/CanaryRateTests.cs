using System.Text.Json.Nodes;
using Custos.Web.SocketIO.Calibration;

namespace Custos.Web.SocketIO.Tests;

/// <summary>カナリアの累積の検出率（設計書 第19.7節）。カナリアの表示・成熟度・リリース判定で同じ数え方をする。</summary>
public sealed class CanaryRateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "custos-canary-" + Guid.NewGuid().ToString("N"));

    public CanaryRateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string History(params string[] lines)
    {
        var path = Path.Combine(_dir, "canaries.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void MissingHistoryHasNoRate()
    {
        Assert.Null(CanaryRunner.CumulativeRate(Path.Combine(_dir, "none.jsonl")));
    }

    [Fact]
    public void ExcludedNotAppliedAndNotExercisedTrialsAreNotCounted()
    {
        var path = History(
            """{"name":"a","applied":true,"detected":true}""",
            """{"name":"b","applied":true,"exercised":true,"detected":false}""",
            // 異常が有効にならなかった
            """{"name":"c","applied":false,"detected":false}""",
            // 後からフレームワークの誤りが分かった（検出に見えたが当てにならない）
            """{"name":"d","applied":true,"detected":true,"excluded":true,"note":"…"}""",
            // 異常の箇所を通らなかった（見逃しではない）
            """{"name":"e","applied":true,"exercised":false,"detected":false}""",
            string.Empty);
        Assert.Equal(0.5, CanaryRunner.CumulativeRate(path));
    }

    [Fact]
    public void OnlyUncountedTrialsHaveNoRate()
    {
        var path = History("""{"name":"e","applied":true,"exercised":false,"detected":false}""");
        Assert.Null(CanaryRunner.CumulativeRate(path));
    }

    [Fact]
    public void UnknownExercisedIsCounted()
    {
        // 異常の定義が通った回数を数えていない（hits が null）ものは、従来どおり数える
        Assert.True(CanaryRunner.Counts(JsonNode.Parse("""{"name":"p","applied":true,"detected":false}""") ?? new JsonObject()));
        Assert.False(CanaryRunner.Counts(JsonNode.Parse("""{"name":"p","applied":true,"exercised":false,"detected":false}""") ?? new JsonObject()));
    }
}

/// <summary>校正用バグの品質チェックの結果の保存（第19.5節）。一部だけ確かめ直しても、ほかの結果を消さない。</summary>
public sealed class SeedChecksStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "custos-seeds-" + Guid.NewGuid().ToString("N"), "seeds-check.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir != null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResultsAreMergedById()
    {
        SeedStore.SaveChecks(_path, [new SeedCheckResult("seed-001", true, true, false, "R1", "V2", "最初"), new SeedCheckResult("seed-002", true, false, false, "R1", "-", "最初")]);
        var merged = SeedStore.SaveChecks(_path, [new SeedCheckResult("seed-002", true, true, false, "R2", "V2", "確かめ直し"), new SeedCheckResult("seed-003", true, true, false, "R1", "V1", "新しい")]);
        Assert.Equal(["seed-001", "seed-002", "seed-003"], merged.Select(c => c.Id));
        var loaded = SeedStore.LoadChecks(_path).ToDictionary(c => c.Id);
        Assert.Equal("R1V2", loaded["seed-001"].Layer);
        Assert.Equal("R2V2", loaded["seed-002"].Layer);
        Assert.Equal("確かめ直し", loaded["seed-002"].Detail);
    }

    [Fact]
    public void MissingFileIsEmpty()
    {
        Assert.Empty(SeedStore.LoadChecks(_path));
    }
}
