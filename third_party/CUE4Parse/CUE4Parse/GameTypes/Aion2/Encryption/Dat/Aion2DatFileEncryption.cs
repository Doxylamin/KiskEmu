using System.Buffers.Binary;
using System.Text;
using Blake3;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Exceptions;

namespace CUE4Parse.GameTypes.Aion2.Encryption.Aes;

public sealed partial class Aion2DatFileEncryption
{
    private const ulong HeaderXorConstant = 0xCD02190910CE83F6;
    private static ulong _headerKeyRoot = Blake3Hash(HeaderXorConstant);
    private static ulong _headerPermutationRoot;
    private static bool _useHeaderPermutation;

    public static Dictionary<ulong, FAesKey> AesKeys = [];
    private static readonly Lock _instanceLock = new();

    public static void Initialize(IFileProvider provider)
    {
        if (AesKeys.Count != 0)
            return;

        var keyManifest = provider.Files.Values.FirstOrDefault(x => x.Path.EndsWith("/key_manifest.dat", StringComparison.OrdinalIgnoreCase))
            ?? throw new ParserException("Unable to locate key_manifest.dat");

        try
        {
            var manifestFile = new FAion2KeyManifestFile(keyManifest);
            lock (_instanceLock)
            {
                if (manifestFile.VersionContext is { } context)
                {
                    Span<byte> input = stackalloc byte[40];
                    BinaryPrimitives.WriteUInt64LittleEndian(input, HeaderXorConstant);
                    context.CopyTo(input[8..]);
                    _headerPermutationRoot = Blake3Hash(input);
                    _headerKeyRoot = Blake3Hash(_headerPermutationRoot);
                    _useHeaderPermutation = true;
                }
                else
                {
                    _headerPermutationRoot = 0;
                    _headerKeyRoot = Blake3Hash(HeaderXorConstant);
                    _useHeaderPermutation = false;
                }

                AesKeys = manifestFile.AesKeys;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to read key_manifest.dat");
            throw;
        }
    }

    private static byte[] DeriveHeaderKey(ulong seed, EHeaderType type)
    {
        Span<byte> input = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(input, _headerKeyRoot);
        BinaryPrimitives.WriteUInt64LittleEndian(input[8..], seed);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], (int) type);
        input[20..].Clear();
        using var hasher = Hasher.New();
        hasher.Update(input);

        return hasher.Finalize().AsSpan().ToArray();
    }

    private static ulong Blake3Hash(string value)
    {
        using var hasher = Hasher.New();
        hasher.Update(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadUInt64LittleEndian(hasher.Finalize().AsSpan());
    }

    private static ulong Blake3Hash(ulong value)
    {
        Span<byte> input = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(input, value);
        using var hasher = Hasher.New();
        hasher.Update(input);
        return BinaryPrimitives.ReadUInt64LittleEndian(hasher.Finalize().AsSpan());
    }

    private static ulong Blake3Hash(ReadOnlySpan<byte> value)
    {
        using var hasher = Hasher.New();
        hasher.Update(value);
        return BinaryPrimitives.ReadUInt64LittleEndian(hasher.Finalize().AsSpan());
    }

    /// <summary>
    /// KiskEmu: a versioned client's header fields (already XORed) in their shuffled order, drawn from
    /// BLAKE3(permutation root || 0x51) as for L10N headers.
    /// </summary>
    private static (EEncryptionType Type, int Raw, int Packed, int Aligned) ShuffledHeaderFields(ReadOnlySpan<byte> data)
    {
        var permutation = HeaderPermutation();
        Span<int> fields = stackalloc int[4];
        for (var i = 0; i < 4; i++) fields[permutation[i]] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]);
        return ((EEncryptionType) fields[0], fields[1], fields[2], fields[3]);
    }

    /// <summary>KiskEmu: the 16 header bytes (before the XOR) of a versioned client, in its shuffled order.</summary>
    private static byte[] ShuffledHeaderBytes(EEncryptionType type, int raw, int packed, int aligned)
    {
        var permutation = HeaderPermutation();
        Span<int> fields = [(int) type, raw, packed, aligned];
        var data = new byte[0x10];
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), fields[permutation[i]]);
        return data;
    }

    /// <summary>Serialized field i holds logical field permutation[i] (0 type, 1 raw, 2 packed, 3 aligned).</summary>
    private static byte[] HeaderPermutation()
    {
        byte[] permutation = [0, 1, 2, 3];
        Span<byte> input = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(input, _headerPermutationRoot);
        BinaryPrimitives.WriteInt32LittleEndian(input[8..], 0x51);
        using var hasher = Hasher.New();
        hasher.Update(input);
        var shuffle = hasher.Finalize().AsSpan();
        for (var i = 3; i > 0; i--)
        {
            var other = shuffle[3 - i] % (i + 1);
            (permutation[i], permutation[other]) = (permutation[other], permutation[i]);
        }

        return permutation;
    }

    private enum EHeaderType
    {
        CompressedDataTable = 1,
        StreamDataTable = 2,
        Localization = 3
    }

    private enum EEncryptionType : int
    {
        CompressedAES = 2,
        XorAES = 3
    }
}
