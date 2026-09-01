using Ndz.Core.Compression;

namespace Ndz.Core.Tests;

public class BaseRomIndexTests
{
    [Fact]
    public void CandidateWindowOffsets_FindsAWindowContainingContentCopiedFromTheBase()
    {
        var baseRom = new byte[1024 * 1024];
        new Random(1).NextBytes(baseRom);

        // A block that's byte-for-byte a copy of base content living far from its own
        // file offset in a hypothetical target ROM - only the grain-hash match (not the
        // centered-on-file-offset guess) can find it. Grain-aligned on purpose: grain
        // hashing is content-addressed on fixed 2048-byte boundaries (matching
        // ndztool.py's own BaseCtx exactly), so it can only ever match content that
        // lines up with the base's own grain boundaries - not a bug, a real property of
        // the algorithm.
        const int baseContentOffset = BaseRomIndex.Grain * 200;
        const int blockSize = BaseRomIndex.Grain * 4; // several whole grains
        byte[] block = baseRom.AsSpan(baseContentOffset, blockSize).ToArray();

        var index = new BaseRomIndex(baseRom);
        var candidates = index.CandidateWindowOffsets(block, fileOffset: 10);

        bool foundExactMatch = candidates.Any(offset =>
        {
            var window = index.GetWindow(offset);
            // The block should appear somewhere inside at least one candidate window.
            return IndexOf(window, block) >= 0;
        });

        Assert.True(foundExactMatch, "Expected at least one candidate window to actually contain the copied content.");
    }

    [Fact]
    public void CandidateWindowOffsets_NeverExceedsEightCandidates()
    {
        var baseRom = new byte[1024 * 1024];
        // All-identical content maximizes hash collisions/candidate count.
        for (int i = 0; i < baseRom.Length; i++) baseRom[i] = 0x42;

        var index = new BaseRomIndex(baseRom);
        var block = new byte[BaseRomIndex.Grain * 4];
        for (int i = 0; i < block.Length; i++) block[i] = 0x42;

        var candidates = index.CandidateWindowOffsets(block, fileOffset: 999);
        Assert.True(candidates.Count <= 8);
    }

    [Fact]
    public void CandidateWindowOffsets_EveryCandidateWindowStaysInBounds()
    {
        var baseRom = new byte[1024 * 1024];
        new Random(2).NextBytes(baseRom);
        var index = new BaseRomIndex(baseRom);

        var block = baseRom.AsSpan(baseRom.Length - BaseRomIndex.Grain * 2, BaseRomIndex.Grain * 2).ToArray();
        var candidates = index.CandidateWindowOffsets(block, fileOffset: baseRom.Length - 10);

        Assert.All(candidates, offset =>
        {
            Assert.True(offset >= 0);
            Assert.True(offset + BaseRomIndex.WindowSize <= baseRom.Length);
        });
    }

    [Fact]
    public void GetWindow_OnBaseShorterThanWindowSize_ReturnsWhateverFits()
    {
        var baseRom = new byte[1000];
        new Random(3).NextBytes(baseRom);
        var index = new BaseRomIndex(baseRom);

        var window = index.GetWindow(0);
        Assert.Equal(1000, window.Length);
        Assert.Equal(baseRom, window.ToArray());
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}
