using System.Buffers.Binary;

namespace Ndz.Core.Format;

/// <summary>
/// BLAKE2b (RFC 7693), unkeyed, variable digest length - what `ndztool.py`'s
/// `hashlib.blake2b(..., digest_size=N)` calls actually use for the base-ROM header
/// hash (<see cref="NdzConstants.BaseHeaderHashLength"/> = 8 bytes) and the grain-hash
/// index (<see cref="BaseRomIndex"/>, also 8 bytes). Not available from GrindCore -
/// checked (`Nanook.GrindCore.HashType`, GrindCore 0.9.0): it has Blake2sp, Blake3,
/// XXHash, MD5, SHA1/2/3, but not BLAKE2b (a different, non-interchangeable algorithm
/// from Blake2sp despite the similar name) - so this project implements it directly
/// rather than take on a new external dependency for one algorithm. Verified against
/// RFC 7693's own official test vectors and cross-checked empirically against Python's
/// `hashlib.blake2b` before being trusted for real hashing - see <c>Blake2bTests</c>.
/// </summary>
public static class Blake2b
{
    private const int BlockSizeBytes = 128;

    private static readonly ulong[] Iv =
    {
        0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
        0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179,
    };

    private static readonly byte[][] Sigma =
    {
        new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        new byte[] { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
        new byte[] { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
        new byte[] { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
        new byte[] { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
        new byte[] { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
        new byte[] { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
        new byte[] { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
        new byte[] { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
        new byte[] { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
        new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        new byte[] { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
    };

    /// <summary>Computes an unkeyed BLAKE2b digest of <paramref name="data"/>, <paramref name="digestSize"/> bytes long (1-64).</summary>
    public static byte[] Hash(ReadOnlySpan<byte> data, int digestSize)
    {
        if (digestSize is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(digestSize), digestSize, "BLAKE2b digest size must be between 1 and 64 bytes.");

        Span<ulong> h = stackalloc ulong[8];
        Iv.CopyTo(h);
        // Parameter block XOR for the unkeyed, no-salt, no-personalization, fanout=1,
        // depth=1 case: byte 0 = digest length, byte 1 = key length (0), byte 2 =
        // fanout (1), byte 3 = depth (1); the rest of the 64-byte parameter block is
        // zero, so only h[0]'s low 32 bits are affected.
        h[0] ^= 0x0101_0000u ^ (uint)digestSize;

        ulong t0 = 0, t1 = 0;
        Span<byte> buffer = stackalloc byte[BlockSizeBytes];
        int bufferedLength = 0;

        while (data.Length > 0)
        {
            int toCopy = Math.Min(BlockSizeBytes - bufferedLength, data.Length);
            data[..toCopy].CopyTo(buffer[bufferedLength..]);
            bufferedLength += toCopy;
            data = data[toCopy..];

            // Only compress a full buffered block if more input remains - the final
            // block (even if exactly 128 bytes) must be compressed with the
            // last-block flag set, so a full buffer is held back until we know whether
            // it's actually the last one.
            if (bufferedLength == BlockSizeBytes && data.Length > 0)
            {
                t0 += BlockSizeBytes;
                if (t0 < BlockSizeBytes) t1++; // 64-bit counter overflow, unreachable for any real input here
                Compress(h, buffer, t0, t1, isLast: false);
                bufferedLength = 0;
            }
        }

        t0 += (ulong)bufferedLength;
        if (t0 < (ulong)bufferedLength) t1++;
        buffer[bufferedLength..].Clear();
        Compress(h, buffer, t0, t1, isLast: true);

        var digest = new byte[digestSize];
        Span<byte> full = stackalloc byte[64];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(full.Slice(i * 8, 8), h[i]);
        full[..digestSize].CopyTo(digest);
        return digest;
    }

    private static void Compress(Span<ulong> h, ReadOnlySpan<byte> block, ulong t0, ulong t1, bool isLast)
    {
        Span<ulong> m = stackalloc ulong[16];
        for (int i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));

        Span<ulong> v = stackalloc ulong[16];
        h[..8].CopyTo(v);
        Iv.CopyTo(v[8..]);
        v[12] ^= t0;
        v[13] ^= t1;
        if (isLast)
            v[14] = ~v[14];

        for (int round = 0; round < 12; round++)
        {
            byte[] s = Sigma[round];
            Mix(v, 0, 4, 8, 12, m[s[0]], m[s[1]]);
            Mix(v, 1, 5, 9, 13, m[s[2]], m[s[3]]);
            Mix(v, 2, 6, 10, 14, m[s[4]], m[s[5]]);
            Mix(v, 3, 7, 11, 15, m[s[6]], m[s[7]]);
            Mix(v, 0, 5, 10, 15, m[s[8]], m[s[9]]);
            Mix(v, 1, 6, 11, 12, m[s[10]], m[s[11]]);
            Mix(v, 2, 7, 8, 13, m[s[12]], m[s[13]]);
            Mix(v, 3, 4, 9, 14, m[s[14]], m[s[15]]);
        }

        for (int i = 0; i < 8; i++)
            h[i] ^= v[i] ^ v[i + 8];
    }

    private static void Mix(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = RotateRight(v[d] ^ v[a], 32);
        v[c] += v[d];
        v[b] = RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = RotateRight(v[b] ^ v[c], 63);
    }

    private static ulong RotateRight(ulong x, int bits) => (x >> bits) | (x << (64 - bits));
}
