using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ndz.Gui.Views;

public partial class AboutWindow : Window
{
    public string VersionText { get; }

    public AboutWindow()
    {
        InitializeComponent();

        // AssemblyInformationalVersion, not AssemblyVersion (Assembly.GetName().Version) -
        // the latter defaults to a meaningless "1.0.0.0" since nothing in this project
        // sets it. InformationalVersion is computed at build time from git itself (see
        // Directory.Build.targets at the repo root) - a real tag/commit-distance/hash
        // string like "v0.0.1-10-g4994c75-dirty", or just "v0.0.1" at an exact tag.
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "dev";
        VersionText = $"Version {version}";
        DataContext = this;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
