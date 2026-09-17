# xdelta3 / VCDIFF — compatibility notes and open questions

**Update 2026-09-16: a real, from-scratch VCDIFF+DJW codec is now implemented and wired in**,
on branch `xdelta/vcdiff-codec` (kept off `master` until the container-format question below
is resolved) — `src/Ndz.Core/XDelta/` (apply + generate, RFC 3284 default code table, address
cache, Adler32) and `src/Ndz.Core/XDelta/Djw/` (the full DJW secondary compressor: decode and
the complete multi-group adaptive encoder, not a cut-down version). `XDeltaCodec.Apply`/
`Generate` are the public entry points; `ndz patch-apply`/`ndz patch-make` are the CLI
commands. Confirmed **byte-exact against a real `xdelta3.exe` 3.2.0 build in both directions,
with real DJW (Static Huffman) secondary compression genuinely exercised on both sides**:

- Our encoder (with our own from-scratch DJW compressor) → real `xdelta3.exe -d` applies it
  correctly; `xdelta3 printhdr` confirms it reads `VCDIFF secondary compressor: Static Huffman`
  and `VCD_DATACOMP` on our own output.
- Real `xdelta3.exe -e -S djw` → our decoder applies it correctly, including correctly parsing
  xdelta3's `VCD_APPHEADER` extension (the filename/hash block it embeds) to skip past it.
- Also confirmed in the plain (no secondary compression) case, both directions, on a real
  4 MiB synthetic ROM-hack-shaped diff.

The DJW implementation was built from real source, not guessed: the exact algorithm/constants
from `xdelta3/xdelta3-djw.h` (tag v3.2.0, `jmacd/xdelta`), the general canonical-Huffman
construction technique (heap-based build with length-limiting retry, same tie-break rule) cross-
checked against GrindCore's own vendored real bzip2 1.0.8 source
(`external/bzip2/bzip2/huffman.c`, `BZ2_hbMakeCodeLengths`/`BZ2_hbAssignCodes` — DJW's own
header credits bzip2 for this technique), and the wire-level detail that a secondary-compressed
VCDIFF section is `[varint: decompressed size][DJW bitstream]` — confirmed from
`xdelta3-second.h`'s `xd3_encode_secondary`/`xd3_decode_secondary`, not part of DJW's own window
format. See `src/Ndz.Core/XDelta/Djw/DjwCodec.cs`'s own remarks for the full trace.

