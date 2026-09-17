using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Ndz.Core.Archives;
using Ndz.Core.Compression;
using Ndz.Gui.Services;
using Ndz.Gui.ViewModels;

namespace Ndz.Gui.Views;

public partial class MainWindow : Window
{
    /// <summary>Shared file-picker filter for the Pack tab's own "Open ROM…"/"Add target" pickers - a real .nds/.dsi ROM, or a .zip/.7z/.rar archive holding some (see <see cref="ArchiveExtractor"/>).</summary>
    private static readonly IReadOnlyList<FilePickerFileType> RomPickerFileTypes =
    [
        new FilePickerFileType("Nintendo DS/DSi ROM") { Patterns = ["*.nds", "*.dsi"] },
        new FilePickerFileType("Archive") { Patterns = ["*.zip", "*.7z", "*.rar"] },
    ];

    /// <summary>Same idea as <see cref="RomPickerFileTypes"/>, for Examine's own "Open File…" picker, which also accepts .ndz.</summary>
    private static readonly IReadOnlyList<FilePickerFileType> ExaminePickerFileTypes =
    [
        new FilePickerFileType("NDZ / NDS / DSi") { Patterns = ["*.ndz", "*.nds", "*.dsi"] },
        new FilePickerFileType("Archive") { Patterns = ["*.zip", "*.7z", "*.rar"] },
    ];

    /// <summary>A hack target can be either a full ROM or a standalone .xdelta patch against the base - see <see cref="HackTargetViewModel"/>.</summary>
    private static readonly IReadOnlyList<FilePickerFileType> HackTargetPickerFileTypes =
    [
        new FilePickerFileType("ROM or xdelta patch") { Patterns = ["*.nds", "*.dsi", "*.xdelta"] },
    ];

    private static readonly IReadOnlyList<FilePickerFileType> NdzPickerFileTypes =
    [
        new FilePickerFileType("NDZ container") { Patterns = ["*.ndz"] },
    ];

