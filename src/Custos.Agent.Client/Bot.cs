using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Agent.Common;
using Custos.SocketIO.Wire;

namespace Custos.Agent.Client;

/// <summary>
/// ボットの約束事（どのイベントで選択を求められ、何を返すか）。アプリごとの設定（検証用リポジトリの scenarios/bot.yaml）から渡す。
/// サーバーが提示した選択肢から選ぶ（設計書 第25.4節、第25.9節）。
/// </summary>
public sealed record BotProtocol(
    string ChoiceEvent,
    string ChoicesPath,
    string? KindPath,
    string? ObjectChoiceField,
    string ReplyEvent,
    JsonNode ReplyTemplate,
    IReadOnlyList<(string On, string ReplyEvent, JsonNode Template)> Acks,
    IReadOnlyList<string> EndEvents,
    string RoomKey)
{
    /// <summary>引数から読む。</summary>
    public static BotProtocol Parse(JsonObject p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var acks = new List<(string, string, JsonNode)>();
        foreach (var item in (p["acks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            acks.Add((Params.Str(item, "on"), Params.Str(item, "reply_event"), item["reply_template"]?.DeepClone() ?? new JsonObject()));
        }

        return new BotProtocol(
            Params.Str(p, "choice_event"),
            Params.OptStr(p, "choices_path") ?? "$.choices",
            Params.OptStr(p, "kind_path"),
            Params.OptStr(p, "object_choice_field"),
            Params.Str(p, "reply_event"),
            p["reply_template"]?.DeepClone() ?? new JsonObject(),
            acks,
            Params.StrList(p, "end_events"),
            Params.OptStr(p, "room_key") ?? "room");
    }

    /// <summary>
    /// 返信の値を作る。文字列の "{choice}" は選んだ値（型を保つ）に、"{room}" などは覚えている値に置き換える。
    /// </summary>
    public static JsonNode? Fill(JsonNode? template, JsonNode? choice, IReadOnlyDictionary<string, string> memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        switch (template)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var pair in obj)
                {
                    copy[pair.Key] = Fill(pair.Value, choice, memory);
                }

                return copy;
            case JsonArray array:
                var list = new JsonArray();
                foreach (var item in array)
                {
                    list.Add(Fill(item, choice, memory));
                }

                return list;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text == "{choice}")
                {
                    return choice?.DeepClone();
                }

                if (text.Length > 2 && text[0] == '{' && text[^1] == '}' && memory.TryGetValue(text[1..^1], out var remembered))
                {
                    return JsonValue.Create(remembered);
                }

                return JsonValue.Create(text);
            default:
                return template?.DeepClone();
        }
    }
}

