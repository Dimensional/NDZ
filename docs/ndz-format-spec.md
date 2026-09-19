# NDZ file format

NDZ is a seekable, block-compressed container for decrypted Nintendo DS ROMs (`.nds`),
designed for a flashcart (DSPico) that decompresses on the fly while the console is
stalled waiting on a cart read. The format was designed by **Mena Azer**, its author;
this document describes it as implemented by this project (`src/Ndz.Core`).

## Status and provenance

**This is a reverse-engineered specification, not one published by the format author.**
It is built from two confirmed sources: the author's own reference packer
(`reference/mena-packer/pack.rs`, Rust) and a complete, self-contained pack/unpack tool
extracted directly from the author's real implementation module
(`reference/mena-patchbench/ndztool.py`, Python). Every claim below has been verified
against at least one of these, and most have been cross-checked by round-tripping real
files against the genuine reference tooling (`reference/mena-patchbench/README.md`
covers the full provenance).

The format itself is still **work in progress on the author's own side** — this is not a
finished, versioned specification we control. In particular, the multi-ROM pair-container
dictionary design (see "Pair container") is not something the author has confirmed as
settled or verified on real hardware. Treat this document as accurate as of the commit
that touched it, not as a guarantee that the underlying format won't change.

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

- **Frame** is the unit the outer seek table addresses, 128 KiB by default
  (`NdzConstants.FrameSize`). Only the final frame may be shorter. Frame size is a
  per-file packing choice, not a fixed format value — nothing in the front-matter
  records it, since the seek table's own per-frame sizes are sufficient to derive frame
  boundaries on read.
- **Block** is the unit each frame is internally subdivided into, 8 KiB by default
  (`NdzConstants.BlockSize`) — a private structure inside a frame's own bytes, invisible
  to the outer seek table. A frame's payload is laid out as:
  ```
  [u32 csize × nblocks]      per-block compressed size
  [u8  mode  × nblocks]      per-block compression mode (see "Block modes") -
                             present only when the Filters flag (bit 3) is set
  [u32 baseOff × nblocks]    per-block base-window offset - present only when
                             BasePatch (bit 4) is set without HackContainer (bit 6);
                             see "Base-ROM patch mode"
  [compressed block bytes, concatenated]
  ```
  This writer always sets the Filters flag, so the mode array is always present in
  files it produces. A `HackContainer` file sets `BasePatch` too but never has the
  `baseOff` array — see "Hack container format".

  Each block is compressed independently as a standalone zstd frame
  (`Nanook.GrindCore.ZStd.ZStdBlock`), decodable on its own. This independence is what
  makes the format seekable: reading any byte range only requires decompressing the
  frame(s), and within a frame the block(s), that the range falls in
  (`Ndz.Core.Compression.NdzArchive.ReadAt`). Block size is a real per-file variable
  (see "Flags"), not a fixed constant — it must be read from each file's own front-matter.
- The trailer is read from the end of the file backwards: the last 8 bytes are
  `nframes` (u32) followed by a magic (u32); the `nframes × 8` bytes before that are the
  outer, frame-level seek table.

## Front-matter (16384 bytes)

| Offset | Type | Field | Notes |
|---|---|---|---|
| 0x0000 | u32 | magic | `NDZ1` = `0x315A444E` |
| 0x0004 | u32 | frontMatterSize | always `16384` |
| 0x0008 | u32 | originalSize | raw `.nds` size |
| 0x000C | u32 | flags | composite bitfield — see "Flags" |
| 0x0010 | 0x2400 | banner | `.nds` banner, copied from the source ROM's banner offset (header 0x68); real content size depends on the banner's own version field, zero-padded to the full 0x2400 slot — see "Banner sizing" |
| 0x2410 | u32 | gameCode | mirror of `.nds` header 0x0C |
| 0x2414 | u32 | dictionaryStoredSize | 0 if no dictionary section |
| 0x2418 | u32 | baseOriginalSize | meaningful only when BasePatch is set |
| 0x241C | u32 | baseGameCode | meaningful only when BasePatch is set |
| 0x2420 | u64 | baseHeaderHash | meaningful only when BasePatch is set — BLAKE2b-8 hash of the base ROM's first 0x200 bytes, checked against the supplied base at decode time |
| 0x2428 | u32 | dictionaryDecompressedSize | 0 if no dictionary section |
| 0x242C–0x3FFF | — | reserved | zero |

