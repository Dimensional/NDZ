using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Ndz.Core.Archives;
using Ndz.Core.XDelta;

namespace Ndz.Gui.ViewModels;

/// <summary>What <see cref="HackTargetViewModel.Source"/> actually points at.</summary>
public enum HackTargetKind
{
    /// <summary>A full target ROM (.nds/.dsi) - used directly.</summary>
    DirectRom,

    /// <summary>A standalone .xdelta patch against the base - <see cref="XDeltaCodec.Apply"/> reconstructs the target ROM from it before packing, matching ndz-studio's own "Pack hack" (xdelta3-wasm applies the patch page-side).</summary>
    XdeltaPatch,
}

/// <summary>
/// One `.delta.ndz` hack job attached to a base <see cref="RomEntryViewModel"/> card - a
/// genuinely different relationship from that card's own <see cref="RomEntryViewModel.Targets"/>
/// (pair-container targets, which merge into ONE shared output file). Deliberately a pure,
/// passive record of "what's attached" - no per-target options, no per-target action button.
/// Two earlier cuts each added per-target controls (first a base-.ndz build/browse toggle,
/// then just a browse field) and both were flagged in real use as confusing or as reintroducing
/// a dead end - the base .ndz a hack needs is entirely the base CARD's own concern now
/// (<see cref="RomEntryViewModel.PackWithHacksAsync"/> resolves or builds it once, automatically,
/// for every attached hack in one combined action), so there is nothing left for this class to
/// configure or act on by itself.
/// </summary>
public partial class HackTargetViewModel : ViewModelBase
{
    private readonly Action<HackTargetViewModel> _onRemove;

    public RomSource Source { get; }
    public HackTargetKind Kind { get; }

    /// <summary>Null for <see cref="HackTargetKind.XdeltaPatch"/> - a patch file has no ROM header/banner to decode an icon from, and applying it just for a preview felt like more upfront cost than the preview is worth (it's applied anyway once <see cref="RomEntryViewModel.PackWithHacksAsync"/> actually builds this hack).</summary>
    public Bitmap? Icon { get; }

    public bool IsPatchKind => Kind == HackTargetKind.XdeltaPatch;

    public string ShortTitle { get; }
    public string GameCode { get; }
    public bool HasGameCode => !string.IsNullOrEmpty(GameCode);
    public string SourceFileText { get; }
    public string TooltipText { get; }

    public HackTargetViewModel(RomSource source, HackTargetKind kind,
        Bitmap? icon, string shortTitle, string gameCode, long fileSizeBytes,
        Action<HackTargetViewModel> onRemove)
    {
        _onRemove = onRemove;
        Source = source;
        Kind = kind;
        Icon = icon;
        ShortTitle = shortTitle;
        GameCode = gameCode;
        SourceFileText = $"{source.ShortLabel} · {fileSizeBytes / (1024.0 * 1024.0):0.#} MB";

        TooltipText = kind == HackTargetKind.XdeltaPatch
            ? $"Hack via .xdelta patch\n\n{source.FullLabel}"
            : $"Hack target\n\n{gameCode}\n\n{source.FullLabel}";
    }

    [RelayCommand]
    private void Remove() => _onRemove(this);
}
