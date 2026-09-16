using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ndz.Core.Compression;

/// <summary>
/// Finds exact byte-for-byte matches for a fixed-length target window anywhere in a base
/// ROM, via content-defined chunking (see <see cref="ContentDefinedChunker"/>) rather than
/// <see cref="XDelta.HashChainMatcher"/>'s fixed-position hash-chain search - built once per
/// <see cref="HackContainerWriter.Compress"/> call over the whole base+target ROM pair, then
/// queried once per output block.
///
/// The base ROM is chunked and hashed into a duplicate-content index; the target ROM is
/// independently chunked the same way - content-defined boundaries mean genuinely identical
/// content chunks identically in both, regardless of where it sits in either file - and each
/// target chunk that matches some base chunk gets that base position recorded. A query block
/// matches when every target chunk it overlaps has a recorded base match AND those matches
/// are mutually consistent (the same base-minus-target delta throughout), meaning the whole
/// covered span really is one contiguous duplicate region rather than several single-chunk
/// matches stitched together by coincidence. Always double-checked with a real byte compare
/// before reporting success, so a hash collision can never produce a wrong answer.
/// </summary>
internal sealed class ChunkRunMatcher
{
    private readonly byte[] _baseRom;
    private readonly byte[] _targetRom;
    private readonly List<(int Offset, int Length)> _targetChunks;
    private readonly int[] _matchedBaseOffset; // -1 = no match for this target chunk

    /// <param name="avgChunkSizeLog2">
    /// Forwarded to <see cref="ContentDefinedChunker.Chunk"/> - see its own remarks. Not
    /// currently varied by any production caller (<see cref="HackContainerWriter"/> always
    /// uses the default); exposed for the tuning experiment in
    /// <c>ChunkSizeTuningExperiment.cs</c>.
    /// </param>
    public ChunkRunMatcher(byte[] baseRom, byte[] targetRom, int avgChunkSizeLog2 = 12, int? minChunkSize = null, int? maxChunkSize = null)
    {
        _baseRom = baseRom;
        _targetRom = targetRom;
        int min = minChunkSize ?? ContentDefinedChunker.MinChunkSize;
        int max = maxChunkSize ?? ContentDefinedChunker.MaxChunkSize;

        var baseIndex = new Dictionary<(ulong, ulong), List<int>>();
        foreach (var (offset, length) in ContentDefinedChunker.Chunk(baseRom, avgChunkSizeLog2, min, max))
        {
            var key = HashKey(baseRom, offset, length);
            if (!baseIndex.TryGetValue(key, out var list))
                baseIndex[key] = list = new List<int>();
            list.Add(offset);
        }

        _targetChunks = ContentDefinedChunker.Chunk(targetRom, avgChunkSizeLog2, min, max);
        _matchedBaseOffset = new int[_targetChunks.Count];

        for (int i = 0; i < _targetChunks.Count; i++)
        {
            var (offset, length) = _targetChunks[i];
            _matchedBaseOffset[i] = -1;
            if (!baseIndex.TryGetValue(HashKey(targetRom, offset, length), out var candidates))
                continue;

            var targetSpan = targetRom.AsSpan(offset, length);
            foreach (int candidate in candidates)
            {
                if (candidate + length <= baseRom.Length && baseRom.AsSpan(candidate, length).SequenceEqual(targetSpan))
                {
                    _matchedBaseOffset[i] = candidate;
                    break;
                }
            }
        }
    }

    private static (ulong, ulong) HashKey(byte[] data, int offset, int length)
    {
        Span<byte> digest = stackalloc byte[16];
        MD5.HashData(data.AsSpan(offset, length), digest);
        return (BinaryPrimitives.ReadUInt64LittleEndian(digest), BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));
    }

    /// <summary>Binary search for the chunk whose range contains <paramref name="position"/> (last chunk wins ties at the target's final byte).</summary>
    private int ChunkIndexContaining(int position)
    {
        int lo = 0, hi = _targetChunks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_targetChunks[mid].Offset <= position)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    public bool TryFindBlockMatch(int blockOffset, int blockLength, out int baseOffset)
    {
        baseOffset = 0;
        if (_targetChunks.Count == 0 || blockLength <= 0 || blockOffset + blockLength > _targetRom.Length)
            return false;

        int firstChunk = ChunkIndexContaining(blockOffset);
        int lastChunk = ChunkIndexContaining(blockOffset + blockLength - 1);

        if (_matchedBaseOffset[firstChunk] < 0)
            return false;

        int delta = _matchedBaseOffset[firstChunk] - _targetChunks[firstChunk].Offset;
        for (int i = firstChunk + 1; i <= lastChunk; i++)
        {
            if (_matchedBaseOffset[i] < 0 || _matchedBaseOffset[i] - _targetChunks[i].Offset != delta)
                return false;
        }

        long candidate = (long)blockOffset + delta;
        if (candidate < 0 || candidate + blockLength > _baseRom.Length)
            return false;

        if (!_baseRom.AsSpan((int)candidate, blockLength).SequenceEqual(_targetRom.AsSpan(blockOffset, blockLength)))
            return false;

        baseOffset = (int)candidate;
        return true;
    }
}