Implementation: `Ndz.Core.Format.NdzFrontMatter` (read/write), `NdzConstants` (offsets),
`NdsRomInfo` (extracts gameCode/banner from a raw `.nds`).

### Banner sizing

The reserved front-matter slot is a fixed 0x2400 bytes, but the real banner content
copied into it depends on the banner's own version field (a u16 at the banner offset
itself):

| Version | Content size |
|---|---|
| < 2 | 0x840 |
| 2 | 0x940 |
| 3–0x102 | 0xA40 |
| ≥ 0x0103 | 0x23C0 |

Anything beyond the real content size (up to the full 0x2400 slot) is zero-padded, not
copied verbatim from the source ROM (`NdzConstants.GetBannerContentSize`).

A source ROM whose header `bannerOffset` (0x68) is zero, or points past (or within 2
bytes of) the end of the ROM, is refused outright rather than packed with an empty
banner — this matches both reference implementations, which hard-refuse such a ROM the
same way (`NdsRomInfo.FromRom`).

### Flags

`Ndz.Core.Format.NdzFlags` is a composite bitfield: bits 0/1/2/3/5/6 are named boolean
flags, bit 4 is the base-ROM/patch flag, and bits 8+ hold a numeric subfield (the block
size, as log2) rather than another boolean.

| Bit(s) | Meaning |
|---|---|
| 0 | `V2` — set on every file this format produces; marks the frame/block hierarchical layout this document describes, as opposed to a legacy flat layout the format also defines but this project never produces or reads. |
| 1 | `ZStd` — the block codec is zstd. The format also allows an lz4-coded flat-layout variant; this project neither reads nor produces it. |
| 2 | `TrainedDictionary` — a *trained* zstd dictionary (with its own dictID/entropy tables), distinct from `RawDictionary`/bit 5. Retired in the real format: no known encoder produces it. **Not implemented** — reading a file with this bit set throws, rather than risk misreading a trained-dict section as raw content. |
| 3 | `Filters` — gates whether a per-block mode array exists in a frame at all. Without it, every block in a frame is uniformly `Dict` (if a dictionary is present) or `Plain` (if not), with no per-block byte to read. This writer always sets it, since it always writes a mode array. |
| 4 | `BasePatch` — this file's frames encode a patch against a base `.nds` (see the `baseOriginalSize`/`baseGameCode`/`baseHeaderHash` fields and "Base-ROM patch mode"), rather than the ROM's own bytes directly. |
| 5 | `RawDictionary` — a raw, self-referential content dictionary section follows the front-matter (see "Dictionary support"). |
| 6 | `HackContainer` — a `.delta.ndz` hack container (see "Hack container format"). Always set alongside `BasePatch`, whose meaning it changes rather than extends. |
| 7 | Reserved/unknown — never observed set by any confirmed source. |
| 8+ | Block size, packed as `log2(blockSize)`; `0` is a sentinel meaning "unspecified, default to 4096" rather than `1 << 0 = 1`. This 8-bit subfield occupies bits 8–15 (`NdzFlagsExtensions.GetBlockSizeLog2`/`GetBlockSize`/`WithBlockSize`). |

Undefined bits are never repurposed speculatively — reserved bits stay zero pending
confirmation from the format author.

Block size is a real per-file variable: the reference packer always emits fixed 8 KiB
blocks, but the reference tool's own default (when the subfield is left at 0) is 4 KiB —
both are valid, format-legal choices. This project defaults to 8 KiB but reads the
actual value from each file's own flags rather than assuming a constant.

