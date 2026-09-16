using System.Buffers.Binary;
using Nanook.GrindCore;
using Nanook.GrindCore.ZStd;
using Ndz.Core.Format;
using Ndz.Core.XDelta;

namespace Ndz.Core.Compression;

/// <summary>
/// Writes a <c>.delta.ndz</c> "hack" container (flags <see cref="NdzFlags.BasePatch"/> |
/// <see cref="NdzFlags.HackContainer"/>, bits 4+6): a full target ROM stored cheaply
/// against a base ROM's own already-packed <c>.ndz</c>, reproducing ndz-studio's "Pack
/// hack" feature functionally (not necessarily byte-identically - the block-matching/
/// candidate-selection heuristics here are this port's own, not ndz-studio's). See
/// docs/ndz-format-spec.md's "xdelta-based `.delta.ndz` / hack container" section for the
/// on-disk format this reproduces, reverse-engineered from real ndz-studio output.
///
/// A separate type from <see cref="NdzWriter"/> (matching the existing
/// <see cref="NdzPairWriter"/> precedent for a genuinely different outer shape), since the
/// frame-header layout (no <c>baseOff[n]</c> array despite <see cref="NdzFlags.BasePatch"/>
/// being set), base-identity fields (all zero here), and per-block candidate set (an
/// exact-match verbatim-copy candidate instead of a windowed-search one) all differ from
/// ordinary base-patch mode. Reuses <see cref="NdzWriter.CompressBlockCandidates"/> (the
/// Plain/Dict/filter search) and <see cref="NdzWriter.WriteTrailer"/> rather than
/// duplicating them.
/// </summary>
public static class HackContainerWriter
{
    /// <summary>
    /// A block that doesn't exactly match anywhere in <paramref name="baseRom"/> within
    /// this many hash-chain steps just isn't tried as <see cref="BlockMode.Verbatim"/> -
    /// not a failure, the per-block candidate search still tries Plain/Dict/filters.
    /// Deliberately deeper than <see cref="HashChainMatcher.DefaultMaxChainSteps"/> (tuned
    /// for <see cref="VcdiffEncoder"/>'s own greedy partial-match scanning): a full-length
    /// exact match is a stricter, rarer condition, worth searching harder for since a hit
    /// costs only 4 bytes on disk versus a full zstd candidate.
    /// </summary>
    private const int ExactMatchChainSteps = 512;

    /// <param name="targetRom">The full ROM this container reconstructs (already-decrypted .nds bytes) - e.g. a ROM hack, or another version of the same game.</param>
    /// <param name="baseRom">The base ROM <paramref name="targetRom"/> is packed against - used for <see cref="BlockMode.Verbatim"/>'s exact-match search. Never stored in the output; must be supplied again at decode time (as its own packed <paramref name="baseNdzBytes"/> - see <see cref="NdzArchive.Open"/>'s <c>baseNdzBytes</c> parameter).</param>
    /// <param name="baseNdzBytes">
    /// The base ROM's own already-packed <c>.ndz</c> - "the base .ndz the hack will attach
    /// to on the cart" (ndz-studio's own wording). Its front-matter's own <c>GameCode</c>
    /// field becomes this container's <c>GameCode</c> (so a reader/the cart knows which
    /// base this hack needs), and - if it was packed with a raw dictionary
    /// (<see cref="NdzFlags.RawDictionary"/>) - that dictionary becomes the source
    /// <see cref="BlockMode.Dict"/> blocks compress against (via
    /// <see cref="NdzArchive.ReadRawDictionary"/>). If the base has no raw dictionary,
    /// <see cref="BlockMode.Dict"/> is simply never used here - not an error, matching how
    /// <see cref="NdzWriter"/> itself already treats "no dictionary supplied" as a normal
    /// state (see <see cref="NdzWriter.CompressBlockCandidates"/>'s own remarks).
    /// </param>
    public static void Compress(
        byte[] targetRom, byte[] baseRom, byte[] baseNdzBytes, Stream output,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize,
        bool enableFilters = true, int frameSize = NdzConstants.FrameSize,
        int maxDegreeOfParallelism = -1)
    {
        ArgumentNullException.ThrowIfNull(targetRom);
        ArgumentNullException.ThrowIfNull(baseRom);
        ArgumentNullException.ThrowIfNull(baseNdzBytes);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Block size must be a positive power of two.");
        if (frameSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSize), frameSize, "Frame size must be positive.");
        if (frameSize < blockSize)
            throw new ArgumentOutOfRangeException(nameof(frameSize), frameSize, "Frame size must be >= block size.");
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

        var targetInfo = NdsRomInfo.FromRom(targetRom);
        var baseFrontMatter = NdzFrontMatter.Read(baseNdzBytes.AsSpan(0, NdzConstants.FrontMatterSize));
        byte[]? baseDict = NdzArchive.ReadRawDictionary(baseNdzBytes);

