namespace Ndz.Core.XDelta;

/// <summary>
/// Generates an RFC 3284 (VCDIFF) delta describing how to turn <paramref name="source"/>
/// (the base ROM) into a target ROM. Emits multiple windows when the target is large
/// (<see cref="TargetWindowSize"/> bytes' worth of target data per window) rather than one
/// window covering the whole file - real <c>xdelta3.exe</c> refuses to decode a window whose
/// TARGET length exceeds its compiled-in <c>XD3_HARDMAXWINSIZE</c> (64 MB in the stock
/// Windows build - confirmed directly in that build's own downloaded source,
/// <c>xdelta3-decode.h</c>'s <c>DEC_TGTLEN</c> case: the check is <c>stream->dec_tgtlen >
/// XD3_HARDMAXWINSIZE</c>, purely the target window's own declared length - the source
/// segment length is never compared against it at all), confirmed empirically too: a
/// single-window encode of a 256 MB ROM pair produced a patch our own decoder read back fine
/// but real xdelta3.exe rejected outright ("hard window size exceeded"). Each window's source
/// segment is simply the tight bounding box of whatever COPY addresses its own matches
/// actually used - unconstrained, since nothing on the decode side requires bounding it (an
/// earlier cut of this encoder also tried to cap the source segment's span per window,
/// modeled on a wrong guess about what the real hard-cap check covered - confirmed wrong by
/// reading the actual xdelta3 source, and it was actively harmful: rejecting far-apart
/// candidates only after already paying for a full match-length scan on each one turned a
/// ~9s encode of that same 256 MB pair into one that hadn't finished after several minutes).
/// Match-finding is a greedy hash-chain LZ77 scan against <paramref name="source"/> only (not
/// against already-emitted target bytes - a deliberate v1 simplification; the dominant
/// real-world case, a ROM hack or a version diff, is almost entirely covered by source
/// content, not by repeats within the target that aren't already in the source). Always uses
/// each instruction type's "generic" code-table opcode (size always follows as an explicit
/// varint, never packed into the opcode byte itself) rather than the more compact
/// combined/literal-size opcodes real xdelta3 also uses - simpler, fully valid RFC 3284
/// output any compliant decoder (including this project's own <see cref="VcdiffDecoder"/>,
/// real `xdelta3.exe`, and ndz-studio) applies correctly, just not maximally packed.
/// Secondary (DJW) compression of the output sections is wired in separately - see
/// <see cref="Djw.DjwCodec"/>.
/// </summary>
public static class VcdiffEncoder
{
    /// <summary>A COPY match shorter than this is cheaper to leave as literal ADD bytes, given this encoder's generic-opcode-only instruction encoding.</summary>
    public const int MinMatchLength = 8;

    /// <summary>A repeated-byte run shorter than this is cheaper to leave as literal ADD bytes than to pay a RUN instruction's own overhead.</summary>
    public const int MinRunLength = 4;

    /// <summary>How much target data each window covers - matches real xdelta3's own default (<c>XD3_DEFAULT_WINSIZE</c> = 8 MiB), not chosen independently. Comfortably under its <c>XD3_HARDMAXWINSIZE</c> hard cap (64 MB) - see the class remarks for exactly what that cap checks.</summary>
    public const int TargetWindowSize = 8 * 1024 * 1024;

