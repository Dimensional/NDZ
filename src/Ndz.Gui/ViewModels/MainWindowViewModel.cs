using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Ndz.Core.Format;

namespace Ndz.Gui.ViewModels;

/// <summary>
/// The Pack queue: drop or open any number of real .nds/.dsi ROMs - individually, or whole
/// directory trees - and see them appear as decoded cards (icon/title/id). Base/target
/// bundle grouping isn't wired up yet - every item is still just a flat queue entry.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>ROM file extensions this queue accepts, decoded identically - a .dsi-extension dump is header-for-header the same .nds-shaped format, just used for a DSi-exclusive title rather than a plain or DSi-enhanced one.</summary>
    private static readonly string[] RomExtensions = [".nds", ".dsi"];

    public ObservableCollection<RomEntryViewModel> QueueItems { get; } = [];

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasError;

    public bool HasItems => QueueItems.Count > 0;

    public MainWindowViewModel()
    {
        QueueItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>
    /// Accepts a mix of ROM file paths and directory paths (from a drop or a picker) -
    /// directories are walked recursively for .nds/.dsi files. Runs the filesystem walk
    /// and each ROM's decode off the UI thread so pointing this at a large library folder
    /// doesn't freeze the window; only the final queue-collection mutations happen back on
    /// the UI thread (the default continuation context an `await` resumes on).
    /// </summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        SetError(null);

        (List<string> romFiles, int skippedNdzCount) = await Task.Run(() => ExpandPaths(paths));

        string? lastError = null;
        foreach (string path in romFiles)
        {
            (DecodedRom? decoded, string? error) = await Task.Run(() => TryDecode(path));
            if (decoded is not null)
                QueueItems.Add(ToViewModel(decoded));
            else
                lastError = error;
        }

        if (romFiles.Count > 0)
            RecomputeDuplicates();

        if (lastError is not null)
            SetError(lastError);
        else if (skippedNdzCount > 0)
            SetError($"Skipped {skippedNdzCount} .ndz file{(skippedNdzCount == 1 ? "" : "s")} - already-packed containers belong in Examine, not Pack.");
        else if (romFiles.Count == 0)
            SetError("No .nds or .dsi ROMs found in what was dropped.");
    }

    /// <summary>Single-file convenience wrapper over <see cref="AddPathsAsync"/>, kept for the existing Open-ROM/drop-a-file call sites.</summary>
    public Task AddRomAsync(string path) => AddPathsAsync([path]);

    private static (List<string> RomFiles, int SkippedNdzCount) ExpandPaths(IEnumerable<string> paths)
    {
        var romFiles = new List<string>();
        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedNdz = 0;

        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                string full = Path.GetFullPath(path);
                if (!visitedDirs.Add(full))
                    continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable subfolder (permissions, a broken junction, ...)
                    // shouldn't abort the whole scan - just contributes nothing.
                    continue;
                }

                foreach (string file in files)
                    Classify(file, romFiles, ref skippedNdz);
            }
            else if (File.Exists(path))
            {
                Classify(path, romFiles, ref skippedNdz);
            }
        }

        return (romFiles, skippedNdz);
    }

    private static void Classify(string file, List<string> romFiles, ref int skippedNdz)
    {
        string ext = Path.GetExtension(file);
        if (RomExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            romFiles.Add(file);
        else if (ext.Equals(".ndz", StringComparison.OrdinalIgnoreCase))
            skippedNdz++;
    }

    private sealed record DecodedRom(string Path, byte[] Rgba, string ShortTitle, string FullTitle, string GameCode, byte UnitCode, byte RomVersion, ushort BannerVersion, long FileSizeBytes);

    private static (DecodedRom? Decoded, string? Error) TryDecode(string path)
    {
        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Couldn't read \"{Path.GetFileName(path)}\": {ex.Message}");
        }

        NdsRomInfo info;
        try
        {
            info = NdsRomInfo.FromRom(rom);
        }
        catch (InvalidDataException ex)
        {
            return (null, $"\"{Path.GetFileName(path)}\" doesn't look like a valid ROM: {ex.Message}");
        }

        try
        {
            byte[] rgba = NdsIcon.DecodeBitmap(info.Banner);
            var decoded = new DecodedRom(
                path,
                rgba,
                NdsIcon.DecodeShortTitle(info.Banner),
                NdsIcon.DecodeTitle(info.Banner),
                DecodeGameCode(info.GameCode),
                info.UnitCode,
                info.RomVersion,
                info.BannerVersion,
                rom.LongLength);
            return (decoded, null);
        }
        catch (Exception ex)
        {
            // Genuinely unexpected at this point (FromRom already validated the header
            // and banner region) - surfaced rather than left to crash the app, since this
            // is meant to be pointed at arbitrary real-world ROM dumps.
            return (null, $"Decoded the header but failed on the icon/banner for \"{Path.GetFileName(path)}\": {ex.Message}");
        }
    }

    private RomEntryViewModel ToViewModel(DecodedRom decoded) => new(
        filePath: decoded.Path,
        icon: ToBitmap(decoded.Rgba),
        shortTitle: decoded.ShortTitle,
        fullTitle: decoded.FullTitle,
        gameCode: decoded.GameCode,
        unitCode: decoded.UnitCode,
        romVersion: decoded.RomVersion,
        bannerVersion: decoded.BannerVersion,
        fileSizeBytes: decoded.FileSizeBytes,
        onRemove: RemoveItem);

    private void RemoveItem(RomEntryViewModel item)
    {
        QueueItems.Remove(item);
        RecomputeDuplicates();
    }

    /// <summary>
    /// Recomputed fully after every add/remove rather than incrementally - the queue is
    /// small (a handful to a few hundred ROMs at most), so an O(n) full-list sweep beats
    /// tracking a removal-safe running count per key for no real benefit. Keyed on
    /// GameCode + RomVersion together, not GameCode alone - a re-release (e.g. a Wii U
    /// Virtual Console dump) can share a game code with the original cartridge while
    /// carrying a different RomVersion, and is genuinely different content, not a
    /// duplicate.
    /// </summary>
    private void RecomputeDuplicates()
    {
        var seen = new HashSet<(string GameCode, byte RomVersion)>();
        foreach (RomEntryViewModel item in QueueItems)
            item.IsDuplicate = !seen.Add((item.GameCode, item.RomVersion));
    }

    private void SetError(string? message)
    {
        ErrorMessage = message;
        HasError = !string.IsNullOrEmpty(message);
    }

    private static string DecodeGameCode(uint gameCode)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, gameCode);
        return Encoding.ASCII.GetString(bytes);
    }

    private static WriteableBitmap ToBitmap(byte[] rgba)
    {
        var size = new PixelSize(NdsIcon.Width, NdsIcon.Height);
        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Unpremul);

        using ILockedFramebuffer buffer = bitmap.Lock();
        for (int y = 0; y < NdsIcon.Height; y++)
        {
            int srcOffset = y * NdsIcon.Width * 4;
            nint dstRow = buffer.Address + y * buffer.RowBytes;
            Marshal.Copy(rgba, srcOffset, dstRow, NdsIcon.Width * 4);
        }

        return bitmap;
    }
}
