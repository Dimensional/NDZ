using Ndz.Core.XDelta;

namespace Ndz.Core.XDelta.Djw;

/// <summary>
/// xdelta3's DJW secondary compressor - full apply (decode) and generate (encode), ported
/// directly from real xdelta3 source (`xdelta3-djw.h`, `xdelta3-second.h`, tag v3.2.0,
/// `jmacd/xdelta`), not guessed. See <see cref="DjwHuffman"/>/<see cref="DjwMtf"/> for the
/// shared canonical-Huffman and MTF+1/2 building blocks this drives.
///
/// <para>
/// The public <see cref="Compress"/>/<see cref="Decompress"/> methods operate on the exact
/// wire shape xdelta3 uses for a secondary-compressed VCDIFF section - confirmed from
/// `xd3_encode_secondary`/`xd3_decode_secondary` (`xdelta3-second.h`): a leading varint (the
/// section's real, decompressed byte count - the same RFC 3284 base-128 varint used
/// everywhere else in VCDIFF, see <see cref="VarInt"/>), followed by the raw DJW bitstream.
/// That decompressed-size varint is NOT part of DJW's own window format (confirmed from
/// `xd3_decode_huff`'s signature, which takes the expected output length as a parameter, not
/// something it decodes itself) - it's a layer xdelta3 adds around every secondary
/// compressor uniformly, DJW included.
/// </para>
/// </summary>
public static class DjwCodec
{
    /// <summary>
    /// Compresses <paramref name="data"/>, wire-ready (leading decompressed-size varint +
    /// DJW bitstream) - or <see langword="null"/> if compressing wouldn't be worth it
    /// (matching real xdelta3's own "secondary compression was inefficient" bail-out; the
    /// caller should store the section uncompressed instead).
    /// </summary>
    public static byte[]? Compress(byte[] data)
    {
        if (data.Length == 0)
            return null;

        byte[]? body = CompressWindow(data);
        if (body is null)
            return null;

        var output = new List<byte>();
        VarInt.Write(output, data.Length);
        output.AddRange(body);
        return output.ToArray();
    }

    /// <summary>Decompresses a wire-format DJW section blob (as produced by <see cref="Compress"/>).</summary>
    public static byte[] Decompress(byte[] compressed)
    {
        int pos = 0;
        long decompressedLength = VarInt.Read(compressed, ref pos);
        if (decompressedLength <= 0 || decompressedLength > int.MaxValue)
            throw new XDeltaException("DJW section has an invalid decompressed size.");

        return DecompressWindow(compressed, pos, (int)decompressedLength);
    }

    // ------------------------------------------------------------------
    // Encode
    // ------------------------------------------------------------------

