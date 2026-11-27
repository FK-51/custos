using Custos.Cli;
using Custos.Cli.Commands;
using Custos.Core.Util;

// コマンド custos（指示書 付録A）。管理側のホストで動く。
// 値を取らない指定（CliArgs.Flag で読むもの）。ここにない名前は、次の引数を値として読む
var flags = new HashSet<string>(StringComparer.Ordinal)
{
    "json", "rebuild", "overwrite", "egress", "all", "raw", "help", "sign", "all-breakage", "keep", "no-build", "strict", "dry-run",
    "no-reset", "no-register", "register", "allow-used", "phases", "share",
};
var commands = CommandTable.Build();
if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    CommandTable.PrintHelp(commands);
    return 0;
}

CliArgs parsed;
try
{
    parsed = new CliArgs(args, flags);
}
catch (CustosException e)
{
    await Console.Error.WriteLineAsync("エラー：" + e.Message).ConfigureAwait(false);
    return 2;
}

var (command, consumed) = CommandTable.Find(commands, parsed.Positional);
if (command == null)
{
    await Console.Error.WriteLineAsync($"知らないコマンドです：{string.Join(' ', parsed.Positional)}（custos help で一覧）").ConfigureAwait(false);
    return 2;
}

parsed.Positional.RemoveRange(0, consumed);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var context = new CliContext(parsed);
try
{
    var code = await command.Handler(context, parsed, cts.Token).ConfigureAwait(false);
    foreach (var unused in parsed.Unused().Where(u => u is not ("verify-repo" or "data-dir" or "app-repo" or "framework-repo" or "docker-host" or "json" or "config-dir")))
    {
        await Console.Error.WriteLineAsync($"注意：--{unused} は使われませんでした（綴りを確かめてください）").ConfigureAwait(false);
    }

    return code;
}
catch (CustosException e)
{
    await Console.Error.WriteLineAsync("エラー：" + e.Message).ConfigureAwait(false);
    return 1;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("中断しました").ConfigureAwait(false);
    return 130;
}
