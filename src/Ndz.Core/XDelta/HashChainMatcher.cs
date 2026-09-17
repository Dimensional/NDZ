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

    /// <summary>
    /// Default hash-table width (2^17 = 131,072 buckets) - fine for <see cref="VcdiffEncoder"/>'s
    /// typically small-to-medium sources. A large source (e.g. a 256 MB ROM, as
    /// <see cref="Compression.HackContainerWriter"/> indexes) needs a wider table - see
    /// <see cref="RecommendedHashBits"/> - or unrelated 4-byte prefix collisions dominate every
    /// bucket and a fixed chain-step budget stops finding genuine matches long before it reaches
    /// them.
    /// </summary>
    public const int DefaultHashBits = 17;

    private readonly byte[] _source;
    private readonly int[] _head;
    private readonly int[] _prev;
    private readonly int _maxChainSteps;
    private readonly int _hashShift;
    private readonly int _keyBytes;

    /// <param name="keyBytes">
    /// How many leading bytes of each position feed the hash - defaults to
    /// <see cref="HashBytes"/> (4), reproducing <see cref="VcdiffEncoder"/>'s exact original
    /// bucketing bit-for-bit (the extra fold loop in <see cref="Hash"/> simply doesn't run).
    /// A large, real-world source (e.g. <see cref="Compression.HackContainerWriter"/>'s
    /// 256 MB ROM) needs more: with only 4 key bytes, most of a bucket's entries are
    /// unrelated data that merely shares the same 4-byte prefix, not a real candidate for
    /// <see cref="FindExactMatch"/>'s much stricter full-length requirement, so a fixed
    /// chain-step budget spends nearly all of itself on false positives and gives up before
    /// reaching a genuine match. A true full-length match always shares its first
    /// <paramref name="keyBytes"/> bytes trivially, so widening this never loses recall -
    /// it only prunes candidates that could never have satisfied <see cref="FindExactMatch"/>
    /// anyway. Confirmed empirically: widening from 4 to 16 raised real-sample Verbatim
    /// coverage from 56% to ndz-studio's own observed ~99%.
    /// </param>
    /// <param name="maxDegreeOfParallelism">
    /// Bounds the hash-precompute pass's own parallelism (see the constructor's remarks) -
    /// same <see cref="ParallelOptions.MaxDegreeOfParallelism"/> convention used throughout
    /// this codebase (e.g. <see cref="Compression.NdzWriter.Compress"/>,
    /// <see cref="VcdiffEncoder.Encode"/>). -1 (the default) means no limit.
    /// </param>
    public HashChainMatcher(byte[] source, int maxChainSteps = DefaultMaxChainSteps, int hashBits = DefaultHashBits, int keyBytes = HashBytes, int maxDegreeOfParallelism = -1)
    {
        if (hashBits is < 1 or > 27)
            throw new ArgumentOutOfRangeException(nameof(hashBits), hashBits, "Hash width must leave the bucket array (2^hashBits ints) within a sane memory budget.");
        if (keyBytes < HashBytes)
            throw new ArgumentOutOfRangeException(nameof(keyBytes), keyBytes, $"Key length can't be shorter than the base {HashBytes}-byte hash.");

        _source = source;
        _maxChainSteps = maxChainSteps;
        _hashShift = 32 - hashBits;
        _keyBytes = keyBytes;
        _head = new int[1 << hashBits];
        Array.Fill(_head, -1);
        _prev = new int[Math.Max(source.Length, 1)];

        if (source.Length >= _keyBytes)
        {
            // Building the chain links (_prev[i] = _head[h]; _head[h] = i;) is an inherently
            // sequential linked-list insert - but computing each position's own hash isn't,
            // and for a real ROM-sized source that hash pass (reading source bytes + a few
            // multiplies per position, hundreds of millions of times) is the dominant cost,
            // confirmed empirically: building this chain for a 256 MB source took ~2.8s of a
            // ~4.7s encode, almost entirely on a single core. Precomputing hashes in parallel
            // then linking sequentially (cheap - just array reads/writes, no hashing) keeps
            // the link step's ordering guarantee (later positions win ties within a bucket,
            // same lookup preference as before) while using every core for the expensive part.
            int count = source.Length - _keyBytes + 1;
            var hashes = new uint[count];
            Parallel.For(0, count,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
                i => hashes[i] = Hash(source, i));

            for (int i = 0; i < count; i++)
            {
                uint h = hashes[i];
                _prev[i] = _head[h];
                _head[h] = i;
            }
        }
    }

    /// <summary>
    /// A hash width whose bucket count keeps the average chain length near
    /// <paramref name="targetAvgChainLength"/> for a source of <paramref name="sourceLength"/>
    /// bytes, clamped to a sane memory budget (2^24 buckets = 64 MB max by default). Below
    /// <see cref="DefaultHashBits"/>'s own bucket count this just returns
    /// <see cref="DefaultHashBits"/> - widening never hurts small sources, but there's no need
    /// to shrink below the tuned-for-VCDIFF default either.
    /// </summary>
    public static int RecommendedHashBits(int sourceLength, int targetAvgChainLength = 16, int maxHashBits = 24)
    {
        int bits = DefaultHashBits;
        while (bits < maxHashBits && (sourceLength >> bits) > targetAvgChainLength)
            bits++;
        return bits;
    }

    /// <summary>Finds the longest match at <paramref name="targetPos"/>, walking up to the configured chain-step limit - may return a match shorter than any particular length the caller wanted.</summary>
    public void FindBestMatch(byte[] target, int targetPos, out int bestPos, out int bestLength)
    {
        bestPos = 0;
        bestLength = 0;
        if (_source.Length < _keyBytes || targetPos + _keyBytes > target.Length)
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
        if (length <= 0 || targetPos + length > target.Length || _source.Length < _keyBytes || targetPos + _keyBytes > target.Length)
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

    private uint Hash(byte[] data, int pos)
    {
        uint h = (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));
        h *= 2654435761u;
        // No-op when _keyBytes == HashBytes (the default) - reproduces the original 4-byte
        // formula bit-for-bit, so VcdiffEncoder's own byte-exact-vs-xdelta3 behavior is
        // untouched. Only a caller that opts into a wider keyBytes (see the constructor's
        // remarks) folds in the extra bytes here.
        for (int i = pos + HashBytes; i < pos + _keyBytes; i++)
        {
            h ^= data[i];
            h *= 2654435761u;
        }
        return h >> _hashShift;
    }
}
