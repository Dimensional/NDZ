using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Ndz.Gui.ViewModels;

namespace Ndz.Gui.Views;

public partial class MainWindow : Window
{
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

        // Examine has no base/target grouping concept - every drop just adds sources,
        // wherever in the pane it lands.
        if (vm.ShowExamine)
        {
            await vm.Examine.AddPathsAsync(paths);
            return;
        }

        // Landed anywhere on an existing card (not just its "+" strip - a wide, forgiving
        // hit area beats a thin one)? Add as that card's patch target instead of a new
        // top-level card. e.Source is the actual visual under the pointer at drop time -
        // walk up from there looking for the card marker (Tag="RomCard"), whose
        // DataContext is that card's own RomEntryViewModel (inherited from its DataTemplate).
        RomEntryViewModel? targetBase = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<Control>()
            .FirstOrDefault(c => Equals(c.Tag, "RomCard"))
            ?.DataContext as RomEntryViewModel;

        if (targetBase is not null)
            await vm.AddTargetsAsync(targetBase, paths);
        else
            await vm.AddPathsAsync(paths);
    }

    private async void OnOpenRomClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open .nds / .dsi ROM(s)",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Nintendo DS/DSi ROM") { Patterns = ["*.nds", "*.dsi"] }],
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
            FileTypeFilter = [new FilePickerFileType("Nintendo DS/DSi ROM") { Patterns = ["*.nds", "*.dsi"] }],
        });

        string[] paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await vm.AddTargetsAsync(baseItem, paths);
    }

    private async void OnPackClicked(object? sender, RoutedEventArgs e)
    {
        // Same pattern as OnAddTargetClicked: the clicked button's DataContext is this
        // card's own RomEntryViewModel, inherited from the DataTemplate it's declared in.
        if (sender is not Button { DataContext: RomEntryViewModel item })
            return;

        IStorageFolder? suggestedFolder = null;
        try
        {
            string? dir = Path.GetDirectoryName(item.FilePath);
            if (dir is not null)
                suggestedFolder = await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(dir)));
        }
        catch
        {
            // Best-effort only - the picker just opens wherever the OS defaults to instead.
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
            Title = "Open .ndz / .nds / .dsi file(s)",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("NDZ / NDS / DSi") { Patterns = ["*.ndz", "*.nds", "*.dsi"] }],
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
    /// picked first - a pair-container entry never does, it resolves its own base
    /// internally.
    /// </summary>
    private async void OnUnpackClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ExamineEntryViewModel entry })
            return;

        byte[]? externalBaseRom = null;
        if (entry.RequiresExternalBaseRom)
        {
            IReadOnlyList<IStorageFile> baseFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Select base ROM for \"{entry.ShortTitle}\"",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Nintendo DS/DSi ROM") { Patterns = ["*.nds", "*.dsi"] }],
            });

            string? basePath = baseFiles.FirstOrDefault()?.TryGetLocalPath();
            if (basePath is null)
                return;
            externalBaseRom = await File.ReadAllBytesAsync(basePath);
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Save unpacked ROM for \"{entry.ShortTitle}\"",
            SuggestedFileName = entry.SuggestedFileName,
            FileTypeChoices = [new FilePickerFileType("Nintendo DS/DSi ROM") { Patterns = ["*.nds", "*.dsi"] }],
            DefaultExtension = "nds",
        });

        string? outputPath = file?.TryGetLocalPath();
        if (outputPath is not null)
            await entry.UnpackAsync(outputPath, externalBaseRom);
    }
}
