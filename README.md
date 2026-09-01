# NDZ

Compresses decrypted Nintendo DS ROMs (`.nds`) into `.ndz`: a seekable ZStandard
container with 128 KB frames (each subdivided into per-file-variable-size blocks,
8 KB by default) and an external seek table, built on
[GrindCore](https://www.nuget.org/packages/GrindCore) (`Nanook.GrindCore.ZStd`).

Because every frame decompresses independently, reading any byte range only requires
decompressing the frame(s) it falls in — not the whole ROM. Verified against a real,
independent third-party tool (`ndztool.py`), not just internally: this port can pack a
real ROM and have it read correctly by real tooling, and can read a real `.ndz` file
produced by that tooling, filter-mode and base-patch blocks included. See
[`docs/ndz-format-spec.md`](docs/ndz-format-spec.md) for the on-disk format, full
implementation status, and provenance, and
[`docs/ndz-remaining-work.md`](docs/ndz-remaining-work.md) for the plan on what's next.

Implemented: compression, random-access decompression, raw-content dictionary support
(including for dictionaries past zstd's implicit ~8 MiB window default), all five
per-block byte-transform filter modes, and base-ROM patch mode (windowed dictionary
compression against a base ROM, not a binary diff). Understood but not yet implemented:
the pair-container format for shipping a base+patch pair together in one file.

## Usage

```
ndz compress <in.nds> <out.ndz> [--level 1-19] [--block-size N]
                                 [--no-filters] [--dict <file>] [--base <base.nds>]
                                                  Compress a decrypted .nds into .ndz.
                                                  --dict primes compression with a raw
                                                  content dictionary. --base patches
                                                  against a second, already-decrypted
                                                  .nds. --level and --block-size (default
                                                  8192, power of two) are capped at the
                                                  target hardware's decode-speed limits -
                                                  see "Hardware limits" below.
ndz decompress <in.ndz> <out.nds> [--base <base.nds>]
                                                  Reconstruct the original .nds. --base is
                                                  required if the file used base-patch.
ndz info <in.ndz>                                Print front-matter and seek-table summary
                                                  (never needs --base, even for a
                                                  base-patch file).
ndz verify <in.ndz> <in.nds> [--base <base.nds>]  Decompress and byte-compare against the original.
```

### Hardware limits

Compression level (max 19) and block size (max 8192 bytes) are capped, not just
defaulted: the real target hardware (DSPico) decodes on the fly while the console
waits on a cart read, so a higher level or a bigger block would produce a file that
packs fine and then fails - or stalls - on real hardware. `NdzWriter.Compress` throws
rather than silently accept either past its limit, matching `ndztool.py`'s own refusal
(`NDZ_MAX_LEVEL`/`NDZ_MAX_BLOCK_SIZE`).

ROMs should be decrypted first — NDZ compresses raw bytes as-is and does no
cryptographic work of its own.

## Building

```
dotnet build NDZ.slnx
dotnet test NDZ.slnx
```

## Layout

- `src/Ndz.Core` — format types (`NdzFrontMatter`, `NdzFlags`, ...) and the
  compressor/reader (`NdzWriter`, `NdzArchive`).
- `src/Ndz.Cli` — the `ndz` command-line tool.
- `tests/Ndz.Core.Tests` — round-trip, random-access, and format-validation tests
  against synthetic ROM fixtures.
