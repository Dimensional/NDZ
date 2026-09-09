using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ndz.Core.Format;
using Ndz.Gui.Models;

namespace Ndz.Gui.ViewModels;

/// <summary>
/// One examinable ROM: either a raw .nds/.dsi file (already unpacked, nothing to unpack -
/// but still checksummable) or a single packed entry inside a .ndz - which itself might be
/// a standalone blob or one entry of an <see cref="ExamineSourceViewModel"/>'s pair
/// container. Built via <see cref="ForRawRom"/> or <see cref="ForPackedEntry"/> rather than
/// a public constructor, since which fields are meaningful (ratio/flags/unpack vs.
/// platform/region/revision) differs by which factory made it - both supply a
/// <c>getRomBytes</c> delegate, though, so checksumming works uniformly either way.
/// </summary>
public partial class ExamineEntryViewModel : ViewModelBase
{
    public Bitmap Icon { get; }
    public string ShortTitle { get; }
    public string FullTitle { get; }
    public string GameCode { get; }

    /// <summary>One-line summary: size+ratio+flags for a packed entry, platform+region for a raw ROM.</summary>
    public string SummaryText { get; }

    public string TooltipText { get; }

    /// <summary>False for a raw .nds/.dsi - there's nothing to unpack, it already is one.</summary>
    public bool CanUnpack { get; }

    /// <summary>
    /// True only for a standalone base-patched .ndz that isn't part of a pair container
    /// (the pair-container case resolves its own base internally - see
    /// <see cref="Compression.NdzPairContainer.OpenEntry"/>) - the CLI can produce this
    /// shape even though the GUI's own Pack view never does. The view has to prompt for a
    /// base ROM file before <see cref="UnpackAsync"/> or <see cref="ComputeChecksumsAsync"/>
    /// can succeed.
    /// </summary>
    public bool RequiresExternalBaseRom { get; }

    /// <summary>Suggested output file name (without directory) for the unpack save dialog.</summary>
    public string SuggestedFileName { get; }

    /// <summary>
    /// Gets this entry's decompressed ROM bytes - a raw ROM's own bytes for
    /// <see cref="ForRawRom"/>, or a real decompress for <see cref="ForPackedEntry"/>
    /// (<paramref name="externalBaseRom"/> only matters, and is required, when
    /// <see cref="RequiresExternalBaseRom"/> is true). Backs both
    /// <see cref="UnpackAsync"/> and <see cref="ComputeChecksumsAsync"/> - two different
    /// things to do with the same bytes.
    /// </summary>
    private readonly Func<byte[]?, byte[]> _getRomBytes;

    /// <summary>Cached after a successful <see cref="ComputeChecksumsAsync"/> so re-checking <see cref="ExpectedChecksumInput"/> against it doesn't need to re-decompress.</summary>
    private byte[]? _cachedRomBytes;

    [ObservableProperty]
    private bool _isUnpacking;

    public string UnpackButtonText => IsUnpacking ? "Unpacking…" : "Unpack";

    partial void OnIsUnpackingChanged(bool value) => OnPropertyChanged(nameof(UnpackButtonText));

    [ObservableProperty]
    private string? _unpackResultText;

    public bool HasUnpackResult => !string.IsNullOrEmpty(UnpackResultText);

    [ObservableProperty]
    private string? _unpackError;

    public bool HasUnpackError => !string.IsNullOrEmpty(UnpackError);

    partial void OnUnpackResultTextChanged(string? value) => OnPropertyChanged(nameof(HasUnpackResult));
    partial void OnUnpackErrorChanged(string? value) => OnPropertyChanged(nameof(HasUnpackError));

    [ObservableProperty]
    private bool _isComputingChecksums;

    public string ChecksumsButtonText => IsComputingChecksums ? "Computing…" : "Checksums";

    partial void OnIsComputingChecksumsChanged(bool value) => OnPropertyChanged(nameof(ChecksumsButtonText));

    [ObservableProperty]
    private string? _crc32Text;

    [ObservableProperty]
    private string? _md5Text;

    [ObservableProperty]
    private string? _sha1Text;

    [ObservableProperty]
    private string? _sha256Text;

    /// <summary>True once checksums have actually been computed - drives whether the four hashes and the verify box render at all.</summary>
    public bool HasChecksums => !string.IsNullOrEmpty(Crc32Text);

    partial void OnCrc32TextChanged(string? value) => OnPropertyChanged(nameof(HasChecksums));

    [ObservableProperty]
    private string? _checksumError;

    public bool HasChecksumError => !string.IsNullOrEmpty(ChecksumError);

    partial void OnChecksumErrorChanged(string? value) => OnPropertyChanged(nameof(HasChecksumError));

    /// <summary>What the user pasted in to check against - any of CRC32/MD5/SHA-1/SHA-256, auto-detected by length (see <see cref="RomChecksum.Matches"/>).</summary>
    [ObservableProperty]
    private string _expectedChecksumInput = string.Empty;

