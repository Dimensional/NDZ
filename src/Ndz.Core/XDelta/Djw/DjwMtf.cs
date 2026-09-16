namespace Ndz.Core.XDelta.Djw;

/// <summary>
/// Move-to-front + bijective-base-2 ("1/2") run-length coding, ported directly from
/// xdelta3's real `djw_update_mtf`/`djw_update_1_2`/`djw_compute_mtf_1_2`/`djw_decode_1_2`
/// (`xdelta3-djw.h`). Used for both of DJW's two MTF-coded sub-streams: the combined
/// multi-group CLEN (code-length) transmission, and the per-sector group-selector stream -
/// same generic routine, different alphabet/skip-offset per caller.
///
/// <para>
/// The run-length trick: a run of <c>N</c> consecutive "still at MTF front" symbols is coded
/// as a variable-length sequence of RUN_0/RUN_1 digits, where the k-th digit (0-indexed)
/// contributes <c>(digit+1) &lt;&lt; k</c> to the total run length - not a fixed-width count,
/// so short runs cost almost nothing and there's no length ceiling. Decoding must accumulate
/// digits exactly this way (see <see cref="Decode"/>), not assume a fixed digit count.
/// </para>
/// </summary>
public static class DjwMtf
{
    /// <summary>Moves <paramref name="mtf"/>[<paramref name="index"/>] to the front, shifting the rest right - returns the symbol moved.</summary>
    public static int UpdateMtf(int[] mtf, int index)
    {
        int sym = mtf[index];
        for (int k = index; k > 0; k--)
            mtf[k] = mtf[k - 1];
        mtf[0] = sym;
        return sym;
    }

    /// <summary>
    /// Encodes <paramref name="symbols"/> (raw values - code lengths or group indices) via
    /// MTF + 1/2 run-length coding. <paramref name="mtf"/> is the alphabet's initial MTF
    /// order, mutated in place. <paramref name="freq"/> (sized for the full MTF-code
    /// alphabet: RUN_0, RUN_1, plus one entry per possible MTF position + 1) receives the
    /// resulting per-code frequencies, for building the second-level Huffman table.
    /// </summary>
    public static List<int> Encode(ReadOnlySpan<int> symbols, int[] mtf, long[] freq)
    {
        var output = new List<int>();
        int run = 0;

        void FlushRun()
        {
            while (run >= 1)
            {
                run -= 1;
                int code = (run & 1) != 0 ? DjwConstants.Run1 : DjwConstants.Run0;
                output.Add(code);
                freq[code]++;
                run >>= 1;
            }
            run = 0;
        }

        foreach (int sym in symbols)
        {
            int j = Array.IndexOf(mtf, sym);
            for (int k = j; k > 0; k--)
                mtf[k] = mtf[k - 1];
            mtf[0] = sym;

            if (j == 0)
            {
                run++;
                continue;
            }

            FlushRun();
            int mtfSymbol = j + DjwConstants.Run1;
            output.Add(mtfSymbol);
            freq[mtfSymbol]++;
        }

        FlushRun();
        return output;
    }

    /// <summary>
    /// Decodes an MTF+1/2 sequence back into <paramref name="values"/> (already allocated to
    /// its final length). <paramref name="skipOffset"/> is the CLEN multi-group optimization:
    /// when nonzero, any position whose value <see cref="skipOffset"/> slots earlier is
    /// already 0 is automatically 0 too, consuming no bits - this can interrupt an
    /// in-progress run, so the skip check must run every position, not just between runs
    /// (matches the real decoder's per-position loop structure exactly).
    /// </summary>
    public static void Decode(DjwBitReader reader, byte[] inorder, int[] baseArr, int[] limitArr,
        int minClen, int maxClen, int maxSym, int[] mtf, int[] values, int skipOffset)
    {
        int n = 0;
        int rep = 0;
        int pendingMtf = -1; // -1 = none pending; a real pending position is always >= 1.
        int s = 0;

        while (n < values.Length)
        {
            if (skipOffset != 0 && n >= skipOffset && values[n - skipOffset] == 0)
            {
                values[n++] = 0;
                continue;
            }

            if (rep != 0)
            {
                values[n++] = mtf[0];
                rep--;
                continue;
            }

            if (pendingMtf >= 0)
            {
                values[n++] = UpdateMtf(mtf, pendingMtf);
                pendingMtf = -1;
                continue;
            }

            int decoded = DjwHuffman.DecodeSymbol(reader, inorder, baseArr, limitArr, minClen, maxClen, maxSym);

            if (decoded <= DjwConstants.Run1)
            {
                rep = (decoded + 1) << s;
                s++;
            }
            else
            {
                pendingMtf = decoded - DjwConstants.Run1;
                s = 0;
            }
        }
    }
}
