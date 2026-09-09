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
using Ndz.Core.Compression;
using Ndz.Core.Format;
using Ndz.Gui.Models;

namespace Ndz.Gui.ViewModels;

/// <summary>
/// The Examine queue: drop or open any number of .ndz files (single blobs, or pair/N-way
/// containers - <see cref="NdzPairContainer"/>) as well as raw .nds/.dsi ROMs, and see them
/// grouped by source file with per-entry icon/title/id, size/ratio, and flags. Each packed
/// entry can be unpacked back to a real .nds independently - a pair-container entry
/// resolves its own base internally, a standalone base-patched blob (something only the
/// CLI, not this GUI's own Pack view, can produce) prompts for its base ROM first.
/// </summary>
public partial class ExamineViewModel : ViewModelBase
{
    private static readonly string[] AcceptedExtensions = [".ndz", ".nds", ".dsi"];

    public ObservableCollection<ExamineSourceViewModel> Sources { get; } = [];

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasError;

    public bool HasItems => Sources.Count > 0;

    public ExamineViewModel()
    {
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>
    /// Accepts a mix of file and directory paths (from a drop or a picker) - directories
    /// are walked recursively for .ndz/.nds/.dsi files. Runs the filesystem walk and each
    /// file's decode off the UI thread; only bitmap creation and the final
    /// Sources-collection mutation happen back on the UI thread (the default continuation
    /// context an `await` resumes on) - matches <see cref="MainWindowViewModel.AddPathsAsync"/>'s
    /// own split.
    /// </summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        SetError(null);

        List<string> files = await Task.Run(() => ExpandPaths(paths));

        string? lastError = null;
        foreach (string path in files)
        {
            (DecodedSource? decoded, string? error) = await Task.Run(() => TryDecodeSource(path));
            if (decoded is not null)
                Sources.Add(ToViewModel(decoded));
            else
                lastError = error;
        }

        if (lastError is not null)
            SetError(lastError);
        else if (files.Count == 0)
            SetError("No .ndz/.nds/.dsi files found in what was dropped.");
    }

    public void RemoveSource(ExamineSourceViewModel source) => Sources.Remove(source);

    private static List<string> ExpandPaths(IEnumerable<string> paths)
    {
        var files = new List<string>();
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
                    continue;
                }

