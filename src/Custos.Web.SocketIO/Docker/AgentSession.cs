using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Util;

namespace Custos.Web.SocketIO.Docker;

/// <summary>
/// コンテナ内の部品（custos-client、custos-browser、custos-ops）との RPC。
/// docker exec -i で部品を起動し、標準入出力の JSON Lines でやり取りする（docs/architecture.md 5.2）。
/// ネットワーク上の管理用の待ち受けは使わないので、利用者側のコンテナからこの経路には届かない。
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly Task _readLoop;
    private long _nextId;

    private AgentSession(string container, Process process)
    {
        Container = container;
        _process = process;
        _readLoop = Task.Run(ReadLoopAsync);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (_stderr)
                {
                    if (_stderr.Length < 100_000)
                    {
                        _stderr.AppendLine(e.Data);
                    }
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    /// <summary>コンテナの名前。</summary>
    public string Container { get; }

    /// <summary>通知（{"event":...}）を受けたときに呼ばれる。</summary>
    public event EventHandler<JsonObject>? Notified;

    /// <summary>標準エラー出力（部品のログ）。</summary>
    public string StandardError
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>コンテナの中で部品を起動する。</summary>
    public static async Task<AgentSession> StartAsync(DockerCli docker, string container, string executable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(docker);
        var startInfo = new ProcessStartInfo(DockerCli.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in new[] { "exec", "-i", container, executable, "serve" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in docker.Environment)
        {
            if (pair.Value != null)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(startInfo) ?? throw new CustosException($"{container} の部品を起動できませんでした");
        var session = new AgentSession(container, process);
        var pong = await session.CallAsync("ping", new JsonObject(), TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        if (pong?["pong"]?.GetValue<bool>() != true)
        {
            throw new CustosException($"{container} の部品が応答しません：{session.StandardError}");
        }

        return session;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message == null)
                {
                    continue;
                }

                if (message["event"] != null)
                {
                    Notified?.Invoke(this, message);
                    continue;
                }

                if (message["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id) && _pending.TryRemove(id, out var completion))
                {
                    if (message["error"] is JsonObject error)
                    {
                        completion.TrySetException(new CustosException($"{Container}: {error["message"]?.GetValue<string>()}"));
                    }
                    else
                    {
                        completion.TrySetResult(message["result"]?.DeepClone());
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new CustosException($"{Container} の部品が終了しました：{StandardError}"));
        }
    }

    /// <summary>呼び出す。</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject parameters, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var request = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters };
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(request.ToJsonString(WriteOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            return await completion.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"{Container} の {method} が時間内に終わりませんでした");
        }
    }

    /// <summary>呼び出し、結果をオブジェクトとして返す。</summary>
    public async Task<JsonObject> CallObjectAsync(string method, JsonObject parameters, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        return result as JsonObject ?? throw new CustosException($"{Container} の {method} の結果がオブジェクトではありません");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or IOException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
        }

        _process.Dispose();
        _writeLock.Dispose();
    }
}
