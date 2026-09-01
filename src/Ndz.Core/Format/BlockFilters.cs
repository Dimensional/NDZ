namespace Ndz.Core.Format;

/// <summary>
/// The reversible byte-level transforms behind <see cref="BlockMode.Delta1"/>/
/// <see cref="BlockMode.Delta2"/>/<see cref="BlockMode.Delta4"/>/
/// <see cref="BlockMode.Shuffle2"/>/<see cref="BlockMode.Shuffle4"/> - confirmed
/// directly against `ndztool.py`'s own `_delta_fwd`/`_delta_inv`/`_shuffle_fwd`/
/// `_shuffle_inv` (see <see cref="BlockMode"/>'s remarks and
/// docs/ndz-remaining-work.md's "Filter modes" section). Applied before compression and
/// undone after, on the plain (never dictionary-primed) codec.
/// </summary>
public static class BlockFilters
{
    /// <summary>
    /// <c>out[i] = source[i] - source[i - stride]</c> (mod 256) for <c>i</c> from the end
    /// down to <paramref name="stride"/>; the first <paramref name="stride"/> bytes pass
    /// through unchanged. Safe to call with <paramref name="destination"/> aliasing
    /// <paramref name="source"/> (in-place): processed descending, so by the time index
    /// <c>i</c> is written, index <c>i - stride</c> hasn't been touched yet this call.
    /// </summary>
    public static void DeltaForward(ReadOnlySpan<byte> source, Span<byte> destination, int stride)
    {
        if (destination.Length != source.Length)
            throw new ArgumentException("Source and destination must be the same length.", nameof(destination));
        // Span.CopyTo uses a memmove, so this is correct whether destination is a
        // separate buffer or source itself (true in-place) - either way, every read
        // below then reads real data (either the original source at a not-yet-modified
        // index, or the just-copied byte at the current index).
        source.CopyTo(destination);
        for (int i = destination.Length - 1; i >= stride; i--)
            destination[i] = (byte)(destination[i] - destination[i - stride]);
    }

    /// <summary>
    /// The inverse of <see cref="DeltaForward"/>: <c>block[i] += block[i - stride]</c>,
    /// ascending from <paramref name="stride"/> - a running prefix sum, so (unlike the
    /// forward transform) it deliberately reads each already-restored predecessor.
    /// Always in place.
    /// </summary>
    public static void DeltaInverse(Span<byte> block, int stride)
    {
        for (int i = stride; i < block.Length; i++)
            block[i] = (byte)(block[i] + block[i - stride]);
    }

    /// <summary>
    /// De-interleaves <paramref name="source"/> into <paramref name="planeCount"/> byte
    /// planes: <c>destination[k*planeLength + p] = source[p*planeCount + k]</c>. Requires
    /// <paramref name="source"/>'s length to be evenly divisible by
    /// <paramref name="planeCount"/> (true for every full block this format ever applies
    /// a filter to - see <see cref="BlockMode"/>'s remarks). Not in-place: this is a full
    /// gather, not a local swap.
    /// </summary>
    public static void ShuffleForward(ReadOnlySpan<byte> source, Span<byte> destination, int planeCount)
    {
        ValidateShuffleLengths(source.Length, destination.Length, planeCount, out int planeLength);
        for (int k = 0; k < planeCount; k++)
            for (int p = 0; p < planeLength; p++)
                destination[k * planeLength + p] = source[p * planeCount + k];
    }

    /// <summary>The inverse scatter of <see cref="ShuffleForward"/>.</summary>
    public static void ShuffleInverse(ReadOnlySpan<byte> source, Span<byte> destination, int planeCount)
    {
        ValidateShuffleLengths(source.Length, destination.Length, planeCount, out int planeLength);
        for (int k = 0; k < planeCount; k++)
            for (int p = 0; p < planeLength; p++)
                destination[p * planeCount + k] = source[k * planeLength + p];
    }

    private static void ValidateShuffleLengths(int sourceLength, int destinationLength, int planeCount, out int planeLength)
    {
        if (destinationLength != sourceLength)
            throw new ArgumentException("Source and destination must be the same length.", nameof(destinationLength));
        if (planeCount <= 0 || sourceLength % planeCount != 0)
            throw new ArgumentException($"Block length ({sourceLength}) must be evenly divisible by the plane count ({planeCount}).", nameof(sourceLength));
        planeLength = sourceLength / planeCount;
    }

    /// <summary>Delta stride for <see cref="BlockMode.Delta1"/>/<see cref="BlockMode.Delta2"/>/<see cref="BlockMode.Delta4"/>, or 0 if <paramref name="mode"/> isn't a delta mode.</summary>
    public static int GetDeltaStride(BlockMode mode) => mode switch
    {
        BlockMode.Delta1 => 1,
        BlockMode.Delta2 => 2,
        BlockMode.Delta4 => 4,
        _ => 0,
    };

    /// <summary>Plane count for <see cref="BlockMode.Shuffle2"/>/<see cref="BlockMode.Shuffle4"/>, or 0 if <paramref name="mode"/> isn't a shuffle mode.</summary>
    public static int GetShufflePlaneCount(BlockMode mode) => mode switch
    {
        BlockMode.Shuffle2 => 2,
        BlockMode.Shuffle4 => 4,
        _ => 0,
    };
}
