namespace Ndz.Core.XDelta;

/// <summary>
/// A classic hash-chain greedy match finder (à la zlib's deflate) indexing only a fixed
/// source buffer - target positions are never indexed. Promoted 2026-09-16 out of
/// <see cref="VcdiffEncoder"/>'s own private nested class (pure extraction, no behavior
/// change to <see cref="FindBestMatch"/>) so <see cref="Compression.HackContainerWriter"/>
/// can share it too, for a different query: <see cref="VcdiffEncoder"/> wants the longest
/// partial match at a position (greedy LZ77 scanning, accepts a shorter-than-requested
/// match), while the hack-container writer's mode-7 ("Verbatim") search needs a genuine
/// full-length exact match or nothing - see <see cref="FindExactMatch"/>.
/// </summary>
public sealed class HashChainMatcher
{
    public const int HashBytes = 4;

    /// <summary>Default search depth, tuned for <see cref="VcdiffEncoder"/>'s own greedy-match use case - a deeper search (more chain steps) costs more but can find better/longer matches in a source with many repeated 4-byte sequences.</summary>
    public const int DefaultMaxChainSteps = 32;

    private const int HashBits = 17;
    private const int HashSize = 1 << HashBits;

    private readonly byte[] _source;
    private readonly int[] _head;
    private readonly int[] _prev;
    private readonly int _maxChainSteps;

    public HashChainMatcher(byte[] source, int maxChainSteps = DefaultMaxChainSteps)
    {
        _source = source;
        _maxChainSteps = maxChainSteps;
        _head = new int[HashSize];
        Array.Fill(_head, -1);
        _prev = new int[Math.Max(source.Length, 1)];

        if (source.Length >= HashBytes)
        {
            for (int i = 0; i <= source.Length - HashBytes; i++)
            {
                uint h = Hash(source, i);
                _prev[i] = _head[h];
                _head[h] = i;
            }
        }
    }

    /// <summary>Finds the longest match at <paramref name="targetPos"/>, walking up to the configured chain-step limit - may return a match shorter than any particular length the caller wanted.</summary>
    public void FindBestMatch(byte[] target, int targetPos, out int bestPos, out int bestLength)
    {
        bestPos = 0;
        bestLength = 0;
        if (_source.Length < HashBytes || targetPos + HashBytes > target.Length)
            return;

        int maxPossible = target.Length - targetPos;
        uint h = Hash(target, targetPos);
        int candidate = _head[h];
        int steps = 0;

        while (candidate >= 0 && steps < _maxChainSteps)
        {
            int len = MatchLength(candidate, target, targetPos, maxPossible);
            if (len > bestLength)
            {
                bestLength = len;
                bestPos = candidate;
                if (len >= maxPossible)
                    break;
            }
            candidate = _prev[candidate];
            steps++;
        }
    }

    /// <summary>
    /// Finds a source position where exactly <paramref name="length"/> bytes starting at
    /// <paramref name="targetPos"/> in <paramref name="target"/> match byte-for-byte -
    /// unlike <see cref="FindBestMatch"/>, the full length must match, not just the
    /// longest common prefix. Returns false if no chain candidate satisfies that within
    /// the configured chain-step limit (not a failure - the caller falls back to another
    /// encoding for that block).
    /// </summary>
    public bool FindExactMatch(byte[] target, int targetPos, int length, out int sourcePos)
    {
        sourcePos = 0;
        if (length <= 0 || targetPos + length > target.Length || _source.Length < HashBytes || targetPos + HashBytes > target.Length)
            return false;

        uint h = Hash(target, targetPos);
        int candidate = _head[h];
        int steps = 0;

        while (candidate >= 0 && steps < _maxChainSteps)
        {
            if (candidate + length <= _source.Length &&
                _source.AsSpan(candidate, length).SequenceEqual(target.AsSpan(targetPos, length)))
            {
                sourcePos = candidate;
                return true;
            }
            candidate = _prev[candidate];
            steps++;
        }

        return false;
    }

    private int MatchLength(int sourcePos, byte[] target, int targetPos, int maxPossible)
    {
        int len = 0;
        int srcLen = _source.Length;
        while (len < maxPossible && sourcePos + len < srcLen && _source[sourcePos + len] == target[targetPos + len])
            len++;
        return len;
    }

    private static uint Hash(byte[] data, int pos)
    {
        uint h = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
        h *= 2654435761u;
        return h >> (32 - HashBits);
    }
}
