# NDZ format — spec and implementation status

Status: v1 implemented and round-trip tested (`src/Ndz.Core`, `src/Ndz.Cli`). Corrected
against the format author's own reference packer (a Rust implementation, `pack.rs`) on
2026-08-22 after an initial pass mis-modeled the frame/block hierarchy - see "Corrections
from the reference packer" below for exactly what changed and why. Compression,
random-access decompression, and the raw-content dictionary (flags bit 5) are
implemented. Base-ROM patch mode (flags bit 4), the retired trained-dictionary variant
(flags bit 2), the pair-container format, and the five filter per-block compression
modes are all **deferred** — see "Open questions."

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
| 8+ | **not a boolean** — the block size, packed as `log2(blockSize)`, with `0` itself a sentinel meaning "unspecified, default to 4096" rather than a literal `1 << 0 = 1` — confirmed against `ndztool.py`'s own decode logic (see "Reference materials"; we have that script, not the `patchbench.py` module it defers the real format definition to). See `NdzFlagsExtensions.GetBlockSizeLog2`/`GetBlockSize`/`WithBlockSize`. The field's width is confirmed exactly 8 bits (bits 8-15) by `ndztool.py`'s own `describe_flags`, which computes `(flags >> 8) & 0xFF`. |

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

**Bits 2 and 4 are read-but-rejected, not implemented**: `NdzFrontMatter.Read` throws
`NotSupportedException` if either is set, and `NdzWriter` never sets either. See "Open
questions."

### Per-block compression mode (`BlockMode`)

Each block's mode byte selects how its bytes were compressed. All 7 values are now
named and their meaning confirmed - see "Filter modes" under "Open questions" below for
the full writeup and provenance. **Only `Plain` (1) and `Dict` (0) are implemented
here** - the five filter transforms (2-6) are named/documented but not yet coded.
`NdzArchive` fails loudly - naming the exact frame index, block index, and raw mode byte
- on any other mode, rather than misinterpreting bytes it doesn't understand. This means
**real `.ndz` files produced by the reference packer or `ndztool.py` may not be fully
readable by this port yet** whenever a block actually used a filter mode (the common
case - "the reference tool always packs with filters on").

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
size is a `blockSize` parameter, any power of two up to the hardware ceiling of 8192 -
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
- block size: max **8192 bytes** (`NdzConstants.MaxBlockSize`) - a bigger block takes
  too long to fetch *and* decompress on a cache miss and the console freezes.

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

`Ndz.Core.Format.NdzDictionary` is real now, not a placeholder: `NdzWriter.Compress`
takes one and uses it, `NdzArchive.Open` reads a file's dictionary section and applies
it. Per-block behavior matches the reference packer's own architecture: every block is
compressed both plain and dictionary-primed, whichever is smaller wins, tagged
`BlockMode.Plain` or `BlockMode.Dict` accordingly (see `NdzWriter.CompressFrame`). The
dictionary section is stored verbatim/uncompressed (`DictionaryStoredSize ==
DictionaryDecompressedSize` always, for this writer) - a file claiming otherwise is
rejected as an unsupported variant rather than misread.

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
(relayed through the user) and `ndzunpack.py`'s own code - see "Filter modes" below. We
still do not have `patchbench.py` itself, only those two secondhand sources - no
contradiction found between them and the black-box WASM probing, but neither is the
format's real source.

### Filter modes — algorithm confirmed 2026-08-31, not yet implemented

Confirmed real and in active use since 2026-08-25 (mode values 2-6 observed on real
ROMs, independent of dictionary use). On 2026-08-31 Mena shared, through the user, her
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

**Still not implemented in `Ndz.Core`** - only the enum values/meanings are confirmed,
not the encode/decode transform logic (see `BlockMode`'s own doc comment). `NdzArchive`
fails loudly (naming the frame/block/mode) on any block using one, rather than
misinterpreting it - confirmed correct against a real `ndztool.py`-produced file (see
"Per-block compression mode" above): parses everything up to the first actual
filter-mode block, then fails there cleanly, exactly as designed.

### Dictionary flag split — `RawDictionary` (live) vs. `Dict` (retired)

`Ndz.Core.Format.NdzDictionary`/`RawDictionary` (bit 5) is the only dictionary mechanism
`Ndz.Core` implements, matching `pack.rs` exactly - a raw content dictionary, stored
verbatim, loaded directly. There is a second, separate flag: **`TrainedDictionary` = bit
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

### Pair container format — encode side now fully specified, still unimplemented here

An outer container format wrapping two complete `.ndz` blobs side by side, used to ship
a base-patch pair together with no external base file needed. Both read and write sides
are now confirmed from `ndztool.py`'s `--pair-out`:
`NDZ_PAIR_MAGIC = 0x505A444E` ('NDZP'), header `[magic][hdrSize=16384][nRoms][reserved]`
(all u32) at offset 0, then per-entry `[offset][size][origSize][gameCode]` (u32 x3 +
4 bytes) at `0x10 + 0x10*i` - entries themselves are the base blob followed by the
patched blob, each padded to a 16 KiB (front-matter-size) boundary. One entry is
self-contained; the other (the `BasePatch`-flagged one) resolves its base to the *other*
entry in the same container, not an externally-supplied file. Nothing in `Ndz.Core`
models this yet - it sits above the single-`.ndz` level entirely.

### Base-ROM patch mode (flags bit 4) — mechanism now fully understood, still unimplemented

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
stored in the front-matter. Not implemented yet in `Ndz.Core` — a real, non-trivial
feature (grain-hash index, window candidate search, the extra per-block header array),
deferred pending direction on priority, not on missing information anymore.

All items above are explicitly out of scope for this v1, not silently dropped: the
front-matter fields and `NdzFlags`/`BlockMode` values exist and document exactly what's
unconfirmed or unimplemented, and both read and write paths reject anything that would
require the unimplemented behavior rather than mishandling it.

## Reference materials

- `reference/mena-packer/pack.rs` - a verbatim copy of the format author's (Mena Azer)
  own reference packer. The ground truth this port was corrected against; see its
  README for what it needs (the `census`/`filters` modules) that we don't have yet.
- `reference/mena-patchbench/` - shared 2026-08-31, explicitly described (in its own
  docstring) as duplicating `patchbench.py`'s logic rather than being that module
  itself, which we still don't have:
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
