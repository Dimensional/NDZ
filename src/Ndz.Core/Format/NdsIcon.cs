using System.Buffers.Binary;

namespace Ndz.Core.Format;

/// <summary>
/// The language slots present in an .nds banner's title table, in their fixed on-disk
/// order (see <see cref="NdzConstants.NdsBanner"/>). Values are the slot index, not just
/// a label. Chinese and Korean only exist in banners whose version is high enough to have
/// written them (2 and 3 respectively, see <see cref="NdzConstants.GetBannerContentSize"/>)
/// - requesting them from an older banner decodes the zero-padded tail as an empty string,
/// not an error, since <see cref="NdsRomInfo.Banner"/> is always zero-padded to the full
/// <see cref="NdzConstants.BannerSlotLength"/> regardless of the real content size.
/// </summary>
public enum NdsTitleLanguage
{
    Japanese = 0,
    English = 1,
    French = 2,
    German = 3,
    Italian = 4,
    Spanish = 5,
    Chinese = 6,
    Korean = 7,
}

/// <summary>
/// Decodes the 32x32 icon bitmap and title strings embedded in an .nds banner (see
/// <see cref="NdsRomInfo.Banner"/>). Only the banner's primary, static icon frame is
/// decoded - a DSi-enhanced banner (version &gt;= 0x0103, e.g. Pokemon Black/White) also
/// carries an animated icon sequence (extra bitmap/palette frames plus a playback
/// sequence) appended after the title table, which this deliberately does not decode:
/// the animated frames live at a fixed offset past the title table's end regardless of
/// how many language slots that particular version has, and are out of scope here by
/// design, not by oversight - the static frame at the fixed offsets below is present and
/// identical in meaning across every banner version.
/// </summary>
public static class NdsIcon
{
    public const int Width = 32;
    public const int Height = 32;

    /// <summary>
    /// Decodes the 32x32 icon as straight-alpha RGBA8888, row-major top-to-bottom,
    /// <see cref="Width"/>*<see cref="Height"/>*4 bytes. Palette index 0 is always fully
    /// transparent (alpha 0), matching the DS banner format's own convention - not a color
    /// choice made here.
    /// </summary>
    public static byte[] DecodeBitmap(ReadOnlySpan<byte> banner)
    {
        int paletteEnd = NdzConstants.NdsBanner.PaletteOffset + NdzConstants.NdsBanner.PaletteLength;
        if (banner.Length < paletteEnd)
            throw new ArgumentException($"Banner is only {banner.Length} bytes; too small to hold an icon bitmap ({NdzConstants.NdsBanner.BitmapLength} bytes at 0x{NdzConstants.NdsBanner.BitmapOffset:X}) and palette ({NdzConstants.NdsBanner.PaletteLength} bytes at 0x{NdzConstants.NdsBanner.PaletteOffset:X}).", nameof(banner));

        // 16 colors, RGB555 little-endian, index 0 transparent regardless of its stored value.
        Span<(byte R, byte G, byte B)> palette = stackalloc (byte, byte, byte)[16];
        ReadOnlySpan<byte> paletteBytes = banner.Slice(NdzConstants.NdsBanner.PaletteOffset, NdzConstants.NdsBanner.PaletteLength);
        for (int i = 0; i < 16; i++)
        {
            ushort rgb555 = BinaryPrimitives.ReadUInt16LittleEndian(paletteBytes.Slice(i * 2, 2));
            int r5 = rgb555 & 0x1F;
            int g5 = (rgb555 >> 5) & 0x1F;
            int b5 = (rgb555 >> 10) & 0x1F;
            // 5-bit -> 8-bit: replicate the top 3 bits into the low bits so 0 stays 0 and 31 stays 255.
            palette[i] = ((byte)((r5 << 3) | (r5 >> 2)), (byte)((g5 << 3) | (g5 >> 2)), (byte)((b5 << 3) | (b5 >> 2)));
        }

        ReadOnlySpan<byte> bitmap = banner.Slice(NdzConstants.NdsBanner.BitmapOffset, NdzConstants.NdsBanner.BitmapLength);
        var rgba = new byte[Width * Height * 4];

        // Standard DS/GBA tile layout: a 4x4 grid of 8x8-pixel tiles, each tile stored
        // row-major, 4 bits/pixel with the low nibble of each byte as the left pixel of
        // the pair and the high nibble as the right pixel.
        int srcIndex = 0;
        for (int tileY = 0; tileY < 4; tileY++)
        {
            for (int tileX = 0; tileX < 4; tileX++)
            {
                for (int py = 0; py < 8; py++)
                {
                    int y = tileY * 8 + py;
                    for (int px = 0; px < 8; px += 2)
                    {
                        byte b = bitmap[srcIndex++];
                        int x0 = tileX * 8 + px;
                        WritePixel(rgba, x0, y, palette[b & 0xF], (b & 0xF) != 0);
                        WritePixel(rgba, x0 + 1, y, palette[(b >> 4) & 0xF], ((b >> 4) & 0xF) != 0);
                    }
                }
            }
        }

        return rgba;
    }

    private static void WritePixel(byte[] rgba, int x, int y, (byte R, byte G, byte B) color, bool opaque)
    {
        int i = (y * Width + x) * 4;
        rgba[i + 0] = color.R;
        rgba[i + 1] = color.G;
        rgba[i + 2] = color.B;
        rgba[i + 3] = opaque ? (byte)255 : (byte)0;
    }

    /// <summary>
    /// Decodes one language's title slot: up to 128 UTF-16LE characters, NUL-terminated
    /// (real banners NUL-pad the whole slot, so trailing padding decodes away, not into
    /// embedded NUL characters). Typically 2-3 lines separated by '\n' (game name,
    /// subtitle, publisher) - see <see cref="DecodeShortTitle"/> for just the first line.
    /// Returns an empty string, not an error, for a slot past the real banner content
    /// (see <see cref="NdsTitleLanguage"/>) since it decodes as all-zero padding.
    /// </summary>
    public static string DecodeTitle(ReadOnlySpan<byte> banner, NdsTitleLanguage language = NdsTitleLanguage.English)
    {
        int offset = NdzConstants.NdsBanner.TitleTableOffset + (int)language * NdzConstants.NdsBanner.TitleSlotLength;
        if (offset + NdzConstants.NdsBanner.TitleSlotLength > banner.Length)
            return string.Empty;

        ReadOnlySpan<byte> slot = banner.Slice(offset, NdzConstants.NdsBanner.TitleSlotLength);
        int charCount = slot.Length / 2;
        Span<char> chars = stackalloc char[charCount];
        for (int i = 0; i < charCount; i++)
            chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(i * 2, 2));

        int nul = chars.IndexOf('\0');
        return new string(nul >= 0 ? chars[..nul] : chars);
    }

    /// <summary>The title's first line only - the compact, single-line form a card/list row wants.</summary>
    public static string DecodeShortTitle(ReadOnlySpan<byte> banner, NdsTitleLanguage language = NdsTitleLanguage.English)
    {
        string full = DecodeTitle(banner, language);
        int newline = full.IndexOfAny(['\n', '\r']);
        return newline >= 0 ? full[..newline] : full;
    }
}
