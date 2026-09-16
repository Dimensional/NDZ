using System.Security.Cryptography;
using Ndz.Core.Compression;

namespace Ndz.Core.Tests;

/// <summary>
/// Real-file verification against genuine ndz-studio output - the strongest possible
/// check, since it isn't just our own round-trip. Fixtures (a real base `.ndz` packed
/// with `--raw-dict 6m`, a real `.delta.ndz` hack produced by ndz-studio's own "Pack
/// hack" feature, and the real decrypted Black/White Pokémon ROMs) live outside this
/// repo at <see cref="TestFilesDirectory"/> - not available in CI or on another
/// machine, so every test here checks <see cref="HaveFixtures"/> first and returns
/// early (not a true xUnit skip - this project's xunit version predates
/// <c>Assert.Skip</c> and adding a skip-support package for just this felt like more
/// than the one real gap here warrants) rather than failing when they're absent.
/// </summary>
public class HackContainerRealFileTests
{
    private const string TestFilesDirectory = @"E:\source\git\NitroTwl\test_files";
    private const string BaseNdzPath = TestFilesDirectory + @"\Pokemon - Black Version (USA, Europe) (NDSi Enhanced)_dict6m.ndz";
    private const string RealDeltaNdzPath = TestFilesDirectory + @"\Pokemon - White Version (USA, Europe) (NDSi Enhanced).delta.ndz";
    private const string BlackRomPath = TestFilesDirectory + @"\Pokemon - Black Version (USA, Europe) (NDSi Enhanced)\Pokemon - Black Version (USA, Europe) (NDSi Enhanced).nds";
    private const string WhiteRomPath = TestFilesDirectory + @"\Pokemon - White Version (USA, Europe) (NDSi Enhanced)\Pokemon - White Version (USA, Europe) (NDSi Enhanced).nds";

    private static bool HaveFixtures =>
        File.Exists(BaseNdzPath) && File.Exists(RealDeltaNdzPath) && File.Exists(BlackRomPath) && File.Exists(WhiteRomPath);

    private static string Sha256Of(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// The strongest possible check: our reader decodes a `.delta.ndz` genuine
    /// ndz-studio produced (not one of our own) byte-exact against the real White ROM.
    /// This is what confirms the reverse-engineered format is actually right, not just
    /// self-consistent with our own writer's assumptions.
    /// </summary>
    [Fact]
    public void Open_RealNdzStudioDeltaNdz_DecodesByteExactToRealWhiteRom()
    {
        if (!HaveFixtures)
            return;

        byte[] baseNdz = File.ReadAllBytes(BaseNdzPath);
        byte[] realDeltaNdz = File.ReadAllBytes(RealDeltaNdzPath);
        byte[] realWhiteRom = File.ReadAllBytes(WhiteRomPath);

        using var archive = NdzArchive.Open(realDeltaNdz, baseNdzBytes: baseNdz);
        byte[] decoded = archive.DecompressAll();

        Assert.Equal(Sha256Of(realWhiteRom), Sha256Of(decoded));
    }

    /// <summary>
    /// Our own writer's output isn't expected to be byte-identical to ndz-studio's own
    /// file (its block-matching/candidate-selection heuristics are its own, not ours),
    /// but it must round-trip correctly against real ROM content, not just synthetic
    /// test fixtures.
    /// </summary>
    [Fact]
    public void Compress_RealBlackAndWhiteRoms_RoundTripsByteExact()
    {
        if (!HaveFixtures)
            return;

        byte[] baseNdz = File.ReadAllBytes(BaseNdzPath);
        byte[] realBlackRom = File.ReadAllBytes(BlackRomPath);
        byte[] realWhiteRom = File.ReadAllBytes(WhiteRomPath);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(realWhiteRom, realBlackRom, baseNdz, output);

        using var archive = NdzArchive.Open(output.ToArray(), baseNdzBytes: baseNdz);
        byte[] decoded = archive.DecompressAll();

        Assert.Equal(Sha256Of(realWhiteRom), Sha256Of(decoded));
    }
}
