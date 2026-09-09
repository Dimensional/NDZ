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
using CommunityToolkit.Mvvm.Input;
using Ndz.Core.Archives;
using Ndz.Core.Compression;
using Ndz.Core.Format;

namespace Ndz.Gui.ViewModels;

/// <summary>
/// The Pack queue: drop or open any number of real .nds/.dsi ROMs - individually, whole
/// directory trees, or inside a .zip/.7z/.rar archive (each entry read straight from the
/// archive's own decompression stream via <see cref="RomSource"/>/<see cref="ArchiveExtractor"/>
/// - no extraction to disk - see <see cref="Classify"/>) - and see them appear as decoded
/// cards (icon/title/id). Dropping a fresh file directly onto an existing card's "+" strip
/// (<see cref="AddTargetsAsync"/>) adds it as that card's patch target instead of a new
/// top-level card.
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

    /// <summary>The Examine tab's own queue - a sibling feature to this class's own Pack queue, not a child of it.</summary>
    public ExamineViewModel Examine { get; } = new();

    /// <summary>True when the Examine tab is showing instead of Pack - the only two screens this window has.</summary>
    [ObservableProperty]
    private bool _showExamine;

    public MainWindowViewModel()
    {
        QueueItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    [RelayCommand]
    private void ShowPackTab() => ShowExamine = false;

    [RelayCommand]
    private void ShowExamineTab() => ShowExamine = true;

    /// <summary>
    /// Accepts a mix of ROM file paths and directory paths (from a drop or a picker) -
    /// directories are walked recursively for .nds/.dsi files. Runs the filesystem walk
    /// and each ROM's decode off the UI thread so pointing this at a large library folder
    /// doesn't freeze the window; only the final queue-collection mutations happen back on
    /// the UI thread (the default continuation context an `await` resumes on).
    /// </summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        List<DecodedRom> decoded = await DecodeAllAsync(paths);
        foreach (DecodedRom rom in decoded)
            QueueItems.Add(ToViewModel(rom, RemoveItem));

        if (decoded.Count > 0)
            RecomputeDuplicates();
    }

    /// <summary>
    /// Adds each successfully decoded ROM as a patch target of <paramref name="baseItem"/>
    /// rather than as a new top-level queue card - what dropping a fresh file onto a
    /// card's "+" strip does. <paramref name="baseItem"/> is always already a top-level
    /// queue item; targets are never themselves droppable-onto (see
    /// <see cref="RomEntryViewModel"/>'s class remarks - the format is a flat star
    /// topology, not a tree).
    /// </summary>
    public async Task AddTargetsAsync(RomEntryViewModel baseItem, IEnumerable<string> paths)
    {
        // Defense in depth: the UI itself hides the "+" strip that's the only normal way
        // to reach this (see RomEntryViewModel.PairPackingEnabled), but this is the real
        // gate - nothing reaches NdzPairWriter through this class while it's off.
        if (!PairContainerPolicy.CreationEnabled)
        {
            SetError(PairContainerPolicy.DisabledMessage);
            return;
        }

        List<DecodedRom> decoded = await DecodeAllAsync(paths);
        foreach (DecodedRom rom in decoded)
            baseItem.Targets.Add(ToViewModel(rom, target => RemoveTarget(baseItem, target)));

        if (decoded.Count > 0)
            RecomputeDuplicates();
    }

    /// <summary>Single-file convenience wrapper over <see cref="AddPathsAsync"/>, kept for the existing Open-ROM/drop-a-file call sites.</summary>
    public Task AddRomAsync(string path) => AddPathsAsync([path]);

    /// <summary>
    /// Shared expand-and-decode pipeline for both <see cref="AddPathsAsync"/> and
    /// <see cref="AddTargetsAsync"/> - only what happens to a successfully decoded ROM
    /// afterward (new top-level card vs. a target on an existing one) differs between
    /// them. Sets <see cref="ErrorMessage"/> itself; callers only need to place the
    /// results.
    /// </summary>
    private async Task<List<DecodedRom>> DecodeAllAsync(IEnumerable<string> paths)
    {
        SetError(null);

        (List<RomSource> romFiles, int skippedNdzCount) = await Task.Run(() => ExpandPaths(paths));

        var decoded = new List<DecodedRom>();
        string? lastError = null;
        foreach (RomSource source in romFiles)
        {
            (DecodedRom? rom, string? error) = await Task.Run(() => TryDecode(source));
            if (rom is not null)
                decoded.Add(rom);
            else
                lastError = error;
        }

        if (lastError is not null)
            SetError(lastError);
        else if (skippedNdzCount > 0)
            SetError($"Skipped {skippedNdzCount} .ndz file{(skippedNdzCount == 1 ? "" : "s")} - already-packed containers belong in Examine, not Pack.");
        else if (romFiles.Count == 0)
            SetError("No .nds or .dsi ROMs found in what was dropped.");

        return decoded;
    }

    private static (List<RomSource> RomFiles, int SkippedNdzCount) ExpandPaths(IEnumerable<string> paths)
    {
        var romFiles = new List<RomSource>();
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

    private static void Classify(string file, List<RomSource> romFiles, ref int skippedNdz)
    {
        string ext = Path.GetExtension(file);
        if (RomExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            romFiles.Add(RomSource.ForFile(file));
        }
        else if (ext.Equals(".ndz", StringComparison.OrdinalIgnoreCase))
        {
            skippedNdz++;
        }
        else if (ArchiveExtractor.IsSupportedArchive(file))
        {
            // A real ROM collection often has each game as its own separate archive
            // file - list what it holds (cheap, no decompression yet) and add a
            // RomSource pointer per match, which only actually reads (and
            // decompresses) that one entry once something needs its bytes - no
            // extraction to disk at any point. Only .nds/.dsi are looked for here - an
            // .ndz inside an archive isn't something Pack wants either, matching a
            // loose one dropped directly (though it isn't counted in skippedNdz, unlike
            // a loose file - listing a second time just to find those felt like more
            // archive-opening cost than the message is worth).
            try
            {
                foreach (string key in ArchiveExtractor.ListMatchingEntryKeys(file, RomExtensions))
                    romFiles.Add(RomSource.ForArchiveEntry(file, key));
            }
            catch (Exception)
            {
                // Corrupt, encrypted, or otherwise unreadable archive - contributes
                // nothing, same as an unreadable folder elsewhere in this method's own
                // caller.
            }
        }
    }

    private sealed record DecodedRom(RomSource Source, byte[] Rgba, string ShortTitle, string FullTitle, string GameCode, byte UnitCode, string DestinationLabel, string RegionLockLabel, byte RomVersion, ushort BannerVersion, long FileSizeBytes);

    private static (DecodedRom? Decoded, string? Error) TryDecode(RomSource source)
    {
        byte[] rom;
        try
        {
            rom = source.ReadBytes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, $"Couldn't read \"{source.ShortLabel}\": {ex.Message}");
        }

        NdsRomInfo info;
        try
        {
            info = NdsRomInfo.FromRom(rom);
        }
        catch (InvalidDataException ex)
        {
            return (null, $"\"{source.ShortLabel}\" doesn't look like a valid ROM: {ex.Message}");
        }

        try
        {
            byte[] rgba = NdsIcon.DecodeBitmap(info.Banner);
            var decoded = new DecodedRom(
                source,
                rgba,
                NdsIcon.DecodeShortTitle(info.Banner),
                NdsIcon.DecodeTitle(info.Banner),
                DecodeGameCode(info.GameCode),
                info.UnitCode,
                info.DestinationLabel,
                info.RegionLockLabel,
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
            return (null, $"Decoded the header but failed on the icon/banner for \"{source.ShortLabel}\": {ex.Message}");
        }
    }

    private RomEntryViewModel ToViewModel(DecodedRom decoded, Action<RomEntryViewModel> onRemove) => new(
        source: decoded.Source,
        icon: ToBitmap(decoded.Rgba),
        shortTitle: decoded.ShortTitle,
        fullTitle: decoded.FullTitle,
        gameCode: decoded.GameCode,
        unitCode: decoded.UnitCode,
        destinationLabel: decoded.DestinationLabel,
        regionLockLabel: decoded.RegionLockLabel,
        romVersion: decoded.RomVersion,
        bannerVersion: decoded.BannerVersion,
        fileSizeBytes: decoded.FileSizeBytes,
        onRemove: onRemove);

    private void RemoveItem(RomEntryViewModel item)
    {
        QueueItems.Remove(item);
        RecomputeDuplicates();
    }

    private void RemoveTarget(RomEntryViewModel baseItem, RomEntryViewModel target)
    {
        baseItem.Targets.Remove(target);
        RecomputeDuplicates();
    }

    /// <summary>
    /// Recomputed fully after every add/remove rather than incrementally - the queue is
    /// small (a handful to a few hundred ROMs at most), so an O(n) full-list sweep beats
    /// tracking a removal-safe running count per key for no real benefit. Keyed on
    /// GameCode + RomVersion together, not GameCode alone - a re-release (e.g. a Wii U
    /// Virtual Console dump) can share a game code with the original cartridge while
    /// carrying a different RomVersion, and is genuinely different content, not a
    /// duplicate. Sweeps every top-level card AND every target nested under it - a target
    /// dropped onto the wrong base's strip by mistake (including a self-pairing, the same
    /// ROM dropped onto its own base) is flagged exactly the same way as a top-level
    /// collision.
    /// </summary>
    private void RecomputeDuplicates()
    {
        var seen = new HashSet<(string GameCode, byte RomVersion)>();
        foreach (RomEntryViewModel item in QueueItems)
        {
            item.IsDuplicate = !seen.Add((item.GameCode, item.RomVersion));
            foreach (RomEntryViewModel target in item.Targets)
                target.IsDuplicate = !seen.Add((target.GameCode, target.RomVersion));
        }
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
