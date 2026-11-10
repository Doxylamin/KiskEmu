using AionClientEditor.Serialization;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Versions;
using Xunit;

namespace Aion.ClientEditor.Tests;

/// <summary>Byte formats OBSERVED in the client's own tables (checked against them by the CLI's round-trip guard).</summary>
public class DatWriterFormatTests
{
    static readonly VersionContainer Versions = new(EGame.GAME_Aion2);

    static byte[] Write(Action<FAion2DatFileWriter> write, bool unicode)
    {
        using var ms = new MemoryStream();
        using (var w = new FAion2DatFileWriter(ms, Versions) { UnicodeNames = unicode }) write(w);
        return ms.ToArray();
    }

    [Fact]
    public void Unicode_names_are_xored_utf16_with_plain_terminator()
    {
        var bytes = Write(w => w.WriteName("None"), unicode: true);
        // -5 chars, "None" XORed with 25 00 a8 00 7e 00 91 00, then 00 00.
        Assert.Equal(Convert.FromHexString("FBFFFFFF" + "6B00C7001000F400" + "0000"), bytes);
        Assert.Equal("None", new FAion2DatFileArchive(bytes, Versions).ReadFString());
    }

    [Fact]
    public void One_character_names_stay_ansi()
    {
        Assert.Equal(Convert.FromHexString("020000004100"), Write(w => w.WriteName("A"), unicode: true));
    }

    [Fact]
    public void Utf8_mode_is_unchanged()
    {
        Assert.Equal(Convert.FromHexString("050000004E6F6E6500"), Write(w => w.WriteName("None"), unicode: false));
    }
}
