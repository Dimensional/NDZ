namespace Ndz.Core.Compression;

/// <summary>
/// Whether the CLI and GUI's own user-facing tools currently allow *creating* a new
/// pair/N-way container (<see cref="NdzPairWriter"/>). Deliberately NOT consulted
/// anywhere inside <see cref="NdzPairWriter"/> or <see cref="NdzPairContainer"/>
/// themselves - both stay fully functional and fully test-covered either way; this only
/// gates the user-facing entry points (the CLI's `--pair-out`, the GUI's target-grouping
/// UI), which check <see cref="CreationEnabled"/> before ever reaching this code. Reading
/// an EXISTING pair container is never gated by this at all - only creating a new one is,
/// and only through those two tools' own interactive surfaces.
///
/// **Temporarily false (2026-09-09)**, at the user's request: the format author confirmed
/// multi-ROM packing - specifically, whether independent per-entry dictionaries actually
/// work when a base-patched target's own dictionary and the shared base's own dictionary
/// might both need to be resident at once - isn't a finished, confirmed feature yet (see
/// docs/ndz-format-spec.md's "Mena ANSWERED" remarks, and the multi-dictionary discussion
/// in this project's own memory). Flip this back to <c>true</c> once the format author
/// confirms the real intended design - both tools re-enable at once, with none of the
/// underlying pack/write/read code touched.
/// </summary>
public static class PairContainerPolicy
{
    // static readonly, not const: a const bool here would let the compiler prove every
    // "if (!CreationEnabled)" branch statically, flagging the code after it as
    // unreachable (CS0162) everywhere this is checked - real noise for something meant
    // to flip back to true with a one-line edit.
    public static readonly bool CreationEnabled = false;

    /// <summary>Short, user-facing explanation for why creation is currently refused - shown by both the CLI and the GUI.</summary>
    public const string DisabledMessage =
        "Combining multiple ROMs into one pair container is temporarily disabled - the format author " +
        "hasn't confirmed the real multi-ROM dictionary design yet. See docs/ndz-format-spec.md.";
}
