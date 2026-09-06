using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// <see cref="BlockSizeAnalyzer"/> is an original heuristic (see its own remarks and
/// <see cref="DictionaryAnalyzer"/>'s) - no reference implementation to cross-check
/// against. These cover its behavior on deliberately-constructed inputs where the "right"
/// answer follows directly from how per-block compression actually works (blocks are
/// compressed independently, with no cross-block back-references - see
/// <see cref="NdzWriter"/>'s remarks), not from trusting the algorithm's own output.
/// </summary>
public class BlockSizeAnalyzerTests
{
    /// <summary>
    /// Builds a ROM where every 8 KiB "unit" is byte-identical, so an 8 KiB block never
    /// sees more than one occurrence of it (nothing to internally match against - looks
    /// like noise), a 16 KiB block sees two back-to-back copies (a large, easy internal
    /// match), and a 32 KiB block sees four. Deliberately front-loads almost all of the
    /// real win at 16 KiB, mirroring a realistic diminishing-returns shape (32 KiB should
    /// only add a sliver beyond that).
    /// </summary>
    private static byte[] BuildRomWithRepeatingUnit(int unitSize, int totalSize)
    {
        var unit = new byte[unitSize];
        new Random(2024).NextBytes(unit);
        var rom = new byte[totalSize];
        for (int offset = 0; offset < totalSize; offset += unitSize)
            unit.AsSpan(0, Math.Min(unitSize, totalSize - offset)).CopyTo(rom.AsSpan(offset));
        return rom;
    }

    [Fact]
    public void Analyze_WhenBiggerBlocksExposeRealInternalRepetition_RecommendsALargerBlockSize()
    {
        byte[] rom = BuildRomWithRepeatingUnit(unitSize: 8192, totalSize: 8192 * 64);

        var result = BlockSizeAnalyzer.Analyze(rom, maxDictionarySize: 0);

        Assert.True(result.RecommendedBlockSize > NdzConstants.BlockSize);
        // 8 KiB blocks see one unit each (no internal repeat to find) - clearly the worst.
        var at8k = result.Candidates.Single(c => c.BlockSize == NdzConstants.BlockSize);
        var atRecommended = result.Candidates.Single(c => c.BlockSize == result.RecommendedBlockSize);
        Assert.True(atRecommended.BestEstimatedTotalSize < at8k.BestEstimatedTotalSize);
    }

    [Fact]
    public void Analyze_OnIncompressibleNoise_RecommendsTheSmallestBlockSize()
    {
        // No repetition anywhere, at any scale - a bigger block buys nothing, so the
        // smallest (most random-access-friendly) candidate should win by default.
        var rom = new byte[8192 * 48];
        new Random(7).NextBytes(rom);

        var result = BlockSizeAnalyzer.Analyze(rom, maxDictionarySize: 0);

        Assert.Equal(NdzConstants.SupportedBlockSizes.Min(), result.RecommendedBlockSize);
    }

    [Fact]
    public void Analyze_RecommendedBlockSizeIsAlwaysOneOfTheCandidates()
    {
        var rom = new byte[8192 * 16];
        new Random(1).NextBytes(rom);
        int[] candidates = { 16384, 32768 };

        var result = BlockSizeAnalyzer.Analyze(rom, blockSizeCandidates: candidates, maxDictionarySize: 0);

        Assert.Contains(result.RecommendedBlockSize, candidates);
        Assert.Equal(candidates.Length, result.Candidates.Count);
    }

    [Fact]
    public void Analyze_WithNoCandidates_Throws()
    {
        var rom = new byte[8192];
        Assert.Throws<ArgumentException>(() => BlockSizeAnalyzer.Analyze(rom, blockSizeCandidates: Array.Empty<int>()));
    }

    [Fact]
    public void DefaultCandidates_MatchNdzConstantsSupportedBlockSizes()
    {
        Assert.Equal(NdzConstants.SupportedBlockSizes, BlockSizeAnalyzer.DefaultCandidates);
    }

