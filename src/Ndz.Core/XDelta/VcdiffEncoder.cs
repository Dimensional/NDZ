namespace Ndz.Core.XDelta;

/// <summary>
/// Generates an RFC 3284 (VCDIFF) delta describing how to turn <paramref name="source"/>
/// (the base ROM) into a target ROM - a single window covering the whole file (no
/// window-chunking: both ROMs are already fully resident in memory here, matching how
/// <c>NdzWriter</c> works throughout this codebase, so chunking to bound streaming memory
/// buys nothing). Match-finding is a greedy hash-chain LZ77 scan against <paramref
/// name="source"/> only (not against already-emitted target bytes - a deliberate v1
/// simplification; the dominant real-world case, a ROM hack or a version diff, is almost
/// entirely covered by source content, not by repeats within the target that aren't already
/// in the source). Always uses each instruction type's "generic" code-table opcode (size
/// always follows as an explicit varint, never packed into the opcode byte itself) rather
/// than the more compact combined/literal-size opcodes real xdelta3 also uses - simpler,
/// fully valid RFC 3284 output any compliant decoder (including this project's own
/// <see cref="VcdiffDecoder"/>, real `xdelta3.exe`, and ndz-studio) applies correctly, just
/// not maximally packed. Secondary (DJW) compression of the output sections is wired in
/// separately - see <see cref="Djw.DjwCodec"/>.
/// </summary>
public static class VcdiffEncoder
{
    /// <summary>A COPY match shorter than this is cheaper to leave as literal ADD bytes, given this encoder's generic-opcode-only instruction encoding.</summary>
    public const int MinMatchLength = 8;

    /// <summary>A repeated-byte run shorter than this is cheaper to leave as literal ADD bytes than to pay a RUN instruction's own overhead.</summary>
    public const int MinRunLength = 4;

    private const int HashBytes = 4;
    private const int MaxChainSteps = 32;

    /// <summary>
    /// Generates a VCDIFF delta from <paramref name="source"/> to <paramref name="target"/>.
    /// Each of the three per-window sections (ADD/RUN data, instructions, addresses) is
    /// independently tried against <see cref="Djw.DjwCodec"/> and only kept compressed when
    /// it actually pays for itself (matching real xdelta3's own per-section efficiency
    /// checks) - a section that doesn't compress well is stored plain, same as real xdelta3.
    /// </summary>
    public static byte[] Encode(byte[] source, byte[] target)
    {
        var addRun = new List<byte>();
        var instructions = new List<byte>();
        var addresses = new List<byte>();
        EncodeInstructions(source, target, addRun, instructions, addresses);

        (byte[] addRunBytes, bool addRunCompressed) = TryDjwCompress(addRun);
        (byte[] instructionsBytes, bool instructionsCompressed) = TryDjwCompress(instructions);
        (byte[] addressesBytes, bool addressesCompressed) = TryDjwCompress(addresses);
        bool anyCompressed = addRunCompressed || instructionsCompressed || addressesCompressed;

        var output = new List<byte>();
        output.AddRange(VcdiffFormat.Magic);
        output.Add(anyCompressed ? VcdiffFormat.HdrDecompress : (byte)0);
        if (anyCompressed)
            output.Add(VcdiffFormat.SecondaryDjw);

        byte winIndicator = VcdiffFormat.WinChecksum;
        if (source.Length > 0)
            winIndicator |= VcdiffFormat.WinSource;

        var window = new List<byte> { winIndicator };
        if (source.Length > 0)
        {
            VarInt.Write(window, source.Length);
            VarInt.Write(window, 0);
        }

        var rest = new List<byte>();
        VarInt.Write(rest, target.Length);
        byte deltaIndicator = 0;
        if (addRunCompressed) deltaIndicator |= VcdiffFormat.DeltaDataComp;
        if (instructionsCompressed) deltaIndicator |= VcdiffFormat.DeltaInstComp;
        if (addressesCompressed) deltaIndicator |= VcdiffFormat.DeltaAddrComp;
        rest.Add(deltaIndicator);
        VarInt.Write(rest, addRunBytes.Length);
        VarInt.Write(rest, instructionsBytes.Length);
        VarInt.Write(rest, addressesBytes.Length);

        uint checksum = Adler32.Compute(target);
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

        output.AddRange(window);
        return output.ToArray();
    }

    /// <summary>Tries DJW-compressing a section; falls back to the plain bytes if DJW declines (not worth it) or the section is empty.</summary>
    private static (byte[] bytes, bool compressed) TryDjwCompress(List<byte> section)
    {
        if (section.Count == 0)
            return (Array.Empty<byte>(), false);

        byte[] plain = section.ToArray();
        byte[]? compressed = Djw.DjwCodec.Compress(plain);
        return compressed is not null && compressed.Length < plain.Length
            ? (compressed, true)
            : (plain, false);
    }

    private static void EncodeInstructions(byte[] source, byte[] target, List<byte> addRun, List<byte> instructions, List<byte> addresses)
    {
        var matcher = new HashChainMatcher(source);
        var cache = new AddressCache();

        int i = 0;
        int literalStart = -1;

        void FlushLiteral(int end)
        {
            if (literalStart >= 0 && end > literalStart)
                EmitLiteralRun(target, literalStart, end, addRun, instructions);
            literalStart = -1;
        }

        while (i < target.Length)
        {
            int matchLength = 0, matchPos = 0;
            if (source.Length >= HashBytes && i + MinMatchLength <= target.Length)
                matcher.FindBestMatch(target, i, out matchPos, out matchLength);

            if (matchLength >= MinMatchLength)
            {
                FlushLiteral(i);
                long here = source.Length + (long)i;
                int mode = cache.EncodeAddress(matchPos, here, out long encodedValue);
                instructions.Add((byte)(19 + 16 * mode));
                VarInt.Write(instructions, matchLength);
                if (cache.IsSameMode(mode))
                    addresses.Add((byte)encodedValue);
                else
                    VarInt.Write(addresses, encodedValue);

                i += matchLength;
            }
            else
            {
                if (literalStart < 0)
                    literalStart = i;
                i++;
            }
        }

        FlushLiteral(target.Length);
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

    /// <summary>A classic hash-chain greedy match finder (à la zlib's deflate) indexing only <see cref="_source"/> - target positions are never indexed, matching this encoder's source-only match scope.</summary>
    private sealed class HashChainMatcher
    {
        private const int HashBits = 17;
        private const int HashSize = 1 << HashBits;

        private readonly byte[] _source;
        private readonly int[] _head;
        private readonly int[] _prev;

        public HashChainMatcher(byte[] source)
        {
            _source = source;
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

            while (candidate >= 0 && steps < MaxChainSteps)
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
}
