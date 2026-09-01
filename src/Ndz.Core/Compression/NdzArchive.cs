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
/// Every block must be tagged <see cref="BlockMode.Plain"/>, <see cref="BlockMode.Dict"/>,
/// or one of the five filter modes (see <see cref="BlockMode"/>/<see cref="BlockFilters"/>)
/// - any other raw mode byte fails loudly, naming the exact frame/block/mode, rather than
/// misinterpreting bytes it doesn't understand.
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
    private readonly byte[]? _baseRom;
    // One ZStdBlock per distinct base-window offset actually encountered, built lazily
    // and kept for the archive's lifetime (a windowed dictionary decompressor is
    // expensive to construct, and the same window offset can recur across many blocks -
    // matches the same fresh-CDict-per-window cost model as NdzWriter's write side, just
    // cached here since a reader, unlike the writer, revisits the exact same offset
    // across independent ReadAt calls too).
    private readonly Dictionary<uint, ZStdBlock> _baseWindowBlocks = new();
    // Scratch space for Shuffle2/Shuffle4 blocks: ShuffleInverse is a full gather, not
    // an in-place operation, so a shuffled block is decompressed here first, then
    // scattered into the real output buffer. Sized once to the archive's own block size
    // and reused across every block/frame.
    private readonly byte[] _shuffleScratch;
    private byte[]? _cachedFrame;
    private int _cachedFrameIndex = -1;
    private bool _disposed;

    public NdzFrontMatter FrontMatter { get; }
    public IReadOnlyList<SeekTableEntry> SeekTable { get; }

    /// <summary>Total decompressed length - the original .nds size.</summary>
    public long Length => FrontMatter.OriginalSize;

    private NdzArchive(byte[] data, NdzFrontMatter frontMatter, SeekTableEntry[] seekTable, long payloadStart, byte[]? dictionary, byte[]? baseRom)
    {
        _data = data;
        FrontMatter = frontMatter;
        SeekTable = seekTable;
        _baseRom = baseRom;
        // Read from the file's own flags rather than assuming NdzConstants.BlockSize:
        // that constant is only what NdzWriter always produces by default (matching
        // pack.rs's fixed 8 KiB), but block size is a real per-file variable - ndztool.py's
        // own decode logic defaults to 4 KiB when block_log2 is unset. See NdzFlagsExtensions.GetBlockSize.
        _blockSize = frontMatter.Flags.GetBlockSize();
        _plainBlock = new ZStdBlock(new CompressionOptions { Type = CompressionType.Level1, BlockSize = _blockSize });
        _dictBlock = dictionary == null
            ? null
            : new ZStdBlock(new CompressionOptions { Type = CompressionType.Level1, BlockSize = _blockSize, InitProperties = dictionary });
        _shuffleScratch = new byte[_blockSize];

        _compressedFrameOffsets = new long[seekTable.Length];
        long c = payloadStart;
        for (int i = 0; i < seekTable.Length; i++)
        {
            _compressedFrameOffsets[i] = c;
            c += seekTable[i].CompressedSize;
        }
    }

    /// <param name="baseRom">
    /// The base .nds this file was patched against (see <see cref="NdzFlags.BasePatch"/>/
    /// <see cref="BaseRomIndex"/>) - required whenever the file's own front-matter has
    /// `BasePatch` set (verified immediately against the front-matter's own base size/
    /// gameCode/header-hash fields, matching `ndztool.py`'s own `decode_ndz_blob`
    /// checks exactly), and otherwise ignored if supplied for a file that doesn't need
    /// it - also matching the reference's own leniency there.
    /// </param>
    public static NdzArchive Open(byte[] ndzBytes, byte[]? baseRom = null)
    {
        ArgumentNullException.ThrowIfNull(ndzBytes);
        if (ndzBytes.Length < NdzConstants.FrontMatterSize + NdzConstants.TrailerFooterSize)
            throw new InvalidDataException("File is too small to be a valid .ndz (shorter than front-matter + trailer footer).");

        var frontMatter = NdzFrontMatter.Read(ndzBytes.AsSpan(0, NdzConstants.FrontMatterSize));

        if (frontMatter.Flags.HasFlag(NdzFlags.BasePatch))
        {
            if (baseRom == null)
            {
                throw new NotSupportedException(
                    "This .ndz uses base-ROM patch mode (flags bit 4) and needs the base ROM to decode - " +
                    "pass it as NdzArchive.Open's baseRom parameter.");
            }
            if (frontMatter.BaseOriginalSize != (uint)baseRom.Length)
            {
                throw new InvalidDataException(
                    $"Base ROM size mismatch: this .ndz wants a {frontMatter.BaseOriginalSize:N0}-byte base, got {baseRom.Length:N0}.");
            }
            if (baseRom.Length < NdzConstants.NdsHeader.HeaderLength)
                throw new InvalidDataException($"Base ROM is only {baseRom.Length} bytes; too small to be an .nds ROM.");
            uint baseGameCode = BinaryPrimitives.ReadUInt32LittleEndian(baseRom.AsSpan(NdzConstants.NdsHeader.GameCodeOffset, 4));
            if (frontMatter.BaseGameCode != baseGameCode)
                throw new InvalidDataException("Base ROM gameCode mismatch - wrong base ROM.");
            byte[] baseHeaderHash = Blake2b.Hash(baseRom.AsSpan(0, NdzConstants.NdsHeader.HeaderLength), NdzConstants.BaseHeaderHashLength);
            if (!frontMatter.BaseHeaderHash.AsSpan().SequenceEqual(baseHeaderHash))
                throw new InvalidDataException("Base ROM header hash mismatch - wrong base ROM.");
        }

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

        return new NdzArchive(ndzBytes, frontMatter, seekTable, payloadStart, dictionary, baseRom);
    }

    public static NdzArchive OpenFile(string path, string? baseRomPath = null) =>
        Open(File.ReadAllBytes(path), baseRomPath == null ? null : File.ReadAllBytes(baseRomPath));

    /// <summary>
    /// Reads a file's front-matter and seek table without requiring (or verifying) a
    /// base ROM, even if <see cref="NdzFlags.BasePatch"/> is set - matches `ndztool.py`'s
    /// own `cmd_info`, which never needs `--base` since it only ever reads header
    /// fields, not block content. Not a substitute for <see cref="Open"/> - nothing here
    /// is decodable (no dictionary content is read either), this is metadata only.
    /// </summary>
    public static (NdzFrontMatter FrontMatter, IReadOnlyList<SeekTableEntry> SeekTable) ReadInfo(byte[] ndzBytes)
    {
        ArgumentNullException.ThrowIfNull(ndzBytes);
        if (ndzBytes.Length < NdzConstants.FrontMatterSize + NdzConstants.TrailerFooterSize)
            throw new InvalidDataException("File is too small to be a valid .ndz (shorter than front-matter + trailer footer).");

        var frontMatter = NdzFrontMatter.Read(ndzBytes.AsSpan(0, NdzConstants.FrontMatterSize));
        var seekTable = ReadSeekTable(ndzBytes, frontMatter.DictionaryStoredSize, out _);
        return (frontMatter, seekTable);
    }

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
    /// </summary>
    private byte[] GetDecompressedFrame(int frameIndex)
    {
        if (_cachedFrameIndex == frameIndex)
            return _cachedFrame!;

        var entry = SeekTable[frameIndex];
        long frameStart = _compressedFrameOffsets[frameIndex];
        int blockCount = (int)((entry.DecompressedSize + _blockSize - 1) / _blockSize);

        bool hasModesArray = FrontMatter.Flags.HasFlag(NdzFlags.Filters);
        bool hasBaseOffArray = FrontMatter.Flags.HasFlag(NdzFlags.BasePatch);
        BlockMode uniformMode = FrontMatter.HasDictionary ? BlockMode.Dict : BlockMode.Plain;

        int modesArraySize = hasModesArray ? blockCount : 0;
        int baseOffArraySize = hasBaseOffArray ? blockCount * 4 : 0;
        int headerSize = blockCount * 4 + modesArraySize + baseOffArraySize;
        if (headerSize > entry.CompressedSize)
        {
            throw new InvalidDataException(
                $"Frame {frameIndex}'s per-block header ({headerSize} bytes for {blockCount} block(s)) doesn't fit in its declared compressed size ({entry.CompressedSize}).");
        }

        ReadOnlySpan<byte> frameBytes = _data.AsSpan((int)frameStart, (int)entry.CompressedSize);
        ReadOnlySpan<byte> csizeHeader = frameBytes[..(blockCount * 4)];
        ReadOnlySpan<byte> modes = hasModesArray ? frameBytes.Slice(blockCount * 4, blockCount) : default;
        ReadOnlySpan<byte> baseOffs = hasBaseOffArray ? frameBytes.Slice(blockCount * 4 + modesArraySize, baseOffArraySize) : default;
        ReadOnlySpan<byte> blockData = frameBytes[headerSize..];

        var buffer = new byte[entry.DecompressedSize];
        long blockDataOffset = frameStart + headerSize;
        int destOffset = 0;
        int srcOffset = 0;

        for (int b = 0; b < blockCount; b++)
        {
            uint blockCsize = BinaryPrimitives.ReadUInt32LittleEndian(csizeHeader.Slice(b * 4, 4));
            var mode = hasModesArray ? (BlockMode)modes[b] : uniformMode;
            uint baseOff = hasBaseOffArray ? BinaryPrimitives.ReadUInt32LittleEndian(baseOffs.Slice(b * 4, 4)) : BaseRomIndex.NoWindowSentinel;
            int blockDsize = Math.Min(_blockSize, (int)entry.DecompressedSize - destOffset);

            // A recorded base-window offset takes priority over the mode byte entirely
            // (matches ndztool.py's decompress_v2_adv: `if boff != SENTINEL: ... elif
            // mode == DICT: ... else: ...`) - a base-window win is tagged Plain at write
            // time (see NdzWriter.CompressFrame's remarks), so the mode byte alone can't
            // distinguish it; only baseOff can.
            //
            // Otherwise every mode other than Dict decodes through the plain codec -
            // filter modes included, since the transform is applied to the plaintext
            // before/after a perfectly ordinary plain zstd block (matches
            // decompress_v2_adv: only NDZ_MODE_DICT reaches the dict decompressor).
            int deltaStride = BlockFilters.GetDeltaStride(mode);
            int shufflePlanes = BlockFilters.GetShufflePlaneCount(mode);
            ZStdBlock decoder;
            if (baseOff != BaseRomIndex.NoWindowSentinel)
            {
                decoder = GetOrCreateBaseWindowBlock(baseOff, frameIndex, b);
            }
            else if (mode == BlockMode.Dict)
            {
                if (_dictBlock == null)
                {
                    throw new NotSupportedException(
                        $"Frame {frameIndex} block {b} uses Dict/{(byte)BlockMode.Dict} compression mode, " +
                        "but this file has no dictionary section.");
                }
                decoder = _dictBlock;
            }
            else if (mode == BlockMode.Plain || deltaStride != 0 || shufflePlanes != 0)
            {
                decoder = _plainBlock;
            }
            else
            {
                throw new NotSupportedException(
                    $"Frame {frameIndex} block {b} uses compression mode {(byte)mode}, which isn't a " +
                    "recognized BlockMode value (0-6) - the file may be corrupt or use a format extension " +
                    "this port doesn't know about.");
            }

            // A base-window hit is never itself a filter - filters and base-patch are
            // mutually exclusive per block (NdzWriter never applies both), so skip the
            // delta/shuffle inverse below for it even though `mode` might technically be
            // Plain either way.
            if (baseOff != BaseRomIndex.NoWindowSentinel)
            {
                deltaStride = 0;
                shufflePlanes = 0;
            }

            if (srcOffset + blockCsize > blockData.Length)
            {
                throw new InvalidDataException(
                    $"Frame {frameIndex} block {b}'s compressed size ({blockCsize}) runs past the frame's declared data.");
            }

            // Shuffle needs a scratch decode target (ShuffleInverse is a full gather,
            // not in-place); everything else decodes straight into the real output slice.
            bool needsShuffleScratch = shufflePlanes != 0;
            byte[] decompressTarget = needsShuffleScratch ? _shuffleScratch : buffer;
            int decompressOffset = needsShuffleScratch ? 0 : destOffset;

            int dstCount = blockDsize;
            CompressionResultCode result = decoder.Decompress(
                _data, (int)(blockDataOffset + srcOffset), (int)blockCsize,
                decompressTarget, decompressOffset, ref dstCount);

            if (result != CompressionResultCode.Success)
                throw new InvalidDataException($"Failed to decompress frame {frameIndex} block {b}: {result}");
            if (dstCount != blockDsize)
                throw new InvalidDataException($"Frame {frameIndex} block {b} decompressed to {dstCount} bytes, expected {blockDsize}.");

            if (deltaStride != 0)
                BlockFilters.DeltaInverse(buffer.AsSpan(destOffset, blockDsize), deltaStride);
            else if (needsShuffleScratch)
                BlockFilters.ShuffleInverse(_shuffleScratch.AsSpan(0, blockDsize), buffer.AsSpan(destOffset, blockDsize), shufflePlanes);

            srcOffset += (int)blockCsize;
            destOffset += blockDsize;
        }

        _cachedFrameIndex = frameIndex;
        _cachedFrame = buffer;
        return buffer;
    }

    /// <summary>
    /// A decompressor primed with the base ROM's own bytes at <paramref name="baseOff"/>
    /// as a one-shot raw-content dictionary - built once per distinct offset and cached
    /// for the archive's lifetime (see <see cref="_baseWindowBlocks"/>'s remarks).
    /// </summary>
    private ZStdBlock GetOrCreateBaseWindowBlock(uint baseOff, int frameIndex, int blockIndex)
    {
        if (_baseWindowBlocks.TryGetValue(baseOff, out var cached))
            return cached;

        if (_baseRom == null)
        {
            // NdzArchive.Open already refuses to open a BasePatch file with no baseRom
            // supplied, so this can only happen if FrontMatter.Flags.BasePatch was
            // somehow clear while a baseOff array was still present - a corrupt file.
            throw new InvalidDataException(
                $"Frame {frameIndex} block {blockIndex} records a base-window offset, but this archive has no base ROM.");
        }
        if ((long)baseOff + BaseRomIndex.WindowSize > _baseRom.Length)
        {
            throw new InvalidDataException(
                $"Frame {frameIndex} block {blockIndex}'s base window (offset 0x{baseOff:X}) runs past the end of the base ROM.");
        }

        byte[] window = _baseRom.AsSpan((int)baseOff, BaseRomIndex.WindowSize).ToArray();
        var block = new ZStdBlock(new CompressionOptions { Type = CompressionType.Level1, BlockSize = _blockSize, InitProperties = window });
        _baseWindowBlocks[baseOff] = block;
        return block;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _plainBlock.Dispose();
        _dictBlock?.Dispose();
        foreach (var block in _baseWindowBlocks.Values)
            block.Dispose();
        _disposed = true;
    }
}
