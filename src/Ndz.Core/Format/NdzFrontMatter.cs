using System.Buffers.Binary;

namespace Ndz.Core.Format;

/// <summary>
/// The fixed 16 KB front-matter block that opens every .ndz file. See the format spec
/// in docs/ndz-format-spec.md for the full field layout.
/// </summary>
public sealed class NdzFrontMatter
{
    /// <summary>Decompressed size of the original .nds this archive holds (or reconstructs to, in patch mode).</summary>
    public required uint OriginalSize { get; init; }

    public NdzFlags Flags { get; init; } = NdzFlags.None;

    /// <summary>
    /// The .nds banner block (icon/title), copied verbatim from the source ROM, exactly
    /// <see cref="NdzConstants.BannerSlotLength"/> bytes and zero-padded if the real banner is
    /// smaller. Empty (all zero) if the source ROM had no readable banner.
    /// </summary>
    public required byte[] Banner { get; init; }

    /// <summary>Mirror of the .nds header's game code (offset 0x0C).</summary>
    public required uint GameCode { get; init; }

    /// <summary>Compressed size of the dictionary section, or 0 if this file has none.</summary>
    public uint DictionaryStoredSize { get; init; }

    /// <summary>Decompressed size of the dictionary section, or 0 if this file has none.</summary>
    public uint DictionaryDecompressedSize { get; init; }

    /// <summary>Base .nds size. Only meaningful when <see cref="NdzFlags.BasePatch"/> is set.</summary>
    public uint BaseOriginalSize { get; init; }

    /// <summary>Base .nds game code. Only meaningful when <see cref="NdzFlags.BasePatch"/> is set.</summary>
    public uint BaseGameCode { get; init; }

    /// <summary>
    /// BLAKE2b-8-byte hash of the base .nds's first 0x200 bytes (its header), exactly
    /// <see cref="NdzConstants.BaseHeaderHashLength"/> bytes. Only meaningful when
    /// <see cref="NdzFlags.BasePatch"/> is set.
    /// </summary>
    public byte[] BaseHeaderHash { get; init; } = new byte[NdzConstants.BaseHeaderHashLength];

    public bool HasDictionary => DictionaryStoredSize != 0;

    /// <summary>Serializes this front-matter into exactly <see cref="NdzConstants.FrontMatterSize"/> bytes.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length != NdzConstants.FrontMatterSize)
            throw new ArgumentException($"Destination must be exactly {NdzConstants.FrontMatterSize} bytes.", nameof(destination));
        if (Banner.Length != NdzConstants.BannerSlotLength)
            throw new InvalidOperationException($"Banner must be exactly {NdzConstants.BannerSlotLength} bytes.");
        if (BaseHeaderHash.Length != NdzConstants.BaseHeaderHashLength)
            throw new InvalidOperationException($"BaseHeaderHash must be exactly {NdzConstants.BaseHeaderHashLength} bytes.");

        destination.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x0000..], NdzConstants.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x0004..], NdzConstants.FrontMatterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x0008..], OriginalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[0x000C..], (uint)Flags);

        Banner.CopyTo(destination.Slice(NdzConstants.BannerOffset, NdzConstants.BannerSlotLength));

        BinaryPrimitives.WriteUInt32LittleEndian(destination[NdzConstants.GameCodeOffset..], GameCode);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[NdzConstants.DictionaryStoredSizeOffset..], DictionaryStoredSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[NdzConstants.BaseOriginalSizeOffset..], BaseOriginalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[NdzConstants.BaseGameCodeOffset..], BaseGameCode);
        BaseHeaderHash.CopyTo(destination.Slice(NdzConstants.BaseHeaderHashOffset, NdzConstants.BaseHeaderHashLength));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[NdzConstants.DictionaryDecompressedSizeOffset..], DictionaryDecompressedSize);
    }

    /// <summary>Parses a 16 KB front-matter block, validating the magic and declared size.</summary>
    public static NdzFrontMatter Read(ReadOnlySpan<byte> source)
    {
        if (source.Length != NdzConstants.FrontMatterSize)
            throw new ArgumentException($"Source must be exactly {NdzConstants.FrontMatterSize} bytes.", nameof(source));

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source[0x0000..]);
        if (magic != NdzConstants.Magic)
            throw new InvalidDataException($"Bad NDZ magic: expected 0x{NdzConstants.Magic:X8}, got 0x{magic:X8}.");

        uint frontMatterSize = BinaryPrimitives.ReadUInt32LittleEndian(source[0x0004..]);
        if (frontMatterSize != NdzConstants.FrontMatterSize)
            throw new InvalidDataException($"Unsupported frontMatterSize 0x{frontMatterSize:X}; expected 0x{NdzConstants.FrontMatterSize:X}.");

        var flags = (NdzFlags)BinaryPrimitives.ReadUInt32LittleEndian(source[0x000C..]);
        // BasePatch itself isn't rejected here - unlike TrainedDictionary, it's a real,
        // implemented feature now (see Compression.NdzArchive.Open's `baseRom`
        // parameter). Whether a *specific* open can actually honor it (a base ROM was
        // supplied, and it matches) is a policy decision that needs the caller-supplied
        // base ROM this parse-only method never sees - that check lives in
        // NdzArchive.Open instead.
        if (flags.HasFlag(NdzFlags.TrainedDictionary))
        {
            // Explicit, named check rather than relying on NdzArchive.Open's incidental
            // "stored size != decompressed size" inference: a trained-dict section isn't
            // guaranteed to trip that check (it has no such invariant of its own), and
            // even if the sizes happened to match, blindly loading a trained-dict blob as
            // raw content would silently produce garbage rather than fail loudly.
            throw new NotSupportedException(
                "This .ndz uses a trained dictionary section (flags bit 2, NDZ_FLAG_DICT), " +
                "which is retired in the real format and not implemented here - only the raw " +
                "content dictionary (flags bit 5, RawDictionary) is supported.");
        }

        return new NdzFrontMatter
        {
            OriginalSize = BinaryPrimitives.ReadUInt32LittleEndian(source[0x0008..]),
            Flags = flags,
            Banner = source.Slice(NdzConstants.BannerOffset, NdzConstants.BannerSlotLength).ToArray(),
            GameCode = BinaryPrimitives.ReadUInt32LittleEndian(source[NdzConstants.GameCodeOffset..]),
            DictionaryStoredSize = BinaryPrimitives.ReadUInt32LittleEndian(source[NdzConstants.DictionaryStoredSizeOffset..]),
            BaseOriginalSize = BinaryPrimitives.ReadUInt32LittleEndian(source[NdzConstants.BaseOriginalSizeOffset..]),
            BaseGameCode = BinaryPrimitives.ReadUInt32LittleEndian(source[NdzConstants.BaseGameCodeOffset..]),
            DictionaryDecompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(source[NdzConstants.DictionaryDecompressedSizeOffset..]),
            BaseHeaderHash = source.Slice(NdzConstants.BaseHeaderHashOffset, NdzConstants.BaseHeaderHashLength).ToArray(),
        };
    }
}
