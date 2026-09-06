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

    /// <summary>
    /// A BLAKE2b-8-byte hash of the base ROM's first 0x200 bytes (its header), checked
    /// against the supplied base at decode time. Only meaningful when
    /// <see cref="NdzFlags.BasePatch"/> is set. Corrected 2026-08-31: previously
    /// documented (wrongly) as an unused/retired field - `ndztool.py`'s own front-matter
    /// layout comment confirms it as a live field, "base header hash", not "was an old
    /// blake2b hash" as this project had guessed before obtaining that source.
    /// </summary>
    public const int BaseHeaderHashOffset = 0x2420;

    public const int BaseHeaderHashLength = 8;

    public const int DictionaryDecompressedSizeOffset = 0x2428;

    // There is no defined field at 0x2430 or beyond in the confirmed real format -
    // ndztool.py's own front-matter layout comment lists every field and ends at 0x2428
    // (dictionaryDecompressedSize, 4 bytes, so 0x242C). A previous 0x200-byte
    // "BaseHeader" (raw base header copy) field once existed here in this project's
    // model - removed 2026-08-31 as fictional (inherited from an early, pre-`pack.rs`
    // spec guess, never actually needed since base-patch verification only ever uses
    // the 8-byte hash above, and never functionally populated since BasePatch write/read
    // support doesn't exist). Everything from 0x242C to the end of the 16 KiB
    // front-matter is unstructured reserved space, always zeroed.

    /// <summary>
    /// The unit the outer seek table addresses - what the format's ASCII diagram calls
    /// "frame 0, frame 1, ...". 128 KiB, confirmed against the reference packer's `FRAME`
    /// constant. This is NOT the 8 KiB unit - see <see cref="BlockSize"/>.
    ///
    /// This is only ever a *default*, not a fixed format-wide value: `pack.rs` hardcodes
    /// it with no override, but `ndztool.py`'s own `--frame-size` is a real per-pack
    /// choice, and nothing in the front-matter records whichever value was actually used
    /// - <see cref="Compression.NdzArchive"/> derives real frame boundaries from the seek
    /// table's own per-frame sizes, never from this constant, so it correctly opens a
    /// file packed with any frame size. See <see cref="Compression.NdzWriter"/>'s
    /// `frameSize` parameter.
    /// </summary>
    public const int FrameSize = 128 * 1024;

    /// <summary>
    /// The default inner unit each frame is subdivided into: 8 KiB, independently
    /// zstd-compressed with its own per-block compressed size (and, when
    /// <see cref="NdzFlags.Filters"/> is set - always, for this writer - a per-block
    /// compression-mode byte too). A frame's payload is privately structured as
    /// <c>[u32 csize × nblocks][u8 mode × nblocks if Filters][compressed block bytes,
    /// concatenated]</c> - this inner structure is invisible to the outer seek table,
    /// which only ever records whole-frame (csize, dsize). Confirmed against the
    /// reference packer's `BLOCK` constant and `ndztool.py`'s own default (its
    /// `--block-size` defaults to `8k`, though it - unlike `pack.rs` - allows other
    /// values up to <see cref="MaxBlockSize"/>). Also embedded (as log2) into the
    /// front-matter's flags field - see <see cref="NdzFlagsExtensions.GetBlockSizeLog2"/>.
    /// </summary>
    public const int BlockSize = 8192;

    /// <summary>
    /// Hardware ceiling, not a preference - the real target hardware (DSPico) decodes on
    /// the fly while the console waits on a cart read, and a block bigger than this takes
    /// too long to fetch AND decompress on a cache miss, freezing the console.
    ///
    /// **32 KiB, not 8 KiB** - raised 2026-09-06, relayed through the user directly from
    /// the format author: a firmware bug that capped real blocks at 8 KiB is now fixed,
    /// and 16/32 KiB blocks are confirmed to work on real hardware. The local copy of
    /// `reference/mena-patchbench/ndztool.py` still hardcodes its own
    /// `NDZ_MAX_BLOCK_SIZE = 8192` and refuses to *pack* anything bigger (only in
    /// `cmd_pack`'s own CLI validation, not the decode path) - that script predates the
    /// fix and is now stale on this one point specifically, not evidence the fix is
    /// wrong. Nothing on the decode side needed to change: block size was already read
    /// per-file from the front-matter's own log2 subfield
    /// (<see cref="NdzFlagsExtensions.GetBlockSizeLog2"/>), never hardcoded past 8 KiB -
    /// only this write-side ceiling (here and `NdzWriter`'s own check) was actually
    /// wrong. <see cref="BlockSize"/>'s own default (8 KiB) is unchanged - the new sizes
    /// are an option to opt into (see <see cref="Compression.BlockSizeAnalyzer"/> for
    /// picking one), not a new default.
    /// </summary>
    public const int MaxBlockSize = 32 * 1024;

    /// <summary>
    /// The block sizes the CLI's own `--block-size` menu offers (and all
    /// <see cref="Compression.BlockSizeAnalyzer"/> ever compares) - the original 8 KiB
    /// default plus the two newly-allowed sizes, deliberately curated down from "any
    /// power of two up to <see cref="MaxBlockSize"/>" per the user: a smaller, fixed menu
    /// avoids ever risking a non-power-of-two mistake at the interaction surface, and
    /// these three are the ones actually worth trading ratio for real-hardware read
    /// granularity - nothing in between or below meaningfully helps. This is a curated
    /// CLI-level choice, not a format or `NdzWriter` restriction: the writer itself still
    /// accepts any power of two up to <see cref="MaxBlockSize"/> (needed for
    /// reference-compatibility testing - e.g. `ndztool.py`'s own 4 KiB default, see
    /// `NonDefaultBlockSizeTests`), this list only narrows what a person picks from.
    /// </summary>
    public static readonly IReadOnlyList<int> SupportedBlockSizes = new[] { BlockSize, 16 * 1024, 32 * 1024 };

    /// <summary>
    /// Hardware ceiling, not a preference: confirmed via `ndztool.py`'s own
    /// `NDZ_MAX_LEVEL` and its doc comment - decompression above this zstd level is too
    /// slow for the DSPico to keep up with a cart read. Same refuse-rather-than-ship-a-
    /// broken-file reasoning as <see cref="MaxBlockSize"/>.
    ///
    /// This is not a comfortable margin - confirmed directly by the format author
    /// (2026-09-01, relayed through the user): zstd level 19 decompresses in ~300us,
    /// against a DS hardware timeout of ~330us - about a 10% margin, not a generous one
    /// ("I've been teeter-tottering on the edge because I love living recklessly," her
    /// own words). lz4/lz4hc decompress in ~30us by contrast, a much wider margin, at
    /// the cost of a worse compression ratio - not currently implemented here (or in
    /// either confirmed reference's own packer; see <see cref="Compression.NdzWriter"/>'s
    /// class remarks for what's confirmed vs. not about that).
    /// </summary>
    public const int MaxLevel = 19;

    /// <summary>
    /// u32 magic "NDZP", little-endian bytes 'N','D','Z','P' - the pair container's own
    /// magic, distinct from <see cref="Magic"/> ('NDZ1') precisely so `info`/`unpack` can
    /// tell a pair container from a single .ndz apart from byte 0 alone. Confirmed
    /// against `ndztool.py`'s `NDZ_PAIR_MAGIC`. See <see cref="Compression.NdzPairContainer"/>.
    /// </summary>
    public const uint PairMagic = 0x505A444E;

    /// <summary>
    /// Size in bytes of one pair-container entry record: `[u32 offset][u32 size]
    /// [u32 origSize][4-byte gameCode]`. Confirmed against `ndztool.py`'s own
    /// `struct.unpack_from("&lt;III4s", ...)`.
    /// </summary>
    public const int PairEntrySize = 16;

    /// <summary>Offset of the pair container's first entry record - right after its 16-byte header.</summary>
    public const int PairEntriesOffset = 0x10;

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
