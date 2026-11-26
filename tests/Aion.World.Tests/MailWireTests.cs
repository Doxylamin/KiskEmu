using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>Common_Post on the wire against the official LoadPost_RS of 2026-11-15 (i=833, one reward post; #70).</summary>
public sealed class MailWireTests
{
    const string OfficialPost =
        "01021e000000df020000000029052b7b225f706172616d73223a5b7b225f696e646578223a302c225f76616c7565223a223130303435227d5d7d03"
        + "4fdaa491a10100004fa2232ca20100000140da051e010000000000000000000000000000005901";

    static PostRecord Official(string parameters) => new(
        Id: 0x05290000000002df, Type: 1, Tab: 2, DataId: 30, Params: parameters, Read: true, Taken: true,
        Sent: DateTimeOffset.FromUnixTimeMilliseconds(0x01a191a4da4f), Expire: DateTimeOffset.FromUnixTimeMilliseconds(0x01a22c23a24f),
        Attachments: [(0x1e05da40, 1)], Reason: 0x0159);

    [Fact]
    public void Post_matches_the_official_bytes() =>
        Assert.Equal(Convert.FromHexString(OfficialPost), GameSession.WritePost(new PacketWriter(), Official("10045")).ToArray());

    [Fact]
    public void Stored_parameters_become_the_official_json()
    {
        Assert.Equal("{\"_params\":[{\"_index\":0,\"_value\":\"10045\"}]}", GameSession.PostParamsJson("10045"));
        Assert.Equal("{\"_params\":[{\"_index\":0,\"_value\":\"Hi\"},{\"_index\":1,\"_value\":\"A \\u0022quoted\\u0022 body\"}]}",
            GameSession.PostParamsJson("Hi|A \"quoted\" body"));
        Assert.Equal("{\"_params\":[]}", GameSession.PostParamsJson(""));
        Assert.Equal("{\"_params\":[{\"_index\":0,\"_value\":\"x\"}]}", GameSession.PostParamsJson("{\"_params\":[{\"_index\":0,\"_value\":\"x\"}]}"));
    }
}
