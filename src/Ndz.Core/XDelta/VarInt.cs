namespace Ndz.Core.XDelta;

/// <summary>
/// RFC 3284 §2's variable-length integer encoding: base-128, most-significant chunk first,
/// each byte's high bit (0x80) a continuation flag. <see langword="long"/>-based throughout
/// (not <see langword="int"/>) - a from-scratch design choice, not incidental: the reference
/// `VCDiff` NuGet package this project's own prior investigation tested against
/// (<c>docs/xdelta-vcdiff-notes.md</c>) has a confirmed decode-side bug where these exact
/// fields parse through a 32-bit-capped path despite the wire format itself being unbounded -
/// avoided here by construction rather than patched after the fact.
/// </summary>
public static class VarInt
{
    /// <summary>Appends <paramref name="value"/> to <paramref name="output"/> as a VCDIFF varint.</summary>
    public static void Write(List<byte> output, long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "VCDIFF varints are unsigned.");

        // Emit 7-bit groups most-significant-first: build the byte sequence least-significant
        // first (natural for shifting), then reverse, then OR in the continuation bit on every
        // byte but the last.
        Span<byte> groups = stackalloc byte[10]; // ceil(64/7) = 10
        int count = 0;
        ulong remaining = (ulong)value;
        do
        {
            groups[count++] = (byte)(remaining & 0x7F);
            remaining >>= 7;
        } while (remaining != 0);

        for (int i = count - 1; i >= 0; i--)
            output.Add((byte)(groups[i] | (i == 0 ? 0x00 : 0x80)));
    }

    /// <summary>
    /// Reads one VCDIFF varint starting at <paramref name="offset"/>, advancing it past the
    /// bytes consumed.
    /// </summary>
    /// <exception cref="XDeltaException">
    /// The varint runs past the end of <paramref name="data"/>, or its magnitude would
    /// overflow a signed 64-bit value (ten continuation bytes is already generous for any
    /// real VCDIFF field; a longer sequence means corrupt or hostile input, not a real size).
    /// </exception>
    public static long Read(ReadOnlySpan<byte> data, ref int offset)
    {
        long value = 0;
        for (int i = 0; i < 10; i++)
        {
            if (offset >= data.Length)
                throw new XDeltaException("VCDIFF varint runs past the end of the input.");

            byte b = data[offset++];
            value = (value << 7) | (long)(b & 0x7F);
            if ((b & 0x80) == 0)
                return value;

            if (i == 9)
                throw new XDeltaException("VCDIFF varint is too long (more than 10 continuation bytes).");
        }

        throw new XDeltaException("VCDIFF varint is too long (more than 10 continuation bytes).");
    }
}
