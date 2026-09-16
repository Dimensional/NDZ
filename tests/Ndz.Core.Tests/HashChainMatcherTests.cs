using Ndz.Core.XDelta;

namespace Ndz.Core.Tests;

/// <summary>
/// <see cref="HashChainMatcher.FindExactMatch"/> - promoted 2026-09-16 out of
/// <see cref="VcdiffEncoder"/>'s own private nested class alongside the pre-existing
/// <see cref="HashChainMatcher.FindBestMatch"/> (unchanged behavior, still exercised
/// end-to-end by every round-trip test in <see cref="XDeltaTests"/>) so
/// <c>HackContainerWriter</c> can reuse the same hash-chain index for a different query:
/// a genuine full-length exact match, not the longest partial one.
/// </summary>
public class HashChainMatcherTests
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void FindExactMatch_FindsSelfPosition()
    {
        byte[] source = RandomBytes(4096, seed: 1);
        byte[] target = (byte[])source.Clone();

        var matcher = new HashChainMatcher(source);
        bool found = matcher.FindExactMatch(target, targetPos: 1000, length: 256, out int sourcePos);

        Assert.True(found);
        Assert.Equal(1000, sourcePos);
    }

    [Fact]
    public void FindExactMatch_FindsRelocatedNonAlignedPosition()
    {
        byte[] source = RandomBytes(65536, seed: 2);
        byte[] target = RandomBytes(65536, seed: 3);

        // Deliberately not aligned to any obvious grid (block size, 2048-byte grain,
        // etc.) - the real relocation case this exists for.
        const int relocatedSourceOffset = 12345;
        const int targetPos = 40000;
        Array.Copy(source, relocatedSourceOffset, target, targetPos, 512);

        var matcher = new HashChainMatcher(source);
        bool found = matcher.FindExactMatch(target, targetPos, length: 512, out int sourcePos);

        Assert.True(found);
        Assert.Equal(relocatedSourceOffset, sourcePos);
    }

    [Fact]
    public void FindExactMatch_ReturnsFalse_WhenNoFullLengthMatchExists()
    {
        byte[] source = RandomBytes(4096, seed: 4);
        byte[] target = RandomBytes(4096, seed: 5);

        var matcher = new HashChainMatcher(source);
        bool found = matcher.FindExactMatch(target, targetPos: 500, length: 256, out _);

        Assert.False(found);
    }

    [Fact]
    public void FindExactMatch_RejectsAPartialMatchLongerThanRequestedButNotFull()
    {
        // A candidate that shares the first HashBytes bytes (so it's on the hash chain
        // at all) but diverges before the requested length is complete must not be
        // accepted - unlike FindBestMatch, there is no "shorter match is still fine" here.
        byte[] source = RandomBytes(4096, seed: 6);
        byte[] target = (byte[])source.Clone();
        target[1000 + 50] ^= 0xFF; // breaks byte-for-byte equality partway through

        var matcher = new HashChainMatcher(source);
        bool found = matcher.FindExactMatch(target, targetPos: 1000, length: 256, out _);

        Assert.False(found);
    }
}
