namespace Ndz.Core.XDelta.Djw;

/// <summary>
/// Canonical Huffman table construction and fast decode, ported directly from xdelta3's real
/// `djw_build_prefix`/`djw_build_codes`/`djw_build_decoder`/`djw_decode_symbol`
/// (`xdelta3-djw.h`) - itself closely modeled on bzip2's own `BZ2_hbMakeCodeLengths`/
/// `BZ2_hbAssignCodes` (confirmed against GrindCore's vendored real bzip2 1.0.8 source,
/// `external/bzip2/bzip2/huffman.c`), including the same length-limiting technique (halve
/// frequencies and retry on overflow) and the same heap tie-break rule (lower frequency
/// first, then lower tree depth - <see cref="HeapLess"/>).
/// </summary>
public static class DjwHuffman
{
    private struct HeapEntry
    {
        public long Freq;
        public int Depth;
        public int Parent;
    }

    /// <summary>
    /// Builds canonical Huffman code lengths for <paramref name="freq"/> (length
    /// <paramref name="alphaSize"/>) into <paramref name="clen"/>, each length capped at
    /// <paramref name="maxLen"/> - returns the total encoded bit cost (sum of freq[i] * clen[i]).
    /// </summary>
    public static long BuildCodeLengths(ReadOnlySpan<long> freq, byte[] clen, int alphaSize, int maxLen)
    {
        var workFreq = new long[alphaSize];
        freq[..alphaSize].CopyTo(workFreq);

        var ents = new HeapEntry[alphaSize * 2 + 2];
        var heap = new int[alphaSize + 2];

        while (true)
        {
            int heapLast = 0;
            int entsSize = 1;
            bool overflow = false;
            long totalBits = 0;

            heap[0] = 0;
            ents[0] = default;

            for (int i = 0; i < alphaSize; i++, entsSize++)
            {
                ents[entsSize] = new HeapEntry { Depth = 0, Parent = -1, Freq = workFreq[i] };
                if (workFreq[i] != 0)
                    HeapInsert(heap, ents, ++heapLast, entsSize);
            }

            if (heapLast == 0)
                throw new XDeltaException("DJW Huffman build: every frequency is zero.");

            if (heapLast == 1)
            {
                // Fake a second symbol so a single-symbol alphabet doesn't get a zero-length code.
                int s = workFreq[0] != 0 ? alphaSize - 1 : 0;
                workFreq[s] = 1;
                continue;
            }

            while (heapLast > 1)
            {
                heapLast--;
                int n1 = HeapExtract(heap, ents, heapLast);
                heapLast--;
                int n2 = HeapExtract(heap, ents, heapLast);

                ents[entsSize] = new HeapEntry
                {
                    Freq = ents[n1].Freq + ents[n2].Freq,
                    Depth = 1 + Math.Max(ents[n1].Depth, ents[n2].Depth),
                    Parent = -1,
                };
                var e1 = ents[n1]; e1.Parent = entsSize; ents[n1] = e1;
                var e2 = ents[n2]; e2.Parent = entsSize; ents[n2] = e2;

                heapLast++;
                HeapInsert(heap, ents, heapLast, entsSize);
                entsSize++;
            }

            for (int i = 1; i <= alphaSize; i++)
            {
                int b = 0;
                if (ents[i].Freq != 0)
                {
                    int p = i;
                    while (ents[p].Parent >= 0) { p = ents[p].Parent; b++; }
                    if (b > maxLen) overflow = true;
                    totalBits += (long)b * workFreq[i - 1];
                }
                clen[i - 1] = (byte)b;
            }

            if (!overflow)
                return totalBits;

            for (int i = 0; i < alphaSize; i++)
                workFreq[i] = workFreq[i] / 2 + 1;
        }
    }

    private static bool HeapLess(in HeapEntry a, in HeapEntry b) =>
        a.Freq < b.Freq || (a.Freq == b.Freq && a.Depth < b.Depth);

    private static void HeapInsert(int[] heap, HeapEntry[] ents, int p, int e)
    {
        int pp = p / 2;
        while (HeapLess(ents[e], ents[heap[pp]]))
        {
            heap[p] = heap[pp];
            p = pp;
            pp = p / 2;
        }
        heap[p] = e;
    }

