using System.Diagnostics;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using NativeTavern.Models;
using NativeTavern.ViewModels;

namespace NativeTavern.Views;

public partial class PluginsView : UserControl
{
    public PluginsView() => InitializeComponent();
    private PluginsViewModel? ViewModel => DataContext as PluginsViewModel;

    private async void InstallPackage_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Lorehaven plugins|*.ntplugin;*.zip|All files|*.*",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true && ViewModel is not null)
            await ViewModel.InstallPackageAsync(dialog.FileName);
    }

    private async void PluginsView_OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var package = files.FirstOrDefault(x => Path.GetExtension(x).ToLowerInvariant() is ".ntplugin" or ".zip");
        if (package is not null) await ViewModel.InstallPackageAsync(package);
    }

    private async void Uninstall_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button { Tag: InstalledPlugin plugin }) return;
        if (ConfirmDeleteDialog.Show(this, "卸载插件？", $"确定卸载“{plugin.Name}”吗？",
                "插件文件会被永久移除。插件产生的数据将保留，便于以后重新安装。", "卸载插件"))
            await ViewModel.UninstallAsync(plugin);
    }

    private void OpenDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        Directory.CreateDirectory(ViewModel.PluginsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", ViewModel.PluginsDirectory) { UseShellExecute = true });
    }
}
