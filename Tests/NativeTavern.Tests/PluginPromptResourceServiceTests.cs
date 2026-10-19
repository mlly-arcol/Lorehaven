using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class PluginPromptResourceServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NativeTavern-prompt-plugin-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SelectionAndEnableStateControlSectionsWithoutChangingUserPersona()
    {
        Directory.CreateDirectory(root);
        var factory = new DatabaseConnectionFactory(Path.Combine(root, "data.db"));
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var repository = new PromptRepository(factory);
        await repository.SavePersonaAsync(new Persona { Name = "My persona", Content = "My own text" });

        var plugins = CreatePluginService();
        await plugins.InstallPackageAsync(CreatePackage(CreatePack()));
        var resources = new PluginPromptResourceService(plugins, NullLogger<PluginPromptResourceService>.Instance);
        var listed = await resources.GetAvailableResourcesAsync();
        Assert.Equal(4, listed.Count);
        Assert.All(listed, resource => Assert.False(resource.AutoApply));
        Assert.Contains(listed, resource => resource.QualifiedId == "sample.prompts/narrator");

        var session = new ChatSession { IsGroupChat = false };
        var history = new[] { new ChatMessage { Role = ChatRole.User, Content = "去灯塔" } };
        Assert.Empty(await resources.GetActiveSectionsAsync(session, history));

        var selected = new[] { "sample.prompts/narrator", "sample.prompts/harbor", "sample.prompts/continuity", "sample.prompts/dialogue" };
        var active = await resources.GetActiveSectionsAsync(session, history, selected);
        Assert.Equal(4, active.Count); // persona, two preset parts, matching world entry
        Assert.Contains(active, section => section.Heading.Contains("World Info") && section.Content.Contains("灯塔"));
        Assert.DoesNotContain(active, section => section.Heading.Contains("Text Rule")); // group scope

        await plugins.SetEnabledAsync("sample.prompts", false);
        Assert.Empty(await resources.GetActiveSectionsAsync(session, history, selected));
        Assert.All(await resources.GetAvailableResourcesAsync(), resource => Assert.False(resource.IsEnabled));
        Assert.Single(await repository.GetPersonasAsync());

        await plugins.SetEnabledAsync("sample.prompts", true);
        Assert.Equal(4, (await resources.GetActiveSectionsAsync(session, history, selected)).Count);
        await plugins.UninstallAsync("sample.prompts");
        Assert.Empty(await resources.GetActiveSectionsAsync(session, history, selected));
        Assert.Single(await repository.GetPersonasAsync());
    }

    [Fact]
    public async Task WorldEntriesUseRecentHistoryAndGroupScopedRule()
    {
        var plugins = CreatePluginService();
        await plugins.InstallPackageAsync(CreatePackage(CreatePack()));
        var resources = new PluginPromptResourceService(plugins, NullLogger<PluginPromptResourceService>.Instance);
        var session = new ChatSession { IsGroupChat = true };
        var selected = new[] { "sample.prompts/harbor", "sample.prompts/dialogue" };

        var history = new[]
        {
            new ChatMessage { Role = ChatRole.User, Content = "旧灯塔在哪里？" },
            new ChatMessage { Role = ChatRole.Assistant, Content = "先去码头。" },
            new ChatMessage { Role = ChatRole.User, Content = "现在去船上。" }
        };
        var active = await resources.GetActiveSectionsAsync(session, history, selected);
        Assert.Single(active);
        Assert.Contains("Text Rule", active[0].Heading);

        history[^1].Content = "现在去灯塔。";
        active = await resources.GetActiveSectionsAsync(session, history, selected);
        Assert.Equal(2, active.Count);
        Assert.Contains(active, section => section.Heading.Contains("World Info"));
    }

    [Fact]
    public async Task ExplicitAutomaticResourceAppliesOnlyInMatchingScope()
    {
        var pack = CreatePack();
        pack.TextRules[0].AutoApply = true;
        var plugins = CreatePluginService();
        await plugins.InstallPackageAsync(CreatePackage(pack));
        var resources = new PluginPromptResourceService(plugins, NullLogger<PluginPromptResourceService>.Instance);

        Assert.Empty(await resources.GetActiveSectionsAsync(new ChatSession(), []));
        var groupSections = await resources.GetActiveSectionsAsync(new ChatSession { IsGroupChat = true }, []);
        Assert.Single(groupSections);
        Assert.Equal("dialogue", groupSections[0].ResourceId);
    }

    [Fact]
    public async Task ValidationRejectsMissingFileAndMalformedResources()
    {
        var manifest = Manifest();
        var directory = Path.Combine(root, "staged");
        Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PluginPromptResourceService.ValidatePackageDirectoryAsync(manifest, directory));

        var duplicate = CreatePack();
        duplicate.TextRules[0].Id = "narrator";
        await WritePackAsync(directory, duplicate);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PluginPromptResourceService.ValidatePackageDirectoryAsync(manifest, directory));

        var oversized = CreatePack();
        oversized.Personas[0].Content = new string('x', 4001);
        await WritePackAsync(directory, oversized);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PluginPromptResourceService.ValidatePackageDirectoryAsync(manifest, directory));

        await WritePackAsync(directory, CreatePack());
        await PluginPromptResourceService.ValidatePackageDirectoryAsync(manifest, directory);
        await PluginPromptResourceService.ValidatePackageDirectoryAsync(
            new PluginManifest { Capabilities = [] }, Path.Combine(root, "does-not-exist"));
    }

    [Fact]
    public async Task InvalidUpgradeDoesNotReplaceWorkingResources()
    {
        var plugins = CreatePluginService();
        await plugins.InstallPackageAsync(CreatePackage(CreatePack()));
        var invalid = CreatePack();
        invalid.TextRules[0].Id = "narrator";

        await Assert.ThrowsAsync<InvalidDataException>(() => plugins.InstallPackageAsync(CreatePackage(invalid)));

        var resources = new PluginPromptResourceService(plugins, NullLogger<PluginPromptResourceService>.Instance);
        var listed = await resources.GetAvailableResourcesAsync();
        Assert.Equal(4, listed.Count);
        Assert.Contains(listed, resource => resource.QualifiedId == "sample.prompts/narrator");
    }

    [Fact]
    public async Task InvalidSiblingCapabilityDisablesPromptResourcesUntilRepaired()
    {
        var plugins = CreatePluginService();
        var installed = await plugins.InstallPackageAsync(CreatePackage(CreatePack()));
        var manifest = Manifest();
        manifest.Capabilities = [.. manifest.Capabilities, DiceTableService.Capability];
        await File.WriteAllTextAsync(Path.Combine(installed.DirectoryPath, "plugin.json"),
            JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        var tablesPath = Path.Combine(installed.DirectoryPath, DiceTableService.TablesFileName);
        const string validTables = """{"formatVersion":1,"tables":[{"id":"weather","name":"Weather","entries":[{"text":"Rain","weight":1}]}]}""";
        await File.WriteAllTextAsync(tablesPath, validTables);
        var resources = new PluginPromptResourceService(plugins, NullLogger<PluginPromptResourceService>.Instance);
        var selected = new[] { "sample.prompts/narrator" };
        Assert.Single(await resources.GetActiveSectionsAsync(new ChatSession(), [], selected));

        await File.WriteAllTextAsync(tablesPath, "{invalid");

        Assert.False(await plugins.IsCapabilityEnabledAsync(PluginPromptResourceService.Capability));
        Assert.Empty(await resources.GetActiveSectionsAsync(new ChatSession(), [], selected));
        Assert.All(await resources.GetAvailableResourcesAsync(), resource => Assert.False(resource.IsEnabled));

        await File.WriteAllTextAsync(tablesPath, validTables);
        Assert.Single(await resources.GetActiveSectionsAsync(new ChatSession(), [], selected));
    }

    private PluginService CreatePluginService() =>
        new(Path.Combine(root, "plugins"), NullLogger<PluginService>.Instance);

    private string CreatePackage(PluginPromptResourcePack pack)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".ntplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "plugin.json", JsonSerializer.Serialize(Manifest(), JsonDefaults.Options));
        WriteEntry(archive, PluginPromptResourceService.DataFileName, JsonSerializer.Serialize(pack, JsonDefaults.Options));
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static Task WritePackAsync(string directory, PluginPromptResourcePack pack) =>
        File.WriteAllTextAsync(Path.Combine(directory, PluginPromptResourceService.DataFileName),
            JsonSerializer.Serialize(pack, JsonDefaults.Options));

    private static PluginManifest Manifest() => new()
    {
        Id = "sample.prompts",
        Name = "Sample prompt pack",
        Version = "1.0.0",
        Capabilities = [PluginPromptResourceService.Capability]
    };

    private static PluginPromptResourcePack CreatePack() => new()
    {
        FormatVersion = 1,
        Personas = [new PluginPackPersona { Id = "narrator", Name = "Narrator", Content = "Write clearly." }],
        Lorebooks = [new PluginPackLorebook
        {
            Id = "harbor", Name = "Harbor", Entries =
            [new PluginPackLoreEntry { Id = "lighthouse", Name = "Lighthouse", Keywords = "灯塔", Content = "灯塔在北边。", Depth = 1 }]
        }],
        Presets = [new PluginPackPreset { Id = "continuity", Name = "Continuity", SystemPrompt = "Remember the scene.", MainPrompt = "Move one beat." }],
        TextRules = [new PluginPackTextRule { Id = "dialogue", Name = "Dialogue", Content = "Label speakers.", Scope = "group" }]
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
    }
}
