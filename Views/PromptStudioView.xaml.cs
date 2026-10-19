using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NativeTavern.ViewModels;

namespace NativeTavern.Views;

public partial class PromptStudioView : UserControl
{
    public PromptStudioView() => InitializeComponent();
    private PromptStudioViewModel? ViewModel => DataContext as PromptStudioViewModel;

    private async void ExportResources_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Prompt Resources",
            FileName = $"lorehaven-resources-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "JSON (*.json)|*.json"
        };
        if (dialog.ShowDialog() == true) await ViewModel.ExportResourcesAsync(dialog.FileName);
    }

    private async void ImportResources_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Import Prompt Resources",
            Filter = "Prompt resources (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true) await ViewModel.ImportResourcesAsync(dialog.FileName);
    }

    private async void DeletePersona_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedPersona is not { } persona) return;
        if (ConfirmDeleteDialog.Show(this, "删除人设？", $"确定删除“{persona.Name}”吗？",
                "该人设将从提示词工作室中移除。此操作无法撤销。", "删除人设"))
            await ViewModel.DeleteSelectedPersonaAsync();
    }

    private async void DeleteLorebook_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedLorebook is not { } lorebook) return;
        if (ConfirmDeleteDialog.Show(this, "删除世界书？", $"确定删除“{lorebook.Name}”吗？",
                "世界书及其全部条目都会被永久删除。此操作无法撤销。", "删除世界书"))
            await ViewModel.DeleteSelectedLorebookAsync();
    }

    private async void DeleteLoreEntry_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedLoreEntry is not { } entry) return;
        if (ConfirmDeleteDialog.Show(this, "删除世界书条目？", $"确定删除“{entry.Name}”吗？",
                "该条目将不再参与提示词构建。此操作无法撤销。", "删除条目"))
            await ViewModel.DeleteSelectedLoreEntryAsync();
    }

    private async void DeletePreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedPreset is not { } preset) return;
        if (ConfirmDeleteDialog.Show(this, "删除提示词预设？", $"确定删除“{preset.Name}”吗？",
                "该预设将从所有可选预设中移除。此操作无法撤销。", "删除预设"))
            await ViewModel.DeleteSelectedPresetAsync();
    }
}
