# NDZ format — spec and implementation status

Status: v1 implemented and round-trip tested (`src/Ndz.Core`, `src/Ndz.Cli`). Corrected
against the format author's own reference packer (a Rust implementation, `pack.rs`) on
2026-08-22 after an initial pass mis-modeled the frame/block hierarchy - see "Corrections
from the reference packer" below for exactly what changed and why. Compression,
random-access decompression, the raw-content dictionary (flags bit 5), all five per-block
filter modes, base-ROM patch mode (flags bit 4), and the pair-container format are all
implemented (2026-08-31, see "Filter modes"/"Base-ROM patch mode"/"Pair container format"
below) and cross-verified against `ndztool.py` in both directions - see
`docs/ndz-remaining-work.md` for the full build history. The only thing left unimplemented
is the retired trained-dictionary variant (flags bit 2), which nothing produces and isn't
worth building - see "Open questions".

## Container layout

```
┌──────────────────────────────┐
│ front-matter      16 KB      │  fixed size, cluster-aligned
├──────────────────────────────┤
│ dictionary        (optional) │  raw bytes, verbatim
├──────────────────────────────┤
│ frame 0                      │  ┐
│ frame 1                      │  ├ payload
│ ...                          │  ┘
├──────────────────────────────┤
│ seek table   8 B × nframes   │  (csize, dsize) per frame
│ nframes u32 + magic u32      │  trailer footer
└──────────────────────────────┘
```

- **Frame = 128 KiB** (`NdzConstants.FrameSize`). This is the unit the outer seek table
  addresses. Only the final frame may be shorter.
- **Each frame is internally subdivided into 8 KiB blocks** (`NdzConstants.BlockSize`) -
  this is a private structure inside a frame's own compressed bytes, invisible to the
  outer seek table. A frame's payload is laid out as:
  ```
  [u32 csize × nblocks]   per-block compressed size
  [u8  mode  × nblocks]   per-block compression mode (see BlockMode)
  [compressed block bytes, concatenated]
  ```
  Each block is compressed independently via `Nanook.GrindCore.ZStd.ZStdBlock` (wrapping
  `ZSTD_compressCCtx`/`ZSTD_decompressDCtx`) - a standalone ZStd frame, decodable on its
  own. This is what makes the format seekable: reading any byte range only requires
  decompressing the frame(s) - and within a frame, only the block(s) - it falls in. See
  `Ndz.Core.Compression.NdzArchive.ReadAt`.
- The trailer is written after all frames, and is read from the *end* of the file
  backwards: last 8 bytes = `nframes` (u32) + a magic (u32); the `nframes × 8` bytes
  before that are the outer (frame-level) seek table.

## Front-matter (16384 bytes)

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0x0000 | u32 | magic | `NDZ1` = `0x315A444E` |
| 0x0004 | u32 | frontMatterSize | always `16384` |
| 0x0008 | u32 | originalSize | raw `.nds` size |
| 0x000C | u32 | flags | composite bitfield - see "Flags" below |
| 0x0010 | 0x2400 | banner | `.nds` banner, copied from the source ROM's banner offset (header 0x68); real content size depends on the banner's own version field, zero-padded to the full 0x2400 slot - see "Banner sizing" |
| 0x2410 | u32 | gameCode | mirror of `.nds` header 0x0C |
| 0x2414 | u32 | dictionaryStoredSize | 0 if no dictionary section |
| 0x2418 | u32 | baseOriginalSize | bit 4 only |
| 0x241C | u32 | baseGameCode | bit 4 only |
| 0x2420 | u64 | baseHeaderHash | bit 4 only - BLAKE2b-8 hash of the base ROM's first 0x200 bytes, checked against the supplied base at decode time |
| 0x2428 | u32 | dictionaryDecompressedSize | 0 if no dictionary section |
| 0x242C–0x3FFF | — | reserved | zero |

**Corrected 2026-08-31** against `ndztool.py`'s own complete field-by-field layout
comment, which lists every defined field and ends at `0x2428` - `0x2420` was previously
documented here (wrongly) as unused/retired, and a fictional 0x200-byte `baseHeader`
field was previously documented at `0x2430` (inherited from an early, pre-`pack.rs` spec
guess that was never corrected, since `pack.rs` itself doesn't implement base-patch at
all). Neither error affected any file this project has produced or accepted, since
`BasePatch` support doesn't exist yet either direction - see `NdzConstants.cs`'s remarks
for the full correction.

Implementation: `Ndz.Core.Format.NdzFrontMatter` (read/write), `NdzConstants` (offsets),
`NdsRomInfo` (pulls gameCode/banner out of a raw `.nds`).

### Banner sizing

The reserved front-matter slot is a fixed 0x2400 bytes, but the *real* banner content
copied into it depends on the banner's own version field (a u16 at the banner offset
itself), confirmed against the reference packer:

| Version | Content size |
|---|---|
| < 2 | 0x840 |
| 2 | 0x940 |
| 3–0x102 | 0xA40 |
| ≥ 0x0103 | 0x23C0 |

