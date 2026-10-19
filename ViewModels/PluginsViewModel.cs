using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using NativeTavern.Models;
using NativeTavern.Services;

namespace NativeTavern.ViewModels;

public partial class PluginsViewModel(PluginService pluginService, ILogger<PluginsViewModel> logger) : ObservableObject
{
    private IReadOnlyList<MarketplacePlugin> allMarketplace = [];
    private IReadOnlyList<InstalledPlugin> allInstalled = [];

    public ObservableCollection<MarketplacePlugin> MarketplacePlugins { get; } = [];
    public ObservableCollection<InstalledPlugin> InstalledPlugins { get; } = [];
    public string PluginsDirectory => pluginService.PluginsDirectory;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _stateWarning = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isMarketplaceSelected = true;
    [ObservableProperty] private bool _hasMarketplacePlugins;
    [ObservableProperty] private bool _hasInstalledPlugins;

    public Task InitializeAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            allInstalled = await pluginService.GetInstalledAsync(validateContents: true);
            StateWarning = await pluginService.GetStateWarningAsync() ?? string.Empty;
            allMarketplace = await pluginService.GetCatalogAsync();
            ApplyFilter();
            StatusMessage = string.IsNullOrEmpty(StateWarning) ? "插件目录已刷新。" : "插件状态文件需要恢复。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not refresh plugins.");
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    public async Task InstallPackageAsync(string path)
    {
        IsBusy = true;
        try
        {
            var plugin = await pluginService.InstallPackageAsync(path);
            StatusMessage = plugin.IsEnabled
                ? $"已安装并启用 {plugin.Name} {plugin.Version}。"
                : $"已更新 {plugin.Name} {plugin.Version}；仍保持停用。";
            await ReloadListsAsync();
            IsMarketplaceSelected = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Plugin installation failed for {PluginPackage}", path);
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task InstallMarketplaceAsync(MarketplacePlugin? plugin)
    {
        if (plugin is null || !plugin.CanInstall) return;
        IsBusy = true;
        try
        {
            var installed = await pluginService.InstallFromMarketplaceAsync(plugin);
            StatusMessage = $"已从商城安装 {installed.Name} {installed.Version}。";
            await ReloadListsAsync();
            IsMarketplaceSelected = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException)
        {
            logger.LogWarning(ex, "Marketplace installation failed for {PluginId}", plugin.Id);
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TogglePluginAsync(InstalledPlugin? plugin)
    {
        if (plugin is null) return;
        IsBusy = true;
        try
        {
            await pluginService.SetEnabledAsync(plugin.Id, !plugin.IsEnabled);
            StatusMessage = plugin.IsEnabled ? $"已停用 {plugin.Name}。" : $"已启用 {plugin.Name}。";
            await ReloadListsAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not change plugin state for {PluginId}", plugin.Id);
            StatusMessage = ex.Message;
            StateWarning = await pluginService.GetStateWarningAsync() ?? string.Empty;
        }
        finally { IsBusy = false; }
    }

    public async Task UninstallAsync(InstalledPlugin plugin)
    {
        IsBusy = true;
        try
        {
            await pluginService.UninstallAsync(plugin.Id);
            StatusMessage = $"已卸载 {plugin.Name}。";
            await ReloadListsAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not uninstall plugin {PluginId}", plugin.Id);
            StatusMessage = ex.Message;
            StateWarning = await pluginService.GetStateWarningAsync() ?? string.Empty;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void ShowMarketplace() => IsMarketplaceSelected = true;

    [RelayCommand]
    private void ShowInstalled() => IsMarketplaceSelected = false;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private async Task ReloadListsAsync()
    {
        allInstalled = await pluginService.GetInstalledAsync(validateContents: true);
        StateWarning = await pluginService.GetStateWarningAsync() ?? string.Empty;
        allMarketplace = await pluginService.GetCatalogAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        MarketplacePlugins.Clear();
        foreach (var plugin in allMarketplace.Where(x => Matches(x.Name, x.Id, x.Author, x.Description, query)))
            MarketplacePlugins.Add(plugin);
        InstalledPlugins.Clear();
        foreach (var plugin in allInstalled.Where(x => Matches(x.Name, x.Id, x.Author, x.Description, query)))
            InstalledPlugins.Add(plugin);
        HasMarketplacePlugins = MarketplacePlugins.Count > 0;
        HasInstalledPlugins = InstalledPlugins.Count > 0;
    }

    private static bool Matches(string? name, string? id, string? author, string? description, string query) =>
        string.IsNullOrEmpty(query) || new[] { name, id, author, description }
            .Any(x => x?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
}
