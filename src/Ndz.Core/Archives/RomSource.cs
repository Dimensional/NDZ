namespace Ndz.Core.Archives;

/// <summary>
/// Points at one ROM's bytes without necessarily having read them yet - either a real file
/// on disk, or one entry inside a .zip/.7z/.rar archive, read directly from the archive's
/// own decompression stream on demand (see <see cref="ArchiveExtractor.ReadEntryBytes"/>)
/// with no extraction to disk at any point. Avoids a needless write to the user's storage
/// for something that's only ever going to be read back, never modified - and matches how
/// this project's own GUI already treats a plain file (re-read from disk each time it's
/// actually needed, rather than keeping a whole queue of ROMs resident in memory just in
/// case): for an archive entry, "re-read" means re-opening the archive and
/// re-decompressing that one entry, trading some CPU (and, for a solid archive, having to
/// decompress whatever precedes it too) for zero disk writes and no extra memory held
/// between uses.
/// </summary>
public sealed class RomSource
{
    /// <summary>Compact label for space-constrained UI - just the file name for a real file, or "entry.nds (in archive.zip)" for an archive entry.</summary>
    public string ShortLabel { get; }

    /// <summary>Full detail for a tooltip or similar - the complete file path, or "C:\...\archive.zip » entry/path/inside.nds" for an archive entry.</summary>
    public string FullLabel { get; }

    /// <summary>A real, existing directory worth suggesting as a save/output location - the file's own folder, or the containing archive's own folder for an archive entry.</summary>
    public string? DirectoryHint { get; }

    /// <summary>This ROM's own extension (".nds", ".ndz", ...) - the real file's own, or the archive entry's own, never the containing archive's.</summary>
    public string Extension { get; }

    private readonly Func<byte[]> _readBytes;

    private RomSource(string shortLabel, string fullLabel, string? directoryHint, string extension, Func<byte[]> readBytes)
    {
        ShortLabel = shortLabel;
        FullLabel = fullLabel;
        DirectoryHint = directoryHint;
        Extension = extension;
        _readBytes = readBytes;
    }

    public static RomSource ForFile(string path)
    {
        string full = Path.GetFullPath(path);
        return new RomSource(Path.GetFileName(path), full, Path.GetDirectoryName(full), Path.GetExtension(path), () => File.ReadAllBytes(path));
    }

    /// <param name="archivePath">The archive's own path on disk.</param>
    /// <param name="entryKey">The entry's own path inside the archive - SharpCompress's own <c>IEntry.Key</c>, e.g. <c>"roms/Mario64.nds"</c>.</param>
    public static RomSource ForArchiveEntry(string archivePath, string entryKey)
    {
        string archiveFull = Path.GetFullPath(archivePath);
        string entryFileName = entryKey.Replace('\\', '/').Split('/')[^1];
        return new RomSource(
            $"{entryFileName} (in {Path.GetFileName(archivePath)})",
            $"{archiveFull} \u00bb {entryKey}",
            Path.GetDirectoryName(archiveFull),
            Path.GetExtension(entryFileName),
            () => ArchiveExtractor.ReadEntryBytes(archivePath, entryKey));
    }

    public byte[] ReadBytes() => _readBytes();
}
