using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Hand-verified against `ndztool.py`'s own `_delta_fwd`/`_delta_inv`/`_shuffle_fwd`/
/// `_shuffle_inv` semantics (see BlockFilters' remarks) before this port trusts it for
/// real compression - small, byte-by-byte-checkable fixtures rather than only
/// round-trip fuzzing, so a sign/order mistake would show up as a wrong *value*, not
/// just a failed round-trip.
/// </summary>
public class BlockFiltersTests
{
    [Fact]
    public void DeltaForward_Stride1_MatchesHandComputedValues()
    {
        // source = [10, 12, 15, 11, 20]; out[i] = source[i] - source[i-1] for i>=1.
        byte[] source = { 10, 12, 15, 11, 20 };
        var dest = new byte[source.Length];

        BlockFilters.DeltaForward(source, dest, 1);

        // out[0]=10 (unchanged), out[1]=12-10=2, out[2]=15-12=3, out[3]=11-15=-4=252, out[4]=20-11=9
        Assert.Equal(new byte[] { 10, 2, 3, 252, 9 }, dest);
    }

    [Fact]
    public void DeltaForward_Stride2_MatchesHandComputedValues()
    {
        // out[i] = source[i] - source[i-2] for i>=2; [0],[1] unchanged.
        byte[] source = { 5, 200, 8, 210, 100, 50 };
        var dest = new byte[source.Length];

        BlockFilters.DeltaForward(source, dest, 2);

        // out[2]=8-5=3, out[3]=210-200=10, out[4]=100-8=92, out[5]=50-210=-160=96
        Assert.Equal(new byte[] { 5, 200, 3, 10, 92, 96 }, dest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void DeltaInverse_UndoesDeltaForward(int stride)
    {
        var original = new byte[64];
        new Random(stride).NextBytes(original);

        var forward = new byte[original.Length];
        BlockFilters.DeltaForward(original, forward, stride);

        var restored = (byte[])forward.Clone();
        BlockFilters.DeltaInverse(restored, stride);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void DeltaForward_InPlace_MatchesOutOfPlace()
    {
        byte[] original = { 10, 12, 15, 11, 20, 30, 9, 200 };

        var outOfPlace = new byte[original.Length];
        BlockFilters.DeltaForward(original, outOfPlace, 2);

        var inPlace = (byte[])original.Clone();
        BlockFilters.DeltaForward(inPlace, inPlace, 2);

        Assert.Equal(outOfPlace, inPlace);
    }

    [Fact]
    public void ShuffleForward_Planes2_MatchesHandComputedValues()
    {
        // source = [A0,B0,A1,B1,A2,B2] -> planes: [A0,A1,A2, B0,B1,B2]
        byte[] source = { 0xA0, 0xB0, 0xA1, 0xB1, 0xA2, 0xB2 };
        var dest = new byte[source.Length];

        BlockFilters.ShuffleForward(source, dest, 2);

        Assert.Equal(new byte[] { 0xA0, 0xA1, 0xA2, 0xB0, 0xB1, 0xB2 }, dest);
    }

    [Fact]
    public void ShuffleForward_Planes4_MatchesHandComputedValues()
    {
        // source = [A0,B0,C0,D0, A1,B1,C1,D1] -> [A0,A1, B0,B1, C0,C1, D0,D1]
        byte[] source = { 1, 2, 3, 4, 5, 6, 7, 8 };
        var dest = new byte[source.Length];

        BlockFilters.ShuffleForward(source, dest, 4);

        Assert.Equal(new byte[] { 1, 5, 2, 6, 3, 7, 4, 8 }, dest);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ShuffleInverse_UndoesShuffleForward(int planes)
    {
        var original = new byte[64];
        new Random(planes * 100).NextBytes(original);

        var forward = new byte[original.Length];
        BlockFilters.ShuffleForward(original, forward, planes);

        var restored = new byte[original.Length];
        BlockFilters.ShuffleInverse(forward, restored, planes);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void ShuffleForward_RejectsLengthNotDivisibleByPlaneCount()
    {
        byte[] source = new byte[7];
        var dest = new byte[7];
        Assert.Throws<ArgumentException>(() => BlockFilters.ShuffleForward(source, dest, 4));
    }

    [Theory]
    [InlineData(BlockMode.Delta1, 1)]
    [InlineData(BlockMode.Delta2, 2)]
    [InlineData(BlockMode.Delta4, 4)]
    [InlineData(BlockMode.Plain, 0)]
    [InlineData(BlockMode.Dict, 0)]
    [InlineData(BlockMode.Shuffle2, 0)]
    public void GetDeltaStride_MatchesModeOrZero(BlockMode mode, int expected) =>
        Assert.Equal(expected, BlockFilters.GetDeltaStride(mode));

    [Theory]
    [InlineData(BlockMode.Shuffle2, 2)]
    [InlineData(BlockMode.Shuffle4, 4)]
    [InlineData(BlockMode.Plain, 0)]
    [InlineData(BlockMode.Delta1, 0)]
    public void GetShufflePlaneCount_MatchesModeOrZero(BlockMode mode, int expected) =>
        Assert.Equal(expected, BlockFilters.GetShufflePlaneCount(mode));
}
