using System.Buffers.Binary;
using Nanook.GrindCore;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// Writes a pair container: a base ROM and a second ROM base-patched against it, bundled
/// into one file with no external base needed to unpack either - confirmed against
/// `ndztool.py`'s own `cmd_pack`'s `--pair-out` (see docs/ndz-remaining-work.md's "Pair
/// container" section). Layout: `[16 KiB header][base .ndz blob][zero padding to a
/// 16 KiB boundary][target .ndz blob]` - the header holds
/// <c>[magic 'NDZP'][hdrSize=16384][nRoms=2][reserved]</c> then two
/// <see cref="NdzPairEntry"/> records at <see cref="NdzConstants.PairEntriesOffset"/>.
/// See <see cref="NdzPairContainer"/> for the read side.
/// </summary>
public static class NdzPairWriter
{
    /// <summary>
    /// Packs <paramref name="baseRom"/> standalone and <paramref name="targetRom"/>
    /// base-patched against it (see <see cref="NdzWriter.Compress"/>'s `baseRom`
    /// parameter), then bundles both into one pair container written to
    /// <paramref name="output"/>.
    /// </summary>
    public static void Write(Stream output, byte[] baseRom, byte[] targetRom,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize, bool enableFilters = true, int rawDictionarySize = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(baseRom);
        ArgumentNullException.ThrowIfNull(targetRom);
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        if (baseRom.Length < NdzConstants.NdsHeader.HeaderLength)
            throw new ArgumentException($"Base ROM is only {baseRom.Length} bytes; too small to be an .nds ROM.", nameof(baseRom));

        // rawDictionarySize applies to both sub-packs, matching ndztool.py's own
        // cmd_pack --pair-out (it passes the same raw_dict_size to both pack_ndz_blob
        // calls) - each ROM gets its own dictionary derived from its own content.
        using var baseStream = new MemoryStream();
        NdzWriter.Compress(baseRom, baseStream, level, blockSize, enableFilters, baseRom: null, rawDictionarySize);
        byte[] baseBlob = baseStream.ToArray();

        using var targetStream = new MemoryStream();
        NdzWriter.Compress(targetRom, targetStream, level, blockSize, enableFilters, baseRom: baseRom, rawDictionarySize);
        byte[] targetBlob = targetStream.ToArray();

        const int align = NdzConstants.FrontMatterSize;
        int offsetA = align;
        int padA = (align - baseBlob.Length % align) % align;
        int offsetB = offsetA + baseBlob.Length + padA;

        var header = new byte[align];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), NdzConstants.PairMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), align);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 2u);
        // bytes 12..16: reserved, left zero.

        uint baseGameCode = BinaryPrimitives.ReadUInt32LittleEndian(baseRom.AsSpan(NdzConstants.NdsHeader.GameCodeOffset, 4));
        uint targetGameCode = BinaryPrimitives.ReadUInt32LittleEndian(targetRom.AsSpan(NdzConstants.NdsHeader.GameCodeOffset, 4));
        WriteEntry(header, 0, (uint)offsetA, (uint)baseBlob.Length, (uint)baseRom.Length, baseGameCode);
        WriteEntry(header, 1, (uint)offsetB, (uint)targetBlob.Length, (uint)targetRom.Length, targetGameCode);

        output.Write(header);
        output.Write(baseBlob);
        if (padA > 0)
            output.Write(new byte[padA]);
        output.Write(targetBlob);
    }

    public static void WriteFile(string outputPath, string baseRomPath, string targetRomPath,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize, bool enableFilters = true, int rawDictionarySize = 0)
    {
        byte[] baseRom = File.ReadAllBytes(baseRomPath);
        byte[] targetRom = File.ReadAllBytes(targetRomPath);
        using var output = File.Create(outputPath);
        Write(output, baseRom, targetRom, level, blockSize, enableFilters, rawDictionarySize);
    }

    private static void WriteEntry(byte[] header, int index, uint blobOffset, uint size, uint originalSize, uint gameCode)
    {
        int offset = NdzConstants.PairEntriesOffset + index * NdzConstants.PairEntrySize;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset, 4), blobOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset + 4, 4), size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset + 8, 4), originalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset + 12, 4), gameCode);
    }
}
