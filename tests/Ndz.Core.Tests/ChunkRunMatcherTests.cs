using Ndz.Core.Compression;

namespace Ndz.Core.Tests;

public class ChunkRunMatcherTests
{
    [Fact]
    public void TryFindBlockMatch_UnchangedContent_FindsItsOwnPosition()
    {
        byte[] baseRom = TestRom.Build(256 * 1024, seed: 1);
        byte[] target = (byte[])baseRom.Clone();

        var matcher = new ChunkRunMatcher(baseRom, target);

        Assert.True(matcher.TryFindBlockMatch(8192 * 3, 8192, out int offset));
        Assert.Equal(8192 * 3, offset);
    }

    [Fact]
    public void TryFindBlockMatch_RelocatedSpan_FindsTheRelocatedOffset()
    {
        var rng = new Random(7);
        byte[] baseRom = TestRom.Build(512 * 1024, seed: 2);

        // A span much larger than any single CDC chunk, copied to a different, unrelated
        // position in the target - exactly the "relocated content" real ndz-studio output
        // exhibits (see HackContainerTests' own relocation test).
        byte[] shared = new byte[48 * 1024];
        rng.NextBytes(shared);
        Array.Copy(shared, 0, baseRom, 100_000, shared.Length);

        byte[] target = (byte[])baseRom.Clone();
        // Overwrite the base's original position in the target with unrelated bytes first,
        // so a match can only be found via the relocated copy below, not this position.
        new Random(99).NextBytes(target.AsSpan(100_000, shared.Length));
        Array.Copy(shared, 0, target, 250_000, shared.Length);

        var matcher = new ChunkRunMatcher(baseRom, target);

        // A block comfortably inside the relocated span (away from its edges, where a
        // partial chunk overlap could legitimately fail to resolve).
        int blockOffset = 250_000 + 16 * 1024;
        Assert.True(matcher.TryFindBlockMatch(blockOffset, 8192, out int foundBaseOffset));
        Assert.Equal(blockOffset - 250_000 + 100_000, foundBaseOffset);
    }

    [Fact]
    public void TryFindBlockMatch_NovelContent_ReturnsFalse()
    {
        byte[] baseRom = TestRom.Build(256 * 1024, seed: 3);
        byte[] target = TestRom.Build(256 * 1024, gameCode: "TRGT", seed: 4);

        var matcher = new ChunkRunMatcher(baseRom, target);

        // TestRom.Build's headers/banners share a fixed pattern region, so query well past
        // that into the purely-random filler, which differs between seeds 3 and 4.
        Assert.False(matcher.TryFindBlockMatch(64 * 1024, 8192, out _));
    }

    [Fact]
    public void TryFindBlockMatch_OutOfRange_ReturnsFalse()
    {
        byte[] baseRom = TestRom.Build(64 * 1024, seed: 5);
        byte[] target = (byte[])baseRom.Clone();

        var matcher = new ChunkRunMatcher(baseRom, target);

        Assert.False(matcher.TryFindBlockMatch(target.Length - 100, 8192, out _));
    }
}
