# mena-patchbench

Shared by the user 2026-08-31, relayed from Mena Azer (mena@phenommod.com, the NDZ
format's author), built by Mena's own Claude session against her real internal
`patchbench.py` module - real artifacts from her, after
[`reference/mena-packer/pack.rs`](../mena-packer/pack.rs).

- **`ndztool.py`** (arrived later the same day) - a complete, **self-contained**
  pack + unpack tool. No local imports; `pip install -r requirements.txt`
  (`zstandard`, optionally `lz4`) and it runs standalone. Its own docstring: "The format
  logic is duplicated from `patchbench.py`, which remains the reference implementation."
  This is by far the strongest reference material available for this port - real,
  runnable code covering both encode and decode, including base-patch and the pair
  container, not just a description of them. Kept as one of the two confirmed sources
  for this project, alongside `pack.rs`.
- **`filter-modes-explanation.md`** - a transcription of the same Claude session's own
  explanation of the filter modes (a screenshot the user shared), including citations
  into `patchbench.py` and a previously-unknown C++ firmware decoder,
  `ntrCardRomNdz.cpp` - neither file itself is available to us. Kept as corroborating
  (but secondhand, screenshot-derived) material; `ndztool.py`'s own code is the primary
  citation wherever both agree.
- **`ndzunpack.py`** (arrived first; decode-only, `import patchbench as pb` at the top
  pulled in the actual module, which we don't have) was **removed 2026-08-31** as
  redundant once `ndztool.py` arrived and independently confirmed everything it had
  shown - see git history if it's ever needed again.

**We still do not have `patchbench.py` itself** - both scripts are explicit in their own
docstrings that they duplicate its logic rather than being that module. Everything below
is confirmed from these two scripts' own code (real, readable, testable) plus the
screenshot explanation (secondhand, Mena's Claude describing patchbench.py, not the
source itself) - never overstate this as "confirmed against patchbench.py."

## Methodology: don't trust a secondhand script at face value

Per the user's own explicit instinct ("Mena may not have fully audited their own python
scripts") - cross-checked `ndztool.py` line-by-line against `pack.rs` (the
original, most battle-tested reference) rather than accepting it alone, and verified the
critical finding empirically with a real venv, real ROMs, and real round-trips in both
directions - not just from reading code. That process:

- **Confirmed correct, strengthened**: the `Filters`-flag/mode-array conformance issue
  below - `pack.rs` independently confirms it with its own unconditional
  `flags |= FLAG_FILTERS; // ndz_studio always packs with filters on`, and empirical
  testing (see below) proved the bug and then the fix in both directions.
- **Found one real discrepancy between `pack.rs` and `ndztool.py`**: `pack.rs`'s `tune()`
  applies `WindowLog`/`ContentSizeFlag(false)`/`ChecksumFlag(false)`/`DictIdFlag(false)`
  to every compressor it builds; `ndztool.py`'s actually-live raw-dict and base-window
  compressors (`make_rawdict_cctx`, `BaseCtx.compress_with_window`) skip all four -
  `ndztool.py`'s equivalent (`_make_min_params`) only gets applied to the plain path and
  a retired, dead trained-dict path. Tested empirically (real venv, dictionaries up to
  64 MiB): **the missing `WindowLog` override doesn't matter in practice** - no
  ceiling found, unlike the real 8 MiB ceiling this exact method found in GrindCore (see
  [[ndz-grindcore-dictionary-blocker]] in NDZ's own memory) - python-zstandard evidently
  sizes its window correctly on its own. The missing `ContentSizeFlag`/`ChecksumFlag`/
  `DictIdFlag` suppression is real but harmless: optional frame metadata bytes, safely
  ignorable by any decoder, same character as GrindCore's own already-known
  `ContentSizeFlag` deviation.

## What this confirms, cross-checked against `Ndz.Core`

- **The `block_log2 == 0` sentinel**: `4096 if block_log2 == 0 else (1 << block_log2)` -
  a literal-zero block-size subfield means "default to 4 KiB", not "1 byte". Fixed in
  `NdzFlagsExtensions.GetBlockSize` 2026-08-31 (was previously computing `1 << 0 = 1`, a
  bug our own writer never triggered since it always encodes `log2(8192)=13`, but a real
  reader-side bug for any file using the implicit default). Also found `NdzArchive` was
  hardcoding `NdzConstants.BlockSize` at every block-boundary computation instead of
  reading it from the file's own flags - fixed the same day, `NdzWriter.Compress` also
  gained an optional `blockSize` parameter.
- **Flag names/bit values match what we'd already confirmed**: `V2` (0), `ZStd` (1),
  `Filters` (3), `BasePatch` (4), `RawDictionary` (5) - no contradictions on any bit we
  already had.
- **Filter transform algorithms**: delta (`out[i] -= out[i-stride]`, stride 1/2/4) and
  shuffle (de-interleave into 2/4 byte planes) - `ndztool.py`'s own `_delta_fwd`/
  `_delta_inv`/`_shuffle_fwd`/`_shuffle_inv`. Not yet implemented in `Ndz.Core`.

## What this adds - genuinely new

- **`NDZ_FLAG_DICT` = bit 2, confirmed, and confirmed RETIRED**: `ndztool.py`'s own
  source states it plainly (`NDZ_FLAG_DICT = 1 << 2   # trained dict, retired`), and its
  packer (`pack_ndz_blob`) never actually produces it - the `dict_obj` parameter is
  always `None`. Not worth implementing unless a real file using it ever surfaces.
- **Pair-container format, encode side now fully specified** (`cmd_pack`'s
  `--pair-out`): `NDZ_PAIR_MAGIC = 0x505A444E` ('NDZP'), header
  `[magic][hdrSize=16384][nRoms][reserved]`, then per-entry
  `[offset][size][origSize][gameCode]`, entries 16 KiB-aligned with padding. Nothing in
  `Ndz.Core` models this yet.
- **Base-patch mechanism - not a diff algorithm, confirmed wrong guess**: this repo
  previously assumed bsdiff/VCDIFF-style binary diffing would be needed. The real
  mechanism (`BaseCtx`): grain-hash the base ROM (2048 B grain), find candidate 16 KiB
  windows in the base similar to each target block, dictionary-compress against
  whichever's smallest, record the winning window's offset per-block
  (`u32 baseOff[n]`, sentinel `0xFFFFFFFF`). Reuses the exact per-block
  dictionary-priming machinery `RawDictionary` already needs - no new diff algorithm to
  design. Base verification (size/game-code/BLAKE2b-8 header hash match) also confirmed.
  Not implemented yet in `Ndz.Core` - real, non-trivial work, but the algorithm is known.
- **The real raw-dict content-selection algorithm**: content-defined chunking (gear
  hash, ~4 KB average chunk size) then rank chunks by dedup value `(count-1)*length`,
  pack the best up to the target size (`build_dup_weighted_dict`/`_cdc_chunks`). Our own
  corpus-test "first N bytes of ROM" stand-in (see NDZ's `ndz-grindcore-dictionary-blocker`
  memory) was a much cruder approximation of this.
- **A CRITICAL, confirmed, and now-FIXED writer bug in `Ndz.Core` itself**, found while
  cross-checking: a frame's per-block mode array only exists when `Filters` (bit 3) is
  set - without it, every block is uniformly `Dict`/`Plain`, no per-block byte at all.
  `NdzWriter` always wrote a mode array but never set `Filters` to match - every file it
  had ever produced falsely declared "no mode array" while containing one. Confirmed
  empirically (real venv, real ROM, both directions): pre-fix, `ndztool.py unpack` on our
  output failed with `ValueError: frame body size != frame csize`; post-fix,
  `ndztool.py unpack --verify` succeeds and the resulting file's sha256 matches the
  original ROM exactly. Fixed 2026-08-31 - `NdzWriter` now always sets `Filters`
  (matching `pack.rs`'s own unconditional choice, same reason), `NdzArchive` is now
  flag-aware on read. See `docs/ndz-format-spec.md`'s "Per-block compression mode".
- **A firmware/hardware decoder exists**: `filter-modes-explanation.md` cites
  `ntrCardRomNdz.cpp` - a C++ decoder, presumably for the actual DS-compatible cartridge
  hardware ("DSPico") this format targets. Not something we've seen referenced before;
  we have neither this file nor `patchbench.py` itself.

**Still needed from Mena, if available**: `patchbench.py` itself. Nothing algorithmic is
strictly needed from her for base-patch or the pair container anymore, now that the
mechanism is known - `ndztool.py` alone is sufficient to implement both, if/when that
becomes a priority.