                foreach (string file in found)
                {
                    if (AcceptedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        files.Add(file);
                }
            }
            else if (File.Exists(path) && AcceptedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                files.Add(path);
            }
        }

        return files;
    }

    private sealed record DecodedEntry(byte[] Rgba, string ShortTitle, string FullTitle, string GameCode,
        string SummaryText, string TooltipText, bool CanUnpack, bool RequiresExternalBaseRom, string SuggestedFileName,
        Func<byte[]?, byte[]> GetRomBytes);

    private sealed record DecodedSource(string Path, ExamineSourceKind Kind, string HeaderText, List<DecodedEntry> Entries);

    private static (DecodedSource? Decoded, string? Error) TryDecodeSource(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Couldn't read \"{Path.GetFileName(path)}\": {ex.Message}");
        }

        try
        {
            bool isNdz = Path.GetExtension(path).Equals(".ndz", StringComparison.OrdinalIgnoreCase);
            DecodedSource decoded = isNdz ? DecodeNdz(path, bytes) : DecodeRawRom(path, bytes);
            return (decoded, null);
        }
        catch (Exception ex)
        {
            return (null, $"\"{Path.GetFileName(path)}\" couldn't be examined: {ex.Message}");
        }
    }

    private static DecodedSource DecodeNdz(string path, byte[] bytes) =>
        NdzPairContainer.TryRead(bytes, out NdzPairContainer? pair)
            ? DecodePairContainer(path, pair!)
            : DecodeSingleNdz(path, bytes);

    private static DecodedSource DecodePairContainer(string path, NdzPairContainer pair)
    {
        // Decode the self-contained entry's title first - every base-patched entry's own
        // summary references it by name instead of a raw hex game code.
        (NdzFrontMatter baseFrontMatter, _) = pair.ReadEntryInfo(pair.PlainEntryIndex);
        string baseTitle = NdsIcon.DecodeShortTitle(baseFrontMatter.Banner);

        var entries = new List<DecodedEntry>();
        long totalOriginal = 0;
        for (int i = 0; i < pair.Entries.Count; i++)
        {
            NdzPairEntry entry = pair.Entries[i];
            (NdzFrontMatter frontMatter, _) = pair.ReadEntryInfo(i);
            bool isBase = i == pair.PlainEntryIndex;
            int index = i;

            entries.Add(BuildDecodedEntry(
                frontMatter, entry.OriginalSize, entry.Size,
                requiresExternalBaseRom: false,
                baseLabel: isBase ? null : baseTitle,
                getRomBytes: _ => pair.DecompressEntry(index)));
            totalOriginal += entry.OriginalSize;
        }

        string header = $"Pair container · {pair.Entries.Count} ROMs, {SizeOption.FromBytes((int)Math.Min(totalOriginal, int.MaxValue)).Label} total";
        return new DecodedSource(path, ExamineSourceKind.NdzPair, header, entries);
    }

    private static DecodedSource DecodeSingleNdz(string path, byte[] bytes)
    {
        (NdzFrontMatter frontMatter, _) = NdzArchive.ReadInfo(bytes);
        bool needsBase = frontMatter.Flags.HasFlag(NdzFlags.BasePatch);
        string? baseLabel = needsBase ? DecodeGameCode(frontMatter.BaseGameCode) : null;

        DecodedEntry entry = BuildDecodedEntry(
            frontMatter, frontMatter.OriginalSize, (uint)bytes.LongLength,
            requiresExternalBaseRom: needsBase,
            baseLabel: baseLabel,
            getRomBytes: externalBase =>
            {
                if (needsBase && externalBase is null)
                    throw new InvalidOperationException("This .ndz needs its base ROM to unpack - pick it first.");
                using NdzArchive archive = NdzArchive.Open(bytes, externalBase);
                return archive.DecompressAll();
            });

        string header = needsBase ? "Single .ndz · base-patched, needs its base ROM to unpack" : "Single .ndz";
        return new DecodedSource(path, ExamineSourceKind.NdzSingle, header, [entry]);
    }

    private static DecodedEntry BuildDecodedEntry(NdzFrontMatter frontMatter, uint originalSize, uint storedSize,
        bool requiresExternalBaseRom, string? baseLabel, Func<byte[]?, byte[]> getRomBytes)
    {
        byte[] rgba = NdsIcon.DecodeBitmap(frontMatter.Banner);
        string shortTitle = NdsIcon.DecodeShortTitle(frontMatter.Banner);
        string fullTitle = NdsIcon.DecodeTitle(frontMatter.Banner);
        string gameCode = DecodeGameCode(frontMatter.GameCode);

        double ratio = storedSize == 0 ? 0 : (double)originalSize / storedSize;
        var flags = new List<string>();
        if (frontMatter.Flags.HasFlag(NdzFlags.BasePatch)) flags.Add("base-patched");
        if (frontMatter.HasDictionary) flags.Add("dictionary");
        if (frontMatter.Flags.HasFlag(NdzFlags.Filters)) flags.Add("filters");
        string blockSizeLabel = SizeOption.FromBytes(frontMatter.Flags.GetBlockSize()).Label;

        string summary = $"{SizeOption.FromBytes((int)Math.Min(originalSize, int.MaxValue)).Label} → " +
            $"{SizeOption.FromBytes((int)Math.Min(storedSize, int.MaxValue)).Label} ({ratio:0.#}x) · {blockSizeLabel} block" +
            (flags.Count > 0 ? " · " + string.Join(", ", flags) : "");
        if (baseLabel is not null)
            summary += $" · base: {baseLabel}";

        var tooltipLines = new List<string>();
        if (!string.IsNullOrEmpty(fullTitle))
            tooltipLines.Add(fullTitle);
        tooltipLines.Add($"{gameCode} · {summary}");
        string tooltip = string.Join("\n\n", tooltipLines);

        string suggestedFileName = SanitizeFileName(shortTitle) + ".nds";

        return new DecodedEntry(rgba, shortTitle, fullTitle, gameCode, summary, tooltip, CanUnpack: true, requiresExternalBaseRom, suggestedFileName, getRomBytes);
    }

    private static DecodedSource DecodeRawRom(string path, byte[] rom)
    {
        NdsRomInfo info = NdsRomInfo.FromRom(rom);
        byte[] rgba = NdsIcon.DecodeBitmap(info.Banner);
        string shortTitle = NdsIcon.DecodeShortTitle(info.Banner);
        string fullTitle = NdsIcon.DecodeTitle(info.Banner);
        string gameCode = DecodeGameCode(info.GameCode);

        string platform = info.UnitCode switch
        {
            0x00 => "NDS",
            0x02 => "NDS + DSi",
            0x03 => "DSi-exclusive",
            _ => $"Unknown platform (0x{info.UnitCode:X2})",
        };
        string revision = info.RomVersion != 0 ? $" · Rev {info.RomVersion}" : "";
        string region = string.IsNullOrEmpty(info.RegionLockLabel) ? "" : $" · Region-locked: {info.RegionLockLabel}";
        string summary = $"{SizeOption.FromBytes((int)Math.Min(rom.LongLength, int.MaxValue)).Label} · {platform} · {info.DestinationLabel}{revision}{region}";

        var tooltipLines = new List<string>();
        if (!string.IsNullOrEmpty(fullTitle))
            tooltipLines.Add(fullTitle);
        tooltipLines.Add($"{gameCode} · {summary}");
        tooltipLines.Add(path);
        string tooltip = string.Join("\n\n", tooltipLines);

        // A raw ROM's "decompress" is trivial - the bytes already read from disk, verbatim -
        // but it still goes through the same getRomBytes shape so checksumming works
        // uniformly with a packed entry (see ExamineEntryViewModel's own remarks).
        var entry = new DecodedEntry(rgba, shortTitle, fullTitle, gameCode, summary, tooltip, CanUnpack: false, RequiresExternalBaseRom: false, SuggestedFileName: string.Empty, GetRomBytes: _ => rom);
        return new DecodedSource(path, ExamineSourceKind.RawRom, "Raw ROM · already unpacked", [entry]);
    }

    private static ExamineSourceViewModel ToViewModel(DecodedSource decoded)
    {
        var entries = new ObservableCollection<ExamineEntryViewModel>(decoded.Entries.Select(ToViewModel));
        return new ExamineSourceViewModel(decoded.Path, decoded.Kind, decoded.HeaderText, entries);
    }

    private static ExamineEntryViewModel ToViewModel(DecodedEntry decoded)
    {
        Bitmap icon = ToBitmap(decoded.Rgba);
        return decoded.CanUnpack
            ? ExamineEntryViewModel.ForPackedEntry(icon, decoded.ShortTitle, decoded.FullTitle, decoded.GameCode, decoded.SummaryText, decoded.TooltipText,
                decoded.RequiresExternalBaseRom, decoded.SuggestedFileName, decoded.GetRomBytes)
            : ExamineEntryViewModel.ForRawRom(icon, decoded.ShortTitle, decoded.FullTitle, decoded.GameCode, decoded.SummaryText, decoded.TooltipText, decoded.GetRomBytes);
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

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Where(c => !invalid.Contains(c)).ToArray());
        cleaned = cleaned.Trim();
        return cleaned.Length > 0 ? cleaned : "output";
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
