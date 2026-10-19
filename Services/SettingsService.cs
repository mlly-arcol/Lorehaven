using System.Text.Json;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Models;
using NativeTavern.Security;

namespace NativeTavern.Services;

public sealed class SettingsService(SettingsRepository repository, ISecretProtector protector)
{
    private const string ProviderSettingsKey = "ProviderSettings";
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task<ProviderSettings> LoadAsync()
    {
        var json = await repository.GetAsync(ProviderSettingsKey);
        if (string.IsNullOrWhiteSpace(json)) return new ProviderSettings();
        var settings = JsonSerializer.Deserialize<ProviderSettings>(json, JsonDefaults.Options);
        if (settings is null) return new ProviderSettings();
        using var document = JsonDocument.Parse(json);
        // An existing installation keeps its original visual style on upgrade. A fresh
        // installation, or an explicitly invalid value, uses Lorehaven's new theme.
        if (!document.RootElement.EnumerateObject().Any(property =>
                property.Name.Equals(nameof(ProviderSettings.ThemeStyle), StringComparison.OrdinalIgnoreCase)))
            settings.ThemeStyle = ThemeService.HarborStyle;
        NormalizeTheme(settings);
        return settings;
    }

    public async Task<(ProviderSettings Settings, string ApiKey)> LoadResolvedAsync()
    {
        var settings = await LoadAsync();
        return (settings, protector.Unprotect(settings.ApiKeyEncrypted));
    }

    public async Task SaveAsync(ProviderSettings settings, string? apiKey, bool clearApiKey = false)
    {
        await _writeLock.WaitAsync();
        try
        {
            var existing = await LoadAsync();
            if (clearApiKey)
                settings.ApiKeyEncrypted = string.Empty;
            else if (!string.IsNullOrWhiteSpace(apiKey))
                settings.ApiKeyEncrypted = protector.Protect(apiKey);
            else
                settings.ApiKeyEncrypted = existing.ApiKeyEncrypted;
            await WriteAsync(settings);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveLanguageAsync(string languageCode)
    {
        await UpdateAsync(settings => settings.LanguageCode = Helpers.LanguageCodes.Normalize(languageCode));
    }

    public async Task SaveThemeModeAsync(string themeMode)
    {
        await UpdateAsync(settings => settings.ThemeMode = themeMode);
    }

    public Task SaveThemeStyleAsync(string themeStyle) =>
        UpdateAsync(settings => settings.ThemeStyle = themeStyle);

    public Task SaveThemeAsync(string themeMode, string themeStyle) =>
        UpdateAsync(settings =>
        {
            settings.ThemeMode = themeMode;
            settings.ThemeStyle = themeStyle;
        });

    private async Task UpdateAsync(Action<ProviderSettings> update)
    {
        await _writeLock.WaitAsync();
        try
        {
            var settings = await LoadAsync();
            update(settings);
            await WriteAsync(settings);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Task WriteAsync(ProviderSettings settings)
    {
        NormalizeTheme(settings);
        return repository.SetAsync(ProviderSettingsKey, JsonSerializer.Serialize(settings, JsonDefaults.Options));
    }

    private static void NormalizeTheme(ProviderSettings settings)
    {
        settings.ThemeMode = ThemeService.ToCode(ThemeService.ParseMode(settings.ThemeMode));
        settings.ThemeStyle = ThemeService.NormalizeStyle(settings.ThemeStyle);
    }
}
