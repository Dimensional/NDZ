using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Pair container: two complete `.ndz` blobs (a self-contained base, and a second ROM
/// base-patched against it) bundled with no external base needed to unpack either - see
/// <see cref="NdzPairWriter"/>/<see cref="NdzPairContainer"/> and
/// docs/ndz-remaining-work.md's "Pair container" section.
/// </summary>
public class PairContainerTests
{
    [Fact]
    public void Write_ThenRead_RoundTripsBothRomsByteIdentical()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 1);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 2);

        using var output = new MemoryStream();
        NdzPairWriter.Write(output, baseRom, target);

        var container = NdzPairContainer.Read(output.ToArray());
        Assert.Equal(2, container.Entries.Count);

        Assert.Equal(baseRom, container.DecompressEntry(0));
        Assert.Equal(target, container.DecompressEntry(1));
    }

    [Fact]
    public void Read_IdentifiesThePlainEntryCorrectly()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 3);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 4);

        using var output = new MemoryStream();
        NdzPairWriter.Write(output, baseRom, target);
        var container = NdzPairContainer.Read(output.ToArray());

        Assert.Equal(0, container.PlainEntryIndex);

        var (fm0, _) = container.ReadEntryInfo(0);
        var (fm1, _) = container.ReadEntryInfo(1);
        Assert.False(fm0.Flags.HasFlag(NdzFlags.BasePatch));
        Assert.True(fm1.Flags.HasFlag(NdzFlags.BasePatch));
    }

    [Fact]
    public void Read_EntryOffsetsAreSixteenKiBAligned()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 5);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 6);

        using var output = new MemoryStream();
        NdzPairWriter.Write(output, baseRom, target);
        var container = NdzPairContainer.Read(output.ToArray());

        Assert.All(container.Entries, e => Assert.Equal(0u, e.Offset % NdzConstants.FrontMatterSize));
    }

    [Fact]
    public void TryRead_OnANonPairFile_ReturnsFalse()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output);

        Assert.False(NdzPairContainer.TryRead(output.ToArray(), out var container));
        Assert.Null(container);
    }

    [Fact]
    public void Read_OnANonPairFile_Throws()
    {
        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output);

        Assert.Throws<InvalidDataException>(() => NdzPairContainer.Read(output.ToArray()));
    }

    [Fact]
    public void Write_ProducesSmallerContainer_ThanTheRawRomsCombined()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize * 2, gameCode: "BASE", seed: 7);
        byte[] target = TestRom.Build(NdzConstants.FrameSize * 2, gameCode: "TRGT", seed: 8);
        // Give them shared content so compression (and the base-patch path) has
        // something real to exploit, not just incidental zstd-on-noise savings.
        Array.Copy(baseRom, BaseRomIndex.Grain * 4, target, BaseRomIndex.Grain * 4, NdzConstants.BlockSize * 8);

        using var output = new MemoryStream();
        NdzPairWriter.Write(output, baseRom, target);

        Assert.True(output.Length < baseRom.Length + target.Length);
    }
}
