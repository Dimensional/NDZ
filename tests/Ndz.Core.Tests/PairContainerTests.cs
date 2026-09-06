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

    /// <summary>
    /// Generalization added 2026-09-06 for packing a whole family of similar ROMs (e.g.
    /// several regional/version releases of the same game) together - a star topology,
    /// every target base-patched against the same one shared base, not a chain. Not
    /// something either reference tool's own pack path does (`ndztool.py`'s `cmd_pack`
    /// hardcodes `n_roms=2`), but needs no new wire-format bits - the container header's
    /// own `nRoms` field, and both `ndztool.py`'s and this project's own decode-side
    /// entry-parsing loops, were already fully generic (see NdzPairWriter's own remarks).
    /// </summary>
    [Fact]
    public void Write_WithMultipleTargets_RoundTripsEveryRomByteIdentical()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 10);
        byte[][] targets =
        {
            TestRom.Build(NdzConstants.FrameSize, gameCode: "TRG1", seed: 11),
            TestRom.Build(NdzConstants.FrameSize, gameCode: "TRG2", seed: 12),
            TestRom.Build(NdzConstants.FrameSize, gameCode: "TRG3", seed: 13),
        };

        using var output = new MemoryStream();
        NdzPairWriter.Write(output, baseRom, targets);

        var container = NdzPairContainer.Read(output.ToArray());
        Assert.Equal(4, container.Entries.Count);
        Assert.Equal(0, container.PlainEntryIndex);
        Assert.All(container.Entries, e => Assert.Equal(0u, e.Offset % NdzConstants.FrontMatterSize));

        Assert.Equal(baseRom, container.DecompressEntry(0));
        for (int i = 0; i < targets.Length; i++)
            Assert.Equal(targets[i], container.DecompressEntry(i + 1));
    }

    /// <summary>Confirms this is a real star, not a chain - each target's own OpenEntry independently resolves against the shared base (index 0), not against a neighboring target.</summary>
    [Fact]
    public void Write_WithMultipleTargets_EachTargetOpensIndependentlyAgainstTheSharedBase()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 20);
        byte[][] targets =
        {
            TestRom.Build(NdzConstants.FrameSize, gameCode: "TRG1", seed: 21),
            TestRom.Build(NdzConstants.FrameSize, gameCode: "TRG2", seed: 22),
        };

        using var output = new MemoryStream();
        NdzPairWriter.Write(output, baseRom, targets);
        var container = NdzPairContainer.Read(output.ToArray());

        using var archive1 = container.OpenEntry(1);
        using var archive2 = container.OpenEntry(2);
        Assert.Equal(targets[0], archive1.DecompressAll());
        Assert.Equal(targets[1], archive2.DecompressAll());
    }

    [Fact]
    public void Write_WithNoTargets_Throws()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 30);
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => NdzPairWriter.Write(output, baseRom, Array.Empty<byte[]>()));
    }

    [Fact]
    public void Write_WithMismatchedTargetDictionarySizesCount_Throws()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 31);
        byte[][] targets = { TestRom.Build(NdzConstants.FrameSize, gameCode: "TRG1", seed: 32) };
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() =>
            NdzPairWriter.Write(output, baseRom, targets, targetDictionarySizes: new int?[] { 0, 0 }));
    }

    /// <summary>The header's 16 KiB is shared by all entry records - this must be caught with a clear error, not silent corruption, well before it's ever realistic to hit (1023 ROMs at the current 16-byte entry size). Uses empty placeholder ROMs so this fails fast, before any real compression.</summary>
    [Fact]
    public void Write_WithTooManyRomsForTheHeader_ThrowsBeforeCompressingAnything()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 40);
        byte[][] tooManyTargets = Enumerable.Range(0, 2000).Select(_ => Array.Empty<byte>()).ToArray();
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => NdzPairWriter.Write(output, baseRom, tooManyTargets));
    }

    /// <summary>The single-target overload must still behave exactly like the general N-target one it now delegates to.</summary>
    [Fact]
    public void Write_SingleTargetOverload_MatchesGeneralOverloadByteForByte()
    {
        byte[] baseRom = TestRom.Build(NdzConstants.FrameSize, gameCode: "BASE", seed: 50);
        byte[] target = TestRom.Build(NdzConstants.FrameSize, gameCode: "TRGT", seed: 51);

        using var viaSingle = new MemoryStream();
        NdzPairWriter.Write(viaSingle, baseRom, target);

        using var viaGeneral = new MemoryStream();
        NdzPairWriter.Write(viaGeneral, baseRom, new[] { target });

        Assert.Equal(viaSingle.ToArray(), viaGeneral.ToArray());
    }
}
