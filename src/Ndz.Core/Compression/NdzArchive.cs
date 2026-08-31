using System.Buffers.Binary;
using Nanook.GrindCore;
using Nanook.GrindCore.ZStd;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// An opened .ndz file: parsed front-matter and (frame-level) seek table, plus
/// random-access reads that decompress only the frame(s) - and within a frame, only
/// that frame's own 8 KB blocks - a read actually touches. This is the payoff of the
/// seek table - callers don't have to decompress the whole ROM just to read a chunk of
/// it (e.g. mounting NitroFS content without extracting the full image first). Block
/// size is read from the file's own front-matter flags, not assumed to be
/// <see cref="NdzConstants.BlockSize"/> - see <see cref="Format.NdzFlagsExtensions.GetBlockSize"/>.
///
/// Every block must be tagged <see cref="BlockMode.Plain"/> or <see cref="BlockMode.Dict"/>
/// - this reader has no way to decode filter-transformed blocks yet (see
/// <see cref="BlockMode"/>'s remarks) and fails loudly, naming the exact frame/block/mode,
/// rather than misinterpreting bytes it doesn't understand.
///
/// Holds the whole compressed file in memory; frames are decompressed lazily and the
/// most recently used one is cached for fast sequential reads.
/// </summary>
public sealed class NdzArchive : IDisposable
{
    private readonly byte[] _data;
    private readonly long[] _compressedFrameOffsets;
    private readonly ZStdBlock _plainBlock;
    private readonly ZStdBlock? _dictBlock;
    private readonly int _blockSize;
    private byte[]? _cachedFrame;
    private int _cachedFrameIndex = -1;
    private bool _disposed;

    public NdzFrontMatter FrontMatter { get; }
    public IReadOnlyList<SeekTableEntry> SeekTable { get; }

    /// <summary>Total decompressed length - the original .nds size.</summary>
    public long Length => FrontMatter.OriginalSize;

    private NdzArchive(byte[] data, NdzFrontMatter frontMatter, SeekTableEntry[] seekTable, long payloadStart, byte[]? dictionary)
    {
        _data = data;
        FrontMatter = frontMatter;
        SeekTable = seekTable;
        // Read from the file's own flags rather than assuming NdzConstants.BlockSize:
        // that constant is only what NdzWriter always produces (matching pack.rs's fixed
        // 8 KiB), but block size is a real per-file variable - ndzunpack.py's own decode
        // logic defaults to 4 KiB when block_log2 is unset. See NdzFlagsExtensions.GetBlockSize.
        _blockSize = frontMatter.Flags.GetBlockSize();
        _plainBlock = new ZStdBlock(new CompressionOptions { Type = CompressionType.Level1, BlockSize = _blockSize });
        _dictBlock = dictionary == null
            ? null
            : new ZStdBlock(new CompressionOptions { Type = CompressionType.Level1, BlockSize = _blockSize, InitProperties = dictionary });

        _compressedFrameOffsets = new long[seekTable.Length];
        long c = payloadStart;
        for (int i = 0; i < seekTable.Length; i++)
        {
            _compressedFrameOffsets[i] = c;
            c += seekTable[i].CompressedSize;
        }
    }

