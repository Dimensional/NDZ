using Nanook.GrindCore;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// Picks a block size by running <see cref="DictionaryAnalyzer.Analyze"/> once per
/// candidate (see <see cref="NdzConstants.MaxBlockSize"/>'s remarks on the 2026-09-06
/// firmware fix that newly allows 16/32 KiB blocks, not just the original 8 KiB) and
/// comparing each candidate's best result. Same diminishing-returns principle as
/// dictionary sizing (<see cref="DictionaryAnalyzer.DefaultDiminishingReturnsTolerance"/>),
/// applied to a different resource: a bigger block trades away random-access granularity
/// (a whole block must be decompressed to reach any single byte inside it) for
/// (typically) a somewhat better ratio, so it's only worth recommending when that ratio
/// win is clearly real, not a sliver - exactly like dictionary size trading PSRAM for
/// ratio. This is our own heuristic, not a reference-confirmed algorithm - see
/// <see cref="DictionaryAnalyzer"/>'s own remarks for what that implies about accuracy.
/// </summary>
public static class BlockSizeAnalyzer
{
    /// <summary>The block sizes compared by default - <see cref="NdzConstants.SupportedBlockSizes"/>, the CLI's own curated menu.</summary>
    public static readonly IReadOnlyList<int> DefaultCandidates = NdzConstants.SupportedBlockSizes;

    public readonly record struct Candidate(int BlockSize, DictionaryAnalyzer.Result DictionaryAnalysis)
    {
        /// <summary>The estimated total size at this block size's own recommended dictionary size - what different block sizes are actually compared on.</summary>
        public long BestEstimatedTotalSize
        {
            get
            {
                var analysis = DictionaryAnalysis;
                return analysis.Curve.First(p => p.DictionarySize == analysis.RecommendedDictionarySize).EstimatedTotalSize;
            }
        }
    }

    public sealed class Result
    {
        public required IReadOnlyList<Candidate> Candidates { get; init; }
        public required int RecommendedBlockSize { get; init; }
        /// <summary>The dictionary size <see cref="DictionaryAnalyzer"/> recommended specifically for <see cref="RecommendedBlockSize"/> - dictionary economics shift with block size too (bigger blocks give a self-dictionary or base-window candidate more to match against per hit).</summary>
        public required int RecommendedDictionarySize { get; init; }
    }

    /// <param name="maxDegreeOfParallelism">
    /// Forwarded to <see cref="DictionaryAnalyzer.AnalyzeCore"/> for every block-size
    /// candidate below - see its own remarks. The outer loop over block-size candidates
    /// itself stays sequential (only ~3 iterations, each already ladder-parallel
    /// internally - parallelizing both levels would over-subscribe rather than help on
    /// most machines).
    /// </param>
    public static Result Analyze(
        byte[] rom,
        byte[]? baseRom = null,
        IReadOnlyList<int>? blockSizeCandidates = null,
        int maxDictionarySize = DictionaryAnalyzer.DefaultMaxDictionarySize,
        CompressionType level = NdzWriter.DefaultLevel,
        double sampleFraction = 0.04,
        int minSampleBytes = 4 * 1024 * 1024,
        int maxSampleBytes = 16 * 1024 * 1024,
        double diminishingReturnsTolerance = DictionaryAnalyzer.DefaultDiminishingReturnsTolerance,
        int maxDegreeOfParallelism = -1)
    {
        ArgumentNullException.ThrowIfNull(rom);
        var candidateSizes = (blockSizeCandidates ?? DefaultCandidates).Distinct().OrderBy(x => x).ToArray();
        if (candidateSizes.Length == 0)
            throw new ArgumentException("At least one block size candidate is required.", nameof(blockSizeCandidates));

        // Built once and reused across every block-size candidate below - dictionary
        // content (the full-ROM content-defined-chunking + dedup-ranking pass, the
        // expensive part) depends only on rom's own bytes, never on block size, so
        // redoing it once per candidate would be pure waste. See DictionaryAnalyzer's
        // AnalyzeCore remarks.
        int[] ladder = DictionaryAnalyzer.BuildSizeLadder(maxDictionarySize);
        byte[][] dictionaries = RawDictionaryBuilder.BuildLadder(rom, ladder);

        var candidates = new List<Candidate>(candidateSizes.Length);
        foreach (int blockSize in candidateSizes)
        {
            var analysis = DictionaryAnalyzer.AnalyzeCore(rom, baseRom, level, blockSize, sampleFraction, minSampleBytes, maxSampleBytes, diminishingReturnsTolerance, ladder, dictionaries, maxDegreeOfParallelism);
            candidates.Add(new Candidate(blockSize, analysis));
        }

        int recommendedBlockSize = DictionaryAnalyzer.SelectWithinTolerance(candidates, c => c.BlockSize, c => c.BestEstimatedTotalSize, diminishingReturnsTolerance);
        var recommended = candidates.First(c => c.BlockSize == recommendedBlockSize);

        return new Result
        {
            Candidates = candidates,
            RecommendedBlockSize = recommendedBlockSize,
            RecommendedDictionarySize = recommended.DictionaryAnalysis.RecommendedDictionarySize,
        };
    }

