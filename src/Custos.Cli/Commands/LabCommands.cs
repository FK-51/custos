using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Core.Util;
using Custos.SocketIO.Wire;
using Custos.Web.SocketIO.Lab;
using Custos.Web.SocketIO.State;

namespace Custos.Cli.Commands;

/// <summary>段階0の土台のコマンド（指示書 付録A）。</summary>
public static class LabCommands
{
    /// <summary>custos config check。</summary>
    public static Task<int> ConfigCheckAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var result = context.ConfigResult;
        foreach (var error in result.FormatErrors)
        {
            CliContext.Out("書式の誤り：" + error);
        }

        foreach (var error in result.ReferenceErrors)
        {
            CliContext.Out("参照の誤り：" + error);
        }

        foreach (var warning in result.Warnings)
        {
            CliContext.Out("警告：" + warning);
        }

        if (result.Ok && result.Config != null)
        {
            CliContext.Out($"設定は書式に合っています（{result.Config.FileHashes.Count} ファイル、ハッシュ {result.Config.Hash[..16]}）");
            return Task.FromResult(0);
        }

        CliContext.Out($"設定に誤りがあります（書式 {result.FormatErrors.Count} 件、参照 {result.ReferenceErrors.Count} 件）");
        return Task.FromResult(1);
    }

    /// <summary>custos up。</summary>
    public static async Task<int> UpAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var breakage = args.All("break").ToHashSet(StringComparer.Ordinal);
        foreach (var name in breakage)
        {
            if (!ComposeWriter.Breaks.All.Contains(name))
            {
                throw new CustosException($"知らない壊し方です：{name}（{string.Join(", ", ComposeWriter.Breaks.All)}）");
            }
        }

        var state = await context.Lab.UpAsync(new UpOptions(args.Get("build") ?? "production", breakage, args.Get("profile"), args.Flag("rebuild"), args.Flag("egress")), cancellationToken).ConfigureAwait(false);
        CliContext.Out($"検証環境を起動しました：{state.Project}（セッション {state.SessionId}）");
        CliContext.Out($"  アプリ：{state.AppImage}");
        CliContext.Out($"  利用者側から見た URL：{context.Lab.PublicUrl}");
        CliContext.Out($"  通信の記録：{state.RecordsDir}");
        return 0;
    }

    /// <summary>custos down。</summary>
    public static async Task<int> DownAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await context.Lab.DownAsync(cancellationToken).ConfigureAwait(false);
        CliContext.Out("検証環境を片付けました");
        return 0;
    }

    /// <summary>custos status。</summary>
    public static async Task<int> StatusAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var lab = context.Lab;
        if (!lab.HasState)
        {
            CliContext.Out("検証環境は起動していません");
            return 1;
        }

        var state = lab.State;
        var containers = new JsonObject();
        foreach (var name in new[] { Containers.App(state.Project), Containers.Edge(state.Project), Containers.Ops(state.Project), Containers.Client(state.Project, 1), Containers.Attacker(state.Project, 1), Containers.Browser(state.Project, 1), Containers.Sandbox(state.Project) })
        {
            containers[name] = await lab.ContainerStateAsync(name, cancellationToken).ConfigureAwait(false);
        }

        var activity = lab.Records.Activity();
        CliContext.OutJson(new JsonObject
        {
            ["project"] = state.Project,
            ["session"] = state.SessionId,
            ["build"] = state.BuildKind,
            ["app_image"] = state.AppImage,
            ["breakage"] = new JsonArray(state.Breakage.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray()),
            ["profile"] = state.Profile,
            ["records"] = state.RecordsDir,
            ["proxy"] = new JsonObject
            {
                ["last_activity"] = activity.LastActivity.ToString("O", CultureInfo.InvariantCulture),
                ["last_seq"] = activity.LastSeq,
                ["open_connections"] = activity.OpenConnections,
                ["open_websockets"] = activity.OpenWebSockets,
            },
            ["containers"] = containers,
        });
        return 0;
    }

    /// <summary>custos snapshot save。</summary>
    public static async Task<int> SnapshotSaveAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var name = args.Arg(0, "スナップショットの名前");
        var settle = await context.Lab.SettleAsync(TimeSpan.FromMilliseconds(args.Number("quiet-ms", 500)), TimeSpan.FromMilliseconds(args.Number("timeout-ms", 20000)), cancellationToken).ConfigureAwait(false);
        if (!settle.Settled)
        {
            throw new CustosException("通信が止まらないので、スナップショットを取れません：" + settle.Detail);
        }

        var info = await context.Lab.SnapshotSaveAsync(name, args.Flag("overwrite"), cancellationToken).ConfigureAwait(false);
        CliContext.Out($"スナップショット {info.Name} を取りました：{string.Join(", ", info.Tables.Select(t => $"{t.Key}={t.Value}"))}");
        return 0;
    }

    /// <summary>custos snapshot list。</summary>
    public static Task<int> SnapshotListAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var file in Directory.EnumerateFiles(context.Workspace.SnapshotsDir, "*.json").Order(StringComparer.Ordinal))
        {
            var info = Json.Deserialize<SnapshotInfo>(File.ReadAllText(file));
            CliContext.Out($"{info.Name}\t{info.CreatedAt:yyyy-MM-dd HH:mm:ss}\t{string.Join(", ", info.Tables.Select(t => $"{t.Key}={t.Value}"))}");
        }

        return Task.FromResult(0);
    }

    /// <summary>custos reset。</summary>
    public static async Task<int> ResetAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var result = await context.Lab.ResetAsync(args.Arg(0, "スナップショットの名前"), skipDbReplace: false, cancellationToken).ConfigureAwait(false);
        CliContext.OutJson(result);
        return result["matches"]?.GetValue<bool>() == true ? 0 : 1;
    }

    /// <summary>custos settle。</summary>
    public static async Task<int> SettleAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var result = await context.Lab.SettleAsync(TimeSpan.FromMilliseconds(args.Number("quiet-ms", 500)), TimeSpan.FromMilliseconds(args.Number("timeout-ms", 15000)), cancellationToken).ConfigureAwait(false);
        CliContext.Out($"{(result.Settled ? "止まりました" : "止まりませんでした")}（{result.WaitedMs:0} ms）：{result.Detail}");
        return result.Settled ? 0 : 1;
    }

    /// <summary>custos state get。</summary>
    public static async Task<int> StateGetAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var name = args.Arg(0, "状態のキー");
        if (!context.Config.State.Keys.TryGetValue(name, out var key))
        {
            throw new CustosException($"state.yaml にないキーです：{name}");
        }

        var reader = new StateReader(context.Lab);
        var browser = key.Source is DomSource ? await context.Lab.BrowserAsync(1, false, cancellationToken).ConfigureAwait(false) : null;
        var results = await reader.ReadAsync([key], browser, args.Get("page") ?? "main", cancellationToken).ConfigureAwait(false);
        CliContext.Out(StateReader.Format(results));
        return results.All(r => r.Ok && r.TypeErrors.Count == 0) ? 0 : 1;
    }

    /// <summary>custos state dump。</summary>
    public static async Task<int> StateDumpAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reader = new StateReader(context.Lab);
        var keys = context.Config.State.Keys.Values.Where(k => k.Source is not DomSource).ToList();
        var results = await reader.ReadAsync(keys, null, "main", cancellationToken).ConfigureAwait(false);
        CliContext.Out(StateReader.Format(results));
        return 0;
    }

    /// <summary>custos record show。</summary>
    public static Task<int> RecordShowAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var store = context.Lab.Records;
        var since = args.Number("since", 0);
        var eventName = args.Get("event");
        var all = args.Flag("all");
        var raw = args.Flag("raw");
        foreach (var entry in store.Read(since))
        {
            if (!all && !(entry.Kind == "eio" && entry.Sio is not null and not "pending"))
            {
                continue;
            }

            if (eventName != null && entry.EventName != eventName)
            {
                continue;
            }

            if (raw)
            {
                CliContext.Out(RecordJson.Serialize(entry));
                continue;
            }

            var args2 = entry.Args?.ToJsonString() ?? string.Empty;
            if (args2.Length > 300)
            {
                args2 = args2[..300] + "…";
            }

            var what = entry.Kind switch
            {
                "eio" => $"{entry.Sio} {entry.EventName} {args2}{(entry.Ack is { } ack ? $" ack={ack}" : string.Empty)}{(entry.Valid == false ? " [invalid: " + entry.Error + "]" : string.Empty)}",
                "http" => $"{entry.Method} {entry.Target} -> {entry.Status}{(entry.Blocked == true ? " [blocked]" : string.Empty)}",
                _ => $"{entry.Kind} {entry.Event} {entry.Error}",
            };
            CliContext.Out($"{entry.Seq,6} {entry.Ts:HH:mm:ss.fff} c{entry.Conn} {entry.Client,-15} {entry.Sid ?? "-",-22} {entry.Transport ?? "-",-9} {entry.Dir ?? "-",-3} {what}");
        }

        return Task.FromResult(0);
    }

    /// <summary>custos logs。</summary>
    public static async Task<int> LogsAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        CliContext.Out(await context.Lab.AppLogsAsync(null, cancellationToken).ConfigureAwait(false));
        return 0;
    }

    /// <summary>custos call。</summary>
    public static async Task<int> CallAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        var target = args.Arg(0, "部品（client、attacker、browser、ops）");
        var method = args.Arg(1, "method");
        var parameters = args.Positional.Count > 2 ? JsonNode.Parse(args.Positional[2]) as JsonObject ?? [] : [];
        var session = target switch
        {
            "client" => await context.Lab.ClientAsync(1, cancellationToken).ConfigureAwait(false),
            "attacker" => await context.Lab.AttackerAsync(1, cancellationToken).ConfigureAwait(false),
            "browser" => await context.Lab.BrowserAsync(1, false, cancellationToken).ConfigureAwait(false),
            "ops" => await context.Lab.OpsAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new CustosException($"知らない部品です：{target}"),
        };
        CliContext.OutJson(await session.CallAsync(method, parameters, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false));
        return 0;
    }

    /// <summary>custos results list。</summary>
    public static Task<int> ResultsListAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var run in context.Results.List())
        {
            CliContext.Out($"{run.Id}\t{run.Status}\t{run.StartedAt:yyyy-MM-dd HH:mm:ss}\tframework {run.FrameworkVersion}\tconfig {run.ConfigHash?[..12]}\tjudge {run.JudgeHash[..12]}");
        }

        return Task.FromResult(0);
    }

    /// <summary>custos results verify。</summary>
    public static Task<int> ResultsVerifyAsync(CliContext context, CliArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problems = context.Results.Verify();
        if (problems.Count == 0)
        {
            CliContext.Out("結果の連鎖とハッシュは正しく、改ざんは見つかりませんでした");
            return Task.FromResult(0);
        }

        foreach (var problem in problems)
        {
            CliContext.Out("問題：" + problem);
        }

        return Task.FromResult(1);
    }

    /// <summary>設定の型（使わない警告を避けるため）。</summary>
    internal static StateKey? FindKey(ConfigSet config, string name) => config.State.Keys.GetValueOrDefault(name);
}
