# mena-packer

`pack.rs` here is a verbatim copy of the NDZ format author's (Mena Azer,
mena@phenommod.com) own reference packer, shared 2026-08-22. It's the ground truth this
whole port was corrected against — see `docs/ndz-format-spec.md`'s "Corrections from
the reference packer" for what changed in the .NET port as a result (frame vs. block
hierarchy, real trailer magic, the composite flags bitfield, version-aware banner
sizing).

This file is **not part of any buildable project** here (not the .NET solution, not
`reference/ndz-reference/`) — it doesn't compile standalone, since it references two
sibling modules from Mena's own crate that we don't have:

- `crate::census::{dup_census, prefix_dict}` — builds the optional raw content
  dictionary by finding the ROM's own repeated content.
- `crate::filters::{apply_fwd, FILTER_MODES, MODE_DICT, MODE_PLAIN}` — the per-block
  byte-transform modes and their numeric mode-byte values.

It's kept purely as a reference for exact wire-format details (offsets, constants,
control flow) that would otherwise have to be re-derived from a pasted chat snippet.

**Still needed from Mena, if available**: the `census` and `filters` modules
themselves (or at least the `MODE_*` byte values and what each filter transform does).
Without them, `Ndz.Core`'s reader can parse any real `.ndz` file's structure but
deliberately refuses to decode a block tagged with anything other than `MODE_PLAIN` —
see `BlockMode`'s remarks in the .NET port.

Note: `census` and `filters` are *not* the crates.io packages of the same name (checked
— `census` on crates.io is an unrelated object-lifetime-tracking crate, `filters` is an
unrelated predicate-builder crate). The `crate::` prefix on the imports here means
they're local modules in Mena's own project, not published dependencies.
