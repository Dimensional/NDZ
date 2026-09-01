using Ndz.Core.Format;

namespace Ndz.Core.Tests;

public class NdzFrontMatterTests
{
    private static NdzFrontMatter MakeSample() => new()
    {
        OriginalSize = 0x1234_5678,
        GameCode = 0x4543424Au, // "ACEB" little-endian, arbitrary
        Banner = BuildPattern(NdzConstants.BannerSlotLength, 0xAB),
    };

    private static byte[] BuildPattern(int length, byte seed)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(seed + i);
        return data;
    }

    [Fact]
    public void RoundTrips_AllFields()
    {
        var original = MakeSample();
        var buffer = new byte[NdzConstants.FrontMatterSize];
        original.WriteTo(buffer);

        var parsed = NdzFrontMatter.Read(buffer);

        Assert.Equal(original.OriginalSize, parsed.OriginalSize);
        Assert.Equal(original.GameCode, parsed.GameCode);
        Assert.Equal(original.Banner, parsed.Banner);
        Assert.Equal(original.Flags, parsed.Flags);
        Assert.False(parsed.HasDictionary);
    }

    [Fact]
    public void WriteTo_EmbedsMagicAndSize()
    {
        var buffer = new byte[NdzConstants.FrontMatterSize];
        MakeSample().WriteTo(buffer);

        uint magic = BitConverter.ToUInt32(buffer, 0x0000);
        uint size = BitConverter.ToUInt32(buffer, 0x0004);

        Assert.Equal(NdzConstants.Magic, magic);
        Assert.Equal((uint)NdzConstants.FrontMatterSize, size);
    }

    [Fact]
    public void Read_RejectsBadMagic()
    {
        var buffer = new byte[NdzConstants.FrontMatterSize];
        MakeSample().WriteTo(buffer);
        buffer[0] = 0x00; // corrupt the magic

        Assert.Throws<InvalidDataException>(() => NdzFrontMatter.Read(buffer));
    }

    [Fact]
    public void Read_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => NdzFrontMatter.Read(new byte[NdzConstants.FrontMatterSize - 1]));
    }

    /// <summary>
    /// BasePatch is a real, implemented feature now (see NdzArchive.Open's `baseRom`
    /// parameter) - unlike TrainedDictionary, parsing a front-matter with it set
    /// succeeds; whether a specific *open* can honor it is a policy question that needs
    /// the caller-supplied base ROM this parse-only method never sees.
    /// </summary>
    [Fact]
    public void Read_AllowsBasePatchFlag()
    {
        var sample = new NdzFrontMatter
        {
            OriginalSize = 1,
            GameCode = 0,
            Banner = new byte[NdzConstants.BannerSlotLength],
            Flags = NdzFlags.BasePatch,
        };
        var buffer = new byte[NdzConstants.FrontMatterSize];
        sample.WriteTo(buffer);

        var parsed = NdzFrontMatter.Read(buffer);
        Assert.True(parsed.Flags.HasFlag(NdzFlags.BasePatch));
    }

    [Fact]
    public void Read_RejectsTrainedDictionaryFlag_NotImplemented()
    {
        var sample = new NdzFrontMatter
        {
            OriginalSize = 1,
            GameCode = 0,
            Banner = new byte[NdzConstants.BannerSlotLength],
            Flags = NdzFlags.TrainedDictionary,
        };
        var buffer = new byte[NdzConstants.FrontMatterSize];
        sample.WriteTo(buffer);

        Assert.Throws<NotSupportedException>(() => NdzFrontMatter.Read(buffer));
    }

    [Fact]
    public void WriteTo_RejectsWrongDestinationLength()
    {
        Assert.Throws<ArgumentException>(() => MakeSample().WriteTo(new byte[NdzConstants.FrontMatterSize - 1]));
    }
}
