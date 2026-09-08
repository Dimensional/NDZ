using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ndz.Gui.ViewModels;

/// <summary>
/// One ROM loaded into the Pack queue - a decoded icon/title/id plus enough metadata to
/// render a card. Just a flat queue entry for now; not yet a base/target bundle member
/// (that grouping interaction is a follow-up on top of this).
/// </summary>
public partial class RomEntryViewModel : ViewModelBase
{
    private readonly Action<RomEntryViewModel> _onRemove;

    public string FilePath { get; }
    public Bitmap Icon { get; }
    public string ShortTitle { get; }
    public string FullTitle { get; }
    public string GameCode { get; }

    /// <summary>
    /// The header's revision byte (0x1E) - usually 0, but distinguishes re-releases
    /// sharing the same <see cref="GameCode"/> (e.g. a Wii U Virtual Console dump vs. the
    /// original cartridge). Part of the duplicate-detection key alongside
    /// <see cref="GameCode"/> - see <see cref="MainWindowViewModel"/>.
    /// </summary>
    public byte RomVersion { get; }

    /// <summary>Non-empty only when <see cref="RomVersion"/> isn't the default 0 - most ROMs have nothing worth calling out here.</summary>
    public string RevisionText { get; }

    public bool HasRevision => !string.IsNullOrEmpty(RevisionText);

    /// <summary>
    /// Derived from the header's own unit-code byte (0x12), not the banner's version field
    /// - the authoritative platform classification, e.g. "NDS + DSi" for Pokemon
    /// Black/White regardless of what a .nds/.dsi extension happened to suggest.
    /// </summary>
    public string PlatformText { get; }

    /// <summary>Non-empty only when the banner's own version field marks it as carrying an animated icon sequence - unrelated to <see cref="PlatformText"/>, and not decoded (see <see cref="Ndz.Core.Format.NdsIcon"/>).</summary>
    public string BannerNote { get; }

    public bool HasBannerNote => !string.IsNullOrEmpty(BannerNote);

    public string SourceFileText { get; }

    /// <summary>
    /// True when another queue item already carries the same <see cref="GameCode"/> AND
    /// <see cref="RomVersion"/>. Recomputed by the owning view model over the whole queue
    /// after every add/remove - a same-code-and-revision heuristic, not a full-ROM hash
    /// comparison (cheap, and two files sharing both are effectively always the same
    /// content) - deliberately NOT keyed on GameCode alone, since a same-code re-release
    /// (e.g. a Virtual Console dump) can carry a different RomVersion and is genuinely
    /// different content worth keeping both of.
    /// </summary>
    [ObservableProperty]
    private bool _isDuplicate;

    public RomEntryViewModel(
        string filePath,
        Bitmap icon,
        string shortTitle,
        string fullTitle,
        string gameCode,
        byte unitCode,
        byte romVersion,
        ushort bannerVersion,
        long fileSizeBytes,
        Action<RomEntryViewModel> onRemove)
    {
        FilePath = filePath;
        Icon = icon;
        ShortTitle = shortTitle;
        FullTitle = fullTitle;
        GameCode = gameCode;
        RomVersion = romVersion;
        RevisionText = romVersion != 0 ? $"Rev {romVersion}" : string.Empty;
        PlatformText = unitCode switch
        {
            0x00 => "NDS",
            0x02 => "NDS + DSi",
            0x03 => "DSi-exclusive",
            _ => $"Unknown platform (0x{unitCode:X2})",
        };
        BannerNote = bannerVersion >= 0x0103 ? "Animated icon (not rendered)" : string.Empty;
        SourceFileText = $"{Path.GetFileName(filePath)} · {fileSizeBytes / (1024.0 * 1024.0):0.#} MB";
        _onRemove = onRemove;
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);
}
