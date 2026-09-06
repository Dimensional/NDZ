using Ndz.Core.Compression;

namespace Ndz.Core.Tests;

/// <summary>
/// <see cref="DictionaryAnalyzer"/> is an original heuristic (see its own remarks) -
/// there's no reference implementation to cross-check byte-for-byte, unlike most of this
/// project's other tests. These cover its internal building blocks precisely and its
/// end-to-end behavior on deliberately-constructed inputs where the "right" answer is
/// obvious by construction.
/// </summary>
public class DictionaryAnalyzerTests
{
    [Fact]
    public void BuildSizeLadder_AtDefaultCap_StartsAtZeroEndsAtCapAscending()
    {
        int[] ladder = DictionaryAnalyzer.BuildSizeLadder(DictionaryAnalyzer.DefaultMaxDictionarySize);

        Assert.Equal(0, ladder[0]);
        Assert.Equal(DictionaryAnalyzer.DefaultMaxDictionarySize, ladder[^1]);
        Assert.Equal(ladder, ladder.Distinct().OrderBy(x => x));
        Assert.All(ladder, size => Assert.InRange(size, 0, DictionaryAnalyzer.DefaultMaxDictionarySize));
    }

    [Fact]
    public void BuildSizeLadder_WithZeroCap_IsJustZero()
    {
        Assert.Equal(new[] { 0 }, DictionaryAnalyzer.BuildSizeLadder(0));
    }

    [Fact]
    public void BuildSizeLadder_BelowHalfMebibyteCap_IsZeroThenTheCapItself()
    {
        Assert.Equal(new[] { 0, 300_000 }, DictionaryAnalyzer.BuildSizeLadder(300_000));
    }

    [Fact]
    public void SampleBlockIndexes_WhenTargetExceedsRom_ClampsToEveryBlock()
    {
        int[] indexes = DictionaryAnalyzer.SampleBlockIndexes(totalBlocks: 10, romLength: 10 * 8192, blockSize: 8192,
            sampleFraction: 0.04, minSampleBytes: 64 * 1024 * 1024, maxSampleBytes: 64 * 1024 * 1024);

        Assert.Equal(Enumerable.Range(0, 10), indexes);
    }

    [Fact]
    public void SampleBlockIndexes_AreStrictlyIncreasingAndWithinRange()
    {
        int[] indexes = DictionaryAnalyzer.SampleBlockIndexes(totalBlocks: 32768, romLength: 32768 * 8192, blockSize: 8192,
            sampleFraction: 0.04, minSampleBytes: 4 * 1024 * 1024, maxSampleBytes: 16 * 1024 * 1024);

        Assert.NotEmpty(indexes);
        Assert.All(indexes, i => Assert.InRange(i, 0, 32767));
        for (int i = 1; i < indexes.Length; i++)
            Assert.True(indexes[i] > indexes[i - 1]);
    }

    [Fact]
    public void Analyze_OnEmptyRom_ReturnsSingleZeroPoint()
    {
        var result = DictionaryAnalyzer.Analyze(Array.Empty<byte>());

        Assert.Single(result.Curve);
        Assert.Equal(0, result.Curve[0].DictionarySize);
        Assert.Equal(0, result.RecommendedDictionarySize);
    }

    /// <summary>A ROM built from one 8 KiB block repeated many times - a dictionary should always help here, so the recommended size must be positive and its estimated total must beat packing with no dictionary at all.</summary>
    [Fact]
    public void Analyze_OnHighlyRepetitiveRom_RecommendsANonZeroDictionary()
    {
        const int blockSize = 8192;
        var pattern = new byte[blockSize];
        new Random(7).NextBytes(pattern);
        var rom = new byte[blockSize * 64];
        for (int i = 0; i < 64; i++)
            pattern.CopyTo(rom, i * blockSize);

        var result = DictionaryAnalyzer.Analyze(rom, blockSize: blockSize, maxDictionarySize: 64 * 1024);

        Assert.True(result.RecommendedDictionarySize > 0);
        long noDictTotal = result.Curve.Single(p => p.DictionarySize == 0).EstimatedTotalSize;
        long recommendedTotal = result.Curve.Single(p => p.DictionarySize == result.RecommendedDictionarySize).EstimatedTotalSize;
        Assert.True(recommendedTotal < noDictTotal);
    }