    /// <summary>Re-verifies live as the box is edited, once checksums are actually available - no separate "Verify" click needed.</summary>
    partial void OnExpectedChecksumInputChanged(string value)
    {
        if (_cachedRomBytes is not null)
            UpdateMatchResult();
    }

    [ObservableProperty]
    private string? _matchResultText;

    public bool HasMatchResult => !string.IsNullOrEmpty(MatchResultText);

    partial void OnMatchResultTextChanged(string? value) => OnPropertyChanged(nameof(HasMatchResult));

    [ObservableProperty]
    private bool _matchSucceeded;

    private ExamineEntryViewModel(Bitmap icon, string shortTitle, string fullTitle, string gameCode,
        string summaryText, string tooltipText, bool canUnpack, bool requiresExternalBaseRom,
        string suggestedFileName, Func<byte[]?, byte[]> getRomBytes)
    {
        Icon = icon;
        ShortTitle = shortTitle;
        FullTitle = fullTitle;
        GameCode = gameCode;
        SummaryText = summaryText;
        TooltipText = tooltipText;
        CanUnpack = canUnpack;
        RequiresExternalBaseRom = requiresExternalBaseRom;
        SuggestedFileName = suggestedFileName;
        _getRomBytes = getRomBytes;
    }

    public static ExamineEntryViewModel ForRawRom(Bitmap icon, string shortTitle, string fullTitle, string gameCode,
        string summaryText, string tooltipText, Func<byte[]?, byte[]> getRomBytes) =>
        new(icon, shortTitle, fullTitle, gameCode, summaryText, tooltipText, canUnpack: false, requiresExternalBaseRom: false, suggestedFileName: string.Empty, getRomBytes);

    public static ExamineEntryViewModel ForPackedEntry(Bitmap icon, string shortTitle, string fullTitle, string gameCode,
        string summaryText, string tooltipText, bool requiresExternalBaseRom, string suggestedFileName, Func<byte[]?, byte[]> getRomBytes) =>
        new(icon, shortTitle, fullTitle, gameCode, summaryText, tooltipText, canUnpack: true, requiresExternalBaseRom, suggestedFileName, getRomBytes);

    /// <summary>
    /// Decompresses this entry and writes it to <paramref name="outputPath"/>.
    /// <paramref name="externalBaseRom"/> is required (and used) only when
    /// <see cref="RequiresExternalBaseRom"/> is true - ignored otherwise (a pair-container
    /// entry always resolves its own base internally, and a self-contained entry needs no
    /// base at all).
    /// </summary>
    public async Task UnpackAsync(string outputPath, byte[]? externalBaseRom)
    {
        IsUnpacking = true;
        UnpackError = null;
        UnpackResultText = null;
        try
        {
            byte[] rom = await Task.Run(() => _getRomBytes(externalBaseRom));
            await Task.Run(() => File.WriteAllBytes(outputPath, rom));
            UnpackResultText = $"Unpacked \"{Path.GetFileName(outputPath)}\" ({SizeOption.FromBytes((int)Math.Min(rom.LongLength, int.MaxValue)).Label}).";
        }
        catch (Exception ex)
        {
            UnpackError = $"Unpack failed: {ex.Message}";
        }
        finally
        {
            IsUnpacking = false;
        }
    }

    /// <summary>
    /// Computes CRC32/MD5/SHA-1/SHA-256 of this entry's decompressed bytes (see
    /// <see cref="RomChecksum.ComputeAll"/>) and caches them so <see cref="ExpectedChecksumInput"/>
    /// can be checked against them live without re-decompressing. Same
    /// <paramref name="externalBaseRom"/> contract as <see cref="UnpackAsync"/>.
    /// </summary>
    public async Task ComputeChecksumsAsync(byte[]? externalBaseRom)
    {
        IsComputingChecksums = true;
        ChecksumError = null;
        try
        {
            byte[] rom = await Task.Run(() => _getRomBytes(externalBaseRom));
            RomChecksums checksums = await Task.Run(() => RomChecksum.ComputeAll(rom));

            _cachedRomBytes = rom;
            Crc32Text = checksums.Crc32;
            Md5Text = checksums.Md5;
            Sha1Text = checksums.Sha1;
            Sha256Text = checksums.Sha256;

            if (!string.IsNullOrWhiteSpace(ExpectedChecksumInput))
                UpdateMatchResult();
        }
        catch (Exception ex)
        {
            ChecksumError = $"Checksum failed: {ex.Message}";
        }
        finally
        {
            IsComputingChecksums = false;
        }
    }

    private void UpdateMatchResult()
    {
        if (string.IsNullOrWhiteSpace(ExpectedChecksumInput))
        {
            MatchResultText = null;
            return;
        }

        bool? result = RomChecksum.Matches(_cachedRomBytes!, ExpectedChecksumInput);
        MatchSucceeded = result == true;
        MatchResultText = result switch
        {
            true => "✓ Matches",
            false => "✗ Doesn't match",
            null => "Not a recognized length (need 8/32/40/64 hex chars for CRC32/MD5/SHA-1/SHA-256)",
        };
    }
}