    /// <summary>
    /// Generates a VCDIFF delta from <paramref name="source"/> to <paramref name="target"/>,
    /// windowed per <see cref="TargetWindowSize"/> (see the class remarks). Each window's
    /// three sections (ADD/RUN data, instructions, addresses) are independently tried against
    /// <see cref="Djw.DjwCodec"/> and only kept compressed when it actually pays for itself
    /// (matching real xdelta3's own per-section efficiency checks) - a section that doesn't
    /// compress well is stored plain, same as real xdelta3.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">
    /// Windows are fully independent (each only reads the shared <see cref="HashChainMatcher"/>,
    /// never mutates it) and DJW compression is the dominant per-window cost, so encoding them
    /// in parallel is a straightforward, safe win on a multi-window (large-target) encode -
    /// same <see cref="ParallelOptions.MaxDegreeOfParallelism"/> convention used throughout
    /// this codebase (e.g. <see cref="Compression.NdzWriter.Compress"/>). -1 (the default)
    /// means no limit - every core.
    /// </param>
    public static byte[] Encode(byte[] source, byte[] target, int maxDegreeOfParallelism = -1)
    {
        // Shared across every window - matching against the full source is unaffected by
        // windowing (only which addresses a window is allowed to actually USE is bounded),
        // and building the hash chain once is far cheaper than rebuilding it per window.
        var matcher = new HashChainMatcher(source, hashBits: HashChainMatcher.RecommendedHashBits(source.Length), maxDegreeOfParallelism: maxDegreeOfParallelism);

        // Computed in `long` and only narrowed back to `int` after the division - the
        // pre-division sum can exceed int.MaxValue for a target within ~8 MB of it (still a
        // valid array length), which would otherwise silently wrap to a garbage (possibly
        // negative) window count.
        int windowCount = target.Length == 0 ? 1 : (int)(((long)target.Length + TargetWindowSize - 1) / TargetWindowSize);
        var windows = new byte[windowCount][];
        var compressedFlags = new bool[windowCount];

        try
        {
            Parallel.For(0, windowCount,
                new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
                w =>
                {
                    // `long` arithmetic here for the same reason windowCount above is: for
                    // a target within ~8 MB of int.MaxValue, `w * TargetWindowSize` or
                    // `windowStart + TargetWindowSize` in plain `int` math can overflow
                    // before the final values (which do fit in `int` - bounded by
                    // target.Length) are ever reached.
                    int windowStart = target.Length == 0 ? 0 : (int)((long)w * TargetWindowSize);
                    int windowEnd = target.Length == 0 ? 0 : (int)Math.Min((long)windowStart + TargetWindowSize, target.Length);
                    windows[w] = EncodeWindow(source, target, windowStart, windowEnd, matcher, out bool compressed);
                    compressedFlags[w] = compressed;
                });
        }
        catch (AggregateException ex)
        {
            // Parallel.For wraps every window's exception in an AggregateException, unlike
            // the pre-windowing single-pass encoder this replaced - unwrapped here so a
            // caller's existing catch (e.g. by exact exception type, or just for the real
            // message) keeps working the same as it did before windowing. Only the first
            // failure is surfaced when several windows fail at once (rare in practice - most
            // real failure causes, like a malformed source/target, affect every window the
            // same way), matching how a plain sequential loop would have stopped at its own
            // first exception anyway.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw; // unreachable - Throw() above always throws - satisfies the compiler's definite-return-or-throw analysis.
        }

        bool anyCompressed = Array.Exists(compressedFlags, c => c);

        var output = new List<byte>();
        output.AddRange(VcdiffFormat.Magic);
        output.Add(anyCompressed ? VcdiffFormat.HdrDecompress : (byte)0);
        if (anyCompressed)
            output.Add(VcdiffFormat.SecondaryDjw);

        foreach (byte[] window in windows)
            output.AddRange(window);

        return output.ToArray();
    }

