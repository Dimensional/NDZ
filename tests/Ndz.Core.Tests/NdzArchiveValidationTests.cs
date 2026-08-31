using System.Buffers.Binary;
using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

public class NdzArchiveValidationTests
{
    [Fact]
    public void Open_RejectsFileShorterThanFrontMatterPlusFooter()
    {
        var tooShort = new byte[NdzConstants.FrontMatterSize]; // no room for the trailer footer
        Assert.Throws<InvalidDataException>(() => NdzArchive.Open(tooShort));
    }

    [Fact]
    public void Open_RejectsBadTrailerMagic()
    {
        byte[] original = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        NdzWriter.Compress(original, output);
        byte[] bytes = output.ToArray();

        // Corrupt the last 4 bytes (the trailer magic).
        bytes[^1] = 0x00;

        Assert.Throws<InvalidDataException>(() => NdzArchive.Open(bytes));
    }

    [Fact]
    public void Open_RejectsSeekTableFrameCountThatDoesNotFit()
    {
        byte[] original = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        NdzWriter.Compress(original, output);
        byte[] bytes = output.ToArray();

        // Inflate the declared frame count so the implied seek-table start falls before the payload.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8, 4), 1_000_000);

        Assert.Throws<InvalidDataException>(() => NdzArchive.Open(bytes));
    }

    [Fact]
    public void Open_RejectsDictionaryWithMismatchedStoredAndDecompressedSize()
    {
        // The dictionary section is documented as verbatim raw bytes, not separately
        // compressed - NdzWriter always writes stored == decompressed for exactly that
        // reason. A file claiming otherwise (64 vs 128 here) uses a variant this reader
        // doesn't understand.
        var frontMatter = new NdzFrontMatter
        {
            OriginalSize = 0,
            GameCode = 0,
            Banner = new byte[NdzConstants.BannerSlotLength],
            DictionaryStoredSize = 64,
            DictionaryDecompressedSize = 128,
        };

        var bytes = new byte[NdzConstants.FrontMatterSize + NdzConstants.TrailerFooterSize];
        frontMatter.WriteTo(bytes.AsSpan(0, NdzConstants.FrontMatterSize));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8, 4), 0); // nframes
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4, 4), NdzConstants.TrailerMagic);

        Assert.Throws<NotSupportedException>(() => NdzArchive.Open(bytes));
    }

    [Fact]
    public void Open_RejectsMismatchedOriginalSizeVsSeekTable()
    {
        byte[] original = TestRom.Build(NdzConstants.FrameSize);
        using var output = new MemoryStream();
        NdzWriter.Compress(original, output);
        byte[] bytes = output.ToArray();

        // Lie about the original size in the front-matter (offset 0x0008) without touching the seek table.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x0008, 4), (uint)(original.Length * 2));

        Assert.Throws<InvalidDataException>(() => NdzArchive.Open(bytes));
    }
}
