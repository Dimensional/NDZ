namespace Ndz.Core.XDelta;

/// <summary>Which of the three per-window sections a piece of VCDIFF data belongs to - each can independently carry secondary compression.</summary>
public enum VcdiffSection
{
    AddRunData,
    InstructionsAndSizes,
    AddressesForCopy,
}

/// <summary>The three real instruction types a code-table opcode can decode to (RFC 3284 §5.1); NOOP fills the unused half of a single-instruction opcode.</summary>
public enum VcdiffInstructionType : byte
{
    Noop = 0,
    Add = 1,
    Run = 2,
    Copy = 3,
}

/// <summary>
/// Magic bytes, header/window bit layout, and the RFC 3284 default code table - all
/// confirmed directly from the real, working `SnowflakePowered/vcdiff` package's source
/// (`Shared/CodeTable.cs`, `Includes/Include.cs`, `Decoders/WindowDecoder.cs`,
/// `Decoders/VcDecoderEx.cs`), not guessed or reconstructed from the RFC text alone - that
/// package's default code table and window-header handling were already exercised on every
/// successful real-`xdelta3.exe`-interop decode during this project's prior xdelta
/// investigation (see docs/xdelta-vcdiff-notes.md). The six code-table arrays below are
/// generated programmatically from the confirmed structure (not hand-transcribed byte by
/// byte) specifically to avoid a transcription slip - each array's shape (which opcode
/// ranges map to which instruction/size/mode) is exactly what the real source above defines.
/// </summary>
public static class VcdiffFormat
{
    /// <summary>4-byte file magic: 'V'|0x80, 'C'|0x80, 'D'|0x80, then a version byte (0x00 for plain VCDIFF; 'S' marks the SDCH variant, not produced or consumed here).</summary>
    public static readonly byte[] Magic = { 0xD6, 0xC3, 0xC4, 0x00 };

    // Hdr_Indicator bits (RFC 3284 §4.1).
    public const byte HdrDecompress = 0x01; // a secondary-compressor ID byte follows
    public const byte HdrCodeTable = 0x02;  // a custom code table follows - never produced or accepted here
    public const byte HdrAppHeader = 0x04;  // an application-defined header follows - never produced here

    // Win_Indicator bits (RFC 3284 §4.2). VCD_SOURCE/VCD_TARGET are mutually exclusive.
    public const byte WinSource = 0x01;   // source segment comes from the source (base) file
    public const byte WinTarget = 0x02;   // source segment comes from previously-decoded target output - not produced here (single-window encoder)
    public const byte WinChecksum = 0x04; // an Adler32 checksum follows the section lengths (xdelta3's own convention, not core RFC 3284)

    // Delta_Indicator bits (RFC 3284 §4.3) - per-section secondary compression flags.
    public const byte DeltaDataComp = 0x01;
    public const byte DeltaInstComp = 0x02;
    public const byte DeltaAddrComp = 0x04;

    /// <summary>xdelta3's secondary-compressor IDs (not part of core RFC 3284 - xdelta3-specific, confirmed against real xdelta3 output and this format's own `Hdr_Indicator`/secondary-compressor-ID byte). 0 (no byte written at all) means "none".</summary>
    public const byte SecondaryNone = 0;
    public const byte SecondaryDjw = 1;

    /// <summary>RFC 3284 §5.3's default address cache sizes.</summary>
    public const int DefaultNearCacheSize = 4;
    public const int DefaultSameCacheSize = 3;

    // The RFC 3284 default code table: 256 opcodes, each packing up to two instructions
    // (inst1/size1/mode1 and inst2/size2/mode2). A size of 0 means "the real size follows
    // as a varint in the instruction stream"; any other value is the literal size packed
    // directly into the opcode.
    private const byte N = (byte)VcdiffInstructionType.Noop;
    private const byte A = (byte)VcdiffInstructionType.Add;
    private const byte R = (byte)VcdiffInstructionType.Run;
    private const byte C = (byte)VcdiffInstructionType.Copy;
    public static readonly byte[] DefaultInst1 =
    {
        R, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A,
        A, A, A, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, A, A, A, A, A, A, A, A, A, A, A, A, A,
        A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A,
        A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A,
        A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A,
        A, A, A, A, A, A, A, A, A, A, A, A, A, A, A, A,
        A, A, A, A, A, A, A, C, C, C, C, C, C, C, C, C,
    };

    public static readonly byte[] DefaultInst2 =
    {
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, N, N, N, N, N, N, N, N, N, N, N, N, N,
        N, N, N, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, C, C, C, C, C, C, C, C, C,
        C, C, C, C, C, C, C, A, A, A, A, A, A, A, A, A,
    };

    public static readonly byte[] DefaultSize1 =
    {
        0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
        15, 16, 17, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 17, 18, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 1,
        1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 1, 1, 1, 2, 2,
        2, 3, 3, 3, 4, 4, 4, 1, 1, 1, 2, 2, 2, 3, 3, 3,
        4, 4, 4, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 1,
        1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 1, 2, 3, 4, 1,
        2, 3, 4, 1, 2, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    };

    public static readonly byte[] DefaultSize2 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4,
        5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5,
        6, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6,
        4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4,
        5, 6, 4, 5, 6, 4, 5, 6, 4, 5, 6, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    };

    public static readonly byte[] DefaultMode1 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
        6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
        7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
        8, 8, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8,
    };

    public static readonly byte[] DefaultMode2 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 7,
        7, 7, 7, 8, 8, 8, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };
}