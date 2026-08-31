using System.Linq;
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
/// (<see cref="CompressionDictionaryOptions.WindowBits"/>), which
/// <see cref="NdzWriter"/> now uses (see its <c>ComputeDictionaryWindowBits</c>).
///
/// This test proves that override is actually wired up and taking effect - not just
/// that the code compiles - by placing matching content deliberately *beyond* the old
/// 8 MiB ceiling, where only a correctly-sized window can reach it.
/// </summary>
public class DictionaryWindowSizingTests
{
    [Fact]
    public void Compress_WithDictionaryLargerThan8MiB_CanReferenceContentPastTheOldWindowCeiling()
    {
        const int eightMiB = 8 * 1024 * 1024;

        var dictionary = new byte[eightMiB + NdzConstants.BlockSize * 4]; // past the old ceiling
        new Random(1).NextBytes(dictionary);

        var sharedPattern = new byte[NdzConstants.BlockSize];
        new Random(2).NextBytes(sharedPattern);
        // Placed at the very end - unreachable under the old implicit 8 MiB window,
        // reachable only if the window was actually sized to cover the whole dictionary.
        Array.Copy(sharedPattern, 0, dictionary, dictionary.Length - sharedPattern.Length, sharedPattern.Length);

        byte[] rom = TestRom.Build(NdzConstants.FrameSize);
        Array.Copy(sharedPattern, 0, rom, NdzConstants.FrameSize - NdzConstants.BlockSize, sharedPattern.Length);

        using var withDict = new MemoryStream();
        NdzWriter.Compress(rom, withDict, dictionary: new NdzDictionary { Content = dictionary });

        using var withoutDict = new MemoryStream();
        NdzWriter.Compress(rom, withoutDict);

        // Compare compressed *payload* size only, not whole-file size - the dictionary
        // itself is stored verbatim in the file (8+ MiB here), which would otherwise
        // completely swamp the small effect this test is actually trying to isolate.
        using var withDictArchive = NdzArchive.Open(withDict.ToArray());
        using var withoutDictArchive = NdzArchive.Open(withoutDict.ToArray());
        long withDictPayload = withDictArchive.SeekTable.Sum(e => (long)e.CompressedSize);
        long withoutDictPayload = withoutDictArchive.SeekTable.Sum(e => (long)e.CompressedSize);

        // If the far-placed pattern weren't reachable, dictionary-primed compression
        // would do no better than plain for this block (nothing else in `rom` or
        // `dictionary` is shared) - so a smaller result here specifically demonstrates
        // the window reaches content beyond the old 8 MiB ceiling, not just that
        // dictionaries work at all (DictionaryRoundTripTests already covers that).
        Assert.True(withDictPayload < withoutDictPayload,
            $"Expected the dictionary to help compress a block matching content placed beyond the " +
            $"old 8 MiB window ceiling ({withDictPayload} vs {withoutDictPayload} payload bytes) - if " +
            "this fails, the WindowBits override may not be taking effect.");
    }

    [Fact]
    public void Compress_WithDictionaryLargerThan8MiB_StillRoundTripsByteIdentical()
    {
        const int eightMiB = 8 * 1024 * 1024;

        var dictionary = new byte[eightMiB + NdzConstants.BlockSize * 4];
        new Random(3).NextBytes(dictionary);

        byte[] rom = TestRom.Build(NdzConstants.FrameSize * 2);

        using var output = new MemoryStream();
        NdzWriter.Compress(rom, output, dictionary: new NdzDictionary { Content = dictionary });

        using var archive = NdzArchive.Open(output.ToArray());
        byte[] rebuilt = archive.DecompressAll();

        Assert.Equal(rom, rebuilt);
    }
}
