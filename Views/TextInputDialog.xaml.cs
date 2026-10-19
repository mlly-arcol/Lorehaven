using System.Windows;

namespace NativeTavern.Views;

public partial class TextInputDialog : Window
{
    private TextInputDialog(string title, string prompt, string value, int maxLength)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = value;
        ValueTextBox.MaxLength = maxLength;
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    public string Value => ValueTextBox.Text.Trim();

    public static string? Show(DependencyObject owner, string title, string prompt, string value, int maxLength)
    {
        var dialog = new TextInputDialog(title, prompt, value, maxLength)
        {
            Owner = Window.GetWindow(owner)
        };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            MessageBox.Show("标题不能为空。", "Lorehaven", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
