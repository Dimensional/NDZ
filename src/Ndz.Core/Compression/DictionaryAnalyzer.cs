using Nanook.GrindCore;
using Nanook.GrindCore.ZStd;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// Estimates, for a size ladder of candidate raw dictionaries, roughly how big the
/// resulting .ndz would be - so a caller can pick a dictionary size (or let
/// <see cref="Analyze"/> pick one for them) without paying for a full real pack at every
/// candidate size first.
///
/// This is NOT reverse-engineered from either reference implementation - `pack.rs` and
/// `ndztool.py` both only ever take a target dictionary *size* as input; neither has a
/// size-recommender of its own. The shape (a discrete size/result curve with a "knee"
/// recommendation) is inspired by ndz-studio's own deployed `analyze()` UI feature (see
/// docs/ndz-format-spec.md's "Dictionary sizing" section), but that lives only in the
/// website's frontend JS/WASM, isn't part of the confirmed format spec, and its actual
/// algorithm was never seen - this is an original implementation, not a port.
///
/// Speed comes from two deliberate shortcuts, both worth knowing about before trusting a
/// number to the byte:
/// 1. Only a stratified sample of the ROM's blocks is actually test-compressed at each
///    candidate size (see <see cref="SampleBlockIndexes"/>), not the whole ROM - each
///    candidate's total size is extrapolated from the sampled ratio. A real pack
///    (<see cref="NdzWriter.Compress"/>) always compresses every block for real.
/// 2. The five per-block filter transforms are not tried here - a real pack will
///    typically end up slightly smaller than this estimate as a result.
/// Dictionary *selection* (which chunks make the cut) barely shifts between neighboring
/// candidate sizes, and the tradeoff being sized here - stored dictionary bytes vs. the
/// compression they buy on the rest of the ROM - is dominated by the sampled ratio
/// either way, so a recommendation is still worth committing to sight-unseen.
/// </summary>
public static class DictionaryAnalyzer
{
    /// <summary>
    /// The real, confirmed target-hardware constraint: dictionary and the decompressed-
    /// block cache share one 8 MiB PSRAM pool (read directly from ndz_studio's own
    /// deployed source - see docs/ndz-format-spec.md's "Dictionary sizing" section) - a
    /// dictionary that eats the whole budget leaves nothing for the cache that makes
    /// random access fast in the first place.
    /// </summary>
    public const int PsramBudget = 8 * 1024 * 1024;

    /// <summary>
    /// Default cap on the dictionary-size search - <see cref="PsramBudget"/> itself, the
    /// real technical ceiling (a dictionary literally cannot be bigger than the whole
    /// shared pool). No fixed slice is carved out for the cache ahead of time - there's
    /// nothing to enforce here, the cache doesn't need a guaranteed minimum to function,
    /// it's just slower with less of it. Whether it's worth spending PSRAM on the
    /// dictionary instead is exactly what <see cref="DefaultDiminishingReturnsTolerance"/>
    /// decides, size by size, not a reservation carved out in advance. Per-call override
    /// via <see cref="Analyze"/>'s <c>maxDictionarySize</c>.
    /// </summary>
    public const int DefaultMaxDictionarySize = PsramBudget;

    /// <summary>
    /// Default diminishing-returns tolerance for <see cref="Analyze"/>'s recommendation:
    /// the smallest dictionary size whose estimated total is within this fraction of the
    /// best total found anywhere in the curve wins, rather than always the exact global
    /// minimum - a size that only shaves a further sliver off the true best isn't worth
    /// recommending just because it's technically smallest, since that extra PSRAM has a
    /// real alternative use (the decompressed-block cache). Not a hard reservation -
    /// nothing stops the search reaching the full <see cref="PsramBudget"/> when the
    /// savings genuinely justify it, this just says how big a marginal saving has to be
    /// before it does. This specific fraction (1%) is our own tuning choice, picked
    /// because it's the smallest round value that reproduces BOTH real examples we have
    /// end to end: Mena's own hardcoded Pokemon White 2 curve (docs/ndz-format-spec.md,
    /// "Dictionary sizing" - 5 MiB, one step before a 6 MiB uptick, even though 7 MiB
    /// recovers to a technically-smaller total) and ndz-studio's live `analyze()`
    /// recommendation of 6 MiB on a real Pokemon Black/White ROM
    /// (docs/ndz-remaining-work.md) - not a confirmed algorithm, just tuned to match
    /// observed real behavior (see
    /// <c>DictionaryAnalyzerTests.SelectRecommendedSize_OnMenasWhite2ExampleCurve_PicksTheKneeNotTheGlobalMinimum</c>
    /// in the test project).
    /// </summary>
    public const double DefaultDiminishingReturnsTolerance = 0.01;

