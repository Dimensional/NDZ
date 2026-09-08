using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ndz.Gui.ViewModels;

/// <summary>
/// One ROM loaded into the Pack queue - a decoded icon/title/id plus enough metadata to
/// render a card. Doubles as a base when <see cref="Targets"/> is non-empty: the pair
/// format is a star topology (one base, N base-patched targets, no further nesting), so a
/// target's own <see cref="Targets"/> is always left empty - nothing adds to it, and the
/// UI never renders a "+" strip on a target chip in the first place. Grouping only ever
/// happens by dropping a fresh file from Explorer onto a card's "+" strip (see
/// <see cref="MainWindowViewModel.AddTargetsAsync"/>) - dragging one already-queued card
/// onto another isn't a supported gesture.
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

    /// <summary>
    /// The "USA / Europe / Japan"-style release territory, from the game code's own 4th
    /// character (see <see cref="Ndz.Core.Format.NdsRomInfo.DestinationLabel"/>) - what
    /// most people mean by "region". Always present (every game code has a 4th
    /// character), unlike <see cref="RegionLockText"/>.
    /// </summary>
    public string DestinationText { get; }

    /// <summary>
    /// A different, rarely-relevant hardware field GBATEK happens to also call "Region"
    /// (see <see cref="Ndz.Core.Format.NdsRomInfo.RegionLockLabel"/>) - already empty
    /// (from Core) when there's nothing worth claiming, including on any DSi title, where
    /// the byte is confirmed unreliable.
    /// </summary>
    public string RegionLockText { get; }

    public bool HasRegionLock => !string.IsNullOrEmpty(RegionLockText);

    public string SourceFileText { get; }

    /// <summary>
    /// A composed multi-line hover summary (full title, platform/region/revision, full
    /// file path) for the card and chip templates' tooltips - everything the compact card
    /// truncates away is still one hover away.
    /// </summary>
    public string TooltipText { get; }

    /// <summary>
    /// True when another loaded ROM - anywhere, a top-level card or a target chip nested
    /// under any base - already carries the same <see cref="GameCode"/> AND
    /// <see cref="RomVersion"/>. Recomputed over everything currently loaded after every
    /// add/remove - a same-code-and-revision heuristic, not a full-ROM hash comparison
    /// (cheap, and two files sharing both are effectively always the same content) -
    /// deliberately NOT keyed on GameCode alone, since a same-code re-release (e.g. a
    /// Virtual Console dump) can carry a different RomVersion and is genuinely different
    /// content worth keeping both of.
    /// </summary>
    [ObservableProperty]
    private bool _isDuplicate;

    /// <summary>This card's patch targets, if any - rendered as small chips, not full cards. Empty for a target itself (see the class remarks).</summary>
    public ObservableCollection<RomEntryViewModel> Targets { get; } = [];

    public RomEntryViewModel(
        string filePath,
        Bitmap icon,
        string shortTitle,
        string fullTitle,
        string gameCode,
        byte unitCode,
        string destinationLabel,
        string regionLockLabel,
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
        DestinationText = destinationLabel;
        RegionLockText = string.IsNullOrEmpty(regionLockLabel) ? string.Empty : $"Region-locked: {regionLockLabel}";
        SourceFileText = $"{Path.GetFileName(filePath)} · {fileSizeBytes / (1024.0 * 1024.0):0.#} MB";
        _onRemove = onRemove;

        var tooltipLines = new List<string>();
        if (!string.IsNullOrEmpty(fullTitle))
            tooltipLines.Add(fullTitle);
        tooltipLines.Add($"{gameCode}{(HasRevision ? " · " + RevisionText : "")} · {PlatformText} · {DestinationText}");
        if (HasRegionLock)
            tooltipLines.Add(RegionLockText);
        if (HasBannerNote)
            tooltipLines.Add(BannerNote);
        tooltipLines.Add(filePath);
        TooltipText = string.Join("\n\n", tooltipLines);
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);
}
