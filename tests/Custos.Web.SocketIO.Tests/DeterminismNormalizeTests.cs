using System.Text.Json.Nodes;
using Custos.Core.Config;
using Custos.Web.SocketIO.Determinism;

namespace Custos.Web.SocketIO.Tests;

/// <summary>決定性の比べ方：ID の振り直し（設計書 第18.2節、第22章）。</summary>
public class DeterminismNormalizeTests
{
    private static readonly DeterminismConfig Config = new([], "creation_order", [], 3, "report", 39);

    private static SortedDictionary<string, JsonNode?> State(string alice, string bob) => new(StringComparer.Ordinal)
    {
        ["player.name"] = new JsonArray(new JsonArray(alice, "alice-det"), new JsonArray(bob, "bob-det")),
        ["room.owner_id"] = new JsonArray(new JsonArray("room-abc123def456", alice)),
        ["view.choice_kind"] = new JsonArray(new JsonArray(alice, "opponentChoice"), new JsonArray(bob, "draw")),
    };

    private static string Text(SortedDictionary<string, JsonNode?> state) =>
        string.Join("\n", state.Select(p => p.Key + "=" + (p.Value?.ToJsonString() ?? "null")));

    [Fact]
    public void SocketIdsWithoutDigitsAreRenumberedByConnectionOrder()
    {
        // socket.id の乱数の部分に数字が1文字もないことがある（実際に起きた例）
        var normalized = DeterminismRunner.Normalize(State("xFxGFRqKOKGjBhlLAAAC", "9r3AB6l0q3HFt9jVAAAE"), Config, ["xFxGFRqKOKGjBhlLAAAC", "9r3AB6l0q3HFt9jVAAAE"]);
        Assert.Equal("[[\"#1\",\"alice-det\"],[\"#2\",\"bob-det\"]]", normalized["player.name"]?.ToJsonString());
        Assert.Equal("[[\"#3\",\"#1\"]]", normalized["room.owner_id"]?.ToJsonString());
    }

    [Fact]
    public void OrdinaryWordsAreNotTreatedAsIds()
    {
        var normalized = DeterminismRunner.Normalize(State("xFxGFRqKOKGjBhlLAAAC", "9r3AB6l0q3HFt9jVAAAE"), Config, ["xFxGFRqKOKGjBhlLAAAC", "9r3AB6l0q3HFt9jVAAAE"]);
        Assert.Equal("[[\"#1\",\"opponentChoice\"],[\"#2\",\"draw\"]]", normalized["view.choice_kind"]?.ToJsonString());
    }

    [Fact]
    public void SameStateWithDifferentRandomIdsNormalizesEqually()
    {
        var withoutDigits = DeterminismRunner.Normalize(State("Dk-hYSRlgFMdmHE_AAAC", "AlW9A_o5GbynEDcpAAAE"), Config, ["Dk-hYSRlgFMdmHE_AAAC", "AlW9A_o5GbynEDcpAAAE"]);
        var withDigits = DeterminismRunner.Normalize(State("1c0B74jbHaoA8AYnAAAC", "bbZWHTj81q_gCd6FAAAE"), Config, ["1c0B74jbHaoA8AYnAAAC", "bbZWHTj81q_gCd6FAAAE"]);
        Assert.Equal(Text(withDigits), Text(withoutDigits));
    }
}

/// <summary>送信された状態の比べ方（校正用バグの品質チェックで、V2「送信された状態が変わる」を見る。第19.11節）。</summary>
public class SentNodeTests
{
    private static Custos.SocketIO.Wire.RecordEntry Connect(long seq, string eioSid, string socketId) => new()
    {
        Seq = seq, Kind = "eio", Dir = "s2c", Sio = "connect", Sid = eioSid, Args = new JsonArray(new JsonObject { ["sid"] = socketId }),
    };

    private static Custos.SocketIO.Wire.RecordEntry Event(long seq, string eioSid, string name, JsonNode payload) => new()
    {
        Seq = seq, Kind = "eio", Dir = "s2c", Sio = "event", Sid = eioSid, EventName = name, Args = new JsonArray(payload),
    };

    [Fact]
    public void IdsAreRenumberedAndInterleavingAcrossSocketsIsIgnored()
    {
        string[] order = ["xFxGFRqKOKGjBhlLAAAC", "9r3AB6l0q3HFt9jVAAAE"];
        var first = DeterminismRunner.SentNode(
            [
                Connect(1, "e1", order[0]), Connect(2, "e2", order[1]),
                Event(3, "e1", "yourTurn", new JsonObject { ["room"] = "room-abc123def456", ["hand"] = new JsonArray(1, 2) }),
                Event(4, "e2", "anotherTurn", new JsonObject { ["player"] = order[0] }),
            ],
            order);

        // 別の実行：ID の値は違い、接続をまたいだ届く順も違う
        string[] other = ["KjQwPzRmTnBvXyZaAAAC", "a1b2C3d4E5f6G7h8AAAE"];
        var second = DeterminismRunner.SentNode(
            [
                Connect(10, "f1", other[0]), Connect(11, "f2", other[1]),
                Event(12, "f2", "anotherTurn", new JsonObject { ["player"] = other[0] }),
                Event(13, "f1", "yourTurn", new JsonObject { ["room"] = "room-zzz999yyy888", ["hand"] = new JsonArray(1, 2) }),
            ],
            other);

        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        Assert.Equal("[[\"anotherTurn\",[{\"player\":\"#1\"}]]]", first["#2"]?.ToJsonString());
    }

    [Fact]
    public void TimesNearTheFixedClockAreIgnored()
    {
        // 時計は合わせた時刻から進み続けるので、期限などの時刻は実行ごとに数ミリ秒ずれる
        string[] order = ["xFxGFRqKOKGjBhlLAAAC"];
        JsonObject Payload(long deadline, string at) => new() { ["deadline"] = deadline, ["at"] = at, ["deck"] = 5, ["count"] = 1767 };
        var first = DeterminismRunner.SentNode([Connect(1, "e1", order[0]), Event(2, "e1", "yourTurn", Payload(DeterminismRunner.FixedClockEpochMs + 75565, "2026-01-01T00:01:15.565Z"))], order);
        var second = DeterminismRunner.SentNode([Connect(1, "e1", order[0]), Event(2, "e1", "yourTurn", Payload(DeterminismRunner.FixedClockEpochMs + 75366, "2026-01-01T00:01:15.366Z"))], order);
        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        Assert.Contains("\"deck\":5", first.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"count\":1767", first.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExtraFieldChangesTheSentState()
    {
        string[] order = ["xFxGFRqKOKGjBhlLAAAC"];
        var normal = DeterminismRunner.SentNode([Connect(1, "e1", order[0]), Event(2, "e1", "yourTurn", new JsonObject { ["now"] = new JsonObject { ["deck"] = 5 } })], order);
        var leaked = DeterminismRunner.SentNode([Connect(1, "e1", order[0]), Event(2, "e1", "yourTurn", new JsonObject { ["now"] = new JsonObject { ["deck"] = 5, ["nextCard"] = 7 } })], order);
        Assert.NotEqual(normal.ToJsonString(), leaked.ToJsonString());
    }
}