/// <summary>相手役のボット。ルールどおり（提示された選択肢の中から）に手を打つ。</summary>
public static class Bot
{
    /// <summary>
    /// 対局を進める。users のそれぞれについて、選択を求められたら選択肢から選んで返す。
    /// 返り値は、打った手の数、終わった利用者、選んだ手の一覧（再生に使える）。
    /// </summary>
    public static async Task<JsonObject> PlayAsync(
        IReadOnlyDictionary<string, UserSession> sessions,
        IReadOnlyList<string> users,
        BotProtocol protocol,
        string policy,
        int seed,
        int maxMoves,
        TimeSpan timeout,
        TimeSpan think,
        CancellationToken cancellationToken,
        TimeSpan? stall = null,
        JsonNode? prefer = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(protocol);
        var random = new Random(seed);
        // 始める前に届いていて、まだ答えていない選択の要求があれば、そこから始める（届いた後にボットを始めても止まらないように）
        var cursors = users.ToDictionary(u => u, u => StartCursor(Session(sessions, u), protocol), StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var trace = new JsonArray();
        var moves = 0;
        var stopwatch = Stopwatch.StartNew();
        var stopReason = "timeout";
        var lastProgress = stopwatch.Elapsed;
        while (stopwatch.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            // 調べる前に「次のイベント」を取っておく（調べた後に届いたイベントで、すぐに起きられるように）
            var wake = users.Select(u => Session(sessions, u).NextEvent).ToList();
            var progressed = false;
            foreach (var user in users)
            {
                var session = Session(sessions, user);
                foreach (var received in session.EventsSince(cursors[user]))
                {
                    cursors[user] = received.Seq;
                    progressed = true;
                    if (protocol.EndEvents.Contains(received.Event, StringComparer.Ordinal))
                    {
                        finished.Add(user);
                        continue;
                    }

                    foreach (var ack in protocol.Acks.Where(a => a.On == received.Event))
                    {
                        var reply = BotProtocol.Fill(ack.Template, null, session.Memory);
                        await session.EmitAsync(ack.ReplyEvent, [reply], false, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    }

                    if (received.Event != protocol.ChoiceEvent || received.Args.Count == 0)
                    {
                        continue;
                    }

                    var payload = received.Args[0];
                    var choices = Json(payload, protocol.ChoicesPath) as JsonArray;
                    var kind = protocol.KindPath == null ? null : Json(payload, protocol.KindPath)?.ToString();
                    if (choices == null || choices.Count == 0)
                    {
                        trace.Add(new JsonObject { ["user"] = user, ["kind"] = kind, ["stuck"] = true, ["seq"] = received.Seq });
                        continue;
                    }

                    var index = policy switch
                    {
                        "first" => 0,
                        "last" => choices.Count - 1,
                        "prefer" => PreferredIndex(choices, protocol, prefer) ?? random.Next(choices.Count),
                        _ => random.Next(choices.Count),
                    };
                    var item = choices[index];
                    var value = item is JsonObject obj && protocol.ObjectChoiceField != null ? obj[protocol.ObjectChoiceField]?.DeepClone() : item?.DeepClone();
                    var replyValue = BotProtocol.Fill(protocol.ReplyTemplate, value, session.Memory);
                    await session.EmitAsync(protocol.ReplyEvent, [replyValue], false, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    moves++;
                    trace.Add(new JsonObject
                    {
                        ["user"] = user,
                        ["kind"] = kind,
                        ["choices"] = choices.DeepClone(),
                        ["choice"] = value?.DeepClone(),
                        ["seq"] = received.Seq,
                        ["at_ms"] = stopwatch.ElapsedMilliseconds,
                    });
                    if (maxMoves > 0 && moves >= maxMoves)
                    {
                        stopReason = "max_moves";
                        goto Done;
                    }

                    // 直列に送る（think）：返答の後に待ち、その返答へのサーバーの反応（次の選択の要求など）が届いてから次の要求を見る。
                    // 返答の前に待つと、返答の直後にほかの利用者の要求を見ることになり、反応が届いたかどうかで答える順序がぶれる
                    // （決定性の測定。設計書 第22.2節「並行の操作の順序はぶれの原因」）
                    if (think > TimeSpan.Zero)
                    {
                        await Task.Delay(think, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (finished.Count == users.Count)
            {
                stopReason = "all_finished";
                break;
            }

            if (progressed)
            {
                lastProgress = stopwatch.Elapsed;
            }
            else
            {
                // 詰みの判定（第25.4節）：誰にも何も届かないまま一定時間が過ぎた（選択を待っている人がいないのに終局していない）
                if (stall is { } limit && limit > TimeSpan.Zero && stopwatch.Elapsed - lastProgress > limit)
                {
                    stopReason = "stalled";
                    break;
                }

                // 誰かにイベントが届いたらすぐに起きる（以前は 20 ms ごとに見に行っていたので、返答が最大 20 ms 遅れ、
                // 性能の計測の妥当性（送信の遅れが SLO の 2 % 以内）を負荷側が満たせなかった）。詰みの判定のため、長くても 20 ms で見直す
                await Task.WhenAny([.. wake, Task.Delay(20, cancellationToken)]).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

    Done:
        return new JsonObject
        {
            ["moves"] = moves,
            ["stop_reason"] = stopReason,
            ["finished"] = new JsonArray(finished.Order(StringComparer.Ordinal).Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["elapsed_ms"] = stopwatch.ElapsedMilliseconds,
            ["trace"] = trace,
        };
    }

    /// <summary>選択肢の中に、選びたい値（prefer）があればその位置（探索で「このカードを使う」ために使う）。</summary>
    private static int? PreferredIndex(JsonArray choices, BotProtocol protocol, JsonNode? prefer)
    {
        if (prefer == null)
        {
            return null;
        }

        // 文字列の "5" と数の 5 は同じものとして比べる（台帳の対象は文字列で書くため）
        var wanted = prefer.ToString();
        for (var i = 0; i < choices.Count; i++)
        {
            var value = choices[i] is JsonObject obj && protocol.ObjectChoiceField != null ? obj[protocol.ObjectChoiceField] : choices[i];
            if (value?.ToString() == wanted)
            {
                return i;
            }
        }

        return null;
    }

    private static long StartCursor(UserSession session, BotProtocol protocol)
    {
        // 最後に送った後に届いた、選択の要求と確認の要求（acks の on）は、まだ答えていない
        var pending = session.EventsSince(session.LastEmitSeq).FirstOrDefault(e => e.Event == protocol.ChoiceEvent || protocol.Acks.Any(a => a.On == e.Event));
        return pending != null ? pending.Seq - 1 : session.LastSeq;
    }

    private static UserSession Session(IReadOnlyDictionary<string, UserSession> sessions, string user) =>
        sessions.TryGetValue(user, out var session) ? session : throw new RpcException($"知らない利用者です：{user}");

    /// <summary>JSONPath の最初の値。</summary>
    public static JsonNode? Json(JsonNode? root, string path)
    {
        var current = root;
        foreach (var token in Tokens(path))
        {
            current = current switch
            {
                JsonObject obj => obj[token],
                JsonArray array when int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < array.Count => array[i],
                _ => null,
            };
        }

        return current;
    }

    private static IEnumerable<string> Tokens(string path)
    {
        var text = path.StartsWith('$') ? path[1..] : path;
        foreach (var part in text.Replace("[", ".", StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return part.Trim('\'', '"');
        }
    }

    /// <summary>JSON の値を文字列にする（覚えておくため）。</summary>
    public static string? AsText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
    };
}
