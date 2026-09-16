namespace Ndz.Core.XDelta.Djw;

/// <summary>
/// Real constants from xdelta3's DJW secondary compressor, confirmed directly from
/// `xdelta3/xdelta3-djw.h` (tag v3.2.0, `jmacd/xdelta` on GitHub) - not guessed. DJW is a
/// bzip2-style adaptive multi-table Huffman coder (its own header credits Julian Seward's
/// bzip2 sources for "the multi-table Huffman technique"), but with its own distinct
/// bitstream: no BWT stage (raw bytes go straight into MTF+Huffman), its own header/group/
/// sector framing, and its own constants - not the `.bz2` container format.
/// </summary>
public static class DjwConstants
{
    public const int AlphabetSize = 256;

    /// <summary>Maximum length of a data/selector Huffman code.</summary>
    public const int MaxCodeLen = 20;

    /// <summary>RUN_0/RUN_1: the two MTF+1/2 run-length symbols, shared by both the CLEN and selector sub-streams.</summary>
    public const int Run0 = 0;
    public const int Run1 = 1;

    /// <summary>Total alphabet size of the CLEN (code-length) meta-Huffman stream: RUN_0, RUN_1, plus code-length values 1..MaxCodeLen.</summary>
    public const int TotalClenCodes = MaxCodeLen + 2; // 22

    public const int BasicCodes = 5;
    public const int ExtraCodes = 15;

    /// <summary>Offset into the CLEN meta-alphabet where the "extra" (optionally transmitted) codes begin.</summary>
    public const int Extra12Offset = BasicCodes + Run0Run1Count; // 7
    private const int Run0Run1Count = 2;

    /// <summary>Bits used to encode how many "extra" CLEN meta-codes follow (0..ExtraCodes).</summary>
    public const int ExtraCodeBits = 4;

    public const int MaxGroups = 8;
    public const int GroupBits = 3;

    public const int SectorSizeMult = 5;
    public const int SectorSizeBits = 5;
    public const int MaxSectorSize = (1 << SectorSizeBits) * SectorSizeMult; // 160

    /// <summary>Maximum refinement passes over the group-assignment/table-rebuild loop.</summary>
    public const int MaxIterations = 6;

    /// <summary>Minimum bit-count improvement an iteration must show to keep going.</summary>
    public const int MinImprovementBits = 20;

    /// <summary>Maximum code length of a CLEN meta-code itself.</summary>
    public const int MaxClclen = 15;
    public const int ClclenBits = 4;

    /// <summary>Maximum code length of a group-selector code.</summary>
    public const int MaxGroupSelectorClen = 7;
    public const int GroupSelectorClenBits = 3;

    /// <summary>Secondary compression must save at least this many bits to be worth using at all.</summary>
    public const int EfficiencyBits = 16;

    /// <summary>
    /// The initial move-to-front order for CLEN meta-symbols (code-length values 1..20,
    /// excluding the always-present "0" and the <see cref="BasicCodes"/> entries) - tuned by
    /// the format author from real-world CLEN distributions so the common case needs fewer
    /// "extra" codes transmitted. Order matters for correctness (both sides must agree), not
    /// just efficiency.
    /// </summary>
    public static readonly int[] ExtraCodeOrder = { 9, 10, 3, 11, 2, 12, 13, 1, 14, 15, 16, 17, 18, 19, 20 };

    /// <summary>The always-present CLEN meta-symbols (after the mandatory "0" entry).</summary>
    public static readonly int[] BasicCodeOrder = { 4, 5, 6, 7, 8 };

    /// <summary>Builds the initial MTF order for the CLEN meta-alphabet: [0, then <see cref="BasicCodeOrder"/>, then <see cref="ExtraCodeOrder"/>] - 21 entries (code-length values 0..20).</summary>
    public static int[] BuildInitialClenMtf()
    {
        var mtf = new int[1 + BasicCodes + ExtraCodes];
        int i = 0;
        mtf[i++] = 0;
        foreach (int v in BasicCodeOrder) mtf[i++] = v;
        foreach (int v in ExtraCodeOrder) mtf[i++] = v;
        return mtf;
    }
}