    public static NdzArchive Open(byte[] ndzBytes)
    {
        ArgumentNullException.ThrowIfNull(ndzBytes);
        if (ndzBytes.Length < NdzConstants.FrontMatterSize + NdzConstants.TrailerFooterSize)
            throw new InvalidDataException("File is too small to be a valid .ndz (shorter than front-matter + trailer footer).");

        var frontMatter = NdzFrontMatter.Read(ndzBytes.AsSpan(0, NdzConstants.FrontMatterSize));

        byte[]? dictionary = null;
        if (frontMatter.HasDictionary)
        {
            // The dictionary section is documented as "raw bytes, verbatim" - not
            // separately compressed. NdzWriter always stores stored == decompressed for
            // exactly that reason; a file claiming otherwise uses a variant this reader
            // doesn't understand, so fail loudly rather than misinterpret it.
            if (frontMatter.DictionaryStoredSize != frontMatter.DictionaryDecompressedSize)
            {
                throw new NotSupportedException(
                    $"This .ndz's dictionary section has a different stored size ({frontMatter.DictionaryStoredSize}) " +
                    $"than decompressed size ({frontMatter.DictionaryDecompressedSize}) - a compressed dictionary " +
                    "section isn't supported (the format is documented as verbatim raw bytes).");
            }

            long dictEnd = (long)NdzConstants.FrontMatterSize + frontMatter.DictionaryStoredSize;
            if (dictEnd > ndzBytes.Length)
                throw new InvalidDataException("Declared dictionary section runs past the end of the file.");

            dictionary = ndzBytes.AsSpan(NdzConstants.FrontMatterSize, (int)frontMatter.DictionaryStoredSize).ToArray();
        }

        var seekTable = ReadSeekTable(ndzBytes, frontMatter.DictionaryStoredSize, out long payloadStart);

        long expectedFrameCount = frontMatter.OriginalSize == 0
            ? 0
            : (frontMatter.OriginalSize + NdzConstants.FrameSize - 1) / NdzConstants.FrameSize;
        if (seekTable.Length != expectedFrameCount)
        {
            throw new InvalidDataException(
                $"Seek table has {seekTable.Length} frame(s), but originalSize ({frontMatter.OriginalSize}) implies {expectedFrameCount}.");
        }

        long decompressedTotal = 0;
        foreach (var entry in seekTable)
            decompressedTotal += entry.DecompressedSize;
        if (decompressedTotal != frontMatter.OriginalSize)
        {
            throw new InvalidDataException(
                $"Seek table's total decompressed size ({decompressedTotal}) doesn't match originalSize ({frontMatter.OriginalSize}).");
        }

        return new NdzArchive(ndzBytes, frontMatter, seekTable, payloadStart, dictionary);
    }

    public static NdzArchive OpenFile(string path) => Open(File.ReadAllBytes(path));

    private static SeekTableEntry[] ReadSeekTable(byte[] ndz, uint dictionaryStoredSize, out long payloadStart)
    {
        int footerOffset = ndz.Length - NdzConstants.TrailerFooterSize;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(ndz.AsSpan(footerOffset + 4, 4));
        if (magic != NdzConstants.TrailerMagic)
            throw new InvalidDataException($"Bad trailer magic: expected 0x{NdzConstants.TrailerMagic:X8}, got 0x{magic:X8}. File may be truncated or corrupt.");

        uint frameCount = BinaryPrimitives.ReadUInt32LittleEndian(ndz.AsSpan(footerOffset, 4));
        long seekTableOffset = footerOffset - (long)frameCount * NdzConstants.SeekTableEntrySize;
        payloadStart = NdzConstants.FrontMatterSize + dictionaryStoredSize;

        if (seekTableOffset < payloadStart)
            throw new InvalidDataException("Corrupt seek table: declared frame count doesn't fit between the front-matter/dictionary and the trailer footer.");

        var entries = new SeekTableEntry[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            int off = (int)seekTableOffset + i * NdzConstants.SeekTableEntrySize;
            uint csize = BinaryPrimitives.ReadUInt32LittleEndian(ndz.AsSpan(off, 4));
            uint dsize = BinaryPrimitives.ReadUInt32LittleEndian(ndz.AsSpan(off + 4, 4));
            entries[i] = new SeekTableEntry(csize, dsize);
        }

        return entries;
    }

    /// <summary>
    /// Reads up to <paramref name="destination"/>'s length bytes of decompressed ROM data
    /// starting at decompressed offset <paramref name="offset"/>. Returns the number of
    /// bytes actually read (fewer than requested only at end-of-archive).
    /// </summary>
    public int ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset > Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        int total = 0;
        while (total < destination.Length && offset + total < Length)
        {
            int frameIndex = (int)((offset + total) / NdzConstants.FrameSize);
            byte[] frameData = GetDecompressedFrame(frameIndex);
            int frameLocalOffset = (int)((offset + total) - (long)frameIndex * NdzConstants.FrameSize);

            int available = frameData.Length - frameLocalOffset;
            int toCopy = Math.Min(available, destination.Length - total);
            frameData.AsSpan(frameLocalOffset, toCopy).CopyTo(destination[total..]);
            total += toCopy;
        }

