using Ndz.Core.XDelta.Djw;

namespace Ndz.Core.Tests;

public class DjwCodecTests
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>
    /// Regression test for a real bitstream-desync bug (see <see cref="DjwCodec"/>'s own
    /// remarks): a large, low-diversity section makes <c>BuildInitialPartition</c>'s "too many
    /// groups for this data" retry loop reduce the requested group count all the way down to
    /// 1, which must route through the single-group encode path (no sector-size field, no
    /// selector stream) to stay in sync with the decoder's own <c>groups &gt; 1</c> gate on
    /// reading either. A single repeated byte, large enough to select more than one group in
    /// <c>ChooseGroupsAndSectorSize</c>, reproduces the exact real-world failure (a 240 KB
    /// all-zero section pulled from a real ROM diff).
    /// </summary>
    [Fact]
    public void RoundTrips_LargeSingleByteValueSection()
    {
        byte[] data = new byte[240_000];
        Array.Fill(data, (byte)0);

        byte[]? compressed = DjwCodec.Compress(data);
        Assert.NotNull(compressed);

        byte[] decompressed = DjwCodec.Decompress(compressed);
        Assert.Equal(data, decompressed);
    }

    /// <summary>Same shape as <see cref="RoundTrips_LargeSingleByteValueSection"/> but with a nonzero repeated byte, in case zero itself were somehow special-cased anywhere.</summary>
    [Fact]
    public void RoundTrips_LargeSingleByteValueSection_NonzeroByte()
    {
        byte[] data = new byte[240_000];
        Array.Fill(data, (byte)0xAB);

        byte[]? compressed = DjwCodec.Compress(data);
        Assert.NotNull(compressed);

        byte[] decompressed = DjwCodec.Decompress(compressed);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void RoundTrips_LargeTwoByteValueSection()
    {
        var data = new byte[240_000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 7 == 0 ? 1 : 0);

        byte[]? compressed = DjwCodec.Compress(data);
        Assert.NotNull(compressed);

        byte[] decompressed = DjwCodec.Decompress(compressed);
        Assert.Equal(data, decompressed);
    }

    /// <summary>
    /// Uniformly random bytes carry no exploitable byte-frequency skew, so DJW correctly
    /// declines (returns null - real xdelta3 does the same "not worth it" bail-out) rather
    /// than that being a bug; this only checks that WHEN it does return non-null for such
    /// data, it still round-trips correctly.
    /// </summary>
    [Fact]
    public void RoundTrips_LargeDiverseRandomSection()
    {
        byte[] data = RandomBytes(240_000, seed: 7);

        byte[]? compressed = DjwCodec.Compress(data);
        if (compressed is null)
            return;

        byte[] decompressed = DjwCodec.Decompress(compressed);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void Compress_EmptySection_ReturnsNull()
    {
        Assert.Null(DjwCodec.Compress([]));
    }

    /// <summary>
    /// The <see cref="RoundTrips_LargeSingleByteValueSection"/> regression above only proves
    /// THIS ONE specific edge case (a section low-diversity enough to collapse
    /// <c>ChooseGroupsAndSectorSize</c>'s requested group count all the way to 1) is now
    /// fixed - it was found by luck (a real ROM diff happened to contain a 240 KB all-zero
    /// run), not by systematic testing, in a "fairly intricate ported codec" (this class's
    /// own remarks) that had carried this exact bug class undetected since it was first
    /// written. This sweeps a grid of (length, byte-diversity shape) combinations - lengths
    /// straddling every tier boundary in <see cref="DjwCodec"/>'s private
    /// <c>ChooseGroupsAndSectorSize</c> (1000/4000/10000/50000), crossed with shapes from
    /// "one repeated byte" through "genuinely full 256-value random" and a skewed
    /// mostly-one-value shape specifically meant to provoke <c>BuildInitialPartition</c>'s
    /// group-reduction retry without going all the way to the single-byte extreme - looking
    /// for a SIBLING bug in the same area, not just re-confirming the one already found.
    /// </summary>
    public static IEnumerable<object[]> FuzzCases()
    {
        int[] lengths = [1, 2, 10, 999, 1000, 1001, 3999, 4000, 4001, 9999, 10000, 10001, 49999, 50000, 50001, 100_000, 500_000];
        string[] shapes = ["single", "two-skewed", "small-alphabet", "moderate", "full-random"];
        foreach (int length in lengths)
            foreach (string shape in shapes)
                yield return new object[] { length, shape };
    }

    [Theory]
    [MemberData(nameof(FuzzCases))]
    public void RoundTrips_LengthAndDiversitySweep(int length, string shape)
    {
        for (int seed = 0; seed < 3; seed++)
        {
            byte[] data = GenerateShaped(length, shape, seed);

            byte[]? compressed = DjwCodec.Compress(data);
            if (compressed is null)
                continue; // Declined as not worth compressing - nothing to verify (see RoundTrips_LargeDiverseRandomSection's own remarks).

            byte[] decompressed = DjwCodec.Decompress(compressed);
            Assert.True(data.AsSpan().SequenceEqual(decompressed), $"Round-trip mismatch: length={length} shape={shape} seed={seed}");
        }
    }

    private static byte[] GenerateShaped(int length, string shape, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        switch (shape)
        {
            case "single":
                Array.Fill(data, (byte)(seed * 77 + 3));
                break;
            case "two-skewed":
            {
                byte common = (byte)(seed * 41 + 1);
                byte rare = (byte)(common + 1);
                for (int i = 0; i < length; i++)
                    data[i] = rng.Next(100) < 2 ? rare : common; // ~2% rare byte, rest one dominant value.
                break;
            }
            case "small-alphabet":
                for (int i = 0; i < length; i++)
                    data[i] = (byte)rng.Next(5);
                break;
            case "moderate":
                for (int i = 0; i < length; i++)
                    data[i] = (byte)rng.Next(32);
                break;
            default: // full-random
                rng.NextBytes(data);
                break;
        }
        return data;
    }
}
