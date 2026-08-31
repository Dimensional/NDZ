using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Real dictionary compression, now that GrindCore.net's local dev build supports it
/// (see NdzDictionary's remarks). Mirrors the reference packer's own motivating case:
/// a raw content dictionary built from the ROM's own repeated content, helping blocks
/// that share content with each other but are too far apart for a per-block-blind
/// plain compressor to see.
/// </summary>
public class DictionaryRoundTripTests
{
    /// <summary>
    /// Builds a ROM where every other 8 KB block (from a point past the header/banner)
    /// is byte-for-byte the same repeating pattern - isolated from its other
    /// occurrences by intervening random-filler blocks, so only a whole-file dictionary
    /// (not per-block matching) can exploit the repetition. Returns the ROM and that
    /// pattern, since a real dictionary is exactly this kind of content extracted from
    /// the file itself.
    /// </summary>
    private static (byte[] Rom, byte[] SharedPattern) BuildRomWithRepeatedContent(int totalSize, int seed)
    {
        byte[] rom = TestRom.Build(totalSize, seed: seed);

        var shared = new byte[NdzConstants.BlockSize];
        new Random(seed ^ 0x5EED).NextBytes(shared);

        for (int off = NdzConstants.BlockSize * 4; off + NdzConstants.BlockSize <= totalSize; off += NdzConstants.BlockSize * 2)
            Array.Copy(shared, 0, rom, off, NdzConstants.BlockSize);

        return (rom, shared);
    }

    [Fact]
    public void Compress_WithDictionary_RoundTripsByteIdentical()
    {
        var (original, sharedPattern) = BuildRomWithRepeatedContent(NdzConstants.FrameSize * 2, seed: 1);
        var dictionary = new NdzDictionary { Content = sharedPattern };

        using var output = new MemoryStream();
        NdzWriter.Compress(original, output, dictionary: dictionary);

        using var archive = NdzArchive.Open(output.ToArray());
        byte[] rebuilt = archive.DecompressAll();

        Assert.Equal(original, rebuilt);
    }

    [Fact]
    public void Compress_WithDictionary_SetsFrontMatterFields()
    {
        var (original, sharedPattern) = BuildRomWithRepeatedContent(NdzConstants.FrameSize * 2, seed: 2);
        var dictionary = new NdzDictionary { Content = sharedPattern };

        using var output = new MemoryStream();
        NdzWriter.Compress(original, output, dictionary: dictionary);
        using var archive = NdzArchive.Open(output.ToArray());

        Assert.True(archive.FrontMatter.HasDictionary);
        Assert.True(archive.FrontMatter.Flags.HasFlag(NdzFlags.RawDictionary));
        Assert.Equal((uint)sharedPattern.Length, archive.FrontMatter.DictionaryStoredSize);
        Assert.Equal((uint)sharedPattern.Length, archive.FrontMatter.DictionaryDecompressedSize);
    }

    [Fact]
    public void Compress_WithDictionary_ProducesSmallerOutput_ThanWithoutDictionary()
    {
        var (original, sharedPattern) = BuildRomWithRepeatedContent(NdzConstants.FrameSize * 2, seed: 3);
        var dictionary = new NdzDictionary { Content = sharedPattern };

        using var withDict = new MemoryStream();
        NdzWriter.Compress(original, withDict, dictionary: dictionary);

        using var withoutDict = new MemoryStream();
        NdzWriter.Compress(original, withoutDict);

        // Sanity check: this only proves anything if the dictionary was actually
        // consulted - a no-op/ignored dictionary would produce identical sizes.
        Assert.True(withDict.Length < withoutDict.Length,
            $"Expected dictionary-primed compression ({withDict.Length} bytes) to beat plain " +
            $"compression ({withoutDict.Length} bytes) on data crafted to share content with the dictionary.");
    }

    [Fact]
    public void Compress_WithDictionary_UsesDictModeForAtLeastOneBlock()
    {
        // Confirms the per-block adaptive choice actually happens by directly reading
        // the raw mode bytes out of the written file - not just inferring it from a
        // smaller total size (which the sibling test already covers).
        var (original, sharedPattern) = BuildRomWithRepeatedContent(NdzConstants.FrameSize * 2, seed: 4);
        var dictionary = new NdzDictionary { Content = sharedPattern };

        using var output = new MemoryStream();
        NdzWriter.Compress(original, output, dictionary: dictionary);
        byte[] bytes = output.ToArray();

        using var archive = NdzArchive.Open(bytes);
        long dictModeCount = 0;
        long payloadStart = NdzConstants.FrontMatterSize + archive.FrontMatter.DictionaryStoredSize;
        long cursor = payloadStart;
        foreach (var entry in archive.SeekTable)
        {
            int blockCount = (int)((entry.DecompressedSize + NdzConstants.BlockSize - 1) / NdzConstants.BlockSize);
            for (int b = 0; b < blockCount; b++)
            {
                byte mode = bytes[cursor + blockCount * 4 + b];
                if (mode == (byte)BlockMode.Dict)
                    dictModeCount++;
            }
            cursor += entry.CompressedSize;
        }

        Assert.True(dictModeCount > 0, "Expected at least one block to pick BlockMode.Dict over BlockMode.Plain.");
    }
}
