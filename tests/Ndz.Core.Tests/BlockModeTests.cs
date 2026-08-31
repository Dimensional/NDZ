using System.Buffers.Binary;
using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Confirms the reader fails loudly - naming the exact frame/block/mode - on any
/// per-block compression mode other than <see cref="BlockMode.Plain"/>, rather than
/// misinterpreting bytes it doesn't understand. See BlockMode's remarks: real .ndz
/// files from the reference packer can and do use other modes (raw-dictionary,
/// filters), which this port can't decode yet.
/// </summary>
public class BlockModeTests
{
    /// <summary>
    /// Hand-builds a minimal, otherwise-valid .ndz with exactly one frame containing
    /// one block tagged with the given mode byte. The block's "compressed" bytes are
    /// empty and never actually decompressed - the mode check fires first.
    /// </summary>
    private static byte[] BuildSingleBlockNdz(byte modeByte)
    {
        const uint originalSize = 100;

        var frontMatter = new NdzFrontMatter
        {
            OriginalSize = originalSize,
            GameCode = 0,
            Banner = new byte[NdzConstants.BannerSlotLength],
            // Filters must be set for a mode array to exist at all - see NdzArchive's
            // remarks on GetDecompressedFrame. Without it the injected modeByte below
            // wouldn't be read as a mode at all, just as the start of block data.
            Flags = (NdzFlags.V2 | NdzFlags.ZStd | NdzFlags.Filters).WithBlockSize(NdzConstants.BlockSize),
        };

        // Frame payload: [u32 csize x 1][u8 mode x 1][compressed block bytes (empty)].
        byte[] framePayload = new byte[4 + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(framePayload.AsSpan(0, 4), 0); // block csize = 0
        framePayload[4] = modeByte;

        var bytes = new byte[NdzConstants.FrontMatterSize + framePayload.Length + NdzConstants.SeekTableEntrySize + NdzConstants.TrailerFooterSize];
        frontMatter.WriteTo(bytes.AsSpan(0, NdzConstants.FrontMatterSize));
        framePayload.CopyTo(bytes, NdzConstants.FrontMatterSize);

        int seekTableOffset = NdzConstants.FrontMatterSize + framePayload.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(seekTableOffset, 4), (uint)framePayload.Length); // frame csize
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(seekTableOffset + 4, 4), originalSize); // frame dsize

        int footerOffset = seekTableOffset + NdzConstants.SeekTableEntrySize;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footerOffset, 4), 1); // nframes
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footerOffset + 4, 4), NdzConstants.TrailerMagic);

        return bytes;
    }

    [Fact]
    public void PlainMode_OpensWithoutError()
    {
        // Sanity check that the hand-built fixture itself is otherwise well-formed.
        using var archive = NdzArchive.Open(BuildSingleBlockNdz((byte)BlockMode.Plain));
        Assert.Equal(100, archive.Length);
    }

    /// <summary>
    /// Confirmed against both the reference packer (which sets Filters unconditionally,
    /// with its own comment explaining why) and `ndztool.py`'s own decoder: without
    /// Filters, a frame has NO per-block mode array at all - every block is uniformly
    /// Dict (if a dictionary is present) or Plain (if not), and the byte immediately
    /// after the csize header is the start of block data, not a mode byte.
    /// </summary>
    [Fact]
    public void NoFiltersFlag_HasNoModeArray_InfersPlainUniformly()
    {
        const uint originalSize = 5;
        byte[] plainCompressed;
        using (var plain = new Nanook.GrindCore.ZStd.ZStdBlock(
            new Nanook.GrindCore.CompressionOptions { Type = Nanook.GrindCore.CompressionType.Level1, BlockSize = NdzConstants.BlockSize }))
        {
            var dst = new byte[plain.RequiredCompressOutputSize];
            int dstCount = dst.Length;
            byte[] src = { 1, 2, 3, 4, 5 };
            plain.Compress(src, 0, src.Length, dst, 0, ref dstCount);
            plainCompressed = dst.AsSpan(0, dstCount).ToArray();
        }

        var frontMatter = new NdzFrontMatter
        {
            OriginalSize = originalSize,
            GameCode = 0,
            Banner = new byte[NdzConstants.BannerSlotLength],
            // No Filters, no RawDictionary - the simple layout.
            Flags = (NdzFlags.V2 | NdzFlags.ZStd).WithBlockSize(NdzConstants.BlockSize),
        };

        // Frame payload: [u32 csize x 1][compressed block bytes] - no mode byte at all.
        byte[] framePayload = new byte[4 + plainCompressed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framePayload.AsSpan(0, 4), (uint)plainCompressed.Length);
        plainCompressed.CopyTo(framePayload, 4);

        var bytes = new byte[NdzConstants.FrontMatterSize + framePayload.Length + NdzConstants.SeekTableEntrySize + NdzConstants.TrailerFooterSize];
        frontMatter.WriteTo(bytes.AsSpan(0, NdzConstants.FrontMatterSize));
        framePayload.CopyTo(bytes, NdzConstants.FrontMatterSize);

        int seekTableOffset = NdzConstants.FrontMatterSize + framePayload.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(seekTableOffset, 4), (uint)framePayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(seekTableOffset + 4, 4), originalSize);

        int footerOffset = seekTableOffset + NdzConstants.SeekTableEntrySize;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footerOffset, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footerOffset + 4, 4), NdzConstants.TrailerMagic);

        using var archive = NdzArchive.Open(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, archive.DecompressAll());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public void NonPlainMode_ThrowsNamingFrameBlockAndMode(byte modeByte)
    {
        using var archive = NdzArchive.Open(BuildSingleBlockNdz(modeByte));

        var ex = Assert.Throws<NotSupportedException>(() => archive.DecompressAll());

        Assert.Contains("frame 0", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("block 0", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(modeByte.ToString(), ex.Message);
    }
}
