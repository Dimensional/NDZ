using System.Buffers.Binary;
using System.Text;
using Ndz.Core.Format;

namespace Ndz.Core.Tests;

public class NdsIconTests
{
    /// <summary>Builds a <see cref="NdzConstants.BannerSlotLength"/>-byte banner with a controlled palette, bitmap, and title table.</summary>
    private static byte[] BuildBanner(Action<byte[]>? customize = null)
    {
        var banner = new byte[NdzConstants.BannerSlotLength];

        // Palette: index 0 arbitrary (must render transparent regardless), index 1 pure
        // red, index 2 pure green, index 3 pure blue - exact 5-bit channel values so the
        // 5->8 bit expansion is exact (0 and 31 round-trip cleanly).
        WriteRgb555(banner, NdzConstants.NdsBanner.PaletteOffset + 0 * 2, 0x7FFF); // index 0: white, but must still be transparent
        WriteRgb555(banner, NdzConstants.NdsBanner.PaletteOffset + 1 * 2, 0x001F); // index 1: pure red (r=31,g=0,b=0)
        WriteRgb555(banner, NdzConstants.NdsBanner.PaletteOffset + 2 * 2, 0x03E0); // index 2: pure green
        WriteRgb555(banner, NdzConstants.NdsBanner.PaletteOffset + 3 * 2, 0x7C00); // index 3: pure blue

        // Bitmap: every pixel index 1 (red), except the very first byte's low nibble is
        // index 0 (must be transparent) and high nibble is index 2 (green).
        banner.AsSpan(NdzConstants.NdsBanner.BitmapOffset, NdzConstants.NdsBanner.BitmapLength).Fill(0x11);
        banner[NdzConstants.NdsBanner.BitmapOffset] = 0x20; // pixel (0,0)=index0(transparent), pixel(1,0)=index2(green)

        // English title: "Test Game\nSubtitle", NUL-padded tail.
        WriteTitle(banner, NdsTitleLanguage.English, "Test Game\nSubtitle");

        customize?.Invoke(banner);
        return banner;
    }

    private static void WriteRgb555(byte[] banner, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(banner.AsSpan(offset, 2), value);

    private static void WriteTitle(byte[] banner, NdsTitleLanguage language, string text)
    {
        int offset = NdzConstants.NdsBanner.TitleTableOffset + (int)language * NdzConstants.NdsBanner.TitleSlotLength;
        byte[] utf16 = Encoding.Unicode.GetBytes(text);
        utf16.CopyTo(banner, offset);
        // Rest of the slot stays zero (NUL-padded), matching a real banner.
    }

    [Fact]
    public void DecodeBitmap_TransparentAtPaletteIndexZero()
    {
        byte[] rgba = NdsIcon.DecodeBitmap(BuildBanner());

        int i = PixelIndex(0, 0);
        Assert.Equal(0, rgba[i + 3]); // alpha 0 regardless of the stored (0x7FFF) color
    }

    [Fact]
    public void DecodeBitmap_DecodesExpandedRgbForKnownIndices()
    {
        byte[] rgba = NdsIcon.DecodeBitmap(BuildBanner());

        // Pixel (1,0) = palette index 2 = pure green (5-bit 31 -> 8-bit 255).
        int greenPixel = PixelIndex(1, 0);
        Assert.Equal((0, 255, 0, 255), (rgba[greenPixel], rgba[greenPixel + 1], rgba[greenPixel + 2], rgba[greenPixel + 3]));

        // Pixel (2,0) = palette index 1 = pure red.
        int redPixel = PixelIndex(2, 0);
        Assert.Equal((255, 0, 0, 255), (rgba[redPixel], rgba[redPixel + 1], rgba[redPixel + 2], rgba[redPixel + 3]));
    }

    [Fact]
    public void DecodeBitmap_ProducesFullSizedBuffer()
    {
        byte[] rgba = NdsIcon.DecodeBitmap(BuildBanner());
        Assert.Equal(NdsIcon.Width * NdsIcon.Height * 4, rgba.Length);
    }

    [Fact]
    public void DecodeBitmap_ThrowsWhenBannerTooSmall()
    {
        var tooSmall = new byte[NdzConstants.NdsBanner.PaletteOffset];
        Assert.Throws<ArgumentException>(() => NdsIcon.DecodeBitmap(tooSmall));
    }

    [Fact]
    public void DecodeTitle_ReadsFullMultiLineTitleAndTrimsNulPadding()
    {
        string title = NdsIcon.DecodeTitle(BuildBanner(), NdsTitleLanguage.English);
        Assert.Equal("Test Game\nSubtitle", title);
    }

    [Fact]
    public void DecodeShortTitle_ReturnsOnlyFirstLine()
    {
        string title = NdsIcon.DecodeShortTitle(BuildBanner(), NdsTitleLanguage.English);
        Assert.Equal("Test Game", title);
    }

    [Fact]
    public void DecodeTitle_EmptyForLanguageBeyondRealBannerContent()
    {
        // Only English was written; Korean's slot is all-zero padding either way (the
        // banner array is always the full zero-padded BannerSlotLength), so it must
        // decode as an empty string, not throw or return garbage.
        string title = NdsIcon.DecodeTitle(BuildBanner(), NdsTitleLanguage.Korean);
        Assert.Equal(string.Empty, title);
    }

    [Fact]
    public void DecodeTitle_JapaneseSlotUnaffectedByEnglishWrite()
    {
        string japanese = NdsIcon.DecodeTitle(BuildBanner(), NdsTitleLanguage.Japanese);
        Assert.Equal(string.Empty, japanese);
    }

    private static int PixelIndex(int x, int y) => (y * NdsIcon.Width + x) * 4;
}
