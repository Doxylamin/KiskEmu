using System.Buffers.Binary;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Metadata-only summaries for NCGuard game packets (0xe259 / 0xe25a); no opaque bytes or secrets. Mirrors
/// tools/protocol_knowledge.py; from A2Core-ChanAge (Artezianec).</summary>
public static class NcGuardPacketDiagnostics
{
    static readonly byte[] ObservedHeaderPrefix =
        Convert.FromHexString("1400000000000e001800040008000c00100014000e000000");

    public static IReadOnlyDictionary<string, object?> Summarize(ReadOnlySpan<byte> body, ushort opcode)
    {
        var fields = new Dictionary<string, object?>
        {
            ["opcode"] = $"0x{opcode:x4}",
            ["length"] = body.Length,
        };
        if (body.Length < 2)
        {
            fields["envelope_shape"] = "truncated";
            return fields;
        }
        if (opcode == 0xe25a && body.Length >= 4)
            fields["result_candidate"] = BinaryPrimitives.ReadUInt16LittleEndian(body[2..4]);
        var varintStart = opcode == 0xe25a ? 4 : 2;
        if (body.Length <= varintStart)
        {
            fields["envelope_shape"] = "unrecognized";
            return fields;
        }
        try
        {
            var reader = new PacketReader(body.ToArray()) { Offset = varintStart };
            var size = checked((int)reader.Varint());
            var start = reader.Offset;
            fields["vector_bytes"] = size;
            fields["outer_length_valid"] = size == body.Length - start;
            if (size != body.Length - start)
            {
                fields["envelope_shape"] = "unrecognized";
                return fields;
            }
            var blob = body[start..];
            if (size < 84 || !blob[..24].SequenceEqual(ObservedHeaderPrefix))
            {
                fields["envelope_shape"] = "unrecognized";
                return fields;
            }
            var payloadAt = 40 + BinaryPrimitives.ReadInt32LittleEndian(blob[40..44]);
            var tagAt = 32 + BinaryPrimitives.ReadInt32LittleEndian(blob[32..36]);
            if (payloadAt != 44 || tagAt < 48 || tagAt > size - 36)
            {
                fields["envelope_shape"] = "unrecognized";
                return fields;
            }
            var payloadSize = BinaryPrimitives.ReadInt32LittleEndian(blob[44..48]);
            var tagSize = BinaryPrimitives.ReadInt32LittleEndian(blob[tagAt..(tagAt + 4)]);
            if (tagAt != 48 + payloadSize || tagSize != 32 || tagAt + 36 != size)
            {
                fields["envelope_shape"] = "unrecognized";
                return fields;
            }
            var candidateLength = BinaryPrimitives.ReadInt32LittleEndian(blob[36..40]);
            fields["envelope_shape"] = "observed_five_field_layout";
            fields["payload_bytes"] = payloadSize;
            fields["trailing_vector_bytes"] = tagSize;
            fields["candidate_unpadded_bytes"] = candidateLength;
            fields["block_rounding_matches"] = ((candidateLength + 15) / 16) * 16 == payloadSize;
        }
        catch (Exception)
        {
            fields["envelope_shape"] = "unrecognized";
        }
        return fields;
    }
}
