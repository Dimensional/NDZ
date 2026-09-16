using Ndz.Core.XDelta;

namespace Ndz.Core.Tests;

public class XDeltaTests
{
    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void RoundTrips_SingleByteChange()
    {
        byte[] source = RandomBytes(4096, seed: 1);
        byte[] target = (byte[])source.Clone();
        target[2000] ^= 0xFF;

        byte[] delta = XDeltaCodec.Generate(source, target);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Equal(target, applied);
    }

    [Fact]
    public void RoundTrips_SmallInsertAndDelete()
    {
        byte[] source = RandomBytes(8192, seed: 2);

        // Insert 37 bytes at offset 3000, then delete 50 bytes at offset 6000 (post-insert).
        var target = new List<byte>(source[..3000]);
        target.AddRange(RandomBytes(37, seed: 99));
        target.AddRange(source[3000..6000]);
        target.AddRange(source[6050..]);
        byte[] targetArray = target.ToArray();

        byte[] delta = XDeltaCodec.Generate(source, targetArray);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Equal(targetArray, applied);
    }

    [Fact]
    public void RoundTrips_LargeMostlyIdenticalRomHackShape()
    {
        // The realistic case: a big shared ROM with one small changed region, like a ROM
        // hack or a version diff - dominated by huge COPY matches, one small ADD run.
        byte[] source = RandomBytes(2 * 1024 * 1024, seed: 3);
        byte[] target = (byte[])source.Clone();
        RandomBytes(4096, seed: 4).CopyTo(target, 1_000_000);

        byte[] delta = XDeltaCodec.Generate(source, target);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Equal(target, applied);
        // Real compression, not a fallback to storing the whole target literally.
        Assert.True(delta.Length < target.Length / 4);
    }

    [Fact]
    public void RoundTrips_UnrelatedBuffers()
    {
        byte[] source = RandomBytes(4096, seed: 5);
        byte[] target = RandomBytes(4096, seed: 6);

        byte[] delta = XDeltaCodec.Generate(source, target);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Equal(target, applied);
    }

    [Fact]
    public void RoundTrips_RepeatedByteRuns()
    {
        // ROM padding - long spans of a single repeated byte value - should hit the RUN path.
        byte[] source = RandomBytes(4096, seed: 7);
        var target = new List<byte>(source);
        for (int i = 0; i < 200; i++)
            target.Add(0xFF);
        target.AddRange(RandomBytes(100, seed: 8));
        byte[] targetArray = target.ToArray();

        byte[] delta = XDeltaCodec.Generate(source, targetArray);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Equal(targetArray, applied);
    }

    [Fact]
    public void RoundTrips_EmptySource()
    {
        byte[] source = Array.Empty<byte>();
        byte[] target = RandomBytes(1024, seed: 9);

        byte[] delta = XDeltaCodec.Generate(source, target);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Equal(target, applied);
    }

    [Fact]
    public void RoundTrips_EmptyTarget()
    {
        byte[] source = RandomBytes(1024, seed: 10);
        byte[] target = Array.Empty<byte>();

        byte[] delta = XDeltaCodec.Generate(source, target);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Empty(applied);
    }

    [Fact]
    public void Apply_RejectsBadMagic()
    {
        byte[] source = RandomBytes(64, seed: 11);
        byte[] badDelta = { 0x00, 0x01, 0x02, 0x03, 0x04 };

        Assert.Throws<XDeltaException>(() => XDeltaCodec.Apply(source, badDelta));
    }

    [Fact]
    public void Apply_RejectsTruncatedDelta()
    {
        byte[] source = RandomBytes(4096, seed: 12);
        byte[] target = RandomBytes(4096, seed: 13);
        byte[] delta = XDeltaCodec.Generate(source, target);

        byte[] truncated = delta[..(delta.Length / 2)];

        Assert.Throws<XDeltaException>(() => XDeltaCodec.Apply(source, truncated));
    }

    [Fact]
    public void RoundTrips_DjwSingleGroupPath()
    {
        // A small (<1000 byte), highly compressible ADD section forces DJW's groups==1 path.
        byte[] source = Array.Empty<byte>();
        var target = new List<byte>();
        for (int i = 0; i < 500; i++) target.Add((byte)(i % 3));
        byte[] targetArray = target.ToArray();

        byte[] delta = XDeltaCodec.Generate(source, targetArray);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Equal(targetArray, applied);
    }

    [Fact]
    public void RoundTrips_DjwMultiGroupPath()
    {
        // A large (>1000 byte), varied-but-skewed ADD section forces DJW's multi-group
        // path, exercising real iterative table refinement (not just a single Huffman table).
        byte[] source = Array.Empty<byte>();
        var rng = new Random(20);
        var target = new byte[20000];
        for (int i = 0; i < target.Length; i++)
        {
            // Skewed distribution (not uniform random) - gives the multi-table grouping
            // something real to exploit, unlike uniform noise.
            target[i] = rng.Next(10) < 7 ? (byte)rng.Next(4) : (byte)rng.Next(256);
        }

        byte[] delta = XDeltaCodec.Generate(source, target);
        byte[] applied = XDeltaCodec.Apply(source, delta);

        Assert.Equal(target, applied);
    }

    [Fact]
    public void Apply_RejectsWrongSourceViaChecksumMismatch()
    {
        // Needs a delta that actually contains COPY instructions referencing the source
        // (an edited clone, not two unrelated buffers - two unrelated random buffers
        // wouldn't share any matches, so the delta would be pure ADD and wouldn't
        // reference source at all, making this test meaningless).
        byte[] source = RandomBytes(4096, seed: 14);
        byte[] target = (byte[])source.Clone();
        target[1000] ^= 0xFF;
        byte[] delta = XDeltaCodec.Generate(source, target);

        byte[] wrongSource = RandomBytes(4096, seed: 16);

        Assert.Throws<XDeltaException>(() => XDeltaCodec.Apply(wrongSource, delta));
    }
}
