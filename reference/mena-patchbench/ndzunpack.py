#!/usr/bin/env python3
"""Unpack a .ndz back to the original .nds.

Handles every shipped variant: plain LZ4 or zstd blocks, per-block filters,
raw-content dict, base-patch windows, and the two-ROM pair container.

The decode itself is imported from patchbench.py rather than reimplemented --
that module IS the format definition, so a copy here would be a second thing to
keep in sync and a second thing to get subtly wrong.

    ndzunpack.py rom.ndz                  # -> rom.nds
    ndzunpack.py rom.ndz out.nds
    ndzunpack.py pair.ndz --list          # what is inside a pair container
    ndzunpack.py pair.ndz --index 1 out.nds
    ndzunpack.py patched.ndz --base base.nds out.nds
"""

import argparse
import hashlib
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import patchbench as pb


def fmt_bytes(n: int) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.1f} {unit}" if unit != "B" else f"{n} B"
        n /= 1024
    return str(n)


def read_pair_entries(blob: bytes):
    """Return [(offset, size, origSize, gameCode)] for a pair container, or
    None if this is a single .ndz."""
    if len(blob) < 0x30:
        return None
    magic, hdr_size, n_roms, _ = struct.unpack_from("<IIII", blob, 0)
    if magic != pb.NDZ_PAIR_MAGIC:
        return None
    out = []
    for i in range(n_roms):
        off, size, orig, gc = struct.unpack_from("<III4s", blob, 0x10 + 0x10 * i)
        out.append((off, size, orig, gc))
    return out


def decode_ndz_blob(blob: bytes, base: bytes | None = None) -> bytes:
    """Decode one complete .ndz blob to the original .nds bytes."""
    if len(blob) < 16:
        sys.exit("error: .ndz too short")

    magic, fm_size, orig_size, flags = struct.unpack("<IIII", blob[0:16])
    if magic != pb.NDZ_MAGIC:
        sys.exit(f"error: bad NDZ magic 0x{magic:08X} (a pair container? try --list)")
    if fm_size != pb.NDZ_FRONTMATTER_SIZE:
        sys.exit(f"error: unexpected frontMatterSize {fm_size}")

    # A dict section, when present, sits between front-matter and payload.
    dict_obj = None
    raw_dict = None
    dict_size = 0
    if flags & pb.NDZ_FLAG_RAWDICT:
        dict_size = struct.unpack_from("<I", blob, pb.NDZ_DICTSIZE_OFFSET)[0]
        rd_dsize = struct.unpack_from("<I", blob, pb.NDZ_RAWDICT_DSIZE_OFFSET)[0]
        if dict_size != rd_dsize:
            sys.exit("error: raw dict must be stored verbatim "
                     "(storedSize != dsize -- old compressed-dict pack?)")
        raw_dict = bytes(blob[fm_size: fm_size + dict_size])
        if len(raw_dict) != rd_dsize:
            sys.exit("error: raw dict section truncated")
    elif flags & pb.NDZ_FLAG_DICT:
        dict_size = struct.unpack_from("<I", blob, pb.NDZ_DICTSIZE_OFFSET)[0]
        dict_bytes = blob[fm_size: fm_size + dict_size]
        if len(dict_bytes) != dict_size:
            sys.exit("error: dict section truncated")
        dict_obj = pb.zstd.ZstdCompressionDict(dict_bytes)

    if flags & pb.NDZ_FLAG_BASE:
        if base is None:
            sys.exit("error: this .ndz is a base-patch pack (flag bit 4) and needs\n"
                     "       the base ROM: pass --base <base.nds>, or unpack the\n"
                     "       pair container that carries both")
        want_size = struct.unpack_from("<I", blob, pb.NDZ_BASE_ORIGSIZE_OFFSET)[0]
        want_gc = blob[pb.NDZ_BASE_GAMECODE_OFFSET:pb.NDZ_BASE_GAMECODE_OFFSET + 4]
        want_hash = blob[pb.NDZ_BASE_HDRHASH_OFFSET:pb.NDZ_BASE_HDRHASH_OFFSET + 8]
        if want_size != len(base):
            sys.exit(f"error: base size mismatch (pack wants {want_size}, got {len(base)})")
        if want_gc != base[0x0C:0x10]:
            sys.exit("error: base gameCode mismatch")
        if want_hash != hashlib.blake2b(base[:0x200], digest_size=8).digest():
            sys.exit("error: base header hash mismatch -- wrong base ROM")

    payload = blob[fm_size + dict_size:]

    if flags & pb.NDZ_FLAG_V2_HIERARCHICAL:
        block_log2 = (flags >> 8) & 0xFF
        block_dsize = 4096 if block_log2 == 0 else (1 << block_log2)
        adv = flags & (pb.NDZ_FLAG_DICT | pb.NDZ_FLAG_FILTERS
                       | pb.NDZ_FLAG_BASE | pb.NDZ_FLAG_RAWDICT)
        if adv:
            data = pb.decompress_seekable_v2_adv(
                payload, block_dsize, dict_obj,
                bool(flags & pb.NDZ_FLAG_FILTERS),
                base if (flags & pb.NDZ_FLAG_BASE) else None,
                raw_dict)
        else:
            codec = "zstd" if (flags & pb.NDZ_FLAG_ZSTD_BLOCKS) else "lz4"
            data = pb.decompress_seekable_v2(payload, block_dsize, codec)
    else:
        # Legacy flat layout. These carry the LZ4B trailer; the ORIGINAL
        # zstd-seekable payload that shipped before it does not, and
        # patchbench no longer writes or reads that form.
        trailer = (struct.unpack_from("<I", payload, len(payload) - 4)[0]
                   if len(payload) >= 4 else 0)
        if trailer != pb.LZ4BENCH_MAGIC:
            sys.exit("error: payload has no LZ4B trailer -- this looks like "
                     "the older zstd-seekable .ndz, which this tool (and "
                     "patchbench) cannot read")
        data = pb.decompress_seekable(payload)

    if len(data) != orig_size:
        sys.exit(f"error: decoded {len(data)} bytes, front-matter says {orig_size}")
    return bytes(data)


