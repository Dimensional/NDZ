using System;
using System.Collections.Generic;
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
}