    /// <summary>
    /// AnalyzePair must score each block size on the BASE and the base-patched TARGET
    /// combined, not the base alone - a real, previously-shipped bug (see
    /// AnalyzePair's own remarks) picked block size from the base's self-contained curve
    /// only, which real-ROM testing (a genuine Pokemon Black/White pair) showed can pick
    /// a block size that looks great for the base while badly hurting the base-patched
    /// target (91 MB -> 127.6 MB). This pins the fix's actual mechanism directly - each
    /// candidate's combined total must equal its two components' totals added together,
    /// not either one alone - rather than trying to re-derive the specific real-ROM
    /// magnitude synthetically (too dependent on real zstd match behavior to engineer
    /// reliably in a small fixture).
    /// </summary>
    [Fact]
    public void AnalyzePair_CombinedTotalIsTheSumOfBothSides_NotEitherAlone()
    {
        byte[] baseRom = BuildRomWithRepeatingUnit(unitSize: 8192, totalSize: 8192 * 64);
        var rng = new Random(55);
        byte[] targetRom = new byte[baseRom.Length];
        rng.NextBytes(targetRom);

        var result = BlockSizeAnalyzer.AnalyzePair(baseRom, targetRom, maxDictionarySize: 0);

        foreach (var candidate in result.Candidates)
        {
            var targetAnalysis = Assert.Single(candidate.TargetAnalyses);
            long baseBest = candidate.BaseAnalysis.Curve.Single(p => p.DictionarySize == candidate.BaseAnalysis.RecommendedDictionarySize).EstimatedTotalSize;
            long targetBest = targetAnalysis.Curve.Single(p => p.DictionarySize == targetAnalysis.RecommendedDictionarySize).EstimatedTotalSize;
            Assert.Equal(baseBest + targetBest, candidate.CombinedBestEstimatedTotalSize);
            // Neither side's number alone should be silently treated as the whole story.
            Assert.NotEqual(baseBest, candidate.CombinedBestEstimatedTotalSize);
            Assert.NotEqual(targetBest, candidate.CombinedBestEstimatedTotalSize);
        }

        Assert.Contains(result.RecommendedBlockSize, NdzConstants.SupportedBlockSizes);
        var recommended = result.Candidates.Single(c => c.BlockSize == result.RecommendedBlockSize);
        Assert.Equal(recommended.BaseAnalysis.RecommendedDictionarySize, result.RecommendedBaseDictionarySize);
        Assert.Equal(Assert.Single(recommended.TargetAnalyses).RecommendedDictionarySize, Assert.Single(result.RecommendedTargetDictionarySizes));
    }

    /// <summary>The N-target overload combines ALL targets' totals, not just the first - two identical-shaped targets should double the target contribution to the combined metric.</summary>
    [Fact]
    public void AnalyzePair_WithMultipleTargets_CombinedTotalIncludesEveryTarget()
    {
        byte[] baseRom = BuildRomWithRepeatingUnit(unitSize: 8192, totalSize: 8192 * 64);
        var rng = new Random(66);
        byte[] targetA = new byte[baseRom.Length];
        byte[] targetB = new byte[baseRom.Length];
        rng.NextBytes(targetA);
        rng.NextBytes(targetB);

        var result = BlockSizeAnalyzer.AnalyzePair(baseRom, new[] { targetA, targetB }, maxDictionarySize: 0);

        Assert.Equal(2, result.RecommendedTargetDictionarySizes.Count);
        foreach (var candidate in result.Candidates)
        {
            Assert.Equal(2, candidate.TargetAnalyses.Count);
            long expected = candidate.BaseAnalysis.Curve.Single(p => p.DictionarySize == candidate.BaseAnalysis.RecommendedDictionarySize).EstimatedTotalSize;
            foreach (var t in candidate.TargetAnalyses)
                expected += t.Curve.Single(p => p.DictionarySize == t.RecommendedDictionarySize).EstimatedTotalSize;
            Assert.Equal(expected, candidate.CombinedBestEstimatedTotalSize);
        }
    }

    [Fact]
    public void AnalyzePair_WithNoCandidates_Throws()
    {
        var rom = new byte[8192];
        Assert.Throws<ArgumentException>(() => BlockSizeAnalyzer.AnalyzePair(rom, rom, blockSizeCandidates: Array.Empty<int>()));
    }
}
