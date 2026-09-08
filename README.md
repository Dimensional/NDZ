# NDZ

Compresses decrypted Nintendo DS ROMs (`.nds`) into `.ndz`: a seekable ZStandard
container with 128 KB frames by default (each subdivided into per-file-variable-size
blocks, 8 KB by default) and an external seek table, built on
[GrindCore](https://www.nuget.org/packages/GrindCore) (`Nanook.GrindCore.ZStd`).

Because every frame decompresses independently, reading any byte range only requires
decompressing the frame(s) it falls in — not the whole ROM. Verified against a real,
independent third-party tool (`ndztool.py`), not just internally: this port can pack a
real ROM and have it read correctly by real tooling, and can read a real `.ndz` file (or
pair container) produced by that tooling, filter-mode and base-patch blocks included -
confirmed end-to-end on real cartridge dumps too, not just synthetic test fixtures,
including a base-patched Pokémon Black/White pair (140x on the patched ROM alone, from
how much of the two games' content overlaps) cross-checked byte-identical against
`ndztool.py` in every pack/read direction. See
[`docs/ndz-format-spec.md`](docs/ndz-format-spec.md) for the on-disk format, full
implementation status, and provenance, and
[`docs/ndz-remaining-work.md`](docs/ndz-remaining-work.md) for the build history.

Implemented: compression, random-access decompression, raw-content dictionary support
(including for dictionaries past zstd's implicit ~8 MiB window default), all five
per-block byte-transform filter modes, base-ROM patch mode (windowed dictionary
compression against a base ROM, not a binary diff), and the pair-container format for
shipping a whole family of ROMs together in one self-contained file - a star topology:
one shared base plus any number of targets, each base-patched against that same base
(never against each other), so e.g. every regional/version release of one game can go
in a single file, not just a base+one-target pair. The only thing left unimplemented is
a retired, never-produced trained-dictionary flag not worth building.

## Usage

```
ndz compress <in.nds> <out.ndz> [--level 1-19] [--block-size 8192|16384|32768|auto]
                                 [--frame-size N] [--no-filters] [--no-verify]
                                 [--raw-dict <size>|auto] [--max-dict <size>] [--base <base.nds>]
                                                  Compress a decrypted .nds into .ndz.
                                                  --raw-dict (e.g. 8m, 512k) derives a
                                                  dictionary from this ROM's own repeated
                                                  content, up to that size - there's no
                                                  option to load externally-supplied
                                                  dictionary content, because neither
                                                  reference implementation has one either;
                                                  `auto` samples the ROM (like `ndz
                                                  analyze`) and picks a size itself, capped
                                                  by --max-dict (default 8m). --block-size
                                                  defaults to 8192 bytes; 16384/32768 are
                                                  also offered (see "Hardware limits"
                                                  below), or `auto` to pick from those the
                                                  same way --raw-dict auto does (can't be
                                                  combined with an explicit --raw-dict
                                                  <size>, since the best dictionary size
                                                  depends on which block size wins).
                                                  --frame-size (e.g. 128k, default 131072)
                                                  is the outer seek-table bucketing
                                                  granularity, not a hardware limit - just
                                                  must be >= --block-size. --base patches
                                                  against a second, already-decrypted
                                                  .nds. --level is capped at the target
                                                  hardware's decode-speed limit - see
                                                  "Hardware limits" below. --no-verify
                                                  skips the default decode-and-byte-compare
                                                  check that runs after every pack.
ndz compress <target1.nds> [target2.nds ...] --pair-out <pair.ndz> --base <base.nds>
                                                  Pack a base plus one or more base-patched
                                                  targets into one self-contained file - a
                                                  star topology: every target is patched
                                                  against the same shared base, never
                                                  against each other, so a whole family of
                                                  similar ROMs can go in one file.
ndz decompress <in.ndz> <out.nds> [--base <base.nds>] [--index N]
                                                  Reconstruct the original .nds. --base is
                                                  required if the file used base-patch
                                                  (not for a pair container, which carries
                                                  its own). --index picks which ROM to
                                                  extract from a pair container (default:
                                                  the self-contained one).
ndz info <in.ndz>                                Print front-matter and seek-table summary,
                                                  or (for a pair container) both entries' -
                                                  never needs --base either way.
ndz verify <in.ndz> <in.nds> [--base <base.nds>] [--index N]
                                                  Decompress and byte-compare against the original.
ndz analyze <in.nds> [--base <base.nds>] [--max-dict <size>] [--level N] [--block-size N]
                                                  Print a sampled dictionary-size/block-size
                                                  curve and recommended settings (seconds,
                                                  not a full pack) - what --raw-dict auto
                                                  and --block-size auto run internally.
```

### Hardware limits

Compression level (max 19) and block size (max 32768 bytes) are capped, not just
defaulted: the real target hardware (DSPico) decodes on the fly while the console
waits on a cart read, so a higher level or a bigger block would produce a file that
packs fine and then fails - or stalls - on real hardware. `NdzWriter.Compress` throws
rather than silently accept either past its limit, matching `ndztool.py`'s own refusal
(`NDZ_MAX_LEVEL`/`NDZ_MAX_BLOCK_SIZE`).

The block-size ceiling was raised from 8192 to 32768 bytes on 2026-09-06 after a
firmware fix on the real hardware; the CLI's own `--block-size` menu is curated down to
exactly three choices (8 KiB/16 KiB/32 KiB) rather than every power of two up to that
ceiling, to keep the choice simple and always a safe one - a bigger block trades away
random-access granularity (a whole block must be decompressed to reach any byte in it)
for ratio.

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

---

Built collaboratively with [Claude 5](https://claude.com) (Anthropic).