    private static byte[]? CompressWindow(byte[] data)
    {
        var realFreq = new long[DjwConstants.AlphabetSize];
        foreach (byte b in data)
            realFreq[b]++;
        long inputBits = (long)data.Length * 8;

        var output = new List<byte>();
        var writer = new DjwBitWriter(output);

        (int groups, int sectorSize) = ChooseGroupsAndSectorSize(data.Length);

        if (groups == 1)
        {
            var clen = new byte[DjwConstants.AlphabetSize];
            long outputBits = DjwHuffman.BuildCodeLengths(realFreq, clen, DjwConstants.AlphabetSize, DjwConstants.MaxCodeLen);

            if (outputBits + DjwConstants.EfficiencyBits >= inputBits)
                return null;

            writer.WriteBits(groups - 1, DjwConstants.GroupBits);
            var clenAsInt = new int[DjwConstants.AlphabetSize];
            for (int i = 0; i < DjwConstants.AlphabetSize; i++) clenAsInt[i] = clen[i];
            EncodePrefixStream(writer, clenAsInt, DjwConstants.AlphabetSize);

            var code = new int[DjwConstants.AlphabetSize];
            DjwHuffman.BuildCodes(code, clen, DjwConstants.AlphabetSize);
            foreach (byte b in data)
                writer.WriteBits(code[b], clen[b]);

            writer.Flush();
            return output.ToArray();
        }

        (groups, byte[][] evolveClen) = BuildInitialPartition(realFreq, data.Length, groups);

        int sectors = 1 + (data.Length - 1) / sectorSize;
        var gbest = new int[sectors];
        var evolveFreq = new long[groups][];
        for (int g = 0; g < groups; g++) evolveFreq[g] = new long[DjwConstants.AlphabetSize];

        long bestBits = 0;
        long outputBitsTotal = 0;

        for (int iter = 1; ; iter++)
        {
            foreach (var f in evolveFreq) Array.Clear(f);

            for (int sec = 0; sec < sectors; sec++)
            {
                int start = sec * sectorSize;
                int end = Math.Min(start + sectorSize, data.Length);

                long bestCost = long.MaxValue;
                int winner = 0;
                for (int gp = 0; gp < groups; gp++)
                {
                    long cost = 0;
                    for (int i = start; i < end; i++)
                        cost += evolveClen[gp][data[i]];
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        winner = gp;
                    }
                }

                gbest[sec] = winner;
                for (int i = start; i < end; i++)
                    evolveFreq[winner][data[i]]++;
            }

            outputBitsTotal = 0;
            for (int gp = 0; gp < groups; gp++)
            {
                var forcedZero = new bool[DjwConstants.AlphabetSize];
                bool anyForced = false;
                for (int i = 0; i < DjwConstants.AlphabetSize; i++)
                {
                    if (evolveFreq[gp][i] == 0 && realFreq[i] != 0)
                    {
                        evolveFreq[gp][i] = 1;
                        forcedZero[i] = true;
                        anyForced = true;
                    }
                }

                long groupBits = DjwHuffman.BuildCodeLengths(evolveFreq[gp], evolveClen[gp], DjwConstants.AlphabetSize, DjwConstants.MaxCodeLen);

                if (anyForced)
                {
                    for (int i = 0; i < DjwConstants.AlphabetSize; i++)
                        if (forcedZero[i])
                            groupBits -= evolveClen[gp][i];
                }

                outputBitsTotal += groupBits;
            }

            bool keepGoing = iter == 1 || (iter < DjwConstants.MaxIterations && (bestBits - outputBitsTotal) >= DjwConstants.MinImprovementBits);
            if (keepGoing)
            {
                bestBits = outputBitsTotal;
                continue;
            }

            break;
        }

        if (outputBitsTotal + DjwConstants.EfficiencyBits >= inputBits)
            return null;

        writer.WriteBits(groups - 1, DjwConstants.GroupBits);
        writer.WriteBits((sectorSize / DjwConstants.SectorSizeMult) - 1, DjwConstants.SectorSizeBits);

        int[] combinedClenSymbols = BuildCombinedClenSymbols(groups, evolveClen);
        EncodePrefixStream(writer, combinedClenSymbols, combinedClenSymbols.Length);

        long selectBits = EncodeSelectorStream(writer, gbest, groups);

        if (outputBitsTotal + selectBits + (8L * output.Count) + DjwConstants.EfficiencyBits >= inputBits)
            return null;

        var evolveCode = new int[groups][];
        for (int gp = 0; gp < groups; gp++)
        {
            evolveCode[gp] = new int[DjwConstants.AlphabetSize];
            DjwHuffman.BuildCodes(evolveCode[gp], evolveClen[gp], DjwConstants.AlphabetSize);
        }

        for (int sec = 0; sec < sectors; sec++)
        {
            int gp = gbest[sec];
            int start = sec * sectorSize;
            int end = Math.Min(start + sectorSize, data.Length);
            for (int i = start; i < end; i++)
                writer.WriteBits(evolveCode[gp][data[i]], evolveClen[gp][data[i]]);
        }

