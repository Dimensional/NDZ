using System.Buffers.Binary;
using Ndz.Core.Compression;
using Ndz.Core.Format;
using Ndz.Core.XDelta;

namespace Ndz.Core.Tests;

/// <summary>
/// End-to-end `.delta.ndz` "hack container" support - a full target ROM stored cheaply
/// against a base ROM's own already-packed `.ndz`, reverse-engineered from real
/// ndz-studio output (not guessed) - see docs/ndz-format-spec.md's "xdelta-based
/// `.delta.ndz` / hack container" section for the full format writeup this implements.
/// <see cref="BasePatchTests"/> covers the older, unrelated windowed-dictionary base-patch
/// mode this format deliberately does NOT use.
/// </summary>
public class HackContainerTests
{
    private static byte[] BuildBaseRom(int size, int seed, string gameCode = "BASE") => TestRom.Build(size, gameCode: gameCode, seed: seed);

    /// <summary>Packs <paramref name="baseRom"/> into an ordinary (non-hack) .ndz, as if it had already been shared via ndz-studio's "Pack .ndz" - what <see cref="HackContainerWriter"/>'s `baseNdzBytes` parameter expects.</summary>
    private static byte[] BuildBaseNdz(byte[] baseRom, int rawDictionarySize = 0)
    {
        using var output = new MemoryStream();
        NdzWriter.Compress(baseRom, output, rawDictionarySize: rawDictionarySize);
        return output.ToArray();
    }

    /// <summary>One block-sized repeated pattern, planted starting at <paramref name="repeatStart"/> through the end of <paramref name="rom"/> - mirrors <c>DictionaryRoundTripTests</c>' own construction, which reliably gives <see cref="RawDictionaryBuilder"/> real duplicate content to find.</summary>
    private static byte[] PlantRepeatedPattern(byte[] rom, int seed, int repeatStart)
    {
        var pattern = new byte[NdzConstants.BlockSize];
        new Random(seed ^ 0x5EED).NextBytes(pattern);
        for (int off = repeatStart; off + pattern.Length <= rom.Length; off += pattern.Length)
            pattern.CopyTo(rom, off);
        return pattern;
    }

    /// <summary>Hand-parses every block's (mode, data) out of a hack-container's raw bytes - there's no `baseOff[n]` array to skip (unlike an ordinary base-patch frame), so this is simpler than <c>BasePatchTests</c>' own equivalent.</summary>
    private static List<(int FrameIndex, int BlockIndex, BlockMode Mode, ReadOnlyMemory<byte> Data)> ReadHackBlocks(byte[] deltaNdzBytes, int blockSize)
    {
        var (frontMatter, seekTable) = NdzArchive.ReadInfo(deltaNdzBytes);
        Assert.True(frontMatter.Flags.HasFlag(NdzFlags.HackContainer));

        var result = new List<(int, int, BlockMode, ReadOnlyMemory<byte>)>();
        long cursor = NdzConstants.FrontMatterSize;
        int frameIndex = 0;
        foreach (var entry in seekTable)
        {
            int blockCount = (int)((entry.DecompressedSize + blockSize - 1) / blockSize);
            int headerSize = blockCount * 4 + blockCount;
            long dataStart = cursor + headerSize;
            long dataOffset = 0;
            for (int b = 0; b < blockCount; b++)
            {
                uint csize = BinaryPrimitives.ReadUInt32LittleEndian(deltaNdzBytes.AsSpan((int)cursor + b * 4, 4));
                byte mode = deltaNdzBytes[cursor + blockCount * 4 + b];
                var data = new ReadOnlyMemory<byte>(deltaNdzBytes, (int)(dataStart + dataOffset), (int)csize);
                result.Add((frameIndex, b, (BlockMode)mode, data));
                dataOffset += csize;
            }
            cursor += entry.CompressedSize;
            frameIndex++;
        }
        return result;
    }

