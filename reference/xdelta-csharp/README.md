# xdelta-csharp (detached reference material)

Shared 2026-09-15, written during a Gog.Net investigation into whether GOG's real xdelta3
patches could be applied from pure managed C# (they can — see
`[technomage6/GOG.net]/GOG.net/src/Gog.Net/Download/XDeltaPatchApplier.cs` for the
GOG-specific version this was generalized from). Placed here because the user asked to have
something ready to build on once Mena confirms what role, if any, xdelta3/VCDIFF plays in
NDZ — see `docs/xdelta-vcdiff-notes.md` for the open question and why it isn't resolved yet.

**Not wired into NDZ's real pipeline.** Not referenced by `NDZ.slnx`, `Ndz.Core`, `Ndz.Cli`,
or `Ndz.Gui`. Builds and runs standalone (`dotnet build`/`dotnet test` from this folder) but
is otherwise inert — nothing in the real product calls into it.

## What's here

- `XDeltaCodec.cs` — apply (`Apply`/`ApplyToFile`) and generate (`Generate`/`GenerateToFile`)
  wrappers over the `VCDiff` NuGet package (`SnowflakePowered/vcdiff` — pure managed C#, no
  native binaries). Every claim in its XML doc comments is backed by an actual test run
  against a real `xdelta3.exe` 3.2.0 reference build during this investigation, not assumed —
  see `docs/xdelta-vcdiff-notes.md` for the full methodology and evidence.
- `XDelta.Support.csproj` — a standalone class library, `net10.0` (matching `Ndz.Core`'s own
  TFM), with just the one `VCDiff` package reference.

## Why this likely can't just be dropped into `Ndz.Core` as-is

**`VCDiff` depends on plain `SharpCompress` 0.46.3. `Ndz.Core` already depends on
`GrindCore.SharpCompress` 0.50.5 — a different assembly identity, not a version of the same
package.** Confirmed 2026-09-15 by inspecting both DLLs' metadata directly:

| | Assembly identity | Namespace surface |
|---|---|---|
| plain `SharpCompress` (pulled in by `VCDiff`) | `SharpCompress, Version=0.46.3.0` | `SharpCompress.*` |
| `GrindCore.SharpCompress` (`Ndz.Core`'s own) | `GrindCore.SharpCompress, Version=0.50.5.0` | `SharpCompress.*` (identical) |

`GrindCore.SharpCompress` is a *source-compatible fork* of SharpCompress (same authors,
Adam Hathcock + Nanook, same public namespace on purpose, so existing code can migrate by
swapping the package reference) that routes its codecs through `Nanook.GrindCore` for native
performance — but it ships under its own assembly name, not as a literal binary replacement
for `SharpCompress.dll`.

That distinction matters concretely: `VCDiff.dll` was compiled against plain `SharpCompress`
and resolves fine on its own at runtime regardless of what else is loaded — no crash. But if
`Ndz.Core` referenced `VCDiff` directly (pulling in plain `SharpCompress` transitively) while
also referencing `GrindCore.SharpCompress` directly and using any `SharpCompress.*` type in
its own code (which is presumably the entire reason it depends on `GrindCore.SharpCompress`
at all), the C# compiler hits `CS0433`: the type exists in both assemblies. That's a hard
build break, not a warning, and the standard fix (`extern alias`) is real but painful and easy
to let rot. This isn't avoidable by only using `VCDiff`'s plain-decode path and never its
LZMA secondary-decompression feature — the assembly-level dependency on plain `SharpCompress`
is unconditional, baked into `VCDiff.dll` regardless of which of its own features actually get
called.

**Practical implication, matching what the user anticipated when asking for this check:** if
xdelta3/VCDIFF support ever gets built for real into `Ndz.Core`, doing it by directly
referencing the off-the-shelf `VCDiff` NuGet package is probably not the move, specifically
because of this SharpCompress collision — not because of any correctness problem with
`VCDiff` itself (see `docs/xdelta-vcdiff-notes.md`; everything else about it checked out
against a real reference `xdelta3.exe`). The realistic paths, roughly in order of effort:

1. **Fork/vendor just the pieces of `VCDiff` actually needed**, dropping its LZMA secondary-
   decompression path (and its `SharpCompress` dependency with it) entirely. Worth checking
   first whether NDZ's own use case even needs to *decode* externally-authored,
   secondary-compressed xdelta3 files at all — if NDZ only ever needs to decode patches its
   own encoder produced, this is moot, since `VCDiff`'s own encoder (confirmed by reading
   `WindowEncoder.cs`) never produces secondary-compressed output in the first place.
2. **`extern alias`** to keep both `VCDiff` (transitively, plain `SharpCompress`) and
   `GrindCore.SharpCompress` side by side. Works, but a real, easy-to-forget maintenance wart
   for a project that otherwise doesn't need it anywhere else.
3. **A genuinely from-scratch VCDIFF/xdelta3 implementation**, reusing what this
   investigation already confirmed (magic bytes, header/window layout, the varint scheme, the
   address-cache requirement, the real overflow pitfalls to avoid) without pulling in
   `VCDiff`'s dependency chain at all — routing any secondary-compression need through
   `Nanook.GrindCore` directly, consistent with the rest of `Ndz.Core`.

This repo isn't the place that decision gets made — that's for whenever xdelta3's actual role
in NDZ is confirmed. This is just the working, verified reference to build from either way.

## What's confirmed to work, and what isn't

See `docs/xdelta-vcdiff-notes.md` for the full writeup with evidence. Short version, also in
`XDeltaCodec`'s own XML doc comments:

- Decode: plain VCDIFF and LZMA-secondary-compressed windows both work, cross-verified
  against real `xdelta3.exe` output in both directions, including a real 107MB/7-window file.
- Decode: DJW (static Huffman) secondary compression throws `NotSupportedException` (a real,
  confirmed gap, not a guess) — this wrapper surfaces it as `XDeltaException` rather than
  letting it escape raw. FGK is untested (not even compiled into the reference xdelta3 build
  used for testing) — assume unsupported.
- Encode: produces valid output the real `xdelta3.exe` applies correctly (confirmed both
  directions), including matching Adler32 checksums byte-for-byte. Never produces secondary-
  compressed, custom-code-table, or app-header output itself, even though it can *decode*
  files that use those features.
- **Hard, confirmed ceiling: stay well under 2 GiB for any single window or for a source/
  dictionary file.** Two independent, confirmed integer-overflow-adjacent bugs converge on
  this exact number — see `XDeltaCodec`'s XML doc comments and `docs/xdelta-vcdiff-notes.md`
  for the precise mechanism. `XDeltaCodec.Generate` enforces this with an explicit
  `ArgumentOutOfRangeException` rather than letting either bug fire silently. Not a concern
  for NDS ROMs specifically (well under 1GB even DSi-enhanced), but real and worth carrying
  forward into whatever replaces this.
- **Not evaluated**: whether VCDIFF's inherently sequential, whole-file-apply model is even
  the right shape for NDZ's real hardware constraint (on-the-fly, bounded-latency random-
  access block decode on a DSPico flashcart) — that's *why* NDZ's existing base-patch mode
  uses windowed dictionary compression instead of a diff algorithm in the first place. This
  material is aimed at a standalone/external patch use case, not at replacing that mode,
  unless/until Mena confirms otherwise.
