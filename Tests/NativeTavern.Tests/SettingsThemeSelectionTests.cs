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
public sealed class SettingsThemeSelectionTests
{
    [Fact]
    public async Task ThemeAppliesImmediatelyAndReturnsOnTheNextSettingsPage()
    {
        using var fixture = new SettingsFixture();
        using var themeState = new ThemeState();
        await fixture.Settings.SaveAsync(fixture.InitialSettings("harbor", "light"), null);
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();

        Assert.Equal("harbor", viewModel.SelectedThemeStyle!.Code);
        Assert.Equal("潮汐手札", viewModel.SelectedThemeStyle.DisplayName);
        viewModel.SelectedThemeStyle = viewModel.ThemeStyles.Single(x => x.Code == "starlit");
        Assert.Equal("starlit", ThemeService.StyleCode);
        Assert.Equal(AppThemeMode.Light, ThemeService.Mode);
        await viewModel.AppearanceSaveCompletion;

        var restarted = fixture.CreateViewModel();
        await restarted.InitializeAsync();
        Assert.Equal("starlit", restarted.SelectedThemeStyle!.Code);
        Assert.Equal("light", restarted.SelectedTheme!.Code);
        restarted.Model = "new-model";
        await restarted.SaveCommand.ExecuteAsync(null);
        var stored = await fixture.Settings.LoadAsync();
        Assert.Equal("starlit", stored.ThemeStyle);
        Assert.Equal("new-model", stored.Model);
    }

    [Fact]
    public async Task RapidThemeAndModeChangesKeepTheLastPairWithoutSavingUnrelatedEdits()
    {
        using var fixture = new SettingsFixture();
        using var themeState = new ThemeState();
        await fixture.Settings.SaveAsync(fixture.InitialSettings("harbor", "light"), null);
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.Model = "unfinished-edit";

        for (var i = 0; i < 8; i++)
        {
            viewModel.SelectedThemeStyle = viewModel.ThemeStyles.Single(x => x.Code == (i % 2 == 0 ? "starlit" : "harbor"));
            viewModel.SelectedTheme = viewModel.Themes.Single(x => x.Code == (i % 2 == 0 ? "dark" : "light"));
        }
        viewModel.SelectedThemeStyle = viewModel.ThemeStyles.Single(x => x.Code == "starlit");
        viewModel.SelectedTheme = viewModel.Themes.Single(x => x.Code == "dark");
        await viewModel.AppearanceSaveCompletion;

        var stored = await fixture.Settings.LoadAsync();
        Assert.Equal("starlit", stored.ThemeStyle);
        Assert.Equal("dark", stored.ThemeMode);
        Assert.Equal("saved-model", stored.Model);
        Assert.Equal("unfinished-edit", viewModel.Model);
    }

    [Fact]
    public async Task LanguageRefreshPreservesTheSelectionsWithoutWritingSettings()
    {
        using var fixture = new SettingsFixture();
        await fixture.Settings.SaveAsync(fixture.InitialSettings("harbor", "dark"), null);
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();
        var before = await fixture.Repository.GetAsync("ProviderSettings");

        fixture.Localization.SetLanguage(LocalizationService.English);

        Assert.Equal("harbor", viewModel.SelectedThemeStyle!.Code);
        Assert.Equal("Harbor Journal", viewModel.SelectedThemeStyle.DisplayName);
        Assert.Equal("dark", viewModel.SelectedTheme!.Code);
        Assert.Equal("Dark", viewModel.SelectedTheme.DisplayName);
        Assert.Equal(["Starlit Archive", "Harbor Journal"], viewModel.ThemeStyles.Select(x => x.DisplayName));
        await viewModel.AppearanceSaveCompletion;
        Assert.Equal(before, await fixture.Repository.GetAsync("ProviderSettings"));
    }

    [Fact]
    public async Task ASettingsPageForAnOldConfigurationKeepsItsOriginalTheme()
    {
        using var fixture = new SettingsFixture();
        const string legacyJson = """
            {"providerId":"llamacpp","baseUrl":"http://127.0.0.1:8080/v1",
             "model":"saved-model","themeMode":"dark","autoScanLocalModels":false}
            """;
        await fixture.Repository.SetAsync("ProviderSettings", legacyJson);
        var viewModel = fixture.CreateViewModel();

        await viewModel.InitializeAsync();

        Assert.Equal("harbor", viewModel.SelectedThemeStyle!.Code);
        Assert.Equal("dark", viewModel.SelectedTheme!.Code);
        Assert.Equal(legacyJson, await fixture.Repository.GetAsync("ProviderSettings"));
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LorehavenTheme-" + Guid.NewGuid().ToString("N"));
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

        public ProviderSettings InitialSettings(string style, string mode) => new()
        {
            ProviderId = "llamacpp",
            BaseUrl = "http://127.0.0.1:8080/v1",
            Model = "saved-model",
            ThemeStyle = style,
            ThemeMode = mode,
            AutoScanLocalModels = false,
            LocalModelDirectory = _root
        };

        public SettingsViewModel CreateViewModel() => new(Settings, Localization, null!, null!, _localModels, null!,
            NullLogger<SettingsViewModel>.Instance);

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

    private sealed class ThemeState : IDisposable
    {
        private readonly string _style = ThemeService.StyleCode;
        private readonly AppThemeMode _mode = ThemeService.Mode;
        public void Dispose() => ThemeService.Apply(_mode, _style);
    }
}
