using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NativeTavern.Helpers;
using NativeTavern.Models;

namespace NativeTavern.Services;

public sealed partial class PluginService
{
    public const string CharacterStatusCapability = "character-status-v1";
    public const string PromptResourcesCapability = "prompt-resources-v1";
    public const string DiceRandomTablesCapability = "dice-random-tables-v1";
    public const string StoryTimelineCapability = "story-timeline-v1";
    private static readonly HashSet<string> SupportedCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        CharacterStatusCapability, PromptResourcesCapability, DiceRandomTablesCapability, StoryTimelineCapability
    };
    private const long MaxPackageBytes = 100L * 1024 * 1024;
    private const long MaxExpandedBytes = 500L * 1024 * 1024;
    private const int MaxEntries = 2_000;
    private const int MaxManifestBytes = 1024 * 1024;
    private readonly string root;
    private readonly string catalogPath;
    private readonly string statePath;
    private readonly ILogger<PluginService> logger;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim gate = new(1, 1);

    public event Action? PluginsChanged;

    public PluginService(HttpClient httpClient, ILogger<PluginService> logger) : this(AppPaths.PluginsDirectory, httpClient, logger) { }

    public PluginService(string pluginsDirectory, ILogger<PluginService> logger) : this(pluginsDirectory, new HttpClient(), logger) { }

    public PluginService(string pluginsDirectory, HttpClient httpClient, ILogger<PluginService> logger)
    {
        root = Path.GetFullPath(pluginsDirectory);
        catalogPath = Path.Combine(root, "catalog.json");
        statePath = Path.Combine(root, ".state.json");
        this.logger = logger;
        this.httpClient = httpClient;
    }

    public string PluginsDirectory => root;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        if (!File.Exists(catalogPath))
        {
            await WriteJsonAtomicAsync(catalogPath, new PluginCatalog());
        }
    }

    public async Task<IReadOnlyList<InstalledPlugin>> GetInstalledAsync(bool validateContents = false)
    {
        await InitializeAsync();
        var states = await ReadStatesAsync();
        var plugins = new List<InstalledPlugin>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            var manifestPath = Path.Combine(directory, "plugin.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = await ReadManifestAsync(manifestPath);
                ValidateManifest(manifest);
                if (!string.Equals(name, manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Ignoring plugin whose directory does not match its id: {PluginDirectory}", directory);
                    continue;
                }
                string? validationError = null;
                if (validateContents)
                {
                    try { await ValidatePackageContentsAsync(manifest, directory); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        validationError = ex.Message;
                        logger.LogWarning(ex, "Invalid data for plugin {PluginId}.", manifest.Id);
                    }
                }
                plugins.Add(CreateInstalledPlugin(manifest, directory,
                    states.IsReadable &&
                    (!states.Values.TryGetValue(manifest.Id, out var enabled) || enabled), validationError));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Ignoring invalid plugin at {PluginDirectory}", directory);
            }
        }
        return plugins.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<string?> GetStateWarningAsync()
    {
        await InitializeAsync();
        return (await ReadStatesAsync()).IsReadable
            ? null
            : "插件状态文件 .state.json 已损坏或无法读取，所有插件已临时停用。请在插件目录恢复此文件后刷新。";
    }

    public async Task<IReadOnlyList<MarketplacePlugin>> GetCatalogAsync()
    {
        await InitializeAsync();
        try
        {
            await using var stream = File.OpenRead(catalogPath);
            var catalog = await JsonSerializer.DeserializeAsync<PluginCatalog>(stream, JsonDefaults.Options)
                          ?? new PluginCatalog();
            if (catalog.FormatVersion != 1) throw new InvalidDataException("不支持此插件商城目录版本。");
            var installed = (await GetInstalledAsync()).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (catalog.Plugins ?? [])
                .Where(x => x is not null && IsValidId(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
                .Select(x =>
                {
                    x.Id ??= string.Empty;
                    x.Name ??= string.Empty;
                    x.Version ??= string.Empty;
                    x.Author ??= string.Empty;
                    x.Description ??= string.Empty;
                    x.IsInstalled = installed.Contains(x.Id);
                    return x;
                })
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("插件商城目录格式无效。", ex);
        }
    }

    public async Task<InstalledPlugin> InstallPackageAsync(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("找不到插件安装包。", packagePath);
        var file = new FileInfo(packagePath);
        if (file.Length > MaxPackageBytes) throw new InvalidDataException("插件安装包不能超过 100 MB。");
        if (file.Extension.ToLowerInvariant() is not (".zip" or ".ntplugin"))
            throw new InvalidDataException("仅支持 .ntplugin 和 .zip 插件安装包。");

        await gate.WaitAsync();
        string? staging = null;
        string? backup = null;
        try
        {
            await InitializeAsync();
            using var archive = ZipFile.OpenRead(packagePath);
            ValidateArchive(archive);
            var manifest = await ReadManifestAsync(archive);

            staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            ExtractArchive(archive, staging);
            await ValidatePackageContentsAsync(manifest, staging);
            var destination = Path.Combine(root, manifest.Id);
            EnsureDirectChild(destination);
            if (Directory.Exists(destination))
            {
                backup = Path.Combine(root, ".backup-" + Guid.NewGuid().ToString("N"));
                Directory.Move(destination, backup);
            }
            try
            {
                Directory.Move(staging, destination);
                staging = null;
            }
            catch
            {
                if (backup is not null && Directory.Exists(backup) && !Directory.Exists(destination))
                    Directory.Move(backup, destination);
                throw;
            }
            if (backup is not null) TryDeleteDirectory(backup);
            logger.LogInformation("Installed plugin {PluginId} version {PluginVersion}", manifest.Id, manifest.Version);
            PluginsChanged?.Invoke();
            var states = await ReadStatesAsync();
            return CreateInstalledPlugin(manifest, destination,
                states.IsReadable &&
                (!states.Values.TryGetValue(manifest.Id, out var enabled) || enabled));
        }
        finally
        {
            if (staging is not null) TryDeleteDirectory(staging);
            gate.Release();
        }
    }

    public async Task<InstalledPlugin> InstallFromMarketplaceAsync(MarketplacePlugin plugin)
    {
        if (!Uri.TryCreate(plugin.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("商城插件必须使用 HTTPS 下载地址。");
        var expectedHash = plugin.Sha256?.Trim();
        if (expectedHash is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("商城插件缺少有效的 SHA-256 校验值。");

        var temporary = Path.Combine(Path.GetTempPath(), "Lorehaven-plugin-" + Guid.NewGuid().ToString("N") + ".ntplugin");
        try
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxPackageBytes)
                throw new InvalidDataException("插件安装包不能超过 100 MB。");
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes) throw new InvalidDataException("插件安装包不能超过 100 MB。");
                    await destination.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            await using var package = File.OpenRead(temporary);
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(package));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件安装包的 SHA-256 校验失败。");
            using (var archive = ZipFile.OpenRead(temporary))
            {
                ValidateArchive(archive);
                var manifest = await ReadManifestAsync(archive);
                if (!string.Equals(manifest.Id, plugin.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(manifest.Version, plugin.Version, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载的插件身份或版本与商城目录不一致。");
            }
            return await InstallPackageAsync(temporary);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled)
    {
        await gate.WaitAsync();
        try
        {
            var directory = GetPluginDirectory(pluginId);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("找不到已安装插件。");
            var states = RequireWritableStates(await ReadStatesAsync());
            if (enabled)
            {
                var manifest = await ReadManifestAsync(Path.Combine(directory, "plugin.json"));
                ValidateManifest(manifest);
                if (!string.Equals(manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("插件目录与清单 ID 不一致。");
                await ValidatePackageContentsAsync(manifest, directory);
            }
            states[pluginId] = enabled;
            await WriteJsonAtomicAsync(statePath, states);
            logger.LogInformation("Plugin {PluginId} enabled state changed to {Enabled}", pluginId, enabled);
            PluginsChanged?.Invoke();
        }
        finally { gate.Release(); }
    }

    public async Task UninstallAsync(string pluginId)
    {
        await gate.WaitAsync();
        try
        {
            var directory = GetPluginDirectory(pluginId);
            var states = RequireWritableStates(await ReadStatesAsync());
            string? pendingDeletion = null;
            if (Directory.Exists(directory))
            {
                pendingDeletion = Path.Combine(root, ".pending-delete-" + Guid.NewGuid().ToString("N"));
                Directory.Move(directory, pendingDeletion);
            }
            try
            {
                if (states.Remove(pluginId)) await WriteJsonAtomicAsync(statePath, states);
            }
            catch (Exception stateError)
            {
                if (pendingDeletion is not null && Directory.Exists(pendingDeletion))
                {
                    try { Directory.Move(pendingDeletion, directory); }
                    catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                    {
                        logger.LogError(restoreError,
                            "Could not restore plugin {PluginId} after uninstall state update failed.", pluginId);
                        throw new IOException($"卸载未完成，插件目录保留在 {pendingDeletion}。请恢复后重试。",
                            new AggregateException(stateError, restoreError));
                    }
                }
                throw;
            }
            logger.LogInformation("Uninstalled plugin {PluginId}", pluginId);
            try { PluginsChanged?.Invoke(); }
            finally { if (pendingDeletion is not null) TryDeleteDirectory(pendingDeletion); }
        }
        finally { gate.Release(); }
    }

    private string GetPluginDirectory(string pluginId)
    {
        if (!IsValidId(pluginId)) throw new InvalidDataException("插件 ID 无效。");
        var directory = Path.Combine(root, pluginId);
        EnsureDirectChild(directory);
        return directory;
    }

    private void EnsureDirectChild(string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) ||
            relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("插件路径不安全。");
    }

    private async Task<PluginStates> ReadStatesAsync()
    {
        try
        {
            await using var stream = File.OpenRead(statePath);
            var values = await JsonSerializer.DeserializeAsync<Dictionary<string, bool>>(stream, JsonDefaults.Options);
            if (values is null) throw new InvalidDataException("插件状态文件内容为空。");
            return new(new Dictionary<string, bool>(values, StringComparer.OrdinalIgnoreCase), true);
        }
        catch (FileNotFoundException)
        {
            return new(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Plugin state file is invalid or unreadable; all plugins will be disabled until it is repaired.");
            return new(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), false);
        }
    }

    private static Dictionary<string, bool> RequireWritableStates(PluginStates states) =>
        states.IsReadable
            ? states.Values
            : throw new InvalidDataException("插件状态文件 .state.json 已损坏或无法读取。所有插件已暂时停用；请先恢复此文件，再修改或卸载插件。");

    private sealed record PluginStates(Dictionary<string, bool> Values, bool IsReadable);

    private static async Task<PluginManifest> ReadManifestAsync(string path)
    {
        if (new FileInfo(path).Length > MaxManifestBytes) throw new InvalidDataException("插件清单过大。");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PluginManifest>(stream, JsonDefaults.Options)
               ?? throw new InvalidDataException("插件清单为空。");
    }

    private static async Task<PluginManifest> ReadManifestAsync(ZipArchive archive)
    {
        var manifestEntry = archive.Entries.SingleOrDefault(x =>
            string.Equals(x.FullName.Replace('\\', '/'), "plugin.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null) throw new InvalidDataException("插件包根目录缺少 plugin.json。");
        if (manifestEntry.Length > MaxManifestBytes) throw new InvalidDataException("插件清单过大。");
        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(manifestStream, JsonDefaults.Options)
                       ?? throw new InvalidDataException("插件清单为空。");
        ValidateManifest(manifest);
        return manifest;
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (!IsValidId(manifest.Id)) throw new InvalidDataException("插件 ID 只能包含小写字母、数字、点、短横线和下划线。");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 80)
            throw new InvalidDataException("插件名称不能为空且不能超过 80 个字符。");
        if (!Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("插件版本必须是有效版本号。");
        if (!string.IsNullOrWhiteSpace(manifest.MinimumAppVersion))
        {
            if (!Version.TryParse(manifest.MinimumAppVersion, out var minimumVersion))
                throw new InvalidDataException("最低应用版本格式无效。");
            var applicationVersion = Helpers.AppVersion.Current;
            if (minimumVersion > applicationVersion)
                throw new InvalidDataException($"此插件需要 Lorehaven {minimumVersion} 或更高版本。");
        }
        manifest.Id ??= string.Empty;
        manifest.Name ??= string.Empty;
        manifest.Version ??= string.Empty;
        manifest.Author ??= string.Empty;
        manifest.Description ??= string.Empty;
        manifest.Permissions ??= [];
        manifest.Capabilities ??= [];
        if (manifest.Description.Length > 2_000 || manifest.Author.Length > 120)
            throw new InvalidDataException("插件清单文字过长。");
        if (manifest.Permissions.Count > 50)
            throw new InvalidDataException("插件声明的权限不能超过 50 项。");
        if (manifest.Permissions.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("插件权限不能包含空白项。");
        if (manifest.Permissions.Any(x => x.Length > 80))
            throw new InvalidDataException("插件权限文字不能超过 80 个字符。");
        if (manifest.Capabilities.Count > 20)
            throw new InvalidDataException("插件声明的能力不能超过 20 项。");
        if (manifest.Capabilities.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("插件能力不能包含空白项。");
        if (manifest.Capabilities.Any(x => x.Length > 80))
            throw new InvalidDataException("插件能力名称不能超过 80 个字符。");
    }

    public static bool IsSupportedCapability(string capability) => SupportedCapabilities.Contains(capability);

    public async Task<IReadOnlyList<InstalledPlugin>> GetEnabledPluginsAsync(string capability) =>
        !IsSupportedCapability(capability)
            ? []
            : (await GetInstalledAsync(validateContents: true)).Where(x => x.IsEnabled &&
                x.ValidationError is null &&
                x.Manifest.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)).ToArray();

    public async Task<bool> IsCapabilityEnabledAsync(string capability) =>
        (await GetEnabledPluginsAsync(capability)).Count > 0;

    private static InstalledPlugin CreateInstalledPlugin(
        PluginManifest manifest, string directory, bool enabled, string? validationError = null) => new()
    {
        Manifest = manifest,
        DirectoryPath = directory,
        IsEnabled = enabled,
        UnsupportedCapabilities = manifest.Capabilities.Where(x => !IsSupportedCapability(x)).ToArray(),
        SupportedCapabilityCount = manifest.Capabilities.Count(IsSupportedCapability),
        ValidationError = validationError
    };

    private static async Task ValidatePackageContentsAsync(PluginManifest manifest, string directory)
    {
        await PluginPromptResourceService.ValidatePackageDirectoryAsync(manifest, directory);
        await DiceTableService.ValidatePackageDirectoryAsync(manifest, directory);
    }

    private static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && IdPattern().IsMatch(id);

    private void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove temporary plugin directory {PluginDirectory}", path);
        }
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("插件包包含过多文件。");
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > MaxExpandedBytes - expanded) throw new InvalidDataException("插件解压后超过 500 MB。");
            expanded += entry.Length;
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) || Path.IsPathRooted(entry.FullName))
                throw new InvalidDataException("插件包包含不安全路径。");
            if (!names.Add(normalized)) throw new InvalidDataException("插件包包含重复路径。");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("插件包不能包含符号链接。");
        }
    }

    private static void ExtractArchive(ZipArchive archive, string destination)
    {
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件包包含不安全路径。");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(output);
        }
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
