using Ndz.Core.Format;

namespace Ndz.Core.Tests;

public class NdsRomInfoTests
{
    [Theory]
    [InlineData((ushort)0, 0x840)]
    [InlineData((ushort)1, 0x840)]
    [InlineData((ushort)2, 0x940)]
    [InlineData((ushort)3, 0xA40)]
    [InlineData((ushort)0x0103, 0x23C0)]
    [InlineData((ushort)0xFFFF, 0x23C0)]
    public void FromRom_SizesBannerByVersion(ushort version, int expectedContentSize)
    {
        Assert.Equal(expectedContentSize, NdzConstants.GetBannerContentSize(version));

        // Big enough to fit even the largest (0x23C0) banner variant past the 0x200 offset.
        byte[] rom = TestRom.Build(0x200 + 0x23C0 + 0x100, bannerVersion: version);
        NdsRomInfo info = NdsRomInfo.FromRom(rom);

        var expectedBanner = new byte[NdzConstants.BannerSlotLength];
        rom.AsSpan(0x200, expectedContentSize).CopyTo(expectedBanner);

        Assert.Equal(expectedBanner, info.Banner);
        // Confirm the tail really is zero-padded, not leftover source bytes.
        Assert.All(info.Banner.AsSpan(expectedContentSize).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void FromRom_TruncatesBannerAtEndOfRom()
    {
        // ROM ends partway through what would otherwise be a full 0x23C0 banner.
        int romSize = 0x200 + 0x1000;
        byte[] rom = TestRom.Build(romSize, bannerVersion: 0x0103);

        NdsRomInfo info = NdsRomInfo.FromRom(rom);

        var expectedBanner = new byte[NdzConstants.BannerSlotLength];
        rom.AsSpan(0x200, romSize - 0x200).CopyTo(expectedBanner);

        Assert.Equal(expectedBanner, info.Banner);
    }

    /// <summary>
    /// Confirmed against both reference implementations (`pack.rs`'s
    /// `build_frontmatter`, `ndztool.py`'s `build_ndz_frontmatter`): a zero or
    /// out-of-range bannerOffset is a hard error, not something to silently degrade
    /// past with an empty banner.
    /// </summary>
    [Theory]
    [InlineData((byte)0x00, false, false, false)] // NDS-only
    [InlineData((byte)0x02, true, false, true)]   // NDS+DSi (DSi-enhanced, e.g. Pokemon Black/White)
    [InlineData((byte)0x03, false, true, true)]   // DSi-exclusive
    public void FromRom_ReadsUnitCodeAndDerivedPlatformFlags(byte unitCode, bool expectedEnhanced, bool expectedExclusive, bool expectedHasExtendedHeader)
    {
        byte[] rom = TestRom.Build(0x1000, unitCode: unitCode);
        NdsRomInfo info = NdsRomInfo.FromRom(rom);

        Assert.Equal(unitCode, info.UnitCode);
        Assert.Equal(expectedEnhanced, info.IsDsiEnhanced);
        Assert.Equal(expectedExclusive, info.IsDsiExclusive);
        Assert.Equal(expectedHasExtendedHeader, info.HasDsiExtendedHeader);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)255)]
    public void FromRom_ReadsRomVersion(byte romVersion)
    {
        byte[] rom = TestRom.Build(0x1000, romVersion: romVersion);
        NdsRomInfo info = NdsRomInfo.FromRom(rom);

        Assert.Equal(romVersion, info.RomVersion);
    }

    [Fact]
    public void FromRom_ThrowsOnZeroBannerOffset()
    {
        byte[] rom = TestRom.Build(0x200 + 0x1000);
        BitConverter.GetBytes((uint)0).CopyTo(rom, 0x68);

        Assert.Throws<InvalidDataException>(() => NdsRomInfo.FromRom(rom));
    }

    [Fact]
    public void FromRom_ThrowsOnBannerOffsetPastEndOfRom()
    {
        byte[] rom = TestRom.Build(0x200 + 0x1000);
        BitConverter.GetBytes((uint)rom.Length).CopyTo(rom, 0x68);

        Assert.Throws<InvalidDataException>(() => NdsRomInfo.FromRom(rom));
    }

    /// <summary>
    /// Confirmed against `ndztool.py`'s `build_ndz_frontmatter`: it reads the banner's
    /// own 2-byte version field unconditionally (`struct.unpack("&lt;H", ...)`), which
    /// hard-crashes if fewer than 2 bytes remain - a ROM whose bannerOffset leaves room
    /// for only 1 more byte is exactly as invalid as one with no banner at all.
    /// </summary>
    [Fact]
    public void FromRom_ThrowsWhenBannerOffsetLeavesNoRoomForVersionField()
    {
        byte[] rom = TestRom.Build(0x200 + 0x1000);
        uint bannerOffset = (uint)rom.Length - 1;
        BitConverter.GetBytes(bannerOffset).CopyTo(rom, 0x68);

        Assert.Throws<InvalidDataException>(() => NdsRomInfo.FromRom(rom));
    }
}
