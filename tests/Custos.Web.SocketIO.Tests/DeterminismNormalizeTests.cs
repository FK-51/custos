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
