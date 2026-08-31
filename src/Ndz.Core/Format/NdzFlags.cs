namespace Ndz.Core.Format;

/// <summary>
/// The front-matter's u32 flags field (offset 0x000C) - a composite bitfield, not just
/// booleans. Confirmed against the format author's reference packer (`pack.rs`): bits
/// 0/1/3/5 are named boolean flags, bit 4 is the previously-known base-ROM/patch flag,
/// and bits 8+ hold an embedded numeric subfield (the block size, as log2) rather than
/// another boolean - see <see cref="NdzFlagsExtensions"/>. Bits 2, 6, and 7 are never
/// set by the reference packer - reserved/unknown, not necessarily meaningless to the
/// format overall. Do not repurpose any undefined bit speculatively.
/// </summary>
[Flags]
public enum NdzFlags : uint
{
    None = 0,

    /// <summary>Bit 0. Set by the reference packer on every file it writes; exact meaning ("format v2"?) not otherwise documented.</summary>
    V2 = 1u << 0,

    /// <summary>Bit 1. The block codec is ZStd.</summary>
    ZStd = 1u << 1,

    // Bit 2: never set by the reference packer - reserved/unknown.

    /// <summary>
    /// Bit 3. This file may contain blocks compressed via one of the byte-transform
    /// "filter" modes, in addition to plain/dictionary blocks. Not implemented here -
    /// the transforms themselves (`crate::filters` in the reference packer) haven't
    /// been made available to this port yet.
    /// </summary>
    Filters = 1u << 3,

    /// <summary>
    /// Bit 4: this file's frames encode a patch against a base .nds (see the
    /// BaseOriginalSize/BaseGameCode/BaseHeader front-matter fields) rather than the
    /// ROM's own bytes. Not implemented - see NdzFrontMatter.Read's remarks.
    /// </summary>
    BasePatch = 1u << 4,

    /// <summary>
    /// Bit 5. A raw (untrained, self-referential - extracted from the ROM's own
    /// repeated content) dictionary section follows the front-matter. Not implemented
    /// here - see <see cref="NdzDictionary"/>.
    /// </summary>
    RawDictionary = 1u << 5,

    // Bits 6-7: never set by the reference packer - reserved/unknown.
}

/// <summary>
/// Accessors for the numeric block-size subfield packed into <see cref="NdzFlags"/>
/// bits 8+ (as log2 of the block size), alongside the boolean flags in bits 0-7.
/// </summary>
public static class NdzFlagsExtensions
{
    private const int BlockSizeLog2Shift = 8;

    // The exact field width beyond "starts at bit 8" isn't confirmed by the one
    // reference value observed (log2(8192) = 13). An 8-bit window is a generous,
    // conservative guess that doesn't collide with any bit below 8.
    private const uint BlockSizeLog2Mask = 0xFFu << BlockSizeLog2Shift;

    public static int GetBlockSizeLog2(this NdzFlags flags) =>
        (int)(((uint)flags & BlockSizeLog2Mask) >> BlockSizeLog2Shift);

    /// <summary>
    /// Confirmed against `ndzunpack.py`'s own decode logic (reference/mena-patchbench -
    /// we have that script itself, not the `patchbench.py` module it imports and defers
    /// the real format definition to): a subfield value of exactly 0 is a sentinel
    /// meaning "unspecified - default to 4096", not "1 &lt;&lt; 0 = 1 byte". `pack.rs`'s
    /// own fixed 8192-byte blocks always explicitly encode log2=13, so this never
    /// mattered for anything <c>NdzWriter</c> has produced - it matters for reading a
    /// real file that leaves the subfield at its default.
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
