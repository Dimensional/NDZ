using System.Buffers.Binary;

namespace Ndz.Core.Format;

/// <summary>
/// The handful of .nds header fields NDZ needs to populate its front-matter, plus the
/// verbatim banner block. See GBATEK for the full .nds header layout; NDZ only cares
/// about the game code (0x0C) and the banner offset (0x68).
/// </summary>
public sealed class NdsRomInfo
{
    public required uint GameCode { get; init; }

    /// <summary>Always exactly <see cref="NdzConstants.BannerSlotLength"/> bytes, zero-padded.</summary>
    public required byte[] Banner { get; init; }

    /// <summary>
    /// The banner's own version field (the same u16 <see cref="Banner"/> starts with) -
    /// determines which languages and features the banner actually carries (see
    /// <see cref="NdzConstants.GetBannerContentSize"/>). A value &gt;= 0x0103 marks a
    /// DSi-enhanced banner (e.g. Pokemon Black/White), which additionally carries an
    /// animated icon sequence - not decoded by <see cref="NdsIcon"/>, which only reads the
    /// static frame present at the same fixed offsets in every version.
    /// </summary>
    public required ushort BannerVersion { get; init; }

    /// <summary>
    /// The header's own unit-code byte (0x12): 00h=NDS-only, 02h=NDS+DSi (DSi-enhanced,
    /// e.g. Pokemon Black/White), 03h=DSi-exclusive. The authoritative platform flag -
    /// unlike <see cref="BannerVersion"/>, which only reflects whether the banner happens
    /// to carry an animated icon, not what platform the cartridge actually targets.
    /// </summary>
    public required byte UnitCode { get; init; }

    /// <summary>UnitCode == 0x02 - NDS+DSi. Runs on original DS hardware too, unlike <see cref="IsDsiExclusive"/>.</summary>
    public bool IsDsiEnhanced => UnitCode == 0x02;

    /// <summary>UnitCode == 0x03 - DSi/3DS only, will not boot on original DS hardware.</summary>
    public bool IsDsiExclusive => UnitCode == 0x03;

    /// <summary>UnitCode bit1 set (0x02 or 0x03) - the cartridge carries a DSi extended header past the plain NDS one, regardless of which of the two DSi unit codes it is.</summary>
    public bool HasDsiExtendedHeader => (UnitCode & 0x02) != 0;

    /// <summary>
    /// The header's own revision byte (0x1E) - usually 0x00, but distinguishes a
    /// re-release sharing the same <see cref="GameCode"/> (e.g. a Wii U Virtual Console
    /// dump of Super Mario 64 DS vs. the original cartridge). Two ROMs are only truly
    /// guaranteed identical when both GameCode and RomVersion match - not GameCode alone.
    /// </summary>
    public required byte RomVersion { get; init; }

    /// <summary>
    /// Reads the game code and banner out of a raw, decrypted .nds image. The banner's
    /// real content size depends on its own version field (a u16 at the banner offset
    /// itself) - see <see cref="NdzConstants.GetBannerContentSize"/> - not a fixed
    /// full-slot copy.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The banner offset is 0 or points past the end of the ROM. Confirmed against both
    /// reference implementations (`pack.rs`'s `build_frontmatter`, `ndztool.py`'s
    /// `build_ndz_frontmatter`) - both hard-refuse to pack such a ROM at all rather than
    /// degrading to an empty banner, which this method previously did (fixed 2026-08-31,
    /// a real, confirmed divergence found during a broader audit pass - no test had ever
    /// exercised this path, and nothing downstream depended on the lenient behavior).
    /// </exception>
    public static NdsRomInfo FromRom(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < NdzConstants.NdsHeader.HeaderLength)
            throw new InvalidDataException($"Input is only {rom.Length} bytes; too small to be an .nds ROM (need at least {NdzConstants.NdsHeader.HeaderLength}-byte header).");

        uint gameCode = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(NdzConstants.NdsHeader.GameCodeOffset, 4));
        byte unitCode = rom[NdzConstants.NdsHeader.UnitCodeOffset];
        byte romVersion = rom[NdzConstants.NdsHeader.RomVersionOffset];
        uint bannerOffset = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(NdzConstants.NdsHeader.BannerOffsetOffset, 4));

        if (bannerOffset == 0 || bannerOffset >= (uint)rom.Length)
            throw new InvalidDataException($"Invalid bannerOffset 0x{bannerOffset:X} in .nds header - must be nonzero and within the ROM ({rom.Length:N0} bytes).");

        // The banner's own version field is a u16 read unconditionally at bannerOffset
        // by both reference implementations (`nds_data[banner_offset:banner_offset+2]`
        // unpacked as "<H" in ndztool.py, which hard-crashes via struct.error if fewer
        // than 2 bytes remain) - not defaulted past. A ROM 1 byte short of a full version
        // field is exactly as invalid as one with no banner at all.
        if (bannerOffset + 2 > (uint)rom.Length)
            throw new InvalidDataException($"Banner offset 0x{bannerOffset:X} leaves no room for the banner's own 2-byte version field in a {rom.Length:N0}-byte ROM.");

        var banner = new byte[NdzConstants.BannerSlotLength];

        ushort bannerVersion = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice((int)bannerOffset, 2));

        int bannerContentSize = NdzConstants.GetBannerContentSize(bannerVersion);
        int available = (int)Math.Min(bannerContentSize, rom.Length - (long)bannerOffset);
        available = Math.Min(available, NdzConstants.BannerSlotLength);

        rom.Slice((int)bannerOffset, available).CopyTo(banner);

        return new NdsRomInfo { GameCode = gameCode, Banner = banner, BannerVersion = bannerVersion, UnitCode = unitCode, RomVersion = romVersion };
    }
}
