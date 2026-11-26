namespace Custos.Agent.Common;

/// <summary>コンテナ内の部品の入口の共通処理。</summary>
public static class AgentHost
{
    /// <summary>
    /// 「serve」なら標準入出力で指示を受ける。「idle」なら何もせずに待つ（コンテナを生かしておくため）。
    /// </summary>
    public static async Task<int> RunAsync(string[] args, Action<RpcServer> register, Func<string[], CancellationToken, Task<int>>? other = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(register);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        var mode = args.Length > 0 ? args[0] : "idle";
        switch (mode)
        {
            case "idle":
                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                return 0;
            case "serve":
                {
                    using var server = new RpcServer();
                    Probes.Register(server);
                    register(server);
                    await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cts.Token).ConfigureAwait(false);
                    return 0;
                }

            default:
                if (other != null)
                {
                    return await other(args, cts.Token).ConfigureAwait(false);
                }

                await Console.Error.WriteLineAsync($"知らない動き方です：{mode}（idle か serve）").ConfigureAwait(false);
                return 2;
        }
    }
}