        NdzFlags flags = (NdzFlags.V2 | NdzFlags.ZStd | NdzFlags.Filters | NdzFlags.BasePatch | NdzFlags.HackContainer)
            .WithBlockSize(blockSize);

        var frontMatter = new NdzFrontMatter
        {
            OriginalSize = checked((uint)targetRom.Length),
            // The base's own game code, not the target's - so a reader/the cart knows
            // which base .ndz this hack needs (see NdzFlags.HackContainer's remarks).
            GameCode = baseFrontMatter.GameCode,
            Banner = targetInfo.Banner,
            Flags = flags,
            // No dictionary section of this file's own - Dict blocks draw from the BASE's
            // raw-dict blob (baseDict, above), not one derived from targetRom itself.
            DictionaryStoredSize = 0,
            DictionaryDecompressedSize = 0,
            // Zero, not the base's real size/gameCode/hash: this variant doesn't verify an
            // externally-supplied base .nds the way ordinary base-patch mode does - see
            // NdzFlags.HackContainer's remarks.
            BaseOriginalSize = 0,
            BaseGameCode = 0,
            BaseHeaderHash = new byte[NdzConstants.BaseHeaderHashLength],
        };

        var frontMatterBytes = new byte[NdzConstants.FrontMatterSize];
        frontMatter.WriteTo(frontMatterBytes);
        output.Write(frontMatterBytes);

        // One matcher over the whole base ROM, built once and reused for every block's
        // Verbatim search - the expensive one-time cost (O(baseRom.Length)), same model as
        // NdzWriter's own per-Compress-call BaseRomIndex.
        var matcher = new HashChainMatcher(baseRom, ExactMatchChainSteps);

        int frameCount = targetRom.Length == 0 ? 0 : (targetRom.Length + frameSize - 1) / frameSize;
        var frames = new byte[frameCount][];
        var seekTable = new SeekTableEntry[frameCount];
        var plainOptions = new CompressionOptions { Type = level, BlockSize = blockSize };
        var dictOptions = baseDict == null
            ? null
            : new CompressionOptions
            {
                Type = level,
                BlockSize = blockSize,
                InitProperties = baseDict,
                Dictionary = new CompressionDictionaryOptions { WindowBits = NdzWriter.ComputeDictionaryWindowBits(baseDict.Length, blockSize) },
            };

        Parallel.For(0, frameCount,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
            localInit: () => (Plain: new ZStdBlock(plainOptions), Dict: dictOptions == null ? null : new ZStdBlock(dictOptions)),
            body: (f, _, threadLocalBlocks) =>
            {
                int frameOffset = f * frameSize;
                int frameLength = Math.Min(frameSize, targetRom.Length - frameOffset);
                (frames[f], seekTable[f]) = CompressHackFrame(
                    targetRom, frameOffset, frameLength, f, threadLocalBlocks.Plain, threadLocalBlocks.Dict, blockSize, enableFilters, matcher);
                return threadLocalBlocks;
            },
            localFinally: threadLocalBlocks =>
            {
                threadLocalBlocks.Plain.Dispose();
                threadLocalBlocks.Dict?.Dispose();
            });

        foreach (byte[] frame in frames)
            output.Write(frame);