### Block modes (`BlockMode`)

Each block's mode byte selects how its bytes were compressed. Selection is brute
force: every block is compressed plain and, if a dictionary is active, also
dictionary-primed; a **full** block (exactly the configured block size — a frame's
final, possibly-short trailing block never qualifies, even if its length happens to
itself be a power of two) is additionally tried through each of the five filter
transforms below; whichever result compresses smallest wins.

| mode | name | description |
|---|---|---|
| 0 | `Dict` | zstd, primed with the file's raw content dictionary |
| 1 | `Plain` | zstd, no dictionary, no filter |
| 2/3/4 | `Delta1`/`Delta2`/`Delta4` | `out[i] -= out[i - stride]` (mod 256), stride 1/2/4, then zstd |
| 5/6 | `Shuffle2`/`Shuffle4` | de-interleave into 2 or 4 byte planes, then zstd |
| 7 | `Verbatim` | hack-container only — see "Hack container format" |

Delta targets slowly-varying 16/32-bit sequences (coordinates, pointers, audio samples),
turning them into runs of near-zeros. Shuffle groups a value's high bytes together
(often near-identical across an array) separately from its noisier low bytes. Neither
filter shrinks data by itself — both just give the compressor longer matches to find.
Both filters are applied before compression and undone after decompression; the
underlying codec is always the same plain zstd compressor used for `Plain`, just fed
filtered bytes.

In base-patch mode, a winning base-window match is tagged `Plain` (mode 1), not a
distinct mode value — it's distinguished by its recorded `baseOff` entry instead, not
the mode byte (see "Base-ROM patch mode").

A mode byte outside the range this format defines (0–7, or 0–6 outside a hack
container) is a hard read error, naming the exact frame, block, and mode byte, rather
than being misinterpreted.

Implementation: `Ndz.Core.Format.BlockMode`/`BlockFilters` (transforms),
`Ndz.Core.Compression.NdzWriter` (encode-side selection), `NdzArchive` (decode).

## Compression

`Ndz.Core.Compression.NdzWriter.Compress` splits the ROM into frames, splits each frame
into blocks, and for every block runs the brute-force candidate search described above
via `Nanook.GrindCore.ZStd.ZStdBlock` with `CompressionOptions { Type = <level>,
BlockSize = blockSize }`. Default level is `CompressionType.Level19` (near-max zstd
effort) — worth it even at a small block size, since it's search effort, not window
size, and there is no cross-block history to lose regardless of level. Frames are
independent, so they compress in parallel (one worker per frame); the resulting file is
byte-identical regardless of how many threads did the work. Concurrency is capped by an
optional `maxDegreeOfParallelism` parameter (unbounded by default) so an interactive
caller — a GUI packing in the background — can leave a core or two free.

`Ndz.Core.Compression.NdzArchive` is the reader: it parses the front-matter and outer
seek table once, then serves `ReadAt(offset, buffer)` by decompressing only the
frame(s) — and within a frame, only the block(s) — a read actually touches, with a
single-frame cache for sequential access. `DecompressAll()` is `ReadAt(0, wholeBuffer)`.

An `NdzArchive` instance is not thread-safe (its frame cache and scratch buffers are
shared, unsynchronized mutable state); open a separate instance per thread for
concurrent random-access reads.

## Hardware limits

Two knobs are hardware ceilings on the target decoder, not preferences — the flashcart
decompresses on the fly while the console is blocked on a cart read, so a value past
either ceiling produces a file that packs successfully and then fails, or stalls, on
real hardware:

- **Compression level: max 19** (`NdzConstants.MaxLevel`). Level 19 decompresses in
  roughly 300 μs on real hardware against a roughly 330 μs decode timeout — about a 10%
  margin, confirmed directly by the format author. A faster, lower-ratio codec (lz4)
  exists as a legacy read-only option in the format but is not produced by any known
  encoder, including this one.
