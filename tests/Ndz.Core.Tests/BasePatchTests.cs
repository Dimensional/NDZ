using System.Buffers.Binary;
using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// End-to-end base-patch mode: windowed raw-dictionary compression against a second
/// ("base") ROM - see <see cref="BaseRomIndex"/> and docs/ndz-remaining-work.md's
/// "Base-ROM patch mode" section. <see cref="BaseRomIndexTests"/> and
/// <see cref="Blake2bTests"/> already cover the pieces this builds on in isolation.
/// </summary>
public class BasePatchTests
{
    /// <summary>Builds a "target" ROM that shares a large, grain-aligned chunk of content with <paramref name="baseRom"/>, elsewhere filled with independent random bytes.</summary>
    private static byte[] BuildTargetSharingContentWithBase(byte[] baseRom, int targetSize, int baseContentOffset, int sharedLength, int targetContentOffset, string gameCode, int seed)
    {
        byte[] target = TestRom.Build(targetSize, gameCode: gameCode, seed: seed);
        Array.Copy(baseRom, baseContentOffset, target, targetContentOffset, sharedLength);
        return target;
    }

    private static byte[] BuildBaseRom(int size, int seed) => TestRom.Build(size, gameCode: "BASE", seed: seed);

    [Fact]
    public void Compress_WithBaseRom_RoundTripsByteIdentical()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize * 2, seed: 1);
        byte[] target = BuildTargetSharingContentWithBase(
            baseRom, NdzConstants.FrameSize * 2,
            baseContentOffset: BaseRomIndex.Grain * 10, sharedLength: NdzConstants.BlockSize * 4,
            targetContentOffset: BaseRomIndex.Grain * 10, gameCode: "TRGT", seed: 2);

        using var output = new MemoryStream();
        NdzWriter.Compress(target, output, baseRom: baseRom);

        using var archive = NdzArchive.Open(output.ToArray(), baseRom: baseRom);
        Assert.Equal(target, archive.DecompressAll());
    }

    [Fact]
    public void Compress_WithBaseRom_SetsFrontMatterFields()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 3);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 4);

        using var output = new MemoryStream();
        NdzWriter.Compress(target, output, baseRom: baseRom);

        using var archive = NdzArchive.Open(output.ToArray(), baseRom: baseRom);
        Assert.True(archive.FrontMatter.Flags.HasFlag(NdzFlags.BasePatch));
        Assert.Equal((uint)baseRom.Length, archive.FrontMatter.BaseOriginalSize);

        uint expectedBaseGameCode = BinaryPrimitives.ReadUInt32LittleEndian(baseRom.AsSpan(0x0C, 4));
        Assert.Equal(expectedBaseGameCode, archive.FrontMatter.BaseGameCode);
        Assert.Equal(Blake2b.Hash(baseRom.AsSpan(0, 0x200), 8), archive.FrontMatter.BaseHeaderHash);
    }

    [Fact]
    public void Compress_WithBaseRom_ProducesSmallerOutput_ThanWithoutBase()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize * 2, seed: 5);
        byte[] target = BuildTargetSharingContentWithBase(
            baseRom, NdzConstants.FrameSize * 2,
            baseContentOffset: BaseRomIndex.Grain * 20, sharedLength: NdzConstants.BlockSize * 6,
            targetContentOffset: BaseRomIndex.Grain * 20, gameCode: "TRGT", seed: 6);

        using var withBase = new MemoryStream();
        NdzWriter.Compress(target, withBase, baseRom: baseRom);

        using var withoutBase = new MemoryStream();
        NdzWriter.Compress(target, withoutBase);

        long withBasePayload = SumSeekTable(withBase.ToArray(), baseRom);
        long withoutBasePayload = SumSeekTable(withoutBase.ToArray(), null);

        Assert.True(withBasePayload < withoutBasePayload,
            $"Expected base-patched compression ({withBasePayload} bytes) to beat standalone " +
            $"compression ({withoutBasePayload} bytes) on a target crafted to share content with the base.");
    }

    [Fact]
    public void Compress_WithBaseRom_UsesAtLeastOneBaseWindow()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize * 2, seed: 7);
        byte[] target = BuildTargetSharingContentWithBase(
            baseRom, NdzConstants.FrameSize * 2,
            baseContentOffset: BaseRomIndex.Grain * 30, sharedLength: NdzConstants.BlockSize * 4,
            targetContentOffset: BaseRomIndex.Grain * 30, gameCode: "TRGT", seed: 8);

        using var output = new MemoryStream();
        NdzWriter.Compress(target, output, baseRom: baseRom);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes, baseRom: baseRom);
        var entry = archive.SeekTable[0];
        int blockCount = (int)((entry.DecompressedSize + NdzConstants.BlockSize - 1) / NdzConstants.BlockSize);
        long payloadStart = NdzConstants.FrontMatterSize;

        bool anyWindowed = false;
        for (int b = 0; b < blockCount; b++)
        {
            uint baseOff = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)payloadStart + blockCount * 4 + blockCount + b * 4, 4));
            if (baseOff != BaseRomIndex.NoWindowSentinel)
                anyWindowed = true;
        }

        Assert.True(anyWindowed, "Expected at least one block to use a base window.");
    }

    [Fact]
    public void Open_BasePatchFile_WithoutBaseRom_ThrowsNotSupported()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 9);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 10);

        using var output = new MemoryStream();
        NdzWriter.Compress(target, output, baseRom: baseRom);

        Assert.Throws<NotSupportedException>(() => NdzArchive.Open(output.ToArray()));
    }

    [Fact]
    public void Open_BasePatchFile_WithWrongSizeBase_ThrowsInvalidData()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 11);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 12);

        using var output = new MemoryStream();
        NdzWriter.Compress(target, output, baseRom: baseRom);

        byte[] wrongSizeBase = BuildBaseRom(NdzConstants.FrameSize * 3, seed: 11);
        Assert.Throws<InvalidDataException>(() => NdzArchive.Open(output.ToArray(), baseRom: wrongSizeBase));
    }

    [Fact]
    public void Open_BasePatchFile_WithWrongContentBase_ThrowsInvalidData()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 13);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 14);

        using var output = new MemoryStream();
        NdzWriter.Compress(target, output, baseRom: baseRom);

        // Same size, same game code even, but different header bytes -> hash mismatch.
        byte[] differentBase = BuildBaseRom(NdzConstants.FrameSize, seed: 99);
        Assert.Throws<InvalidDataException>(() => NdzArchive.Open(output.ToArray(), baseRom: differentBase));
    }

    /// <summary>
    /// A base ROM under <see cref="BaseRomIndex.WindowSize"/> (16 KiB) is refused up
    /// front rather than risked - see <see cref="NdzWriter.Compress"/>'s `baseRom`
    /// remarks for the confirmed-live `ndztool.py` pack-succeeds-then-unpack-fails bug
    /// this sidesteps. A minimal homebrew binary can genuinely be this small, unlike any
    /// ordinary commercial ROM.
    /// </summary>
    [Fact]
    public void Compress_WithBaseRomUnderWindowSize_ThrowsArgumentException()
    {
        byte[] baseRom = TestRom.Build(BaseRomIndex.WindowSize - 1, gameCode: "BASE");
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT");

        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => NdzWriter.Compress(target, output, baseRom: baseRom));
    }

    /// <summary>The floor is exact: exactly <see cref="BaseRomIndex.WindowSize"/> bytes is accepted.</summary>
    [Fact]
    public void Compress_WithBaseRomExactlyAtWindowSize_Succeeds()
    {
        byte[] baseRom = TestRom.Build(BaseRomIndex.WindowSize, gameCode: "BASE");
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT");

        using var output = new MemoryStream();
        NdzWriter.Compress(target, output, baseRom: baseRom);

        using var archive = NdzArchive.Open(output.ToArray(), baseRom: baseRom);
        Assert.Equal(target, archive.DecompressAll());
    }

    /// <summary>Matches `ndztool.py`'s own `cmd_info`: never needs `--base`, even for a BasePatch file.</summary>
    [Fact]
    public void ReadInfo_OnBasePatchFile_DoesNotRequireBaseRom()
    {
        byte[] baseRom = BuildBaseRom(NdzConstants.FrameSize, seed: 15);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 16);

        using var output = new MemoryStream();
        NdzWriter.Compress(target, output, baseRom: baseRom);

        var (frontMatter, seekTable) = NdzArchive.ReadInfo(output.ToArray());
        Assert.True(frontMatter.Flags.HasFlag(NdzFlags.BasePatch));
        Assert.Equal((uint)baseRom.Length, frontMatter.BaseOriginalSize);
        Assert.NotEmpty(seekTable);
    }

    private static long SumSeekTable(byte[] ndzBytes, byte[]? baseRom)
    {
        using var archive = NdzArchive.Open(ndzBytes, baseRom);
        return archive.SeekTable.Sum(e => (long)e.CompressedSize);
    }
}
