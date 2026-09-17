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
    /// Accepts a mix of ROM/.ndz file paths and directory paths (from a drop or a picker) -
    /// directories are walked recursively. A raw .nds/.dsi becomes a normal card; a .ndz
    /// becomes a "packed base" card (<see cref="RomEntryViewModel.IsPackedBase"/>) - not
    /// something to pack again (it already is), but something a hack target can attach to
    /// with zero extra configuration, since it already carries both its own packed bytes
    /// and (via decompression) the raw ROM bytes a hack's matching needs - see
    /// <see cref="HackTargetViewModel"/>'s own remarks on why that's a real improvement
    /// over needing a separate build/browse step per hack target. Runs the filesystem walk
    /// and each file's decode off the UI thread so pointing this at a large library folder
    /// doesn't freeze the window; only the final queue-collection mutations happen back on
    /// the UI thread (the default continuation context an `await` resumes on).
    /// </summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        List<DecodedRom> decoded = await DecodeAllAsync(paths, allowNdz: true);
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
    /// topology, not a tree). Unlike <see cref="AddPathsAsync"/>, a .ndz is never accepted
    /// here - <see cref="NdzPairWriter"/> needs the base's own raw ROM bytes, and a pair
    /// target specifically merges into ONE shared output file, a different shape from a
    /// hack target's own always-standalone output (see <see cref="AddHackTargetsAsync"/>).
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

        List<DecodedRom> decoded = await DecodeAllAsync(paths, allowNdz: false);
        foreach (DecodedRom rom in decoded)
            baseItem.Targets.Add(ToViewModel(rom, target => RemoveTarget(baseItem, target)));

        if (decoded.Count > 0)
            RecomputeDuplicates();
    }

    /// <summary>Single-file convenience wrapper over <see cref="AddPathsAsync"/>, kept for the existing Open-ROM/drop-a-file call sites.</summary>
    public Task AddRomAsync(string path) => AddPathsAsync([path]);

    private static readonly string[] HackTargetExtensions = [".nds", ".dsi", ".xdelta"];

    /// <summary>
    /// Adds each path as one of <paramref name="baseItem"/>'s <see cref="RomEntryViewModel.HackTargets"/>
    /// jobs - a genuinely different relationship from <see cref="AddTargetsAsync"/>'s pair
    /// targets (see <see cref="HackTargetViewModel"/>'s own remarks), reached via a
    /// dedicated "+ hack" control rather than the plain "+" strip, since a hack target can
    /// be either a ROM or an .xdelta patch and there's no ambiguity to resolve here - unlike
    /// a plain drop on the card, which always means a pair target. No directory/archive
    /// expansion (unlike <see cref="AddPathsAsync"/>) - this is a deliberate one-off pick,
    /// not a bulk library import. Never gated by <see cref="PairContainerPolicy.CreationEnabled"/> -
    /// that gate is specifically about the pair-container format's own unresolved
    /// multi-dictionary design, which has nothing to do with hack containers.
    /// </summary>
    public async Task AddHackTargetsAsync(RomEntryViewModel baseItem, IEnumerable<string> paths)
    {
        SetError(null);
        List<string> validPaths = paths.Where(p => File.Exists(p) && HackTargetExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToList();

        string? lastError = null;
        foreach (string path in validPaths)
        {
            (HackTargetViewModel? item, string? error) = await Task.Run(() => TryDecodeHackTarget(baseItem, path));
            if (item is not null)
                baseItem.HackTargets.Add(item);
            else
                lastError = error;
        }

        if (lastError is not null)
            SetError(lastError);
        else if (validPaths.Count == 0)
            SetError("Drop a .nds/.dsi ROM or a .xdelta patch to add a hack target.");
    }

    private (HackTargetViewModel? Item, string? Error) TryDecodeHackTarget(RomEntryViewModel baseItem, string path)
    {
        RomSource source = RomSource.ForFile(path);
        bool isPatch = source.Extension.Equals(".xdelta", StringComparison.OrdinalIgnoreCase);
        long size;

        try
        {
            size = new FileInfo(path).Length;
            if (isPatch)
            {
                // No ROM to decode an icon/title from without applying the patch first
                // (see HackTargetViewModel's own remarks on why that's deferred to pack
                // time rather than done here just for a preview).
                string title = Path.GetFileNameWithoutExtension(path);
                var item = new HackTargetViewModel(source, HackTargetKind.XdeltaPatch,
                    icon: null, shortTitle: title, gameCode: string.Empty, fileSizeBytes: size,
                    onRemove: t => baseItem.HackTargets.Remove(t));
                return (item, null);
            }

            byte[] rom = source.ReadBytes();
            NdsRomInfo info = NdsRomInfo.FromRom(rom);
            byte[] rgba = NdsIcon.DecodeBitmap(info.Banner);
            var romItem = new HackTargetViewModel(source, HackTargetKind.DirectRom,
                ToBitmap(rgba), NdsIcon.DecodeShortTitle(info.Banner), DecodeGameCode(info.GameCode), size,
                onRemove: t => baseItem.HackTargets.Remove(t));
            return (romItem, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, $"Couldn't read \"{source.ShortLabel}\": {ex.Message}");
        }
    }

    /// <summary>
    /// Shared expand-and-decode pipeline for both <see cref="AddPathsAsync"/> and
    /// <see cref="AddTargetsAsync"/> - only what happens to a successfully decoded ROM
    /// afterward (new top-level card vs. a target on an existing one) differs between
    /// them. <paramref name="allowNdz"/> distinguishes the two (see their own remarks).
    /// Sets <see cref="ErrorMessage"/> itself; callers only need to place the results.
    /// </summary>
    private async Task<List<DecodedRom>> DecodeAllAsync(IEnumerable<string> paths, bool allowNdz)
    {
        SetError(null);

        List<RomSource> files = await Task.Run(() => ExpandPaths(paths, allowNdz));

        var decoded = new List<DecodedRom>();
        string? lastError = null;
        foreach (RomSource source in files)
        {
            (DecodedRom? rom, string? error) = await Task.Run(() => TryDecode(source));
            if (rom is not null)
                decoded.Add(rom);
            else
                lastError = error;
        }

        if (lastError is not null)
            SetError(lastError);
        else if (files.Count == 0)
        {
            SetError(allowNdz
                ? "No .nds/.dsi ROMs or .ndz files found in what was dropped."
                : "No .nds or .dsi ROMs found in what was dropped.");
        }

        return decoded;
    }

    private static List<RomSource> ExpandPaths(IEnumerable<string> paths, bool allowNdz)
    {
        var files = new List<RomSource>();
        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                string full = Path.GetFullPath(path);
                if (!visitedDirs.Add(full))
                    continue;

                IEnumerable<string> found;
                try
                {
                    found = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable subfolder (permissions, a broken junction, ...)
                    // shouldn't abort the whole scan - just contributes nothing.
                    continue;
                }

                foreach (string file in found)
                    Classify(file, files, allowNdz);
            }
            else if (File.Exists(path))
            {
                Classify(path, files, allowNdz);
            }
        }

        return files;
    }

    private static void Classify(string file, List<RomSource> files, bool allowNdz)
    {
        string ext = Path.GetExtension(file);
        if (RomExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(RomSource.ForFile(file));
        }
        else if (allowNdz && ext.Equals(".ndz", StringComparison.OrdinalIgnoreCase))
        {
            // A pair-target add (allowNdz: false) silently ignores a .ndz, same as any
            // other unsupported extension - NdzPairWriter needs raw ROM bytes, and a
            // pair target's whole point is merging into ONE shared output file, which
            // doesn't apply to something already packed.
            files.Add(RomSource.ForFile(file));
        }
        else if (ArchiveExtractor.IsSupportedArchive(file))
        {
            // A real ROM collection often has each game as its own separate archive
            // file - list what it holds (cheap, no decompression yet) and add a
            // RomSource pointer per match, which only actually reads (and
            // decompresses) that one entry once something needs its bytes - no
            // extraction to disk at any point. Only .nds/.dsi are looked for here - a
            // packed base is meant to be a real file the user can also point Examine
            // or the CLI at directly, so pulling one out of an archive here felt like
            // more complexity than the convenience is worth.
            try
            {
                foreach (string key in ArchiveExtractor.ListMatchingEntryKeys(file, RomExtensions))
                    files.Add(RomSource.ForArchiveEntry(file, key));
            }
            catch (Exception)
            {
                // Corrupt, encrypted, or otherwise unreadable archive - contributes
                // nothing, same as an unreadable folder elsewhere in this method's own
                // caller.
            }
        }
    }

    private sealed record DecodedRom(RomSource Source, byte[] Rgba, string ShortTitle, string FullTitle, string GameCode,
        byte UnitCode, string DestinationLabel, string RegionLockLabel, byte RomVersion, ushort BannerVersion,
        long FileSizeBytes, bool IsPackedBase);

    private static (DecodedRom? Decoded, string? Error) TryDecode(RomSource source)
    {
        bool isNdz = source.Extension.Equals(".ndz", StringComparison.OrdinalIgnoreCase);
        return isNdz ? TryDecodePackedBase(source) : TryDecodeRawRom(source);
    }

    /// <summary>
    /// A .ndz added to the Pack tab becomes a "packed base" card - not something to pack
    /// again (it already is), but a base a hack target can attach to with zero extra
    /// configuration (see <see cref="RomEntryViewModel.IsPackedBase"/>'s remarks). Must be
    /// a plain, self-contained .ndz: a pair container or an already base-patched/hack
    /// .ndz can't serve as a hack's base without a base of its own first, so both are
    /// rejected here with a clear reason rather than failing confusingly later at pack time.
    /// </summary>
    private static (DecodedRom? Decoded, string? Error) TryDecodePackedBase(RomSource source)
    {
        byte[] bytes;
        try
        {
            bytes = source.ReadBytes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, $"Couldn't read \"{source.ShortLabel}\": {ex.Message}");
        }

        if (NdzPairContainer.TryRead(bytes, out _))
            return (null, $"\"{source.ShortLabel}\" is a pair container, not a plain base - point at a single self-contained .ndz instead.");

        NdzFrontMatter frontMatter;
        try
        {
            (frontMatter, _) = NdzArchive.ReadInfo(bytes);
        }
        catch (InvalidDataException ex)
        {
            return (null, $"\"{source.ShortLabel}\" doesn't look like a valid .ndz: {ex.Message}");
        }

        if (frontMatter.Flags.HasFlag(NdzFlags.BasePatch))
            return (null, $"\"{source.ShortLabel}\" needs its own base to decode - point at a plain, self-contained .ndz instead.");

        try
        {
            byte[] rgba = NdsIcon.DecodeBitmap(frontMatter.Banner);
            var decoded = new DecodedRom(
                source, rgba,
                NdsIcon.DecodeShortTitle(frontMatter.Banner),
                NdsIcon.DecodeTitle(frontMatter.Banner),
                DecodeGameCode(frontMatter.GameCode),
                UnitCode: 0, DestinationLabel: string.Empty, RegionLockLabel: string.Empty, RomVersion: 0, BannerVersion: 0,
                FileSizeBytes: bytes.LongLength, IsPackedBase: true);
            return (decoded, null);
        }
        catch (Exception ex)
        {
            return (null, $"Decoded the header but failed on the icon/banner for \"{source.ShortLabel}\": {ex.Message}");
        }
    }

    private static (DecodedRom? Decoded, string? Error) TryDecodeRawRom(RomSource source)
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
                rom.LongLength,
                IsPackedBase: false);
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
        isPackedBase: decoded.IsPackedBase,
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
