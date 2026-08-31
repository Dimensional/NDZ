namespace Ndz.Core.Format;

/// <summary>
/// The per-block compression-mode byte stored inside each frame's payload (one byte
/// per block - see <see cref="NdzConstants.BlockSize"/>). Selection is brute force:
/// every full block is compressed plain, with the dictionary (if any), and once per
/// filter, and whichever comes out smallest wins - per <c>compress_block_best</c>,
/// which a screenshot explanation from Mena's own Claude session (relayed through the
/// user, 2026-08-31) cites as living in `patchbench.py:488`. <b>We do not have
/// `patchbench.py` itself</b> - only that screenshot's transcription
/// (reference/mena-patchbench/filter-modes-explanation.md) and a companion Python
/// unpacker, `ndzunpack.py`, which imports `patchbench` but doesn't include it. All 7
/// mode values are named/confirmed from those two secondhand sources, not from reading
/// the format's real decoder directly:
///
/// - <see cref="Dict"/> = 0, <see cref="Plain"/> = 1: also independently confirmed
///   empirically beforehand (2026-08-25) against the real deployed
///   `https://pheeeeenom.github.io/ndz-studio/` WASM build - see git history/memory for
///   that methodology. Consistent with the naming in the 2026-08-31 explanation.
/// - <see cref="Delta1"/>/<see cref="Delta2"/>/<see cref="Delta4"/> = 2/3/4:
///   <c>out[i] -= out[i - stride]</c> for stride 1, 2, or 4 - turns a slowly-varying
///   sequence (e.g. coordinates, pointers) into a run of near-zeros, which compresses
///   much better.
/// - <see cref="Shuffle2"/>/<see cref="Shuffle4"/> = 5/6: de-interleave the block into 2
///   or 4 byte planes (all byte 0s together, then all byte 1s, etc. - <c>b[k::s]</c> in
///   Python slice terms). Groups the (often near-identical) high bytes of a 16/32-bit
///   array together, separated from the noisier low bytes.
///
/// Filters only ever apply to <b>full</b> blocks whose length is itself a power of two
/// (so the shuffle planes divide evenly) - a frame's final, possibly-short trailing
/// block is always <see cref="Plain"/> or <see cref="Dict"/>, never a filter mode.
///
/// <b>Still not implemented here</b> - only the enum values/meanings are confirmed so
/// far (and only from the secondhand sources above, not real `patchbench.py` source),
/// not the encode/decode transform logic itself, and not the underlying compression
/// codec the real format decoder uses (may not be zstd/GrindCore's exact block API -
/// not yet cross-checked, we have no way to check it directly).
/// <see cref="Compression.NdzArchive"/> still fails loudly with the raw mode byte on
/// anything other than <see cref="Plain"/> or <see cref="Dict"/>, rather than assuming
/// an unknown mode behaves like either.
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