    public readonly record struct CurvePoint(int DictionarySize, long EstimatedTotalSize)
    {
        public double EstimatedRatio(long originalSize) => EstimatedTotalSize <= 0 ? 0 : (double)originalSize / EstimatedTotalSize;
    }

    public sealed class Result
    {
        public required IReadOnlyList<CurvePoint> Curve { get; init; }
        public required int RecommendedDictionarySize { get; init; }
        public required int SampledBlockCount { get; init; }
        public required int TotalBlockCount { get; init; }
    }

    /// <summary>
    /// Builds the dictionary-size/estimated-total-size curve for <paramref name="rom"/>
    /// (optionally scored as if base-patched against <paramref name="baseRom"/>, so the
    /// estimate accounts for the same per-block "smallest of plain/dict/base-window"
    /// choice a real pair-container pack would make - a dictionary competing against an
    /// already-excellent base-window match on some block buys nothing there, and ignoring
    /// that would overstate the dictionary's benefit).
    ///
    /// The recommendation is deliberately NOT just "smallest estimated total size" - two
    /// separate effects push it lower than the pure global minimum:
    /// 1. The dictionary itself is stored verbatim in the file (see
    ///    <see cref="NdzFrontMatter.DictionaryStoredSize"/>), so past some point a bigger
    ///    dictionary's own storage cost outweighs what it saves elsewhere - can make a
    ///    genuinely larger dictionary score *worse*, not just plateau (matches Mena's own
    ///    hardcoded example curve, docs/ndz-format-spec.md's "Dictionary sizing": 6 MiB
    ///    scored worse than 5 MiB for Pokemon White 2).
    /// 2. <paramref name="diminishingReturnsTolerance"/>: once a candidate is already
    ///    within this fraction of the best total anywhere in the curve, growing the
    ///    dictionary further isn't recommended even if it would technically shave off a
    ///    few more bytes - on the real hardware, PSRAM not spent on the dictionary is
    ///    available to the decompressed-block cache instead (<see cref="PsramBudget"/>),
    ///    and that's normally worth more than a marginal fraction of a percent of file
    ///    size. This is why <see cref="DefaultMaxDictionarySize"/> is 6 MiB, not the full
    ///    8 MiB budget, and why the recommendation can land below the cap too.
    /// </summary>
    public static Result Analyze(
        byte[] rom,
        byte[]? baseRom = null,
        int maxDictionarySize = DefaultMaxDictionarySize,
        CompressionType level = NdzWriter.DefaultLevel,
        int blockSize = NdzConstants.BlockSize,
        double sampleFraction = 0.04,
        int minSampleBytes = 4 * 1024 * 1024,
        int maxSampleBytes = 16 * 1024 * 1024,
        double diminishingReturnsTolerance = DefaultDiminishingReturnsTolerance)
    {
        ArgumentNullException.ThrowIfNull(rom);
        if (maxDictionarySize < 0)
            throw new ArgumentOutOfRangeException(nameof(maxDictionarySize));
        int[] ladder = BuildSizeLadder(maxDictionarySize);
        byte[][] dictionaries = rom.Length == 0 ? Array.Empty<byte[]>() : RawDictionaryBuilder.BuildLadder(rom, ladder);
        return AnalyzeCore(rom, baseRom, level, blockSize, sampleFraction, minSampleBytes, maxSampleBytes, diminishingReturnsTolerance, ladder, dictionaries);
    }

