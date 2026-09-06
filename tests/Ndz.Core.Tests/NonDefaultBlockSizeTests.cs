using System.Linq;
using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Confirmed against `ndztool.py`'s own decode logic (see reference/mena-patchbench)
/// that block size is a real per-file variable read from the front-matter flags, not
/// always 8192: that
/// script's own default (when the log2 subfield is left at 0) is 4096.
/// Before this test existed, <c>NdzArchive</c> hardcoded
/// <see cref="NdzConstants.BlockSize"/> at every block-boundary computation instead of
/// reading <see cref="NdzFrontMatter"/>.Flags - a real bug for any file using a
/// different block size, just one <c>NdzWriter</c> itself never triggered (it always
/// wrote 8192). This proves the fix by actually writing and reading back a 4096-block file,
/// not just a synthetic byte layout.
/// </summary>
public class NonDefaultBlockSizeTests
{
    [Fact]
    public void RoundTrips_With4096ByteBlocks()
    {
        // Multiple frames, with the last one short, so both a full and a partial block
        // count are exercised at the smaller block size.
        byte[] rom = TestRom.Build(NdzConstants.FrameSize * 2 + 4096 * 3);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, blockSize: 4096);

        var bytes = output.ToArray();
        using var archive = NdzArchive.Open(bytes);

        Assert.Equal(4096, archive.FrontMatter.Flags.GetBlockSize());
        byte[] rebuilt = archive.DecompressAll();
        Assert.Equal(rom, rebuilt);
    }

    [Fact]
    public void RoundTrips_With2048ByteBlocks_AndSupportsRandomAccess()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize * 3);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, blockSize: 2048);

        using var archive = NdzArchive.Open(output.ToArray());
        Assert.Equal(2048, archive.FrontMatter.Flags.GetBlockSize());

        // Read a chunk that straddles two 2 KiB blocks within one frame.
        var buffer = new byte[1024];
        int read = archive.ReadAt(2048 - 512, buffer);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(rom.AsSpan(2048 - 512, buffer.Length).ToArray(), buffer);
    }

    /// <summary>Proves the 2026-09-06 firmware fix's new ceiling actually works end to end, not just that the check moved - see <see cref="NdzConstants.MaxBlockSize"/>'s remarks.</summary>
    [Theory]
    [InlineData(16384)]
    [InlineData(32768)]
    public void RoundTrips_WithNewlyAllowedLargerBlocks(int blockSize)
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize * 2 + blockSize * 3);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, blockSize: blockSize);

        using var archive = NdzArchive.Open(output.ToArray());
        Assert.Equal(blockSize, archive.FrontMatter.Flags.GetBlockSize());
        Assert.Equal(rom, archive.DecompressAll());
    }

    [Fact]
    public void RejectsNonPowerOfTwoBlockSize()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => NdzWriter.Compress(rom, output, blockSize: 5000));
    }

    /// <summary>
    /// Hardware ceiling, not a preference - see <see cref="NdzConstants.MaxBlockSize"/>'s
    /// remarks (32 KiB since the 2026-09-06 firmware fix, was 8 KiB - `ndztool.py`'s own
    /// `--block-size` validation still refuses past the old 8 KiB ceiling since its local
    /// copy predates that fix, which is why this can no longer cross-check against it
    /// directly the way <see cref="RejectsCompressionLevelAboveTheHardwareLimit"/> still
    /// does).
    /// </summary>
    [Fact]
    public void RejectsBlockSizeAboveTheHardwareLimit()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => NdzWriter.Compress(rom, output, blockSize: NdzConstants.MaxBlockSize * 2));
    }

    /// <summary>
    /// Hardware ceiling, not a preference - see <see cref="NdzConstants.MaxLevel"/>'s
    /// remarks. Confirmed against `ndztool.py`'s own `--level` validation, which refuses
    /// the same way for the same reason.
    /// </summary>
    [Fact]
    public void RejectsCompressionLevelAboveTheHardwareLimit()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NdzWriter.Compress(rom, output, level: Nanook.GrindCore.CompressionType.Level20));
    }
}
