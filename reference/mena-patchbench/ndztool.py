#!/usr/bin/env python3
"""ndztool -- pack and unpack .ndz files. Single file, no local imports.

    ndztool.py rom.nds                       # pack   -> rom.ndz
    ndztool.py rom.ndz                       # unpack -> rom.nds
    ndztool.py info rom.ndz                  # identify without decoding

    ndztool.py pack rom.nds out.ndz --level 19 --block-size 8k
    ndztool.py pack tgt.nds out.ndz --base base.nds          # base-patch
    ndztool.py pack tgt.nds --pair-out pair.ndz --base base.nds
    ndztool.py pack rom.nds out.ndz --raw-dict 8m

    ndztool.py unpack rom.ndz out.nds
    ndztool.py unpack patched.ndz out.nds --base base.nds
    ndztool.py unpack pair.ndz --index 1 out.nds

The bare form infers the direction from the extension.

Requires: pip install zstandard lz4
(lz4 is only needed to READ older lz4-block packs; packing is zstd only.)

This file is self-contained on purpose so it can be shared alone. The format
logic is duplicated from patchbench.py, which remains the reference
implementation -- if the format ever changes there, it must change here too.

--------------------------------------------------------------------------
.ndz format
--------------------------------------------------------------------------
  [16 KB front-matter][optional dict section][payload][frame table][trailer]

Front-matter (little-endian):
  0x00 u32  magic 'NDZ1'
  0x04 u32  frontMatterSize (16384)
  0x08 u32  originalSize
  0x0C u32  flags
  0x10      banner, 0x2400 bytes
  0x2410 u32 gameCode
  0x2414 u32 dict stored size
  0x2418 u32 base originalSize     0x241C u32 base gameCode
  0x2420 u64 base header hash      0x2428 u32 raw dict decompressed size

flags: bit0 v2 hierarchical, bit1 zstd blocks, bit2 trained dict (retired),
       bit3 per-block filter modes, bit4 base-patch, bit5 raw-content dict,
       bits 8..15 log2(block size).

Payload is a list of frames; a trailing table gives (csize, dsize) per frame
and ends with u32 nframes + magic 'LZ4B'. Each v2 frame is:
  [u32 blockCsize[n]][u8 mode[n] if bit3][u32 baseOff[n] if bit4][blocks...]

Per-block modes: 0 dict, 1 plain, 2/3/4 delta stride 1/2/4,
5/6 de-interleave 2/4 byte planes. The filter is applied BEFORE compression
and undone after; the packer tries them all and keeps the smallest.
"""

import argparse
import hashlib
import struct
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path

try:
    import zstandard as zstd
except ImportError:
    zstd = None

try:
    import lz4.block as lz4block
except ImportError:
    lz4block = None


# ------------------------------------------------------------- constants ---

NDZ_MAGIC = 0x315A444E            # 'NDZ1'
NDZ_PAIR_MAGIC = 0x505A444E       # 'NDZP'
LZ4BENCH_MAGIC = 0x4C5A3442       # 'LZ4B'

NDZ_FRONTMATTER_SIZE = 16384
NDZ_BANNER_SLOT_SIZE = 0x2400

NDZ_FLAG_V2_HIERARCHICAL = 1 << 0
NDZ_FLAG_ZSTD_BLOCKS     = 1 << 1
NDZ_FLAG_DICT            = 1 << 2   # trained dict, retired
NDZ_FLAG_FILTERS         = 1 << 3
NDZ_FLAG_BASE            = 1 << 4
NDZ_FLAG_RAWDICT         = 1 << 5

NDZ_DICTSIZE_OFFSET      = 0x10 + NDZ_BANNER_SLOT_SIZE + 4
NDZ_BASE_ORIGSIZE_OFFSET = NDZ_DICTSIZE_OFFSET + 4
NDZ_BASE_GAMECODE_OFFSET = NDZ_BASE_ORIGSIZE_OFFSET + 4
NDZ_BASE_HDRHASH_OFFSET  = NDZ_BASE_GAMECODE_OFFSET + 4
NDZ_RAWDICT_DSIZE_OFFSET = NDZ_BASE_HDRHASH_OFFSET + 8

NDZ_BASE_WINDOW   = 16384
NDZ_BASE_SENTINEL = 0xFFFFFFFF
BASE_GRAIN        = 2048

NDZ_MODE_DICT     = 0
NDZ_MODE_PLAIN    = 1
NDZ_MODE_DELTA1   = 2
NDZ_MODE_DELTA2   = 3
NDZ_MODE_DELTA4   = 4
NDZ_MODE_SHUFFLE2 = 5
NDZ_MODE_SHUFFLE4 = 6

MODE_NAMES = ["dict", "plain", "delta1", "delta2", "delta4",
              "shuffle2", "shuffle4"]

# Hardware limits, not preferences. The DSpico decodes on the fly while the
# console waits on a cart read, so both of these are latency ceilings:
#   level > 19  -- decompression is too slow for the console
#   block > 8k  -- too long to fetch AND decompress; the console freezes
# Going past either produces a file that packs fine and then fails on real
# hardware, which is worse than refusing, so refuse.
NDZ_MAX_LEVEL = 19
NDZ_MAX_BLOCK_SIZE = 8192


@dataclass
class BlockStat:
    csize: int
    dsize: int


# --------------------------------------------------------------- filters ---
# Reversible byte-level decorrelation, applied before compression. Delta turns
# a slowly varying sequence into near-zeros; shuffle groups the high bytes of
# 16/32-bit arrays away from the noisy low bytes. Both give the compressor
# longer matches. They only apply to FULL blocks so the planes divide evenly.