    /// <summary>
    /// Same as <see cref="Analyze"/>, but takes an already-built <paramref name="ladder"/>/
    /// <paramref name="dictionaries"/> pair instead of building them from
    /// <paramref name="rom"/> itself - <see cref="RawDictionaryBuilder.BuildLadder"/>'s
    /// content-defined-chunking + ranking pass scans the WHOLE rom and depends only on
    /// its bytes, never on <paramref name="blockSize"/>, so a caller comparing several
    /// block sizes for the same ROM (<see cref="BlockSizeAnalyzer"/>) can build it once
    /// and reuse it across every candidate instead of redundantly re-chunking the same
    /// ROM once per block size. <paramref name="dictionaries"/> must be exactly what
    /// <c>RawDictionaryBuilder.BuildLadder(rom, ladder)</c> would itself produce - this is
    /// an internal, trusted-caller entry point (only <see cref="BlockSizeAnalyzer"/> calls
    /// it today), not a public safety-checked one.
    /// </summary>
    internal static Result AnalyzeCore(
        byte[] rom,
        byte[]? baseRom,
        CompressionType level,
        int blockSize,
        double sampleFraction,
        int minSampleBytes,
        int maxSampleBytes,
        double diminishingReturnsTolerance,
        int[] ladder,
        byte[][] dictionaries)
    {
        if (rom.Length == 0)
        {
            return new Result
            {
                Curve = new[] { new CurvePoint(0, 0) },
                RecommendedDictionarySize = 0,
                SampledBlockCount = 0,
                TotalBlockCount = 0,
            };
        }

        int totalBlocks = (rom.Length + blockSize - 1) / blockSize;
        int[] sampleIndexes = SampleBlockIndexes(totalBlocks, rom.Length, blockSize, sampleFraction, minSampleBytes, maxSampleBytes);

        BaseRomIndex? baseIndex = baseRom == null ? null : new BaseRomIndex(baseRom);

        var plainOptions = new CompressionOptions { Type = level, BlockSize = blockSize };
        using var plainBlock = new ZStdBlock(plainOptions);
        int maxOutputSize = plainBlock.RequiredCompressOutputSize;
        byte[] dst = new byte[maxOutputSize];

        // Per sampled block: the best size achievable with no raw dictionary at all
        // (plain, or the best base-patch window if a base ROM was supplied) - the same
        // regardless of which dictionary candidate is being scored, so computed once and
        // reused across the whole ladder rather than re-searched per candidate.
        long sampledOriginalBytes = 0;
        var baselineBest = new int[sampleIndexes.Length];
        var blockLengths = new int[sampleIndexes.Length];

        for (int s = 0; s < sampleIndexes.Length; s++)
        {
            int blockOffset = sampleIndexes[s] * blockSize;
            int blockLength = Math.Min(blockSize, rom.Length - blockOffset);
            blockLengths[s] = blockLength;
            sampledOriginalBytes += blockLength;

            int count = dst.Length;
            var plainResult = plainBlock.Compress(rom, blockOffset, blockLength, dst, 0, ref count);
            if (plainResult != CompressionResultCode.Success)
                throw new InvalidDataException($"ZStd compression failed sampling block at offset {blockOffset}: {plainResult}");
            int best = count;

            if (baseIndex != null)
            {
                ReadOnlySpan<byte> blockSource = rom.AsSpan(blockOffset, blockLength);
                foreach (int windowOffset in baseIndex.CandidateWindowOffsets(blockSource, blockOffset))
                {
                    byte[] window = baseIndex.GetWindow(windowOffset).ToArray();
                    using var candidateBlock = new ZStdBlock(new CompressionOptions { Type = level, BlockSize = blockSize, InitProperties = window });
                    byte[] candidateDst = new byte[Math.Max(maxOutputSize, candidateBlock.RequiredCompressOutputSize)];
                    int candidateCount = candidateDst.Length;
                    var candidateResult = candidateBlock.Compress(rom, blockOffset, blockLength, candidateDst, 0, ref candidateCount);
                    if (candidateResult == CompressionResultCode.Success && candidateCount < best)
                        best = candidateCount;
                }
            }

            baselineBest[s] = best;
        }

        var curve = new List<CurvePoint>(ladder.Length);

        for (int i = 0; i < ladder.Length; i++)
        {
            byte[] dictionary = dictionaries[i];
            // Matches NdzWriter's own `if (derivedDictionary.Length >= 4096)` threshold -
            // ndztool.py's own "not worth storing a near-useless sliver" cutoff.
            ZStdBlock? dictBlock = dictionary.Length < 4096 ? null : new ZStdBlock(new CompressionOptions
            {
                Type = level,
                BlockSize = blockSize,
                InitProperties = dictionary,
                Dictionary = new CompressionDictionaryOptions { WindowBits = NdzWriter.ComputeDictionaryWindowBits(dictionary.Length, blockSize) },
            });

            try
            {
                long sampledCompressedBytes = 0;
                byte[] dictDst = dictBlock == null ? Array.Empty<byte>() : new byte[Math.Max(maxOutputSize, dictBlock.RequiredCompressOutputSize)];
                for (int s = 0; s < sampleIndexes.Length; s++)
                {
                    int best = baselineBest[s];
                    if (dictBlock != null)
                    {
                        int blockOffset = sampleIndexes[s] * blockSize;
                        int count = dictDst.Length;
                        var result = dictBlock.Compress(rom, blockOffset, blockLengths[s], dictDst, 0, ref count);
                        if (result == CompressionResultCode.Success && count < best)
                            best = count;
                    }
                    sampledCompressedBytes += best;
                }

                double ratio = sampledOriginalBytes == 0 ? 1.0 : (double)sampledCompressedBytes / sampledOriginalBytes;
                long estimatedPayload = (long)Math.Round(ratio * rom.Length);
                long estimatedTotal = estimatedPayload + dictionary.Length;
                curve.Add(new CurvePoint(ladder[i], estimatedTotal));
            }
            finally
            {
                dictBlock?.Dispose();
            }
        }

        return new Result
        {
            Curve = curve,
            RecommendedDictionarySize = SelectRecommendedSize(curve, diminishingReturnsTolerance),
            SampledBlockCount = sampleIndexes.Length,
            TotalBlockCount = totalBlocks,
        };
    }

