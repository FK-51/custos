using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Custos.Agent.Common;

namespace Custos.Agent.Client;

/// <summary>
/// 負荷を出すボット（設計書 第13.3節、第26.6節）。LLM を使わない決定的なスクリプトで、複数の対局を同時に最後まで回す。
/// 着手の反映（手を送ってから、相手と自分に結果が届くまで）の時間、エラー、送信の遅れ、負荷側の CPU 時間を返す。
/// </summary>
public static class LoadRunner
{
    private static readonly string[] Roles = ["a", "b"];

    /// <summary>実行する。</summary>
    public static async Task<JsonNode?> RunAsync(JsonObject p, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(p);
        var url = new Uri(Params.Str(p, "url"));
        var games = (int)Params.OptLong(p, "games", 1);
        var concurrency = Math.Max(1, (int)Params.OptLong(p, "concurrency", 1));
        var seed = (int)Params.OptLong(p, "seed", 1);
        var transport = Params.OptStr(p, "transport") ?? "polling";
        var socketPath = new SocketPath(Params.OptStr(p, "path") ?? "/socket.io/");
        var gameTimeout = TimeSpan.FromMilliseconds(Params.OptLong(p, "game_timeout_ms", 120_000));
        var protocol = BotProtocol.Parse(p["protocol"] as JsonObject ?? throw new RpcException("protocol がありません"));
        var setup = (p["setup"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p["headers"] is JsonObject h)
        {
            foreach (var pair in h)
            {
                headers[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
            }
        }

        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var latencies = new List<double>();
        var lags = new List<double>();
        var errors = new List<string>();
        var completed = 0;
        var moves = 0;
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = Enumerable.Range(0, games).Select(async g =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var sessions = new Dictionary<string, UserSession>(StringComparer.Ordinal);
            try
            {
                foreach (var role in Roles)
                {
                    var session = new UserSession($"{role}{g.ToString(CultureInfo.InvariantCulture)}");
                    sessions[role] = session;
                    await session.ConnectAsync(url, transport, headers, null, TimeSpan.FromSeconds(15), cancellationToken, socketPath).ConfigureAwait(false);
                }

                foreach (var step in setup)
                {
                    var role = Params.Str(step, "user");
                    var session = sessions[role];
                    var memory = new Dictionary<string, string>(StringComparer.Ordinal) { ["user"] = session.Name };
                    foreach (var pair in sessions)
                    {
                        foreach (var m in pair.Value.Memory)
                        {
                            memory[$"{pair.Key}.{m.Key}"] = m.Value;
                            if (pair.Key == role)
                            {
                                memory[m.Key] = m.Value;
                            }
                        }
                    }

                    var args = (JsonArray?)BotProtocol.Fill(step["args"], null, memory) ?? [];
                    var ack = Params.OptBool(step, "ack", false);
                    var result = await session.EmitAsync(Params.Str(step, "emit"), args, ack, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    if (result != null && step["remember"] is JsonObject remember)
                    {
                        foreach (var pair in remember)
                        {
                            if (Bot.AsText(Bot.Json(result, pair.Value?.GetValue<string>() ?? string.Empty)) is { } value)
                            {
                                session.Memory[pair.Key] = value;
                            }
                        }
                    }

                    if (step["copy"] is JsonObject copy)
                    {
                        foreach (var pair in copy)
                        {
                            var parts = (pair.Value?.GetValue<string>() ?? string.Empty).Split('.', 2);
                            if (parts.Length == 2 && sessions.TryGetValue(parts[0], out var from) && from.Memory.TryGetValue(parts[1], out var value))
                            {
                                session.Memory[pair.Key] = value;
                            }
                        }
                    }
                }

                var result2 = await Bot.PlayAsync(sessions.Values.ToDictionary(s => s.Name, s => s, StringComparer.Ordinal), sessions.Values.Select(s => s.Name).ToList(), protocol, "random", seed + g, 0, gameTimeout, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                var gameMoves = result2["moves"]?.GetValue<int>() ?? 0;
                var startedAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(result2["elapsed_ms"]?.GetValue<long>() ?? 0);
                foreach (var move in (result2["trace"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var user = move["user"]?.GetValue<string>();
                    var at = startedAt + TimeSpan.FromMilliseconds(move["at_ms"]?.GetValue<long>() ?? 0);
                    var arrivals = new List<DateTimeOffset>();
                    foreach (var session in sessions.Values)
                    {
                        var next = session.EventsSince(0).FirstOrDefault(e => e.Ts > at && e.Event != "__disconnected");
                        if (next != null)
                        {
                            arrivals.Add(next.Ts);
                        }
                    }

                    if (arrivals.Count == sessions.Count)
                    {
                        lock (latencies)
                        {
                            latencies.Add((arrivals.Max() - at).TotalMilliseconds);
                        }
                    }

                    var seq = move["seq"]?.GetValue<long>() ?? 0;
                    var owner = sessions.Values.FirstOrDefault(s => s.Name == user);
                    var received = owner?.EventsSince(seq - 1).FirstOrDefault(e => e.Seq == seq);
                    if (received != null)
                    {
                        lock (lags)
                        {
                            lags.Add(Math.Max(0, (at - received.Ts).TotalMilliseconds));
                        }
                    }
                }

                Interlocked.Add(ref moves, gameMoves);
                if (result2["stop_reason"]?.GetValue<string>() == "all_finished")
                {
                    Interlocked.Increment(ref completed);
                }
                else
                {
                    lock (errors)
                    {
                        errors.Add($"game {g}: {result2["stop_reason"]}");
                    }
                }
            }
            catch (Exception e) when (e is RpcException or TimeoutException or InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException or HttpRequestException or OperationCanceledException)
            {
                lock (errors)
                {
                    errors.Add($"game {g}: {e.Message}");
                }
            }
            finally
            {
                foreach (var session in sessions.Values)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }

                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        wall.Stop();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        return new JsonObject
        {
            ["games"] = games,
            ["completed"] = completed,
            ["moves"] = moves,
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            ["error_rate"] = games == 0 ? 0 : (double)errors.Count / games,
            ["latency_ms"] = new JsonArray(latencies.Select(l => (JsonNode?)JsonValue.Create(Math.Round(l, 3))).ToArray()),
            ["send_lag_ms"] = new JsonArray(lags.Select(l => (JsonNode?)JsonValue.Create(Math.Round(l, 3))).ToArray()),
            ["wall_ms"] = wall.ElapsedMilliseconds,
            ["cpu_ms"] = Math.Round(cpu.TotalMilliseconds, 1),
            ["processors"] = Environment.ProcessorCount,
        };
    }
}
