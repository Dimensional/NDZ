namespace Ndz.Core.Format;

/// <summary>
/// Standard CRC-32 (ISO-HDLC / zlib's `crc32` / "PKZIP CRC32" - the polynomial every
/// ROM-verification database, e.g. No-Intro/Redump DATs, actually uses). Implemented
/// directly rather than pulled from a package: GrindCore already ships one
/// (<c>Nanook.GrindCore.ZLib.Crc32Helper</c>), but that type is internal to that assembly,
/// not something this project can call - see <see cref="RomChecksum"/>'s own remarks on
/// why MD5/SHA-1/SHA-256 come from GrindCore's own public <c>HashFactory</c> instead
/// while this one doesn't. Table-driven, reflected in/out, polynomial 0xEDB88320,
/// initial/final XOR 0xFFFFFFFF - confirmed against the standard CRC-32 check value
/// (0xCBF43926 for the ASCII string "123456789") plus the well-known digests of "" and
/// "abc" - see <see cref="Ndz.Core.Tests.RomChecksumTests"/>.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>Computes the CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
