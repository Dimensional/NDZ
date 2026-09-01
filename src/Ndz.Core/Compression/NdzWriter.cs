using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nanook.GrindCore;
using Nanook.GrindCore.ZStd;
using Ndz.Core.Format;

[assembly: InternalsVisibleTo("Ndz.Core.Tests")]

namespace Ndz.Core.Compression;

/// <summary>
/// Compresses a raw, decrypted .nds image into the NDZ container: 16 KB front-matter,
/// an optional verbatim dictionary section, then 128 KB frames (each internally
/// subdivided into 8 KB blocks with a per-block mode byte, and - when a base ROM is
/// supplied - a per-block base-window offset too - see
/// <see cref="NdzConstants"/>/<see cref="BlockMode"/>/<see cref="Compression.BaseRomIndex"/>),
/// then a seek-table trailer.
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
    /// <param name="enableFilters">
    /// Try the five byte-transform filter modes (see <see cref="BlockMode"/>/
    /// <see cref="BlockFilters"/>) on every full block, keeping whichever of plain/dict/
    /// filter compresses smallest - mirrors `ndztool.py`'s own default (its
    /// `--no-filters` is opt-out, not opt-in). This is real, brute-force extra cost (up
    /// to 5 more zstd compress calls per full block, matching the reference exactly, not
    /// a shortcut around it) - set false to skip it if that cost isn't worth it for a
    /// given ROM. Either way the front-matter's `Filters` bit stays set and every block
    /// still gets a mode byte, since Plain vs. Dict alone already needs one - see
    /// <see cref="NdzFlags.Filters"/>'s remarks.
    /// </param>
    /// <param name="baseRom">
    /// A second, already-decrypted .nds to base-patch <paramref name="rom"/> against
    /// (see <see cref="NdzFlags.BasePatch"/>/<see cref="Compression.BaseRomIndex"/>) -
    /// windowed raw-dictionary compression against the base ROM's own content, not a
    /// binary diff. Every block (full or the frame's short trailing one - unlike
    /// filters, base-window search has no full-block restriction, matching
    /// `ndztool.py`'s own `compress_block_best`) is additionally tried against up to 8
    /// candidate 16 KiB windows into <paramref name="baseRom"/>; a win is tagged
    /// <see cref="BlockMode.Plain"/> (not a distinct mode value) with the window's
    /// offset recorded in a second per-block header array. The base ROM itself must be
    /// supplied again at decode time (<see cref="Compression.NdzArchive.Open"/>'s own
    /// <c>baseRom</c> parameter) - it is never stored in the output, only its size, game
    /// code, and an 8-byte BLAKE2b header hash, checked against whatever base is
    /// supplied at decode time.
    /// </param>
    /// <param name="rawDictionarySize">
    /// Target size (bytes) for a raw-content dictionary derived from <paramref name="rom"/>'s
    /// own repeated content (<see cref="Compression.RawDictionaryBuilder"/>) - the *only*
    /// way either reference implementation ever builds one (`pack.rs`'s own
    /// `pack(nds, raw_dict_size: usize, ...)`, `ndztool.py`'s `--raw-dict &lt;size&gt;`);
    /// there has never been a real "load an externally-supplied dictionary file" path in
    /// either, and this port no longer pretends otherwise (an earlier version accepted
    /// arbitrary external content here - removed 2026-08-31 once that was noticed).
    /// 0 (the default) means no dictionary. If the ROM doesn't actually have 4 KiB worth
    /// of useful duplicate content, no dictionary is stored even though this was
    /// nonzero - matches `ndztool.py`'s own `if len(raw_dict) >= 4096` gate exactly.
    /// </param>
    public static void Compress(byte[] rom, Stream output, CompressionType level = DefaultLevel, int blockSize = NdzConstants.BlockSize, bool enableFilters = true, byte[]? baseRom = null, int rawDictionarySize = 0)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        if (baseRom != null && baseRom.Length < NdzConstants.NdsHeader.HeaderLength)
            throw new ArgumentException($"Base ROM is only {baseRom.Length} bytes; too small to be an .nds ROM.", nameof(baseRom));
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

        byte[]? dictionaryContent = null;
        if (rawDictionarySize > 0)
        {
            byte[] derivedDictionary = RawDictionaryBuilder.Build(rom, rawDictionarySize);
            // Matches ndztool.py's own `if len(raw_dict) >= 4096: ... else: raw_dict = b""`
            // - not worth storing (or setting the flag for) a near-useless sliver.
            if (derivedDictionary.Length >= 4096)
            {
                dictionaryContent = derivedDictionary;
                flags |= NdzFlags.RawDictionary;
            }
        }

        BaseRomIndex? baseIndex = null;
        uint baseOriginalSize = 0, baseGameCode = 0;
        byte[] baseHeaderHash = new byte[NdzConstants.BaseHeaderHashLength];
        if (baseRom != null)
        {
            flags |= NdzFlags.BasePatch;
            baseIndex = new BaseRomIndex(baseRom);
            baseOriginalSize = checked((uint)baseRom.Length);
            baseGameCode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                baseRom.AsSpan(NdzConstants.NdsHeader.GameCodeOffset, 4));
            // BLAKE2b-8 of the base's own first 0x200 bytes (its header) - checked
            // against whatever base is supplied at decode time, see NdzArchive.Open.
            baseHeaderHash = Blake2b.Hash(baseRom.AsSpan(0, NdzConstants.NdsHeader.HeaderLength), NdzConstants.BaseHeaderHashLength);
        }

        var frontMatter = new NdzFrontMatter
        {
            OriginalSize = checked((uint)rom.Length),
            GameCode = romInfo.GameCode,
            Banner = romInfo.Banner,
            Flags = flags,
            // The reference packer stores its dictionary uncompressed, so stored == decompressed.
            DictionaryStoredSize = dictionaryContent == null ? 0u : checked((uint)dictionaryContent.Length),
            DictionaryDecompressedSize = dictionaryContent == null ? 0u : checked((uint)dictionaryContent.Length),
            BaseOriginalSize = baseOriginalSize,
            BaseGameCode = baseGameCode,
            BaseHeaderHash = baseHeaderHash,
        };

        var frontMatterBytes = new byte[NdzConstants.FrontMatterSize];
        frontMatter.WriteTo(frontMatterBytes);
        output.Write(frontMatterBytes);

        if (dictionaryContent != null)
            output.Write(dictionaryContent);

        int frameCount = rom.Length == 0 ? 0 : (rom.Length + NdzConstants.FrameSize - 1) / NdzConstants.FrameSize;
        var frames = new byte[frameCount][];
        var seekTable = new SeekTableEntry[frameCount];
        var plainOptions = new CompressionOptions { Type = level, BlockSize = blockSize };
        var dictOptions = dictionaryContent == null
            ? null
            : new CompressionOptions
            {
                Type = level,
                BlockSize = blockSize,
                InitProperties = dictionaryContent,
                // Forces the CDict's window to cover the whole dictionary + a block, matching
                // the reference packer's own wlog(dict.len() + BLOCK).max(15) exactly (GrindCore
                // 0.9.0+). Without this, ZSTD_createCDict()'s implicit sizing caps the window at
                // 8 MB for any dictionary over 256 KB at level 19 and never grows further,
                // silently losing match material beyond that for larger dictionaries - see
                // docs/ndz-format-spec.md's "Dictionary window sizing" section.
                Dictionary = new CompressionDictionaryOptions { WindowBits = ComputeDictionaryWindowBits(dictionaryContent.Length, blockSize) },
            };

        Parallel.For(0, frameCount,
            localInit: () => (Plain: new ZStdBlock(plainOptions), Dict: dictOptions == null ? null : new ZStdBlock(dictOptions)),
            body: (f, _, threadLocalBlocks) =>
            {
                int frameOffset = f * NdzConstants.FrameSize;
                int frameLength = Math.Min(NdzConstants.FrameSize, rom.Length - frameOffset);
                (frames[f], seekTable[f]) = CompressFrame(rom, frameOffset, frameLength, f, threadLocalBlocks.Plain, threadLocalBlocks.Dict, blockSize, enableFilters, baseIndex, level);
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

    public static void CompressFile(string inputNdsPath, string outputNdzPath, CompressionType level = DefaultLevel, int blockSize = NdzConstants.BlockSize, bool enableFilters = true, string? baseRomPath = null, int rawDictionarySize = 0)
    {
        byte[] rom = File.ReadAllBytes(inputNdsPath);
        byte[]? baseRom = baseRomPath == null ? null : File.ReadAllBytes(baseRomPath);
        using var output = File.Create(outputNdzPath);
        Compress(rom, output, level, blockSize, enableFilters, baseRom, rawDictionarySize);
    }

    /// <summary>
    /// log2 of a window big enough to cover the whole dictionary plus one block, so the
    /// compressor can reference any byte of the dictionary as match material - mirrors
    /// the reference packer's <c>wlog(dict.len() + BLOCK).max(15)</c> exactly. Clamped to
    /// GrindCore's own accepted range [10, 31] (it clamps internally too, but computing
    /// a value already in range avoids relying on that). Internal (not private) so
    /// <c>DictionaryWindowSizingTests</c> can check the arithmetic directly for
    /// dictionary sizes past the old 8 MiB ceiling, without needing to actually
    /// construct that much genuinely-duplicate ROM content through the public,
    /// size-only <see cref="RawDictionaryBuilder"/> API (only one representative copy
    /// of each unique repeated chunk is ever included, so reaching 8+ MiB of *derived*
    /// dictionary content for real would need hundreds of distinct duplicate chunks -
    /// impractical to engineer reliably in a test, whereas this calculation itself
    /// takes only a length).
    /// </summary>
    internal static int ComputeDictionaryWindowBits(int dictionaryLength, int blockSize)
    {
        long n = (long)dictionaryLength + blockSize;
        int bits = n <= 1 ? 0 : System.Numerics.BitOperations.Log2((ulong)(n - 1)) + 1;
        return Math.Clamp(Math.Max(bits, 15), 10, 31);
    }

    /// <summary>Delta filter modes tried per full block, paired with their stride - see <see cref="BlockFilters"/>.</summary>
    private static readonly (BlockMode Mode, int Stride)[] DeltaFilters =
    {
        (BlockMode.Delta1, 1),
        (BlockMode.Delta2, 2),
        (BlockMode.Delta4, 4),
    };

    /// <summary>Shuffle filter modes tried per full block, paired with their plane count - see <see cref="BlockFilters"/>.</summary>
    private static readonly (BlockMode Mode, int Planes)[] ShuffleFilters =
    {
        (BlockMode.Shuffle2, 2),
        (BlockMode.Shuffle4, 4),
    };

    /// <summary>
    /// Compresses one frame's worth of ROM bytes into its private
    /// <c>[u32 csize × nblocks][u8 mode × nblocks][u32 baseOff × nblocks][compressed
    /// block bytes]</c> layout (see the type-level remarks). Every block is compressed
    /// plain and, if a dictionary is active, also dictionary-primed; a *full* block (not
    /// the frame's possibly-short trailing one - see <see cref="BlockFilters"/>/
    /// <see cref="BlockMode"/>) is additionally tried through each of the five filter
    /// transforms via the plain codec (never dictionary-primed, matching `ndztool.py`'s
    /// own `compress_block_best`). If a base ROM is active, *every* block (no full-block
    /// restriction here) is also tried against each of its candidate base windows.
    /// Whichever candidate compresses smallest wins; a base-window win is tagged
    /// <see cref="BlockMode.Plain"/> (matching the reference exactly - the win is
    /// distinguished by its recorded `baseOff`, not a mode value) and every other
    /// candidate is tagged its own mode.
    /// </summary>
    private static (byte[] FrameBytes, SeekTableEntry Entry) CompressFrame(
        byte[] rom, int frameOffset, int frameLength, int frameIndex, ZStdBlock plainBlock, ZStdBlock? dictBlock, int blockSize, bool enableFilters, BaseRomIndex? baseIndex, CompressionType level)
    {
        int blockCount = (frameLength + blockSize - 1) / blockSize;
        int maxOutputSize = plainBlock.RequiredCompressOutputSize;
        // Deliberately sized from plainBlock, not dictBlock: dictBlock's own
        // RequiredCompressOutputSize is inflated to 1 << windowLog once WindowBits is set
        // (megabytes, not ~8 KB) - see ComputeDictionaryWindowBits's remarks. Every
        // candidate here (plain, dict, and every filter, which always compresses via
        // plainBlock) compresses the same BlockSize-bounded input, so this bound is safe
        // for all of them.
        //
        // Two equally-sized buffers, swapped by reference whenever a candidate beats the
        // current best, rather than one buffer per candidate mode - keeps the candidate
        // count (2 today, up to 7 with filters) cheap to extend without more allocation.
        byte[] bestDstBuffer = new byte[maxOutputSize];
        byte[] candidateDstBuffer = new byte[maxOutputSize];
        byte[] filterSrcBuffer = new byte[blockSize];

        var blockCsizes = new uint[blockCount];
        var blockModes = new byte[blockCount];
        var blockBaseOffs = baseIndex == null ? null : new uint[blockCount];
        using var blockData = new MemoryStream();

        for (int b = 0; b < blockCount; b++)
        {
            int blockOffset = frameOffset + b * blockSize;
            int blockLength = Math.Min(blockSize, frameOffset + frameLength - blockOffset);

            int bestCount = bestDstBuffer.Length;
            CompressionResultCode plainResult = plainBlock.Compress(rom, blockOffset, blockLength, bestDstBuffer, 0, ref bestCount);
            if (plainResult != CompressionResultCode.Success)
                throw new InvalidDataException($"ZStd compression failed on frame {frameIndex} block {b}: {plainResult}");
            BlockMode mode = BlockMode.Plain;

            if (dictBlock != null)
            {
                int candidateCount = candidateDstBuffer.Length;
                CompressionResultCode dictResult = dictBlock.Compress(rom, blockOffset, blockLength, candidateDstBuffer, 0, ref candidateCount);
                if (dictResult != CompressionResultCode.Success)
                    throw new InvalidDataException($"ZStd dictionary compression failed on frame {frameIndex} block {b}: {dictResult}");

                if (candidateCount < bestCount)
                {
                    (bestDstBuffer, candidateDstBuffer) = (candidateDstBuffer, bestDstBuffer);
                    bestCount = candidateCount;
                    mode = BlockMode.Dict;
                }
            }

            // Filters only ever apply to a FULL block - matches ndztool.py's own
            // `len(blk) == block_dsize` condition exactly (not merely "some power-of-two
            // length"), so a frame's short trailing block never qualifies and Shuffle's
            // plane split always divides evenly.
            if (enableFilters && blockLength == blockSize)
            {
                ReadOnlySpan<byte> blockSource = rom.AsSpan(blockOffset, blockLength);
                Span<byte> filterSrc = filterSrcBuffer.AsSpan(0, blockLength);

                foreach (var (filterMode, stride) in DeltaFilters)
                {
                    BlockFilters.DeltaForward(blockSource, filterSrc, stride);
                    TryFilterCandidate(filterMode, filterSrcBuffer, blockLength, plainBlock, frameIndex, b,
                        ref bestDstBuffer, ref candidateDstBuffer, ref bestCount, ref mode);
                }

                foreach (var (filterMode, planes) in ShuffleFilters)
                {
                    BlockFilters.ShuffleForward(blockSource, filterSrc, planes);
                    TryFilterCandidate(filterMode, filterSrcBuffer, blockLength, plainBlock, frameIndex, b,
                        ref bestDstBuffer, ref candidateDstBuffer, ref bestCount, ref mode);
                }
            }

            uint baseOff = BaseRomIndex.NoWindowSentinel;
            if (baseIndex != null)
            {
                // No full-block restriction here (unlike filters, above) - matches
                // ndztool.py's own compress_block_best, which tries base windows for
                // every block including the frame's short trailing one.
                ReadOnlySpan<byte> blockSource = rom.AsSpan(blockOffset, blockLength);
                foreach (int windowOffset in baseIndex.CandidateWindowOffsets(blockSource, blockOffset))
                {
                    byte[] window = baseIndex.GetWindow(windowOffset).ToArray();
                    // A fresh CDict per candidate, matching ndztool.py's own
                    // `zstd.ZstdCompressor(level=self.level, dict_data=d)` being built
                    // fresh per call in BaseCtx.compress_with_window - real, expected
                    // cost, not something to "optimize away" without re-confirming
                    // parity. No explicit WindowBits: a 16 KiB window is far under the
                    // implicit ~8 MiB ceiling that only matters for RawDictionary's much
                    // bigger dictionaries (see ComputeDictionaryWindowBits's remarks) -
                    // ndztool.py's own compress_with_window skips the override too.
                    using var candidateBlock = new ZStdBlock(new CompressionOptions { Type = level, BlockSize = blockSize, InitProperties = window });
                    // Expected to match plainBlock's own bound exactly - no explicit
                    // WindowBits is set here (unlike RawDictionary's much bigger
                    // dictionaries, see ComputeDictionaryWindowBits's remarks), and that
                    // override is specifically what inflates the bound elsewhere. Fail
                    // loudly rather than silently risk a too-small buffer if that
                    // assumption is ever wrong for some GrindCore version.
                    if (candidateBlock.RequiredCompressOutputSize > candidateDstBuffer.Length)
                    {
                        throw new InvalidDataException(
                            $"Base-window compressor's output bound ({candidateBlock.RequiredCompressOutputSize}) exceeds the " +
                            $"plain compressor's ({candidateDstBuffer.Length}) on frame {frameIndex} block {b} - unexpected, " +
                            "since no explicit window override is set for base windows.");
                    }

                    int windowCount = candidateDstBuffer.Length;
                    CompressionResultCode windowResult = candidateBlock.Compress(rom, blockOffset, blockLength, candidateDstBuffer, 0, ref windowCount);
                    if (windowResult != CompressionResultCode.Success)
                        throw new InvalidDataException($"ZStd base-window compression failed on frame {frameIndex} block {b} (offset 0x{windowOffset:X}): {windowResult}");

                    if (windowCount < bestCount)
                    {
                        (bestDstBuffer, candidateDstBuffer) = (candidateDstBuffer, bestDstBuffer);
                        bestCount = windowCount;
                        mode = BlockMode.Plain; // matches ndztool.py: a base-window win is never a distinct mode value.
                        baseOff = (uint)windowOffset;
                    }
                }
            }
            if (blockBaseOffs != null)
                blockBaseOffs[b] = baseOff;

            blockCsizes[b] = (uint)bestCount;
            blockModes[b] = (byte)mode;
            blockData.Write(bestDstBuffer, 0, bestCount);
        }

        int baseOffArraySize = blockBaseOffs == null ? 0 : blockCount * 4;
        var frameBytes = new byte[blockCount * 4 + blockCount + baseOffArraySize + blockData.Length];
        for (int b = 0; b < blockCount; b++)
            BinaryPrimitives.WriteUInt32LittleEndian(frameBytes.AsSpan(b * 4, 4), blockCsizes[b]);
        blockModes.CopyTo(frameBytes.AsSpan(blockCount * 4, blockCount));
        if (blockBaseOffs != null)
        {
            for (int b = 0; b < blockCount; b++)
                BinaryPrimitives.WriteUInt32LittleEndian(frameBytes.AsSpan(blockCount * 4 + blockCount + b * 4, 4), blockBaseOffs[b]);
        }
        Buffer.BlockCopy(blockData.GetBuffer(), 0, frameBytes, blockCount * 4 + blockCount + baseOffArraySize, (int)blockData.Length);

        return (frameBytes, new SeekTableEntry((uint)frameBytes.Length, (uint)frameLength));
    }

    /// <summary>Compresses one already-filtered candidate and, if it beats the current best, swaps it in.</summary>
    private static void TryFilterCandidate(
        BlockMode filterMode, byte[] filterSrcBuffer, int blockLength, ZStdBlock plainBlock, int frameIndex, int blockIndex,
        ref byte[] bestDstBuffer, ref byte[] candidateDstBuffer, ref int bestCount, ref BlockMode mode)
    {
        int candidateCount = candidateDstBuffer.Length;
        CompressionResultCode result = plainBlock.Compress(filterSrcBuffer, 0, blockLength, candidateDstBuffer, 0, ref candidateCount);
        if (result != CompressionResultCode.Success)
            throw new InvalidDataException($"ZStd filter compression failed on frame {frameIndex} block {blockIndex} ({filterMode}): {result}");

        if (candidateCount < bestCount)
        {
            (bestDstBuffer, candidateDstBuffer) = (candidateDstBuffer, bestDstBuffer);
            bestCount = candidateCount;
            mode = filterMode;
        }
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
