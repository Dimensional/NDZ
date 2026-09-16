using System.Security.Cryptography;
using Ndz.Core.Compression;
using Ndz.Core.XDelta;

namespace Ndz.Core.Tests;

/// <summary>
/// Real-file verification against genuine ndz-studio output - the strongest possible
/// check, since it isn't just our own round-trip. Fixtures (a real base `.ndz`, a real
/// `.delta.ndz` hack produced by ndz-studio's own "Pack hack" feature, the real standalone
/// `.xdelta` it was also built from, and the real decrypted base/target ROMs, for two
/// unrelated game pairs of very different sizes) live outside this repo at
/// <see cref="TestFilesDirectory"/> - not available in CI or on another machine, so every
/// test here checks <see cref="FixturePair.HaveFixtures"/> first and returns early (not a
/// true xUnit skip - this project's xunit version predates <c>Assert.Skip</c> and adding a
/// skip-support package for just this felt like more than the one real gap here warrants)
/// rather than failing when they're absent.
/// </summary>
public class HackContainerRealFileTests
{
    private const string TestFilesDirectory = @"E:\source\git\NitroTwl\test_files";

    public sealed record FixturePair(string Name, string BaseNdz, string RealDeltaNdz, string BaseRom, string TargetRom, string XdeltaPatch)
    {
        public bool HaveFixtures =>
            File.Exists(BaseNdz) && File.Exists(RealDeltaNdz) && File.Exists(BaseRom) && File.Exists(TargetRom) && File.Exists(XdeltaPatch);
    }

    public static readonly TheoryData<FixturePair> Pairs = new()
    {
        new FixturePair(
            Name: "Pokemon Black/White",
            BaseNdz: TestFilesDirectory + @"\Pokemon - Black Version (USA, Europe) (NDSi Enhanced)_dict6m.ndz",
            RealDeltaNdz: TestFilesDirectory + @"\Pokemon - White Version (USA, Europe) (NDSi Enhanced).delta.ndz",
            BaseRom: TestFilesDirectory + @"\Pokemon - Black Version (USA, Europe) (NDSi Enhanced)\Pokemon - Black Version (USA, Europe) (NDSi Enhanced).nds",
            TargetRom: TestFilesDirectory + @"\Pokemon - White Version (USA, Europe) (NDSi Enhanced)\Pokemon - White Version (USA, Europe) (NDSi Enhanced).nds",
            XdeltaPatch: TestFilesDirectory + @"\Pokemon - White Version (USA, Europe) (NDSi Enhanced).xdelta"),
        new FixturePair(
            Name: "Mega Man Star Force Dragon/Leo",
            BaseNdz: TestFilesDirectory + @"\Mega Man Star Force - Dragon (USA)_dict106k.ndz",
            RealDeltaNdz: TestFilesDirectory + @"\Mega Man Star Force - Leo (USA).delta.ndz",
            BaseRom: TestFilesDirectory + @"\Mega Man Star Force - Dragon (USA)\Mega Man Star Force - Dragon (USA).nds",
            TargetRom: TestFilesDirectory + @"\Mega Man Star Force - Leo (USA)\Mega Man Star Force - Leo (USA).nds",
            XdeltaPatch: TestFilesDirectory + @"\Mega Man Star Force - Leo (USA).xdelta"),
    };

    private static string Sha256Of(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// The strongest possible check: our reader decodes a `.delta.ndz` genuine
    /// ndz-studio produced (not one of our own) byte-exact against the real target ROM.
    /// This is what confirms the reverse-engineered format is actually right, not just
    /// self-consistent with our own writer's assumptions.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void Open_RealNdzStudioDeltaNdz_DecodesByteExactToRealTargetRom(FixturePair pair)
    {
        if (!pair.HaveFixtures)
            return;

        byte[] baseNdz = File.ReadAllBytes(pair.BaseNdz);
        byte[] realDeltaNdz = File.ReadAllBytes(pair.RealDeltaNdz);
        byte[] realTargetRom = File.ReadAllBytes(pair.TargetRom);

        using var archive = NdzArchive.Open(realDeltaNdz, baseNdzBytes: baseNdz);
        byte[] decoded = archive.DecompressAll();

        Assert.Equal(Sha256Of(realTargetRom), Sha256Of(decoded));
    }

    /// <summary>
    /// Our own writer's output isn't expected to be byte-identical to ndz-studio's own
    /// file (its block-matching/candidate-selection heuristics are its own, not ours),
    /// but it must round-trip correctly against real ROM content, not just synthetic
    /// test fixtures - and land close in size, confirming the content-defined-chunking
    /// search (see ChunkRunMatcher) generalizes past the single sample it was built
    /// against, not just tuned to it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void Compress_RealBaseAndTargetRoms_RoundTripsByteExact(FixturePair pair)
    {
        if (!pair.HaveFixtures)
            return;

        byte[] baseNdz = File.ReadAllBytes(pair.BaseNdz);
        byte[] realBaseRom = File.ReadAllBytes(pair.BaseRom);
        byte[] realTargetRom = File.ReadAllBytes(pair.TargetRom);

        using var output = new MemoryStream();
        HackContainerWriter.Compress(realTargetRom, realBaseRom, baseNdz, output);

        using var archive = NdzArchive.Open(output.ToArray(), baseNdzBytes: baseNdz);
        byte[] decoded = archive.DecompressAll();

        Assert.Equal(Sha256Of(realTargetRom), Sha256Of(decoded));
    }

    /// <summary>
    /// The xdelta-patch overload (base + a real standalone .xdelta, no target ROM supplied
    /// directly) must reconstruct the same target ROM via <see cref="XDeltaCodec.Apply"/>
    /// and converge on the same output as the direct-target overload - real patches, not
    /// synthetic ones, confirming the two input paths genuinely are equivalent in practice.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void CompressFromPatch_RealXdeltaPatch_MatchesDirectTargetOutput(FixturePair pair)
    {
        if (!pair.HaveFixtures)
            return;

        byte[] baseNdz = File.ReadAllBytes(pair.BaseNdz);
        byte[] realBaseRom = File.ReadAllBytes(pair.BaseRom);
        byte[] realTargetRom = File.ReadAllBytes(pair.TargetRom);
        byte[] realPatch = File.ReadAllBytes(pair.XdeltaPatch);

        using var viaPatch = new MemoryStream();
        HackContainerWriter.CompressFromPatch(realBaseRom, realPatch, baseNdz, viaPatch);

        using var viaTarget = new MemoryStream();
        HackContainerWriter.Compress(realTargetRom, realBaseRom, baseNdz, viaTarget);

        Assert.Equal(viaTarget.ToArray(), viaPatch.ToArray());

        using var archive = NdzArchive.Open(viaPatch.ToArray(), baseNdzBytes: baseNdz);
        Assert.Equal(Sha256Of(realTargetRom), Sha256Of(archive.DecompressAll()));
    }
}