**Corrected from the original 2026-09-15 assumption below**: the user confirmed the DS Pico
cart itself decodes VCDIFF/xdelta live, on the fly, while playing — this is not a purely
PC-side preprocessing step (see "Open question" below, still open: the on-cart
`.delta.ndz`/`xdelta.ndz` container/indexing format that makes that possible over what's
fundamentally a sequential format is unconfirmed, blocked on a real reference from Mena, and
explicitly out of scope for what's built so far). What *is* now real: applying/generating
standalone `.xdelta`/VCDIFF patches — useful for both ROM hacks and version diffs — works
correctly and interoperates with the real tooling.

**Update 2026-09-17: real multi-window encoding, a real DJW bitstream bug found and fixed,
and GUI wiring.** The encoder originally emitted one VCDIFF window covering the entire
target, which real `xdelta3.exe` silently accepted for small test files but **rejected
outright on a real 256 MB target** (`hard window size exceeded: XD3_INVALID_INPUT`) — caught
specifically by cross-checking our own output against real `xdelta3.exe`, not just our own
decoder, per the project's own standing "don't confirm your own bias" practice. Root cause,
confirmed by reading the real xdelta3 3.2.0 source (not guessed): `XD3_HARDMAXWINSIZE` (64
MiB, `xdelta3.h`) is checked **only against the target window length** (`dec_tgtlen` in
`xdelta3-decode.h`), never against the source segment length — an earlier attempt that also
capped each window's source-segment span caused a severe multi-minute encode regression
before this was confirmed and removed. `VcdiffEncoder` now splits the target into real
8 MiB (`TargetWindowSize`) windows, each with its own tightly-bounded source segment and
encoded independently (parallelized via `Parallel.For`, `HashChainMatcher`'s hash-table
precompute pass parallelized too).

Windowing surfaced a second, previously-undetected bug: some windows' DJW-compressed
sections failed to decode (both our own decoder and real `xdelta3.exe` rejected them) once
sections got small/low-diversity enough. Root cause: `Djw/DjwCodec.cs`'s ported
`BuildInitialPartition` can reduce its requested group count all the way down to 1 (real
xdelta3's own `goto regroup` retry behavior, confirmed from `xdelta3-djw.h`), but the port
was missing the corresponding re-check that routes to the real single-group encode path (no
sector-size field, no selector stream) when that happens — a real bitstream desync, not a
windowing bug per se, just never exercised before windowing produced small enough sections to
trigger it. Fixed in `DjwCodec`; regression-tested directly (`DjwCodecTests.cs`) plus a 90-case
fuzz sweep across every `ChooseGroupsAndSectorSize` length-tier boundary crossed with several
byte-diversity shapes, looking for (and not finding) a sibling bug in the same area.

Also wired into the GUI the same day: a "Create .xdelta patch(es)" button on a packed/raw ROM
card (visible once at least one direct-ROM hack target is attached) generates a standalone
`.xdelta` per target via `XDeltaCodec.Generate`, with a round-trip verify against
`XDeltaCodec.Apply` before reporting success — plus an in-app Help view (`HelpWindow`).

**Update 2026-09-16 (later the same day): the "Open question" below is now fully answered**,
by reverse-engineering three real files a real `ndz-studio` "Pack hack" run produced, plus
reading the real `ndzcore.js` wasm-bindgen glue the site itself loads (not guessed) — see
`docs/ndz-format-spec.md`'s "xdelta-based `.delta.ndz` / hack container" section for the full
writeup, now a complete, byte-exact-verified result (all 32,768 blocks of a real 256 MB
sample reconstruct correctly). Short version: **the on-cart `.delta.ndz` does not contain
VCDIFF/xdelta data at all.** The disqualifying concern in option 2 below (VCDIFF is
sequential, can't support the hardware's random-access block reads) turned out to be exactly
right, and real `xdelta3` is only ever used PC-side, to reconstruct the full target ROM from
base + patch before packing — the same way option 1 describes, just as an internal
implementation step of "Pack hack" rather than the only way to use it. The actual on-disk
container is a *new variant* of the existing random-access-friendly block/frame mechanism:
whole 8 KiB blocks are either copied verbatim from an explicit offset in the base (mode 7,
confirmed to handle real content relocation, not just same-position matches) or
zstd-compressed against the *base `.ndz`'s own embedded raw-dict section* (mode 0) - the
missing piece turned out to be that the packer takes the base's already-packed `.ndz` as an
input, not just the raw base ROM, and reuses its dictionary rather than deriving a new one.

---

Status (original, 2026-09-15): **investigation complete, nothing wired in.** Written after the
user asked whether xdelta3 patch generation/reading could become an alternative (not yet
decided how — see "Open question" below) to NDZ's current multi-game packaging. The
investigation itself happened in a sibling project, Gog.Net (an unofficial GOG.com client
library), which needed to apply real GOG-issued xdelta3 patches; this doc generalizes what was
confirmed there since none of it is GOG-specific. See `reference/xdelta-csharp/` for the actual
working code this produced — detached, not referenced by `NDZ.slnx` or anything under `src/`
(now superseded by the real implementation above, but left as historical reference).

## Open question — what role does xdelta3 actually play here? Not yet decided.

Do not assume either answer below and start building against it. The user is waiting on
confirmation from Mena (the NDZ format's author) before committing to a direction:

1. **Standalone patch files, separate from `.ndz`.** Generate/apply plain `.vcdiff`/`.xdelta`
   patches between two ROMs (e.g. a Pearl→Diamond patch) for distribution outside the
   flashcart pipeline entirely — closer to how ROM-hacking communities already distribute
   xdelta patches. Never touches the on-cart random-access hot path, so the hardware
   constraint in the next section doesn't apply.
2. **Replace base-patch mode (flags bit 4) inside `.ndz` files that run on real hardware.**
   This looks like the "obvious" fit on paper, but there's a real, likely-disqualifying
   mismatch: `ndz-format-spec.md`'s own history already records that a bsdiff/VCDIFF-style
   binary diff was the *original guess* for base-patch mode, and it was **confirmed wrong**
   — the real mechanism (`ndztool.py`'s `BaseCtx`) is windowed raw-dictionary zstd
   compression, specifically because the target hardware (a DSPico flashcart) decodes
   on-the-fly, in random-access 8–32KB blocks, while the console is blocked waiting on a cart
   read (`NdzArchive.ReadAt(offset, buffer)` only ever decompresses the block(s) actually
   touched). VCDIFF/xdelta3 is fundamentally a **sequential, whole-file-apply** format — there
   is no way to jump to an arbitrary output byte range without replaying the relevant window
   chain from wherever it starts. That's not a small wrinkle; it's the same reason the format
   author's own reference tool doesn't use VCDIFF for this despite it being the more obvious
   textbook answer.

Everything below is written to be useful for either answer (or for just knowing the
territory), but no design decision here should be read as picking one.

## What's confirmed, with evidence

All of this comes from round-tripping real files against a real `xdelta3.exe` 3.2.0 reference
build (`E:\source\xdelta3-3.2.0-windows-x86_64`, the genuine `jmacd/xdelta` project — confirmed
via its GitHub releases page, not a fork) and reading the `VCDiff` NuGet package's
(`SnowflakePowered/vcdiff`) actual source, not guessing from either project's docs alone.

### Container basics

- Magic: `0xD6 0xC3 0xC4` (`'V'|0x80, 'C'|0x80, 'D'|0x80` per RFC 3284), then a version byte
  (`0x00` in every real sample seen). **A secondhand AI-generated description the user
  encountered while researching this claimed the magic was ASCII "DEL" (`0x44 0x45 0x4C`) —
  confirmed wrong** against both real GOG patches and this project's own generated test files.
  Don't trust that source for anything else either; its accompanying skeleton reader also
  never accounted for the Adler32 checksum field (see below), which would misalign it by 4
  bytes on window 1 of any real-world file.
- Integers are RFC 3284's base-128 variable-length encoding: each byte's high bit (0x80) is a
  continuation flag (more bytes follow if set), the low 7 bits are payload, most-significant
  chunk first. This part of the AI-generated description was accurate. The wire format itself
  has no size ceiling — but see "Confirmed pitfalls" below for where an actual implementation
  (this one) imposes one anyway.
- Per-window checksum mechanism is Adler32 (`VCD_ADLER32` win-indicator bit) — confirmed
  identical between this library's encoder and real xdelta3 for the same content
  (`0D7870F7` both ways on a test file). This is the *only* real per-window integrity
  mechanism; it has nothing to do with the BLAKE3 item below.
- A working, required-for-correct-decode detail neither this doc nor casual descriptions
  dwell on: RFC 3284's default code table's **address cache** (near/same-mode slots) for COPY
  instructions. Already correctly implemented in the library tested here — confirmed
  implicitly by every successful multi-window real-file decode during this investigation, not
  separately unit-tested.

### Secondary compression: DJW unsupported, LZMA works, FGK untested

xdelta3 supports up to three optional secondary compressors layered under the main VCDIFF
window structure. Real xdelta3's own `config` output for the build tested here:
`SECONDARY_DJW=1 SECONDARY_FGK=0 SECONDARY_LZMA=1`.

- **LZMA**: the `VCDiff` package decodes it correctly — confirmed against a real `-S lzma`
  file and against this xdelta3 build's own default behavior (it applies LZMA automatically
  even without `-S`, a newer default this specific 3.2.0 line has that classic xdelta3
  doesn't).
- **DJW (static Huffman)**: confirmed **not supported** — decoding a real `-S djw` file throws
  `NotSupportedException`. Fails loudly, not silently; `XDeltaCodec.Apply` in this repo wraps
  it as `XDeltaException`.
- **FGK**: wasn't even compiled into the reference xdelta3 build used for this investigation
  (`SECONDARY_FGK=0`), so it's untested here and not otherwise known to be used by any
  real-world encoder. Treat as unsupported until proven otherwise.
- None of this matters if NDZ only ever needs to decode patches it generated itself: the
  `VCDiff` package's own **encoder never produces secondary-compressed output at all** —
  confirmed by reading `WindowEncoder.cs`'s source, which hardcodes the delta-indicator byte
  to "uncompressed" with a comment acknowledging the feature isn't implemented on the write
  side. It can *decode* secondary-compressed input; it can't *produce* it.

### BLAKE3 "armor" — a real, brand-new xdelta3 feature, irrelevant to VCDIFF's own integrity mechanism

`xdelta3 -h` on the 3.2.0 build reveals `-a` ("armor (whole-file BLAKE3 verification, on by
default; requires a seekable source)"). Confirmed genuine and new (not a fork oddity) by
checking `jmacd/xdelta`'s real v3.2.0 release notes directly on GitHub: "Add armor mode: BLAKE3
whole-file verification via app-header." It works by embedding a whole-file BLAKE3 hash as
text inside a richer, xdelta3-CLI-specific app-header format (`filename#hash//filename#hash/`,
183 bytes in a test case here, vs. a plain 73-byte `md5//md5/` convention GOG's own patches
use). It has nothing to do with VCDIFF's own per-window Adler32 checksum and isn't part of RFC
3284 — it's xdelta3-CLI-level, version-specific behavior layered on top. The `VCDiff` decoder
handles a richer/different app-header shape than expected just fine either way, since the app
header is opaque application metadata it never needs to interpret to decode correctly.

### Confirmed pitfalls: integer-overflow-adjacent bugs, not guesses

This is the section most worth internalizing if xdelta3 support ever gets built here for real,
since NDZ (unlike a GOG patch) isn't bound by xdelta3's own conservative window-size defaults
and could plausibly be configured into this territory without realizing it.

- **Decode side**: `WindowDecoder.cs` parses per-window fields (source segment length/offset,
  target window length, section lengths) via a 32-bit-capped varint parse path
  (`VarIntBE.ParseInt32`), even though the same codebase has a `ParseInt64` helper that just
  isn't wired to these fields, and even though the wire format itself is unbounded. Confirmed
  by reading the source directly, not inferred from behavior.
- **Encode side, worse because it's silent rather than a clean error**:
  `VcEncoder`'s `maxBufferSize` constructor parameter (MiB) is multiplied out in 32-bit
  arithmetic — `bufferSize = maxBufferSize * 1024 * 1024` — and `2048 * 1024 * 1024`
  overflows a signed 32-bit int to exactly `int.MinValue`. Confirmed by computing it directly.
  Independently, `WindowEncoder.Output()` writes the *entire source/dictionary file's length*
  (never chunked — the whole file is loaded into memory as one unit) via an unchecked
  `(int)dictionarySize` cast with no bounds check, meaning a source file at or beyond 2GB has
  its recorded size silently truncated regardless of how conservatively `maxBufferSize` is
  set.
- **The exact, confirmed rule**: never configure a window/chunk size at or above 2048 MiB, and
  never encode against a source/dictionary file at or beyond 2GB, with this library. Below
  those, everything tested here worked correctly.
- **This is specific to the `VCDiff` C# port, not inherent to VCDIFF or to xdelta3 in
  general** — real xdelta3's own C implementation has genuine 64-bit internal offsets
  (`xdelta3 config` shows `XD3_USE_LARGEFILE64=1`, `sizeof(xoff_t)=8`), and its own v3.2.0
  release notes list "Harden allocation sizing against integer overflow" and "Clamp the
  source window size" as real, recent fixes to the reference implementation itself — confirms
  this class of bug is a live, known concern in this exact space, not something this
  investigation is overthinking.
- `reference/xdelta-csharp/XDeltaCodec.cs`'s `Generate` method enforces this with an explicit
  `ArgumentOutOfRangeException` (`MaxSafeSize`, set conservatively under the exact 2^31
  boundary) rather than letting either bug fire.

### The SharpCompress collision — likely blocks using the `VCDiff` package as-is here

**This is probably the deciding factor against pulling the off-the-shelf `VCDiff` NuGet
package directly into `Ndz.Core`, separate from the "which use case" question above.**

`VCDiff` depends on plain `SharpCompress` 0.46.3. `Ndz.Core` already depends on
`GrindCore.SharpCompress` 0.50.5 — confirmed by inspecting both DLLs' metadata directly to be
a **different assembly identity** (`SharpCompress` vs `GrindCore.SharpCompress`), not a
different version of the same package, despite exposing an **identical public namespace**
(`SharpCompress.*` on both — `GrindCore.SharpCompress` is a deliberate source-compatible fork
by the same authors, routing codecs through `Nanook.GrindCore` for native performance, built
to namespace-match for easy migration, not to be a binary drop-in).

Practical effect: if `Ndz.Core` referenced `VCDiff` directly while also using
`GrindCore.SharpCompress` types directly (which is presumably the entire reason it depends on
that package), the C# compiler would hit `CS0433` — an ambiguous type existing in both
assemblies — the moment any shared type name is touched in `Ndz.Core`'s own code. That's a
hard build break, not a warning, and not something NuGet's normal version-unification handles,
since these are two different package IDs. The standard fix (`extern alias`) works but is a
real, easy-to-forget maintenance wart. See `reference/xdelta-csharp/README.md` for the full
detail and the realistic paths forward (fork/vendor just the needed pieces of `VCDiff` and
drop its `SharpCompress` dependency, `extern alias`, or a genuinely from-scratch
implementation routing any secondary-compression need through `Nanook.GrindCore` directly).

## Reference materials

- `reference/xdelta-csharp/` — the actual verified C# code (apply + generate, with the
  overflow guardrails above enforced), detached and not wired into the real build. Its own
  README covers the SharpCompress collision in full.
- `E:\source\xdelta3-3.2.0-windows-x86_64` (outside this repo, user-supplied) — the real
  reference `xdelta3.exe`/`xdelta3decode.exe` this investigation was verified against.
  `xdelta3 config`, `xdelta3 printhdr`/`printdelta`, and `xdelta3 -h` are the fastest way to
  re-confirm anything here directly rather than trusting this doc secondhand.
- `github.com/jmacd/xdelta` at tag `v3.2.0` — the genuine upstream project; its release notes
  page is what confirmed BLAKE3 armor and the overflow-hardening changes are real, current,
  official features/fixes, not fork-specific behavior.
- `github.com/SnowflakePowered/vcdiff` — the `VCDiff` NuGet package's real source, read
  directly (`WindowDecoder.cs`, `VarIntBE.cs`, `VcEncoder.cs`, `WindowEncoder.cs`) for every
  claim in this doc about what it does or doesn't support.
