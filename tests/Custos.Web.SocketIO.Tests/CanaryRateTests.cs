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
