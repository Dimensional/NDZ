using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Ndz.Gui.Models;

namespace Ndz.Gui.ViewModels;

/// <summary>
/// One examinable ROM: either a raw .nds/.dsi file (already unpacked, nothing to do but
/// show it's valid) or a single packed entry inside a .ndz - which itself might be a
/// standalone blob or one entry of an <see cref="ExamineSourceViewModel"/>'s pair
/// container. Built via <see cref="ForRawRom"/> or <see cref="ForPackedEntry"/> rather
/// than a public constructor, since which fields are meaningful (ratio/flags/unpack vs.
/// platform/region/revision) differs by which factory made it.
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
    /// shape even though the GUI's own Pack view never does. The view has to prompt for
    /// a base ROM file before <see cref="UnpackAsync"/> can succeed.
    /// </summary>
    public bool RequiresExternalBaseRom { get; }

    /// <summary>Suggested output file name (without directory) for the unpack save dialog.</summary>
    public string SuggestedFileName { get; }

    private readonly Func<byte[]?, byte[]>? _decompress;

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

    private ExamineEntryViewModel(Bitmap icon, string shortTitle, string fullTitle, string gameCode,
        string summaryText, string tooltipText, bool canUnpack, bool requiresExternalBaseRom,
        string suggestedFileName, Func<byte[]?, byte[]>? decompress)
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
        _decompress = decompress;
    }

    public static ExamineEntryViewModel ForRawRom(Bitmap icon, string shortTitle, string fullTitle, string gameCode, string summaryText, string tooltipText) =>
        new(icon, shortTitle, fullTitle, gameCode, summaryText, tooltipText, canUnpack: false, requiresExternalBaseRom: false, suggestedFileName: string.Empty, decompress: null);

    public static ExamineEntryViewModel ForPackedEntry(Bitmap icon, string shortTitle, string fullTitle, string gameCode,
        string summaryText, string tooltipText, bool requiresExternalBaseRom, string suggestedFileName, Func<byte[]?, byte[]> decompress) =>
        new(icon, shortTitle, fullTitle, gameCode, summaryText, tooltipText, canUnpack: true, requiresExternalBaseRom, suggestedFileName, decompress);

    /// <summary>
    /// Decompresses this entry and writes it to <paramref name="outputPath"/>.
    /// <paramref name="externalBaseRom"/> is required (and used) only when
    /// <see cref="RequiresExternalBaseRom"/> is true - ignored otherwise (a pair-container
    /// entry always resolves its own base internally, and a self-contained entry needs no
    /// base at all).
    /// </summary>
    public async Task UnpackAsync(string outputPath, byte[]? externalBaseRom)
    {
        if (_decompress is null)
            throw new InvalidOperationException("This entry can't be unpacked.");

        IsUnpacking = true;
        UnpackError = null;
        UnpackResultText = null;
        try
        {
            byte[] rom = await Task.Run(() => _decompress(externalBaseRom));
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
}
