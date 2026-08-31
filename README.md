# NDZ

Compresses decrypted Nintendo DS ROMs (`.nds`) into `.ndz`: a seekable ZStandard
container with fixed 8 KB frames and an external seek table, built on
[GrindCore](https://www.nuget.org/packages/GrindCore) (`Nanook.GrindCore.ZStd`).

Because every frame is an independent ZStd frame, reading any byte range only requires
decompressing the frame(s) it falls in — not the whole ROM. See
[`docs/ndz-format-spec.md`](docs/ndz-format-spec.md) for the on-disk format, current
implementation status, and what's deliberately deferred (dictionary support is blocked
on a GrindCore.net API gap; base-ROM patch mode needs a real diffing step and hasn't
been scoped yet).

## Usage

```
ndz compress <in.nds> <out.ndz> [--level 1-22]   Compress a decrypted .nds into .ndz.
ndz decompress <in.ndz> <out.nds>                Reconstruct the original .nds.
ndz info <in.ndz>                                Print front-matter and seek-table summary.
ndz verify <in.ndz> <in.nds>                     Decompress and byte-compare against the original.
```

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
