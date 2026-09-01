# mena-packer

`pack.rs` here is a verbatim copy of the NDZ format author's (Mena Azer,
mena@phenommod.com) own reference packer, shared 2026-08-22. It's the ground truth this
whole port was corrected against — see `docs/ndz-format-spec.md`'s "Corrections from
the reference packer" for what changed in the .NET port as a result (frame vs. block
hierarchy, real trailer magic, the composite flags bitfield, version-aware banner
sizing).

This file is **not part of any buildable project** here (not the .NET solution) — it
doesn't compile standalone, since it references two sibling modules from Mena's own
crate that we don't have:

- `crate::census::{dup_census, prefix_dict}` — builds the optional raw content
  dictionary by finding the ROM's own repeated content. Now cross-confirmed by
  `reference/mena-patchbench/ndztool.py`'s own `build_dup_weighted_dict`/`_cdc_chunks` -
  content-defined chunking (gear hash) ranked by dedup value.
- `crate::filters::{apply_fwd, FILTER_MODES, MODE_DICT, MODE_PLAIN}` — the per-block
  byte-transform modes and their numeric mode-byte values. Now cross-confirmed by
  `ndztool.py`'s own `_FILTER_FWD`/`_FILTER_INV` - delta and shuffle, see
  `docs/ndz-format-spec.md`'s "Filter modes".

It's kept purely as a reference for exact wire-format details (offsets, constants,
control flow) that would otherwise have to be re-derived from a pasted chat snippet.

**Still needed from Mena, if available**: the `census` and `filters` modules
themselves - not for their algorithms anymore (both are now confirmed via `ndztool.py`,
and fully implemented on the .NET side - see `BlockFilters`/`RawDictionaryBuilder`), just
as the format author's own original Rust source rather than a duplicate.

Note: `census` and `filters` are *not* the crates.io packages of the same name (checked
— `census` on crates.io is an unrelated object-lifetime-tracking crate, `filters` is an
unrelated predicate-builder crate). The `crate::` prefix on the imports here means
they're local modules in Mena's own project, not published dependencies.
