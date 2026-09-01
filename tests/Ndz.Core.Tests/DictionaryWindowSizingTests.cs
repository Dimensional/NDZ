using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// GrindCore's implicit ZStd window sizing caps at 8 MiB for any raw content dictionary
/// over 256 KiB at compression level 19, and never grows further regardless of how much
/// bigger the dictionary actually gets - confirmed by reading zstd's own source
/// (clevels.h's size-tiered parameter tables, zstd_compress.c's
/// ZSTD_adjustCParams_internal, which only ever shrinks the window, never grows it).
/// GrindCore 0.9.0 added an explicit windowLog override for exactly this
/// (<see cref="CompressionDictionaryOptions.WindowBits"/>), which `NdzWriter` uses (see
/// its <c>ComputeDictionaryWindowBits</c>, `internal` specifically so this class can
/// reach it).
///
/// Now that dictionary content is always derived from the ROM itself
/// (<see cref="RawDictionaryBuilder"/> - see its remarks for why), an end-to-end test
/// genuinely past the old 8 MiB ceiling isn't practical to construct: only one
/// representative copy of each unique repeated chunk is ever included, so reaching
/// 8+ MiB of real derived content would need hundreds of distinct duplicate chunks.
/// This checks the windowLog *calculation* directly for sizes past that ceiling instead
/// - the GrindCore wiring itself (passing the computed value through
/// `CompressionDictionaryOptions.WindowBits`) is unchanged code, already proven to take
/// real effect end-to-end in this project's history (see docs/ndz-format-spec.md's
/// "Dictionary window sizing" section) - plus one real end-to-end test with a large
/// (multi-MiB, under the ceiling) derived dictionary to confirm the whole pipeline still
/// works correctly for genuinely big dictionaries, not just the arithmetic in isolation.
/// </summary>
public class DictionaryWindowSizingTests
{
    private const int EightMiB = 8 * 1024 * 1024;

    [Theory]
    [InlineData(0, 8192, 15)] // empty dictionary still gets NdzWriter's own floor
    [InlineData(256 * 1024, 8192, 19)] // wlog(256K+8K-1)+1 = 19
    [InlineData(EightMiB, 8192, 24)] // past the old implicit ceiling - still computed correctly
    [InlineData(EightMiB + 8192 * 4, 8192, 24)]
    [InlineData(64 * 1024 * 1024, 8192, 27)] // well past it
    public void ComputeDictionaryWindowBits_CoversTheWholeDictionaryPlusOneBlock(int dictionaryLength, int blockSize, int expectedBits)
    {
        int bits = NdzWriter.ComputeDictionaryWindowBits(dictionaryLength, blockSize);
        Assert.Equal(expectedBits, bits);

        // The defining property, regardless of the exact expected value above: a window
        // of this size must be able to address every byte of dictionary + one block.
        long window = 1L << bits;
        Assert.True(window >= (long)dictionaryLength + blockSize);
    }

    [Fact]
    public void ComputeDictionaryWindowBits_StaysWithinGrindCoresAcceptedRange()
    {
        foreach (int size in new[] { 0, 1, 1024, EightMiB, int.MaxValue / 2 })
        {
            int bits = NdzWriter.ComputeDictionaryWindowBits(size, NdzConstants.BlockSize);
            Assert.InRange(bits, 10, 31);
        }
    }

    /// <summary>
    /// Builds a ROM whose derived dictionary is genuinely large (several hundred KiB+) -
    /// not past the old 8 MiB ceiling (impractical to construct for real, see the class
    /// remarks: repeating one pattern many times only ever contributes *one* copy of it
    /// to the dictionary, capping achievable size at that one pattern's own length no
    /// matter how many times it repeats), but big enough to meaningfully exercise
    /// WindowBits (the old ceiling already bites above 256 KiB), not just the small
    /// sizes <see cref="DictionaryRoundTripTests"/> covers.
    /// </summary>
    [Fact]
    public void Compress_WithLargeDerivedDictionary_RoundTripsByteIdentical()
    {
        const int patternSize = 1024 * 1024; // 1 MiB - the ceiling on unique content this test can derive
        const int repeatCount = 20;

        byte[] rom = TestRom.Build(NdzConstants.FrameSize + patternSize * repeatCount);
        var pattern = new byte[patternSize];
        new Random(7).NextBytes(pattern);

        int start = NdzConstants.FrameSize;
        for (int i = 0; i < repeatCount; i++)
            pattern.CopyTo(rom, start + i * patternSize);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, rawDictionarySize: 2 * 1024 * 1024);

        using var archive = NdzArchive.Open(output.ToArray());
        Assert.True(archive.FrontMatter.HasDictionary);
        Assert.True(archive.FrontMatter.DictionaryStoredSize > 256 * 1024,
            $"Expected a several-hundred-KiB+ derived dictionary to actually exercise WindowBits, got {archive.FrontMatter.DictionaryStoredSize} bytes.");

        byte[] rebuilt = archive.DecompressAll();
        Assert.Equal(rom, rebuilt);
    }
}
