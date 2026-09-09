using System.Text;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Every expected digest here is a well-known, widely-published standard test vector for
/// its algorithm (CRC-32's own official "check value" for "123456789" is CBF43926; the
/// MD5/SHA-1/SHA-256 empty-string and "abc" digests are the textbook examples every
/// implementation is checked against) - independently confirmed here too via a throwaway
/// scratch project calling `System.IO.Hashing.Crc32` directly, which is what caught the
/// CRC32 byte-order bug <see cref="RomChecksum.ComputeHex"/>'s own remarks describe
/// (`Crc32.Hash` returns little-endian/write-order bytes, not the conventional big-endian
/// hex display order every ROM database actually uses).
/// </summary>
public class RomChecksumTests
{
    [Theory]
    [InlineData("", ChecksumAlgorithm.Crc32, "00000000")]
    [InlineData("123456789", ChecksumAlgorithm.Crc32, "cbf43926")]
    [InlineData("abc", ChecksumAlgorithm.Crc32, "352441c2")]
    [InlineData("", ChecksumAlgorithm.Md5, "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("abc", ChecksumAlgorithm.Md5, "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("", ChecksumAlgorithm.Sha1, "da39a3ee5e6b4b0d3255bfef95601890afd80709")]
    [InlineData("abc", ChecksumAlgorithm.Sha1, "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData("", ChecksumAlgorithm.Sha256, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", ChecksumAlgorithm.Sha256, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void ComputeHex_MatchesStandardTestVectors(string input, ChecksumAlgorithm algorithm, string expectedHex)
    {
        byte[] data = Encoding.ASCII.GetBytes(input);
        Assert.Equal(expectedHex, RomChecksum.ComputeHex(data, algorithm));
    }

    [Fact]
    public void ComputeAll_ReturnsAllFourAtOnce_MatchingIndividualCalls()
    {
        byte[] data = Encoding.ASCII.GetBytes("abc");
        RomChecksums all = RomChecksum.ComputeAll(data);

        Assert.Equal(RomChecksum.ComputeHex(data, ChecksumAlgorithm.Crc32), all.Crc32);
        Assert.Equal(RomChecksum.ComputeHex(data, ChecksumAlgorithm.Md5), all.Md5);
        Assert.Equal(RomChecksum.ComputeHex(data, ChecksumAlgorithm.Sha1), all.Sha1);
        Assert.Equal(RomChecksum.ComputeHex(data, ChecksumAlgorithm.Sha256), all.Sha256);
    }

    [Theory]
    [InlineData("352441C2")] // uppercase
    [InlineData(" 352441c2 ")] // whitespace, as a paste from somewhere often carries
    [InlineData("352441c2")]
    public void Matches_IsCaseInsensitiveAndTrims_ForCrc32(string pasted)
    {
        byte[] data = Encoding.ASCII.GetBytes("abc");
        Assert.True(RomChecksum.Matches(data, pasted));
    }

    [Theory]
    [InlineData("900150983cd24fb0d6963f7d28e17f72")] // MD5
    [InlineData("a9993e364706816aba3e25717850c26c9cd0d89d")] // SHA-1
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")] // SHA-256
    public void Matches_AutoDetectsAlgorithmByLength_ForAbc(string expected)
    {
        byte[] data = Encoding.ASCII.GetBytes("abc");
        Assert.True(RomChecksum.Matches(data, expected));
    }

    [Fact]
    public void Matches_ReturnsFalse_ForWrongValueAtACorrectLength()
    {
        byte[] data = Encoding.ASCII.GetBytes("abc");
        // Right length for MD5 (32 hex chars), wrong value.
        Assert.False(RomChecksum.Matches(data, "00000000000000000000000000000000"[..32]));
    }

    [Fact]
    public void Matches_ReturnsNull_ForAnUnrecognizedLength()
    {
        byte[] data = Encoding.ASCII.GetBytes("abc");
        Assert.Null(RomChecksum.Matches(data, "deadbeef123")); // 11 hex chars - not 8/32/40/64
    }
}
