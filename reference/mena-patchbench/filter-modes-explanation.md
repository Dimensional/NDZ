# Filter modes — explanation from Mena's Claude session

Transcribed verbatim from a screenshot the user shared 2026-08-31 (that session's own
explanation of `patchbench.py`'s filter modes, alongside the `ndzunpack.py` script kept
in this same folder). Not our own analysis — kept as a direct source, same spirit as
`pack.rs` being kept verbatim rather than paraphrased.

---

Filters are per-block reversible byte transforms applied *before* compression. They
don't shrink anything themselves — they rearrange bytes so LZ4/zstd finds more
redundancy. Each 4 KB block gets its own mode, stored as one `u8` in the frame index
(`patchbench.py:144`):

| mode | what it does |
|---|---|
| 0 `DICT` | zstd + shared dict, no filter |
| 1 `PLAIN` | zstd, no dict, no filter |
| 2/3/4 `DELTA1/2/4` | `out[i] -= out[i-s]`, stride 1, 2 or 4 |
| 5/6 `SHUFFLE2/4` | de-interleave into 2 or 4 byte planes |

Why those two families: DS ROM data is full of arrays of 16- and 32-bit values —
coordinates, pointers, audio samples. **Delta** turns a slowly-varying sequence into a
run of near-zeros. **Shuffle** groups all the byte-0s together, then all the byte-1s
(`b[k::s]` — `patchbench.py:167`), so the high bytes of a pointer array, which are often
identical, get separated from the noisy low bytes. Both hand the compressor much longer
matches.

**Selection is brute force.** `compress_block_best` (`patchbench.py:488`) compresses the
block plain, with the dict, and once per filter, then keeps whichever came out smallest.
Filters only apply to *full* blocks — the length must be a power of two so the shuffle
planes divide evenly, so the final short block of a frame is always mode 0 or 1.

**Format: flag bit 3 `NDZ_FLAG_FILTERS`.** When set, each frame's index (`nBlocks x
u32` csizes) is followed by `nBlocks x u8` modes, which the firmware pulls as a second
small read on frame change (`ntrCardRomNdz.cpp:592`).

**The clever bit on the decode side:** delta and shuffle each collapse to one parametric
inverse, so the firmware needs only two routines total for five modes
(`ntrCardRomNdz.cpp:512`). Delta is undone in place; shuffle borrows `sCBuf` as scratch,
which is safe because that block's compressed payload has already been consumed by the
time the inverse runs.

Cost is one byte per block plus a single pass; gain was ~3.5% shipped on its own.

---

## Notes on cross-checking this against `Ndz.Core`

- **"Each 4 KB block"**: this session's example used a 4 KiB block size, which lines up
  with `patchbench.py`'s own `block_log2 == 0 -> 4096` default sentinel (see the
  `ndzunpack.py` README) — but `pack.rs`'s reference packer always emits fixed 8 KiB
  blocks (`pub const BLOCK: usize = 8192`), which is what `NdzConstants.BlockSize` and
  the rest of `Ndz.Core` are built around. Both are apparently valid, format-legal block
  sizes (the log2 subfield is per-file, not fixed) — "4 KB" here is not evidence our own
  8 KiB choice is wrong, just a reminder the block size is a real per-file variable we
  should not hardcode assumptions about beyond what `NdzFlagsExtensions` already reads
  from the flags.
- **Mode values 0/1/2/3/4/5/6** match exactly what empirical WASM probing had already
  independently found (`Dict=0`, `Plain=1`, five more values 2-6) — see
  [[ndz-grindcore-dictionary-blocker]] and `BlockMode`'s own doc comment. This session's
  explanation is the first source that names what 2-6 actually *do*, not just that they
  exist.
- **Not yet implemented in `Ndz.Core`**: the delta/shuffle transform logic itself. We
  now have the algorithm (`out[i] -= out[i-s]` / byte-plane de-interleave), but haven't
  written or tested an implementation against it yet.