- **Block size: max 32768 bytes** (`NdzConstants.MaxBlockSize`). A larger block takes
  too long to fetch and decompress on a cache miss. This ceiling was raised from an
  earlier 8192-byte limit after a firmware fix; the CLI's own `--block-size` menu offers
  exactly three choices (8/16/32 KiB, `NdzConstants.SupportedBlockSizes`, or `auto`),
  though the writer itself accepts any power of two up to the ceiling.

Both `NdzWriter.Compress` and the CLI refuse to pack past either limit, rather than
produce a file that fails later on real hardware.

## Dictionary support

`RawDictionary` (bit 5) is the only dictionary mechanism this format defines for
general-purpose use — a raw, untrained content dictionary derived from the ROM's own
repeated content and stored verbatim after the front-matter. There is a second,
formally-retired flag (`TrainedDictionary`, bit 2) that wraps dictionary bytes in a
trained zstd dictionary object instead of loading them raw; no known encoder produces
it, and this project does not implement it.

**Dictionary content is always derived, never externally supplied.** Given only a
target size, `Ndz.Core.Compression.RawDictionaryBuilder` finds the dictionary content
in two steps:

1. **Content-defined chunking**: a rolling gear hash finds natural chunk boundaries
   (512 B–16 KiB, ~4 KiB average) that shift with the content itself, so a repeated
   region chunks identically wherever it reappears, even when not aligned to any fixed
   block boundary.
2. **Dedup-weighted ranking**: chunks that recur are ranked by
   `(occurrences - 1) × length` — the bytes an ideal dedup would save. A chunk that is
   really just a long run of one repeated byte (e.g. zero-padding) is skipped, since
   it isn't genuine duplicate content. The highest-value chunks are packed into the
   dictionary up to the target size. If the ROM doesn't have at least 4 KiB of useful
   duplicate content, no dictionary is stored at all.

Per block, both a plain and a dictionary-primed compression are tried, and whichever is
smaller wins (`BlockMode.Plain` or `BlockMode.Dict`). The dictionary section is always
stored uncompressed — a file whose declared stored and decompressed dictionary sizes
differ is rejected as an unsupported variant.

**Window sizing**: zstd's default dictionary window sizing caps at 8 MiB for any
dictionary over 256 KiB at compression level 19, and never grows further regardless of
dictionary size — content beyond that reach becomes unreachable as match material (a
compression-ratio issue, not a correctness one). This writer explicitly sizes the
compression window to cover the whole dictionary plus one block
(`NdzWriter.ComputeDictionaryWindowBits`), avoiding the cap.

### Dictionary sizing

On the target hardware, the raw dictionary and the decompressed-block cache share one
**8 MiB PSRAM pool** — a dictionary that consumes the whole budget leaves nothing for
the cache that makes random access fast. This is a real hardware constraint, not a
tuning choice, and is why the dictionary size search never goes above it.

Choosing a dictionary size is otherwise a pure trade-off with no reference algorithm to
port — neither reference implementation has a size *recommender*, only a caller-supplied
target size. `Ndz.Core.Compression.DictionaryAnalyzer` (wired to the CLI's `ndz analyze`
and `--raw-dict auto`) is this project's own heuristic: it estimates, via a fast
stratified sample of the ROM's blocks, how a ladder of candidate dictionary sizes would
compress the ROM, then recommends the smallest size already within 1% of the best total
found anywhere in the curve — not the literal global minimum, since a dictionary's own
on-disk storage cost can make a larger dictionary score strictly worse past some point,
and PSRAM not spent on the dictionary is normally worth more to the cache than shaving a
further sliver off file size.

