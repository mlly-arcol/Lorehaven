using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Services;
using NativeTavern.ViewModels;
using Xunit;

namespace NativeTavern.Tests;

public sealed class LocalModelDirectoryTests
{
    [Fact]
    public void RefreshReadsTheChosenFolderWithoutContactingServicesAndRetainsSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "NativeTavernFolders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.gguf"), "model");
            File.WriteAllText(Path.Combine(root, "b.gguf"), "model");
            using var service = new LocalModelService();
            var viewModel = new SettingsViewModel(null!, new LocalizationService(), null!, null!, service, null!, NullLogger<SettingsViewModel>.Instance);
            viewModel.LocalModelDirectory = root;
            viewModel.RefreshModelFilesCommand.Execute(null);
            Assert.Equal(2, viewModel.LocalModels.Count);
            viewModel.SelectedLocalModel = viewModel.LocalModels.Single(x => x.Name == "b.gguf");
            viewModel.RefreshModelFilesCommand.Execute(null);
            Assert.Equal("b.gguf", viewModel.SelectedLocalModel!.Name);

            viewModel.LocalModelDirectory = Path.Combine(root, "missing");
            viewModel.RefreshModelFilesCommand.Execute(null);
            Assert.Equal(2, viewModel.LocalModels.Count);
            Assert.Contains("目录不存在", viewModel.StatusMessage);
            viewModel.IsBusy = true;
            Assert.False(viewModel.RefreshModelFilesCommand.CanExecute(null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DiagnosticsKeepOnlyTheLatestCharacters()
    {
        var diagnostics = new LocalModelDiagnostics();
        diagnostics.Append(new string('x', LocalModelDiagnostics.Capacity));
        diagnostics.Append("the useful ending");
        var text = diagnostics.Snapshot();
        Assert.Equal(LocalModelDiagnostics.Capacity, text.Length);
        Assert.EndsWith("the useful ending", text);
    }
}
