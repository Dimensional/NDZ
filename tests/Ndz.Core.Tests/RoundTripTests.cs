using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

public class RoundTripTests
{
    [Theory]
    [InlineData(0x1000)] // smaller than one block
    [InlineData(NdzConstants.BlockSize)] // exactly one block
    [InlineData(NdzConstants.FrameSize)] // exactly one frame (16 full blocks)
    [InlineData(NdzConstants.FrameSize + NdzConstants.BlockSize * 3)] // one full frame plus a partial second frame
    [InlineData(NdzConstants.FrameSize * 2 + 17)] // multiple frames plus a short remainder
    public void Compress_ThenDecompressAll_IsByteIdentical(int romSize)
    {
        byte[] original = TestRom.Build(romSize);

        using var output = new MemoryStream();
        NdzWriter.Compress(original, output);

        using var archive = NdzArchive.Open(output.ToArray());
        byte[] rebuilt = archive.DecompressAll();

        Assert.Equal(original, rebuilt);
    }

    [Fact]
    public void Compress_RecordsExpectedFrameCountAndSizes()
    {
        byte[] original = TestRom.Build(NdzConstants.FrameSize * 2 + 100);

        using var output = new MemoryStream();
        NdzWriter.Compress(original, output);
        using var archive = NdzArchive.Open(output.ToArray());

        Assert.Equal(3, archive.SeekTable.Count);
        Assert.Equal((uint)NdzConstants.FrameSize, archive.SeekTable[0].DecompressedSize);
        Assert.Equal((uint)NdzConstants.FrameSize, archive.SeekTable[1].DecompressedSize);
        Assert.Equal(100u, archive.SeekTable[2].DecompressedSize);
        Assert.All(archive.SeekTable, e => Assert.True(e.CompressedSize > 0));
    }

    [Fact]
    public void Compress_PopulatesFrontMatterFromRomHeader()
    {
        byte[] original = TestRom.Build(NdzConstants.FrameSize * 2, gameCode: "ABCJ");

        using var output = new MemoryStream();
        NdzWriter.Compress(original, output);
        using var archive = NdzArchive.Open(output.ToArray());

        uint expectedGameCode = BitConverter.ToUInt32(System.Text.Encoding.ASCII.GetBytes("ABCJ"));
        Assert.Equal(expectedGameCode, archive.FrontMatter.GameCode);
        Assert.Equal((uint)original.Length, archive.FrontMatter.OriginalSize);
        Assert.False(archive.FrontMatter.HasDictionary);

        // Version 0 (TestRom's default) -> a 0x840-byte banner, verbatim, zero-padded
        // to the full reserved slot - not a raw full-slot copy from the source ROM.
        var expectedBanner = new byte[NdzConstants.BannerSlotLength];
        original.AsSpan(0x200, NdzConstants.GetBannerContentSize(0)).CopyTo(expectedBanner);
        Assert.Equal(expectedBanner, archive.FrontMatter.Banner);
    }

    [Fact]
    public void Compress_SetsExpectedFlags()
    {
        byte[] original = TestRom.Build(NdzConstants.BlockSize);

        using var output = new MemoryStream();
        NdzWriter.Compress(original, output);
        using var archive = NdzArchive.Open(output.ToArray());

        NdzFlags flags = archive.FrontMatter.Flags;
        Assert.True(flags.HasFlag(NdzFlags.V2));
        Assert.True(flags.HasFlag(NdzFlags.ZStd));
        // Filters must be set whenever a per-block mode array is written, which
        // CompressFrame always does (even Plain-only) - see NdzWriter's remarks and
        // docs/ndz-format-spec.md's "Per-block compression mode".
        Assert.True(flags.HasFlag(NdzFlags.Filters));
        Assert.False(flags.HasFlag(NdzFlags.RawDictionary));
        Assert.False(flags.HasFlag(NdzFlags.BasePatch));
        Assert.Equal(NdzConstants.BlockSize, flags.GetBlockSize());
    }

    [Fact]
    public void HighlyCompressibleRom_CompressesSmallerThanOriginal()
    {
        // An all-zero-filler ROM (still with a real header/banner) should compress well.
        byte[] withHeader = TestRom.Build(NdzConstants.FrameSize * 2);
        int bannerContentEnd = 0x200 + NdzConstants.GetBannerContentSize(0);
        Array.Clear(withHeader, bannerContentEnd, withHeader.Length - bannerContentEnd);

        using var output = new MemoryStream();
        NdzWriter.Compress(withHeader, output);

        Assert.True(output.Length < withHeader.Length);
    }

    // Dictionary-specific round-trip coverage lives in DictionaryRoundTripTests.cs.
}
