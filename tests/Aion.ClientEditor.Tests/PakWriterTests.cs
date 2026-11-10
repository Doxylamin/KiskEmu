using AionClientEditor.Patch;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Versions;
using Xunit;

namespace Aion.ClientEditor.Tests;

public class PakWriterTests
{
    // Synthetic key: tests never use the client's real archive key.
    static readonly FAesKey TestKey = new("0x" + string.Concat(Enumerable.Range(1, 32).Select(i => i.ToString("X2"))));

    [Fact]
    public void Written_pak_reads_back_with_CUE4Parse()
    {
        var dir = Directory.CreateTempSubdirectory("pakwriter-");
        try
        {
            var pakPath = Path.Combine(dir.FullName, "z_Test-Windows_P.pak");
            var random = new Random(42);
            byte[] Bytes(int n) { var b = new byte[n]; random.NextBytes(b); return b; }

            var payloads = new Dictionary<string, byte[]>
            {
                ["AION2/Content/Data/Table/NpcData.dat"] = Bytes(1000),
                ["AION2/Content/Data/Table/NpcTalk.dat"] = Bytes(16),
                ["AION2/Content/L10N/Text/en-US/L10NString.dat"] = Bytes(3),
                ["AION2/Content/Root.dat"] = Bytes(0),
            };
            PatchPakBuilder.WritePak(pakPath, payloads, TestKey);

            using var reader = new PakFileReader(pakPath, new VersionContainer(EGame.GAME_Aion2));
            Assert.Equal(EPakFileVersion.PakFile_Version_Fnv64BugFix, reader.Info.Version);
            Assert.True(reader.Info.EncryptedIndex);
            reader.AesKey = TestKey;
            reader.Mount(StringComparer.OrdinalIgnoreCase);

            Assert.EndsWith("Aion2/", reader.MountPoint, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(payloads.Count, reader.Files.Count);
            foreach (var (virtualPath, expected) in payloads)
            {
                var pakPathInReader = reader.MountPoint + virtualPath["AION2/".Length..];
                var file = reader.Files.Values.Single(f =>
                    string.Equals(f.Path, pakPathInReader, StringComparison.OrdinalIgnoreCase));
                Assert.True(file.IsEncrypted);
                Assert.Equal(expected, file.Read());
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Path_hash_matches_fnv64_reference()
    {
        // FNV-1a 64 of an empty input is the offset basis.
        Assert.Equal(0xcbf29ce484222325UL, MinimalPakWriter.PathHash("", 0));
        // Case-insensitive, as Unreal lower-cases before hashing.
        Assert.Equal(MinimalPakWriter.PathHash("content/a.dat", 7), MinimalPakWriter.PathHash("Content/A.dat", 7));
    }
}