Anything beyond the real content size (up to the full 0x2400 slot) is zero-padded, not
copied verbatim from the source ROM. See `NdzConstants.GetBannerContentSize`.

### Flags

`Ndz.Core.Format.NdzFlags` — a **composite bitfield**, confirmed against the reference
packer: it is not simply a set of independent boolean flags.

| Bit(s) | Meaning |
|---|---|
| 0 | `V2` — set on every file the reference packer writes; exact meaning ("format v2"?) not otherwise documented |
| 1 | `ZStd` — the block codec is ZStd |
| 2 | `TrainedDictionary` — a *trained* dictionary (python-zstandard `ZstdCompressionDict`), distinct from `RawDictionary`/bit 5 — confirmed 2026-08-31 (`ndztool.py`'s `NDZ_FLAG_DICT`), and confirmed **retired**: the real packer never actually produces it, only reads it if encountered. Not implemented here: `NdzFrontMatter.Read` throws `NotSupportedException` if it's set, the same as `BasePatch`, rather than risking a silent misread as raw content. |
| 3 | `Filters` — **not really about filter transforms specifically**: this bit gates whether a per-block mode array exists in each frame at all. Without it, every block in a frame is uniformly `Dict` (if a dictionary is present) or `Plain` (if not) — no per-block byte to read. `NdzWriter` always writes a mode array (even when every block is Plain/Dict, no real filter transform involved) and so must always set this bit — confirmed empirically 2026-08-31 by cross-testing against `ndztool.py`'s real decoder in both directions; see "Per-block compression mode" below. |
| 4 | `BasePatch` — this file's frames encode a patch against a base `.nds` (previously the only bit this spec documented) |
| 5 | `RawDictionary` — a raw, self-referential content-dictionary section follows the front-matter |
| 6–7 | never set by the reference packer — reserved/unknown |
| 8+ | **not a boolean** — the block size, packed as `log2(blockSize)`, with `0` itself a sentinel meaning "unspecified, default to 4096" rather than a literal `1 << 0 = 1` — confirmed against `ndztool.py`'s own decode logic (see "Reference materials"). See `NdzFlagsExtensions.GetBlockSizeLog2`/`GetBlockSize`/`WithBlockSize`. The field's width is confirmed exactly 8 bits (bits 8-15) by `ndztool.py`'s own `describe_flags`, which computes `(flags >> 8) & 0xFF`. |

Do not repurpose any undefined bit speculatively - reserved bits stay zero pending the
format author's confirmation of their meaning.

Block size itself is a real per-file variable, not something to assume: the reference
packer (`pack.rs`) always emits fixed 8 KiB blocks, but `ndztool.py`'s own default
(when the subfield is left at `0`) is 4 KiB — both are apparently valid, format-legal
choices. `Ndz.Core` defaults to 8 KiB (`NdzConstants.BlockSize`, matching what `pack.rs`
always produces), but block size is no longer hardcoded: `NdzArchive` reads it from the
file's own flags (fixed 2026-08-31 — it previously assumed the constant everywhere,
which would have misparsed any file using a different size), and `NdzWriter.Compress`
now takes an optional `blockSize` parameter to produce one. See
`NonDefaultBlockSizeTests.cs`.