    /// <summary>
    /// The smallest candidate already within <paramref name="diminishingReturnsTolerance"/>
    /// of the best total anywhere in <paramref name="curve"/>, not necessarily the literal
    /// global minimum - see <see cref="Analyze"/>'s remarks on why (PSRAM not spent on the
    /// dictionary is available to the cache instead). <paramref name="curve"/> must be in
    /// ascending dictionary-size order (as <see cref="Analyze"/> always builds it), so the
    /// first match found is the smallest one. Split out from <see cref="Analyze"/> so the
    /// selection math itself can be tested against known curves directly, independent of
    /// the (slow, approximate) sampling that normally produces one.
    /// </summary>
    internal static int SelectRecommendedSize(IReadOnlyList<CurvePoint> curve, double diminishingReturnsTolerance) =>
        SelectWithinTolerance(curve, p => p.DictionarySize, p => p.EstimatedTotalSize, diminishingReturnsTolerance);

    /// <summary>
    /// The smallest-"size" candidate (by <paramref name="size"/>) already within
    /// <paramref name="tolerance"/> of the best <paramref name="total"/> anywhere in
    /// <paramref name="candidates"/> - the same diminishing-returns rule
    /// <see cref="SelectRecommendedSize"/> applies to dictionary size, generalized so
    /// <see cref="BlockSizeAnalyzer"/> can apply the identical logic to block size
    /// (there, the resource block size trades away isn't PSRAM but random-access
    /// granularity - the same "don't chase a sliver" reasoning still applies).
    /// <paramref name="candidates"/> must already be in ascending size order.
    /// </summary>
    internal static int SelectWithinTolerance<T>(IReadOnlyList<T> candidates, Func<T, int> size, Func<T, long> total, double tolerance)
    {
        long bestTotal = candidates.Min(total);
        long slack = (long)Math.Ceiling(bestTotal * tolerance);
        return size(candidates.First(c => total(c) <= bestTotal + slack));
    }

    /// <summary>0, 512 KiB, then every whole MiB up to (and always ending exactly on) <paramref name="maxDictionarySize"/> - roughly the shape of Mena's own hardcoded example curve (docs/ndz-format-spec.md's "Dictionary sizing" section).</summary>
    internal static int[] BuildSizeLadder(int maxDictionarySize)
    {
        var sizes = new List<int> { 0 };
        if (maxDictionarySize > 0)
        {
            const int half = 512 * 1024, mib = 1024 * 1024;
            if (half < maxDictionarySize)
                sizes.Add(half);
            for (int step = mib; step < maxDictionarySize; step += mib)
                sizes.Add(step);
            sizes.Add(maxDictionarySize);
        }
        return sizes.Distinct().OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// Evenly-strided (not random - reproducible run to run) block indexes covering
    /// roughly <paramref name="sampleFraction"/> of the ROM, clamped to
    /// [<paramref name="minSampleBytes"/>, <paramref name="maxSampleBytes"/>] worth of
    /// blocks (or the whole ROM, if that's smaller than the floor).
    /// </summary>
    internal static int[] SampleBlockIndexes(int totalBlocks, int romLength, int blockSize, double sampleFraction, int minSampleBytes, int maxSampleBytes)
    {
        long targetBytes = Math.Clamp((long)(romLength * sampleFraction), minSampleBytes, maxSampleBytes);
        int sampleBlocks = Math.Clamp((int)((targetBytes + blockSize - 1) / blockSize), 1, totalBlocks);
        int stride = Math.Max(1, totalBlocks / sampleBlocks);

        var indexes = new List<int>(sampleBlocks);
        for (int i = 0; i < totalBlocks && indexes.Count < sampleBlocks; i += stride)
            indexes.Add(i);
        return indexes.ToArray();
    }
}
