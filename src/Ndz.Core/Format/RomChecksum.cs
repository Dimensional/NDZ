using Nanook.GrindCore;

namespace Ndz.Core.Format;

/// <summary>The checksum algorithm a computed/expected value uses - the four a No-Intro/Redump-style ROM database DAT entry typically carries.</summary>
public enum ChecksumAlgorithm
{
    Crc32,
    Md5,
    Sha1,
    Sha256,
}

/// <summary>
/// Computes the standard ROM-verification checksums of a decompressed .nds/.dsi image -
/// letting Examine confirm a ROM (raw, or unpacked from a .ndz) matches a known-good
/// database entry. MD5/SHA-1/SHA-256 come from GrindCore's own public
/// <see cref="HashFactory"/> (this project already depends on GrindCore for compression -
/// no reason to also pull in `System.Security.Cryptography` or another package for the
/// same three algorithms it already ships). CRC32 is the one exception: GrindCore has its
/// own CRC32 too, but that type is internal to that assembly, so this project implements
/// it directly instead - see <see cref="Crc32"/>.
/// </summary>
public static class RomChecksum
{
    /// <summary>Computes all four checksums of <paramref name="data"/> in one pass, each as a lowercase hex string.</summary>
    public static RomChecksums ComputeAll(ReadOnlySpan<byte> data) => new(
        Crc32: ComputeHex(data, ChecksumAlgorithm.Crc32),
        Md5: ComputeHex(data, ChecksumAlgorithm.Md5),
        Sha1: ComputeHex(data, ChecksumAlgorithm.Sha1),
        Sha256: ComputeHex(data, ChecksumAlgorithm.Sha256));

    /// <summary>Computes one checksum of <paramref name="data"/> as a lowercase hex string.</summary>
    public static string ComputeHex(ReadOnlySpan<byte> data, ChecksumAlgorithm algorithm)
    {
        if (algorithm == ChecksumAlgorithm.Crc32)
            return Crc32.Compute(data).ToString("x8");

        // HashFactory.Compute only has a byte[]-taking overload, not ReadOnlySpan<byte> -
        // an extra copy for a span-based caller, but checksumming a whole ROM is already
        // an O(n) full-buffer pass, so this doesn't meaningfully add to the cost.
        HashType hashType = algorithm switch
        {
            ChecksumAlgorithm.Md5 => HashType.MD5,
            ChecksumAlgorithm.Sha1 => HashType.SHA1,
            ChecksumAlgorithm.Sha256 => HashType.SHA2_256,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null),
        };
        byte[] hash = HashFactory.Compute(hashType, data.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Compares <paramref name="expectedHex"/> (in any case, optionally with whitespace
    /// around it - pasted-in checksums commonly have both) against
    /// <paramref name="data"/>'s own checksum for whichever algorithm
    /// <paramref name="expectedHex"/>'s length identifies (8 hex chars = CRC32, 32 = MD5,
    /// 40 = SHA-1, 64 = SHA-256) - returns null, not false, when the length matches none
    /// of them, so a caller can tell "wrong length" apart from "wrong value".
    /// </summary>
    public static bool? Matches(ReadOnlySpan<byte> data, string expectedHex)
    {
        string trimmed = expectedHex.Trim();
        ChecksumAlgorithm? algorithm = trimmed.Length switch
        {
            8 => ChecksumAlgorithm.Crc32,
            32 => ChecksumAlgorithm.Md5,
            40 => ChecksumAlgorithm.Sha1,
            64 => ChecksumAlgorithm.Sha256,
            _ => null,
        };
        if (algorithm is null)
            return null;

        string actual = ComputeHex(data, algorithm.Value);
        return string.Equals(actual, trimmed, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>All four checksums of one ROM image, each a lowercase hex string.</summary>
public readonly record struct RomChecksums(string Crc32, string Md5, string Sha1, string Sha256);