        return total;
    }

    /// <summary>Decompresses the entire archive back into the original .nds bytes.</summary>
    public byte[] DecompressAll()
    {
        var result = new byte[Length];
        int read = ReadAt(0, result);
        if (read != result.Length)
            throw new InvalidDataException($"Expected to decompress {result.Length} bytes but only got {read}.");
        return result;
    }

    /// <summary>
    /// Decompresses one frame: parses its private
    /// <c>[u32 csize × nblocks][u8 mode × nblocks if Filters][compressed block bytes]</c>
    /// layout and decodes each <see cref="_blockSize"/>-byte block in turn.
    ///
    /// The mode array only exists when <see cref="NdzFlags.Filters"/> is set - confirmed
    /// against both the reference packer (which sets it unconditionally, since it always
    /// writes a mode array) and `ndztool.py`'s own decoder. Without it, every block in
    /// the frame is uniformly <see cref="BlockMode.Dict"/> (if this archive has a
    /// dictionary) or <see cref="BlockMode.Plain"/> (if not) - no per-block byte to read.
    /// See the type-level remarks for why any mode other than <see cref="BlockMode.Plain"/>/
    /// <see cref="BlockMode.Dict"/> throws.
    /// </summary>
    private byte[] GetDecompressedFrame(int frameIndex)
    {
        if (_cachedFrameIndex == frameIndex)
            return _cachedFrame!;

        var entry = SeekTable[frameIndex];
        long frameStart = _compressedFrameOffsets[frameIndex];
        int blockCount = (int)((entry.DecompressedSize + _blockSize - 1) / _blockSize);

        bool hasModesArray = FrontMatter.Flags.HasFlag(NdzFlags.Filters);
        BlockMode uniformMode = FrontMatter.HasDictionary ? BlockMode.Dict : BlockMode.Plain;

        int headerSize = blockCount * 4 + (hasModesArray ? blockCount : 0);
        if (headerSize > entry.CompressedSize)
        {
            throw new InvalidDataException(
                $"Frame {frameIndex}'s per-block header ({headerSize} bytes for {blockCount} block(s)) doesn't fit in its declared compressed size ({entry.CompressedSize}).");
        }

        ReadOnlySpan<byte> frameBytes = _data.AsSpan((int)frameStart, (int)entry.CompressedSize);
        ReadOnlySpan<byte> csizeHeader = frameBytes[..(blockCount * 4)];
        ReadOnlySpan<byte> modes = hasModesArray ? frameBytes.Slice(blockCount * 4, blockCount) : default;
        ReadOnlySpan<byte> blockData = frameBytes[headerSize..];

        var buffer = new byte[entry.DecompressedSize];
        long blockDataOffset = frameStart + headerSize;
        int destOffset = 0;
        int srcOffset = 0;

        for (int b = 0; b < blockCount; b++)
        {
            uint blockCsize = BinaryPrimitives.ReadUInt32LittleEndian(csizeHeader.Slice(b * 4, 4));
            var mode = hasModesArray ? (BlockMode)modes[b] : uniformMode;
            int blockDsize = Math.Min(_blockSize, (int)entry.DecompressedSize - destOffset);

            ZStdBlock decoder;
            if (mode == BlockMode.Plain)
            {
                decoder = _plainBlock;
            }
            else if (mode == BlockMode.Dict && _dictBlock != null)
            {
                decoder = _dictBlock;
            }
            else
            {
                throw new NotSupportedException(
                    $"Frame {frameIndex} block {b} uses compression mode {(byte)mode}" +
                    (mode == BlockMode.Dict ? " (dictionary mode, but this file has no dictionary section)" : "") +
                    $", which isn't implemented (only Plain/{(byte)BlockMode.Plain} and Dict/{(byte)BlockMode.Dict} are " +
                    "supported here - filter modes need the reference packer's filters source confirmed first; see BlockMode's remarks).");
            }

            if (srcOffset + blockCsize > blockData.Length)
            {
                throw new InvalidDataException(
                    $"Frame {frameIndex} block {b}'s compressed size ({blockCsize}) runs past the frame's declared data.");
            }

            int dstCount = blockDsize;
            CompressionResultCode result = decoder.Decompress(
                _data, (int)(blockDataOffset + srcOffset), (int)blockCsize,
                buffer, destOffset, ref dstCount);

            if (result != CompressionResultCode.Success)
                throw new InvalidDataException($"Failed to decompress frame {frameIndex} block {b}: {result}");
            if (dstCount != blockDsize)
                throw new InvalidDataException($"Frame {frameIndex} block {b} decompressed to {dstCount} bytes, expected {blockDsize}.");

            srcOffset += (int)blockCsize;
            destOffset += blockDsize;
        }

        _cachedFrameIndex = frameIndex;
        _cachedFrame = buffer;
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _plainBlock.Dispose();
        _dictBlock?.Dispose();
        _disposed = true;
    }
}
