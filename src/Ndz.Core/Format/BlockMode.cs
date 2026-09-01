namespace Ndz.Core.Format;

/// <summary>
/// The per-block compression-mode byte stored inside each frame's payload (one byte per
/// block, only present at all when <see cref="NdzFlags.Filters"/> is set - see that
/// flag's remarks). Selection is brute force, per `ndztool.py`'s own
/// `compress_block_best`: every block is compressed plain and, if a dictionary is
/// active, dictionary-primed too; a <b>full</b> block (exactly the configured block
/// size, <c>len(blk) == block_dsize</c> - not merely "some power-of-two length", so a
/// frame's final, possibly-short trailing block never qualifies even if its length
/// happens to be a power of two) is additionally tried through each of the five filter
/// transforms below; whichever result comes out smallest wins. All 7 mode values are
/// directly confirmed from `ndztool.py`'s own source
/// (`reference/mena-patchbench/ndztool.py` - `NDZ_MODE_*`/`MODE_NAMES`/`_FILTER_FWD`/
/// `_FILTER_INV`/`compress_block_best`), not secondhand - a screenshot explanation from
/// Mena's own Claude session (2026-08-31, transcribed to
/// `reference/mena-patchbench/filter-modes-explanation.md`) agrees with it, and was the
/// first source for the mode names, but `ndztool.py`'s actual code is what's now cited
/// below:
///
/// - <see cref="Dict"/> = 0, <see cref="Plain"/> = 1: also independently confirmed
///   empirically beforehand (2026-08-25) against the real deployed
///   `https://pheeeeenom.github.io/ndz-studio/` WASM build - see git history/memory for
///   that methodology.
/// - <see cref="Delta1"/>/<see cref="Delta2"/>/<see cref="Delta4"/> = 2/3/4:
///   `_delta_fwd`/`_delta_inv` - <c>out[i] -= out[i - stride]</c> (mod 256) for stride
///   1, 2, or 4, applied back-to-front so each subtraction reads the still-original
///   predecessor - turns a slowly-varying sequence (e.g. coordinates, pointers) into a
///   run of near-zeros, which compresses much better.
/// - <see cref="Shuffle2"/>/<see cref="Shuffle4"/> = 5/6: `_shuffle_fwd`/`_shuffle_inv` -
///   de-interleave the block into 2 or 4 byte planes (all byte 0s together, then all
///   byte 1s, etc. - <c>b[k::s]</c> in Python slice terms). Groups the (often
///   near-identical) high bytes of a 16/32-bit array together, separated from the
///   noisier low bytes.
///
/// Both filter families are applied *before* compression and undone *after*
/// decompression - the underlying codec is the same plain zstd compressor used for
/// <see cref="Plain"/> (`plain_cctx.compress(fwd(blk))` in `ndztool.py`), just fed
/// filtered bytes; there is no separate codec to cross-check.
///
/// <b>Still not implemented here</b> - the enum values/meanings and the transform
/// algorithms themselves are now fully confirmed (above), but the encode/decode
/// transform logic isn't coded yet. <see cref="Compression.NdzArchive"/> still fails
/// loudly with the raw mode byte on anything other than <see cref="Plain"/> or
/// <see cref="Dict"/>, rather than assuming an unknown mode behaves like either.
/// </summary>
public enum BlockMode : byte
{
    Dict = 0,
    Plain = 1,
    Delta1 = 2,
    Delta2 = 3,
    Delta4 = 4,
    Shuffle2 = 5,
    Shuffle4 = 6,
}