    /// <summary>
    /// Encodes one window covering <paramref name="target"/>[<paramref name="windowStart"/>..<paramref name="windowEnd"/>)
    /// and returns its complete on-wire record (window indicator through the three sections).
    /// Two passes: the first finds every match in the window (unconstrained - see the class
    /// remarks on why there's nothing to bound here) and from them determines the source
    /// segment's tight bounding box; the second replays those matches to actually emit
    /// instructions, now that the final source-segment start (and therefore every relative
    /// COPY address) is known. A single forward pass can't do both at once - the address of
    /// the FIRST match in a window depends on the window's source-segment start, which isn't
    /// final until the LAST match (the bounding box only ever grows as the window is built).
    /// </summary>
    private static byte[] EncodeWindow(byte[] source, byte[] target, int windowStart, int windowEnd, HashChainMatcher matcher, out bool anyCompressed)
    {
        List<(int TargetPos, int SourcePos, int Length)> matches = FindWindowMatches(source, target, windowStart, windowEnd, matcher);

        int sourceSegStart = 0, sourceSegLength = 0;
        if (matches.Count > 0)
        {
            int min = int.MaxValue, max = int.MinValue;
            foreach (var m in matches)
            {
                min = Math.Min(min, m.SourcePos);
                max = Math.Max(max, m.SourcePos + m.Length);
            }
            sourceSegStart = min;
            sourceSegLength = max - min;
        }

        var addRun = new List<byte>();
        var instructions = new List<byte>();
        var addresses = new List<byte>();
        var cache = new AddressCache();

        int cursor = windowStart;
        foreach (var (targetPos, sourcePos, length) in matches)
        {
            if (targetPos > cursor)
                EmitLiteralRun(target, cursor, targetPos, addRun, instructions);

            long here = sourceSegLength + (targetPos - windowStart);
            long relativeAddress = sourcePos - sourceSegStart;
            int mode = cache.EncodeAddress(relativeAddress, here, out long encodedValue);
            instructions.Add((byte)(19 + 16 * mode));
            VarInt.Write(instructions, length);
            if (cache.IsSameMode(mode))
                addresses.Add((byte)encodedValue);
            else
                VarInt.Write(addresses, encodedValue);

            cursor = targetPos + length;
        }
        if (cursor < windowEnd)
            EmitLiteralRun(target, cursor, windowEnd, addRun, instructions);

        (byte[] addRunBytes, bool addRunCompressed) = TryDjwCompress(addRun);
        (byte[] instructionsBytes, bool instructionsCompressed) = TryDjwCompress(instructions);
        (byte[] addressesBytes, bool addressesCompressed) = TryDjwCompress(addresses);
        anyCompressed = addRunCompressed || instructionsCompressed || addressesCompressed;

        byte winIndicator = VcdiffFormat.WinChecksum;
        bool usesSource = sourceSegLength > 0;
        if (usesSource)
            winIndicator |= VcdiffFormat.WinSource;

        var window = new List<byte> { winIndicator };
        if (usesSource)
        {
            VarInt.Write(window, sourceSegLength);
            VarInt.Write(window, sourceSegStart);
        }

        int targetWindowLength = windowEnd - windowStart;
        var rest = new List<byte>();
        VarInt.Write(rest, targetWindowLength);
        byte deltaIndicator = 0;
        if (addRunCompressed) deltaIndicator |= VcdiffFormat.DeltaDataComp;
        if (instructionsCompressed) deltaIndicator |= VcdiffFormat.DeltaInstComp;
        if (addressesCompressed) deltaIndicator |= VcdiffFormat.DeltaAddrComp;
        rest.Add(deltaIndicator);
        VarInt.Write(rest, addRunBytes.Length);
        VarInt.Write(rest, instructionsBytes.Length);
        VarInt.Write(rest, addressesBytes.Length);

        uint checksum = Adler32.Compute(target.AsSpan(windowStart, targetWindowLength));
        rest.Add((byte)(checksum >> 24));
        rest.Add((byte)(checksum >> 16));
        rest.Add((byte)(checksum >> 8));
        rest.Add((byte)checksum);

        long deltaEncodingLength = rest.Count + addRunBytes.Length + instructionsBytes.Length + addressesBytes.Length;
        VarInt.Write(window, deltaEncodingLength);
        window.AddRange(rest);
        window.AddRange(addRunBytes);
        window.AddRange(instructionsBytes);
        window.AddRange(addressesBytes);

        return window.ToArray();
    }

