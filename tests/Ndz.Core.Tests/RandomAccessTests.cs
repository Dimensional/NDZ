using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

public class RandomAccessTests
{
    private static NdzArchive CompressToArchive(byte[] rom)
    {
        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output);
        return NdzArchive.Open(output.ToArray());
    }

    [Theory]
    [InlineData(0, 100)] // within block 0 of frame 0
    [InlineData(NdzConstants.BlockSize - 10, 20)] // spans an inner block boundary, still within frame 0
    [InlineData(NdzConstants.BlockSize, 50)] // starts exactly at block 1 of frame 0
    [InlineData(NdzConstants.FrameSize - 10, 20)] // spans the frame 0/1 boundary
    [InlineData(NdzConstants.FrameSize, 50)] // starts exactly at frame 1
    [InlineData(NdzConstants.FrameSize * 2 + 5, 50)] // reaches into the short final frame
    public void ReadAt_MatchesOriginalSlice(int offset, int length)
    {
        byte[] original = TestRom.Build(NdzConstants.FrameSize * 2 + 60);
        using var archive = CompressToArchive(original);

        var actual = new byte[length];
        int read = archive.ReadAt(offset, actual);

        Assert.Equal(length, read);
        Assert.Equal(original.AsSpan(offset, length).ToArray(), actual);
    }

    [Fact]
    public void ReadAt_NearEndOfArchive_ReturnsOnlyAvailableBytes()
    {
        byte[] original = TestRom.Build(NdzConstants.FrameSize + 30);
        using var archive = CompressToArchive(original);

        var buffer = new byte[100];
        int read = archive.ReadAt(archive.Length - 10, buffer);

        Assert.Equal(10, read);
        Assert.Equal(original.AsSpan(original.Length - 10, 10).ToArray(), buffer.AsSpan(0, 10).ToArray());
    }

    [Fact]
    public void ReadAt_AtExactEnd_ReturnsZero()
    {
        byte[] original = TestRom.Build(NdzConstants.BlockSize);
        using var archive = CompressToArchive(original);

        int read = archive.ReadAt(archive.Length, new byte[10]);

        Assert.Equal(0, read);
    }

    [Fact]
    public void ReadAt_RejectsNegativeOrOutOfRangeOffset()
    {
        byte[] original = TestRom.Build(NdzConstants.BlockSize);
        using var archive = CompressToArchive(original);

        Assert.Throws<ArgumentOutOfRangeException>(() => archive.ReadAt(-1, new byte[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => archive.ReadAt(archive.Length + 1, new byte[1]));
    }

    [Fact]
    public void ReadAt_OutOfOrderReadsAcrossFrames_StillMatchOriginal()
    {
        // Exercises the single-frame cache: bounce between two different frames and
        // confirm neither read gets stale cached data from the other.
        byte[] original = TestRom.Build(NdzConstants.FrameSize * 3);
        using var archive = CompressToArchive(original);

        var fromFrame2 = new byte[16];
        var fromFrame0 = new byte[16];
        archive.ReadAt(NdzConstants.FrameSize * 2, fromFrame2);
        archive.ReadAt(0, fromFrame0);

        Assert.Equal(original.AsSpan(NdzConstants.FrameSize * 2, 16).ToArray(), fromFrame2);
        Assert.Equal(original.AsSpan(0, 16).ToArray(), fromFrame0);
    }

    [Fact]
    public void DecompressAll_MatchesReadAt_ForWholeRange()
    {
        byte[] original = TestRom.Build(NdzConstants.FrameSize * 2 + 42);
        using var archive = CompressToArchive(original);

        byte[] viaDecompressAll = archive.DecompressAll();

        var viaReadAt = new byte[original.Length];
        archive.ReadAt(0, viaReadAt);

        Assert.Equal(viaDecompressAll, viaReadAt);
    }
}
