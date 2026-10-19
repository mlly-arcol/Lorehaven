using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class PluginServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NativeTavern-plugin-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InitializeCreatesAnEmptyCatalog()
    {
        var service = CreateService();

        await service.InitializeAsync();
        var catalog = await service.GetCatalogAsync();

        Assert.Empty(catalog);
        Assert.True(File.Exists(Path.Combine(root, "catalog.json")));
    }

    [Fact]
    public async Task CatalogSkipsNullEntriesAndRetainsValidListings()
    {
        var service = CreateService();
        await service.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "catalog.json"),
            """{"formatVersion":1,"plugins":[null,{"id":"store.plugin","name":"Store Plugin","version":"1.0.0"}]}""");

        var listing = Assert.Single(await service.GetCatalogAsync());

        Assert.Equal("store.plugin", listing.Id);
    }

    [Fact]
    public async Task InstallsTogglesAndUninstallsPackage()
    {
        var package = CreatePackage("sample.plugin", "1.2.0");
        var service = CreateService();

        var installed = await service.InstallPackageAsync(package);
        Assert.Equal("sample.plugin", installed.Id);
        Assert.True(File.Exists(Path.Combine(root, "sample.plugin", "plugin.json")));
        Assert.Single(await service.GetInstalledAsync());

        await service.SetEnabledAsync(installed.Id, false);
        Assert.False(Assert.Single(await service.GetInstalledAsync()).IsEnabled);
        Assert.Null(await service.GetStateWarningAsync());

        await service.UninstallAsync(installed.Id);
        Assert.Empty(await service.GetInstalledAsync());
    }

    [Fact]
    public async Task EnabledCapabilityFollowsPluginState()
    {
        var package = CreatePackageWithCapability("status.plugin", "1.0.0", PluginService.CharacterStatusCapability);
        var service = CreateService();
        var installed = await service.InstallPackageAsync(package);

        Assert.True(await service.IsCapabilityEnabledAsync(PluginService.CharacterStatusCapability));
        await service.SetEnabledAsync(installed.Id, false);
        Assert.False(await service.IsCapabilityEnabledAsync(PluginService.CharacterStatusCapability));
    }

    [Fact]
    public async Task DamagedMultiCapabilityPluginKeepsUserChoiceButLosesRuntimeCapabilities()
    {
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, "multi.ntplugin");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "plugin.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                id = "multi.plugin", name = "Multi", version = "1.0.0", author = "Tests",
                description = "Test plugin", permissions = Array.Empty<string>(),
                capabilities = new[]
                {
                    PluginService.CharacterStatusCapability,
                    PluginService.PromptResourcesCapability
                }
            }));
            WriteEntry(archive, "prompt-resources.json",
                "{\"formatVersion\":1,\"personas\":[],\"lorebooks\":[],\"presets\":[],\"textRules\":[]}");
        }

        var service = CreateService();
        await service.InstallPackageAsync(package);
        Assert.True(await service.IsCapabilityEnabledAsync(PluginService.CharacterStatusCapability));
        Assert.True(await service.IsCapabilityEnabledAsync(PluginService.PromptResourcesCapability));

        await File.WriteAllTextAsync(Path.Combine(root, "multi.plugin", "prompt-resources.json"), "{invalid");

        var displayed = Assert.Single(await service.GetInstalledAsync(validateContents: true));
        Assert.True(displayed.IsEnabled);
        Assert.NotNull(displayed.ValidationError);
        Assert.Equal("配置无效", displayed.StateText);
        Assert.False(await service.IsCapabilityEnabledAsync(PluginService.CharacterStatusCapability));
        Assert.False(await service.IsCapabilityEnabledAsync(PluginService.PromptResourcesCapability));
        Assert.Empty(await service.GetEnabledPluginsAsync(PluginService.CharacterStatusCapability));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("{\"status.plugin\":false,\"STATUS.PLUGIN\":true}")]
    public async Task CorruptStateDisablesAllPluginsAndBlocksStateChanges(string corruptState)
    {
        var service = CreateService();
        await service.InstallPackageAsync(CreatePackageWithCapability(
            "status.plugin", "1.0.0", PluginService.CharacterStatusCapability));
        await service.InstallPackageAsync(CreatePackage("other.plugin", "1.0.0"));
        await service.SetEnabledAsync("status.plugin", false);

        var statePath = Path.Combine(root, ".state.json");
        await File.WriteAllTextAsync(statePath, corruptState);

        var installed = await service.GetInstalledAsync();
        Assert.Equal(2, installed.Count);
        Assert.All(installed, plugin => Assert.False(plugin.IsEnabled));
        Assert.False(await service.IsCapabilityEnabledAsync(PluginService.CharacterStatusCapability));
        var warning = await service.GetStateWarningAsync();
        Assert.NotNull(warning);
        Assert.Contains("所有插件已临时停用", warning);

        var toggleError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SetEnabledAsync("other.plugin", true));
        Assert.Contains("状态文件", toggleError.Message);
        var uninstallError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.UninstallAsync("status.plugin"));
        Assert.Contains("状态文件", uninstallError.Message);
        Assert.Equal(corruptState, await File.ReadAllTextAsync(statePath));
        Assert.True(Directory.Exists(Path.Combine(root, "status.plugin")));
    }

    [Fact]
    public async Task UninstallHidesPluginBeforeNotifyingAndPreservesOtherStates()
    {
        var service = CreateService();
        await service.InstallPackageAsync(CreatePackage("removed.plugin", "1.0.0"));
        await service.InstallPackageAsync(CreatePackage("other.plugin", "1.0.0"));
        await service.SetEnabledAsync("other.plugin", false);

        var notificationSawHiddenPlugin = false;
        service.PluginsChanged += () =>
        {
            Assert.False(Directory.Exists(Path.Combine(root, "removed.plugin")));
            Assert.Single(Directory.EnumerateDirectories(root, ".pending-delete-*"));
            notificationSawHiddenPlugin = true;
        };

        await service.UninstallAsync("removed.plugin");

        Assert.True(notificationSawHiddenPlugin);
        Assert.Empty(Directory.EnumerateDirectories(root, ".pending-delete-*"));
        var remaining = Assert.Single(await service.GetInstalledAsync());
        Assert.Equal("other.plugin", remaining.Id);
        Assert.False(remaining.IsEnabled);
    }

    [Fact]
    public async Task UninstallRestoresVisiblePluginWhenStateWriteFails()
    {
        var service = CreateService();
        await service.InstallPackageAsync(CreatePackage("sample.plugin", "1.0.0"));
        await service.SetEnabledAsync("sample.plugin", false);

        var statePath = Path.Combine(root, ".state.json");
        using var lockState = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var failure = await Record.ExceptionAsync(() => service.UninstallAsync("sample.plugin"));
        Assert.True(failure is IOException or UnauthorizedAccessException,
            $"Expected a file access failure, got {failure?.GetType().Name ?? "none"}.");

        Assert.True(Directory.Exists(Path.Combine(root, "sample.plugin")));
        Assert.Empty(Directory.EnumerateDirectories(root, ".pending-delete-*"));
        Assert.False(Assert.Single(await service.GetInstalledAsync()).IsEnabled);
    }

    [Fact]
    public async Task BundledPluginPackagesCanBeInstalled()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Lorehaven.csproj")))
            directory = directory.Parent;
        if (directory is null) return; // Source packages are absent beside a published test binary.

        var service = CreateService();
        var packages = new (string Id, string Capability)[]
        {
            ("nativetavern.dynamic-character-status", PluginService.CharacterStatusCapability),
            ("nativetavern.story-prompts", PluginService.PromptResourcesCapability),
            ("nativetavern.dice-random-tables", PluginService.DiceRandomTablesCapability),
            ("nativetavern.story-timeline", PluginService.StoryTimelineCapability)
        };
        foreach (var (id, capability) in packages)
        {
            var path = Path.Combine(directory.FullName, "PluginPackages", id + ".ntplugin");
            Assert.True(File.Exists(path), $"Missing distributable plugin: {path}");
            var installed = await service.InstallPackageAsync(path);
            Assert.Equal(id, installed.Id);
            Assert.Contains(capability, installed.Manifest.Capabilities);
        }
        Assert.Equal(packages.Length, (await service.GetInstalledAsync(validateContents: true)).Count);
    }

    [Fact]
    public async Task UpdatingPackageReplacesOldFiles()
    {
        var service = CreateService();
        await service.InstallPackageAsync(CreatePackage("sample.plugin", "1.0.0", ("old.txt", "old")));

        await service.InstallPackageAsync(CreatePackage("sample.plugin", "2.0.0", ("new.txt", "new")));

        var installed = Assert.Single(await service.GetInstalledAsync());
        Assert.Equal("2.0.0", installed.Version);
        Assert.False(File.Exists(Path.Combine(root, "sample.plugin", "old.txt")));
        Assert.True(File.Exists(Path.Combine(root, "sample.plugin", "new.txt")));
    }

    [Fact]
    public async Task RejectsUnsafeArchivePaths()
    {
        var package = Path.Combine(root, Guid.NewGuid().ToString("N") + ".ntplugin");
        Directory.CreateDirectory(root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "plugin.json", Manifest("safe.plugin", "1.0.0"));
            WriteEntry(archive, "../outside.txt", "unsafe");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CreateService().InstallPackageAsync(package));
        Assert.Contains("不安全", error.Message);
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(root)!.FullName, "outside.txt")));
    }

    [Fact]
    public async Task MarketplaceInstallVerifiesAndInstallsDownloadedPackage()
    {
        var bytes = await File.ReadAllBytesAsync(CreatePackage("store.plugin", "1.0.0"));
        using var client = new HttpClient(new StaticResponseHandler(bytes));
        var service = new PluginService(root, client, NullLogger<PluginService>.Instance);
        var listing = new MarketplacePlugin
        {
            Id = "store.plugin",
            Name = "Store Plugin",
            Version = "1.0.0",
            PackageUrl = "https://plugins.example/store.ntplugin",
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };

        var installed = await service.InstallFromMarketplaceAsync(listing);

        Assert.Equal(listing.Id, installed.Id);
        Assert.True(File.Exists(Path.Combine(root, listing.Id, "plugin.json")));
    }

    [Theory]
    [InlineData("../bad")]
    [InlineData("UPPERCASE")]
    [InlineData("a")]
    public async Task RejectsInvalidPluginIds(string id)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateService().InstallPackageAsync(CreatePackage(id, "1.0.0")));
        Assert.Contains("插件 ID", error.Message);
    }

    private PluginService CreateService() => new(root, NullLogger<PluginService>.Instance);

    private string CreatePackage(string id, string version, params (string Name, string Content)[] files)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".ntplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "plugin.json", Manifest(id, version));
        foreach (var file in files) WriteEntry(archive, file.Name, file.Content);
        return path;
    }

    private string CreatePackageWithCapability(string id, string version, string capability)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".ntplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "plugin.json", Manifest(id, version, capability));
        return path;
    }

    private static string Manifest(string id, string version, string? capability = null) =>
        $$"""{"id":"{{id}}","name":"Sample","version":"{{version}}","author":"Tests","description":"Test plugin","permissions":[],"capabilities":[{{(capability is null ? "" : "\"" + capability + "\"")}}]}""";

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class StaticResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request
            });
    }
}
