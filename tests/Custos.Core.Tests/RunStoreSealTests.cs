using Custos.Core.Results;

namespace Custos.Core.Tests;

/// <summary>コマンドが例外で終わったときの、封をしていない実行の扱い（設計書 第12.6節「結果を改ざんできなくする」）。</summary>
public sealed class RunStoreSealTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "custos-runs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_dir))
        {
            return;
        }

        // 封をしたファイルは読み取り専用なので、書けるように戻してから消す
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void OpenRunsAreSealedWithReasonAndStayVerifiable()
    {
        var store = new RunStore(_dir);
        var done = store.Begin("scenario", null, null);
        done.Seal("passed");
        var open = store.Begin("calibrate", null, null);
        open.WriteText("partial.txt", "途中まで");

        var sealedIds = RunStore.SealOpenRuns("error", "結果のファイルはすでにあります");

        Assert.Contains(open.Id, sealedIds);
        Assert.DoesNotContain(done.Id, sealedIds);
        Assert.True(open.IsSealed);
        var listed = store.List().Single(r => r.Id == open.Id);
        Assert.Equal("error", listed.Status);
        Assert.NotNull(listed.FinishedAt);
        Assert.Single(Directory.GetFiles(open.Dir, "error-*.txt"));
        Assert.Empty(store.Verify());

        // 2回目は何もしない（封をしたものは対象から外れる）
        Assert.DoesNotContain(open.Id, RunStore.SealOpenRuns("error", "もう一度"));
    }
}
