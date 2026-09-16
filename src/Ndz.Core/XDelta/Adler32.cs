namespace Ndz.Core.XDelta;

/// <summary>
/// Adler-32 (RFC 1950 §8.2) - VCDIFF's per-window integrity checksum (RFC 3284 §5.1's
/// <c>VCD_ADLER32</c> win-indicator bit). A distinct algorithm from <see cref="Format.Crc32"/>,
/// not reusable from it; confirmed identical to real `xdelta3.exe`'s own checksum for the same
/// content during this project's prior xdelta investigation (see
/// <c>docs/xdelta-vcdiff-notes.md</c>) - not something this format shares with zstd's own
/// (absent) checksum handling elsewhere in NDZ.
/// </summary>
public static class Adler32
{
    private const uint Modulus = 65521;

    /// <summary>Computes the Adler-32 checksum of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;

        // Standard byte-at-a-time form: NMAX-batched reduction (the usual performance
        // trick) isn't needed here - VCDIFF windows are ROM-sized at most, not the
        // multi-gigabyte streams Adler-32 batching exists for.
        foreach (byte value in data)
        {
            a = (a + value) % Modulus;
            b = (b + a) % Modulus;
        }

        return (b << 16) | a;
    }
}
