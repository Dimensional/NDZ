using Nanook.GrindCore;
using Nanook.GrindCore.ZStd;
using Ndz.Core.Compression;

namespace Ndz.Core.Tests;

public class ZstdFrameHeaderTrimTests
{
    /// <summary>Compresses via the same plain ZStdBlock path NdzWriter itself uses, so the trim sees GrindCore's real, actual output shape rather than a hand-built approximation.</summary>
    private static (byte[] Buffer, int Count) CompressPlain(byte[] data)
    {
        using var block = new ZStdBlock(new CompressionOptions { Type = CompressionType.Level1, BlockSize = data.Length });
        byte[] buffer = new byte[block.RequiredCompressOutputSize];
        int count = buffer.Length;
        var result = block.Compress(data, 0, data.Length, buffer, 0, ref count);
        Assert.Equal(CompressionResultCode.Success, result);
        return (buffer, count);
    }

    [Fact]
    public void TrimZstdFrameHeader_RealGrindCoreOutput_ShrinksByExactlyOneByte()
    {
        byte[] data = TestRom.Build(8192, seed: 1);
        var (buffer, count) = CompressPlain(data);

        // Confirm the known, untrimmed shape first (single-segment, 2-byte content size) -
        // if GrindCore ever changes this, the trim's defensive shape check should make it
        // a silent no-op rather than a correctness bug, but this test should then fail
        // loudly so the assumption gets re-examined.
        byte fhdBefore = buffer[4];
        Assert.Equal(0x60, fhdBefore);

        int trimmedCount = NdzWriter.TrimZstdFrameHeader(buffer, count, data.Length);

        Assert.Equal(count - 1, trimmedCount);
        Assert.Equal(0x00, buffer[4]);
    }

    [Fact]
    public void TrimZstdFrameHeader_TrimmedOutput_StillDecompressesCorrectly()
    {
        byte[] data = TestRom.Build(8192, seed: 2);
        var (buffer, count) = CompressPlain(data);
        int trimmedCount = NdzWriter.TrimZstdFrameHeader(buffer, count, data.Length);

        using var block = new ZStdBlock(new CompressionOptions { Type = CompressionType.Level1, BlockSize = data.Length });
        byte[] decompressed = new byte[data.Length];
        int decompressedCount = decompressed.Length;
        var result = block.Decompress(buffer, 0, trimmedCount, decompressed, 0, ref decompressedCount);

        Assert.Equal(CompressionResultCode.Success, result);
        Assert.Equal(data.Length, decompressedCount);
        Assert.Equal(data, decompressed);
    }

    [Fact]
    public void TrimZstdFrameHeader_UnrecognizedShape_ReturnsUnchanged()
    {
        // Not a zstd frame at all (wrong magic) - must be a safe no-op, never guessed at.
        byte[] notZstd = new byte[20];
        Array.Fill(notZstd, (byte)0xAA);

        int result = NdzWriter.TrimZstdFrameHeader(notZstd, notZstd.Length, 8192);

        Assert.Equal(notZstd.Length, result);
        Assert.All(notZstd, b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public void TrimZstdFrameHeader_TooShortToContainAHeader_ReturnsUnchanged()
    {
        byte[] tiny = new byte[4];
        int result = NdzWriter.TrimZstdFrameHeader(tiny, tiny.Length, 8192);
        Assert.Equal(tiny.Length, result);
    }
}