    public readonly record struct PairCandidate(int BlockSize, DictionaryAnalyzer.Result BaseAnalysis, IReadOnlyList<DictionaryAnalyzer.Result> TargetAnalyses)
    {
        /// <summary>The base's own best total plus every base-patched target's best total, summed - what different block sizes are actually compared on in pair mode (a star topology: one base, one or more targets, all patched against that same base).</summary>
        public long CombinedBestEstimatedTotalSize
        {
            get
            {
                var b = BaseAnalysis;
                long total = b.Curve.First(p => p.DictionarySize == b.RecommendedDictionarySize).EstimatedTotalSize;
                foreach (var t in TargetAnalyses)
                    total += t.Curve.First(p => p.DictionarySize == t.RecommendedDictionarySize).EstimatedTotalSize;
                return total;
            }
        }
    }

    public sealed class PairResult
    {
        public required IReadOnlyList<PairCandidate> Candidates { get; init; }
        public required int RecommendedBlockSize { get; init; }
        public required int RecommendedBaseDictionarySize { get; init; }
        /// <summary>One recommended dictionary size per target ROM, in the same order they were passed to <see cref="AnalyzePair"/> - each target's own economics are analyzed independently (see <see cref="DictionaryAnalyzer.Analyze"/>'s pair-mode remarks), they don't affect each other.</summary>
        public required IReadOnlyList<int> RecommendedTargetDictionarySizes { get; init; }
    }

