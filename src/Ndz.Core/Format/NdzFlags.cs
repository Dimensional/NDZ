namespace Ndz.Core.Format;

/// <summary>
/// The front-matter's u32 flags field (offset 0x000C) - a composite bitfield, not just
/// booleans. Confirmed against the format author's reference packer (`pack.rs`) and,
/// since 2026-08-31, directly against `ndztool.py`'s own constants
/// (`NDZ_FLAG_V2_HIERARCHICAL`/`NDZ_FLAG_ZSTD_BLOCKS`/`NDZ_FLAG_DICT`/`NDZ_FLAG_FILTERS`/
/// `NDZ_FLAG_BASE`/`NDZ_FLAG_RAWDICT`): bits 0/1/2/3/5 are named boolean flags, bit 4 is
/// the base-ROM/patch flag, and bits 8+ hold an embedded numeric subfield (the block
/// size, as log2) rather than another boolean - see <see cref="NdzFlagsExtensions"/>.
/// Bits 6-7 are still never set by either reference and remain reserved/unknown. Do not
/// repurpose any undefined bit speculatively.
/// </summary>
[Flags]
public enum NdzFlags : uint
{
    None = 0,

    /// <summary>
    /// Bit 0. Set by the reference packer on every file it writes.
    /// `ndztool.py` names this bit `NDZ_FLAG_V2_HIERARCHICAL` - confirmed 2026-08-31 to
    /// mean the frame/block hierarchical layout this whole format describes, as opposed
    /// to a legacy flat layout `ndztool.py` can still read (`decode_ndz_blob`'s
    /// non-hierarchical branch) but this port never produces or expects.
    /// </summary>
    V2 = 1u << 0,

    /// <summary>Bit 1. The block codec is ZStd (`ndztool.py`'s `NDZ_FLAG_ZSTD_BLOCKS`) - the only codec this port implements; the format also allows an lz4 flat-layout variant this port never produces or reads.</summary>
    ZStd = 1u << 1,

    /// <summary>
    /// Bit 2. `ndztool.py`'s `NDZ_FLAG_DICT` ("trained dict, retired") - confirmed
    /// 2026-08-31, not reserved/unknown as previously documented here. Wraps the
    /// dictionary section in python-zstandard's `ZstdCompressionDict` (a *trained*
    /// dictionary, with its own dictID/entropy tables) rather than loading it as raw
    /// content - a fundamentally different section format from <see cref="RawDictionary"/>.
    /// Confirmed retired: `ndztool.py`'s own packer (`pack_ndz_blob`) never actually
    /// produces this bit - the parameter that would enable it is always `None`. Not
    /// implemented here (no trained-dict path exists in this project) and not
    /// worth implementing unless a real file using it ever surfaces -
    /// <see cref="NdzFrontMatter.Read"/> throws if it's set, the same as
    /// <see cref="BasePatch"/>, rather than silently misreading the section as raw content.
    /// </summary>
    TrainedDictionary = 1u << 2,

    /// <summary>
    /// Bit 3. Confirmed 2026-08-31 (both `pack.rs` and `ndztool.py`) to NOT be about
    /// filter transforms specifically: this bit gates whether a per-block mode array
    /// exists in each frame at all. Without it, every block in a frame is uniformly
    /// <see cref="BlockMode.Dict"/> (if a dictionary is present) or
    /// <see cref="BlockMode.Plain"/> (if not) - no per-block byte to read; with it, one
    /// mode byte per block follows the csize array, naming which of the 7
    /// <see cref="BlockMode"/> values that block used. `NdzWriter` always writes a mode
    /// array (even when every block ends up Plain/Dict, no real filter transform
    /// chosen), so it always sets this bit too - matching `pack.rs`'s own unconditional
    /// choice, and `ndztool.py`'s default (`--no-filters` is opt-out, not opt-in). The
    /// five actual byte-transform filter modes (<see cref="BlockMode.Delta1"/> etc.)
    /// this bit's name refers to are not implemented here - see <see cref="BlockMode"/>.
    /// </summary>
    Filters = 1u << 3,

    /// <summary>
    /// Bit 4: this file's frames encode a patch against a base .nds (see the
    /// BaseOriginalSize/BaseGameCode/BaseHeaderHash front-matter fields) rather than the
    /// ROM's own bytes. Not implemented - see NdzFrontMatter.Read's remarks.
    /// </summary>
    BasePatch = 1u << 4,

    /// <summary>
    /// Bit 5. A raw (untrained, self-referential - extracted from the ROM's own
    /// repeated content) dictionary section follows the front-matter. Implemented here -
    /// see <see cref="NdzDictionary"/>. `ndztool.py`'s `NDZ_FLAG_RAWDICT`; its own packer
    /// always sets <see cref="Filters"/> alongside this bit too (matched by `NdzWriter`,
    /// which sets `Filters` unconditionally regardless).
    /// </summary>
    RawDictionary = 1u << 5,

    // Bits 6-7: never set by either reference implementation - reserved/unknown.
}

/// <summary>
/// Accessors for the numeric block-size subfield packed into <see cref="NdzFlags"/>
/// bits 8+ (as log2 of the block size), alongside the boolean flags in bits 0-7.
/// </summary>
public static class NdzFlagsExtensions
{
    private const int BlockSizeLog2Shift = 8;

    // Confirmed exactly 8 bits (not a guess): ndztool.py's own describe_flags computes
    // this identically, `(flags >> 8) & 0xFF`.
    private const uint BlockSizeLog2Mask = 0xFFu << BlockSizeLog2Shift;

    public static int GetBlockSizeLog2(this NdzFlags flags) =>
        (int)(((uint)flags & BlockSizeLog2Mask) >> BlockSizeLog2Shift);

    /// <summary>
    /// Confirmed against both `ndzunpack.py`'s and `ndztool.py`'s own decode logic
    /// (`4096 if block_log2 == 0 else (1 &lt;&lt; block_log2)`, identical in both): a
    /// subfield value of exactly 0 is a sentinel meaning "unspecified - default to
    /// 4096", not "1 &lt;&lt; 0 = 1 byte". `pack.rs`'s own fixed 8192-byte blocks always
    /// explicitly encode log2=13, so this never mattered for anything <c>NdzWriter</c>
    /// has produced - it matters for reading a real file that leaves the subfield at its
    /// default.
    /// </summary>
    public static int GetBlockSize(this NdzFlags flags)
    {
        int log2 = flags.GetBlockSizeLog2();
        return log2 == 0 ? 4096 : 1 << log2;
    }

    public static NdzFlags WithBlockSize(this NdzFlags flags, int blockSize)
    {
        int log2 = System.Numerics.BitOperations.Log2((uint)blockSize);
        return (NdzFlags)(((uint)flags & ~BlockSizeLog2Mask) | ((uint)log2 << BlockSizeLog2Shift));
    }
}
