using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;
using VCDiff.Shared;

namespace Ndz.XDelta;

/// <summary>Thrown when applying or generating an xdelta3/VCDIFF patch fails.</summary>
public sealed class XDeltaException : Exception
{
    public XDeltaException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Applies and generates xdelta3/VCDIFF (RFC 3284) patches using the <c>VCDiff</c> NuGet
/// package (<c>SnowflakePowered/vcdiff</c> -- a pure managed C# implementation, no native
/// binaries, MIT/Apache-2.0 licensed). This is <b>detached reference material, not wired into
/// NDZ's real pipeline</b> -- see this folder's README.md for why, and
/// <c>docs/xdelta-vcdiff-notes.md</c> for the full compatibility investigation this was built
/// from (originally done for a sibling project, Gog.Net, then generalized here since it isn't
/// GOG-specific in any way).
///
/// <para>
/// <b>Confirmed working</b> (round-tripped against a real <c>xdelta3.exe</c> 3.2.0 reference
/// build, both directions, on files from 100 bytes to 213MB across several real-world
/// sources): decode of plain VCDIFF and LZMA-secondary-compressed windows, encode producing
/// output the real xdelta3 tool applies correctly, Adler32 window checksums matching
/// byte-for-byte between this library and the reference tool, and correct handling of
/// multi-window files (address-cache COPY instructions spanning windows).
/// </para>
///
/// <para>
/// <b>Confirmed gaps -- fail loudly rather than silently corrupt, but know about them:</b>
/// </para>
/// <list type="bullet">
/// <item>Decoding a <b>DJW (static Huffman) secondary-compressed window throws
/// <see cref="NotSupportedException"/></b> (wrapped into <see cref="XDeltaException"/>
/// below) -- confirmed by generating a real <c>-S djw</c> file and decoding it. FGK wasn't
/// even compiled into the reference xdelta3 build tested against
/// (<c>SECONDARY_FGK=0</c> in its own <c>xdelta3 config</c> output) and isn't known to be
/// used by any real encoder -- untested, assume unsupported.</item>
/// <item><b>The encoder here never produces secondary-compressed, custom-code-table, or
/// app-header output</b> -- confirmed by reading <c>WindowEncoder.cs</c>'s own source, which
/// hardcodes the delta indicator byte to "uncompressed" and has a comment acknowledging
/// secondary compression isn't implemented on the write side. It can still <i>decode</i>
/// files that use those features (per above); it just can't <i>produce</i> them. Fine for a
/// from-scratch NDZ patch format that doesn't need those extensions, but means this can't
/// be used to exactly reproduce real xdelta3's own encoder output.</item>
/// <item><b>Hard, confirmed size ceiling on both sides of a window or source/dictionary
/// file: stay well under 2 GiB (2<sup>31</sup> bytes).</b> Two independent bugs converge on
/// this number: (1) <c>VcEncoder</c>'s <c>maxBufferSize</c> parameter (MiB) is multiplied out
/// in 32-bit arithmetic -- <c>2048 * 1024 * 1024</c> silently wraps to <c>int.MinValue</c>,
/// confirmed by computing it directly, so <c>maxBufferSize &gt;= 2048</c> is broken before
/// encoding even starts; and (2) <c>WindowEncoder.Output()</c> writes the source/dictionary
/// file's total length via an unchecked <c>(int)</c> cast with no bounds check, so a source
/// file at or beyond 2 GiB has its recorded size silently truncated regardless of
/// <c>maxBufferSize</c>. The decoder has an analogous, independently-confirmed limit: window
/// fields (source segment length/offset, target window length, section lengths) parse
/// through a 32-bit-capped varint path even though the VCDIFF wire format itself is
/// unbounded and a 64-bit parse path exists elsewhere in the same codebase, unused for these
/// fields. <see cref="MaxSafeSize"/> below is set well clear of the exact <c>2_147_483_648</c>
/// boundary as a conservative, enforced guardrail -- see
/// <see cref="Generate(Stream, Stream, Stream, int, bool)"/>'s checks. None of this is
/// inherent to VCDIFF or to xdelta3 generally: the real xdelta3 C tool uses genuine 64-bit
/// internal offsets (<c>xdelta3 config</c> shows <c>XD3_USE_LARGEFILE64=1</c>,
/// <c>sizeof(xoff_t)=8</c>), and its own v3.2.0 release notes list "Harden allocation sizing
/// against integer overflow" and "Clamp the source window size" as real, recent fixes -- this
/// is specific to this C# port's encoder, and matters here because NDZ isn't bound by
/// xdelta3's own conservative 64MB default/max window size the way a real GOG patch always
/// is.</item>
/// </list>
///
/// <para>
/// Not evaluated at all here (out of scope for what this investigation actually tested):
/// whether this library's random-access characteristics (or lack thereof) are compatible
/// with NDZ's real hardware constraint of on-the-fly, bounded-latency block decode on a
/// DSPico flashcart -- VCDIFF is fundamentally a sequential, whole-file-apply format, not a
/// random-byte-range-seekable one, which is <i>why</i> NDZ's existing base-patch mode
/// (flags bit 4) deliberately uses windowed dictionary compression instead of a diff
/// algorithm. This type is meant for a standalone/external patch use case, not as a
/// drop-in replacement for that mode, unless/until confirmed otherwise with the format
/// author.
/// </para>
/// </summary>
public static class XDeltaCodec
{
    /// <summary>
    /// Conservative ceiling for any single window's target length, or for a source/
    /// dictionary file's total length -- kept well clear of the confirmed 2<sup>31</sup>-byte
    /// boundary where this library's encoder silently misbehaves (see the type's remarks).
    /// Not a VCDIFF or xdelta3 limit; specific to this port.
    /// </summary>
    public const long MaxSafeSize = 1_900_000_000; // ~1.77 GiB