`BlockSizeAnalyzer` applies the same diminishing-returns logic to block size instead of
PSRAM (random-access granularity is the resource traded away there). Its pair-aware mode
(`AnalyzePair`, `ndz analyze --pair`) scores a candidate block size against the base ROM
and every base-patched target **combined**, not the base alone — the base-patch window
search always uses a fixed 16 KiB window (see "Base-ROM patch mode") that does not grow
with block size, so a block size that looks better for a self-contained ROM can badly
hurt a base-patched one at the same size.

None of this analysis changes the on-disk format: `auto` sizing only ever picks a plain
size or block-size value that then goes through the same write path as a manually-chosen
one.

## Base-ROM patch mode (flags bit 4)

Base-patch mode stores a ROM as a patch against a second, previously-decrypted `.nds`
supplied again at decode time (e.g. one game version stored as a patch against another
version of the same game). It is **windowed raw-dictionary compression against the base
ROM's own content, not a binary diff algorithm.**

The base ROM's identity is verified, not its content bytes: `baseOriginalSize` and
`baseGameCode` must match exactly, and an 8-byte BLAKE2b hash of the base's first 0x200
bytes (its header) must match the front-matter's `baseHeaderHash`.

At pack time, the base ROM is indexed by hashing it into non-overlapping 2048-byte
grains (BLAKE2b-8, up to 4 offsets per hash bucket). For each target block, candidate
16 KiB windows into the base are found — one centered near the block's own file offset,
plus one per grain-sized sub-chunk of the block whose hash matches something in the
index, capped at 8 candidates total. Each candidate window is used as a one-shot raw
content dictionary to compress the block; whichever candidate (or plain/dict/filter, if
none help) compresses smallest wins. The winning window's byte offset is recorded per
block in a `u32 baseOff[nblocks]` array (sentinel `0xFFFFFFFF` for "no window used"); a
win is tagged mode `Plain`, not a distinct mode value, so only `baseOff` distinguishes
it. A recorded base-window offset always takes priority over the block's own mode byte
when both a `Dict` mode and a base-window offset could apply.

A base ROM shorter than the 16 KiB window size is refused outright at pack time, since a
pack that happens to select a base-window candidate near the end of a too-short base ROM
could otherwise succeed at pack time and fail to unpack.

Implementation: `Ndz.Core.Format.Blake2b`, `Ndz.Core.Compression.BaseRomIndex` (grain-hash
index and candidate search), integrated into `NdzWriter`'s per-block candidate search and
`NdzArchive`'s decode path.

## Pair container format

An outer container wrapping two or more complete `.ndz` blobs together, so a family of
related ROMs (e.g. several regional or version releases of the same game) can ship as a
single self-contained file with no external base ROM needed to unpack any of them.

Layout: a 16 KiB header — `[magic 'NDZP' = 0x505A444E][hdrSize=16384][nRoms][reserved]`
(all u32) — followed by one entry record per ROM at offset `0x10 + 0x10 × i`:
`[offset][size][origSize][gameCode]` (three u32s plus a 4-byte game code). Entries
themselves are complete `.ndz` blobs, each padded to a 16 KiB boundary. Entry 0 is
always self-contained; every other entry is `BasePatch`-flagged and resolves its base to
that same entry 0 — a star topology, never a chain, and never against an externally
supplied file.

Each entry is independently `RawDictionary`-capable, since each is a fully independent
`.ndz` blob with its own front-matter. **The format author has stated that independent
per-entry dictionaries were not part of their own intended design for this container**,
and has not confirmed whether it behaves correctly on real hardware either way — this is
a genuinely open question, not resolved in either direction. Because of this,
pair-container **creation** is currently disabled in this project's own CLI and GUI
(`Ndz.Core.Compression.PairContainerPolicy.CreationEnabled = false`); the underlying
write/read implementation (`NdzPairWriter`/`NdzPairContainer`) is untouched and fully
functional, and reading an existing pair container is unaffected — only creating a new
one through those two entry points is refused, pending confirmation of the intended
design.

