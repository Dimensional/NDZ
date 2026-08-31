// (c) Mena Azer, mena@phenommod.com

//! Native .ndz packer
//! path (zstd, level 19, 8k blocks, filters, optional raw dict; no base-patch).
//!

use crate::census::{dup_census, prefix_dict};
use crate::filters::{apply_fwd, FILTER_MODES, MODE_DICT, MODE_PLAIN};
use rayon::prelude::*;
use std::sync::atomic::{AtomicU64, Ordering};
use zstd::zstd_safe::CParameter;

const NDZ_MAGIC: u32 = 0x315A_444E;
const LZ4BENCH_MAGIC: u32 = 0x4C5A_3442;
const FRONTMATTER_SIZE: usize = 16384;
const BANNER_SLOT_SIZE: usize = 0x2400;
const GAMECODE_OFFSET: usize = 0x10 + BANNER_SLOT_SIZE; // 0x2410
const DICTSIZE_OFFSET: usize = GAMECODE_OFFSET + 4; // 0x2414
const RAWDICT_DSIZE_OFFSET: usize = 0x2428;

const FLAG_V2: u32 = 1 << 0;
const FLAG_ZSTD: u32 = 1 << 1;
const FLAG_FILTERS: u32 = 1 << 3;
const FLAG_RAWDICT: u32 = 1 << 5;

pub const BLOCK: usize = 8192;
pub const FRAME: usize = 128 << 10;
pub const LEVEL: i32 = 19;

fn u32le(d: &[u8], o: usize) -> u32 {
    u32::from_le_bytes([d[o], d[o + 1], d[o + 2], d[o + 3]])
}
fn put_u32(v: &mut Vec<u8>, x: u32) {
    v.extend_from_slice(&x.to_le_bytes());
}

fn wlog(n: usize) -> u32 {
    let bits = if n <= 1 { 0 } else { (n - 1).ilog2() + 1 };
    bits.max(10)
}

fn block_log2(block: usize) -> u32 {
    block.trailing_zeros()
}

pub struct PackInfo {
    pub total: usize,
    pub raw_dict: usize,
    pub payload: usize,
    pub n_frames: usize,
    pub n_blocks: usize,
    pub mode_hist: [u64; 7],
}

fn build_frontmatter(nds: &[u8], flags: u32, dict_stored: usize) -> Result<Vec<u8>, String> {
    if nds.len() < 0x200 {
        return Err("input too small for .nds".into());
    }
    let boff = u32le(nds, 0x68) as usize;
    if boff == 0 || boff >= nds.len() {
        return Err(format!("invalid bannerOffset 0x{:X}", boff));
    }
    let version = u16::from_le_bytes([nds[boff], nds[boff + 1]]);
    let mut banner_size = if version >= 0x0103 {
        0x23C0
    } else if version >= 3 {
        0xA40
    } else if version >= 2 {
        0x940
    } else {
        0x840
    };
    if boff + banner_size > nds.len() {
        banner_size = nds.len() - boff;
    }
    if banner_size > BANNER_SLOT_SIZE {
        return Err(format!("banner size 0x{:X} exceeds slot", banner_size));
    }

    let mut fm = vec![0u8; FRONTMATTER_SIZE];
    fm[0..4].copy_from_slice(&NDZ_MAGIC.to_le_bytes());
    fm[4..8].copy_from_slice(&(FRONTMATTER_SIZE as u32).to_le_bytes());
    fm[8..12].copy_from_slice(&(nds.len() as u32).to_le_bytes());
    fm[12..16].copy_from_slice(&flags.to_le_bytes());
    fm[0x10..0x10 + banner_size].copy_from_slice(&nds[boff..boff + banner_size]);
    fm[GAMECODE_OFFSET..GAMECODE_OFFSET + 4].copy_from_slice(&nds[0x0C..0x10]);
    if dict_stored > 0 {
        fm[DICTSIZE_OFFSET..DICTSIZE_OFFSET + 4]
            .copy_from_slice(&(dict_stored as u32).to_le_bytes());
        fm[RAWDICT_DSIZE_OFFSET..RAWDICT_DSIZE_OFFSET + 4]
            .copy_from_slice(&(dict_stored as u32).to_le_bytes());
    }
    Ok(fm)
}

fn mk_plain() -> zstd::bulk::Compressor<'static> {
    let mut c = zstd::bulk::Compressor::new(LEVEL).unwrap();
    tune(&mut c, wlog(BLOCK));
    c
}
fn mk_rawdict(dict: &[u8]) -> zstd::bulk::Compressor<'static> {
    let wl = wlog(dict.len() + BLOCK).max(15);
    let mut c = zstd::bulk::Compressor::with_dictionary(LEVEL, dict).unwrap();
    tune(&mut c, wl);
    c
}
fn tune(c: &mut zstd::bulk::Compressor, wl: u32) {
    let _ = c.set_parameter(CParameter::ContentSizeFlag(false));
    let _ = c.set_parameter(CParameter::ChecksumFlag(false));
    let _ = c.set_parameter(CParameter::DictIdFlag(false));
    let _ = c.set_parameter(CParameter::WindowLog(wl));
}

