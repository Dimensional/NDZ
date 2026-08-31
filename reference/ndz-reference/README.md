# ndz-reference

Scratch Rust crate, not a shipped part of NDZ. Exists to pull down the same `zstd`
(0.13.x, wrapping libzstd 1.5.7 - matching GrindCore's own vendored version) and
`rayon` crates the NDZ format author's reference packer (`pack.rs`) depends on, so
their real source can be read directly (in
`~/.cargo/registry/src/index.crates.io-*/zstd-0.13.3/`) instead of guessing at their
behavior.

This is how the exact root cause of GrindCore's missing ZStd dictionary support was
pinned down (`zstd-0.13.3/src/bulk/{compressor,decompressor}.rs`) — see
`docs/ndz-format-spec.md`'s "Dictionary support" section and the GrindCore workspace's
own memory (`zstd-dictionary-gap.md`) for the full writeup.

If you get hold of the reference packer's `crate::filters`/`crate::census` modules
(needed to implement the per-block raw-dictionary and filter compression modes - see
`BlockMode`'s remarks in the .NET port), this is a reasonable place to drop them in and
experiment before porting the logic to C#.

## Building

Needs `dlltool.exe` on `PATH` (from a MinGW/MSYS2 install, e.g.
`C:\tools\msys64\mingw64\bin`) if the default Rust toolchain targets
`*-pc-windows-gnu` — `zstd-sys`'s build script needs it.

```
cargo build
```
