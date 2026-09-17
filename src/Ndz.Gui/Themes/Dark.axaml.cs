using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ndz.Gui.Themes;

public partial class DarkTheme : ResourceDictionary
{
    public DarkTheme() => AvaloniaXamlLoader.Load(this);
}
