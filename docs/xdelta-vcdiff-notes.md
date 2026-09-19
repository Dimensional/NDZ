# xdelta3 / VCDIFF codec

`Ndz.Core` includes a from-scratch VCDIFF (RFC 3284) codec, used for two distinct
features:

- **Standalone `.xdelta` patches** between two ROMs (`ndz patch-make`/`patch-apply`),
  for distributing a version diff or ROM hack outside the `.ndz` pipeline entirely.
- **Reconstructing a hack container's target ROM** from a base ROM plus a patch, as a
  PC-side preprocessing step before packing a `.delta.ndz` (`ndz pack-hack --patch`) —
  see `docs/ndz-format-spec.md`'s "Hack container format" for the on-disk container
  itself, which does **not** embed VCDIFF/xdelta data.

The codec is implemented natively (`src/Ndz.Core/XDelta/`, `src/Ndz.Core/XDelta/Djw/`),
not via the third-party `VCDiff` NuGet package — see "Why not the `VCDiff` package"
below. It is confirmed byte-exact, bidirectionally, against a real `xdelta3.exe` 3.2.0
build, including real DJW secondary compression exercised on both sides.

## What this codec implements

- Full VCDIFF apply and generate (RFC 3284): the default code table, address cache
  (near/same-mode slots for COPY instructions), and per-window Adler32 checksums.
- Multi-window encoding: a target is split into 8 MiB windows, each independently
  encoded with its own bounded source segment — required because a single window
  covering an entire large target exceeds a real decoder's maximum window size.
- The complete DJW secondary compressor (decode and the full multi-group adaptive
  encoder, not a simplified variant), ported from xdelta3's own source and
  cross-checked against GrindCore's vendored bzip2 implementation for the shared
  canonical-Huffman construction technique the two formats have in common.

`XDeltaCodec.Apply`/`Generate` are the public entry points; `ndz patch-apply`/
`patch-make` are the CLI commands.

## VCDIFF wire-format facts this implementation relies on

- Magic: `0xD6 0xC3 0xC4` (`'V'|0x80`, `'C'|0x80`, `'D'|0x80` per RFC 3284), followed by
  a version byte.
- Integers use RFC 3284's base-128 variable-length encoding: each byte's high bit is a
  continuation flag, the low 7 bits are payload, most-significant chunk first. The wire
  format itself has no size ceiling.
- A secondary-compressed VCDIFF section is `[varint: decompressed size][DJW bitstream]`
  — a detail of how a section is framed, not part of DJW's own window format.
- xdelta3 also supports an optional whole-file BLAKE3 "armor" mode (an app-header
  convention, not part of RFC 3284 itself) and LZMA/FGK as alternative secondary
  compressors; this codec implements DJW only, matching what the format author's own
  web-based packing tool produces.

## Why not the `VCDiff` NuGet package

Two independent reasons ruled out `SnowflakePowered/vcdiff`:

1. It depends on plain `SharpCompress`, a different assembly identity than `Ndz.Core`'s
   own `GrindCore.SharpCompress` despite both exposing an identical public namespace —
   referencing both directly causes a hard, unresolvable ambiguous-type build error.
2. Real bugs in the package itself: its encoder never produces secondary-compressed
   output at all; its decoder parses some fields through a 32-bit-capped path even
   though the wire format is unbounded; and its encoder has integer-overflow-adjacent
   bugs in its buffer-size and source-length handling that can silently truncate data
   for large inputs.

Full detail on this investigation, including the exact overflow arithmetic and a
verified-but-detached proof-of-concept, is preserved in project memory rather than here,
since it concerns a package this project no longer depends on at all.
