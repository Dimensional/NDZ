using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
        if (paths.Length > 0)
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
