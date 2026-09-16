using Ndz.Core.Compression;
using Xunit.Abstractions;

namespace Ndz.Core.Tests;

/// <summary>
/// A one-off, real-file-gated experiment (not a correctness test - see the user's own
/// 2026-09-17 question about whether the real packer might auto-tune content-defined
/// chunking's average chunk size, the way this project's own `--raw-dict auto`/
/// `--block-size auto` sweep candidate sizes) measuring how <see cref="ChunkRunMatcher"/>'s
/// Verbatim-block coverage actually changes across a range of average chunk sizes, on both
/// real fixture pairs. Answers "is there headroom left in this specific lever" empirically
/// rather than by guessing from the wasm symbols alone (which don't reveal tuning, since
/// chunk size is a pure writer-side search heuristic never serialized to disk).
/// </summary>
public class ChunkSizeTuningExperiment
{
    private readonly ITestOutputHelper _output;

    public ChunkSizeTuningExperiment(ITestOutputHelper output) => _output = output;

    [Theory]
    [MemberData(nameof(HackContainerRealFileTests.Pairs), MemberType = typeof(HackContainerRealFileTests))]
    public void SweepAverageChunkSize_ReportsVerbatimCoverage(HackContainerRealFileTests.FixturePair pair)
    {
        if (!pair.HaveFixtures)
            return;

        byte[] baseRom = File.ReadAllBytes(pair.BaseRom);
        byte[] targetRom = File.ReadAllBytes(pair.TargetRom);
        const int blockSize = 8192;
        int blockCount = (targetRom.Length + blockSize - 1) / blockSize;

        _output.WriteLine($"=== {pair.Name} ({targetRom.Length:N0} bytes, {blockCount:N0} blocks) ===");

        foreach (int avgSizeLog2 in new[] { 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 })
        {
            // Scale min/max proportionally with the average (min=avg/8, max=avg*4,
            // matching ContentDefinedChunker's own default ratio) so larger averages
            // actually get a chance to form larger chunks, instead of immediately
            // saturating against a fixed 16 KiB MaxChunkSize ceiling.
            int avg = 1 << avgSizeLog2;
            int min = Math.Max(64, avg / 8);
            int max = avg * 4;
            var matcher = new ChunkRunMatcher(baseRom, targetRom, avgSizeLog2, min, max);
            int verbatimCount = 0;
            for (int b = 0; b < blockCount; b++)
            {
                int offset = b * blockSize;
                int length = Math.Min(blockSize, targetRom.Length - offset);
                if (matcher.TryFindBlockMatch(offset, length, out _))
                    verbatimCount++;
            }

            double pct = 100.0 * verbatimCount / blockCount;
            _output.WriteLine($"  avgChunkSize=2^{avgSizeLog2,2} ({1 << avgSizeLog2,6:N0} B)  verbatim={verbatimCount,6:N0}/{blockCount:N0}  ({pct:F2}%)");
        }
    }
}