**Bit 2 is read-but-rejected, not implemented** (retired, not worth it - see "Open
questions"): `NdzFrontMatter.Read` throws `NotSupportedException` if it's set, and
`NdzWriter` never sets it. **Bit 4 (`BasePatch`) is implemented** - see "Base-ROM patch
mode" below; `NdzFrontMatter.Read` no longer rejects it, `NdzArchive.Open`'s `baseRom`
parameter does the real (base-ROM-dependent) verification instead.

### Per-block compression mode (`BlockMode`)

Each block's mode byte selects how its bytes were compressed. All 7 values are named,
confirmed, and **implemented** (2026-08-31, `Ndz.Core.Format.BlockFilters`) - see
"Filter modes" under "Open questions" below for the transform algorithms and
`docs/ndz-remaining-work.md` for the write/read integration design. `NdzArchive` still
fails loudly - naming the exact frame index, block index, and raw mode byte - on a mode
byte outside the 7 defined values, or `Dict` with no dictionary section present, rather
than misinterpreting bytes it doesn't understand. Verified in both directions against a
real `ndztool.py` run on real byte content (not just synthetic fixtures): `NdzWriter`'s
filter-mode output decodes correctly via `ndztool.py unpack --verify` (sha256-identical
to the source), and `NdzArchive` correctly decodes a real `ndztool.py`-packed file whose
mode histogram actually used `plain`/`delta1`/`delta2`/`shuffle2` blocks.

**The mode array's existence is itself conditional on the `Filters` flag (bit 3) - a
real, confirmed, and now-fixed writer bug lived here.** A frame's per-block mode array
only exists when `Filters` is set; without it, every block in the frame is uniformly
`Dict` (if a dictionary is present) or `Plain` (if not) - no per-block byte at all, and
a real reader skips straight from the csize header into block data. `NdzWriter` always
writes a per-block mode array (even when every block ends up `Plain`/`Dict`, no real
filter transform chosen), but until 2026-08-31 never set `Filters` to match - meaning
every file `NdzWriter` had ever produced falsely declared "no mode array" while
containing one anyway.

Found by cross-checking `ndztool.py` against `pack.rs` (which sets `Filters`
unconditionally, with its own comment: `// ndz_studio always packs with filters on`),
and confirmed empirically with a real venv and a real ROM, in both directions:
- `ndztool.py unpack` on a file `NdzWriter` produced (pre-fix): `ValueError: frame body
  size != frame csize` - it correctly read our (missing) `Filters` bit, assumed the
  simple layout, and choked on our mode-array bytes sitting where it expected block data.
- `NdzArchive.Open` on a file `ndztool.py` produced: parsed the front-matter, seek table,
  and every frame header correctly, failing only at an actual, not-yet-implemented
  filter-mode block - exactly the already-known, deliberately-deferred gap, not a
  structural problem.

Fixed 2026-08-31: `NdzWriter` now always sets `Filters` (matching `pack.rs`'s own
unconditional choice, for the identical reason - it always writes a mode array).
`NdzArchive` is now flag-aware on read - it infers the uniform mode when `Filters` is
clear, instead of always assuming an array exists. Re-verified with the same real-venv,
real-ROM, both-directions test: `ndztool.py unpack --verify` on a fresh `NdzWriter`
output now succeeds, and the resulting file's sha256 matches the original ROM exactly.
See `NonDefaultBlockSizeTests.cs`-style coverage in `BlockModeTests.cs`
(`NoFiltersFlag_HasNoModeArray_InfersPlainUniformly`) for the regression test.

## Compression

`Ndz.Core.Compression.NdzWriter.Compress` — for each 128 KiB frame, splits it into up to
sixteen 8 KiB blocks by default (the last block of the last frame may be shorter; block
size is a `blockSize` parameter, any power of two up to the hardware ceiling of 32768 -
see "Hardware limits" below) and compresses each independently via `ZStdBlock` with
`CompressionOptions { Type = <level>, BlockSize = blockSize }`. If a dictionary was
supplied, every block is compressed *both* plain and
dictionary-primed (`InitProperties = dictionary.Content`), and whichever is smaller
wins - tagged `BlockMode.Plain` or `BlockMode.Dict` accordingly; with no dictionary,
every block is just `BlockMode.Plain`. Writes `[csizes][modes][compressed bytes]` as
that frame's payload, recording the *whole frame's* `(compressedSize, decompressedSize)`
for the outer seek table. Default level is `CompressionType.Level19` (near-max ZStd
effort) — worth it even at 8 KiB block size since it's search-effort, not window size,
and there's no cross-block history to lose regardless of level.

Frames are compressed in parallel (`Parallel.For` with one pair of `ZStdBlock`s - plain
and, if a dictionary is active, dictionary-primed - per worker thread, mirroring the
reference packer's own per-thread-group compressor), then written to the output stream
in order afterward - the resulting file is byte-identical
regardless of how many threads did the work.

`Ndz.Core.Compression.NdzArchive` is the reader: parses front-matter + outer seek table
once, precomputes cumulative compressed-byte offsets per frame, then serves
`ReadAt(offset, buffer)` by decompressing only the frame(s) touched - and within each
frame, parsing its private block header to decompress only the block(s) actually needed
- with a 1-frame cache for sequential reads. `DecompressAll()` is just `ReadAt(0,
wholeBuffer)`.

## Hardware limits

Confirmed 2026-08-31 via `ndztool.py`'s own `NDZ_MAX_LEVEL`/`NDZ_MAX_BLOCK_SIZE` and their
doc comment: the real target hardware (DSPico) decodes on the fly while the console
waits on a cart read, so two knobs are **hardware ceilings, not preferences** -

- compression level: max **19** (`NdzConstants.MaxLevel`) - higher decompresses too
  slowly for the console to keep up.
- block size: max **32768 bytes** (`NdzConstants.MaxBlockSize`) - a bigger block takes
  too long to fetch *and* decompress on a cache miss and the console freezes. **Raised
  2026-09-06** from an earlier 8192-byte ceiling, relayed through the user directly from
  the format author: a firmware bug that capped real blocks at 8 KiB is now fixed, and
  16/32 KiB blocks are confirmed to work on real hardware. The CLI's own `--block-size`
  menu is deliberately curated down to exactly three choices - 8/16/32 KiB
  (`NdzConstants.SupportedBlockSizes`), or `auto` to pick among them
  (`Ndz.Core.Compression.BlockSizeAnalyzer`) - not every power of two up to this ceiling,
  to keep the choice simple and always a safe one; `NdzWriter` itself still accepts any
  power of two up to the ceiling (needed for reference-compatibility testing, e.g.
  `ndztool.py`'s own 4 KiB default). The local `reference/mena-patchbench/ndztool.py`
  copy still hardcodes `NDZ_MAX_BLOCK_SIZE = 8192` and refuses to *pack* past it - that
  script predates the fix and is stale on this one point specifically, not evidence
  against it.

`ndztool.py` refuses to pack past either rather than produce a file that "packs fine and
then fails on real hardware" - `NdzWriter.Compress` throws `ArgumentOutOfRangeException`
for the same reason, and `Ndz.Cli`'s `compress` command validates both up front too (a
clean usage error instead of an exception bubbling out of the library). See
`NonDefaultBlockSizeTests.cs`'s `RejectsBlockSizeAboveTheHardwareLimit`/
`RejectsCompressionLevelAboveTheHardwareLimit`.

## Open questions (deferred, not guessed at)

### Dictionary support — implemented (2026-08-25), on GrindCore 0.9.0 (NuGet)

**Fully wired end-to-end.** GrindCore's ZStd wrapper gained one-shot dictionary-primed
block compression, both directions: `Nanook.GrindCore.ZStd.ZStdBlock` now reads
`CompressionOptions.InitProperties` as raw dictionary content and dispatches to
`CompressBlockWithDict`/`DecompressBlockWithDict` (native:
`SZ_ZStd_v1_5_7_CompressBlockWithDict`/`DecompressBlockWithDict` → `ZSTD_compress_usingCDict`/
`ZSTD_decompress_usingDDict`, and the same pair for v1.5.2). Landed first in GrindCore
0.8.1 (nuget.org, 2026-08-26); `src/Ndz.Core/Ndz.Core.csproj` now targets 0.9.0 (see
"Dictionary window sizing" below for why).

`NdzWriter.Compress`'s `rawDictionarySize` parameter (see
`Compression.RawDictionaryBuilder`, and "Dictionary input: size-derived, not
externally-supplied" below) drives it; `NdzArchive.Open` reads a file's dictionary
section and applies it regardless of how it was built. Per-block behavior matches the
reference packer's own architecture: every block is compressed both plain and
dictionary-primed, whichever is smaller wins, tagged `BlockMode.Plain` or
`BlockMode.Dict` accordingly (see `NdzWriter.CompressFrame`). The dictionary section is
stored verbatim/uncompressed (`DictionaryStoredSize == DictionaryDecompressedSize`
always, for this writer) - a file claiming otherwise is rejected as an unsupported
variant rather than misread.

The fix does **not** use `ZSTD_compress2()` - that was this doc's original guess at the
mechanism, made before anyone had built the fix, and it guessed wrong. The mechanism
that actually landed is `ZSTD_createCDict`/`ZSTD_compress_usingCDict` and
`ZSTD_createDDict`/`ZSTD_decompress_usingDDict` - a dedicated one-shot dictionary API
that sidesteps the `ZSTD_compressCCtx` sticky-parameter-reset problem entirely, rather
than working around it via `compress2()`. Both mechanisms are standard, valid zstd
dictionary paths and produce mutually-decompressible output; for a *raw* content
dictionary like the one this format uses (not a magic-numbered trained dictionary),
there's no dictID embedded either way, so there's no interop wrinkle from the API choice.

The format author reached out to Nanook (GrindCore.net's maintainer) about the original
blocker on 2026-08-22; Nanook landed the fix himself shortly after.

### Dictionary input: size-derived, not externally-supplied — corrected 2026-08-31

**A real, confirmed wrong turn, caught and fixed the same day.** From when dictionary
support first landed (2026-08-25/26) until this correction, `NdzWriter.Compress` took a
`dictionary: NdzDictionary?` parameter - arbitrary, externally-supplied dictionary bytes
from wherever a caller got them (the CLI's `--dict <file>` read them from a file). This
was never based on either reference implementation. Both `pack.rs` (`pack(nds: &[u8],
raw_dict_size: usize, ...)`) and `ndztool.py` (`--raw-dict <size>`, `pack_ndz_blob(...,
raw_dict_size=0, ...)`) only ever take a *target size* - the dictionary content itself is
always **derived from the ROM being packed**, via content-defined chunking + dedup-value
ranking (`dup_census`/`prefix_dict` in `pack.rs`, unavailable to us;
`build_dup_weighted_dict`/`_cdc_chunks` in `ndztool.py`, which we do have and this is
built against). Neither reference has ever had a way to load externally-supplied
dictionary content at pack time.

This wasn't a deliberate alternative design - it predates having either reference's real
signature confirmed (dictionary support was built from a black-box WASM probe, before
`pack.rs`'s `raw_dict_size: usize` was even read closely, and well before `ndztool.py`
arrived and named the actual algorithm). It just never got reconciled against either
once both were available, across three more features built on top of it. The *storage*
format was never wrong - a dictionary section is always embedded verbatim in the `.ndz`
regardless of how its content was chosen, and that was correct and cross-verified
throughout. Only the *input* mechanism was invented.

**Fixed**: `Ndz.Core.Format.NdzDictionary` removed. `NdzWriter.Compress`/`CompressFile`
now take `rawDictionarySize: int` instead of a dictionary parameter, deriving the
dictionary from `rom` itself via the new `Ndz.Core.Compression.RawDictionaryBuilder`
(content-defined chunking + dedup-weighted ranking, confirmed byte-for-byte against
`ndztool.py`'s `_cdc_chunks`/`build_dup_weighted_dict` run on identical input in an
isolated venv - not just round-trip tested, the actual chunk boundaries and selected
dictionary bytes are asserted equal). The CLI's `--dict <file>` became `--raw-dict
<size>` (e.g. `8m`, `512k`), matching `ndztool.py`'s own flag shape exactly. Verified
end-to-end against a real `ndztool.py --raw-dict` in both directions too: our derived-
dictionary output decodes via `ndztool.py unpack --verify` sha256-identical to the
source, a real `ndztool.py --raw-dict`-packed file decodes correctly through this CLI,
and - on the same real ROM at the same target size - both implementations independently
derived a dictionary of the exact same size (405,048 bytes), not just similar.

### Dictionary window sizing — fixed in GrindCore 0.9.0

Traced through zstd's own source (`clevels.h`'s size-tiered parameter tables,
`zstd_compress.c`'s `ZSTD_adjustCParams_internal`): `ZSTD_createCDict()`'s implicit
window sizing caps at **8 MiB for any dictionary over 256 KiB at compression level
19**, and never grows further no matter how much bigger the dictionary gets - the
adjustment logic only ever *shrinks* the window from the level's default, never grows
it. Content beyond that 8 MiB reach becomes unreachable as match material - a
compression-ratio-only issue (never a correctness one; nothing corrupts), but a real
one for large dictionaries. Both real ROMs tested before this was found (Super Mario 64
DS ≈430 KB dictionary, Golden Sun: Dark Dawn ≈6 MB) happened to stay under that ceiling,
so it went unnoticed until traced deliberately.

The format author (also a GrindCore.net contributor) and Nanook fixed this together in
GrindCore 0.9.0: `SZ_ZStd_v1_5_7_CreateCompressionDict`/`SZ_ZStd_v1_5_2_CreateCompressionDict`
take an additional `windowLog` parameter (0 = old default behavior; >0 routes through
`ZSTD_createCDict_advanced2` with an explicit `ZSTD_c_windowLog`, bypassing the cap),
exposed managed-side as `CompressionDictionaryOptions.WindowBits`. `NdzWriter`'s
private `ComputeDictionaryWindowBits` now sets this to cover the whole dictionary plus
one block, mirroring the reference packer's own `wlog(dict.len() + BLOCK).max(15)`
exactly. `DictionaryWindowSizingTests.cs` proves the override actually takes effect (not
just that the code compiles) by placing matching content deliberately past the old
8 MiB ceiling and confirming the dictionary can still reach it.

One implementation detail worth knowing if this code is touched again: setting
`WindowBits` also changes how `ZStdBlock.RequiredCompressOutputSize` is computed - it
switches from `BlockSize`-based to `1 << windowLog`-based, which would size the
per-block destination buffer in *megabytes* rather than ~8 KB if used directly.
`NdzWriter.CompressFrame` deliberately sizes both the plain and dictionary-primed
buffers from the plain block's `RequiredCompressOutputSize` to avoid that.

### `MODE_PLAIN`/`MODE_DICT` values — confirmed empirically against the real deployed reference tool

`BlockMode.Plain = 1` and `BlockMode.Dict = 0` - **not guesses**. Confirmed by running
the actual live reference tool (`https://pheeeeenom.github.io/ndz-studio/`'s
`ndzcore_bg.wasm`, unstripped Rust symbols) directly under Node, against both crafted
synthetic input and two real .nds ROMs (Super Mario 64 DS, Golden Sun: Dark Dawn - the
latter is a regular NDS title, not DSi-enhanced):

- Blocks filled with genuine random noise (which no filter or dictionary could ever
  improve on) consistently came back tagged mode 1, never mode 0 - logically decisive,
  not just correlative.
- Packing both real ROMs with a dictionary (sized via the tool's own `analyze()`
  recommendation) made mode 0 appear for the first time, in the thousands, with a
  corresponding drop in mode 1's share - strong correlational evidence, not a logical
  necessity proof like Plain's.
- Five other mode values were observed (2, 3, 4, 5, 6) - clearly filter transforms,
  present at similar counts whether or not a dictionary was active. `mode_hist: [u64;
  7]`'s seven slots are now fully accounted for: 1 plain + 1 dict + 5 filters.

Caveat, raised by the format author at the time: this was one specific deployed build of
one consumer (`ndz_studio`) of the underlying `ndzcore` library, not Mena's own source or
tool - treat as strong provisional evidence, not authoritative. **Update 2026-08-31**:
this empirical `Dict=0`/`Plain=1` finding, and the five filter values, are now also
independently consistent with a screenshot explanation from Mena's own Claude session
(relayed through the user, secondhand) and `ndzunpack.py`'s own code - see "Filter modes"
below. `ndztool.py` (which superseded `ndzunpack.py`) was extracted directly from the
real `patchbench.py` module by a Claude session with actual read access to it, not
reconstructed secondhand - see `reference/mena-patchbench/README.md`. No contradiction
found between any of these and the black-box WASM probing either.

### Filter modes — algorithm confirmed and implemented 2026-08-31

Confirmed real and in active use since 2026-08-25 (mode values 2-6 observed on real
ROMs, independent of dictionary use). On 2026-08-31 Mena shared, through the user, their
own Claude session's explanation of `patchbench.py`'s filter modes, then later the same
day a working self-contained pack+unpack tool (`ndztool.py`) built against it — see
`reference/mena-patchbench/` (`ndztool.py`, `filter-modes-explanation.md`, and that
folder's README for full provenance and cross-checking notes). All 7 mode
values are now named, confirmed in `ndztool.py`'s own source
(`_FILTER_FWD`/`_FILTER_INV`, `MODE_NAMES`):

| mode | name | transform |
|---|---|---|
| 0 | `Dict` | zstd + shared dict, no filter |
| 1 | `Plain` | zstd, no dict, no filter |
| 2/3/4 | `Delta1`/`Delta2`/`Delta4` | `out[i] -= out[i - stride]`, stride 1/2/4 |
| 5/6 | `Shuffle2`/`Shuffle4` | de-interleave into 2 or 4 byte planes |

Delta targets slowly-varying 16/32-bit sequences (coordinates, pointers, audio samples)
— turns them into runs of near-zeros. Shuffle groups all the byte-0s together, then all
the byte-1s, etc., separating a pointer array's often-identical high bytes from its
noisier low bytes. Both just hand the compressor longer matches; neither shrinks
anything by itself. Selection is brute force: every block is tried plain (and
dictionary-primed, if a dictionary is active); a block is additionally tried through
each filter only if it's a **full** block, i.e. exactly the configured block size
(`len(blk) == block_dsize` in `ndztool.py`, not merely "some power-of-two length" - a
frame's final, possibly-short trailing block never qualifies for a filter even if its
length happens to itself be a power of two) — whichever result compresses smallest wins.
Reported gain from filters alone: ~3.5%, for one extra stored byte per block.

In base-patch mode, a winning base-window match is tagged `Plain` (mode 1), not a
distinct mode value — `compress_block_best` records the win via the separate `baseOff`
array instead (see "Base-ROM patch mode" below), so the mode byte alone can't
distinguish a base-window hit from an ordinary plain block.

**Implemented 2026-08-31** (`Ndz.Core.Format.BlockFilters`, wired into
`NdzWriter.CompressFrame`'s candidate search and `NdzArchive.GetDecompressedFrame`'s
mode dispatch - see `docs/ndz-remaining-work.md` for the integration design). Verified
in both directions against a real `ndztool.py`: our filter-mode output round-trips
through `ndztool.py unpack --verify` sha256-identical to the source, and `NdzArchive`
correctly decodes a real `ndztool.py`-packed file whose mode histogram used
`plain`/`delta1`/`delta2`/`shuffle2` blocks in practice, not just `plain`. `NdzWriter`
gained an `enableFilters` parameter (default on) mirroring `ndztool.py`'s own
`--no-filters` opt-out.

### Dictionary flag split — `RawDictionary` (live) vs. `Dict` (retired)

`RawDictionary` (bit 5, `Ndz.Core.Compression.RawDictionaryBuilder`) is the only
dictionary mechanism `Ndz.Core` implements, matching `pack.rs` exactly - a raw content
dictionary, derived from the ROM itself and stored verbatim. There is a second, separate
flag: **`TrainedDictionary` = bit
2, confirmed 2026-08-31** from `ndztool.py`'s own source (`NDZ_FLAG_DICT = 1 << 2 #
trained dict, retired`) - wraps dictionary bytes in python-zstandard's
`ZstdCompressionDict` object instead of loading them raw. Confirmed **retired**:
`ndztool.py`'s own packer (`pack_ndz_blob`) never actually produces it - the parameter
that would enable it is always `None`. Not implemented here, and not worth implementing
unless a real file using it ever surfaces - the live path is `RawDictionary`.
`NdzFrontMatter.Read` throws `NotSupportedException` if it's ever encountered set,
rather than risk misreading a trained-dict blob as raw content on the (currently
impossible, since nothing produces this bit) chance its stored/decompressed sizes
happened to match.

**Multi-dictionary pair containers — confirmed NOT the format author's intended design,
2026-09-08.** Each pair-container entry (the self-contained base, and every base-patched
target) is independently `RawDictionary`-capable, since each is a fully independent
`.ndz` blob with its own front-matter - `NdzPairWriter` uses this to give the base and
each target their own separately-sized (or absent) dictionary (see "Pair container
format" below, and `Ndz.Cli`'s `--raw-dict auto`/repeatable `--raw-dict <size>` in pair
mode). The user asked Mena directly whether this was intended. Their answer: **no** -
multiple independent dictionaries within one pair container was not part of their own
design. They also said they **haven't worked on the multi-ROM "stacking" feature in a
while**, and have **not confirmed whether it actually misbehaves on real hardware** either
way - so this is a confirmed *design* divergence from the format author's intent, with
the real-hardware *functional* question still genuinely open, not resolved in either
direction. Do not treat "not intended" as "confirmed broken," and do not treat "not yet
confirmed broken" as "confirmed safe." Ask the user before changing this behavior, and
before treating either open question as settled.

**Pair-container creation temporarily disabled in both user-facing tools, 2026-09-09.**
At the user's own request, following the same conversation above: since the multi-ROM
dictionary design isn't confirmed, the CLI's `--pair-out` and the GUI's target-grouping
UI both now refuse to create a new pair container at all, gated by
`Ndz.Core.Compression.PairContainerPolicy.CreationEnabled` (currently `false`). This is a
policy flag checked only at those two entry points - `NdzPairWriter`/`NdzPairContainer`
themselves are completely untouched, still fully implemented and fully test-covered, and
reading/unpacking an *existing* pair container is not gated at all (Examine's own view,
`ndz info`/`unpack`/`verify`) - only *creating* a new one is blocked. Flip
`CreationEnabled` back to `true` once the format author confirms the real intended
design; nothing else needs to change.

### Pair container format — implemented 2026-08-31, generalized to N ROMs 2026-09-06

An outer container format wrapping two or more complete `.ndz` blobs side by side, used
to ship a base-patch family together with no external base file needed. Both read and
write sides are confirmed from `ndztool.py`'s `--pair-out`:
`NDZ_PAIR_MAGIC = 0x505A444E` ('NDZP'), header `[magic][hdrSize=16384][nRoms][reserved]`
(all u32) at offset 0, then per-entry `[offset][size][origSize][gameCode]` (u32 x3 +
4 bytes) at `0x10 + 0x10*i` - entries themselves are the base blob followed by one or
more patched blobs, each padded to a 16 KiB (front-matter-size) boundary. Entry 0 is
always self-contained; every other entry (each `BasePatch`-flagged) resolves its base to
that *same* entry 0 in the container, not an externally-supplied file and not each
other - a star topology, not a chain.

**Generalized from exactly 2 ROMs to N, 2026-09-06** (`NdzPairWriter`/commit `35c87ba`):
the on-disk format's own `nRoms` header field, and both `ndztool.py`'s own decode side
(`read_pair_entries`) and this project's read side (`NdzPairContainer`), were already
fully generic - only `ndztool.py`'s own `cmd_pack` CLI hardcodes exactly 2 (`n_roms=2`),
a limitation of its CLI, not the format. `NdzPairWriter.Write` now takes
`IReadOnlyList<byte[]> targetRoms` (one or more), packing the base once and every target
base-patched against it; the original 2-ROM signature is now a single-target convenience
overload. `Ndz.Cli`'s `compress --pair-out` accepts one or more positional targets. No
wire-format changes were needed. Validated on real ROMs (Mega Man Star Force: 4 and 6
regional/version releases packed together, cross-checked byte-identical against a real
`ndztool.py` on every entry) - see the `ndz-spec-provenance` memory for full numbers.

**Implemented** (`Ndz.Core.Format.NdzPairEntry`, `Ndz.Core.Compression.NdzPairWriter`/
`NdzPairContainer` - mirroring the `NdzWriter`/`NdzArchive` write/read split; see
`docs/ndz-remaining-work.md` for the design). `NdzPairContainer.TryRead`/`Read` detect a
pair container purely from its magic (distinct from a single `.ndz`'s), resolve which
entry is self-contained, and open either entry - a base-patched one transparently
decompresses the self-contained one first to resolve its base, matching `ndztool.py`'s
own `cmd_unpack` exactly. `NdzArchive.ReadInfo` is reused per-entry, so `info` never
needs to decode either side just to summarize it. The CLI's `compress` gained
`--pair-out` (needs `--base`), and `decompress`/`verify` gained `--index`.

Verified in both directions against a real `ndztool.py --pair-out` on real byte content:
our pair-container output's both entries decode via `ndztool.py unpack --index 0`/`1`
sha256-identical to their sources, and a real `ndztool.py --pair-out`-packed container's
both entries decode correctly through the CLI's `verify --index 0`/`1`.

### Base-ROM patch mode (flags bit 4) — implemented 2026-08-31

The bit-4 fields (`baseOriginalSize`, `baseGameCode`, `baseHeaderHash`) record metadata
*about* a base ROM, not its content bytes — the base ROM itself must be supplied
separately at both compress and decompress time, matching the motivating use case (e.g.
Pokémon Diamond stored as a patch against a Pearl base). **This spec previously guessed
a bsdiff/VCDIFF-style binary diff would be needed - confirmed wrong 2026-08-31.** The
real mechanism, from `ndztool.py`'s `BaseCtx`: it's windowed raw-dictionary compression,
not a diff algorithm. The base ROM gets grain-hashed (2048 B grain, BLAKE2b-8, up to 4
offsets per hash bucket) at pack time; for each target block, candidate 16 KiB windows in
the base are found (one centered near the block's own file offset, plus any whose
grain-hash matches something inside the block), each window is used as a raw content
zstd dictionary to compress the block, and whichever window (or plain/dict/filter, if
none help) compresses smallest wins - the winning window's byte offset is recorded
per-block (`u32 baseOff[n]`, sentinel `0xFFFFFFFF` for "no window used"). This reuses the
exact per-block dictionary-priming machinery `RawDictionary` already needs - no new diff
algorithm has to be designed or chosen, just the base-ROM window search added to the
existing per-block "try everything, keep the smallest" loop. Base verification is also
confirmed: the base's declared original size and game code must match exactly, and a
BLAKE2b-8-byte hash of the base's first `0x200` bytes (its header) must match a value
stored in the front-matter.

**Implemented** (`Ndz.Core.Format.Blake2b` - a from-scratch BLAKE2b, since GrindCore has
Blake2sp/Blake3 but not BLAKE2b itself, verified against Python's own `hashlib.blake2b`;
`Ndz.Core.Compression.BaseRomIndex` for the grain-hash index and candidate search;
integrated into `NdzWriter.CompressFrame`'s existing best-candidate search and
`NdzArchive`'s decode loop, with a per-window-offset cached decompressor). See
`docs/ndz-remaining-work.md` for the full integration design. `NdzWriter.Compress`/
`CompressFile` gained a `baseRom`/`baseRomPath` parameter; `NdzArchive.Open`/`OpenFile`
gained a `baseRom`/`baseRomPath` parameter, required and verified (size/gameCode/header
hash) whenever the opened file has `BasePatch` set; the CLI's `compress`/`decompress`/
`verify` all gained `--base <base.nds>`. `ndz info` deliberately does **not** need a base
ROM even for a `BasePatch` file (`NdzArchive.ReadInfo`, matching `ndztool.py`'s own
`cmd_info`) - only actually decoding block content needs it.

Verified in both directions against a real `ndztool.py --base` on real byte content: our
base-patch output decodes via `ndztool.py unpack --base ... --verify` sha256-identical
to the source, and a real `ndztool.py --base`-packed file decodes correctly through
`NdzArchive`/the CLI's `verify` command.

One property worth knowing if base-patch is touched again: the *candidate window set* a
grain-hash lookup produces is confirmed identical to `BaseCtx.window_candidates`, but
which up-to-8 subset gets used when more than 8 candidates exist is not, and can't be,
made to match a specific `ndztool.py` run bit-for-bit - Python's own `set` iteration
order depends on per-process hash randomization there, so even two `ndztool.py` runs on
identical input aren't guaranteed to agree with each other. This doesn't affect
interop - decode only ever needs whichever offset was actually recorded, never a
specific *selection* of it.

Only the retired trained-dictionary flag (bit 2) remains genuinely out of scope now, and
deliberately so, not silently dropped: `NdzFlags` documents exactly why, and both read
and write paths reject anything that would require it rather than mishandling it.

## Reference materials

- `reference/mena-packer/pack.rs` - a verbatim copy of the format author's (Mena Azer)
  own reference packer. The ground truth this port was corrected against; see its
  README for what it needs (the `census`/`filters` modules) that we don't have yet.
- `reference/mena-patchbench/` - shared 2026-08-31, explicitly described (in its own
  docstring) as duplicating `patchbench.py`'s logic rather than being that module itself.
  Confirmed 2026-09-01: it was extracted directly from the real module by a Claude
  session with actual read access to it, not reconstructed secondhand - see that folder's
  README for the full provenance:
  - `ndztool.py` - a complete, **self-contained** pack + unpack tool, no local imports,
    just `pip install -r requirements.txt` (`zstandard`, optionally `lz4`). By far the
    strongest reference material available - real, runnable code covering both encode
    and decode, including base-patch and the pair container. Cross-checked line-by-line
    against `pack.rs` and tested empirically (real venv, real ROMs, both read and write
    directions) before anything here was built against it - see that folder's README.
  - `filter-modes-explanation.md` - a transcription of a screenshot (Mena's own Claude
    session describing `patchbench.py`'s filter modes) that first named the five filter
    modes, before `ndztool.py` arrived and confirmed the same names directly in code.
  - An earlier decode-only script, `ndzunpack.py`, and a scratch Rust project,
    `reference/ndz-reference/` (kept only for inspecting the `zstd` crate's dictionary
    API against GrindCore's), were both removed 2026-08-31 as redundant once `ndztool.py`
    arrived and independently confirmed everything either one had shown - see git
    history if either is ever needed again.
