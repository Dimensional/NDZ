using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Frame size (the outer seek-table bucketing unit) is a real per-file variable, not a
/// format-wide constant - `pack.rs` hardcodes it at 128 KiB with no override, but
/// `ndztool.py`'s own `--frame-size` is a real, adjustable CLI argument (default `128k`),
/// and nothing in the front-matter records whichever value a given file actually used.
/// <see cref="NdzArchive"/> derives real frame boundaries from the seek table's own
/// per-frame decompressed sizes (a cumulative-sum binary search, see its
/// <c>FindFrameIndex</c>), never from <see cref="NdzConstants.FrameSize"/>, so it opens
/// and randomly-accesses a file packed at any frame size correctly - these tests actually
/// exercise a non-default frame size end-to-end (write, open, random-access), not just
/// the arithmetic. See <see cref="NdzWriter.Compress"/>'s `frameSize` remarks.
/// </summary>
public class FrameSizeTests
{
    [Fact]
    public void RoundTrips_WithSmallNonDefaultFrameSize()
    {
        // Several frames plus a short trailing one, well under the default 128 KiB, so
        // the seek table actually has more than one entry to prove boundary lookup works.
        byte[] rom = TestRom.Build(4096 * 5 + 777);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, blockSize: 512, frameSize: 4096);

        using var archive = NdzArchive.Open(output.ToArray());
        Assert.Equal(6, archive.SeekTable.Count); // 5 full 4096-byte frames + one short one
        Assert.Equal(4096u, archive.SeekTable[0].DecompressedSize);
        Assert.Equal(777u, archive.SeekTable[^1].DecompressedSize);

        byte[] rebuilt = archive.DecompressAll();
        Assert.Equal(rom, rebuilt);
    }

    [Fact]
    public void RandomAccess_WorksAcrossNonDefaultFrameBoundaries()
    {
        byte[] rom = TestRom.Build(4096 * 4);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, blockSize: 512, frameSize: 4096);

        using var archive = NdzArchive.Open(output.ToArray());

        // Straddles the boundary between frame 1 and frame 2.
        var buffer = new byte[600];
        int read = archive.ReadAt(4096 * 2 - 300, buffer);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(rom.AsSpan(4096 * 2 - 300, buffer.Length).ToArray(), buffer);

        // A read entirely within a single non-default-size frame.
        read = archive.ReadAt(4096 * 3 + 100, buffer);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(rom.AsSpan(4096 * 3 + 100, buffer.Length).ToArray(), buffer);
    }

    /// <summary>
    /// Frame size has no power-of-two requirement (unlike block size) - it's purely a
    /// seek-table bucketing choice, confirmed against `ndztool.py`'s own lack of such a
    /// check for `--frame-size` (only `--block-size` goes through `_block_log2`, which
    /// enforces it). This uses a deliberately odd, non-power-of-two frame size to prove
    /// nothing here secretly assumes one.
    /// </summary>
    [Fact]
    public void RoundTrips_WithNonPowerOfTwoFrameSize()
    {
        byte[] rom = TestRom.Build(10_000 * 3 + 42);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, blockSize: 512, frameSize: 10_000);

        using var archive = NdzArchive.Open(output.ToArray());
        Assert.Equal(4, archive.SeekTable.Count);
        byte[] rebuilt = archive.DecompressAll();
        Assert.Equal(rom, rebuilt);
    }

    [Fact]
    public void RejectsFrameSizeSmallerThanBlockSize()
    {
        byte[] rom = TestRom.Build(4096);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NdzWriter.Compress(rom, output, blockSize: 8192, frameSize: 4096));
    }

    [Fact]
    public void RejectsNonPositiveFrameSize()
    {
        byte[] rom = TestRom.Build(4096);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NdzWriter.Compress(rom, output, blockSize: 512, frameSize: 0));
    }

    /// <summary>NdzPairWriter passes frameSize through to both sub-packs - matches ndztool.py's cmd_pack --pair-out, which reuses the same frame_size for both pack_ndz_blob calls.</summary>
    [Fact]
    public void PairWriter_PassesFrameSizeThroughToBothSubPacks()
    {
        // Comfortably over BaseRomIndex.WindowSize (16 KiB) - a base ROM shorter than the
        // window is a real, confirmed-live edge case in ndztool.py itself (pack succeeds,
        // unpack throws "base window out of range", verified directly against the real
        // tool) that no actual .nds ever hits, so it's deliberately avoided here rather
        // than "fixed" - matching that edge case's real behavior, not diverging from it,
        // is what a faithful port should do.
        byte[] baseRom = TestRom.Build(4096 * 8, gameCode: "BASE");
        byte[] targetRom = TestRom.Build(4096 * 8, gameCode: "TARG");

        using var output = new MemoryStream();
        NdzPairWriter.Write(output, baseRom, targetRom, blockSize: 512, frameSize: 4096);

        var pair = NdzPairContainer.Read(output.ToArray());
        var (_, baseSeekTable) = pair.ReadEntryInfo(pair.PlainEntryIndex);
        Assert.Equal(8, baseSeekTable.Count);

        int targetIndex = 1 - pair.PlainEntryIndex;
        var (_, targetSeekTable) = pair.ReadEntryInfo(targetIndex);
        Assert.Equal(8, targetSeekTable.Count);

        Assert.Equal(baseRom, pair.DecompressEntry(pair.PlainEntryIndex));
        Assert.Equal(targetRom, pair.DecompressEntry(targetIndex));
    }
}
