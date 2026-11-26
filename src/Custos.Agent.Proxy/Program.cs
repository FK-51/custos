using System.Diagnostics;
using System.Globalization;
using System.Net;
using Custos.Agent.Common;
using Custos.Agent.Proxy;

// custos-proxy run --listen 0.0.0.0:13000 --upstream app:3001 --records /records --control /control
//              [--block /__verify/] [--socketio-path /socket.io/] [--netem "delay 30ms 10ms loss 0.5% rate 20mbit"]
//              [--break-add-header]
return await AgentHost.RunAsync(args, _ => { }, RunAsync).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
{
    if (args.Length > 0 && args[0] == "egress")
    {
        // custos-proxy egress --listen 0.0.0.0:3128 --allow api.anthropic.com:443[,…]
        var listenText = "0.0.0.0:3128";
        var allow = new List<string>();
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--listen")
            {
                listenText = args[i + 1];
            }
            else if (args[i] == "--allow")
            {
                allow.AddRange(args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        var endpoint = new IPEndPoint(IPAddress.Parse(listenText[..listenText.LastIndexOf(':')]), int.Parse(listenText[(listenText.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture));
        await new EgressProxy(endpoint, allow).RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    if (args.Length == 0 || args[0] != "run")
    {
        await Console.Error.WriteLineAsync("使い方: custos-proxy run --listen <addr:port> --upstream <host:port> --records <dir> [--control <dir>]").ConfigureAwait(false);
        return 2;
    }

    var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var name = args[i][2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        if (!options.TryGetValue(name, out var list))
        {
            list = [];
            options[name] = list;
        }

        list.Add(value);
    }

    string Get(string name, string fallback) => options.TryGetValue(name, out var v) ? v[^1] : fallback;

    var listen = Get("listen", "0.0.0.0:13000");
    var upstream = Get("upstream", "app:3001");
    var listenPort = int.Parse(listen[(listen.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    var listenAddress = IPAddress.Parse(listen[..listen.LastIndexOf(':')]);
    var upstreamHost = upstream[..upstream.LastIndexOf(':')];
    var upstreamPort = int.Parse(upstream[(upstream.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    var blocks = options.TryGetValue("block", out var b) ? b : ["/__verify/"];
    var breakHeader = options.ContainsKey("break-add-header") || Environment.GetEnvironmentVariable("CUSTOS_BREAK_ADD_HEADER") == "1";
    if (options.TryGetValue("netem", out var netem))
    {
        ApplyNetem(netem[^1]);
    }

    var proxyOptions = new ProxyOptions(
        new IPEndPoint(listenAddress, listenPort),
        upstreamHost,
        upstreamPort,
        Get("records", "/records"),
        options.ContainsKey("control") ? Get("control", "/control") : null,
        blocks,
        Get("socketio-path", "/socket.io/"),
        breakHeader);
    using var server = new ProxyServer(proxyOptions);
    await server.RunAsync(cancellationToken).ConfigureAwait(false);
    return 0;
}

// 仮想ネットワーク上の通信速度、遅延、パケットロス（設計書 第16.5節）。NET_ADMIN の権限が要る。
static void ApplyNetem(string spec)
{
    foreach (var dir in Directory.EnumerateDirectories("/sys/class/net"))
    {
        var device = Path.GetFileName(dir);
        if (device == "lo")
        {
            continue;
        }

        var startInfo = new ProcessStartInfo("tc") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var part in new[] { "qdisc", "replace", "dev", device, "root", "netem" }.Concat(spec.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            startInfo.ArgumentList.Add(part);
        }

        try
        {
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
            Console.Error.WriteLine($"netem {device}: {spec} (exit {process?.ExitCode})");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Console.Error.WriteLine($"netem failed: {e.Message}");
        }
    }
}