    /// <summary>
    /// Pair-container-aware version of <see cref="Analyze"/>: picks ONE block size shared
    /// by the base and every target (matching `ndztool.py`'s own `--pair-out`, which only
    /// ever takes one - and this project's own generalization to more than one target,
    /// see <see cref="NdzPairWriter"/>, is still a star topology, one shared block size
    /// for all of it), scored on their COMBINED total, not the base ROM's self-contained
    /// curve alone. That distinction is not cosmetic - the base-patch window search
    /// (<see cref="BaseRomIndex"/>) always uses a fixed <see cref="BaseRomIndex.WindowSize"/>
    /// (16 KiB, confirmed via `ndztool.py`'s own `NDZ_BASE_WINDOW` constant - `pack.rs`
    /// has no base-patch mode at all, so there's no second opinion to check), which does
    /// NOT grow with block size. A 32 KiB block only ever gets a 16 KiB window to match
    /// against - at most half of it can ever be covered by one candidate - so a bigger
    /// block size can look clearly better for a self-contained ROM while badly hurting a
    /// base-patched one at the very same size. Evaluating block size from the base alone
    /// (an earlier, real mistake here - a full 256 MB real Pokemon Black/White pair
    /// packed 91 MB -> 127.6 MB after "helpfully" auto-picking 32 KiB blocks this way)
    /// completely misses that interaction, since the base-patched target(s) - normally
    /// the dominant share of a pair's total size - are exactly what a bigger block
    /// quietly breaks.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">Forwarded to <see cref="DictionaryAnalyzer.AnalyzeCore"/> for every ROM/block-size combination below - see its own remarks. The outer loops (block-size candidates, and base+each target within one candidate) stay sequential for the same over-subscription reason as the plain <see cref="Analyze"/> overload.</param>
    public static PairResult AnalyzePair(
        byte[] baseRom,
        IReadOnlyList<byte[]> targetRoms,
        IReadOnlyList<int>? blockSizeCandidates = null,
        int maxDictionarySize = DictionaryAnalyzer.DefaultMaxDictionarySize,
        CompressionType level = NdzWriter.DefaultLevel,
        double sampleFraction = 0.04,
        int minSampleBytes = 4 * 1024 * 1024,
        int maxSampleBytes = 16 * 1024 * 1024,
        double diminishingReturnsTolerance = DictionaryAnalyzer.DefaultDiminishingReturnsTolerance,
        int maxDegreeOfParallelism = -1)
    {
        ArgumentNullException.ThrowIfNull(baseRom);
        ArgumentNullException.ThrowIfNull(targetRoms);
        if (targetRoms.Count == 0)
            throw new ArgumentException("At least one target ROM is required.", nameof(targetRoms));
        var candidateSizes = (blockSizeCandidates ?? DefaultCandidates).Distinct().OrderBy(x => x).ToArray();
        if (candidateSizes.Length == 0)
            throw new ArgumentException("At least one block size candidate is required.", nameof(blockSizeCandidates));

        // Built once per ROM (base + each target) and reused across every block-size
        // candidate - see the single-ROM Analyze's identical remarks above. Without this,
        // an N-target pair would redo the full-ROM chunking pass 3x(N+1) times instead of
        // just (N+1), for identical results either way.
        int[] ladder = DictionaryAnalyzer.BuildSizeLadder(maxDictionarySize);
        byte[][] baseDictionaries = RawDictionaryBuilder.BuildLadder(baseRom, ladder);
        var targetDictionaries = targetRoms.Select(t => RawDictionaryBuilder.BuildLadder(t, ladder)).ToArray();

        var candidates = new List<PairCandidate>(candidateSizes.Length);
        foreach (int blockSize in candidateSizes)
        {
            var baseAnalysis = DictionaryAnalyzer.AnalyzeCore(baseRom, null, level, blockSize, sampleFraction, minSampleBytes, maxSampleBytes, diminishingReturnsTolerance, ladder, baseDictionaries, maxDegreeOfParallelism);
            var targetAnalyses = new DictionaryAnalyzer.Result[targetRoms.Count];
            for (int i = 0; i < targetRoms.Count; i++)
                targetAnalyses[i] = DictionaryAnalyzer.AnalyzeCore(targetRoms[i], baseRom, level, blockSize, sampleFraction, minSampleBytes, maxSampleBytes, diminishingReturnsTolerance, ladder, targetDictionaries[i], maxDegreeOfParallelism);
            candidates.Add(new PairCandidate(blockSize, baseAnalysis, targetAnalyses));
        }

        int recommendedBlockSize = DictionaryAnalyzer.SelectWithinTolerance(candidates, c => c.BlockSize, c => c.CombinedBestEstimatedTotalSize, diminishingReturnsTolerance);
        var recommended = candidates.First(c => c.BlockSize == recommendedBlockSize);

        return new PairResult
        {
            Candidates = candidates,
            RecommendedBlockSize = recommendedBlockSize,
            RecommendedBaseDictionarySize = recommended.BaseAnalysis.RecommendedDictionarySize,
            RecommendedTargetDictionarySizes = recommended.TargetAnalyses.Select(t => t.RecommendedDictionarySize).ToArray(),
        };
    }

    /// <summary>Single-target convenience overload - see the general (N-target) <see cref="AnalyzePair"/> overload.</summary>
    public static PairResult AnalyzePair(
        byte[] baseRom,
        byte[] targetRom,
        IReadOnlyList<int>? blockSizeCandidates = null,
        int maxDictionarySize = DictionaryAnalyzer.DefaultMaxDictionarySize,
        CompressionType level = NdzWriter.DefaultLevel,
        double sampleFraction = 0.04,
        int minSampleBytes = 4 * 1024 * 1024,
        int maxSampleBytes = 16 * 1024 * 1024,
        double diminishingReturnsTolerance = DictionaryAnalyzer.DefaultDiminishingReturnsTolerance,
        int maxDegreeOfParallelism = -1)
    {
        ArgumentNullException.ThrowIfNull(targetRom);
        return AnalyzePair(baseRom, new[] { targetRom }, blockSizeCandidates, maxDictionarySize, level, sampleFraction, minSampleBytes, maxSampleBytes, diminishingReturnsTolerance, maxDegreeOfParallelism);
    }
}
