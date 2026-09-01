using System.Text;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

/// <summary>
/// Every expected digest here was computed directly from Python's `hashlib.blake2b` in
/// an isolated venv (the same one used to cross-check this whole project against
/// `ndztool.py`), not transcribed from memory - the actual ground truth this
/// implementation needs to match, since `ndztool.py`'s base-patch mechanism calls
/// `hashlib.blake2b` directly. Includes a 64-byte (full digest, most commonly published
/// as a sanity check), several 8-byte digests (this format's actual use, e.g.
/// <see cref="NdzConstants.BaseHeaderHashLength"/>), and one that spans multiple 128-byte
/// compression blocks (2048 bytes of input) to exercise the multi-block path, not just
/// the single-block/empty-input path.
/// </summary>
public class Blake2bTests
{
    [Theory]
    [InlineData("", 64, "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce")]
    [InlineData("abc", 64, "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923")]
    [InlineData("", 8, "e4a6a0577479b2b4")]
    [InlineData("abc", 8, "d8bb14d833d59559")]
    [InlineData("The quick brown fox jumps over the lazy dog", 8, "4c4531b978d589f8")]
    public void Hash_MatchesPythonHashlibBlake2b_ForAsciiInputs(string input, int digestSize, string expectedHex)
    {
        byte[] data = Encoding.ASCII.GetBytes(input);
        byte[] digest = Blake2b.Hash(data, digestSize);
        Assert.Equal(expectedHex, Convert.ToHexStringLower(digest));
    }

    [Fact]
    public void Hash_MatchesPythonHashlibBlake2b_ForByteRange0To255()
    {
        var data = new byte[256];
        for (int i = 0; i < 256; i++) data[i] = (byte)i;

        byte[] digest = Blake2b.Hash(data, 8);
        Assert.Equal("2b2cedfed655ad3f", Convert.ToHexStringLower(digest));
    }

    /// <summary>2048 bytes - spans 16 full 128-byte compression blocks, exercising the multi-block loop (not just the single/empty-block path the other cases cover).</summary>
    [Fact]
    public void Hash_MatchesPythonHashlibBlake2b_ForMultiBlockInput()
    {
        var unit = new byte[256];
        for (int i = 0; i < 256; i++) unit[i] = (byte)i;
        var data = new byte[2048];
        for (int i = 0; i < 8; i++) unit.CopyTo(data, i * 256);

        Assert.Equal("5c4d874b3280908d", Convert.ToHexStringLower(Blake2b.Hash(data, 8)));
        Assert.Equal("5832595d6e631c13af1c4f5ef234ed7d", Convert.ToHexStringLower(Blake2b.Hash(data, 16)));
    }

    [Fact]
    public void Hash_RejectsDigestSizeOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Blake2b.Hash(Array.Empty<byte>(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Blake2b.Hash(Array.Empty<byte>(), 65));
    }

    [Fact]
    public void Hash_IsDeterministic_AndDigestSizeChangesOutput()
    {
        byte[] data = Encoding.ASCII.GetBytes("determinism check");
        Assert.Equal(Blake2b.Hash(data, 8), Blake2b.Hash(data, 8));
        Assert.NotEqual(Blake2b.Hash(data, 8), Blake2b.Hash(data, 16).AsSpan(0, 8).ToArray());
    }
}
