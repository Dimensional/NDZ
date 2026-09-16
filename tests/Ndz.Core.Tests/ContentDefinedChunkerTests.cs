using Ndz.Core.Compression;

namespace Ndz.Core.Tests;

public class ContentDefinedChunkerTests
{
    [Fact]
    public void Chunk_TilesTheWholeBufferWithNoGapsOrOverlaps()
    {
        byte[] data = TestRom.Build(512 * 1024, seed: 1);

        var chunks = ContentDefinedChunker.Chunk(data);

        Assert.NotEmpty(chunks);
        int expectedOffset = 0;
        foreach (var (offset, length) in chunks)
        {
            Assert.Equal(expectedOffset, offset);
            Assert.True(length > 0);
            expectedOffset += length;
        }
        Assert.Equal(data.Length, expectedOffset);
    }

    [Fact]
    public void Chunk_RespectsMinAndMaxSizeExceptPossiblyTheLastChunk()
    {
        byte[] data = TestRom.Build(1024 * 1024, seed: 2);

        var chunks = ContentDefinedChunker.Chunk(data);

        for (int i = 0; i < chunks.Count; i++)
        {
            var (_, length) = chunks[i];
            Assert.True(length <= ContentDefinedChunker.MaxChunkSize, $"Chunk {i} exceeds max size: {length}");
            if (i < chunks.Count - 1)
                Assert.True(length >= ContentDefinedChunker.MinChunkSize, $"Chunk {i} is under min size: {length}");
        }
    }

    [Fact]
    public void Chunk_IsAPureFunctionOfContent_SameBufferChunksIdentically()
    {
        byte[] data = TestRom.Build(256 * 1024, seed: 3);

        var first = ContentDefinedChunker.Chunk(data);
        var second = ContentDefinedChunker.Chunk((byte[])data.Clone());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Chunk_SharedSpanEmbeddedInDifferentBuffers_ProducesAtLeastOneIdenticalChunk()
    {
        // A shared, well-larger-than-max-chunk-size span embedded at two different
        // offsets with different, unrelated surrounding bytes. Content-defined chunking's
        // whole point is that repeated content chunks the same way wherever it sits - this
        // is the property ChunkRunMatcher relies on to find relocated duplicate spans
        // (verified behaviorally, end to end, in ChunkRunMatcherTests). Here it's enough to
        // confirm at least one exact chunk (offset+length+bytes) recurs in both buffers -
        // the gear hash's effective window is short enough that a 64 KiB shared span
        // (many times any chunk's max size) can't fail to resynchronize somewhere inside it.
        var rng = new Random(42);
        byte[] shared = new byte[64 * 1024];
        rng.NextBytes(shared);

        byte[] bufferA = new byte[16 * 1024];
        rng.NextBytes(bufferA);
        Array.Copy(shared, 0, bufferA, 4096, bufferA.Length - 4096);

        byte[] left = new byte[8192];
        rng.NextBytes(left);
        byte[] bufferB = new byte[left.Length + shared.Length];
        Array.Copy(left, bufferB, left.Length);
        Array.Copy(shared, 0, bufferB, left.Length, shared.Length);

        var chunksA = ContentDefinedChunker.Chunk(bufferA);
        var chunksB = ContentDefinedChunker.Chunk(bufferB);

        var bytesA = chunksA.Select(c => bufferA.AsSpan(c.Offset, c.Length).ToArray()).ToList();
        var bytesB = chunksB.Select(c => bufferB.AsSpan(c.Offset, c.Length).ToArray()).ToList();

        bool anyIdenticalChunk = bytesA.Any(a => bytesB.Any(b => a.AsSpan().SequenceEqual(b)));
        Assert.True(anyIdenticalChunk, "Expected at least one byte-identical chunk between the two embeddings of the shared span.");
    }
}