Implementation: `Ndz.Core.Format.NdzPairEntry`, `Ndz.Core.Compression.NdzPairWriter`/
`NdzPairContainer`, mirroring the single-file writer/reader split. `NdzPairContainer`
detects a pair container purely from its magic and resolves entries transparently — a
base-patched entry decompresses the self-contained one first to resolve its base.

## Hack container format (`.delta.ndz`)

A hack container packs a ROM hack or version diff cheaply against an already-packed base
`.ndz`, for cases where the target hardware attaches an on-cart patch to a base title
already installed. Unlike the rest of this format, no specification or sample exists
from the format author for this variant — it is reverse-engineered from real output of
the author's own web-based packing tool.

A hack container is not a new container type: same `NDZ1` magic, same 16 KiB
front-matter, same frame/trailer layout as any other `.ndz`. It sets the existing
`BasePatch` (bit 4) flag together with `HackContainer` (bit 6) — bit 6 changes what bit 4
means here rather than extending it:

- The per-frame `baseOff[nblocks]` array that `BasePatch` normally implies does **not**
  exist.
- The base-identity fields (`baseOriginalSize`/`baseGameCode`/`baseHeaderHash`) are all
  zero — this variant does not verify an externally-supplied raw base `.nds` the way
  ordinary base-patch mode does.
- The front-matter's `gameCode` field holds the **base's** game code, not this file's
  own content — so a reader can locate the base `.ndz` this hack needs by game code
  alone.
- Every block additionally has `BlockMode.Verbatim` (mode 7) available: the block's
  "compressed data" slot holds a literal 4-byte little-endian offset into the base ROM,
  and the block's content is exactly that many bytes copied verbatim from that offset —
  no zstd involved. This offset is not necessarily the block's own aligned position;
  relocated content (a whole run of blocks shifted to a different base offset, advancing
  by one block size per block) is handled the same way as unchanged content.
- `BlockMode.Dict` blocks draw their dictionary from the **base `.ndz`'s own embedded
  raw-dict section** (its `RawDictionary` blob), not one derived from this file's own
  content — this file has no dictionary section of its own. This only works when the
  base was packed with a raw dictionary in the first place; the fallback behavior when
  it wasn't has not been observed in a real sample.
- Modes 1 and 3–6 (plain and the five byte-transform filters) are unchanged from
  ordinary use — self-contained, no base reference.

Decoding requires the already-packed base `.ndz` (not the raw base ROM) — supplied to
`NdzArchive.Open`'s `baseNdzBytes` parameter. Because the base-identity fields are zero,
decoding against the wrong base `.ndz` produces silently-wrong output; this is inherent
to the real format, not a gap in this implementation.

Implementation: `NdzFlags.HackContainer`, `BlockMode.Verbatim`,
`Ndz.Core.Compression.HackContainerWriter` (a separate writer from `NdzWriter`, sharing
its plain/dict/filter candidate search), `NdzArchive.Open`'s `baseNdzBytes` parameter.
Verbatim-block matching uses content-defined chunking (the same technique the format
author's own packer uses internally) as its primary search, with a fixed-position
hash-chain search as a fallback for whatever chunk boundaries miss.

## Unimplemented: trained dictionary (flags bit 2)

The only defined feature this project does not implement is the trained-dictionary
variant of the dictionary flag (bit 2). It is formally retired in the real format — no
known encoder, including the format author's own reference tooling, produces it — and
implementing it is not planned unless a real file using it surfaces. Reading a file with
this bit set is a hard, named error rather than a silent misread.

## Reference materials

- `reference/mena-packer/pack.rs` — a verbatim copy of the format author's own reference
  packer (Rust).
- `reference/mena-patchbench/` — a complete, self-contained pack/unpack tool
  (`ndztool.py`) extracted directly from the format author's real implementation module;
  the strongest available reference, covering encode and decode for every feature in
  this document including base-patch and the pair container. See that folder's own
  README for full provenance.
