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
    /// Reads the game code and banner out of a raw, decrypted .nds image. The banner's
    /// real content size depends on its own version field (a u16 at the banner offset
    /// itself) - see <see cref="NdzConstants.GetBannerContentSize"/> - not a fixed
    /// full-slot copy.
    /// </summary>
    public static NdsRomInfo FromRom(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < NdzConstants.NdsHeader.HeaderLength)
            throw new InvalidDataException($"Input is only {rom.Length} bytes; too small to be an .nds ROM (need at least {NdzConstants.NdsHeader.HeaderLength}-byte header).");

        uint gameCode = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(NdzConstants.NdsHeader.GameCodeOffset, 4));
        uint bannerOffset = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(NdzConstants.NdsHeader.BannerOffsetOffset, 4));

        var banner = new byte[NdzConstants.BannerSlotLength];
        if (bannerOffset != 0 && bannerOffset < (uint)rom.Length)
        {
            ushort bannerVersion = bannerOffset + 2 <= (uint)rom.Length
                ? BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice((int)bannerOffset, 2))
                : (ushort)0;

            int bannerContentSize = NdzConstants.GetBannerContentSize(bannerVersion);
            int available = (int)Math.Min(bannerContentSize, rom.Length - (long)bannerOffset);
            available = Math.Min(available, NdzConstants.BannerSlotLength);

            rom.Slice((int)bannerOffset, available).CopyTo(banner);
        }

        return new NdsRomInfo { GameCode = gameCode, Banner = banner };
    }
}
