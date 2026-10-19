using System.Text.Json.Serialization;

namespace NativeTavern.Models;

public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Homepage { get; set; }
    public string? MinimumAppVersion { get; set; }
    public IReadOnlyList<string> Permissions { get; set; } = [];
    public IReadOnlyList<string> Capabilities { get; set; } = [];
}

public sealed class PluginCatalog
{
    public int FormatVersion { get; set; } = 1;
    public IReadOnlyList<MarketplacePlugin> Plugins { get; set; } = [];
}

public sealed class MarketplacePlugin
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Homepage { get; set; }
    public string? PackageUrl { get; set; }
    public string? Sha256 { get; set; }

    [JsonIgnore]
    public bool IsInstalled { get; set; }

    [JsonIgnore]
    public string InstallState => IsInstalled ? "已安装" : "尚未安装";

    [JsonIgnore]
    public bool CanInstall => !IsInstalled && !string.IsNullOrWhiteSpace(PackageUrl) && !string.IsNullOrWhiteSpace(Sha256);
}

public sealed class InstalledPlugin
{
    public required PluginManifest Manifest { get; init; }
    public required string DirectoryPath { get; init; }
    public bool IsEnabled { get; init; }
    public IReadOnlyList<string> UnsupportedCapabilities { get; init; } = [];
    public int SupportedCapabilityCount { get; init; }
    public string? ValidationError { get; init; }
    public string Name => Manifest.Name;
    public string Id => Manifest.Id;
    public string Version => Manifest.Version;
    public string Author => Manifest.Author;
    public string Description => Manifest.Description;
    public string StateText => !IsEnabled ? "已停用" : ValidationError is not null
        ? "配置无效" : SupportedCapabilityCount == 0 ? "已启用 · 无可用能力" : "已启用";
    public string CapabilityStatusText => UnsupportedCapabilities.Count == 0
        ? string.Empty
        : "当前版本不支持：" + string.Join("、", UnsupportedCapabilities);
    public string ValidationStatusText => ValidationError is null ? string.Empty : "插件数据无效：" + ValidationError;
    public string PermissionText => Manifest.Permissions.Count == 0
        ? "未声明权限"
        : "权限：" + string.Join("、", Manifest.Permissions);
}
