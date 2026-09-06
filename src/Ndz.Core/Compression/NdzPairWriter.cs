using System.Buffers.Binary;
using Nanook.GrindCore;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// Writes a pair (or, since 2026-09-06, N-way) container: one self-contained base ROM
/// plus one or more other ROMs, each base-patched against that same base, bundled into
/// one file with no external base needed to unpack any of them - a star topology (every
/// non-base entry patches against the one shared base, never against each other).
/// Two-ROM shape confirmed against `ndztool.py`'s own `cmd_pack`'s `--pair-out` (see
/// docs/ndz-remaining-work.md's "Pair container" section); the N-ROM generalization is
/// NOT something either reference tool's own pack path ever does (`ndztool.py`'s
/// `cmd_pack` hardcodes `n_roms=2`) - but it needs no new wire-format bits, since the
/// container header's own `nRoms` field, and both `ndztool.py`'s and this project's
/// decode-side (<see cref="NdzPairContainer"/>) entry-parsing loops, were already fully
/// generic. Layout: `[16 KiB header][entry 0 blob][padding to a 16 KiB boundary]
/// [entry 1 blob][padding]...` - the header holds
/// <c>[magic 'NDZP'][hdrSize=16384][nRoms][reserved]</c> then one
/// <see cref="NdzPairEntry"/> record per ROM at <see cref="NdzConstants.PairEntriesOffset"/>
/// (entry 0 is always the self-contained base; every other entry is base-patched against
/// it). See <see cref="NdzPairContainer"/> for the read side.
/// </summary>
public static class NdzPairWriter
{
    /// <summary>
    /// Packs <paramref name="baseRom"/> standalone and each of <paramref name="targetRoms"/>
    /// base-patched against it (see <see cref="NdzWriter.Compress"/>'s `baseRom`
    /// parameter) - a star topology, not a chain: every target patches against the same
    /// shared base, never against another target. Which ROM among a family of similar
    /// ones (e.g. several regional/version releases of the same game) gets to be the
    /// base doesn't meaningfully matter - base-patch's own per-block search
    /// (<see cref="BaseRomIndex"/>) finds matching content wherever it is in the base, so
    /// the dedup a pair achieves is roughly symmetric regardless of which side is "base"
    /// (confirmed on the real Pokemon Black/White pair - either direction gave
    /// comparable results). There's also no check for, or restriction against, packing
    /// unrelated ROMs together (different games entirely) - base-patch just finds little
    /// or no matching content in that case and each target's blocks fall back to plain/
    /// self-dictionary compression, exactly as if patched against nothing; safe, just not
    /// beneficial.
    /// </summary>
    /// <param name="targetDictionarySizes">
    /// Per-target dictionary size override, if different from <paramref name="rawDictionarySize"/>
    /// (which is always used for the base, and for any target whose own entry here is
    /// null or when this whole parameter is left null) - must have exactly one entry per
    /// <paramref name="targetRoms"/> if supplied. Defaulting every ROM to the same size
    /// matches `ndztool.py`'s own `cmd_pack --pair-out` exactly (it passes one
    /// `raw_dict_size` to every `pack_ndz_blob` call) - neither reference has any notion
    /// of choosing them independently. This override exists only for the CLI's own
    /// `--raw-dict auto` (see <see cref="DictionaryAnalyzer"/>), which found real savings
    /// left on the table forcing one shared size on every entry - a dictionary on an
    /// already-near-perfectly-base-patched target competes with an excellent base-window
    /// match and mostly just adds its own storage cost, while the same size helps the
    /// self-contained base a lot (confirmed on a real Pokemon Black/White pair - see
    /// docs/ndz-remaining-work.md).
    /// </param>
    public static void Write(Stream output, byte[] baseRom, IReadOnlyList<byte[]> targetRoms,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize, bool enableFilters = true, int rawDictionarySize = 0, int frameSize = NdzConstants.FrameSize, IReadOnlyList<int?>? targetDictionarySizes = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(baseRom);
        ArgumentNullException.ThrowIfNull(targetRoms);
        if (targetRoms.Count == 0)
            throw new ArgumentException("At least one target ROM is required.", nameof(targetRoms));
        if (targetDictionarySizes != null && targetDictionarySizes.Count != targetRoms.Count)
            throw new ArgumentException("targetDictionarySizes must have exactly one entry per target ROM (or be null).", nameof(targetDictionarySizes));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        if (baseRom.Length < NdzConstants.NdsHeader.HeaderLength)
            throw new ArgumentException($"Base ROM is only {baseRom.Length} bytes; too small to be an .nds ROM.", nameof(baseRom));

        const int align = NdzConstants.FrontMatterSize;
        int entryCount = 1 + targetRoms.Count;
        // The header itself is exactly one aligned block (align bytes) - entries have to
        // fit in that same block ahead of where blob data starts, or they'd collide with
        // entry 0's own blob. 1023 ROMs' worth of headroom at the current 16-byte entry
        // size, but a real error beats silent corruption if this is ever pushed that far.
        int neededHeaderBytes = NdzConstants.PairEntriesOffset + entryCount * NdzConstants.PairEntrySize;
        if (neededHeaderBytes > align)
        {
            throw new ArgumentException(
                $"{entryCount} ROMs need {neededHeaderBytes} header bytes for their entry records, " +
                $"more than the {align}-byte header holds.", nameof(targetRoms));
        }

        var roms = new byte[entryCount][];
        roms[0] = baseRom;
        for (int i = 0; i < targetRoms.Count; i++)
            roms[i + 1] = targetRoms[i];

        var blobs = new byte[entryCount][];
        using (var baseStream = new MemoryStream())
        {
            NdzWriter.Compress(baseRom, baseStream, level, blockSize, enableFilters, baseRom: null, rawDictionarySize, frameSize);
            blobs[0] = baseStream.ToArray();
        }
        for (int i = 0; i < targetRoms.Count; i++)
        {
            int dictSize = targetDictionarySizes?[i] ?? rawDictionarySize;
            using var targetStream = new MemoryStream();
            NdzWriter.Compress(targetRoms[i], targetStream, level, blockSize, enableFilters, baseRom: baseRom, dictSize, frameSize);
            blobs[i + 1] = targetStream.ToArray();
        }

        var header = new byte[align];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), NdzConstants.PairMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), align);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)entryCount);
        // bytes 12..16: reserved, left zero.

        var offsets = new int[entryCount];
        var pads = new int[entryCount];
        int cursor = align;
        for (int i = 0; i < entryCount; i++)
        {
            offsets[i] = cursor;
            pads[i] = (align - blobs[i].Length % align) % align;
            cursor += blobs[i].Length + pads[i];
        }

        for (int i = 0; i < entryCount; i++)
        {
            uint gameCode = BinaryPrimitives.ReadUInt32LittleEndian(roms[i].AsSpan(NdzConstants.NdsHeader.GameCodeOffset, 4));
            WriteEntry(header, i, (uint)offsets[i], (uint)blobs[i].Length, (uint)roms[i].Length, gameCode);
        }

        output.Write(header);
        for (int i = 0; i < entryCount; i++)
        {
            output.Write(blobs[i]);
            if (pads[i] > 0)
                output.Write(new byte[pads[i]]);
        }
    }

    /// <summary>Single-target convenience overload - see the <see cref="Write(Stream, byte[], IReadOnlyList{byte[]}, CompressionType, int, bool, int, int, IReadOnlyList{int?}?)"/> overload for the general (and star-topology) case.</summary>
    public static void Write(Stream output, byte[] baseRom, byte[] targetRom,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize, bool enableFilters = true, int rawDictionarySize = 0, int frameSize = NdzConstants.FrameSize, int? targetDictionarySize = null)
    {
        ArgumentNullException.ThrowIfNull(targetRom);
        Write(output, baseRom, new[] { targetRom }, level, blockSize, enableFilters, rawDictionarySize, frameSize, new[] { targetDictionarySize });
    }

    public static void WriteFile(string outputPath, string baseRomPath, IReadOnlyList<string> targetRomPaths,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize, bool enableFilters = true, int rawDictionarySize = 0, int frameSize = NdzConstants.FrameSize, IReadOnlyList<int?>? targetDictionarySizes = null)
    {
        ArgumentNullException.ThrowIfNull(targetRomPaths);
        byte[] baseRom = File.ReadAllBytes(baseRomPath);
        byte[][] targetRoms = targetRomPaths.Select(File.ReadAllBytes).ToArray();
        using var output = File.Create(outputPath);
        Write(output, baseRom, targetRoms, level, blockSize, enableFilters, rawDictionarySize, frameSize, targetDictionarySizes);
    }

    public static void WriteFile(string outputPath, string baseRomPath, string targetRomPath,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize, bool enableFilters = true, int rawDictionarySize = 0, int frameSize = NdzConstants.FrameSize, int? targetDictionarySize = null)
    {
        byte[] baseRom = File.ReadAllBytes(baseRomPath);
        byte[] targetRom = File.ReadAllBytes(targetRomPath);
        using var output = File.Create(outputPath);
        Write(output, baseRom, targetRom, level, blockSize, enableFilters, rawDictionarySize, frameSize, targetDictionarySize);
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
