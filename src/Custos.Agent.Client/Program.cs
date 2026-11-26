using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Text.Json.Nodes;
using Custos.Agent.Client;
using Custos.Agent.Common;

// custos-client：Socket.IO のスクリプトクライアント（指示書 タスク6）。
// 利用者側のネットワークにだけつなぎ、管理側から docker exec -i で操作する。複数の利用者を同時に動かせる。
var sessions = new ConcurrentDictionary<string, UserSession>(StringComparer.Ordinal);
var raws = new ConcurrentDictionary<string, RawClient>(StringComparer.Ordinal);
return await AgentHost.RunAsync(args, server => Register(server, sessions, raws)).ConfigureAwait(false);

static void Register(RpcServer server, ConcurrentDictionary<string, UserSession> sessions, ConcurrentDictionary<string, RawClient> raws)
{
    UserSession Get(JsonObject p)
    {
        var user = Params.Str(p, "user");
        return sessions.TryGetValue(user, out var session) ? session : throw new RpcException($"知らない利用者です：{user}");
    }

    server.Register("user.connect", async (p, ct) =>
    {
        var user = Params.Str(p, "user");
        var session = sessions.GetOrAdd(user, u => new UserSession(u));
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p["headers"] is JsonObject h)
        {
            foreach (var pair in h)
            {
                headers[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
            }
        }

        NameValueCollection? query = null;
        if (p["query"] is JsonObject q)
        {
            query = [];
            foreach (var pair in q)
            {
                query[pair.Key] = pair.Value?.ToString();
            }
        }

        await session.ConnectAsync(
            new Uri(Params.Str(p, "url")),
            Params.OptStr(p, "transport") ?? "websocket",
            headers,
            query,
            TimeSpan.FromMilliseconds(Params.OptLong(p, "timeout_ms", 15000)),
            ct,
            new SocketPath(Params.OptStr(p, "path") ?? "/socket.io/")).ConfigureAwait(false);
        return new JsonObject { ["user"] = user, ["socket_id"] = session.SocketId, ["connected"] = session.Connected };
    });

    server.Register("user.emit", async (p, ct) =>
    {
        var session = Get(p);
        var args = p["args"] as JsonArray ?? [];
        var ack = Params.OptBool(p, "ack", false);
        var result = await session.EmitAsync(Params.Str(p, "event"), (JsonArray)args.DeepClone(), ack, TimeSpan.FromMilliseconds(Params.OptLong(p, "timeout_ms", 10000)), ct).ConfigureAwait(false);
        if (result != null && p["remember"] is JsonObject remember)
        {
            foreach (var pair in remember)
            {
                var path = pair.Value?.GetValue<string>() ?? string.Empty;
                if (Bot.AsText(Bot.Json(result, path)) is { } value)
                {
                    session.Memory[pair.Key] = value;
                }
            }
        }

        return new JsonObject { ["ack"] = result, ["memory"] = MemoryJson(session) };
    });

    server.Register("users.emit_concurrent", async (p, ct) =>
    {
        // 同時送信（二重処理、時間切れと着手の同時、など）
        var items = (p["items"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var tasks = items.Select(item =>
        {
            var session = Get(item);
            var args = item["args"] as JsonArray ?? [];
            return session.EmitAsync(Params.Str(item, "event"), (JsonArray)args.DeepClone(), false, TimeSpan.FromSeconds(10), ct);
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new JsonObject { ["sent"] = tasks.Count };
    });

    server.Register("user.remember", p =>
    {
        var session = Get(p);
        session.Memory[Params.Str(p, "key")] = Params.Str(p, "value");
        return MemoryJson(session);
    });

    server.Register("user.events", p =>
    {
        var session = Get(p);
        var events = session.EventsSince(Params.OptLong(p, "since", 0));
        return new JsonObject
        {
            ["events"] = new JsonArray(events.Select(e => (JsonNode?)e.ToJson()).ToArray()),
            ["last_seq"] = session.LastSeq,
        };
    });

    server.Register("user.wait", async (p, ct) =>
    {
        var session = Get(p);
        var name = Params.OptStr(p, "event");
        var path = Params.OptStr(p, "match_path");
        var expected = p["match_value"]?.ToJsonString();
        var found = await session.WaitForAsync(
            e => (name == null || e.Event == name) && (path == null || Bot.Json(e.Args, path)?.ToJsonString() == expected),
            Params.OptLong(p, "since", 0),
            TimeSpan.FromMilliseconds(Params.OptLong(p, "timeout_ms", 10000)),
            ct).ConfigureAwait(false);
        return new JsonObject { ["found"] = found != null, ["event"] = found?.ToJson(), ["last_seq"] = session.LastSeq };
    });

    server.Register("user.disconnect", async (p, ct) =>
    {
        var session = Get(p);
        await session.DisconnectAsync().ConfigureAwait(false);
        return new JsonObject { ["connected"] = session.Connected };
    });

    server.Register("user.remove", async (p, ct) =>
    {
        if (sessions.TryRemove(Params.Str(p, "user"), out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        return new JsonObject { ["removed"] = true };
    });

    server.Register("user.info", p =>
    {
        var session = Get(p);
        return new JsonObject
        {
            ["user"] = session.Name,
            ["connected"] = session.Connected,
            ["socket_id"] = session.SocketId,
            ["last_seq"] = session.LastSeq,
            ["last_emit_seq"] = session.LastEmitSeq,
            ["last_disconnect"] = session.LastDisconnectReason,
            ["memory"] = MemoryJson(session),
        };
    });

    server.Register("users.list", _ => new JsonArray(sessions.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()));

    server.Register("users.clear", async (p, ct) =>
    {
        // リセットのとき：すべての利用者と生のクライアントを切断して消す（古い接続がアプリの再起動後に再接続しないように）
        var users = sessions.Keys.ToList();
        foreach (var user in users)
        {
            if (sessions.TryRemove(user, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        var rawNames = raws.Keys.ToList();
        foreach (var name in rawNames)
        {
            if (raws.TryRemove(name, out var raw))
            {
                await raw.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new JsonObject { ["users"] = users.Count, ["raw"] = rawNames.Count };
    });

    server.Register("bot.play", async (p, ct) =>
    {
        var protocol = BotProtocol.Parse(p["protocol"] as JsonObject ?? throw new RpcException("protocol がありません"));
        return await Bot.PlayAsync(
            sessions,
            Params.StrList(p, "users"),
            protocol,
            Params.OptStr(p, "policy") ?? "random",
            (int)Params.OptLong(p, "seed", 1),
            (int)Params.OptLong(p, "max_moves", 0),
            TimeSpan.FromMilliseconds(Params.OptLong(p, "timeout_ms", 120_000)),
            TimeSpan.FromMilliseconds(Params.OptLong(p, "think_ms", 0)),
            ct,
            TimeSpan.FromMilliseconds(Params.OptLong(p, "stall_ms", 0)),
            p["prefer"]?.DeepClone()).ConfigureAwait(false);
    });

    server.Register("raw.connect", async (p, ct) =>
    {
        var name = Params.Str(p, "name");
        if (raws.TryRemove(name, out var old))
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }

        var client = new RawClient(name);
        raws[name] = client;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (p["headers"] is JsonObject h)
        {
            foreach (var pair in h)
            {
                headers[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
            }
        }

        var rawPath = new SocketPath(Params.OptStr(p, "path") ?? "/socket.io/");
        if ((Params.OptStr(p, "transport") ?? "polling") == "websocket")
        {
            await client.ConnectAsync(new Uri(Params.Str(p, "url")), headers, Params.OptBool(p, "connect_namespace", true), ct, rawPath).ConfigureAwait(false);
        }
        else
        {
            await client.ConnectPollingAsync(new Uri(Params.Str(p, "url")), headers, Params.OptBool(p, "connect_namespace", true), ct, rawPath).ConfigureAwait(false);
        }

        return new JsonObject { ["name"] = name, ["sid"] = client.Sid, ["state"] = client.State };
    });

    server.Register("raw.send", async (p, ct) =>
    {
        var name = Params.Str(p, "name");
        var client = raws.TryGetValue(name, out var c) ? c : throw new RpcException($"知らない生のクライアントです：{name}");
        if (Params.OptStr(p, "binary_b64") is { } b64)
        {
            await client.SendBinaryAsync(Convert.FromBase64String(b64), ct).ConfigureAwait(false);
        }
        else
        {
            await client.SendTextAsync(Params.Str(p, "text"), ct).ConfigureAwait(false);
        }

        return new JsonObject { ["state"] = client.State };
    });

    server.Register("raw.events", p =>
    {
        var name = Params.Str(p, "name");
        var client = raws.TryGetValue(name, out var c) ? c : throw new RpcException($"知らない生のクライアントです：{name}");
        return new JsonObject { ["events"] = client.Since(Params.OptLong(p, "since", 0)), ["state"] = client.State };
    });

    server.Register("raw.close", async (p, ct) =>
    {
        if (raws.TryRemove(Params.Str(p, "name"), out var client))
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        return new JsonObject { ["closed"] = true };
    });

    server.Register("load.run", (p, ct) => LoadRunner.RunAsync(p, ct));
}

static JsonObject MemoryJson(UserSession session)
{
    var memory = new JsonObject();
    foreach (var pair in session.Memory)
    {
        memory[pair.Key] = pair.Value;
    }

    return memory;
}
