using System.Buffers.Binary;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// Pack-time index over a base ROM for base-patch mode (flags bit 4): a grain-hash
/// table plus per-block candidate-window search, confirmed directly against
/// `ndztool.py`'s own <c>BaseCtx</c> (see docs/ndz-remaining-work.md's "Base-ROM patch
/// mode" section for the full writeup). Not a diff algorithm - candidates are 16 KiB
/// windows into the base ROM, later used as one-shot raw-content zstd dictionaries.
///
/// The candidate *set* this produces is confirmed identical to `BaseCtx.window_candidates`
/// (same centered-offset seed, same grain-hash lookups, same clamping); which up-to-8
/// subset gets returned when more than 8 candidates exist is not, and can't be, made to
/// match a specific `ndztool.py` run bit-for-bit - Python's own `set` iteration order
/// depends on per-process hash randomization there, so even two `ndztool.py` runs on the
/// identical input aren't guaranteed to agree with each other on which >8 candidates get
/// tried. This doesn't affect interop: decode only ever needs the base-window offset that
/// was actually recorded, never a specific *selection* of it.
/// </summary>
public sealed class BaseRomIndex
{
    /// <summary>Grain size for hashing the base ROM into index buckets - `ndztool.py`'s `BASE_GRAIN`.</summary>
    public const int Grain = 2048;

    /// <summary>Candidate window size, and the size of the raw-content dictionary built from it - `ndztool.py`'s `NDZ_BASE_WINDOW`.</summary>
    public const int WindowSize = 16384;

    /// <summary>Sentinel `baseOff` value meaning "no base window used for this block" - `ndztool.py`'s `NDZ_BASE_SENTINEL`.</summary>
    public const uint NoWindowSentinel = 0xFFFFFFFF;

    private const int MaxOffsetsPerBucket = 4;
    private const int MaxCandidates = 8;

    private readonly byte[] _base;
    private readonly Dictionary<ulong, List<int>> _index = new();

    public BaseRomIndex(byte[] baseRom)
    {
        _base = baseRom ?? throw new ArgumentNullException(nameof(baseRom));
        for (int offset = 0; offset + Grain <= baseRom.Length; offset += Grain)
        {
            ulong key = HashKey(baseRom.AsSpan(offset, Grain));
            if (!_index.TryGetValue(key, out var bucket))
                _index[key] = bucket = new List<int>();
            // A later grain hashing to an already-full bucket is simply not indexed -
            // matches BaseCtx.__init__'s own `if len(bucket) < 4` exactly, not an
            // approximation of it.
            if (bucket.Count < MaxOffsetsPerBucket)
                bucket.Add(offset);
        }
    }

    public int BaseLength => _base.Length;

    /// <summary>
    /// Up to 8 candidate window start offsets for compressing <paramref name="block"/>
    /// (found at decompressed file offset <paramref name="fileOffset"/>) against this
    /// base ROM: one centered on the block's own file offset, plus one per
    /// <see cref="Grain"/>-sized sub-chunk of the block whose grain hash matches
    /// something in the index - deduplicated, every offset clamped so the resulting
    /// window never runs past the base ROM's end.
    /// </summary>
    public IReadOnlyList<int> CandidateWindowOffsets(ReadOnlySpan<byte> block, int fileOffset)
    {
        var candidates = new HashSet<int>();
        int center = Math.Max(0, (WindowSize - block.Length) / 2);
        int hi = Math.Max(0, _base.Length - WindowSize);

        if (fileOffset < _base.Length)
            candidates.Add(Math.Clamp(fileOffset - center, 0, hi));

        for (int sub = 0; sub + Grain <= block.Length; sub += Grain)
        {
            ulong key = HashKey(block.Slice(sub, Grain));
            if (_index.TryGetValue(key, out var bucket))
            {
                foreach (int offset in bucket)
                    candidates.Add(Math.Clamp(offset - sub - center, 0, hi));
            }
        }

        return candidates.Count <= MaxCandidates ? candidates.ToArray() : candidates.Take(MaxCandidates).ToArray();
    }

    /// <summary>
    /// The base ROM bytes for the window starting at <paramref name="offset"/> - exactly
    /// <see cref="WindowSize"/> bytes, except when the base ROM itself is shorter than
    /// that (an unrealistic case for a real NDS ROM, but Python's forgiving slice
    /// semantics allow it in `ndztool.py`, so this mirrors that rather than throwing).
    /// </summary>
    public ReadOnlySpan<byte> GetWindow(int offset)
    {
        int length = Math.Min(WindowSize, _base.Length - offset);
        return _base.AsSpan(offset, Math.Max(0, length));
    }

    private static ulong HashKey(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Blake2b.Hash(data, 8));
}
