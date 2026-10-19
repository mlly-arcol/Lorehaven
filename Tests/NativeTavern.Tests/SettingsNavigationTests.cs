using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Security;
using NativeTavern.Services;
using NativeTavern.ViewModels;
using Xunit;

namespace NativeTavern.Tests;

[Collection("Theme settings")]
public sealed class SettingsNavigationTests
{
    [Fact]
    public async Task AllSettingsSectionsAreAvailableWithAnInitialSelection()
    {
        using var fixture = new SettingsFixture();
        var viewModel = fixture.CreateViewModel();

        Assert.Equal("interface", viewModel.SelectedSettingsSection.Code);
        await viewModel.InitializeAsync();

        Assert.Equal(["interface", "connection", "generation", "privacy", "local-models", "backup"],
            viewModel.SettingsSections.Select(x => x.Code));
        Assert.All(viewModel.SettingsSections, section =>
        {
            Assert.False(string.IsNullOrWhiteSpace(section.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(section.Icon));
        });
        Assert.Same(viewModel.SettingsSections[0], viewModel.SelectedSettingsSection);
    }

    [Fact]
    public async Task SwitchingSectionsKeepsUnsavedEditsAndDoesNotWriteToDisk()
    {
        using var fixture = new SettingsFixture();
        await fixture.SaveInitialSettingsAsync();
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();
        var saved = await fixture.Repository.GetAsync("ProviderSettings");
        viewModel.Model = "unfinished-model";
        viewModel.BaseUrl = "http://localhost:9876/v1";
        viewModel.ApiKey = "unfinished-key";
        viewModel.LocalGpuLayers = 17;
        viewModel.IncludeImageContext = true;

        foreach (var section in viewModel.SettingsSections)
            viewModel.SelectedSettingsSection = section;

        Assert.Equal("backup", viewModel.SelectedSettingsSection.Code);
        Assert.Equal("unfinished-model", viewModel.Model);
        Assert.Equal("http://localhost:9876/v1", viewModel.BaseUrl);
        Assert.Equal("unfinished-key", viewModel.ApiKey);
        Assert.Equal(17, viewModel.LocalGpuLayers);
        Assert.True(viewModel.IncludeImageContext);
        Assert.Equal(saved, await fixture.Repository.GetAsync("ProviderSettings"));
        Assert.Equal("saved-key", (await fixture.Settings.LoadResolvedAsync()).ApiKey);
    }

    [Fact]
    public async Task LanguageRefreshKeepsTheCurrentSectionAndUnsavedFields()
    {
        using var fixture = new SettingsFixture();
        await fixture.SaveInitialSettingsAsync();
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();
        var saved = await fixture.Repository.GetAsync("ProviderSettings");
        viewModel.SelectedSettingsSection = viewModel.SettingsSections.Single(x => x.Code == "local-models");
        viewModel.ApiKey = "unfinished-key";
        viewModel.Model = "unfinished-model";

        fixture.Localization.SetLanguage(LocalizationService.English);

        Assert.Equal("local-models", viewModel.SelectedSettingsSection.Code);
        Assert.Equal("Local models", viewModel.SelectedSettingsSection.DisplayName);
        Assert.Same(viewModel.SettingsSections.Single(x => x.Code == "local-models"), viewModel.SelectedSettingsSection);
        Assert.Equal(["Interface & theme", "Model connection", "Generation", "Context privacy", "Local models", "Data backup"],
            viewModel.SettingsSections.Select(x => x.DisplayName));
        Assert.Equal("unfinished-key", viewModel.ApiKey);
        Assert.Equal("unfinished-model", viewModel.Model);
        Assert.Equal(saved, await fixture.Repository.GetAsync("ProviderSettings"));

        fixture.Localization.SetLanguage(LocalizationService.Chinese);
        Assert.Equal("local-models", viewModel.SelectedSettingsSection.Code);
        Assert.Equal("本地模型", viewModel.SelectedSettingsSection.DisplayName);
    }

    [Fact]
    public async Task EmptyOrUnknownSelectionsFallBackToTheInterfaceSection()
    {
        using var fixture = new SettingsFixture();
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.SelectedSettingsSection = viewModel.SettingsSections.Single(x => x.Code == "privacy");

        viewModel.SelectedSettingsSection = null!;
        Assert.Same(viewModel.SettingsSections[0], viewModel.SelectedSettingsSection);

        viewModel.SelectedSettingsSection = new SettingsSectionOption("missing", "Missing", "");
        Assert.Same(viewModel.SettingsSections[0], viewModel.SelectedSettingsSection);

        viewModel.SelectedSettingsSection = new SettingsSectionOption("generation", "Other name", "");
        Assert.Same(viewModel.SettingsSections.Single(x => x.Code == "generation"), viewModel.SelectedSettingsSection);
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LorehavenNavigation-" + Guid.NewGuid().ToString("N"));
        private readonly LocalModelService _localModels = new();

        public SettingsFixture()
        {
            Directory.CreateDirectory(_root);
            var factory = new DatabaseConnectionFactory(Path.Combine(_root, "settings.db"));
            new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync().GetAwaiter().GetResult();
            Repository = new SettingsRepository(factory);
            Settings = new SettingsService(Repository, new PlainProtector());
        }

        public SettingsRepository Repository { get; }
        public SettingsService Settings { get; }
        public LocalizationService Localization { get; } = new();

        public Task SaveInitialSettingsAsync() => Settings.SaveAsync(InitialSettings(), "saved-key");

        private ProviderSettings InitialSettings() => new()
        {
            ProviderId = "llamacpp",
            BaseUrl = "http://127.0.0.1:8080/v1",
            Model = "saved-model",
            AutoScanLocalModels = false,
            LocalModelDirectory = _root
        };

        public SettingsViewModel CreateViewModel()
        {
            // Avoid model discovery, network requests, and a dependency on real user data.
            if (Repository.GetAsync("ProviderSettings").GetAwaiter().GetResult() is null)
                Settings.SaveAsync(InitialSettings(), null).GetAwaiter().GetResult();
            return new SettingsViewModel(Settings, Localization, null!, null!, _localModels, null!,
                NullLogger<SettingsViewModel>.Instance);
        }

        public void Dispose()
        {
            _localModels.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedText) => protectedText;
    }
}