def describe_flags(flags: int) -> str:
    names = []
    if flags & pb.NDZ_FLAG_V2_HIERARCHICAL: names.append("v2")
    if flags & pb.NDZ_FLAG_ZSTD_BLOCKS:     names.append("zstd")
    else:                                   names.append("lz4")
    if flags & pb.NDZ_FLAG_DICT:            names.append("trained-dict")
    if flags & pb.NDZ_FLAG_FILTERS:         names.append("filters")
    if flags & pb.NDZ_FLAG_BASE:            names.append("base-patch")
    if flags & pb.NDZ_FLAG_RAWDICT:         names.append("raw-dict")
    log2 = (flags >> 8) & 0xFF
    names.append(f"block={4096 if log2 == 0 else 1 << log2}")
    return ", ".join(names)


def main() -> None:
    ap = argparse.ArgumentParser(description="Unpack a .ndz back to .nds")
    ap.add_argument("input", type=Path)
    ap.add_argument("output", type=Path, nargs="?")
    ap.add_argument("--base", type=Path,
                    help="base .nds, for a base-patch pack")
    ap.add_argument("--index", type=int, default=None,
                    help="which ROM to extract from a pair container (default: all)")
    ap.add_argument("--list", action="store_true",
                    help="describe the file and exit")
    args = ap.parse_args()

    blob = args.input.read_bytes()
    entries = read_pair_entries(blob)

    if args.list:
        if entries is None:
            magic, fm, orig, flags = struct.unpack_from("<IIII", blob, 0)
            if magic != pb.NDZ_MAGIC:
                sys.exit(f"error: not a .ndz -- magic is 0x{magic:08X}, "
                         f"expected 0x{pb.NDZ_MAGIC:08X} ('NDZ1')")
            if fm != pb.NDZ_FRONTMATTER_SIZE:
                sys.exit(f"error: unexpected frontMatterSize {fm}")
            gc = blob[0x10 + pb.NDZ_BANNER_SLOT_SIZE:
                      0x10 + pb.NDZ_BANNER_SLOT_SIZE + 4].decode("ascii", "replace")
            print(f"single .ndz  {gc}  {fmt_bytes(len(blob))} -> "
                  f"{fmt_bytes(orig)}  [{describe_flags(flags)}]")
        else:
            print(f"pair container, {len(entries)} ROMs:")
            for i, (off, size, orig, gc) in enumerate(entries):
                sub = blob[off:off + size]
                fl = struct.unpack_from("<IIII", sub, 0)[3]
                print(f"  [{i}] {gc.decode('ascii', 'replace')}  "
                      f"{fmt_bytes(size)} -> {fmt_bytes(orig)}  [{describe_flags(fl)}]")
        return

    base = args.base.read_bytes() if args.base else None

    if entries is None:
        out = args.output or args.input.with_suffix(".nds")
        data = decode_ndz_blob(blob, base)
        out.write_bytes(data)
        print(f"{args.input.name} -> {out}  ({fmt_bytes(len(data))})")
        return

    # Pair container. A base-patch sub-file resolves its base to the other
    # entry in the SAME file, so decode the non-patched one first and feed it in.
    plain_idx = None
    for i, (off, size, _, _) in enumerate(entries):
        fl = struct.unpack_from("<IIII", blob[off:off + size], 0)[3]
        if not (fl & pb.NDZ_FLAG_BASE):
            plain_idx = i
            break
    if plain_idx is None:
        sys.exit("error: pair container has no self-contained sub-file to use as base")

    off, size, _, _ = entries[plain_idx]
    resolved_base = decode_ndz_blob(blob[off:off + size], None)

    targets = range(len(entries)) if args.index is None else [args.index]
    for i in targets:
        if i < 0 or i >= len(entries):
            sys.exit(f"error: --index {i} out of range (0..{len(entries) - 1})")
        off, size, _, gc = entries[i]
        data = (resolved_base if i == plain_idx
                else decode_ndz_blob(blob[off:off + size], resolved_base))
        if args.output and args.index is not None:
            out = args.output
        else:
            code = gc.decode("ascii", "replace").strip("\x00") or f"rom{i}"
            out = args.input.with_name(f"{args.input.stem}_{i}_{code}.nds")
        out.write_bytes(data)
        print(f"[{i}] -> {out}  ({fmt_bytes(len(data))})")


if __name__ == "__main__":
    main()
