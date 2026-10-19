using Dapper;
using Microsoft.Data.Sqlite;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Security;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class ThemeSettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LorehavenTheme-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsRepository _repository;
    private readonly SettingsService _service;

    public ThemeSettingsPersistenceTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new DatabaseConnectionFactory(Path.Combine(_directory, "settings.db"));
        using var connection = factory.CreateConnection();
        connection.Execute("CREATE TABLE AppSettings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)");
        _repository = new SettingsRepository(factory);
        _service = new SettingsService(_repository, new TestProtector());
    }

    [Fact]
    public async Task FreshInstallationStartsWithStarlitArchive()
    {
        var settings = await _service.LoadAsync();
        Assert.Equal("starlit", settings.ThemeStyle);
        Assert.Equal("system", settings.ThemeMode);
    }

    [Theory]
    [InlineData("{}", "system")]
    [InlineData("{\"themeMode\":\"dark\",\"model\":\"example\"}", "dark")]
    [InlineData("{\"ThemeMode\":\" LIGHT \"}", "light")]
    public async Task ExistingSettingsWithoutStyleKeepTheOriginalTheme(string json, string expectedMode)
    {
        await _repository.SetAsync("ProviderSettings", json);
        var settings = await _service.LoadAsync();
        Assert.Equal("harbor", settings.ThemeStyle);
        Assert.Equal(expectedMode, settings.ThemeMode);
        await _service.SaveLanguageAsync("en-US");
        Assert.Equal("harbor", (await _service.LoadAsync()).ThemeStyle);
    }

    [Theory]
    [InlineData("{\"themeStyle\":\" HARBOR \",\"themeMode\":\" DARK \"}", "harbor", "dark")]
    [InlineData("{\"THEMESTYLE\":\"STARLIT\",\"themeMode\":\"light\"}", "starlit", "light")]
    [InlineData("{\"themeStyle\":\"unknown\",\"themeMode\":\"invalid\"}", "starlit", "system")]
    [InlineData("{\"themeStyle\":null}", "starlit", "system")]
    public async Task ThemeSettingsNormalizeWithoutInventingUnsupportedStyles(string json, string expectedStyle, string expectedMode)
    {
        await _repository.SetAsync("ProviderSettings", json);
        var settings = await _service.LoadAsync();
        Assert.Equal(expectedStyle, settings.ThemeStyle);
        Assert.Equal(expectedMode, settings.ThemeMode);
    }

    [Fact]
    public async Task ThemeSelectionSurvivesRestartWithoutChangingProviderOrSecret()
    {
        await _service.SaveAsync(new ProviderSettings { Model = "existing-model", LanguageCode = "en-US" }, "secret");
        await _service.SaveThemeAsync("dark", "harbor");
        var restartedService = new SettingsService(_repository, new TestProtector());
        var (settings, secret) = await restartedService.LoadResolvedAsync();
        Assert.Equal("harbor", settings.ThemeStyle);
        Assert.Equal("dark", settings.ThemeMode);
        Assert.Equal("existing-model", settings.Model);
        Assert.Equal("en-US", settings.LanguageCode);
        Assert.Equal("secret", secret);
    }

    [Fact]
    public async Task ConcurrentStyleAndModeChangesDoNotOverwriteEachOtherOrLanguage()
    {
        await _service.SaveAsync(new ProviderSettings { Model = "keep-model" }, "keep-secret");
        await Task.WhenAll(
            _service.SaveThemeStyleAsync("harbor"),
            _service.SaveThemeModeAsync("dark"),
            _service.SaveLanguageAsync("en-US"));
        var (settings, secret) = await _service.LoadResolvedAsync();
        Assert.Equal("harbor", settings.ThemeStyle);
        Assert.Equal("dark", settings.ThemeMode);
        Assert.Equal("en-US", settings.LanguageCode);
        Assert.Equal("keep-model", settings.Model);
        Assert.Equal("keep-secret", secret);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;
        public string Unprotect(string protectedText) => protectedText.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedText["protected:".Length..] : protectedText;
    }
}
