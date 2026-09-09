using SharpCompress.Archives;

namespace Ndz.Core.Archives;

/// <summary>
/// Looks inside a .zip/.7z/.rar archive without ever extracting anything to disk - what
/// lets a real ROM collection, where every game is often its own separate archive file,
/// get dropped straight into the Pack/Examine queues with no manual extraction step and
/// no throwaway files written to the user's storage along the way. Backed by
/// GrindCore.SharpCompress (a fork of the well-known SharpCompress with GrindCore's own
/// native codecs underneath) - zip and 7z are read AND write capable there, rar is
/// read-only, but this project only ever reads (nothing here writes archives). See
/// <see cref="RomSource"/> for the pointer type built from what this class finds.
/// </summary>
public static class ArchiveExtractor
{
    public static readonly IReadOnlyList<string> SupportedExtensions = [".zip", ".7z", ".rar"];

    public static bool IsSupportedArchive(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lists the entry keys (not the bytes) of every entry in the archive at
    /// <paramref name="archivePath"/> whose extension is in <paramref name="wantedExtensions"/> -
    /// cheap, since an archive's own central directory/header enumerates its entries
    /// without decompressing any of them. Pair each returned key with
    /// <see cref="RomSource.ForArchiveEntry"/> to build a pointer that only actually reads
    /// (and decompresses) that one entry when something asks for its bytes.
    /// </summary>
    public static List<string> ListMatchingEntryKeys(string archivePath, IReadOnlyCollection<string> wantedExtensions)
    {
        using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
        var keys = new List<string>();
        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (entry.IsDirectory || entry.Key is null)
                continue;
            if (wantedExtensions.Contains(Path.GetExtension(entry.Key), StringComparer.OrdinalIgnoreCase))
                keys.Add(entry.Key);
        }
        return keys;
    }

    /// <summary>
    /// Reads one entry's bytes straight from the archive's own decompression stream - no
    /// extraction to disk. Re-opens the archive fresh each call rather than keeping it (or
    /// any entry stream) open across calls, so reading many entries of the same archive
    /// this way is real repeated work, not free - fine for this project's own usage
    /// pattern (one entry, once per actual Analyze/Pack/Unpack/Checksums action a person
    /// triggers), not meant for a tight loop over every entry.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive no longer contains an entry with this key (it changed on disk between listing and reading, or the key was wrong to begin with).</exception>
    public static byte[] ReadEntryBytes(string archivePath, string entryKey)
    {
        using IArchive archive = ArchiveFactory.OpenArchive(archivePath);
        IArchiveEntry entry = archive.Entries.FirstOrDefault(e => e.Key == entryKey)
            ?? throw new InvalidDataException($"\"{archivePath}\" no longer has an entry named \"{entryKey}\".");

        using Stream stream = entry.OpenEntryStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
