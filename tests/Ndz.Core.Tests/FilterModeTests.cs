using System.Buffers.Binary;
using Nanook.GrindCore;
using Nanook.GrindCore.ZStd;
using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Filter-mode integration: <see cref="BlockFiltersTests"/> already proves the raw
/// transforms are correct in isolation; this covers the two things that need the rest
/// of the pipeline - decode-side dispatch for each mode (hand-built fixtures, so it
/// doesn't depend on the writer's own compression-ratio heuristic ever picking a given
/// mode) and a real end-to-end write-then-read round trip on content deliberately
/// crafted so a filter actually wins (proving the write-side selection logic too).
/// </summary>
public class FilterModeTests
{
    /// <summary>
    /// Hand-builds a single-block .ndz where the block's stored bytes are
    /// <paramref name="plaintext"/> run through <paramref name="forwardFilter"/> and
    /// then compressed with a real, plain (non-dictionary) <see cref="ZStdBlock"/> -
    /// exactly what <c>NdzWriter</c> would produce for a block that picked
    /// <paramref name="mode"/> - tagged with that mode byte.
    /// </summary>
    private static byte[] BuildSingleFilteredBlockNdz(BlockMode mode, byte[] plaintext, Action<ReadOnlySpan<byte>, Span<byte>> forwardFilter)
    {
        var filtered = new byte[plaintext.Length];
        forwardFilter(plaintext, filtered);

        byte[] compressed;
        using (var plain = new ZStdBlock(new CompressionOptions { Type = CompressionType.Level19, BlockSize = plaintext.Length }))
        {
            var dst = new byte[plain.RequiredCompressOutputSize];
            int dstCount = dst.Length;
            CompressionResultCode result = plain.Compress(filtered, 0, filtered.Length, dst, 0, ref dstCount);
            Assert.Equal(CompressionResultCode.Success, result);
            compressed = dst.AsSpan(0, dstCount).ToArray();
        }

        var frontMatter = new NdzFrontMatter
        {
            OriginalSize = (uint)plaintext.Length,
            GameCode = 0,
            Banner = new byte[NdzConstants.BannerSlotLength],
            Flags = (NdzFlags.V2 | NdzFlags.ZStd | NdzFlags.Filters).WithBlockSize(plaintext.Length),
        };

        byte[] framePayload = new byte[4 + 1 + compressed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(framePayload.AsSpan(0, 4), (uint)compressed.Length);
        framePayload[4] = (byte)mode;
        compressed.CopyTo(framePayload, 5);

        var bytes = new byte[NdzConstants.FrontMatterSize + framePayload.Length + NdzConstants.SeekTableEntrySize + NdzConstants.TrailerFooterSize];
        frontMatter.WriteTo(bytes.AsSpan(0, NdzConstants.FrontMatterSize));
        framePayload.CopyTo(bytes, NdzConstants.FrontMatterSize);

        int seekTableOffset = NdzConstants.FrontMatterSize + framePayload.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(seekTableOffset, 4), (uint)framePayload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(seekTableOffset + 4, 4), (uint)plaintext.Length);

        int footerOffset = seekTableOffset + NdzConstants.SeekTableEntrySize;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footerOffset, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footerOffset + 4, 4), NdzConstants.TrailerMagic);

        return bytes;
    }

    private static byte[] RandomPlaintext(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Theory]
    [InlineData(BlockMode.Delta1, 1)]
    [InlineData(BlockMode.Delta2, 2)]
    [InlineData(BlockMode.Delta4, 4)]
    public void DeltaMode_DecodesBackToOriginalPlaintext(BlockMode mode, int stride)
    {
        byte[] plaintext = RandomPlaintext(4096, seed: stride);
        byte[] file = BuildSingleFilteredBlockNdz(mode, plaintext,
            (src, dst) => BlockFilters.DeltaForward(src, dst, stride));

        using var archive = NdzArchive.Open(file);
        Assert.Equal(plaintext, archive.DecompressAll());
    }

    [Theory]
    [InlineData(BlockMode.Shuffle2, 2)]
    [InlineData(BlockMode.Shuffle4, 4)]
    public void ShuffleMode_DecodesBackToOriginalPlaintext(BlockMode mode, int planes)
    {
        byte[] plaintext = RandomPlaintext(4096, seed: 1000 + planes);
        byte[] file = BuildSingleFilteredBlockNdz(mode, plaintext,
            (src, dst) => BlockFilters.ShuffleForward(src, dst, planes));

        using var archive = NdzArchive.Open(file);
        Assert.Equal(plaintext, archive.DecompressAll());
    }

    /// <summary>A random walk: each byte is the previous plus a small step - low local entropy per-delta, but no long-range LZ matches, so Delta filtering has a real advantage over plain zstd.</summary>
    private static byte[] BuildRandomWalkBlock(int length, int seed)
    {
        var data = new byte[length];
        var rng = new Random(seed);
        byte value = 128;
        for (int i = 0; i < length; i++)
        {
            value = (byte)(value + rng.Next(-2, 3));
            data[i] = value;
        }
        return data;
    }

    /// <summary>16-bit words with a genuinely random low byte and a constant high byte - Shuffle2 isolates the constant half into a near-free run, which plain zstd's LZ matcher can't do as well through the interleaving.</summary>
    private static byte[] BuildShuffleFriendlyBlock(int length, int seed)
    {
        var data = new byte[length];
        var rng = new Random(seed);
        for (int i = 0; i < length; i += 2)
        {
            data[i] = (byte)rng.Next(256);
            data[i + 1] = 0x40;
        }
        return data;
    }

    [Fact]
    public void Compress_OnDeltaFriendlyContent_ActuallyPicksADeltaMode_AndRoundTrips()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        byte[] walk = BuildRandomWalkBlock(NdzConstants.BlockSize, seed: 7);
        // Placed at frame-relative offset covering one whole block, past the header/banner.
        Array.Copy(walk, 0, rom, NdzConstants.BlockSize * 2, walk.Length);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes);
        Assert.Equal(rom, archive.DecompressAll());

        BlockMode modeOfThatBlock = ReadModeByte(bytes, blockIndex: 2);
        Assert.True(modeOfThatBlock is BlockMode.Delta1 or BlockMode.Delta2 or BlockMode.Delta4,
            $"Expected a Delta mode to win on random-walk content, got {modeOfThatBlock}.");
    }

    [Fact]
    public void Compress_OnShuffleFriendlyContent_ActuallyPicksAShuffleMode_AndRoundTrips()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        byte[] block = BuildShuffleFriendlyBlock(NdzConstants.BlockSize, seed: 9);
        Array.Copy(block, 0, rom, NdzConstants.BlockSize * 2, block.Length);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes);
        Assert.Equal(rom, archive.DecompressAll());

        BlockMode modeOfThatBlock = ReadModeByte(bytes, blockIndex: 2);
        Assert.True(modeOfThatBlock is BlockMode.Shuffle2 or BlockMode.Shuffle4,
            $"Expected a Shuffle mode to win on shuffle-friendly content, got {modeOfThatBlock}.");
    }

    [Fact]
    public void Compress_WithFiltersDisabled_NeverPicksAFilterMode()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        byte[] walk = BuildRandomWalkBlock(NdzConstants.BlockSize, seed: 7);
        Array.Copy(walk, 0, rom, NdzConstants.BlockSize * 2, walk.Length);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, enableFilters: false);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes);
        Assert.Equal(rom, archive.DecompressAll());
        // Filters bit stays set regardless (the mode array always exists to distinguish Plain/Dict).
        Assert.True(archive.FrontMatter.Flags.HasFlag(NdzFlags.Filters));

        BlockMode modeOfThatBlock = ReadModeByte(bytes, blockIndex: 2);
        Assert.True(modeOfThatBlock is BlockMode.Plain or BlockMode.Dict,
            $"Expected no filter mode with enableFilters:false, got {modeOfThatBlock}.");
    }

    /// <summary>Reads frame 0's mode byte for the given block index directly out of the raw file bytes - same layout logic as DictionaryRoundTripTests.</summary>
    private static BlockMode ReadModeByte(byte[] ndzBytes, int blockIndex)
    {
        using var archive = NdzArchive.Open(ndzBytes);
        var entry = archive.SeekTable[0];
        int blockCount = (int)((entry.DecompressedSize + NdzConstants.BlockSize - 1) / NdzConstants.BlockSize);
        long payloadStart = NdzConstants.FrontMatterSize + archive.FrontMatter.DictionaryStoredSize;
        return (BlockMode)ndzBytes[payloadStart + blockCount * 4 + blockIndex];
    }
}