def _delta_fwd(b, s):
    out = bytearray(b)
    for i in range(len(b) - 1, s - 1, -1):
        out[i] = (b[i] - b[i - s]) & 0xFF
    return bytes(out)


def _delta_inv(b, s):
    out = bytearray(b)
    for i in range(s, len(b)):
        out[i] = (out[i] + out[i - s]) & 0xFF
    return bytes(out)


def _shuffle_fwd(b, s):
    return b"".join(b[k::s] for k in range(s))


def _shuffle_inv(b, s):
    plane = len(b) // s
    out = bytearray(len(b))
    for k in range(s):
        out[k::s] = b[k * plane:(k + 1) * plane]
    return bytes(out)


_FILTER_FWD = {
    NDZ_MODE_DELTA1:   lambda b: _delta_fwd(b, 1),
    NDZ_MODE_DELTA2:   lambda b: _delta_fwd(b, 2),
    NDZ_MODE_DELTA4:   lambda b: _delta_fwd(b, 4),
    NDZ_MODE_SHUFFLE2: lambda b: _shuffle_fwd(b, 2),
    NDZ_MODE_SHUFFLE4: lambda b: _shuffle_fwd(b, 4),
}
_FILTER_INV = {
    NDZ_MODE_DELTA1:   lambda b: _delta_inv(b, 1),
    NDZ_MODE_DELTA2:   lambda b: _delta_inv(b, 2),
    NDZ_MODE_DELTA4:   lambda b: _delta_inv(b, 4),
    NDZ_MODE_SHUFFLE2: lambda b: _shuffle_inv(b, 2),
    NDZ_MODE_SHUFFLE4: lambda b: _shuffle_inv(b, 4),
}


# --------------------------------------------------------------- helpers ---

def fmt_bytes(n):
    n = float(n)
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.1f} {unit}" if unit != "B" else f"{int(n)} B"
        n /= 1024
    return str(n)


def parse_size(s):
    s = str(s).strip().lower()
    mult = 1
    if s.endswith("k"):
        mult, s = 1024, s[:-1]
    elif s.endswith("m"):
        mult, s = 1024 * 1024, s[:-1]
    return int(s) * mult


def _block_log2(block_dsize):
    if block_dsize < 1 or block_dsize & (block_dsize - 1) != 0:
        sys.exit(f"error: block size must be a power of 2 (got {block_dsize})")
    n, v = 0, block_dsize
    while v > 1:
        v >>= 1
        n += 1
    return n


def _wlog(n):
    return max(10, (n - 1).bit_length())


def need_zstd():
    if zstd is None:
        sys.exit("error: pip install zstandard")


def sha256_file(path):
    d = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            d.update(chunk)
    return d.hexdigest()


# ------------------------------------------------------------ codec glue ---

_zstd_dctx = None


def zstd_decompress_block(payload, dsize):
    global _zstd_dctx
    need_zstd()
    if _zstd_dctx is None:
        _zstd_dctx = zstd.ZstdDecompressor()
    return _zstd_dctx.decompress(payload, max_output_size=dsize)


def lz4_decompress_block(payload, dsize):
    if lz4block is None:
        sys.exit("error: pip install lz4 (needed to read lz4-block packs)")
    return lz4block.decompress(payload, uncompressed_size=dsize)


def _make_min_params(level, block_dsize, window_log):
    """Smallest possible frame header: the frame table already records dsize
    and the dict is known out of band, so content-size, dict-id and checksum
    are redundant per-block bytes."""
    return zstd.ZstdCompressionParameters.from_level(
        level, source_size=block_dsize, window_log=window_log,
        write_content_size=False, write_dict_id=False, write_checksum=False)


def make_block_compressors(level, block_dsize, dict_obj):
    plain = zstd.ZstdCompressor(
        compression_params=_make_min_params(level, block_dsize, _wlog(block_dsize)))
    dict_cctx = None
    dict_wl = _wlog(block_dsize)
    if dict_obj is not None:
        dict_wl = max(_wlog(block_dsize),
                      _wlog(len(dict_obj.as_bytes()) + block_dsize))
        dict_cctx = zstd.ZstdCompressor(
            compression_params=_make_min_params(level, block_dsize, dict_wl),
            dict_data=dict_obj)
    return plain, dict_cctx, dict_wl


def make_rawdict_cctx(raw, level):
    d = zstd.ZstdCompressionDict(raw, dict_type=zstd.DICT_TYPE_RAWCONTENT)
    d.precompute_compress(level=level)
    # Must be level= + dict_data=. The compression_params + dict_data
    # combination silently IGNORES the dictionary.
    return zstd.ZstdCompressor(level=level, dict_data=d)


# ------------------------------------------------------------ raw dict -----

def _cdc_chunks(data):
    """Content-defined chunking (gear hash, ~4 KB avg, 512 B..16 KB)."""
    gear = [(i * 2654435761 + 0x9E3779B9) & 0xFFFFFFFF for i in range(256)]
    mask = (1 << 12) - 1
    min_c, max_c = 512, 16384
    out = []
    start = h = 0
    i, n = 0, len(data)
    while i < n:
        h = ((h << 1) + gear[data[i]]) & 0xFFFFFFFF
        i += 1
        if (i - start >= min_c and (h & mask) == 0) or i - start >= max_c:
            out.append((start, i - start))
            start, h = i, 0
    if start < n:
        out.append((start, n - start))
    return out