        writer.Flush();
        return output.ToArray();
    }

    /// <summary>Encodes the combined multi-group CLEN transmission (group 0's full 256 lengths, then each later group's nonzero-only lengths) or a single group's full 256 lengths.</summary>
    private static void EncodePrefixStream(DjwBitWriter writer, int[] symbols, int symbolCount)
    {
        int[] initialMtf = DjwConstants.BuildInitialClenMtf();
        var freq = new long[DjwConstants.TotalClenCodes];

        List<int> mtfCoded = DjwMtf.Encode(symbols.AsSpan(0, symbolCount), initialMtf, freq);

        var tableClen = new byte[DjwConstants.TotalClenCodes];
        DjwHuffman.BuildCodeLengths(freq, tableClen, DjwConstants.TotalClenCodes, DjwConstants.MaxClclen);

        int numToEncode = DjwConstants.TotalClenCodes;
        while (numToEncode > DjwConstants.Extra12Offset && tableClen[numToEncode - 1] == 0)
            numToEncode--;

        writer.WriteBits(numToEncode - DjwConstants.Extra12Offset, DjwConstants.ExtraCodeBits);
        for (int i = 0; i < numToEncode; i++)
            writer.WriteBits(tableClen[i], DjwConstants.ClclenBits);

        var tableCode = new int[DjwConstants.TotalClenCodes];
        DjwHuffman.BuildCodes(tableCode, tableClen, DjwConstants.TotalClenCodes);

        foreach (int sym in mtfCoded)
            writer.WriteBits(tableCode[sym], tableClen[sym]);
    }

    /// <summary>Encodes the per-sector group-selector stream. Returns the bit cost, for the caller's own efficiency accounting.</summary>
    private static long EncodeSelectorStream(DjwBitWriter writer, int[] gbest, int groups)
    {
        int[] initialMtf = new int[groups];
        for (int i = 0; i < groups; i++) initialMtf[i] = i;

        var freq = new long[groups + 1];
        List<int> mtfCoded = DjwMtf.Encode(gbest, initialMtf, freq);

        var tableClen = new byte[groups + 1];
        long selectBits = DjwHuffman.BuildCodeLengths(freq, tableClen, groups + 1, DjwConstants.MaxGroupSelectorClen);

        for (int i = 0; i < groups + 1; i++)
            writer.WriteBits(tableClen[i], DjwConstants.GroupSelectorClenBits);

        var tableCode = new int[groups + 1];
        DjwHuffman.BuildCodes(tableCode, tableClen, groups + 1);

        foreach (int sym in mtfCoded)
            writer.WriteBits(tableCode[sym], tableClen[sym]);

        return selectBits;
    }

    private static int[] BuildCombinedClenSymbols(int groups, byte[][] evolveClen)
    {
        var symbols = new List<int>(DjwConstants.AlphabetSize * groups);
        for (int i = 0; i < DjwConstants.AlphabetSize; i++)
            symbols.Add(evolveClen[0][i]);
        for (int gp = 1; gp < groups; gp++)
            for (int i = 0; i < DjwConstants.AlphabetSize; i++)
                if (evolveClen[gp][i] != 0)
                    symbols.Add(evolveClen[gp][i]);
        return symbols.ToArray();
    }

    /// <summary>An encoder policy choice, not a wire-format requirement - any valid (groups, sectorSize) pair produces decodable output. Not tuned to match real xdelta3's own empirical tables.</summary>
    private static (int groups, int sectorSize) ChooseGroupsAndSectorSize(int length)
    {
        if (length < 1000)
            return (1, 0);

        int groups = length switch
        {
            < 4000 => 2,
            < 10000 => 4,
            < 50000 => 6,
            _ => DjwConstants.MaxGroups,
        };
        return (groups, 50);
    }

    /// <summary>Ported from xdelta3's real initial-group-partition loop, including its "too many groups for this data - drop one and retry" behavior.</summary>
    private static (int groups, byte[][] evolveClen) BuildInitialPartition(long[] realFreq, long totalBytes, int groups)
    {
        while (true)
        {
            var evolveClen = new byte[groups][];
            for (int g = 0; g < groups; g++) evolveClen[g] = new byte[DjwConstants.AlphabetSize];

            long left = totalBytes;
            int sym1 = 0, sym2 = 0;
            bool tooMany = false;

            for (int gp = 0; gp < groups; gp++)
            {
                long goal = left / (groups - gp);
                if (goal == 0)
                {
                    tooMany = true;
                    break;
                }

                long sum = 0;
                while (sum < goal)
                {
                    sum += realFreq[sym2];
                    sym2++;
                }

                for (int s = 0; s < DjwConstants.AlphabetSize; s++)
                    evolveClen[gp][s] = (byte)(s >= sym1 && s <= sym2 ? 1 : 16);

                left -= sum;
                sym1 = sym2 + 1;
            }

            if (!tooMany)
                return (groups, evolveClen);

            groups--;
            if (groups < 1)
                throw new XDeltaException("DJW encoder: could not find a valid group partition for this data.");
        }
    }

    // ------------------------------------------------------------------
    // Decode
    // ------------------------------------------------------------------

    private static byte[] DecompressWindow(byte[] compressed, int startOffset, int outputBytes)
    {
        if (outputBytes == 0)
            throw new XDeltaException("DJW secondary decoder: invalid output size.");

        var reader = new DjwBitReader(compressed, startOffset);
        var output = new byte[outputBytes];

        int groups = reader.ReadBits(DjwConstants.GroupBits) + 1;

        int sectorSize;
        if (groups > 1)
        {
            int raw = reader.ReadBits(DjwConstants.SectorSizeBits);
            sectorSize = (raw + 1) * DjwConstants.SectorSizeMult;
        }
        else
        {
            sectorSize = outputBytes;
        }

        int sectors = 1 + (outputBytes - 1) / sectorSize;

        // Decode the CLEN meta-table, then the combined per-group data-code-length table.
        int[] clenInitialMtf = DjwConstants.BuildInitialClenMtf();
        int numExtra = reader.ReadBits(DjwConstants.ExtraCodeBits);
        int numCodes = numExtra + DjwConstants.Extra12Offset;

        var clclen = new byte[DjwConstants.TotalClenCodes];
        for (int i = 0; i < numCodes; i++)
            clclen[i] = (byte)reader.ReadBits(DjwConstants.ClclenBits);

        DjwHuffman.BuildDecodeTable(clclen, DjwConstants.TotalClenCodes, DjwConstants.MaxClclen,
            out byte[] clInorder, out int[] clBase, out int[] clLimit, out int clMinLen, out int clMaxLen);

        var clenValues = new int[DjwConstants.AlphabetSize * groups];
        var clenMtfState = (int[])clenInitialMtf.Clone();
        DjwMtf.Decode(reader, clInorder, clBase, clLimit, clMinLen, clMaxLen, DjwConstants.TotalClenCodes - 1,
            clenMtfState, clenValues, DjwConstants.AlphabetSize);

        var groupInorder = new byte[groups][];
        var groupBase = new int[groups][];
        var groupLimit = new int[groups][];
        var groupMinLen = new int[groups];
        var groupMaxLen = new int[groups];

        for (int gp = 0; gp < groups; gp++)
        {
            var clen = new byte[DjwConstants.AlphabetSize];
            for (int i = 0; i < DjwConstants.AlphabetSize; i++)
                clen[i] = (byte)clenValues[gp * DjwConstants.AlphabetSize + i];

            DjwHuffman.BuildDecodeTable(clen, DjwConstants.AlphabetSize, DjwConstants.MaxCodeLen,
                out byte[] inorder, out int[] baseArr, out int[] limitArr, out int minLen, out int maxLen);
            groupInorder[gp] = inorder;
            groupBase[gp] = baseArr;
            groupLimit[gp] = limitArr;
            groupMinLen[gp] = minLen;
            groupMaxLen[gp] = maxLen;
        }

        int[] selGroup = new int[sectors];
        if (groups > 1)
        {
            var selClen = new byte[groups + 1];
            for (int i = 0; i < groups + 1; i++)
                selClen[i] = (byte)reader.ReadBits(DjwConstants.GroupSelectorClenBits);

            DjwHuffman.BuildDecodeTable(selClen, groups + 1, DjwConstants.MaxGroupSelectorClen,
                out byte[] selInorder, out int[] selBase, out int[] selLimit, out int selMinLen, out int selMaxLen);

            var selMtfState = new int[groups];
            for (int i = 0; i < groups; i++) selMtfState[i] = i;

            DjwMtf.Decode(reader, selInorder, selBase, selLimit, selMinLen, selMaxLen, groups,
                selMtfState, selGroup, 0);
        }

        int produced = 0;
        for (int sec = 0; sec < sectors; sec++)
        {
            int gp = groups > 1 ? selGroup[sec] : 0;
            int n = Math.Min(sectorSize, outputBytes - produced);

            for (int i = 0; i < n; i++)
            {
                int sym = DjwHuffman.DecodeSymbol(reader, groupInorder[gp], groupBase[gp], groupLimit[gp],
                    groupMinLen[gp], groupMaxLen[gp], DjwConstants.AlphabetSize - 1);
                output[produced++] = (byte)sym;
            }
        }

        return output;
    }
}
