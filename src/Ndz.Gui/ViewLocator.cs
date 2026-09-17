using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Ndz.Gui.ViewModels;
using Ndz.Gui.Views;

namespace Ndz.Gui;

/// <summary>
/// Registered as the app-wide fallback <see cref="Avalonia.Controls.Templates.IDataTemplate"/>
/// (see App.axaml) for any <see cref="ViewModelBase"/>-typed DataContext that reaches a
/// content control without its own explicit DataTemplate. In practice every window today
/// is constructed directly (see App.axaml.cs and MainWindow.axaml.cs) and every list-item
/// view model already has an explicit DataTemplate in MainWindow.axaml, so this path isn't
/// currently exercised - kept only as a safety net, and as an explicit type-to-type map
/// rather than the original name-convention-plus-reflection lookup (Type.GetType by string
/// name + Activator.CreateInstance) so it stays correct under trimming: a view referenced
/// only by a runtime-constructed string name has no static reference the trimmer can see,
/// so it can be removed from a trimmed build even though the string-based lookup at
/// runtime still expects to find it.
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        null => null,
        MainWindowViewModel => new MainWindow(),
        _ => new TextBlock { Text = "Not Found: " + param.GetType().Name },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
