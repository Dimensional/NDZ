using Ndz.Core.Format;

namespace Ndz.Core.Tests;

public class NdzFlagsExtensionsTests
{
    [Theory]
    [InlineData(8192, 13)]
    [InlineData(4096, 12)]
    [InlineData(65536, 16)]
    public void WithBlockSize_RoundTripsThroughGetBlockSize(int blockSize, int expectedLog2)
    {
        NdzFlags flags = NdzFlags.None.WithBlockSize(blockSize);

        Assert.Equal(expectedLog2, flags.GetBlockSizeLog2());
        Assert.Equal(blockSize, flags.GetBlockSize());
    }

    /// <summary>
    /// Confirmed against `ndztool.py`'s own decode logic (reference/mena-patchbench -
    /// we have that script, not the `patchbench.py` module it defers to): a log2
    /// subfield of exactly 0 is a sentinel for "unspecified - default to 4096", not a
    /// literal "1 &lt;&lt; 0 = 1 byte" block size. That means <c>WithBlockSize(1)</c> does not
    /// round-trip through <see cref="NdzFlagsExtensions.GetBlockSize"/> - a real 1-byte
    /// block size isn't representable, which is fine since nothing would realistically
    /// use one.
    /// </summary>
    [Fact]
    public void WithBlockSize_OfOne_ReadsBackAsTheDefaultFourKiBSentinel()
    {
        NdzFlags flags = NdzFlags.None.WithBlockSize(1);

        Assert.Equal(0, flags.GetBlockSizeLog2());
        Assert.Equal(4096, flags.GetBlockSize());
    }

    [Fact]
    public void WithBlockSize_DoesNotDisturbBooleanBits()
    {
        NdzFlags flags = (NdzFlags.V2 | NdzFlags.ZStd | NdzFlags.RawDictionary).WithBlockSize(NdzConstants.BlockSize);

        Assert.True(flags.HasFlag(NdzFlags.V2));
        Assert.True(flags.HasFlag(NdzFlags.ZStd));
        Assert.True(flags.HasFlag(NdzFlags.RawDictionary));
        Assert.False(flags.HasFlag(NdzFlags.Filters));
        Assert.False(flags.HasFlag(NdzFlags.BasePatch));
        Assert.Equal(NdzConstants.BlockSize, flags.GetBlockSize());
    }

    [Fact]
    public void GetBlockSizeLog2_IsZeroWhenNeverSet()
    {
        Assert.Equal(0, NdzFlags.V2.GetBlockSizeLog2());
        Assert.Equal(4096, NdzFlags.V2.GetBlockSize()); // confirmed sentinel default, see WithBlockSize_OfOne_...
    }
}
