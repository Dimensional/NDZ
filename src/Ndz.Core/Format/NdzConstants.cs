namespace Ndz.Core.Format;

/// <summary>
/// Fixed sizes and magic numbers from the NDZ format spec. Corrected against the format
/// author's reference packer (a Rust implementation, `pack.rs`) after an initial pass
/// that mis-modeled the frame/block hierarchy - see docs/ndz-format-spec.md for the
/// full history of what changed and why.
/// </summary>
public static class NdzConstants
{
    /// <summary>u32 magic "NDZ1", little-endian bytes 'N','D','Z','1'.</summary>
    public const uint Magic = 0x315A444E;

    /// <summary>
    /// The trailer footer's second u32 is a distinct magic, NOT <see cref="Magic"/>
    /// reused - confirmed against the reference packer (which calls it
    /// "LZ4BENCH_MAGIC" internally, an apparently unrelated reused name).
    /// </summary>
    public const uint TrailerMagic = 0x4C5A3442;

    /// <summary>Fixed, cluster-aligned size of the front-matter block.</summary>
    public const int FrontMatterSize = 16384;

    public const int BannerOffset = 0x0010;

    /// <summary>
    /// Reserved front-matter space for the banner. The banner's actual content is
    /// smaller and version-dependent (see <see cref="GetBannerContentSize"/>) and is
    /// zero-padded up to this size - it is NOT simply an 0x2400-byte copy from the
    /// source ROM regardless of the real banner's size.
    /// </summary>
    public const int BannerSlotLength = 0x2400;

    public const int GameCodeOffset = 0x2410;
    public const int DictionaryStoredSizeOffset = 0x2414;
    public const int BaseOriginalSizeOffset = 0x2418;
    public const int BaseGameCodeOffset = 0x241C;
    public const int UnusedHashOffset = 0x2420;
    public const int DictionaryDecompressedSizeOffset = 0x2428;
    public const int BaseHeaderOffset = 0x2430;
    public const int BaseHeaderLength = 0x200;

    /// <summary>
    /// The unit the outer seek table addresses - what the format's ASCII diagram calls
    /// "frame 0, frame 1, ...". 128 KiB. Confirmed against the reference packer's
    /// `FRAME` constant. This is NOT the 8 KiB unit - see <see cref="BlockSize"/>.
    /// </summary>
    public const int FrameSize = 128 * 1024;

    /// <summary>
    /// The inner unit each frame is subdivided into: 8 KiB, independently
    /// zstd-compressed with its own per-block compression-mode byte. A frame's payload
    /// is privately structured as
    /// <c>[u32 csize × nblocks][u8 mode × nblocks][compressed block bytes, concatenated]</c>
    /// - this inner structure is invisible to the outer seek table, which only ever
    /// records whole-frame (csize, dsize). Confirmed against the reference packer's
    /// `BLOCK` constant. Also embedded (as log2) into the front-matter's flags field -
    /// see <see cref="NdzFlagsExtensions.GetBlockSizeLog2"/>.
    /// </summary>
    public const int BlockSize = 8192;

    /// <summary>Size in bytes of one outer (frame-level) seek-table entry: u32 csize + u32 dsize.</summary>
    public const int SeekTableEntrySize = 8;

    /// <summary>Size of the trailer footer that follows the seek table: u32 nframes + u32 <see cref="TrailerMagic"/>.</summary>
    public const int TrailerFooterSize = 8;

    /// <summary>Offsets and lengths of fields within an .nds ROM header, used to populate the front-matter.</summary>
    public static class NdsHeader
    {
        public const int GameCodeOffset = 0x0C;
        public const int BannerOffsetOffset = 0x68;
        public const int HeaderLength = 0x200;
    }

    /// <summary>
    /// Real banner content size, keyed by the banner's own version field (a u16 at the
    /// banner offset itself) - NOT always the full <see cref="BannerSlotLength"/>.
    /// Confirmed against the reference packer's `build_frontmatter`.
    /// </summary>
    public static int GetBannerContentSize(ushort bannerVersion) => bannerVersion switch
    {
        >= 0x0103 => 0x23C0,
        >= 3 => 0xA40,
        >= 2 => 0x940,
        _ => 0x840,
    };
}