    [Fact]
    public void RoundTrips_SmallSyntheticRom()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 1);
        byte[] baseNdz = BuildBaseNdz(baseRom);

        byte[] target = (byte[])baseRom.Clone();
        Array.Copy(TestRom.Build(NdzConstants.BlockSize, seed: 99), 0, target, NdzConstants.BlockSize * 3, NdzConstants.BlockSize);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);

        using var archive = NdzArchive.Open(output.ToArray(), baseNdzBytes: baseNdz);
        Assert.Equal(target, archive.DecompressAll());
    }

    [Fact]
    public void RoundTrips_LargeMostlyIdenticalRomHackShape()
    {
        // The realistic case: a big shared base with one small changed region, like a ROM
        // hack or a version diff - dominated by Verbatim blocks, one small changed region.
        byte[] baseRom = BuildBaseRom(2 * 1024 * 1024, seed: 2);
        byte[] baseNdz = BuildBaseNdz(baseRom);

        byte[] target = (byte[])baseRom.Clone();
        TestRom.Build(4096, seed: 3).AsSpan(0, 4096).CopyTo(target.AsSpan(1_000_000, 4096));

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes, baseNdzBytes: baseNdz);
        Assert.Equal(target, archive.DecompressAll());

        var blocks = ReadHackBlocks(bytes, NdzConstants.BlockSize);
        int verbatimCount = blocks.Count(b => b.Mode == BlockMode.Verbatim);
        Assert.True(verbatimCount > blocks.Count / 2, $"Expected most of {blocks.Count} blocks to be Verbatim (unchanged/shared content), got {verbatimCount}.");
    }

    [Fact]
    public void RoundTrips_WithContentRelocation()
    {
        // A run of target blocks copied from a deliberately non-block-aligned, non-2048-
        // grain-aligned offset in the base - confirmed real behavior (not just guessed):
        // ndz-studio's own real output has whole clusters of blocks pointing at a
        // relocated, unaligned base offset, not just each block's own position.
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize * 2, seed: 4);
        byte[] baseNdz = BuildBaseNdz(baseRom);

        const int relocatedBaseOffset = NdzConstants.BlockSize * 5 + 37; // deliberately unaligned
        const int targetBlockOffset = NdzConstants.BlockSize * 20;
        byte[] target = (byte[])baseRom.Clone();
        Array.Copy(baseRom, relocatedBaseOffset, target, targetBlockOffset, NdzConstants.BlockSize);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes, baseNdzBytes: baseNdz);
        Assert.Equal(target, archive.DecompressAll());

        var blocks = ReadHackBlocks(bytes, NdzConstants.BlockSize);
        int expectedFrameIndex = targetBlockOffset / NdzConstants.FrameSize;
        int expectedBlockIndex = (targetBlockOffset % NdzConstants.FrameSize) / NdzConstants.BlockSize;
        var relocatedBlock = blocks.Single(b => b.FrameIndex == expectedFrameIndex && b.BlockIndex == expectedBlockIndex);
        Assert.Equal(BlockMode.Verbatim, relocatedBlock.Mode);
        uint recordedOffset = BinaryPrimitives.ReadUInt32LittleEndian(relocatedBlock.Data.Span);
        Assert.Equal((uint)relocatedBaseOffset, recordedOffset);
    }

    [Fact]
    public void RoundTrips_BaseWithNoRawDictionary()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 5);
        byte[] baseNdz = BuildBaseNdz(baseRom, rawDictionarySize: 0);
        Assert.False(NdzArchive.ReadInfo(baseNdz).FrontMatter.HasDictionary);

        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 6);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes, baseNdzBytes: baseNdz);
        Assert.Equal(target, archive.DecompressAll());

        var blocks = ReadHackBlocks(bytes, NdzConstants.BlockSize);
        Assert.DoesNotContain(blocks, b => b.Mode == BlockMode.Dict);
    }

    [Fact]
    public void RoundTrips_ViaXdeltaPatchOverload()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 7);
        byte[] baseNdz = BuildBaseNdz(baseRom);

        byte[] target = (byte[])baseRom.Clone();
        target[5000] ^= 0xFF;
        byte[] patch = XDeltaCodec.Generate(baseRom, target);

        using var viaPatch = new MemoryStream();
        HackContainerWriter.CompressFromPatch(baseRom, patch, baseNdz, viaPatch);

        using var viaTarget = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, viaTarget);

        using var archiveViaPatch = NdzArchive.Open(viaPatch.ToArray(), baseNdzBytes: baseNdz);
        using var archiveViaTarget = NdzArchive.Open(viaTarget.ToArray(), baseNdzBytes: baseNdz);
        Assert.Equal(target, archiveViaPatch.DecompressAll());
        Assert.Equal(archiveViaTarget.DecompressAll(), archiveViaPatch.DecompressAll());
    }

    [Fact]
    public void PacksModeZeroBlocks_WhenBaseHasRawDictionary()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize * 2, seed: 8);
        byte[] pattern = PlantRepeatedPattern(baseRom, seed: 8, repeatStart: NdzConstants.BlockSize * 2);
        byte[] baseNdz = BuildBaseNdz(baseRom, rawDictionarySize: 32 * 1024);
        Assert.True(NdzArchive.ReadInfo(baseNdz).FrontMatter.HasDictionary);

        // A near-copy of the dictionary's own pattern (a few bytes flipped) - close
        // enough to compress great against the base's dict, but not byte-identical to
        // anything in the base, so Verbatim/mode-7 can't win it instead.
        byte[] target = TestRom.Build(NdzConstants.FrameSize * 2, gameCode: "TRGT", seed: 9);
        byte[] nearPattern = (byte[])pattern.Clone();
        nearPattern[10] ^= 0xFF;
        nearPattern[2000] ^= 0xFF;
        nearPattern[6000] ^= 0xFF;
        nearPattern.CopyTo(target, NdzConstants.BlockSize * 10);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes, baseNdzBytes: baseNdz);
        Assert.Equal(target, archive.DecompressAll());

        var blocks = ReadHackBlocks(bytes, NdzConstants.BlockSize);
        Assert.Contains(blocks, b => b.Mode == BlockMode.Dict);
    }

    [Fact]
    public void Open_HackContainerFile_WithoutBaseNdzBytes_ThrowsNotSupported()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 10);
        byte[] baseNdz = BuildBaseNdz(baseRom);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 11);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);

        Assert.Throws<NotSupportedException>(() => NdzArchive.Open(output.ToArray()));
    }

    [Fact]
    public void Open_HackContainerFile_WithWrongBaseNdz_ThrowsInvalidData()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 12);
        byte[] baseNdz = BuildBaseNdz(baseRom);
        byte[] target = (byte[])baseRom.Clone();
        target[100] ^= 0xFF;

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);

        // A shorter, unrelated base .ndz - any Verbatim block's recorded offset will run
        // past its (much shorter) decompressed length.
        byte[] wrongBaseRom = BuildBaseRom(NdzConstants.BlockSize, seed: 999, gameCode: "WRNG");
        byte[] wrongBaseNdz = BuildBaseNdz(wrongBaseRom);

        Assert.Throws<InvalidDataException>(() => NdzArchive.Open(output.ToArray(), baseNdzBytes: wrongBaseNdz).DecompressAll());
    }

    [Fact]
    public void Open_HackContainerFile_CorruptOrTruncated_ThrowsInvalidData()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 13);
        byte[] baseNdz = BuildBaseNdz(baseRom);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 14);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);
        byte[] bytes = output.ToArray();

        byte[] truncated = bytes[..(bytes.Length - 100)];
        Assert.ThrowsAny<Exception>(() => NdzArchive.Open(truncated, baseNdzBytes: baseNdz).DecompressAll());
    }

    [Fact]
    public void NdzFrontMatter_Read_RejectsHackContainerBitWithoutBasePatch()
    {
        var frontMatter = new NdzFrontMatter
        {
            OriginalSize = 100,
            GameCode = 0,
            Banner = new byte[NdzConstants.BannerSlotLength],
            Flags = (NdzFlags.V2 | NdzFlags.ZStd | NdzFlags.Filters | NdzFlags.HackContainer).WithBlockSize(NdzConstants.BlockSize),
        };
        var bytes = new byte[NdzConstants.FrontMatterSize];
        frontMatter.WriteTo(bytes);

        Assert.Throws<NotSupportedException>(() => NdzFrontMatter.Read(bytes));
    }

    [Fact]
    public void Compress_SetsFrontMatterFields()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 15);
        byte[] baseNdz = BuildBaseNdz(baseRom);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 16);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(target, baseRom, baseNdz, output);

        var (frontMatter, _) = NdzArchive.ReadInfo(output.ToArray());
        Assert.True(frontMatter.Flags.HasFlag(NdzFlags.BasePatch));
        Assert.True(frontMatter.Flags.HasFlag(NdzFlags.HackContainer));
        Assert.Equal(0u, frontMatter.BaseOriginalSize);
        Assert.Equal(0u, frontMatter.BaseGameCode);
        Assert.Equal(new byte[NdzConstants.BaseHeaderHashLength], frontMatter.BaseHeaderHash);
        Assert.False(frontMatter.HasDictionary);

        // GameCode holds the BASE's game code, not the target's - so a reader/the cart
        // knows which base .ndz this hack needs.
        var (baseFrontMatter, _) = NdzArchive.ReadInfo(baseNdz);
        Assert.Equal(baseFrontMatter.GameCode, frontMatter.GameCode);
        Assert.NotEqual(BinaryPrimitives.ReadUInt32LittleEndian(target.AsSpan(0x0C, 4)), frontMatter.GameCode);
    }
}