    /// <summary><paramref name="heapLast"/> must already be the post-decrement count (matching real xdelta3's `heap_extract(heap, ents, --heap_last)` call convention).</summary>
    private static int HeapExtract(int[] heap, HeapEntry[] ents, int heapLast)
    {
        int smallest = heap[1];
        heap[1] = heap[heapLast + 1];

        int p = 1;
        while (true)
        {
            int pc = p * 2;
            if (pc > heapLast) break;
            if (pc < heapLast && HeapLess(ents[heap[pc + 1]], ents[heap[pc]])) pc++;
            if (!HeapLess(ents[heap[pc]], ents[heap[p]])) break;
            (heap[pc], heap[p]) = (heap[p], heap[pc]);
            p = pc;
        }
        return smallest;
    }

    /// <summary>Canonical code assignment: for each length from shortest to longest, symbols of that length get sequential codes.</summary>
    public static void BuildCodes(int[] code, byte[] clen, int alphaSize)
    {
        int minClen = DjwConstants.MaxCodeLen;
        int maxClen = 0;
        for (int i = 0; i < alphaSize; i++)
        {
            if (clen[i] > 0 && clen[i] < minClen) minClen = clen[i];
            if (clen[i] > maxClen) maxClen = clen[i];
        }

        int c = 0;
        for (int l = minClen; l <= maxClen; l++)
        {
            for (int i = 0; i < alphaSize; i++)
            {
                if (clen[i] == l) code[i] = c++;
            }
            c <<= 1;
        }
    }

    /// <summary>Fast canonical-Huffman decode tables (the classic zlib-inftrees "base/limit/inorder" technique), ported from `djw_build_decoder`.</summary>
    public static void BuildDecodeTable(byte[] clen, int alphaSize, int absMax,
        out byte[] inorder, out int[] baseArr, out int[] limitArr, out int minClen, out int maxClen)
    {
        var nrClen = new int[absMax + 1];
        for (int i = 0; i < alphaSize; i++)
            nrClen[clen[i]]++;

        minClen = 1;
        while (minClen <= absMax && nrClen[minClen] == 0) minClen++;
        maxClen = absMax;
        while (maxClen > 0 && nrClen[maxClen] == 0) maxClen--;

        baseArr = new int[absMax + 1];
        limitArr = new int[absMax + 1];
        var tmpBase = new int[absMax + 1];

        tmpBase[minClen] = 0;
        baseArr[minClen] = 0;
        limitArr[minClen] = nrClen[minClen] - 1;
        for (int i = minClen + 1; i <= maxClen; i++)
        {
            int lastLimit = (limitArr[i - 1] + 1) << 1;
            tmpBase[i] = tmpBase[i - 1] + nrClen[i - 1];
            limitArr[i] = lastLimit + nrClen[i] - 1;
            baseArr[i] = lastLimit - tmpBase[i];
        }

        inorder = new byte[alphaSize];
        for (int i = 0; i < alphaSize; i++)
        {
            int l = clen[i];
            if (l != 0)
                inorder[tmpBase[l]++] = (byte)i;
        }
    }

    /// <summary>Decodes one Huffman-coded symbol using tables from <see cref="BuildDecodeTable"/>.</summary>
    public static int DecodeSymbol(DjwBitReader reader, byte[] inorder, int[] baseArr, int[] limitArr, int minClen, int maxClen, int maxSym)
    {
        int code = 0;
        int bits = 0;
        while (true)
        {
            if (bits == maxClen)
                throw new XDeltaException("DJW secondary decoder: invalid code (exceeds max length).");
            bits++;
            code = (code << 1) | reader.ReadBit();
            if (bits >= minClen && code <= limitArr[bits])
                break;
        }

        if (baseArr[bits] > code)
            throw new XDeltaException("DJW secondary decoder: invalid code.");

        int offset = code - baseArr[bits];
        if (offset > maxSym)
            throw new XDeltaException("DJW secondary decoder: invalid code (symbol out of range).");

        return inorder[offset];
    }
}
