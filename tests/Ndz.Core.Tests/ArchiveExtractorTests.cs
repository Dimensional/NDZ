using System.IO.Compression;
using Ndz.Core.Archives;

namespace Ndz.Core.Tests;

/// <summary>
/// Builds a real .zip fixture (via `System.IO.Compression`, entirely unrelated to
/// GrindCore.SharpCompress - independent ground truth for what's actually inside it) once
/// per test class instance, and exercises <see cref="ArchiveExtractor"/>/<see cref="RomSource"/>
/// against it. No test here writes anything under the OS temp folder beyond the fixture
/// zip itself - that's the entire point of these two types (see their own remarks): an
/// archive entry is read straight from its decompression stream, never extracted to disk.
/// </summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _zipPath;
    private readonly byte[] _ndsBytes = "fake nds content, byte for byte"u8.ToArray();
    private readonly byte[] _dsiBytes = "fake dsi content, different bytes"u8.ToArray();

    public ArchiveExtractorTests()
    {
        _zipPath = Path.Combine(Path.GetTempPath(), $"archive-extractor-tests-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(_zipPath, ZipArchiveMode.Create);

        using (var s = zip.CreateEntry("roms/Mario64.nds").Open())
            s.Write(_ndsBytes);
        using (var s = zip.CreateEntry("roms/Something.dsi").Open())
            s.Write(_dsiBytes);
        using (var s = zip.CreateEntry("readme.txt").Open())
            s.Write("not a rom"u8);
        zip.CreateEntry("empty-folder/"); // a directory entry - must never be treated as a file
    }

    public void Dispose() => File.Delete(_zipPath);

    [Theory]
    [InlineData("game.zip", true)]
    [InlineData("game.7z", true)]
    [InlineData("game.rar", true)]
    [InlineData("game.ZIP", true)] // case-insensitive
    [InlineData("game.nds", false)]
    [InlineData("game.ndz", false)]
    [InlineData("game.tar", false)] // a real archive format, but not one this project supports
    public void IsSupportedArchive_RecognizesExactlyZip7zRar(string fileName, bool expected) =>
        Assert.Equal(expected, ArchiveExtractor.IsSupportedArchive(fileName));

    [Fact]
    public void ListMatchingEntryKeys_FindsOnlyMatchingExtensions_NotDirectoriesOrOthers()
    {
        List<string> keys = ArchiveExtractor.ListMatchingEntryKeys(_zipPath, [".nds", ".dsi"]);

        Assert.Equal(2, keys.Count);
        Assert.Contains("roms/Mario64.nds", keys);
        Assert.Contains("roms/Something.dsi", keys);
    }

    [Fact]
    public void ListMatchingEntryKeys_NarrowsToJustOneExtension_WhenAsked()
    {
        List<string> keys = ArchiveExtractor.ListMatchingEntryKeys(_zipPath, [".nds"]);
        Assert.Equal(["roms/Mario64.nds"], keys);
    }

    [Fact]
    public void ReadEntryBytes_ReturnsExactContent()
    {
        byte[] actual = ArchiveExtractor.ReadEntryBytes(_zipPath, "roms/Mario64.nds");
        Assert.Equal(_ndsBytes, actual);
    }

    [Fact]
    public void ReadEntryBytes_ThrowsInvalidDataException_ForAMissingKey()
    {
        Assert.Throws<InvalidDataException>(() => ArchiveExtractor.ReadEntryBytes(_zipPath, "does/not/exist.nds"));
    }

    [Fact]
    public void RomSource_ForArchiveEntry_ReadsCorrectBytes_AndDescribesItself()
    {
        RomSource source = RomSource.ForArchiveEntry(_zipPath, "roms/Mario64.nds");

        Assert.Equal(_ndsBytes, source.ReadBytes());
        Assert.Equal(".nds", source.Extension);
        Assert.Equal("Mario64.nds (in " + Path.GetFileName(_zipPath) + ")", source.ShortLabel);
        Assert.Contains(_zipPath, source.FullLabel);
        Assert.Contains("roms/Mario64.nds", source.FullLabel);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(_zipPath)), source.DirectoryHint);
    }

    [Fact]
    public void RomSource_ForFile_ReadsCorrectBytes_AndDescribesItself()
    {
        string path = Path.Combine(Path.GetTempPath(), $"romsource-tests-{Guid.NewGuid():N}.nds");
        try
        {
            File.WriteAllBytes(path, _ndsBytes);
            RomSource source = RomSource.ForFile(path);

            Assert.Equal(_ndsBytes, source.ReadBytes());
            Assert.Equal(".nds", source.Extension);
            Assert.Equal(Path.GetFileName(path), source.ShortLabel);
            Assert.Equal(Path.GetFullPath(path), source.FullLabel);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(path)), source.DirectoryHint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The actual point of <see cref="RomSource"/>/<see cref="ArchiveExtractor"/>: reading
    /// an entry's bytes must never write anything to disk. Snapshots the OS temp folder's
    /// own top-level contents before and after a read and asserts nothing new appeared -
    /// the fixture zip itself is excluded since it's this test class's own setup, not
    /// something ReadEntryBytes created.
    /// </summary>
    [Fact]
    public void ReadEntryBytes_WritesNothingToDisk()
    {
        string osTemp = Path.GetTempPath();
        var before = new HashSet<string>(Directory.EnumerateFileSystemEntries(osTemp), StringComparer.OrdinalIgnoreCase);

        _ = ArchiveExtractor.ReadEntryBytes(_zipPath, "roms/Mario64.nds");
        _ = RomSource.ForArchiveEntry(_zipPath, "roms/Something.dsi").ReadBytes();

        var after = new HashSet<string>(Directory.EnumerateFileSystemEntries(osTemp), StringComparer.OrdinalIgnoreCase);
        after.ExceptWith(before);
        Assert.Empty(after);
    }
}
