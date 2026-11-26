using System.Diagnostics;
using System.Text;

namespace Custos.Core.Util;

/// <summary>外部コマンドの実行結果。</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration, bool TimedOut)
{
    /// <summary>終了コードが 0 で、時間切れでもない。</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>失敗していたら、出力を含めた例外にする。</summary>
    public ProcessResult EnsureSuccess(string what)
    {
        if (!Succeeded)
        {
            var reason = TimedOut ? "時間切れ" : $"終了コード {ExitCode}";
            throw new CustosException($"{what} に失敗しました（{reason}）。\n標準エラー出力:\n{Truncate(StandardError)}\n標準出力:\n{Truncate(StandardOutput)}");
        }

        return this;
    }

    private static string Truncate(string text) => text.Length <= 4000 ? text : text[..2000] + "\n…\n" + text[^2000..];
}

/// <summary>外部コマンドの実行（docker、git、ssh-keygen など）。</summary>
public static class ProcessRunner
{
    /// <summary>コマンドを実行し、終了を待つ。</summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput != null,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (workingDirectory != null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (environment != null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value == null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (stdout)
                {
                    stdout.Append(e.Data).Append('\n');
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (stderr)
                {
                    stderr.Append(e.Data).Append('\n');
                }
            }
        };

        if (!process.Start())
        {
            throw new CustosException($"{fileName} を起動できませんでした");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (standardInput != null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            timeoutSource.CancelAfter(limit);
        }

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // すでに終了している
            }
        }

        process.WaitForExit();
        stopwatch.Stop();
        string outText;
        string errText;
        lock (stdout)
        {
            outText = stdout.ToString();
        }

        lock (stderr)
        {
            errText = stderr.ToString();
        }

        return new ProcessResult(timedOut ? -1 : process.ExitCode, outText, errText, stopwatch.Elapsed, timedOut);
    }
}