    /// <summary>
    /// Applies <paramref name="delta"/> against <paramref name="source"/>, writing the
    /// patched result to <paramref name="target"/>. All three streams must be seekable.
    /// </summary>
    /// <returns>The number of bytes written to <paramref name="target"/>.</returns>
    /// <exception cref="XDeltaException">
    /// The delta is not a valid VCDIFF stream, uses an unsupported secondary compressor
    /// (confirmed: DJW; presumed: FGK), or otherwise failed to apply.
    /// </exception>
    public static long Apply(Stream source, Stream delta, Stream target)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (delta is null) throw new ArgumentNullException(nameof(delta));
        if (target is null) throw new ArgumentNullException(nameof(target));

        var decoder = new VcDecoder(source, delta, target);
        try
        {
            var result = decoder.Decode(out long bytesWritten);
            if (result != VCDiffResult.SUCCESS)
            {
                throw new XDeltaException($"xdelta3/VCDIFF apply failed: decoder returned {result}.");
            }

            return bytesWritten;
        }
        catch (Exception ex) when (ex is not XDeltaException)
        {
            throw new XDeltaException($"xdelta3/VCDIFF apply failed: {ex.Message}", ex);
        }
    }

    /// <summary>File-path convenience for <see cref="Apply(Stream, Stream, Stream)"/>.</summary>
    /// <exception cref="XDeltaException">See <see cref="Apply(Stream, Stream, Stream)"/>.</exception>
    public static long ApplyToFile(string sourcePath, string deltaPath, string outputPath)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
        using var delta = new FileStream(deltaPath, FileMode.Open, FileAccess.Read);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite);
        return Apply(source, delta, output);
    }

    /// <summary>
    /// Generates an xdelta3/VCDIFF patch describing how to turn <paramref name="source"/>
    /// into <paramref name="target"/>, writing it to <paramref name="delta"/>. All three
    /// streams must be seekable; <paramref name="source"/>'s full length is loaded into
    /// memory (this library's own design, not something this wrapper adds).
    /// </summary>
    /// <param name="windowSizeMiB">
    /// Per-window chunk size in MiB. Kept well under the confirmed 2048 MiB overflow
    /// boundary -- see <see cref="MaxSafeSize"/> and the type's remarks. Real xdelta3 itself
    /// defaults to 8 MiB windows and never exceeds 64 MiB; there's rarely a reason to go much
    /// higher here either.
    /// </param>
    /// <param name="includeChecksum">
    /// Whether to embed an Adler32 checksum per window (<c>ChecksumFormat.Xdelta3</c>,
    /// matching real xdelta3's own default behavior and every real GOG patch sampled during
    /// the investigation this was built from). Strongly recommended: without it, a caller
    /// that applies this patch against the wrong source file gets no warning and may
    /// silently produce a corrupt target -- confirmed as the exact warning real xdelta3.exe
    /// itself prints for a checksum-less delta ("the decoded output cannot be verified and a
    /// wrong source may silently produce corrupt output").
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="source"/>'s length, or the requested per-window size, is at or beyond
    /// the confirmed overflow boundary described in this type's remarks.
    /// </exception>
    /// <exception cref="XDeltaException">Encoding otherwise failed.</exception>
    public static void Generate(
        Stream source,
        Stream target,
        Stream delta,
        int windowSizeMiB = 8,
        bool includeChecksum = true)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (delta is null) throw new ArgumentNullException(nameof(delta));

        if (source.Length >= MaxSafeSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source.Length,
                $"Source/dictionary length {source.Length} is at or beyond the confirmed " +
                $"2GiB-adjacent overflow boundary for this encoder (see {nameof(XDeltaCodec)}'s remarks); refusing rather than silently truncating.");
        }

        var windowSizeBytes = (long)windowSizeMiB * 1024 * 1024;
        if (windowSizeMiB >= 2048 || windowSizeBytes >= MaxSafeSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowSizeMiB),
                windowSizeMiB,
                $"A window size of {windowSizeMiB} MiB is at or beyond the confirmed 2048 MiB " +
                "overflow boundary in VcEncoder's own buffer-size arithmetic; refusing rather than silently wrapping.");
        }

        try
        {
            using var encoder = new VcEncoder(source, target, delta, maxBufferSize: windowSizeMiB);
            var result = encoder.Encode(checksumFormat: includeChecksum ? ChecksumFormat.Xdelta3 : ChecksumFormat.None);
            if (result != VCDiffResult.SUCCESS)
            {
                throw new XDeltaException($"xdelta3/VCDIFF generate failed: encoder returned {result}.");
            }
        }
        catch (Exception ex) when (ex is not XDeltaException and not ArgumentOutOfRangeException)
        {
            throw new XDeltaException($"xdelta3/VCDIFF generate failed: {ex.Message}", ex);
        }
    }

    /// <summary>File-path convenience for <see cref="Generate(Stream, Stream, Stream, int, bool)"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">See <see cref="Generate(Stream, Stream, Stream, int, bool)"/>.</exception>
    /// <exception cref="XDeltaException">See <see cref="Generate(Stream, Stream, Stream, int, bool)"/>.</exception>
    public static void GenerateToFile(
        string sourcePath,
        string targetPath,
        string deltaOutputPath,
        int windowSizeMiB = 8,
        bool includeChecksum = true)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read);
        using var target = new FileStream(targetPath, FileMode.Open, FileAccess.Read);
        using var delta = new FileStream(deltaOutputPath, FileMode.Create, FileAccess.Write);
        Generate(source, target, delta, windowSizeMiB, includeChecksum);
    }
}
