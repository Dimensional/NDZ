using System.Buffers.Binary;
using Ndz.Core.Format;

namespace Ndz.Core.Compression;

/// <summary>
/// A parsed pair container: two complete, independently-addressed `.ndz` blobs (one
/// self-contained, one base-patched against the other) - see
/// <see cref="NdzPairWriter"/> for the write side and the exact layout. Distinguished
/// from a single `.ndz` by its own magic (<see cref="NdzConstants.PairMagic"/>, 'NDZP')
/// at byte 0, confirmed against `ndztool.py`'s own `read_pair_entries`.
/// </summary>
public sealed class NdzPairContainer
{
    private readonly byte[] _data;

    public IReadOnlyList<NdzPairEntry> Entries { get; }

    /// <summary>
    /// Index of the entry that is itself self-contained (no <see cref="NdzFlags.BasePatch"/>)
    /// - the one every other entry ultimately resolves its base ROM against. Matches
    /// `ndztool.py`'s own `cmd_unpack` resolution: the first entry found without the
    /// base-patch flag set.
    /// </summary>
    public int PlainEntryIndex { get; }

    private NdzPairContainer(byte[] data, NdzPairEntry[] entries, int plainEntryIndex)
    {
        _data = data;
        Entries = entries;
        PlainEntryIndex = plainEntryIndex;
    }

    /// <summary>
    /// Parses <paramref name="bytes"/> as a pair container. Returns false (not an
    /// exception) if the magic doesn't match - matches `read_pair_entries`' own
    /// "not a pair container" contract, since a caller in general doesn't know in
    /// advance whether a `.ndz`-suffixed file is a single blob or a pair.
    /// </summary>
    public static bool TryRead(byte[] bytes, out NdzPairContainer? container)
    {
        container = null;
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < NdzConstants.PairEntriesOffset)
            return false;

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        if (magic != NdzConstants.PairMagic)
            return false;

        uint romCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        long entriesEnd = (long)NdzConstants.PairEntriesOffset + (long)romCount * NdzConstants.PairEntrySize;
        if (entriesEnd > bytes.Length)
        {
            throw new InvalidDataException(
                $"Pair container declares {romCount} ROM(s), but the file is too short to hold that many entry records.");
        }

        var entries = new NdzPairEntry[romCount];
        for (int i = 0; i < romCount; i++)
        {
            int offset = NdzConstants.PairEntriesOffset + i * NdzConstants.PairEntrySize;
            entries[i] = new NdzPairEntry(
                Offset: BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)),
                Size: BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4)),
                OriginalSize: BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8, 4)),
                GameCode: BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12, 4)));
        }

        int plainIndex = -1;
        for (int i = 0; i < entries.Length; i++)
        {
            var flags = (NdzFlags)ReadEntryFlags(bytes, entries[i], i);
            if (!flags.HasFlag(NdzFlags.BasePatch))
            {
                plainIndex = i;
                break;
            }
        }
        if (plainIndex == -1)
        {
            throw new InvalidDataException(
                "Pair container has no self-contained (non-base-patch) entry to resolve a base ROM from.");
        }

        container = new NdzPairContainer(bytes, entries, plainIndex);
        return true;
    }

    /// <summary>Like <see cref="TryRead"/>, but throws instead of returning false.</summary>
    public static NdzPairContainer Read(byte[] bytes)
    {
        if (!TryRead(bytes, out var container))
            throw new InvalidDataException("Not a pair container - bad magic (expected 'NDZP').");
        return container!;
    }

    public static NdzPairContainer ReadFile(string path) => Read(File.ReadAllBytes(path));

    /// <summary>Front-matter and seek table for entry <paramref name="index"/>, without needing any base ROM - matches <see cref="NdzArchive.ReadInfo"/>.</summary>
    public (NdzFrontMatter FrontMatter, IReadOnlyList<SeekTableEntry> SeekTable) ReadEntryInfo(int index) =>
        NdzArchive.ReadInfo(GetEntryBytes(index));

    /// <summary>
    /// Opens entry <paramref name="index"/> - if it's the self-contained one, directly;
    /// otherwise its base is resolved by fully decompressing <see cref="PlainEntryIndex"/>
    /// first, matching `ndztool.py`'s own `cmd_unpack` (a base-patch entry inside a pair
    /// container always resolves against the *other* entry in the same container, never
    /// an externally-supplied file).
    /// </summary>
    public NdzArchive OpenEntry(int index)
    {
        if (index < 0 || index >= Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (index == PlainEntryIndex)
            return NdzArchive.Open(GetEntryBytes(index));

        byte[] resolvedBase = DecompressEntry(PlainEntryIndex);
        return NdzArchive.Open(GetEntryBytes(index), resolvedBase);
    }

    /// <summary>Convenience: opens and fully decompresses entry <paramref name="index"/> in one call.</summary>
    public byte[] DecompressEntry(int index)
    {
        using var archive = OpenEntry(index);
        return archive.DecompressAll();
    }

    private byte[] GetEntryBytes(int index)
    {
        var entry = Entries[index];
        if ((long)entry.Offset + entry.Size > _data.Length)
            throw new InvalidDataException($"Pair container entry {index} runs past the end of the file.");
        return _data.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
    }

    private static uint ReadEntryFlags(byte[] bytes, NdzPairEntry entry, int index)
    {
        // Flags live at the same fixed 0x000C offset inside every entry's own
        // front-matter as a single .ndz file - no need to fully parse it just to check
        // one field.
        const int flagsOffsetInFrontMatter = 0x000C;
        long flagsOffset = (long)entry.Offset + flagsOffsetInFrontMatter;
        if (flagsOffset + 4 > bytes.Length)
            throw new InvalidDataException($"Pair container entry {index}'s offset runs past the end of the file.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)flagsOffset, 4));
    }
}
