using Ndz.Core.Compression;

namespace Ndz.Core.Tests;

/// <summary>
/// Cross-checked byte-for-byte against `ndztool.py`'s own `_cdc_chunks`/
/// `build_dup_weighted_dict`, run in an isolated venv on the exact same input bytes -
/// the same methodology used throughout this project (see e.g. <see cref="Blake2bTests"/>).
/// Inputs are built from simple, exactly-reproducible-in-both-languages generators (a
/// linear ramp, and a small xorshift32 PRNG) rather than embedded binary fixtures, so
/// there's nothing to keep in sync beyond the formulas themselves.
/// </summary>
public class RawDictionaryBuilderTests
{
    /// <summary>`data[i] = (i*37 + 13) &amp; 0xFF` - periodic with period 256, so every 16 KiB chunk (a multiple of 256) turns out byte-identical to every other one.</summary>
    private static byte[] BuildRampInput(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)((i * 37 + 13) & 0xFF);
        return data;
    }

    /// <summary>xorshift32, matching Python's `x^=(x&lt;&lt;13); x^=(x&gt;&gt;17); x^=(x&lt;&lt;5)` bit-for-bit (uint's shift operators already truncate to 32 bits, same as Python's explicit `&amp; 0xFFFFFFFF`).</summary>
    private static byte[] Xorshift32Bytes(uint seed, int length)
    {
        var data = new byte[length];
        uint x = seed;
        for (int i = 0; i < length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            data[i] = (byte)(x & 0xFF);
        }
        return data;
    }

    [Fact]
    public void ContentDefinedChunks_OnRampInput_MatchesPython()
    {
        byte[] data = BuildRampInput(100_000);
        var chunks = RawDictionaryBuilder.ContentDefinedChunks(data);

        var expected = new (int, int)[]
        {
            (0, 16384), (16384, 16384), (32768, 16384), (49152, 16384),
            (65536, 16384), (81920, 16384), (98304, 1696),
        };
        Assert.Equal(expected, chunks);
    }

    [Fact]
    public void ContentDefinedChunks_OnXorshiftNoiseInput_MatchesPython()
    {
        byte[] data = BuildNoiseWithSplicedRepeats();
        var chunks = RawDictionaryBuilder.ContentDefinedChunks(data);

        var expected = new (int, int)[]
        {
            (0, 16384), (16384, 5477), (21861, 3029), (24890, 7575), (32465, 529),
            (32994, 2660), (35654, 654), (36308, 1272), (37580, 14822), (52402, 1274),
            (53676, 6324),
        };
        Assert.Equal(expected, chunks);
        Assert.Equal(data.Length, chunks.Sum(c => c.Length));
    }

    private static byte[] BuildNoiseWithSplicedRepeats()
    {
        byte[] data = Xorshift32Bytes(12345, 60000);
        byte[] special = Xorshift32Bytes(999, 6000);
        foreach (int offset in new[] { 5000, 25000, 45000 })
            special.CopyTo(data, offset);
        return data;
    }

    [Theory]
    [InlineData(4096, "")]
    [InlineData(20000, "8b75fda1bbd1252572a50c2d4ed0094fc614adc5dd714b0ef1b32dce202a5a10")]
    [InlineData(65536, "8b75fda1bbd1252572a50c2d4ed0094fc614adc5dd714b0ef1b32dce202a5a10")]
    public void Build_OnRampInput_MatchesPython(int dictSize, string expectedSha256)
    {
        byte[] data = BuildRampInput(100_000);
        byte[] dict = RawDictionaryBuilder.Build(data, dictSize);

        if (expectedSha256.Length == 0)
        {
            Assert.Empty(dict);
        }
        else
        {
            Assert.Equal(16384, dict.Length);
            Assert.Equal(expectedSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(dict)));
        }
    }

    /// <summary>
    /// The spliced "special" content doesn't actually produce a whole-chunk match here
    /// (each insertion follows different preceding noise, so the chunker - which
    /// accumulates its rolling hash from each chunk's own start, not a fixed window -
    /// doesn't reliably resynchronize across insertions). Confirmed this isn't a bug in
    /// this port: `ndztool.py` itself returns nothing for this exact input too - a real,
    /// legitimate property of the reference algorithm, not an approximation of it.
    /// </summary>
    [Theory]
    [InlineData(2048)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(65536)]
    public void Build_OnNoiseWithSplicedRepeats_MatchesPython_ReturnsEmpty(int dictSize)
    {
        byte[] data = BuildNoiseWithSplicedRepeats();
        byte[] dict = RawDictionaryBuilder.Build(data, dictSize);
        Assert.Empty(dict);
    }

    [Fact]
    public void Build_OnDeliberatelyRepeatedContent_FindsIt()
    {
        // A cleaner positive case than the spliced-noise one above: repeat one 8 KiB
        // chunk enough times, in an otherwise-constant stream, that its own chunk
        // boundaries land consistently (a full max-size chunk each time, like the ramp
        // test) so the dedup ranking has something unambiguous to find.
        var data = new byte[8192 * 6];
        var rng = new Random(42);
        var repeatedPattern = new byte[8192];
        rng.NextBytes(repeatedPattern);
        for (int i = 0; i < 6; i++)
            repeatedPattern.CopyTo(data, i * 8192);

        byte[] dict = RawDictionaryBuilder.Build(data, dictSize: 16384);

        Assert.NotEmpty(dict);
        Assert.True(dict.Length <= 16384);
    }

    [Fact]
    public void Build_WithNonPositiveSize_ReturnsEmpty()
    {
        byte[] data = BuildRampInput(1000);
        Assert.Empty(RawDictionaryBuilder.Build(data, 0));
        Assert.Empty(RawDictionaryBuilder.Build(data, -5));
    }
}