/// (mode, compressed_bytes) — smallest of {plain, raw-dict, plain+filter}.
fn compress_block_best(
    blk: &[u8],
    plain: &mut zstd::bulk::Compressor,
    rawdict: Option<&mut zstd::bulk::Compressor>,
    filters: bool,
) -> (u8, Vec<u8>) {
    let mut best_mode = MODE_PLAIN;
    let mut best = plain.compress(blk).unwrap();
    if let Some(rd) = rawdict {
        let c = rd.compress(blk).unwrap();
        if c.len() < best.len() {
            best_mode = MODE_DICT;
            best = c;
        }
    }
    if filters && blk.len() == BLOCK {
        for &mode in &FILTER_MODES {
            let c = plain.compress(&apply_fwd(mode, blk)).unwrap();
            if c.len() < best.len() {
                best_mode = mode;
                best = c;
            }
        }
    }
    (best_mode, best)
}

struct FrameOut {
    bytes: Vec<u8>,
    dsize: u32,
    mode_hist: [u64; 7],
    n_blocks: usize,
}

fn compress_frame(
    fchunk: &[u8],
    plain: &mut zstd::bulk::Compressor,
    mut rd: Option<&mut zstd::bulk::Compressor>,
) -> FrameOut {
    let mut csizes: Vec<u32> = Vec::new();
    let mut modes: Vec<u8> = Vec::new();
    let mut blocks: Vec<u8> = Vec::new();
    let mut mode_hist = [0u64; 7];
    let mut n_blocks = 0usize;

    let mut bstart = 0usize;
    while bstart < fchunk.len() {
        let bend = (bstart + BLOCK).min(fchunk.len());
        let blk = &fchunk[bstart..bend];
        let (mode, comp) = compress_block_best(blk, plain, rd.as_deref_mut(), true);
        csizes.push(comp.len() as u32);
        modes.push(mode);
        blocks.extend_from_slice(&comp);
        mode_hist[mode as usize] += 1;
        n_blocks += 1;
        bstart = bend;
    }

    let mut bytes = Vec::with_capacity(csizes.len() * 4 + modes.len() + blocks.len());
    for cs in &csizes {
        put_u32(&mut bytes, *cs);
    }
    bytes.extend_from_slice(&modes); // filters on
    bytes.extend_from_slice(&blocks);
    FrameOut {
        dsize: fchunk.len() as u32,
        bytes,
        mode_hist,
        n_blocks,
    }
}

/// Pack `nds` into a complete .ndz blob.
pub fn pack(
    nds: &[u8],
    raw_dict_size: usize,
    progress: &AtomicU64,
) -> Result<(Vec<u8>, PackInfo), String> {
    let mut flags = FLAG_V2 | FLAG_ZSTD | (block_log2(BLOCK) << 8);
    flags |= FLAG_FILTERS; // ndz_studio always packs with filters on.


    let mut raw_dict: Vec<u8> = Vec::new();
    if raw_dict_size > 0 {
        let census = dup_census(nds);
        let d = prefix_dict(nds, &census, raw_dict_size);
        if d.len() >= 4096 {
            raw_dict = d;
            flags |= FLAG_RAWDICT;
        }
    }

    let fm = build_frontmatter(nds, flags, raw_dict.len())?;

    // frame boundaries
    let mut ranges: Vec<(usize, usize)> = Vec::new();
    let mut fstart = 0usize;
    while fstart < nds.len() {
        let fend = (fstart + FRAME).min(nds.len());
        ranges.push((fstart, fend));
        fstart = fend;
    }


    let threads = rayon::current_num_threads().max(1);
    let group_len = ((ranges.len() + threads - 1) / threads).max(1);
    let groups: Vec<&[(usize, usize)]> = ranges.chunks(group_len).collect();
    let dict = &raw_dict;

    let group_results: Vec<Vec<FrameOut>> = groups
        .par_iter()
        .map(|group| {
            let mut plain = mk_plain();
            let mut rd = if dict.is_empty() { None } else { Some(mk_rawdict(dict)) };
            group
                .iter()
                .map(|&(fs, fe)| {
                    let out = compress_frame(&nds[fs..fe], &mut plain, rd.as_mut());
                    progress.fetch_add((fe - fs) as u64, Ordering::Relaxed);
                    out
                })
                .collect()
        })
        .collect();

    // stitch in order
    let mut payload: Vec<u8> = Vec::new();
    let mut frame_table: Vec<(u32, u32)> = Vec::new();
    let mut mode_hist = [0u64; 7];
    let mut n_blocks = 0usize;
    for group in &group_results {
        for f in group {
            frame_table.push((f.bytes.len() as u32, f.dsize));
            payload.extend_from_slice(&f.bytes);
            for i in 0..7 {
                mode_hist[i] += f.mode_hist[i];
            }
            n_blocks += f.n_blocks;
        }
    }

    for (cs, ds) in &frame_table {
        put_u32(&mut payload, *cs);
        put_u32(&mut payload, *ds);
    }
    put_u32(&mut payload, frame_table.len() as u32);
    put_u32(&mut payload, LZ4BENCH_MAGIC);

    let mut blob = Vec::with_capacity(fm.len() + raw_dict.len() + payload.len());
    blob.extend_from_slice(&fm);
    blob.extend_from_slice(&raw_dict);
    let payload_len = payload.len();
    blob.extend_from_slice(&payload);

    let info = PackInfo {
        total: blob.len(),
        raw_dict: raw_dict.len(),
        payload: payload_len,
        n_frames: frame_table.len(),
        n_blocks,
        mode_hist,
    };
    Ok((blob, info))
}
