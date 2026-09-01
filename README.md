# NDZ

Compresses decrypted Nintendo DS ROMs (`.nds`) into `.ndz`: a seekable ZStandard
container with 128 KB frames (each subdivided into per-file-variable-size blocks,
8 KB by default) and an external seek table, built on
[GrindCore](https://www.nuget.org/packages/GrindCore) (`Nanook.GrindCore.ZStd`).

Because every frame decompresses independently, reading any byte range only requires
decompressing the frame(s) it falls in — not the whole ROM. Verified against a real,
independent third-party tool (`ndztool.py`), not just internally: this port can pack a
real ROM and have it read correctly by real tooling, and can read a real `.ndz` file (or
pair container) produced by that tooling, filter-mode and base-patch blocks included. See
[`docs/ndz-format-spec.md`](docs/ndz-format-spec.md) for the on-disk format, full
implementation status, and provenance, and
[`docs/ndz-remaining-work.md`](docs/ndz-remaining-work.md) for the build history.

Implemented: compression, random-access decompression, raw-content dictionary support
(including for dictionaries past zstd's implicit ~8 MiB window default), all five
per-block byte-transform filter modes, base-ROM patch mode (windowed dictionary
compression against a base ROM, not a binary diff), and the pair-container format for
shipping a base+patch pair together in one self-contained file. The only thing left
unimplemented is a retired, never-produced trained-dictionary flag not worth building.

## Usage

```
ndz compress <in.nds> <out.ndz> [--level 1-19] [--block-size N]
                                 [--no-filters] [--raw-dict <size>] [--base <base.nds>]
                                                  Compress a decrypted .nds into .ndz.
                                                  --raw-dict (e.g. 8m, 512k) derives a
                                                  dictionary from this ROM's own repeated
                                                  content, up to that size - there's no
                                                  option to load externally-supplied
                                                  dictionary content, because neither
                                                  reference implementation has one either.
                                                  --base patches against a second,
                                                  already-decrypted .nds. --level and
                                                  --block-size (default 8192, power of
                                                  two) are capped at the target
                                                  hardware's decode-speed limits - see
                                                  "Hardware limits" below.
ndz compress <in.nds> --pair-out <pair.ndz> --base <base.nds>
                                                  Pack a base + base-patched pair into one
                                                  self-contained file.
ndz decompress <in.ndz> <out.nds> [--base <base.nds>] [--index N]
                                                  Reconstruct the original .nds. --base is
                                                  required if the file used base-patch
                                                  (not for a pair container, which carries
                                                  its own). --index picks which ROM to
                                                  extract from a pair container.
ndz info <in.ndz>                                Print front-matter and seek-table summary,
                                                  or (for a pair container) both entries' -
                                                  never needs --base either way.
ndz verify <in.ndz> <in.nds> [--base <base.nds>] [--index N]
                                                  Decompress and byte-compare against the original.
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
