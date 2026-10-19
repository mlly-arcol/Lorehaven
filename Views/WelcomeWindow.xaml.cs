using System.Windows;
using System.Windows.Input;

namespace NativeTavern.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => EnterWorkspaceButton.Focus();
    }

    private void EnterWorkspace_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
