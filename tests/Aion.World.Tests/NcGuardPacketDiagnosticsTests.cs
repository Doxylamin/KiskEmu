using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class NcGuardPacketDiagnosticsTests
{
    static byte[] Specimen(bool reply)
    {
        var payload = new byte[128];
        Array.Fill(payload, (byte)'!');
        var blob = Convert.FromHexString("1400000000000e001800040008000c00100014000e000000")
            .Concat("NONCE123"u8.ToArray())
            .Concat(BitConverter.GetBytes(176 - 32))
            .Concat(BitConverter.GetBytes(120))
            .Concat(BitConverter.GetBytes(4))
            .Concat(BitConverter.GetBytes(128))
            .Concat(payload)
            .Concat(BitConverter.GetBytes(32))
            .Concat(new byte[32].Select((_, i) => (byte)(i + (byte)'!')).ToArray())
            .ToArray();
        var w = new PacketWriter();
        if (reply) w.U16(0xe25a).U16(0);
        else w.U16(0xe259);
        w.Varint(blob.Length);
        w.Raw(blob);
        return w.ToArray();
    }

    [Fact]
    public void Summarize_e259_reports_layout_without_secrets()
    {
        var body = Specimen(reply: false);
        var fields = NcGuardPacketDiagnostics.Summarize(body, 0xe259);
        Assert.Equal("observed_five_field_layout", fields["envelope_shape"]);
        Assert.Equal(128, fields["payload_bytes"]);
        Assert.True((bool)fields["block_rounding_matches"]!);
        Assert.Equal(body.Length, fields["length"]);
        Assert.DoesNotContain("NONCE", string.Join(' ', fields.Values));
    }

    [Fact]
    public void Summarize_e25a_includes_result_candidate()
    {
        var fields = NcGuardPacketDiagnostics.Summarize(Specimen(reply: true), 0xe25a);
        Assert.Equal(0, Convert.ToInt32(fields["result_candidate"]));
        Assert.Equal("observed_five_field_layout", fields["envelope_shape"]);
    }
}
