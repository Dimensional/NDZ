namespace Ndz.Core.Compression;

/// <summary>
/// Gear-hash content-defined chunking (~4 KiB average, 512 B min, 16 KiB max) - the same
/// algorithm and constants as <c>reference/mena-patchbench/ndztool.py</c>'s own
/// <c>_cdc_chunks</c> (there powering the retired trained-dict feature), and the same
/// technique the real ndz-studio hack packer uses internally: confirmed from
/// <c>ndzcore_bg.wasm</c>'s unstripped Rust symbols, which include
/// <c>ndzcore::census::cdc_chunks</c> feeding <c>census::dup_census</c>'s
/// <c>HashMap&lt;[u8; 16], ...&gt;</c> duplicate-content index.
///
/// Chunk boundaries are determined entirely by local content (a rolling gear hash of the
/// last handful of bytes), not by file position - so the same repeated content chunks
/// identically wherever it appears in either ROM, letting <see cref="ChunkDedupMatcher"/>
/// find relocated/shifted duplicate spans directly instead of <see cref="XDelta.HashChainMatcher"/>'s
/// brute-force "hash every byte offset and hope the chain is deep enough" search - see that
/// class's own remarks on why it tops out well short of the real packer's ~99% Verbatim
/// coverage on a full-size ROM.
/// </summary>
internal static class ContentDefinedChunker
{
    public const int MinChunkSize = 512;
    public const int MaxChunkSize = 16384;
    private const int DefaultAvgSizeLog2 = 12; // ~4 KiB average chunk size

    private static readonly uint[] Gear = BuildGearTable();

    private static uint[] BuildGearTable()
    {
        var table = new uint[256];
        for (int i = 0; i < 256; i++)
            table[i] = unchecked((uint)(i * 2654435761u + 0x9E3779B9u));
        return table;
    }

    /// <param name="avgSizeLog2">
    /// log2 of the target average chunk size (default 12 = 4 KiB, matching
    /// <c>ndztool.py</c>'s own <c>_cdc_chunks</c> and the real packer's confirmed
    /// technique - see the type-level remarks). Exposed as a parameter (rather than only
    /// the fixed default) purely so <c>ChunkRunMatcher</c>'s own callers can experiment
    /// with coarser/finer chunking - see its own remarks on what was actually tried and
    /// why the default was kept.
    /// </param>
    public static List<(int Offset, int Length)> Chunk(byte[] data, int avgSizeLog2 = DefaultAvgSizeLog2, int minChunkSize = MinChunkSize, int maxChunkSize = MaxChunkSize)
    {
        uint mask = (1u << avgSizeLog2) - 1;
        var result = new List<(int Offset, int Length)>();
        int start = 0;
        uint h = 0;
        int n = data.Length;

        for (int i = 0; i < n; i++)
        {
            h = unchecked((h << 1) + Gear[data[i]]);
            int length = i + 1 - start;
            if ((length >= minChunkSize && (h & mask) == 0) || length >= maxChunkSize)
            {
                result.Add((start, length));
                start = i + 1;
                h = 0;
            }
        }

        if (start < n)
            result.Add((start, n - start));

        return result;
    }
}