def build_dup_weighted_dict(data, dict_size):
    """Rank non-constant duplicate CDC chunks by (count-1)*len -- the bytes an
    ideal dedup would save -- and pack the top ones up to dict_size."""
    chunks = _cdc_chunks(data)
    counts = Counter()
    rep = {}
    for off, ln in chunks:
        hh = hashlib.blake2b(data[off:off + ln], digest_size=16).digest()
        counts[hh] += 1
        if hh not in rep:
            rep[hh] = (off, ln)
    ranked = sorted(((cnt - 1) * rep[hh][1], hh)
                    for hh, cnt in counts.items()
                    if cnt > 1 and len(set(data[rep[hh][0]:rep[hh][0] + 256])) > 1)
    ranked.reverse()
    parts, tot = [], 0
    for _val, hh in ranked:
        off, ln = rep[hh]
        if tot + ln > dict_size:
            continue
        parts.append(data[off:off + ln])
        tot += ln
        if tot >= dict_size - 2048:
            break
    return b"".join(parts)


class BaseCtx:
    """Pack-time context for --base: grain-hash index of the base ROM, plus
    per-window candidate search and windowed compression."""

    def __init__(self, base, level):
        self.base = base
        self.level = level
        index = defaultdict(list)
        for off in range(0, len(base) - BASE_GRAIN + 1, BASE_GRAIN):
            h = hashlib.blake2b(base[off:off + BASE_GRAIN], digest_size=8).digest()
            bucket = index[h]
            if len(bucket) < 4:
                bucket.append(off)
        self.index = index

    def window_candidates(self, blk, file_off, max_cand=8):
        cands = set()
        center = max(0, (NDZ_BASE_WINDOW - len(blk)) // 2)
        hi = max(0, len(self.base) - NDZ_BASE_WINDOW)
        if file_off < len(self.base):
            cands.add(max(0, min(file_off - center, hi)))
        for sub in range(0, len(blk) - BASE_GRAIN + 1, BASE_GRAIN):
            h = hashlib.blake2b(blk[sub:sub + BASE_GRAIN], digest_size=8).digest()
            for off in self.index.get(h, ()):
                cands.add(max(0, min(off - sub - center, hi)))
        return list(cands)[:max_cand]

    def compress_with_window(self, blk, woff):
        d = zstd.ZstdCompressionDict(self.base[woff:woff + NDZ_BASE_WINDOW],
                                     dict_type=zstd.DICT_TYPE_RAWCONTENT)
        # level= + dict_data=, see make_rawdict_cctx.
        return zstd.ZstdCompressor(level=self.level, dict_data=d).compress(blk)


# ------------------------------------------------------------------ pack ---

def compress_block_best(blk, block_dsize, plain_cctx, dict_cctx,
                        filters_enabled, base_ctx=None, file_off=0,
                        raw_cctx=None):
    """Smallest of {plain, dict, raw-dict, plain+filter, base-window}."""
    best_mode = NDZ_MODE_PLAIN
    best = plain_cctx.compress(blk)
    if dict_cctx is not None:
        c = dict_cctx.compress(blk)
        if len(c) < len(best):
            best_mode, best = NDZ_MODE_DICT, c
    if raw_cctx is not None:
        c = raw_cctx.compress(blk)
        if len(c) < len(best):
            best_mode, best = NDZ_MODE_DICT, c
    if filters_enabled and len(blk) == block_dsize:
        for mode, fwd in _FILTER_FWD.items():
            c = plain_cctx.compress(fwd(blk))
            if len(c) < len(best):
                best_mode, best = mode, c
    base_off = NDZ_BASE_SENTINEL
    if base_ctx is not None:
        for woff in base_ctx.window_candidates(blk, file_off):
            c = base_ctx.compress_with_window(blk, woff)
            if len(c) < len(best):
                best_mode, best, base_off = NDZ_MODE_PLAIN, c, woff
    return best_mode, best, base_off


def build_seekable_v2_adv(data, frame_dsize, block_dsize, level, dict_obj,
                          filters_enabled, base_ctx=None, raw_cctx=None,
                          progress=None):
    plain_cctx, dict_cctx, dict_wl = make_block_compressors(
        level, block_dsize, dict_obj)
    out = bytearray()
    frame_stats = []
    per_frame_blocks = []
    mode_hist = [0] * 7
    n_windowed = 0

    for fstart in range(0, len(data), frame_dsize):
        fchunk = data[fstart:fstart + frame_dsize]
        sub_blocks, sub_modes, sub_offs, sub_stats = [], [], [], []
        for bstart in range(0, len(fchunk), block_dsize):
            bchunk = fchunk[bstart:bstart + block_dsize]
            mode, blk, boff = compress_block_best(
                bchunk, block_dsize, plain_cctx, dict_cctx, filters_enabled,
                base_ctx, fstart + bstart, raw_cctx)
            sub_blocks.append(blk)
            sub_modes.append(mode)
            sub_offs.append(boff)
            sub_stats.append(BlockStat(csize=len(blk), dsize=len(bchunk)))
            mode_hist[mode] += 1
            if boff != NDZ_BASE_SENTINEL:
                n_windowed += 1

        index_hdr = bytearray()
        for s in sub_stats:
            index_hdr += struct.pack("<I", s.csize)
        if filters_enabled:
            index_hdr += bytes(sub_modes)
        if base_ctx is not None:
            for bo in sub_offs:
                index_hdr += struct.pack("<I", bo)

        frame_payload = bytes(index_hdr) + b"".join(sub_blocks)
        out.extend(frame_payload)
        frame_stats.append(BlockStat(csize=len(frame_payload), dsize=len(fchunk)))
        per_frame_blocks.append(sub_stats)
        if progress is not None and (len(frame_stats) & 7) == 0:
            progress(fstart + len(fchunk), len(data))

    table = bytearray()
    for s in frame_stats:
        table += struct.pack("<II", s.csize, s.dsize)
    table += struct.pack("<II", len(frame_stats), LZ4BENCH_MAGIC)
    out.extend(table)
    return bytes(out), frame_stats, per_frame_blocks, mode_hist, dict_wl, n_windowed


def build_ndz_frontmatter(nds_data, flags=0, dict_size=0, base_data=None,
                          raw_dict_dsize=0):
    if len(nds_data) < 0x200:
        sys.exit("error: input is too small to be a valid .nds")

    game_code = nds_data[0x0C:0x10]
    banner_offset = struct.unpack("<I", nds_data[0x68:0x6C])[0]
    if banner_offset == 0 or banner_offset >= len(nds_data):
        sys.exit(f"error: invalid bannerOffset 0x{banner_offset:X} in NDS header")

    version = struct.unpack("<H", nds_data[banner_offset:banner_offset + 2])[0]
    if version >= 0x0103:
        banner_size = 0x23C0
    elif version >= 3:
        banner_size = 0xA40
    elif version >= 2:
        banner_size = 0x940
    else:
        banner_size = 0x840
    if banner_offset + banner_size > len(nds_data):
        banner_size = len(nds_data) - banner_offset
    if banner_size > NDZ_BANNER_SLOT_SIZE:
        sys.exit(f"error: banner size 0x{banner_size:X} exceeds reserved slot")

    banner = nds_data[banner_offset:banner_offset + banner_size]
    fm = bytearray(NDZ_FRONTMATTER_SIZE)
    struct.pack_into("<IIII", fm, 0,
                     NDZ_MAGIC, NDZ_FRONTMATTER_SIZE, len(nds_data), flags)
    fm[0x10:0x10 + banner_size] = banner
    fm[0x10 + NDZ_BANNER_SLOT_SIZE:0x10 + NDZ_BANNER_SLOT_SIZE + 4] = game_code
    if dict_size:
        struct.pack_into("<I", fm, NDZ_DICTSIZE_OFFSET, dict_size)
    if base_data is not None:
        struct.pack_into("<I", fm, NDZ_BASE_ORIGSIZE_OFFSET, len(base_data))
        fm[NDZ_BASE_GAMECODE_OFFSET:NDZ_BASE_GAMECODE_OFFSET + 4] = base_data[0x0C:0x10]
        fm[NDZ_BASE_HDRHASH_OFFSET:NDZ_BASE_HDRHASH_OFFSET + 8] = \
            hashlib.blake2b(base_data[:0x200], digest_size=8).digest()
    if raw_dict_dsize:
        struct.pack_into("<I", fm, NDZ_RAWDICT_DSIZE_OFFSET, raw_dict_dsize)
    return bytes(fm), game_code


def pack_ndz_blob(nds_data, block_size, frame_size, level, filters_enabled,
                  base_data=None, base_ctx=None, raw_dict_size=0, progress=None):
    """Pack one ROM into a complete .ndz blob. v2 + zstd."""
    need_zstd()
    flags = (NDZ_FLAG_V2_HIERARCHICAL | NDZ_FLAG_ZSTD_BLOCKS
             | (_block_log2(block_size) << 8))
    if filters_enabled:
        flags |= NDZ_FLAG_FILTERS
    if base_ctx is not None:
        flags |= NDZ_FLAG_BASE

    raw_dict = b""
    stored_dict = b""
    raw_cctx = None
    if raw_dict_size > 0:
        raw_dict = build_dup_weighted_dict(nds_data, raw_dict_size)
        if len(raw_dict) >= 4096:
            flags |= NDZ_FLAG_RAWDICT | NDZ_FLAG_FILTERS
            # Stored VERBATIM: firmware OPEN copies it straight to PSRAM,
            # ~20x faster than inflating with decoder state in PSRAM.
            stored_dict = raw_dict
            raw_cctx = make_rawdict_cctx(raw_dict, level)
        else:
            raw_dict = b""

    fm, game_code = build_ndz_frontmatter(
        nds_data, flags, len(stored_dict),
        base_data if base_ctx is not None else None, len(raw_dict))
    seekable, frame_stats, per_frame_blocks, mode_hist, _wl, n_windowed = \
        build_seekable_v2_adv(nds_data, frame_size, block_size, level, None,
                              bool(flags & NDZ_FLAG_FILTERS), base_ctx,
                              raw_cctx, progress=progress)
    blob = fm + stored_dict + seekable
    info = {
        "game_code": game_code,
        "n_frames": len(frame_stats),
        "mode_hist": mode_hist,
        "n_windowed": n_windowed,
        "n_blocks": sum(len(fb) for fb in per_frame_blocks),
        "raw_dict": len(raw_dict),
        "total": len(blob),
    }
    return blob, info


# ---------------------------------------------------------------- unpack ---

def parse_seek_table(buf):
    if len(buf) < 8:
        raise ValueError("buffer too short for trailer")
    nframes, magic = struct.unpack("<II", buf[-8:])
    if magic != LZ4BENCH_MAGIC:
        raise ValueError(f"bad trailer magic 0x{magic:08X}")
    table_start = len(buf) - 8 - 8 * nframes
    if table_start < 0:
        raise ValueError(f"impossible nframes {nframes} for buffer of {len(buf)}")
    entries = []
    for i in range(nframes):
        off = table_start + i * 8
        c, d = struct.unpack("<II", buf[off:off + 8])
        entries.append(BlockStat(csize=c, dsize=d))
    return entries


def decompress_flat(buf):
    entries = parse_seek_table(buf)
    out = bytearray()
    pos = 0
    for e in entries:
        out.extend(lz4_decompress_block(buf[pos:pos + e.csize], e.dsize))
        pos += e.csize
    return bytes(out)


def decompress_v2(buf, block_dsize, codec="lz4"):
    entries = parse_seek_table(buf)
    out = bytearray()
    pos = 0
    for fe in entries:
        frame_start = pos
        nblocks = (fe.dsize + block_dsize - 1) // block_dsize
        hdr_size = nblocks * 4
        if hdr_size > fe.csize:
            raise ValueError(f"frame index {hdr_size} > frame csize {fe.csize}")
        block_csizes = [struct.unpack_from("<I", buf, frame_start + i * 4)[0]
                        for i in range(nblocks)]
        if hdr_size + sum(block_csizes) != fe.csize:
            raise ValueError("frame body size != frame csize")
        bpos = frame_start + hdr_size
        remaining = fe.dsize
        for bcsz in block_csizes:
            bdsize = block_dsize if remaining >= block_dsize else remaining
            blk = buf[bpos:bpos + bcsz]
            out.extend(zstd_decompress_block(blk, bdsize) if codec == "zstd"
                       else lz4_decompress_block(blk, bdsize))
            bpos += bcsz
            remaining -= bdsize
        pos += fe.csize
    return bytes(out)


def decompress_v2_adv(buf, block_dsize, dict_obj, filters_enabled,
                      base=None, raw_dict=None):
    need_zstd()
    plain_d = zstd.ZstdDecompressor()
    if raw_dict is not None:
        dict_d = zstd.ZstdDecompressor(dict_data=zstd.ZstdCompressionDict(
            raw_dict, dict_type=zstd.DICT_TYPE_RAWCONTENT))
    elif dict_obj is not None:
        dict_d = zstd.ZstdDecompressor(dict_data=dict_obj)
    else:
        dict_d = None

    entries = parse_seek_table(buf)
    out = bytearray()
    pos = 0
    for fe in entries:
        frame_start = pos
        nblocks = (fe.dsize + block_dsize - 1) // block_dsize
        hdr_size = (nblocks * 4 + (nblocks if filters_enabled else 0)
                    + (nblocks * 4 if base is not None else 0))
        if hdr_size > fe.csize:
            raise ValueError(f"frame index {hdr_size} > frame csize {fe.csize}")
        block_csizes = [struct.unpack_from("<I", buf, frame_start + i * 4)[0]
                        for i in range(nblocks)]
        mode_end = frame_start + nblocks * 4 + (nblocks if filters_enabled else 0)
        if filters_enabled:
            modes = list(buf[frame_start + nblocks * 4:mode_end])
        else:
            modes = [NDZ_MODE_DICT if (dict_obj is not None or raw_dict is not None)
                     else NDZ_MODE_PLAIN] * nblocks
        if base is not None:
            base_offs = [struct.unpack_from("<I", buf, mode_end + i * 4)[0]
                         for i in range(nblocks)]
        else:
            base_offs = [NDZ_BASE_SENTINEL] * nblocks
        if hdr_size + sum(block_csizes) != fe.csize:
            raise ValueError("frame body size != frame csize")

        bpos = frame_start + hdr_size
        remaining = fe.dsize
        for bcsz, mode, boff in zip(block_csizes, modes, base_offs):
            bdsize = block_dsize if remaining >= block_dsize else remaining
            blk = buf[bpos:bpos + bcsz]
            if boff != NDZ_BASE_SENTINEL:
                if boff + NDZ_BASE_WINDOW > len(base):
                    raise ValueError(f"base window 0x{boff:X} out of range")
                wd = zstd.ZstdCompressionDict(base[boff:boff + NDZ_BASE_WINDOW],
                                              dict_type=zstd.DICT_TYPE_RAWCONTENT)
                raw = zstd.ZstdDecompressor(dict_data=wd).decompress(
                    blk, max_output_size=bdsize)
            elif mode == NDZ_MODE_DICT:
                raw = dict_d.decompress(blk, max_output_size=bdsize)
            else:
                raw = plain_d.decompress(blk, max_output_size=bdsize)
                if mode in _FILTER_INV:
                    raw = _FILTER_INV[mode](raw)
            out.extend(raw)
            bpos += bcsz
            remaining -= bdsize
        pos += fe.csize
    return bytes(out)


def decode_ndz_blob(blob, base=None):
    """Decode one complete .ndz blob to the original .nds bytes."""
    if len(blob) < 16:
        sys.exit("error: .ndz too short")

    magic, fm_size, orig_size, flags = struct.unpack("<IIII", blob[0:16])
    if magic != NDZ_MAGIC:
        sys.exit(f"error: bad NDZ magic 0x{magic:08X} (a pair container? try info)")
    if fm_size != NDZ_FRONTMATTER_SIZE:
        sys.exit(f"error: unexpected frontMatterSize {fm_size}")

    dict_obj = raw_dict = None
    dict_size = 0
    if flags & NDZ_FLAG_RAWDICT:
        dict_size = struct.unpack_from("<I", blob, NDZ_DICTSIZE_OFFSET)[0]
        rd_dsize = struct.unpack_from("<I", blob, NDZ_RAWDICT_DSIZE_OFFSET)[0]
        if dict_size != rd_dsize:
            sys.exit("error: raw dict must be stored verbatim "
                     "(storedSize != dsize -- old compressed-dict pack?)")
        raw_dict = bytes(blob[fm_size:fm_size + dict_size])
        if len(raw_dict) != rd_dsize:
            sys.exit("error: raw dict section truncated")
    elif flags & NDZ_FLAG_DICT:
        need_zstd()
        dict_size = struct.unpack_from("<I", blob, NDZ_DICTSIZE_OFFSET)[0]
        dict_bytes = blob[fm_size:fm_size + dict_size]
        if len(dict_bytes) != dict_size:
            sys.exit("error: dict section truncated")
        dict_obj = zstd.ZstdCompressionDict(dict_bytes)

    if flags & NDZ_FLAG_BASE:
        if base is None:
            sys.exit("error: this .ndz is a base-patch pack and needs the base ROM:\n"
                     "       pass --base <base.nds>, or unpack the pair container\n"
                     "       that carries both")
        want_size = struct.unpack_from("<I", blob, NDZ_BASE_ORIGSIZE_OFFSET)[0]
        want_gc = blob[NDZ_BASE_GAMECODE_OFFSET:NDZ_BASE_GAMECODE_OFFSET + 4]
        want_hash = blob[NDZ_BASE_HDRHASH_OFFSET:NDZ_BASE_HDRHASH_OFFSET + 8]
        if want_size != len(base):
            sys.exit(f"error: base size mismatch (pack wants {want_size}, got {len(base)})")
        if want_gc != base[0x0C:0x10]:
            sys.exit("error: base gameCode mismatch")
        if want_hash != hashlib.blake2b(base[:0x200], digest_size=8).digest():
            sys.exit("error: base header hash mismatch -- wrong base ROM")

    payload = blob[fm_size + dict_size:]

    if flags & NDZ_FLAG_V2_HIERARCHICAL:
        block_log2 = (flags >> 8) & 0xFF
        block_dsize = 4096 if block_log2 == 0 else (1 << block_log2)
        adv = flags & (NDZ_FLAG_DICT | NDZ_FLAG_FILTERS
                       | NDZ_FLAG_BASE | NDZ_FLAG_RAWDICT)
        if adv:
            data = decompress_v2_adv(
                payload, block_dsize, dict_obj, bool(flags & NDZ_FLAG_FILTERS),
                base if (flags & NDZ_FLAG_BASE) else None, raw_dict)
        else:
            codec = "zstd" if (flags & NDZ_FLAG_ZSTD_BLOCKS) else "lz4"
            data = decompress_v2(payload, block_dsize, codec)
    else:
        # Legacy flat layout. These carry the LZ4B trailer; the ORIGINAL
        # zstd-seekable payload that shipped before it does not.
        trailer = (struct.unpack_from("<I", payload, len(payload) - 4)[0]
                   if len(payload) >= 4 else 0)
        if trailer != LZ4BENCH_MAGIC:
            sys.exit("error: payload has no LZ4B trailer -- this looks like "
                     "the older zstd-seekable .ndz, which this tool cannot read")
        data = decompress_flat(payload)

    if len(data) != orig_size:
        sys.exit(f"error: decoded {len(data)} bytes, front-matter says {orig_size}")
    return bytes(data)


# ------------------------------------------------------------ containers ---

def read_pair_entries(blob):
    """[(offset, size, origSize, gameCode)] for a pair container, else None."""
    if len(blob) < 0x30:
        return None
    magic, _hdr, n_roms, _ = struct.unpack_from("<IIII", blob, 0)
    if magic != NDZ_PAIR_MAGIC:
        return None
    return [struct.unpack_from("<III4s", blob, 0x10 + 0x10 * i)
            for i in range(n_roms)]


def describe_flags(flags):
    names = ["v2"] if flags & NDZ_FLAG_V2_HIERARCHICAL else ["flat"]
    names.append("zstd" if flags & NDZ_FLAG_ZSTD_BLOCKS else "lz4")
    if flags & NDZ_FLAG_DICT:    names.append("trained-dict")
    if flags & NDZ_FLAG_FILTERS: names.append("filters")
    if flags & NDZ_FLAG_BASE:    names.append("base-patch")
    if flags & NDZ_FLAG_RAWDICT: names.append("raw-dict")
    log2 = (flags >> 8) & 0xFF
    names.append(f"block={4096 if log2 == 0 else 1 << log2}")
    return ", ".join(names)


# ------------------------------------------------------------------ verbs ---

def cmd_info(args):
    blob = args.input.read_bytes()
    entries = read_pair_entries(blob)
    if entries is None:
        magic, fm, orig, flags = struct.unpack_from("<IIII", blob, 0)
        if magic != NDZ_MAGIC:
            sys.exit(f"error: not a .ndz -- magic is 0x{magic:08X}, "
                     f"expected 0x{NDZ_MAGIC:08X} ('NDZ1')")
        if fm != NDZ_FRONTMATTER_SIZE:
            sys.exit(f"error: unexpected frontMatterSize {fm}")
        gc = blob[0x10 + NDZ_BANNER_SLOT_SIZE:
                  0x10 + NDZ_BANNER_SLOT_SIZE + 4].decode("ascii", "replace")
        ratio = orig / len(blob) if len(blob) else 0
        print(f"single .ndz  {gc}  {fmt_bytes(len(blob))} -> {fmt_bytes(orig)}  "
              f"({ratio:.3f}x)  [{describe_flags(flags)}]")
    else:
        print(f"pair container, {len(entries)} ROMs:")
        for i, (off, size, orig, gc) in enumerate(entries):
            fl = struct.unpack_from("<IIII", blob, off)[3]
            print(f"  [{i}] {gc.decode('ascii', 'replace')}  "
                  f"{fmt_bytes(size)} -> {fmt_bytes(orig)}  [{describe_flags(fl)}]")


def cmd_unpack(args):
    blob = args.input.read_bytes()
    entries = read_pair_entries(blob)
    base = args.base.read_bytes() if args.base else None

    if entries is None:
        out = args.output or args.input.with_suffix(".nds")
        data = decode_ndz_blob(blob, base)
        out.write_bytes(data)
        print(f"{args.input.name} -> {out}  ({fmt_bytes(len(data))})")
        if args.verify:
            print(f"  sha256 {sha256_file(out)}")
        return

    # A base-patch sub-file resolves its base to the other entry in the SAME
    # file, so decode the self-contained one first and feed it in.
    plain_idx = None
    for i, (off, _s, _o, _g) in enumerate(entries):
        if not (struct.unpack_from("<IIII", blob, off)[3] & NDZ_FLAG_BASE):
            plain_idx = i
            break
    if plain_idx is None:
        sys.exit("error: pair container has no self-contained sub-file to use as base")

    off, size, _o, _g = entries[plain_idx]
    resolved_base = decode_ndz_blob(blob[off:off + size], None)

    targets = range(len(entries)) if args.index is None else [args.index]
    for i in targets:
        if not 0 <= i < len(entries):
            sys.exit(f"error: index {i} out of range (0..{len(entries) - 1})")
        off, size, _o, gc = entries[i]
        data = (resolved_base if i == plain_idx
                else decode_ndz_blob(blob[off:off + size], resolved_base))
        if args.output and args.index is not None:
            out = args.output
        else:
            code = gc.decode("ascii", "replace").strip("\x00") or f"rom{i}"
            out = args.input.with_name(f"{args.input.stem}_{i}_{code}.nds")
        out.write_bytes(data)
        print(f"[{i}] -> {out}  ({fmt_bytes(len(data))})")


def cmd_pack(args):
    need_zstd()
    nds = args.input.read_bytes()
    block_size = parse_size(args.block_size)
    frame_size = parse_size(args.frame_size)
    raw_dict_size = parse_size(args.raw_dict)

    if args.level > NDZ_MAX_LEVEL:
        sys.exit(
            f"error: --level {args.level} exceeds {NDZ_MAX_LEVEL}.\n"
            "       Higher levels decompress too slowly on the DSpico for\n"
            "       the console to keep up. The file would pack fine and\n"
            "       then fail on hardware.")
    if block_size > NDZ_MAX_BLOCK_SIZE:
        sys.exit(
            f"error: --block-size {args.block_size} exceeds "
            f"{NDZ_MAX_BLOCK_SIZE // 1024}k.\n"
            "       Bigger blocks take too long to fetch and decompress on\n"
            "       a cache miss and the console freezes.")
    if frame_size < block_size:
        sys.exit("error: --frame-size must be >= --block-size")

    base_data = base_ctx = None
    if args.base:
        base_data = args.base.read_bytes()
        base_ctx = BaseCtx(base_data, args.level)

    progress = (lambda done, total:
                print(f"\r  {done * 100 // total}%", end="", flush=True)) \
        if args.progress else None

    blob, info = pack_ndz_blob(nds, block_size, frame_size, args.level,
                               not args.no_filters, base_data, base_ctx,
                               raw_dict_size=raw_dict_size, progress=progress)
    if progress:
        print()

    if args.pair_out:
        if base_data is None:
            sys.exit("error: --pair-out needs --base (the pair holds base + patched)")
        base_blob, _ = pack_ndz_blob(base_data, block_size, frame_size,
                                     args.level, not args.no_filters,
                                     None, None, raw_dict_size=raw_dict_size)
        align = NDZ_FRONTMATTER_SIZE
        off_a = align
        pad_a = (-len(base_blob)) % align
        off_b = off_a + len(base_blob) + pad_a
        hdr = bytearray(align)
        struct.pack_into("<IIII", hdr, 0, NDZ_PAIR_MAGIC, align, 2, 0)
        struct.pack_into("<III4s", hdr, 0x10, off_a, len(base_blob),
                         len(base_data), base_data[0x0C:0x10])
        struct.pack_into("<III4s", hdr, 0x20, off_b, len(blob),
                         len(nds), nds[0x0C:0x10])
        container = bytes(hdr) + base_blob + bytes(pad_a) + blob
        args.pair_out.write_bytes(container)
        raw_total = len(base_data) + len(nds)
        print(f"{args.input.name} + {args.base.name} -> {args.pair_out}")
        print(f"  {fmt_bytes(raw_total)} -> {fmt_bytes(len(container))}  "
              f"({raw_total / len(container):.3f}x)")
        out_path = args.pair_out
    else:
        out_path = args.output or args.input.with_suffix(".ndz")
        out_path.write_bytes(blob)
        gc = info["game_code"].decode("ascii", "replace")
        print(f"{args.input.name} -> {out_path}")
        print(f"  {gc}  {fmt_bytes(len(nds))} -> {fmt_bytes(len(blob))}  "
              f"({len(nds) / len(blob):.3f}x)")
        if any(info["mode_hist"]):
            total = sum(info["mode_hist"]) or 1
            parts = [f"{MODE_NAMES[m]} {c} ({100 * c // total}%)"
                     for m, c in enumerate(info["mode_hist"]) if c]
            print("  block modes  " + ", ".join(parts))
        if info["raw_dict"]:
            print(f"  raw dict     {fmt_bytes(info['raw_dict'])} (stored verbatim)")

    if not args.no_verify:
        # Round-trip through the DECODER, so what gets verified is the file on
        # disk read back the way this tool will actually read it.
        blob_out = out_path.read_bytes()
        entries = read_pair_entries(blob_out)
        if entries is None:
            ok = decode_ndz_blob(blob_out, base_data) == nds
        else:
            off, size, _o, _g = entries[0]
            b0 = decode_ndz_blob(blob_out[off:off + size], None)
            off, size, _o, _g = entries[1]
            b1 = decode_ndz_blob(blob_out[off:off + size], b0)
            ok = (b0 == base_data and b1 == nds)
        print("  roundtrip    " + ("OK (byte-exact)" if ok else "FAILED"))
        if not ok:
            sys.exit(1)


# ------------------------------------------------------------------- main ---

def main():
    examples = """
examples
  ndztool.py rom.nds                     pack   -> rom.ndz
  ndztool.py rom.ndz                     unpack -> rom.nds
  ndztool.py info rom.ndz                what is in it, without decoding

  pack
    ndztool.py pack rom.nds out.ndz
    ndztool.py pack rom.nds --level 12 --block-size 4k
    ndztool.py pack rom.nds --no-filters          skip the block filters
    ndztool.py pack rom.nds --raw-dict 8m         dup-weighted shared dict
    ndztool.py pack rom.nds --progress            percent while packing

  base-patch: store only what differs from another ROM
    ndztool.py pack white2.nds out.ndz --base black2.nds
    ndztool.py unpack out.ndz w2.nds   --base black2.nds

  pair container: base + patched in ONE file, no external ROM to unpack
    ndztool.py pack white2.nds --base black2.nds --pair-out pair.ndz
    ndztool.py info   pair.ndz
    ndztool.py unpack pair.ndz                    both, auto-named
    ndztool.py unpack pair.ndz --index 1 w2.nds   just the second

  check a result
    ndztool.py unpack rom.ndz out.nds --verify    prints the sha256

notes
  Two limits are HARDWARE limits and the tool refuses to cross them, because
  a file that packs fine and then fails on the console is worse than an error:
    --level      max 19. Higher decompresses too slowly for the console.
    --block-size max 8k. Bigger takes too long to fetch AND decompress on a
                 cache miss, and the console freezes.
  Below those, smaller blocks read less per cache miss but compress worse;
  8k at level 19 is what ships.

  Every pack is round-tripped through the decoder before it is reported OK,
  so "roundtrip OK" means the file on disk decodes, not just that the packer
  agreed with itself. --no-verify skips it.
"""
    ap = argparse.ArgumentParser(
        description="Pack and unpack .ndz files. "
                    "With no verb, the extension decides: .nds packs, .ndz unpacks.",
        epilog=examples,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="verb")

    p = sub.add_parser("pack", help="compress a .nds into a .ndz")
    p.add_argument("input", type=Path)
    p.add_argument("output", type=Path, nargs="?")
    p.add_argument("--level", type=int, default=19,
                   help="zstd level, 1..19 (default 19; 19 is the max the "
                        "DSpico can decode fast enough)")
    p.add_argument("--block-size", default="8k",
                   help="decode granularity, max 8k (default 8k)")
    p.add_argument("--frame-size", default="128k", help="frame size (default 128k)")
    p.add_argument("--no-filters", action="store_true",
                   help="disable per-block decorrelation filters")
    p.add_argument("--raw-dict", default="0",
                   help="dup-weighted raw dict size, e.g. 8m (default off)")
    p.add_argument("--base", type=Path, help="base .nds for a base-patch pack")
    p.add_argument("--pair-out", type=Path,
                   help="write a pair container holding base + patched")
    p.add_argument("--no-verify", action="store_true",
                   help="skip the decode-side round-trip check")
    p.add_argument("--progress", action="store_true")
    p.set_defaults(func=cmd_pack)

    u = sub.add_parser("unpack", help="decompress a .ndz back to .nds")
    u.add_argument("input", type=Path)
    u.add_argument("output", type=Path, nargs="?")
    u.add_argument("--base", type=Path, help="base .nds, for a base-patch pack")
    u.add_argument("--index", type=int, default=None,
                   help="which ROM to extract from a pair container")
    u.add_argument("--verify", action="store_true", help="print the sha256")
    u.set_defaults(func=cmd_unpack)

    i = sub.add_parser("info", help="identify a .ndz without decoding it")
    i.add_argument("input", type=Path)
    i.set_defaults(func=cmd_info)

    argv = sys.argv[1:]
    if argv and argv[0] not in ("pack", "unpack", "info", "-h", "--help"):
        suffix = Path(argv[0]).suffix.lower()
        if suffix == ".nds":
            argv = ["pack"] + argv
        elif suffix == ".ndz":
            argv = ["unpack"] + argv

    args = ap.parse_args(argv)
    if not getattr(args, "func", None):
        ap.print_help()
        sys.exit(2)
    args.func(args)


if __name__ == "__main__":
    main()