    public MainWindow()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(DropZone, true);
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Cheap accept: any file or folder present. What's actually inside a folder can
        // only be known by really walking it, which OnDrop already does - hover just
        // needs to not reject something that would in fact yield ROMs.
        bool hasAny = e.DataTransfer.TryGetFiles()?.Length > 0;
        e.DragEffects = hasAny ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IStorageItem[] items = e.DataTransfer.TryGetFiles() ?? [];
        string[] paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length == 0)
            return;

        // Examine has no base/target grouping concept except one: dropping directly onto
        // one entry's own "+" attach-base bubble (Tag="AttachBaseStrip") attaches that file
        // as its base, same as clicking it and picking one - see OnAttachBaseClicked's own
        // remarks. Anywhere else, every drop just adds sources, wherever in the pane it lands.
        if (vm.ShowExamine)
        {
            ExamineEntryViewModel? attachTarget = (e.Source as Visual)?
                .GetSelfAndVisualAncestors()
                .OfType<Control>()
                .FirstOrDefault(c => Equals(c.Tag, "AttachBaseStrip"))
                ?.DataContext as ExamineEntryViewModel;

            if (attachTarget is not null && paths.Length > 0)
            {
                byte[] bytes = await File.ReadAllBytesAsync(paths[0]);
                attachTarget.AttachExternalBase(bytes, Path.GetFileName(paths[0]));
                return;
            }

            await vm.Examine.AddPathsAsync(paths);
            return;
        }

        // Landed specifically on a card's own "+ hack target" bubble (Tag="HackTargetStrip")?
        // Same routing OnAddHackTargetClicked's picker gives - checked first since that
        // bubble is nested inside the card's own "RomCard" region below, and a hit there
        // should win over the more general pair-target routing.
        RomEntryViewModel? hackTargetBase = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(c => Equals(c.Tag, "HackTargetStrip"))
            ?.DataContext as RomEntryViewModel;

        if (hackTargetBase is not null)
        {
            await vm.AddHackTargetsAsync(hackTargetBase, paths);
            return;
        }

        // Landed anywhere else on an existing card (not just its "+" strip - a wide,
        // forgiving hit area beats a thin one)? Add as that card's patch target instead of
        // a new top-level card. e.Source is the actual visual under the pointer at drop
        // time - walk up from there looking for the card marker (Tag="RomCard"), whose
        // DataContext is that card's own RomEntryViewModel (inherited from its DataTemplate).
        // Skipped entirely while pair-container creation is disabled (RomEntryViewModel.
        // PairPackingEnabled) - a drop on a card just falls through to a normal top-level
        // add instead, same as landing anywhere else, rather than surfacing an error for
        // an interaction the UI no longer visibly offers.
        RomEntryViewModel? targetBase = !PairContainerPolicy.CreationEnabled ? null : (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(c => Equals(c.Tag, "RomCard"))
            ?.DataContext as RomEntryViewModel;

        if (targetBase is not null)
        {
            // A dropped .xdelta can only mean a hack target (a pair target is always a
            // full ROM) - unambiguous, so it's routed there automatically even when it
            // lands somewhere on the card other than the dedicated bubble above. Everything
            // else (.nds/.dsi/archives) keeps meaning a pair target, same as before.
            string[] xdeltaPaths = paths.Where(p => Path.GetExtension(p).Equals(".xdelta", StringComparison.OrdinalIgnoreCase)).ToArray();
            string[] otherPaths = paths.Except(xdeltaPaths).ToArray();

            if (xdeltaPaths.Length > 0)
                await vm.AddHackTargetsAsync(targetBase, xdeltaPaths);
            if (otherPaths.Length > 0)
                await vm.AddTargetsAsync(targetBase, otherPaths);
        }
        else
        {
            await vm.AddPathsAsync(paths);
        }
    }

    private async void OnOpenRomClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open .nds / .dsi ROM(s) or a .zip/.7z/.rar archive",
            AllowMultiple = true,
            FileTypeFilter = RomPickerFileTypes,
        });

        string[] paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await vm.AddPathsAsync(paths);
    }

    private async void OnAddTargetClicked(object? sender, RoutedEventArgs e)
    {
        // The clicked button's DataContext is this card's own RomEntryViewModel
        // (inherited from the DataTemplate it's declared in) - no visual-tree walk needed
        // here, unlike OnDrop.
        if (sender is not Button { DataContext: RomEntryViewModel baseItem } || DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Add patch target for \"{baseItem.ShortTitle}\"",
            AllowMultiple = true,
            FileTypeFilter = RomPickerFileTypes,
        });

        string[] paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await vm.AddTargetsAsync(baseItem, paths);
    }

    /// <summary>
    /// One button, one action - whatever <see cref="RomEntryViewModel.PackButtonText"/>
    /// currently says it is. No hack targets attached: the existing plain solo/pair pack,
    /// prompting for a single output file exactly as before. One or more attached: the
    /// combined build (base, if this card is a raw ROM, plus every hack, all named
    /// automatically from their own titles - see <see cref="RomEntryViewModel.PackWithHacksAsync"/>'s
    /// own remarks), prompting for a single DESTINATION FOLDER instead of a single file,
    /// since this now writes more than one file - the GUI equivalent of the CLI's own
    /// `pack-hack --build-base` in one click, generalized to N hacks at once.
    /// </summary>
    private async void OnPackClicked(object? sender, RoutedEventArgs e)
    {
        // Same pattern as OnAddTargetClicked: the clicked button's DataContext is this
        // card's own RomEntryViewModel, inherited from the DataTemplate it's declared in.
        if (sender is not Button { DataContext: RomEntryViewModel item })
            return;

        IStorageFolder? suggestedFolder = null;
        try
        {
            if (item.Source.DirectoryHint is { } dir)
                suggestedFolder = await StorageProvider.TryGetFolderFromPathAsync(new Uri(dir));
        }
        catch
        {
            // Best-effort only - the picker just opens wherever the OS defaults to instead.
        }

        if (item.HackTargets.Count > 0)
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Choose a folder for \"{item.ShortTitle}\"'s base + hack(s)",
                AllowMultiple = false,
                SuggestedStartLocation = suggestedFolder,
            });

            string? folder = folders.FirstOrDefault()?.TryGetLocalPath();
            if (folder is not null)
                await item.PackWithHacksAsync(folder);
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save .ndz for \"{item.ShortTitle}\"",
            SuggestedFileName = $"{SanitizeFileName(item.ShortTitle)}.ndz",
            SuggestedStartLocation = suggestedFolder,
            FileTypeChoices = [new FilePickerFileType("NDZ container") { Patterns = ["*.ndz"] }],
            DefaultExtension = "ndz",
        });

        string? path = file?.TryGetLocalPath();
        if (path is not null)
            await item.PackAsync(path);
    }

    /// <summary>
    /// Adds a hack target - a ROM or an .xdelta patch, either one landing in
    /// <see cref="RomEntryViewModel.HackTargets"/> rather than <see cref="RomEntryViewModel.Targets"/>
    /// (see <see cref="HackTargetViewModel"/>'s own remarks on why that's a separate
    /// collection). Reached via its own dedicated control rather than the plain "+" strip,
    /// since (unlike a plain drop, which always means a pair target) there's genuine
    /// ambiguity a picker dialog resolves just by being explicitly the "add hack" one.
    /// </summary>
    private async void OnAddHackTargetClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: RomEntryViewModel baseItem } || DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Add a hack target for \"{baseItem.ShortTitle}\" (ROM or .xdelta patch)",
            AllowMultiple = true,
            FileTypeFilter = HackTargetPickerFileTypes,
        });

        string[] paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await vm.AddHackTargetsAsync(baseItem, paths);
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Where(c => !invalid.Contains(c)).ToArray());
        cleaned = cleaned.Trim();
        return cleaned.Length > 0 ? cleaned : "output";
    }

    private async void OnAddFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add ROM folder (searched recursively)",
            AllowMultiple = true,
        });

        string[] paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await vm.AddPathsAsync(paths);
    }

    private async void OnExamineOpenFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open .ndz / .nds / .dsi file(s) or a .zip/.7z/.rar archive",
            AllowMultiple = true,
            FileTypeFilter = ExaminePickerFileTypes,
        });

        string[] paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await vm.Examine.AddPathsAsync(paths);
    }

    private async void OnExamineAddFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a folder (searched recursively for .ndz/.nds/.dsi)",
            AllowMultiple = true,
        });

        string[] paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await vm.Examine.AddPathsAsync(paths);
    }

    private void OnRemoveSourceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not Button { DataContext: ExamineSourceViewModel source })
            return;

        vm.Examine.RemoveSource(source);
    }

    /// <summary>
    /// Unpacks one Examine entry. A standalone base-patched .ndz (see
    /// <see cref="ExamineEntryViewModel.RequiresExternalBaseRom"/>) needs its base ROM
    /// picked first; a `.delta.ndz` hack container (<see cref="ExamineEntryViewModel.RequiresExternalBaseNdz"/>)
    /// needs its base's own already-packed `.ndz` instead - a pair-container entry never
    /// needs either, it resolves its own base internally.
    /// </summary>
    /// <summary>
    /// Attaches this entry's base once (a "+" bubble shown in place of Unpack/Checksums
    /// until a required base is attached - see <see cref="ExamineEntryViewModel.IsBaseReady"/>),
    /// rather than the first cut's re-prompt-on-every-click - real use immediately flagged
    /// that as tedious for something that never changes between clicks on the same entry.
    /// Prompts for a raw ROM (<see cref="ExamineEntryViewModel.RequiresExternalBaseRom"/>) or
    /// a base .ndz (<see cref="ExamineEntryViewModel.RequiresExternalBaseNdz"/>) depending on
    /// which this entry actually needs.
    /// </summary>
    private async void OnAttachBaseClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ExamineEntryViewModel entry })
            return;

        IReadOnlyList<IStorageFile> baseFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = entry.RequiresExternalBaseNdz ? $"Select base .ndz for \"{entry.ShortTitle}\"" : $"Select base ROM for \"{entry.ShortTitle}\"",
            AllowMultiple = false,
            FileTypeFilter = entry.RequiresExternalBaseNdz ? NdzPickerFileTypes : [new FilePickerFileType("Nintendo DS/DSi ROM") { Patterns = ["*.nds", "*.dsi"] }],
        });

        string? basePath = baseFiles.FirstOrDefault()?.TryGetLocalPath();
        if (basePath is null)
            return;

        byte[] bytes = await File.ReadAllBytesAsync(basePath);
        entry.AttachExternalBase(bytes, Path.GetFileName(basePath));
    }

    private void OnDetachBaseClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ExamineEntryViewModel entry })
            entry.DetachExternalBase();
    }

    private async void OnUnpackClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ExamineEntryViewModel entry } || !entry.IsBaseReady)
            return;

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save unpacked ROM for \"{entry.ShortTitle}\"",
            SuggestedFileName = entry.SuggestedFileName,
            FileTypeChoices = [new FilePickerFileType("Nintendo DS/DSi ROM") { Patterns = ["*.nds", "*.dsi"] }],
            DefaultExtension = "nds",
        });

        string? outputPath = file?.TryGetLocalPath();
        if (outputPath is not null)
            await entry.UnpackAsync(outputPath);
    }

    /// <summary>
    /// Unpacks every entry of one Examine source into a single chosen folder - see
    /// <see cref="ExamineSourceViewModel.UnpackAllAsync"/> for the real work (naming,
    /// per-entry results). Suggests that source's own containing folder as the starting
    /// location, matching <see cref="OnPackClicked"/>'s own pattern.
    /// </summary>
    private async void OnUnpackAllClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ExamineSourceViewModel source })
            return;

        IStorageFolder? suggestedFolder = null;
        try
        {
            if (source.Source.DirectoryHint is { } dir)
                suggestedFolder = await StorageProvider.TryGetFolderFromPathAsync(new Uri(dir));
        }
        catch
        {
            // Best-effort only - the picker just opens wherever the OS defaults to instead.
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Unpack all ROMs from \"{source.FileName}\" into…",
            AllowMultiple = false,
            SuggestedStartLocation = suggestedFolder,
        });

        string? outputFolder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (outputFolder is not null)
            await source.UnpackAllAsync(outputFolder);
    }

    /// <summary>
    /// Computes one Examine entry's CRC32/MD5/SHA-1/SHA-256 - same base-ROM-prompt
    /// contract as <see cref="OnUnpackClicked"/>, just without a save dialog afterward.
    /// </summary>
    private async void OnChecksumsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ExamineEntryViewModel entry } || !entry.IsBaseReady)
            return;

        await entry.ComputeChecksumsAsync();
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void OnThemeLightClicked(object? sender, RoutedEventArgs e) => ThemeService.SetTheme(AppTheme.Light);
    private void OnThemeDarkClicked(object? sender, RoutedEventArgs e) => ThemeService.SetTheme(AppTheme.Dark);
    private void OnThemeSystemClicked(object? sender, RoutedEventArgs e) => ThemeService.SetTheme(AppTheme.System);
    private void OnThemeSignatureClicked(object? sender, RoutedEventArgs e) => ThemeService.SetTheme(AppTheme.Signature);

    private async void OnAboutClicked(object? sender, RoutedEventArgs e)
    {
        var about = new AboutWindow();
        await about.ShowDialog(this);
    }
}