    /// <summary>
    /// Tries DJW-compressing a section; falls back to the plain bytes if DJW declines (not
    /// worth it) or the section is empty. Also verifies the compressed output actually
    /// decompresses back to the original bytes before trusting it - windowing's much
    /// smaller, lower-diversity per-window sections surfaced a real bitstream-desync bug in
    /// <see cref="Djw.DjwCodec"/>'s group-count handling (root-caused and fixed - see
    /// <see cref="Djw.DjwCodec"/>'s own remarks), but DJW is a pure space optimization, never
    /// required for correctness, so this check costs little and stays as a permanent
    /// defense-in-depth against any future edge case in that fairly intricate port, not just
    /// the one already found.
    /// </summary>
    private static (byte[] bytes, bool compressed) TryDjwCompress(List<byte> section)
    {
        if (section.Count == 0)
            return (Array.Empty<byte>(), false);

        byte[] plain = section.ToArray();
        try
        {
            byte[]? compressed = Djw.DjwCodec.Compress(plain);
            if (compressed is null || compressed.Length >= plain.Length)
                return (plain, false);

            if (Djw.DjwCodec.Decompress(compressed).AsSpan().SequenceEqual(plain))
                return (compressed, true);
        }
        catch (Exception)
        {
            // Falls through to the plain-bytes fallback below - covers both Compress() and
            // Decompress(). DJW is optional, so ANY failure anywhere in that fairly
            // intricate ported codec should degrade to "store this section uncompressed,"
            // never abort the encode entirely. An earlier version of this filtered to a
            // specific exception-type list (matching only the one failure mode actually
            // observed) - genuinely narrower than this method's own stated intent, so
            // broadened to match: a differently-shaped future edge case in that codec
            // should still degrade gracefully rather than need its exact exception type
            // added to a list first.
        }

        return (plain, false);
    }

    /// <summary>
    /// First pass over one window: finds matches via <paramref name="matcher"/>, clamping any
    /// match that would cross <paramref name="windowEnd"/> (the matcher searches the FULL
    /// target array and doesn't know about window boundaries on its own) - otherwise the same
    /// unconstrained greedy search the pre-windowing single-window encoder always did.
    /// </summary>
    private static List<(int TargetPos, int SourcePos, int Length)> FindWindowMatches(byte[] source, byte[] target, int windowStart, int windowEnd, HashChainMatcher matcher)
    {
        var matches = new List<(int, int, int)>();

        int i = windowStart;
        while (i < windowEnd)
        {
            int matchLength = 0, matchPos = 0;
            if (source.Length >= HashChainMatcher.HashBytes && i + MinMatchLength <= windowEnd)
            {
                matcher.FindBestMatch(target, i, out matchPos, out matchLength);
                matchLength = Math.Min(matchLength, windowEnd - i);
            }

            if (matchLength >= MinMatchLength)
            {
                matches.Add((i, matchPos, matchLength));
                i += matchLength;
            }
            else
            {
                i++;
            }
        }

        return matches;
    }

    /// <summary>Emits target[start..end) as alternating RUN (repeated-byte spans of at least <see cref="MinRunLength"/>) and generic-ADD instructions.</summary>
    private static void EmitLiteralRun(byte[] target, int start, int end, List<byte> addRun, List<byte> instructions)
    {
        int i = start;
        int chunkStart = -1;

        void FlushAdd(int chunkEnd)
        {
            if (chunkStart < 0 || chunkEnd <= chunkStart)
            {
                chunkStart = -1;
                return;
            }
            instructions.Add(1); // generic ADD opcode (size1 = 0)
            VarInt.Write(instructions, chunkEnd - chunkStart);
            for (int k = chunkStart; k < chunkEnd; k++)
                addRun.Add(target[k]);
            chunkStart = -1;
        }

        while (i < end)
        {
            int runLength = 1;
            while (i + runLength < end && target[i + runLength] == target[i])
                runLength++;

            if (runLength >= MinRunLength)
            {
                FlushAdd(i);
                instructions.Add(0); // generic RUN opcode (size1 = 0)
                VarInt.Write(instructions, runLength);
                addRun.Add(target[i]);
                i += runLength;
            }
            else
            {
                if (chunkStart < 0)
                    chunkStart = i;
                i += runLength;
            }
        }

        FlushAdd(end);
    }
}
