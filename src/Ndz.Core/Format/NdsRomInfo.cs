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
    /// The header's own region-LOCK byte (0x1D, GBATEK's literal field name is "NDS
    /// Region") - 00h=Normal, 80h=China (iQue), or 40h=Korea for an NDS-only cart.
    /// Despite the field name, this is NOT the "USA / Europe / Japan"-style release
    /// territory most people mean by "region" - see <see cref="DestinationLabel"/> for
    /// that. The raw byte, always populated regardless of platform - see
    /// <see cref="RegionLockLabel"/> for the version that actually accounts for whether
    /// it's safe to claim anything from it.
    /// </summary>
    public required byte RegionLock { get; init; }

    /// <summary>
    /// <see cref="RegionLock"/> decoded to a human label, or empty when there's nothing
    /// worth asserting: the default "Normal" (0x00, true for virtually every commercial
    /// game), OR any DSi title (<see cref="IsDsiEnhanced"/>/<see cref="IsDsiExclusive"/>).
    /// NitroTwl's own model notes DSi cartridges reinterpret this same byte for an
    /// unrelated, unspecified DSi-specific purpose - confirmed empirically, not just
    /// theoretically: a real DSi-enhanced Pokemon Black dump reads 0x40 here (an NDS-only
    /// cart's "Korea" value) despite not actually being Korea-locked. Claiming a region
    /// lock from it on a DSi title would be actively wrong, not just uninteresting, so
    /// this returns empty there rather than a label nobody can trust.
    /// </summary>
    public string RegionLockLabel => IsDsiEnhanced || IsDsiExclusive ? string.Empty : RegionLock switch
    {
        0x00 => string.Empty,
        0x80 => "China / iQue",
        0x40 => "Korea",
        _ => $"0x{RegionLock:X2}",
    };

    /// <summary>
    /// The game code's own 4th character (not a separate header field - <see cref="GameCode"/>'s
    /// highest byte, since it's read little-endian) - GBATEK calls this "Destination/Language"
    /// and it's what "USA / Europe / Japan" release-territory labels actually come from,
    /// e.g. Pokemon HeartGold's real code IPKE ends in 'E' = USA, Pokemon Black/White's
    /// IRAO/IRBO end in 'O' = International. Distinct from <see cref="RegionLock"/> (a
    /// different, rarely-set hardware field GBATEK happens to also call "Region").
    /// </summary>
    public char DestinationCode => (char)((GameCode >> 24) & 0xFF);

    /// <summary>
    /// <see cref="DestinationCode"/> decoded to a human label, per GBATEK's own
    /// destination-letter table (extracted from no$gba - a copy ships in the NitroTwl
    /// project's docs/, "GBATEK DS Cartridge Header.htm"). W-Z all denote a further,
    /// unspecified European variant per GBATEK's own admittedly vague "W..Z Europe #3..5"
    /// entry - grouped here rather than asserting a precise #3/#4/#5 split GBATEK itself
    /// doesn't commit to.
    /// </summary>
    public string DestinationLabel => DestinationCode switch
    {
        'A' => "Asian",
        'C' => "Chinese",
        'D' => "German",
        'E' => "English/USA",
        'F' => "French",
        'H' => "Dutch",
        'I' => "Italian",
        'J' => "Japanese",
        'K' => "Korean",
        'L' => "USA #2",
        'M' => "Swedish",
        'N' => "Norwegian",
        'O' => "International",
        'P' => "Europe",
        'Q' => "Danish",
        'R' => "Russian",
        'S' => "Spanish",
        'T' => "USA+AUS",
        'U' => "Australian",
        'V' => "EUR+AUS",
        >= 'W' and <= 'Z' => "Europe (alt.)",
        'B' or 'G' => "Unassigned",
        _ => $"Unknown ('{DestinationCode}')",
    };

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
        byte regionLock = rom[NdzConstants.NdsHeader.RegionOffset];
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

        return new NdsRomInfo { GameCode = gameCode, Banner = banner, BannerVersion = bannerVersion, UnitCode = unitCode, RomVersion = romVersion, RegionLock = regionLock };
    }
}
