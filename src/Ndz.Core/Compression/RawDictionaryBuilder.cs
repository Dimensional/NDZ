using System.Buffers.Binary;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// Derives a raw-content dictionary directly from a ROM's own repeated content, given
/// only a target size - confirmed directly against `ndztool.py`'s own `_cdc_chunks`/
/// `build_dup_weighted_dict` (and matches `pack.rs`'s equivalent shape,
/// `dup_census`/`prefix_dict(nds, &census, raw_dict_size)` - a size, never external
/// content, though we don't have those two modules' actual algorithm). This is the
/// *only* way either reference implementation ever builds a raw-content dictionary -
/// there is no external-file input anywhere in either one's real interface. A prior
/// version of this project accepted arbitrary externally-supplied dictionary bytes
/// instead (`NdzDictionary`/`--dict &lt;file&gt;`), which was never based on either
/// reference - removed 2026-08-31 once this was noticed and confirmed against both
/// `pack.rs` and `ndztool.py`'s real signatures, neither of which ever took a file.
///
/// Two steps:
/// 1. **Content-defined chunking** (`ContentDefinedChunks`) - a rolling gear hash finds
///    natural chunk boundaries in the data (512 B - 16 KiB, ~4 KiB average) that shift
///    with the content itself, so a repeated region is chunked identically wherever it
///    reappears, even if not aligned to any fixed block boundary.
/// 2. **Dedup-weighted ranking** (`Build`) - chunks that recur are ranked by
///    `(occurrences - 1) * length` (the bytes an ideal dedup would save), a chunk whose
///    first up-to-256 bytes are all one repeated byte value is skipped (not real
///    duplicate *content*, just a long run - e.g. zero-padding), and the highest-value
///    chunks are packed into the dictionary up to the target size.
/// </summary>
public static class RawDictionaryBuilder
{
    // Confirmed against ndztool.py's own _cdc_chunks: a fixed (not random) gear table,
    // (i * 2654435761 + 0x9E3779B9) mod 2^32 for i in 0..255.
    private static readonly uint[] Gear = BuildGearTable();

    private static uint[] BuildGearTable()
    {
        var table = new uint[256];
        for (int i = 0; i < 256; i++)
            table[i] = (uint)(((ulong)i * 2654435761UL + 0x9E3779B9UL) & 0xFFFFFFFFUL);
        return table;
    }

    /// <summary>
    /// Splits <paramref name="data"/> into content-defined chunks via a rolling gear
    /// hash: a boundary is cut once a chunk is at least 512 bytes and the rolling hash's
    /// low 12 bits are all zero (a ~1-in-4096 event, giving a ~4 KiB average chunk size),
    /// or unconditionally once a chunk reaches 16 KiB. Confirmed against `ndztool.py`'s
    /// own `_cdc_chunks`.
    /// </summary>
    public static List<(int Offset, int Length)> ContentDefinedChunks(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        const int minChunk = 512, maxChunk = 16384;
        const uint mask = (1 << 12) - 1;

        var result = new List<(int Offset, int Length)>();
        int start = 0;
        uint h = 0;
        int n = data.Length;
        for (int i = 0; i < n;)
        {
            h = (h << 1) + Gear[data[i]];
            i++;
            if ((i - start >= minChunk && (h & mask) == 0) || i - start >= maxChunk)
            {
                result.Add((start, i - start));
                start = i;
                h = 0;
            }
        }
        if (start < n)
            result.Add((start, n - start));
        return result;
    }

    /// <summary>
    /// Builds a raw-content dictionary from <paramref name="data"/>'s own repeated
    /// content, up to (but not deliberately over) <paramref name="dictSize"/> bytes -
    /// confirmed against `ndztool.py`'s own `build_dup_weighted_dict`. Returns an empty
    /// array if nothing in the data repeats usefully.
    /// </summary>
    public static byte[] Build(byte[] data, int dictSize)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (dictSize <= 0)
            return Array.Empty<byte>();

        var chunks = ContentDefinedChunks(data);

        var counts = new Dictionary<HashKey, int>();
        var representative = new Dictionary<HashKey, (int Offset, int Length)>();
        foreach (var (offset, length) in chunks)
        {
            var key = new HashKey(Blake2b.Hash(data.AsSpan(offset, length), 16));
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
            if (!representative.ContainsKey(key))
                representative[key] = (offset, length);
        }

        // Rank duplicate, non-constant chunks by (occurrences - 1) * length - the bytes
        // an ideal dedup would save - highest value first. Ties (equal value) break by
        // descending hash bytes, matching Python's `sorted(...).reverse()` on
        // (value, hash) tuples exactly; every entry here has a distinct hash by
        // construction (one per unique chunk content), so this fully determines order.
        var ranked = new List<(long Value, HashKey Key)>();
        foreach (var (key, count) in counts)
        {
            if (count <= 1)
                continue;
            var (offset, length) = representative[key];
            int sampleLength = Math.Min(256, data.Length - offset);
            if (!HasMoreThanOneDistinctByte(data.AsSpan(offset, sampleLength)))
                continue;
            ranked.Add(((long)(count - 1) * length, key));
        }
        ranked.Sort((a, b) =>
        {
            int cmp = a.Value.CompareTo(b.Value);
            return cmp != 0 ? cmp : CompareLexicographic(a.Key.Bytes, b.Key.Bytes);
        });
        ranked.Reverse();

        var parts = new List<(int Offset, int Length)>();
        int total = 0;
        foreach (var (_, key) in ranked)
        {
            var (offset, length) = representative[key];
            if (total + length > dictSize)
                continue;
            parts.Add((offset, length));
            total += length;
            if (total >= dictSize - 2048)
                break;
        }

        var result = new byte[total];
        int pos = 0;
        foreach (var (offset, length) in parts)
        {
            data.AsSpan(offset, length).CopyTo(result.AsSpan(pos));
            pos += length;
        }
        return result;
    }

    private static bool HasMoreThanOneDistinctByte(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return false;
        byte first = data[0];
        foreach (byte b in data)
        {
            if (b != first)
                return true;
        }
        return false;
    }

    private static int CompareLexicographic(byte[] a, byte[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0)
                return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>A 16-byte BLAKE2b digest wrapped for use as a content-equality dictionary key.</summary>
    private readonly struct HashKey : IEquatable<HashKey>
    {
        public byte[] Bytes { get; }
        public HashKey(byte[] bytes) => Bytes = bytes;
        public bool Equals(HashKey other) => Bytes.AsSpan().SequenceEqual(other.Bytes);
        public override bool Equals(object? obj) => obj is HashKey other && Equals(other);
        public override int GetHashCode() => BinaryPrimitives.ReadInt32LittleEndian(Bytes);
    }
}
