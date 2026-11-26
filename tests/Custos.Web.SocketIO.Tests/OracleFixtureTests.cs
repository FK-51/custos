using System.Text.Json.Nodes;
using Custos.SocketIO.Wire;

namespace Custos.Web.SocketIO.Tests;

/// <summary>
/// 公式の JavaScript の実装（socket.io-parser 4.2.7、engine.io-parser 5.2.3）で作った正解のデータと、
/// C# の読み解きの結果が一致することを確かめる（指示書 タスク5）。正解は tests/oracle-node/gen-fixtures.js が作る。
/// </summary>
public class OracleFixtureTests
{
    private static readonly JsonObject Fixtures = LoadFixtures();

    private static JsonObject LoadFixtures()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "socketio-fixtures.json");
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidOperationException("正解のデータを読めません");
    }

    /// <summary>Socket.IO のパケットの名前の一覧。</summary>
    public static TheoryData<string> SocketIoCases()
    {
        var data = new TheoryData<string>();
        foreach (var item in Fixtures["socketio_packets"]?.AsArray() ?? [])
        {
            data.Add(item?["name"]?.GetValue<string>() ?? string.Empty);
        }

        return data;
    }

    /// <summary>境界の文字列の名前の一覧。</summary>
    public static TheoryData<string> EdgeCases()
    {
        var data = new TheoryData<string>();
        foreach (var item in Fixtures["socketio_edge_cases"]?.AsArray() ?? [])
        {
            data.Add(item?["name"]?.GetValue<string>() ?? string.Empty);
        }

        return data;
    }

    /// <summary>Engine.IO のパケットの名前の一覧。</summary>
    public static TheoryData<string> EngineIoCases()
    {
        var data = new TheoryData<string>();
        foreach (var item in Fixtures["engineio_packets"]?.AsArray() ?? [])
        {
            data.Add(item?["name"]?.GetValue<string>() ?? string.Empty);
        }

        return data;
    }

    /// <summary>ポーリングの本文の名前の一覧。</summary>
    public static TheoryData<string> PollingCases()
    {
        var data = new TheoryData<string>();
        foreach (var item in Fixtures["polling_payloads"]?.AsArray() ?? [])
        {
            data.Add(item?["name"]?.GetValue<string>() ?? string.Empty);
        }

        return data;
    }

    private static JsonObject Case(string section, string name) =>
        (Fixtures[section]?.AsArray() ?? []).OfType<JsonObject>().First(c => c["name"]?.GetValue<string>() == name);

    /// <summary>公式が符号化したパケットを読み解くと、公式が読み解いた結果と一致する。</summary>
    [Theory]
    [MemberData(nameof(SocketIoCases))]
    public void DecodesLikeOfficialParser(string name)
    {
        var item = Case("socketio_packets", name);
        var decoder = new SocketIoStreamDecoder();
        SocketIoPacket? packet = null;
        foreach (var part in item["encoded"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            packet = part["text"] is { } text
                ? decoder.OnText(text.GetValue<string>())
                : decoder.OnBinary(Convert.FromBase64String(part["binary_b64"]?.GetValue<string>() ?? string.Empty));
        }

        Assert.NotNull(packet);
        var expected = item["decoded"]?.AsObject() ?? [];
        Assert.True(packet.Valid, packet.Error);
        Assert.Equal(expected["type"]?.GetValue<string>(), packet.TypeName);
        Assert.Equal(expected["nsp"]?.GetValue<string>(), packet.Nsp);
        Assert.Equal(expected["id"]?.GetValue<long?>(), packet.Id);
        Assert.True(JsonNode.DeepEquals(expected["data"], packet.Data), $"data: {expected["data"]?.ToJsonString()} != {packet.Data?.ToJsonString()}");
        Assert.Equal(expected["attachments"]?.GetValue<int>(), packet.Attachments);
    }

    /// <summary>公式が読み解いた値を C# で符号化すると、公式の符号化と同じ文字列とバイナリになる。</summary>
    [Theory]
    [MemberData(nameof(SocketIoCases))]
    public void EncodesLikeOfficialParser(string name)
    {
        var item = Case("socketio_packets", name);
        var expected = item["decoded"]?.AsObject() ?? [];
        var type = expected["type"]?.GetValue<string>() switch
        {
            "connect" => SocketIoPacketType.Connect,
            "disconnect" => SocketIoPacketType.Disconnect,
            "event" => SocketIoPacketType.Event,
            "ack" => SocketIoPacketType.Ack,
            "connect_error" => SocketIoPacketType.ConnectError,
            "binary_event" => SocketIoPacketType.BinaryEvent,
            _ => SocketIoPacketType.BinaryAck,
        };
        var packet = new SocketIoPacket(type, expected["nsp"]?.GetValue<string>() ?? "/", expected["id"]?.GetValue<long?>(), expected["data"]?.DeepClone(), 0);
        var (header, attachments) = SocketIo.Encode(packet);
        var parts = item["encoded"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        Assert.Equal(parts[0]["text"]?.GetValue<string>(), header);
        Assert.Equal(parts.Count - 1, attachments.Count);
        for (var i = 1; i < parts.Count; i++)
        {
            Assert.Equal(parts[i]["binary_b64"]?.GetValue<string>(), Convert.ToBase64String(attachments[i - 1]));
        }
    }

    /// <summary>境界の文字列（崩れた値）も、公式と同じように受け付ける・拒否する。</summary>
    [Theory]
    [MemberData(nameof(EdgeCases))]
    public void EdgeCasesMatchOfficialParser(string name)
    {
        var item = Case("socketio_edge_cases", name);
        var decoder = new SocketIoStreamDecoder();
        var packet = decoder.OnText(item["text"]?.GetValue<string>() ?? string.Empty);
        var expectError = item["error"]?.GetValue<bool>() == true;
        var expectWaiting = item["waiting_for_attachments"]?.GetValue<bool>() == true;
        if (expectWaiting)
        {
            Assert.Null(packet);
            Assert.True(decoder.Waiting);
            return;
        }

        Assert.NotNull(packet);
        Assert.Equal(!expectError, packet.Valid);
        if (expectError)
        {
            Assert.Equal(item["message"]?.GetValue<string>(), packet.Error);
            return;
        }

        var expected = item["decoded"]?.AsObject() ?? [];
        Assert.Equal(expected["type"]?.GetValue<string>(), packet.TypeName);
        Assert.Equal(expected["nsp"]?.GetValue<string>(), packet.Nsp);
        Assert.Equal(expected["id"]?.GetValue<long?>(), packet.Id);
        Assert.True(JsonNode.DeepEquals(expected["data"], packet.Data), $"data: {expected["data"]?.ToJsonString()} != {packet.Data?.ToJsonString()}");
    }

    /// <summary>Engine.IO のパケットの読み解き。</summary>
    [Theory]
    [MemberData(nameof(EngineIoCases))]
    public void EngineIoDecodesLikeOfficialParser(string name)
    {
        var item = Case("engineio_packets", name);
        var expected = item["decoded"]?.AsObject() ?? [];
        var packet = item["encoded_text"]?.GetValue<string>() is { } text
            ? EngineIo.DecodeText(text)
            : EngineIo.DecodeBinary(Convert.FromBase64String(item["encoded_binary_b64"]?.GetValue<string>() ?? string.Empty));
        Assert.Equal(expected["type"]?.GetValue<string>(), packet.TypeName);
        Assert.Equal(expected["binary"]?.GetValue<bool>(), packet.IsBinary);
        if (packet.IsBinary)
        {
            Assert.Equal(expected["data"]?["_binary_b64"]?.GetValue<string>(), Convert.ToBase64String(packet.Binary ?? []));
        }
        else
        {
            Assert.Equal(expected["data"]?.GetValue<string>(), packet.Text);
        }

        var supportsBinary = item["supports_binary"]?.GetValue<bool>() == true;
        var (encodedText, encodedBinary) = EngineIo.Encode(packet, supportsBinary);
        Assert.Equal(item["encoded_text"]?.GetValue<string>(), encodedText);
        Assert.Equal(item["encoded_binary_b64"]?.GetValue<string>(), encodedBinary == null ? null : Convert.ToBase64String(encodedBinary));
    }

    /// <summary>ポーリングの本文（0x1e 区切り）の読み解き。</summary>
    [Theory]
    [MemberData(nameof(PollingCases))]
    public void PollingPayloadDecodesLikeOfficialParser(string name)
    {
        var item = Case("polling_payloads", name);
        var packets = EngineIo.DecodePayload(item["payload"]?.GetValue<string>() ?? string.Empty);
        var expected = item["decoded"]?.AsArray() ?? [];
        Assert.Equal(expected.Count, packets.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i]?.AsObject() ?? [];
            Assert.Equal(e["type"]?.GetValue<string>(), packets[i].TypeName);
            Assert.Equal(e["binary"]?.GetValue<bool>(), packets[i].IsBinary);
            if (packets[i].IsBinary)
            {
                Assert.Equal(e["data"]?["_binary_b64"]?.GetValue<string>(), Convert.ToBase64String(packets[i].Binary ?? []));
            }
            else
            {
                Assert.Equal(e["data"]?.GetValue<string>(), packets[i].Text);
            }
        }
    }
}
