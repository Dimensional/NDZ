# NDZ remaining work — scope and architecture

Companion to `docs/ndz-format-spec.md`. That doc is the format reference; this one is
the implementation plan for the three pieces `Ndz.Core` doesn't do yet, all confirmed
(not guessed) against `reference/mena-patchbench/ndztool.py` and cross-checked against
`reference/mena-packer/pack.rs`. Written 2026-08-31 per the user's request to lay out
scope/architecture before continuing, and to keep "build the real thing, precisely" (not
a scope ceiling) in view - see the `ndz-spec-provenance` memory's note on this.

Status snapshot before this work: compression, random-access decompression, and the raw
dictionary (bit 5) are implemented. Filter modes, base-patch, the trained-dictionary
flag (retired, not worth doing), and the pair container are not.

**Update 2026-08-31: §1 (filter modes) is done.** Implemented as designed below
(`Ndz.Core.Format.BlockFilters`, the best/candidate swap loop in
`NdzWriter.CompressFrame`, mode dispatch in `NdzArchive.GetDecompressedFrame`), unit
tested (hand-verified transform values, hand-built decode fixtures per mode, and two
end-to-end write+read round trips on content crafted so a Delta/Shuffle mode actually
wins the writer's own selection), and cross-checked against a real `ndztool.py` in both
directions on real byte content: our filter-mode output decodes via `ndztool.py unpack
--verify` sha256-identical to the source, and a real `ndztool.py`-packed file (mode
histogram: plain/delta1/delta2/shuffle2 all present) decodes correctly through
`NdzArchive`. `NdzWriter` gained an `enableFilters` parameter (default on, matching
`ndztool.py`'s `--no-filters` being opt-out) and the CLI exposes `--no-filters` too. §2
(base-patch) and §3 (pair container) remain as designed below.

## 1. Filter modes (`Delta1`/`Delta2`/`Delta4`/`Shuffle2`/`Shuffle4`) — done

**Confirmed algorithm** (`ndztool.py`'s `_delta_fwd`/`_delta_inv`/`_shuffle_fwd`/
`_shuffle_inv`, already transcribed in `BlockMode`'s doc comment):

- Delta, stride s: `out[i] = b[i] - b[i-s]` (mod 256) for `i` from the end down to `s`;
  bytes `[0, s)` pass through unchanged. Confirmed safe to do **in place**, descending:
  at the point index `i` is written, index `i-s` (needed for the read) hasn't been
  touched yet in this pass (all prior iterations were at higher indices), so it still
  holds its original value - matches Python's semantics of always reading the
  *original* `b`, not a partially-updated buffer.
- Delta inverse: `out[i] += out[i-s]`, ascending from `s`. This one *is* a running
  prefix-sum and is supposed to read the already-restored `out[i-s]` - also safe
  in-place, ascending.
- Shuffle, `s` planes: de-interleave - `dest[k*planeLen + p] = src[p*s + k]`. Requires a
  second buffer (not in-place - it's a full gather, not a local swap).
- Shuffle inverse: the mirror scatter, `dest[p*s + k] = src[k*planeLen + p]`.

New type: `Ndz.Core.Format.BlockFilters` (static class, next to `BlockMode`) -
`DeltaForward`/`DeltaInverse` (`Span<byte>`, in place after an initial copy for
forward), `ShuffleForward`/`ShuffleInverse` (`ReadOnlySpan<byte> source, Span<byte>
destination`). No allocation policy baked in - callers own the buffers.

**Write side** (`NdzWriter.CompressFrame`): confirmed condition for trying a filter at
all is `blockLength == blockSize` (a full block only - not "any power-of-two length",
see the `BlockMode.cs` correction from the last audit pass). For a full block, try all 7
candidates - plain, dict (if active), and all 5 filters compressed through the **plain**
compressor (`plain_cctx.compress(fwd(blk))` in `ndztool.py` - filters never combine with
a dictionary) - keep whichever is smallest. Restructured from today's 2-buffer
plain/dict comparison into a **best/candidate swap** pattern (two same-sized compressed-
output buffers, swapped by reference whenever a candidate wins) plus one reusable
`blockSize`-sized scratch buffer for the filtered plaintext, so the candidate count stays
easy to extend without allocating per-attempt. `filterSrcBuffer` is always filled fresh
from the ROM's own bytes before each filter attempt, never chained from a previous
filter's output.

**Read side** (`NdzArchive.GetDecompressedFrame`): a `Delta*`/`Shuffle*` mode byte
decompresses through `_plainBlock` (never `_dictBlock` - matches `ndztool.py`'s
`decompress_v2_adv`, where only `NDZ_MODE_DICT` uses the dict decompressor). Delta
inverse runs in place directly on the output buffer's slice for that block. Shuffle
needs a `_blockSize`-sized scratch buffer (one field, allocated once per archive) to
decompress into before scattering into the real output slice.

**API surface change**: `NdzWriter.Compress`/`CompressFile` gain an `enableFilters =
true` parameter, mirroring `ndztool.py`'s `--no-filters` (opt-out, not opt-in) - lets a
caller skip the ~3-6x extra compress-call cost per full block when it isn't worth it.
`Filters` stays set in the front-matter either way (a mode array always exists, to
distinguish `Plain`/`Dict`, independent of whether any block ever picks a filter).

**Cost**: brute-force, matching the reference exactly - up to 7 zstd compress calls per
full block instead of today's 1-2. For an 8 KiB block this is still cheap per-call; for
a full ROM (tens of thousands of blocks) this is a real, expected slowdown versus
today - not a bug, the reference tool pays the identical cost by design ("the reference
tool always packs with filters on").

**Update 2026-08-31: §2 (base-patch) is done too.** Implemented as designed below
(`Ndz.Core.Format.Blake2b` - a from-scratch BLAKE2b since GrindCore has Blake2sp/Blake3
but not BLAKE2b, RFC-7693-and-Python-`hashlib`-verified; `Ndz.Core.Compression.BaseRomIndex`
for the grain-hash index and candidate search; the base-window candidate loop folded into
`NdzWriter.CompressFrame`'s existing best/candidate machinery; a cached per-offset
windowed decompressor in `NdzArchive`). `NdzFrontMatter.Read` no longer rejects
`BasePatch` outright - that policy (a base ROM must be supplied, and must match) moved to
`NdzArchive.Open`'s new `baseRom` parameter, verified immediately (size/gameCode/BLAKE2b
header hash) exactly like `ndztool.py`'s own `decode_ndz_blob`. Also added
`NdzArchive.ReadInfo` (front-matter + seek table only, no base ROM needed) once it became
clear `ndz info` would otherwise regress into requiring `--base` just to print a summary -
`ndztool.py`'s own `cmd_info` never needs it either. `NdzWriter`/`NdzArchive`/the CLI all
gained a `baseRom`/`--base` parameter.

Verified in both directions against a real `ndztool.py --base` on real byte content:
our base-patch output decodes via `ndztool.py unpack --base ... --verify`
sha256-identical to the source, and a real `ndztool.py --base`-packed file (block modes
in that specific run: 99% delta1, since the synthetic test content happened to be even
more delta-friendly than base-window-friendly - not a discrepancy, `ndztool.py`'s own
brute-force selection legitimately preferred it too) decodes correctly through
`NdzArchive`/the CLI's `verify` command. 114 tests passing (21 new: `Blake2bTests`
against Python-`hashlib`-cross-checked vectors including a multi-block one,
`BaseRomIndexTests`, `BasePatchTests`, plus a `ReadInfo` regression test).

## 2. Base-ROM patch mode (flags bit 4) — done

**Confirmed mechanism** (`ndztool.py`'s `BaseCtx`, already summarized in
`docs/ndz-format-spec.md`'s "Base-ROM patch mode" section): windowed raw-dictionary
compression against a second ROM, not a diff algorithm.

- **Index** (pack time only): grain-hash the base ROM - BLAKE2b, 8-byte digest, 2048 B
  non-overlapping grains, bucketed by hash, capped at 4 offsets/bucket (a real, small
  cap - later grains hashing to a full bucket are simply not indexed, matching
  `ndztool.py`'s own behavior exactly, not an approximation of it).
- **Per-block candidate search**: for each target block, one candidate window centered
  on the block's own file offset (clamped to the base's bounds), plus one candidate
  per 2048 B sub-chunk of the block whose grain hash matches something in the index
  (deduped into a set, capped at 8 total candidates) - each is a 16 KiB window into the
  base ROM.
- **Per-candidate compression**: the window is used as a raw-content zstd dictionary
  (the same `InitProperties`-primed `ZStdBlock` mechanism `RawDictionary` blocks use,
  just built from a base-ROM slice instead of the target ROM's own content) to compress
  the block; whichever candidate
  (or plain/dict/filter, if none help) is smallest wins. The winning window's **byte
  offset** is recorded per block, not the window's content - `u32 baseOff[n]`, sentinel
  `0xFFFFFFFF` for "no base window used". A win is tagged mode `Plain` (not a new mode
  value - `ndztool.py`'s own `compress_block_best` does the same, see
  `docs/ndz-format-spec.md`'s note on this), distinguished purely by `baseOff != 0xFFFFFFFF`.
- **Verification**: base's declared original size and game code must match exactly, and
  BLAKE2b-8 of the base's first 0x200 bytes must match `NdzFrontMatter.BaseHeaderHash`.

**New types**:
- `Ndz.Core.Compression.BaseRomIndex` - owns the grain-hash table over a base ROM byte
  array; `IEnumerable<int> CandidateWindowOffsets(ReadOnlySpan<byte> block, int fileOffset)`
  returns up to 8 window start offsets (already clamped into range), mirroring
  `BaseCtx.window_candidates`.
- Per-block base-window compression reuses the existing dictionary compressor
  machinery (`ZStdBlock` + `CompressionOptions.InitProperties`) - no new codec path,
  just a differently-sourced dictionary per candidate window. Note this means
  constructing (or at least re-priming) a `ZStdBlock` per candidate per block, which is
  real, non-trivial per-block overhead `ndztool.py` also pays (`zstd.ZstdCompressor(...,
  dict_data=d)` fresh per call in `BaseCtx.compress_with_window`) - not something to
  "optimize away" relative to the reference without re-confirming it doesn't change output.

**Write-side API**: `NdzWriter.Compress`/`CompressFile` gain an optional `byte[]? baseRom`
parameter. When supplied: build a `BaseRomIndex` once, set `NdzFlags.BasePatch`, write
the three base front-matter fields, and thread the per-block window search through
`CompressFrame`'s existing best/candidate loop (one more candidate family, same
swap pattern as filters) - plus a **second per-block header array**, `u32 baseOff[n]`,
written only when `BasePatch` is set (mirrors `Filters`' own mode-array conditionality -
see `NdzFlags.BasePatch`'s remarks). `frameBytes` layout becomes `[csize×n][mode×n if
Filters][baseOff×n if BasePatch][blocks...]`, exactly `ndztool.py`'s `index_hdr` order.

**Read-side API**: `NdzArchive.Open`/`OpenFile` gain an optional `byte[]? baseRom`
parameter, required whenever the opened file has `BasePatch` set (verified against the
front-matter's base fields immediately, same shape as today's dictionary-section
checks) - `NdzFrontMatter.Read`'s current unconditional-throw-on-`BasePatch` goes away,
replaced by this real support. `GetDecompressedFrame` reads the extra `baseOff` array
when present and, for any block with a non-sentinel offset, decompresses through a
window dictionary built from `baseRom[off..off+16384]` (same as a `Dict`-mode block,
just a differently-sourced one-shot dictionary) **regardless of that block's mode
byte** - a base-window hit is layered on top of the ordinary mode dispatch, matching
`decompress_v2_adv`'s own `if boff != SENTINEL: ... elif mode == DICT: ... else: ...`
priority order exactly.

**Scope note**: this is real, load-bearing new surface area (a second per-block header
array, a new required-sometimes constructor parameter on both `NdzWriter` and
`NdzArchive`), not a small addition. Implementing it well means also covering it with
the same rigor as the rest of this project: unit tests plus a real cross-check against
`ndztool.py --base` on real byte content, not just synthetic fixtures.

**Update 2026-08-31: §3 (pair container) is done - all three items in this plan are now
implemented.** `Ndz.Core.Format.NdzPairEntry` (the per-entry record) plus
`Ndz.Core.Compression.NdzPairWriter`/`NdzPairContainer` (write/read, mirroring the
`NdzWriter`/`NdzArchive` split), matching `ndztool.py`'s `--pair-out`/`read_pair_entries`
exactly. `NdzArchive.ReadInfo` is reused per-entry so `info` never needs to decode
either side just to summarize it. The CLI's `compress` gained `--pair-out` (needs
`--base`, mirroring `ndztool.py`'s own requirement), and `decompress`/`verify` gained
`--index` to pick which entry of a detected pair container to work with (auto-detected by
magic, default: the self-contained one).

Verified in both directions against a real `ndztool.py --pair-out` on real byte content:
our pair-container output's both entries decode via `ndztool.py unpack --index 0/1`
sha256-identical to their sources, and a real `ndztool.py --pair-out`-packed container's
both entries decode correctly through the CLI's `verify --index 0/1`. 120 tests passing
(6 new: `PairContainerTests`).

## 3. Pair container — done

**Confirmed format** (`ndztool.py`'s `cmd_pack`'s `--pair-out`/`read_pair_entries`):
an outer wrapper, not a `.ndz` variant - magic `NDZ_PAIR_MAGIC = 0x505A444E` ('NDZP'),
distinct from the single-file magic so `info`/`unpack` can tell them apart from byte 0
alone. Header (16 KiB, front-matter-size-aligned like everything else in this format):
`[magic][hdrSize=16384][nRoms=2][reserved]` (all u32) at offset 0, then per-entry
`[offset][size][origSize][gameCode]` (u32×3 + 4 bytes) at `0x10 + 0x10*i`. Entries
themselves are two **complete, independently-parseable** `.ndz` blobs back to back
(base first, then the base-patched target), each padded to a 16 KiB boundary. The base
entry is a normal, self-contained `.ndz` (no `BasePatch` flag); the second resolves its
base to the *first entry in the same container*, not an external file.

**New type**: `Ndz.Core.Format.NdzPairContainer` (or under `Compression` - it's a
container operation, not just a data record) - `Write(Stream, byte[] baseBlob, byte[]
patchedBlob, uint baseGameCode, uint baseOrigSize, uint patchedGameCode, uint
patchedOrigSize)` and a `Read`/`Open` that hands back both resolved `NdzArchive`s (the
base opened plain, the second opened with the first's *decompressed* bytes as its
`baseRom`). Genuinely depends on base-patch (§2) existing first - there's nothing to
pair otherwise - so this is naturally the last of the three to build.

**2026-09-06 update: generalized to N ROMs.** This section (and the design as originally
built) described exactly one base plus one target; `NdzPairWriter`/`NdzPairContainer`
were later generalized to a base plus any number of targets (a star topology, still one
shared base, never a chain) with no wire-format changes needed - see
`docs/ndz-format-spec.md`'s "Pair container format" section and the
`ndz-spec-provenance` memory for the full design/validation notes.

## 4. Hack container (`.delta.ndz`, flags bits 4+6) — library done, CLI/GUI not started

Reverse-engineered 2026-09-16 (not guessed) from real ndz-studio "Pack hack" output plus
its own `ndzcore.js` WASM glue - see `docs/ndz-format-spec.md`'s "xdelta-based `.delta.ndz`
/ hack container" section for the full format. Unlike §1-3 above, `ndztool.py` has no code
path for this at all, so there was no reference implementation to cross-check against -
verification instead used real ndz-studio-produced files directly (a real base `.ndz`, a
real `.delta.ndz`, both real ROMs - saved outside this repo at
`E:\source\git\NitroTwl\test_files`).

**Library implemented and verified**: `NdzFlags.HackContainer` (bit 6), `BlockMode.Verbatim`
(mode 7, verbatim-copy-from-an-explicit-base-offset), `Compression.HackContainerWriter`
(write, a separate type from `NdzWriter` mirroring the `NdzPairWriter` precedent), and
`Compression.NdzArchive.Open`'s new `baseNdzBytes` parameter (read - extends `NdzArchive` in
place rather than a new sibling type, since it's the same `NDZ1` envelope).
`NdzWriter.CompressBlockCandidates` was extracted from `NdzWriter.CompressFrame` so both
writers share the Plain/Dict/filter search rather than duplicating it - `NdzWriter`'s own
full test suite re-passed unchanged after that extraction, confirming it's a pure refactor.

Mode 7's exact-match search first reused a promoted `XDelta.HashChainMatcher` (pulled out of
`VcdiffEncoder`'s own private nested class) - functionally correct but far short of real
ndz-studio's own ratio on a full 256 MB ROM (a fixed-position hash search spends its budget
on unrelated data before reaching genuine matches). Fetching the real `ndzcore_bg.wasm` from
ndz-studio's own site and reading its unstripped Rust symbols showed the real packer uses
content-defined chunking instead (`ndzcore::census::cdc_chunks`, the same gear-hash algorithm
as `reference/mena-patchbench/ndztool.py`'s own retired `_cdc_chunks`) - so
`Compression.ContentDefinedChunker` + `Compression.ChunkRunMatcher` now do the primary
search (chunk both ROMs, index the base's chunks by content hash, stitch matched runs),
with `HashChainMatcher` kept as a fallback for whatever the chunk boundaries miss. See
`docs/ndz-format-spec.md`'s "Mode-7 search, corrected same day" note for the full story and
the real-sample numbers (3.23 MB vs. ndz-studio's 3.15 MB, up from an initial 38 MB).

Verified both directions against real files, not just synthetic fixtures: our reader
decodes the real ndz-studio-produced `.delta.ndz` byte-exact (SHA-256-identical) against
the real White ROM, and our own writer's output round-trips byte-exact against the real
Black/White ROMs too (not expected to be byte-identical to ndz-studio's own file - its
candidate-selection heuristics are its own - but correct, and now close in size too). See
`tests/Ndz.Core.Tests/HackContainerTests.cs`, `HashChainMatcherTests.cs`,
`ContentDefinedChunkerTests.cs`, `ChunkRunMatcherTests.cs`, and `HackContainerRealFileTests.cs`
(the real-file tests skip cleanly, not via a true xUnit skip, when
`E:\source\git\NitroTwl\test_files` isn't present - e.g. in CI). Independently confirmed on a
second real pair (Mega Man Star Force Dragon/Leo, 32 MB) added to the same fixture set.

**Two follow-on size optimizations, 2026-09-17** (see `docs/ndz-format-spec.md` for full
detail): (1) `NdzWriter.TrimZstdFrameHeader` shaves a real, deterministic 1-byte-per-block
zstd header overhead GrindCore has no public option to disable - applies project-wide
(ordinary `.ndz`/pair containers too, not just hack containers) since it's wired into the
shared `CompressBlockCandidates`/`TryFilterCandidate`; (2) `HackContainerWriter.SelectBestChunkMatcher`
auto-picks content-defined chunking's average chunk size per-ROM (tries several candidates,
keeps whichever resolves the most blocks) rather than a single fixed ~4 KiB default - never
serialized to disk, so no CLI flag needed. Combined real-world effect: Black/White dropped
from 3,231,906 to 3,180,697 bytes (was 38 MB before the original CDC fix), now only 43
Verbatim blocks short of ndz-studio's own 32,355/32,768; Dragon/Leo now *beats* ndz-studio's
own file by 6 bytes (957,081 vs. 957,087).

**CLI done too**: `ndz pack-hack <base.nds> <base.ndz> <out.delta.ndz> (--target <target.nds>
| --patch <patch.xdelta>)`, plus `--build-base` (packs `<base.nds>` into `<base.ndz>` in the
same invocation, with `compress`-equivalent `--raw-dict`/`--level`/`--block-size`/
`--max-dict` options, `auto` included) for the common case of not having the base already
packed. `decompress`/`verify` gained `--base-ndz <path>` (distinct from `--base <path>`,
which is for ordinary base-patch mode's raw `.nds` instead). Verified end-to-end against the
real Black/White ROMs via the CLI itself, both with a pre-existing base `.ndz` and with
`--build-base` building it fresh in the same command.

**GUI done too, 2026-09-17, then refined the same day from real usage**: a new
`HackTargetViewModel` per hack job, nested under a base `RomEntryViewModel` card in its own
`HackTargets` collection - deliberately separate from `Targets` (pair-container targets),
since a hack job always produces its own standalone output file, never merged with anything.
Reached via a dedicated "+ hack target" control (accepts a ROM or an `.xdelta` patch) rather
than the plain "+" strip, since unlike a plain drop - which always means a pair target -
there's genuine ambiguity a separate control resolves outright; dropping an `.xdelta` directly
onto a card is unambiguous on its own and is routed there automatically. Never gated by
`PairContainerPolicy.CreationEnabled` - that gate is about the pair-container format's own
unresolved multi-dictionary design, unrelated to hack containers.

Went through two more iterations from real usage before landing on the final design:

1. First cut gave each hack target a "Use existing / Build now" toggle, the latter opening a
   *second* save dialog (for the freshly-built base `.ndz`) right next to the "Pack hack"
   button's own save dialog for the delta - flagged as confusing (two saves reading as one
   action).
2. Second cut let the Pack tab accept a `.ndz` too (`RomEntryViewModel.IsPackedBase`), and made
   a hack target under a raw-ROM card browse for an already-packed `.ndz` instead of building
   one. Flagged again: browsing for a `.ndz` that doesn't exist yet is a dead end - there was no
   way to build the base and the hack together from just a raw ROM + a patch anymore, which was
   the actual point of the CLI's own `pack-hack --build-base`.

Landed on: `HackTargetViewModel` is now a **pure passive record** of what's attached - icon,
title, remove button, nothing else. Every hack container's building is *fully automatic* and
happens through the base card's own single action button
(`RomEntryViewModel.PackWithHacksAsync`), whose text/behavior adapts to what it actually does
(`PackButtonText`): plain "Pack" with no hacks attached (unchanged solo/pair behavior, one output
file); "Build base + N hacks" for a raw-ROM card with hacks attached (builds the base `.ndz`
first - named from the card's own title, using its own selected block/dictionary size - then
every hack's own `.delta.ndz`, named from each hack's own title, all into one chosen destination
*folder* rather than a single file, since this now writes more than one); "Build N hacks" for an
`IsPackedBase` card (skips straight to the hacks, reusing the card's own already-packed bytes -
`RomSource.RealFilePath` reuses the existing file directly when there is one, otherwise the
resolved bytes get written out alongside the deltas). One button, one action, always seamless -
mirrors the CLI's own `pack-hack --build-base` exactly, generalized to N hack targets in one go.
`AddTargetsAsync` (pair targets) still rejects `.ndz` - `NdzPairWriter` needs raw bytes and a
pair target's whole point is merging into one shared file, neither of which applies to something
already packed.

Drag-and-drop now also works on both "+" bubbles (Pack tab's hack-target strip, Examine's
attach-base strip), not just click-to-browse - the single window-level drop handler recognizes
a drop landing specifically inside either one (`Tag="HackTargetStrip"`/`Tag="AttachBaseStrip"`)
before falling through to its own more general routing (pair-target routing for the Pack tab,
plain source-add for Examine), the same technique already used for the "RomCard" pair-target
region.

On the Examine side, a `.delta.ndz` prompts for its base's own `.ndz` (`RequiresExternalBaseNdz`,
mutually exclusive with the existing `RequiresExternalBaseRom`), and the entry's summary/header
text calls out the hack-container-specific field semantics (repurposed `GameCode`,
dictionary-from-base) the same way `ndz info` already does. Also refined from the same
feedback round: the base prompt used to re-open a file picker on *every* Unpack/Checksums
click - real use called that tedious for something that never changes between clicks on the
same entry. Replaced with an attach-once model: a "+" bubble (`ExamineEntryViewModel.AttachExternalBase`)
becomes a small "Base: filename" chip with its own detach button once set, and Unpack/Checksums
just reuse it (`CanRunUnpack`/`CanRunChecksums`, gated on `IsBaseReady`) without prompting
again. `ExamineSourceViewModel.UnpackAllAsync` respects an already-attached base too, only
skipping (and calling out in its summary) entries that still need one.

Verified: full solution builds clean, the existing 284-test suite still passes unmodified, and
the app launches without a runtime XAML/binding error - the actual interactive click-through
still needs a human at the keyboard, since that's not something this environment can drive
itself.

## Suggested build order

1. ~~**Filter modes**~~ - done, see the update note above.
2. ~~**Base-patch**~~ - done, see the update note above.
3. ~~**Pair container**~~ - done, see the update note above.
4. ~~**Hack container**~~ - done: library, CLI, and GUI all implemented and verified - see §4 above.

All three original items in this plan are implemented and cross-verified against
`ndztool.py`. What's left there, per `docs/ndz-format-spec.md`'s "Open questions": only the
retired trained-dictionary flag (bit 2 - confirmed not worth implementing, nothing
produces it). `patchbench.py` itself was extracted directly into `ndztool.py` by a Claude
session with real access to it (see `reference/mena-patchbench/README.md`), so it isn't
treated as a separate missing source anymore. The hack container (§4) is a separate,
later addition with its own remaining CLI/GUI work.

Each step: implement, unit-test, then cross-check against a real `ndztool.py` run in an
isolated venv on real byte content (not just synthetic fixtures) in both directions -
the same methodology that caught the `Filters`-flag conformance bug - before moving to
the next step, not batched at the end.
