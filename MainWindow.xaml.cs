using System.Text;
using System.Windows;
using NativeTavern.ViewModels;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace NativeTavern;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
        ContentRendered += OnContentRendered;
        SizeChanged += (_, _) => SidebarColumn.Width = new GridLength(ActualWidth < 1100 ? 192 : 220);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            StartupScale.ScaleX = 1;
            StartupScale.ScaleY = 1;
            StartupTranslate.Y = 0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(280);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        StartupScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.985, 1, duration) { EasingFunction = easing });
        StartupScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.985, 1, duration) { EasingFunction = easing });
        StartupTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            if (viewModel.NewChatCommand.CanExecute(null)) viewModel.NewChatCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.P)
        {
            viewModel.ShowPromptInspectorCommand.Execute(null); e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma)
        {
            viewModel.ShowSettingsCommand.Execute(null); e.Handled = true;
        }
    }

    private async void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await viewModel.HandleDroppedFilesAsync(files);
    }
}