        NdzWriter.WriteTrailer(output, seekTable);
    }

    /// <summary>
    /// Convenience overload: reconstructs the target ROM from <paramref name="baseRom"/> +
    /// an existing standalone <c>.xdelta</c> patch via <see cref="XDeltaCodec.Apply"/>
    /// first (matching how ndz-studio's own "Pack hack" applies an uploaded patch
    /// page-side before packing - see <c>docs/xdelta-vcdiff-notes.md</c>), then calls
    /// <see cref="Compress"/> - both input shapes converge on the same packer.
    /// </summary>
    public static void CompressFromPatch(
        byte[] baseRom, byte[] xdeltaPatch, byte[] baseNdzBytes, Stream output,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize,
        bool enableFilters = true, int frameSize = NdzConstants.FrameSize,
        int maxDegreeOfParallelism = -1)
    {
        ArgumentNullException.ThrowIfNull(xdeltaPatch);
        byte[] targetRom = XDeltaCodec.Apply(baseRom, xdeltaPatch);
        Compress(targetRom, baseRom, baseNdzBytes, output, level, blockSize, enableFilters, frameSize, maxDegreeOfParallelism);
    }

    public static void CompressFile(
        string targetNdsPath, string baseNdsPath, string baseNdzPath, string outputDeltaNdzPath,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize,
        bool enableFilters = true, int frameSize = NdzConstants.FrameSize,
        int maxDegreeOfParallelism = -1)
    {
        byte[] targetRom = File.ReadAllBytes(targetNdsPath);
        byte[] baseRom = File.ReadAllBytes(baseNdsPath);
        byte[] baseNdzBytes = File.ReadAllBytes(baseNdzPath);
        using var output = File.Create(outputDeltaNdzPath);
        Compress(targetRom, baseRom, baseNdzBytes, output, level, blockSize, enableFilters, frameSize, maxDegreeOfParallelism);
    }

    public static void CompressFromPatchFile(
        string baseNdsPath, string xdeltaPatchPath, string baseNdzPath, string outputDeltaNdzPath,
        CompressionType level = NdzWriter.DefaultLevel, int blockSize = NdzConstants.BlockSize,
        bool enableFilters = true, int frameSize = NdzConstants.FrameSize,
        int maxDegreeOfParallelism = -1)
    {
        byte[] baseRom = File.ReadAllBytes(baseNdsPath);
        byte[] xdeltaPatch = File.ReadAllBytes(xdeltaPatchPath);
        byte[] baseNdzBytes = File.ReadAllBytes(baseNdzPath);
        using var output = File.Create(outputDeltaNdzPath);
        CompressFromPatch(baseRom, xdeltaPatch, baseNdzBytes, output, level, blockSize, enableFilters, frameSize, maxDegreeOfParallelism);
    }

    /// <summary>
    /// Compresses one frame into its private <c>[u32 csize × nblocks][u8 mode ×
    /// nblocks][compressed-or-4-byte-offset data]</c> layout - identical shape to a
    /// non-base-patch frame (no <c>baseOff[n]</c> array at all, matching the confirmed
    /// real format), except a <see cref="BlockMode.Verbatim"/> block's "compressed" slot
    /// holds a raw 4-byte little-endian base-ROM offset instead of zstd bytes.
    ///
    /// Verbatim is tried first and, on a hit, short-circuits the rest of the candidate
    /// search entirely (matches the real format's own heavy skew toward this mode - ~99%
    /// of blocks in a real sample - and avoids wasting up to 7 zstd calls per block for
    /// the dominant unchanged/relocated-content case). Otherwise falls through to
    /// <see cref="NdzWriter.CompressBlockCandidates"/> exactly like an ordinary
    /// non-base-patch frame - no windowed base-search candidate here at all, since
    /// Verbatim (arbitrary exact match) and Dict (against the base's own raw-dict) between
    /// them cover what that mechanism did in ordinary base-patch mode.
    /// </summary>
    private static (byte[] FrameBytes, SeekTableEntry Entry) CompressHackFrame(
        byte[] targetRom, int frameOffset, int frameLength, int frameIndex,
        ZStdBlock plainBlock, ZStdBlock? dictBlock, int blockSize, bool enableFilters, HashChainMatcher matcher)
    {
        int blockCount = (frameLength + blockSize - 1) / blockSize;
        int maxOutputSize = plainBlock.RequiredCompressOutputSize;
        byte[] bestDstBuffer = new byte[maxOutputSize];
        byte[] candidateDstBuffer = new byte[maxOutputSize];
        byte[] filterSrcBuffer = new byte[blockSize];

        var blockCsizes = new uint[blockCount];
        var blockModes = new byte[blockCount];
        using var blockData = new MemoryStream();
        Span<byte> offsetBytes = stackalloc byte[4];

        for (int b = 0; b < blockCount; b++)
        {
            int blockOffset = frameOffset + b * blockSize;
            int blockLength = Math.Min(blockSize, frameOffset + frameLength - blockOffset);

            if (matcher.FindExactMatch(targetRom, blockOffset, blockLength, out int sourcePos))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(offsetBytes, (uint)sourcePos);
                blockData.Write(offsetBytes);
                blockCsizes[b] = 4;
                blockModes[b] = (byte)BlockMode.Verbatim;
                continue;
            }

            BlockMode mode = NdzWriter.CompressBlockCandidates(
                targetRom, blockOffset, blockLength, blockSize, plainBlock, dictBlock, enableFilters, filterSrcBuffer,
                frameIndex, b, ref bestDstBuffer, ref candidateDstBuffer, out int bestCount);

            blockCsizes[b] = (uint)bestCount;
            blockModes[b] = (byte)mode;
            blockData.Write(bestDstBuffer, 0, bestCount);
        }

        var frameBytes = new byte[blockCount * 4 + blockCount + blockData.Length];
        for (int b = 0; b < blockCount; b++)
            BinaryPrimitives.WriteUInt32LittleEndian(frameBytes.AsSpan(b * 4, 4), blockCsizes[b]);
        blockModes.CopyTo(frameBytes.AsSpan(blockCount * 4, blockCount));
        Buffer.BlockCopy(blockData.GetBuffer(), 0, frameBytes, blockCount * 4 + blockCount, (int)blockData.Length);

        return (frameBytes, new SeekTableEntry((uint)frameBytes.Length, (uint)frameLength));
    }
}
