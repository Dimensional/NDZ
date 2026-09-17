using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ndz.Gui.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
