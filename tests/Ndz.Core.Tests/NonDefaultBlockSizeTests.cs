using System.Linq;
using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Confirmed against `ndzunpack.py`'s own decode logic (see reference/mena-patchbench -
/// we have that script, not the `patchbench.py` module it imports and defers to) that
/// block size is a real per-file variable read from the front-matter flags, not always
/// 8192: that script's own default (when the log2 subfield is left at 0) is 4096.
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
    public void RoundTrips_With16384ByteBlocks_AndSupportsRandomAccess()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize * 3);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, blockSize: 16384);

        using var archive = NdzArchive.Open(output.ToArray());
        Assert.Equal(16384, archive.FrontMatter.Flags.GetBlockSize());

        // Read a chunk that straddles two 16 KiB blocks within one frame.
        var buffer = new byte[4096];
        int read = archive.ReadAt(16384 - 2048, buffer);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(rom.AsSpan(16384 - 2048, buffer.Length).ToArray(), buffer);
    }

    [Fact]
    public void RejectsNonPowerOfTwoBlockSize()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => NdzWriter.Compress(rom, output, blockSize: 5000));
    }
}