    /// <summary>When the base ROM already contains the target byte-for-byte, base-window patching alone should already be near-perfect - a raw dictionary can only add storage cost on top of that, never a real win, so 0 should come out on top.</summary>
    [Fact]
    public void Analyze_WhenBaseAlreadyMatchesPerfectly_RecommendsNoDictionary()
    {
        const int blockSize = 8192;
        var rng = new Random(99);
        var rom = new byte[blockSize * 8];
        rng.NextBytes(rom);
        byte[] baseRom = (byte[])rom.Clone();

        var result = DictionaryAnalyzer.Analyze(rom, baseRom, maxDictionarySize: 32 * 1024, blockSize: blockSize);

        Assert.Equal(0, result.RecommendedDictionarySize);
    }

    /// <summary>
    /// Mena's own hardcoded ndz-studio example curve for Pokemon White 2
    /// (docs/ndz-format-spec.md's "Dictionary sizing" section) - real numbers, not
    /// fabricated, so this pins <see cref="DictionaryAnalyzer.SelectRecommendedSize"/>'s
    /// tolerance-based knee-picking against a case we know the "obviously right" answer
    /// for: 5 MiB, one step before the curve ticks up at 6 MiB, even though 7 MiB
    /// recovers to a technically-smaller total (108.3) than 5 MiB's 108.7 - a global-
    /// minimum search would wrongly chase that recovery.
    /// </summary>
    [Fact]
    public void SelectRecommendedSize_OnMenasWhite2ExampleCurve_PicksTheKneeNotTheGlobalMinimum()
    {
        var curve = new[]
        {
            new DictionaryAnalyzer.CurvePoint(0, 146_400_000),
            new DictionaryAnalyzer.CurvePoint(523_264, 127_500_000),
            new DictionaryAnalyzer.CurvePoint(1_046_528, 120_600_000),
            new DictionaryAnalyzer.CurvePoint(2_097_152, 117_300_000),
            new DictionaryAnalyzer.CurvePoint(3_145_728, 113_500_000),
            new DictionaryAnalyzer.CurvePoint(4_194_304, 110_000_000),
            new DictionaryAnalyzer.CurvePoint(5_242_880, 108_700_000),
            new DictionaryAnalyzer.CurvePoint(6_291_456, 109_000_000),
            new DictionaryAnalyzer.CurvePoint(7_340_032, 108_300_000),
        };

        // Tight tolerance (0) chases the literal global minimum, wherever it falls.
        Assert.Equal(7_340_032, DictionaryAnalyzer.SelectRecommendedSize(curve, 0.0));
        // The default tolerance instead settles for "close enough", one step before the
        // curve's first uptick - matches the real ndz-studio recommendation of 5 MiB.
        Assert.Equal(5_242_880, DictionaryAnalyzer.SelectRecommendedSize(curve, DictionaryAnalyzer.DefaultDiminishingReturnsTolerance));
    }

    [Fact]
    public void SelectRecommendedSize_WithLooserTolerance_RecommendsAnEarlierSmallerCandidate()
    {
        var curve = new[]
        {
            new DictionaryAnalyzer.CurvePoint(0, 1_000_000),
            new DictionaryAnalyzer.CurvePoint(1024, 600_000),
            new DictionaryAnalyzer.CurvePoint(2048, 590_000),
            new DictionaryAnalyzer.CurvePoint(3072, 585_000),
        };

        Assert.Equal(3072, DictionaryAnalyzer.SelectRecommendedSize(curve, 0.0));
        Assert.Equal(1024, DictionaryAnalyzer.SelectRecommendedSize(curve, 0.05));
    }

    [Fact]
    public void Analyze_RecommendedSizeNeverExceedsCap()
    {
        var rng = new Random(3);
        var rom = new byte[8192 * 40];
        rng.NextBytes(rom);
        // Splice in enough repetition that a dictionary has something real to find.
        var repeated = new byte[8192 * 4];
        rng.NextBytes(repeated);
        for (int i = 0; i < 8; i++)
            repeated.CopyTo(rom, i * repeated.Length % (rom.Length - repeated.Length));

        var result = DictionaryAnalyzer.Analyze(rom, maxDictionarySize: 64 * 1024);

        Assert.True(result.RecommendedDictionarySize <= 64 * 1024);
        Assert.All(result.Curve, p => Assert.InRange(p.DictionarySize, 0, 64 * 1024));
    }
}
