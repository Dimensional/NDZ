using System.Buffers.Binary;
using Nanook.GrindCore;
using Nanook.GrindCore.ZStd;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// Compresses a raw, decrypted .nds image into the NDZ container: 16 KB front-matter,
/// an optional verbatim dictionary section, then 128 KB frames (each internally
/// subdivided into 8 KB blocks with a per-block mode byte - see
/// <see cref="NdzConstants"/>/<see cref="BlockMode"/>), then a seek-table trailer. No
/// "filter" transforms yet (see docs/ndz-format-spec.md) and no base-ROM patch mode
/// either - see <see cref="NdzFlags"/>.
///
/// Frames are independent (dictionary state aside, which is read-only/shared - see
/// below), so they're compressed in parallel - one pair of <see cref="ZStdBlock"/>s per
/// worker thread, mirroring the reference packer's own per-thread-group compressor -
/// then written to <paramref name="output"/> in order afterward, so the resulting file
/// is identical regardless of how many threads did the work.
/// </summary>
public static class NdzWriter
{
    /// <summary>Default level: near-max ZStd effort. Blocks are only 8 KB, so a high level's
    /// extra search effort is cheap and still worth it even without cross-block history.</summary>
    public const CompressionType DefaultLevel = CompressionType.Level19;

    /// <param name="blockSize">
    /// Per-block size in bytes, must be a power of two. Defaults to
    /// <see cref="NdzConstants.BlockSize"/> (8192, matching the reference packer's own
    /// fixed choice) - block size is a real per-file variable in the format (see
    /// <see cref="NdzFlagsExtensions"/>'s remarks), not something every writer must fix.
    /// </param>
    public static void Compress(byte[] rom, Stream output, CompressionType level = DefaultLevel, NdzDictionary? dictionary = null, int blockSize = NdzConstants.BlockSize)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Block size must be a positive power of two.");
        // Hardware ceilings, not preferences - see NdzConstants.MaxBlockSize/MaxLevel's
        // remarks. ndztool.py refuses to pack past either rather than silently produce a
        // file that "packs fine and then fails on real hardware"; matched here. The level
        // check only applies to zstd's own numeric levels (0-22) - GrindCore's
        // CompressionType also has negative meta-values (Fastest/Optimal/SmallestSize/
        // Decompress) with codec-dependent interpretations this project has no reference
        // behavior for, so they pass through unchecked rather than guessed at.
        if (blockSize > NdzConstants.MaxBlockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                $"Block size exceeds the hardware limit of {NdzConstants.MaxBlockSize} bytes - " +
                "bigger blocks take too long to fetch and decompress on a cache miss and the console freezes.");
        }
        if ((int)level > NdzConstants.MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level,
                $"Compression level exceeds the hardware limit of {NdzConstants.MaxLevel} - " +
                "higher levels decompress too slowly for the console to keep up.");
        }

        var romInfo = NdsRomInfo.FromRom(rom);
        // Filters must be set whenever a per-block mode array is written, which is
        // always here (CompressFrame always writes one, even Plain-only) - confirmed
        // against pack.rs, which sets this unconditionally with its own comment
        // ("ndz_studio always packs with filters on") for the exact same reason. Without
        // it, a real reader assumes NO mode array exists and misparses every frame -
        // confirmed empirically against ndztool.py before this fix (see
        // docs/ndz-format-spec.md's "Per-block compression mode" / "Filter modes").
        NdzFlags flags = (NdzFlags.V2 | NdzFlags.ZStd | NdzFlags.Filters).WithBlockSize(blockSize);
        if (dictionary != null)
            flags |= NdzFlags.RawDictionary;

        var frontMatter = new NdzFrontMatter
        {
            OriginalSize = checked((uint)rom.Length),
            GameCode = romInfo.GameCode,
            Banner = romInfo.Banner,
            Flags = flags,
            // The reference packer stores its dictionary uncompressed, so stored == decompressed.
            DictionaryStoredSize = dictionary == null ? 0u : checked((uint)dictionary.Content.Length),
            DictionaryDecompressedSize = dictionary == null ? 0u : checked((uint)dictionary.Content.Length),
        };

        var frontMatterBytes = new byte[NdzConstants.FrontMatterSize];
        frontMatter.WriteTo(frontMatterBytes);
        output.Write(frontMatterBytes);

        if (dictionary != null)
            output.Write(dictionary.Content);

        int frameCount = rom.Length == 0 ? 0 : (rom.Length + NdzConstants.FrameSize - 1) / NdzConstants.FrameSize;
        var frames = new byte[frameCount][];
        var seekTable = new SeekTableEntry[frameCount];
        var plainOptions = new CompressionOptions { Type = level, BlockSize = blockSize };
        var dictOptions = dictionary == null
            ? null
            : new CompressionOptions
            {
                Type = level,
                BlockSize = blockSize,
                InitProperties = dictionary.Content,
                // Forces the CDict's window to cover the whole dictionary + a block, matching
                // the reference packer's own wlog(dict.len() + BLOCK).max(15) exactly (GrindCore
                // 0.9.0+). Without this, ZSTD_createCDict()'s implicit sizing caps the window at
                // 8 MB for any dictionary over 256 KB at level 19 and never grows further,
                // silently losing match material beyond that for larger dictionaries - see
                // docs/ndz-format-spec.md's "Dictionary window sizing" section.
                Dictionary = new CompressionDictionaryOptions { WindowBits = ComputeDictionaryWindowBits(dictionary.Content.Length, blockSize) },
            };

        Parallel.For(0, frameCount,
            localInit: () => (Plain: new ZStdBlock(plainOptions), Dict: dictOptions == null ? null : new ZStdBlock(dictOptions)),
            body: (f, _, threadLocalBlocks) =>
            {
                int frameOffset = f * NdzConstants.FrameSize;
                int frameLength = Math.Min(NdzConstants.FrameSize, rom.Length - frameOffset);
                (frames[f], seekTable[f]) = CompressFrame(rom, frameOffset, frameLength, f, threadLocalBlocks.Plain, threadLocalBlocks.Dict, blockSize);
                return threadLocalBlocks;
            },
            localFinally: threadLocalBlocks =>
            {
                threadLocalBlocks.Plain.Dispose();
                threadLocalBlocks.Dict?.Dispose();
            });

        foreach (byte[] frame in frames)
            output.Write(frame);

        WriteTrailer(output, seekTable);
    }

    public static void CompressFile(string inputNdsPath, string outputNdzPath, CompressionType level = DefaultLevel, NdzDictionary? dictionary = null, int blockSize = NdzConstants.BlockSize)
    {
        byte[] rom = File.ReadAllBytes(inputNdsPath);
        using var output = File.Create(outputNdzPath);
        Compress(rom, output, level, dictionary, blockSize);
    }

    /// <summary>
    /// log2 of a window big enough to cover the whole dictionary plus one block, so the
    /// compressor can reference any byte of the dictionary as match material - mirrors
    /// the reference packer's <c>wlog(dict.len() + BLOCK).max(15)</c> exactly. Clamped to
    /// GrindCore's own accepted range [10, 31] (it clamps internally too, but computing
    /// a value already in range avoids relying on that).
    /// </summary>
    private static int ComputeDictionaryWindowBits(int dictionaryLength, int blockSize)
    {
        long n = (long)dictionaryLength + blockSize;
        int bits = n <= 1 ? 0 : System.Numerics.BitOperations.Log2((ulong)(n - 1)) + 1;
        return Math.Clamp(Math.Max(bits, 15), 10, 31);
    }

    /// <summary>
    /// Compresses one frame's worth of ROM bytes into its private
    /// <c>[u32 csize × nblocks][u8 mode × nblocks][compressed block bytes]</c> layout
    /// (see the type-level remarks). Each block is compressed plain and, if a dictionary
    /// is active, also dictionary-primed - whichever is smaller wins, tagged accordingly
    /// (mirrors the reference packer's own per-block best-of comparison, minus the
    /// filter-transform candidates it also tries - not implemented here).
    /// </summary>
    private static (byte[] FrameBytes, SeekTableEntry Entry) CompressFrame(
        byte[] rom, int frameOffset, int frameLength, int frameIndex, ZStdBlock plainBlock, ZStdBlock? dictBlock, int blockSize)
    {
        int blockCount = (frameLength + blockSize - 1) / blockSize;
        byte[] plainDstBuffer = new byte[plainBlock.RequiredCompressOutputSize];
        // Deliberately sized from plainBlock, not dictBlock: dictBlock's own
        // RequiredCompressOutputSize is inflated to 1 << windowLog once WindowBits is set
        // (megabytes, not ~8 KB) - see ComputeDictionaryWindowBits's remarks. Both blocks
        // compress the same BlockSize-bounded input, so plainBlock's correctly-sized bound
        // is safe for either.
        byte[]? dictDstBuffer = dictBlock == null ? null : new byte[plainBlock.RequiredCompressOutputSize];

        var blockCsizes = new uint[blockCount];
        var blockModes = new byte[blockCount];
        using var blockData = new MemoryStream();

        for (int b = 0; b < blockCount; b++)
        {
            int blockOffset = frameOffset + b * blockSize;
            int blockLength = Math.Min(blockSize, frameOffset + frameLength - blockOffset);

            int plainDstCount = plainDstBuffer.Length;
            CompressionResultCode plainResult = plainBlock.Compress(rom, blockOffset, blockLength, plainDstBuffer, 0, ref plainDstCount);
            if (plainResult != CompressionResultCode.Success)
                throw new InvalidDataException($"ZStd compression failed on frame {frameIndex} block {b}: {plainResult}");

            byte[] bestBuffer = plainDstBuffer;
            int bestCount = plainDstCount;
            byte mode = (byte)BlockMode.Plain;

            if (dictBlock != null)
            {
                int dictDstCount = dictDstBuffer!.Length;
                CompressionResultCode dictResult = dictBlock.Compress(rom, blockOffset, blockLength, dictDstBuffer, 0, ref dictDstCount);
                if (dictResult != CompressionResultCode.Success)
                    throw new InvalidDataException($"ZStd dictionary compression failed on frame {frameIndex} block {b}: {dictResult}");

                if (dictDstCount < bestCount)
                {
                    bestBuffer = dictDstBuffer;
                    bestCount = dictDstCount;
                    mode = (byte)BlockMode.Dict;
                }
            }

            blockCsizes[b] = (uint)bestCount;
            blockModes[b] = mode;
            blockData.Write(bestBuffer, 0, bestCount);
        }

        var frameBytes = new byte[blockCount * 4 + blockCount + blockData.Length];
        for (int b = 0; b < blockCount; b++)
            BinaryPrimitives.WriteUInt32LittleEndian(frameBytes.AsSpan(b * 4, 4), blockCsizes[b]);
        blockModes.CopyTo(frameBytes.AsSpan(blockCount * 4, blockCount));
        Buffer.BlockCopy(blockData.GetBuffer(), 0, frameBytes, blockCount * 4 + blockCount, (int)blockData.Length);

        return (frameBytes, new SeekTableEntry((uint)frameBytes.Length, (uint)frameLength));
    }

    private static void WriteTrailer(Stream output, SeekTableEntry[] seekTable)
    {
        Span<byte> entry = stackalloc byte[NdzConstants.SeekTableEntrySize];
        foreach (var e in seekTable)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entry[..4], e.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], e.DecompressedSize);
            output.Write(entry);
        }

        Span<byte> footer = stackalloc byte[NdzConstants.TrailerFooterSize];
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], (uint)seekTable.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[4..], NdzConstants.TrailerMagic);
        output.Write(footer);
    }
}
