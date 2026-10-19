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
using NativeTavern.ViewModels;
using NativeTavern.Views;
using Xunit;

namespace NativeTavern.Tests;

/// <summary>Verifies that saved per-chat choices reach the prompt without owning the user's resources.</summary>
public sealed class PluginPromptSelectionIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "nt-plugin-selection-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SelectionsArePerSessionAndChangingOneDoesNotChangeAnother()
    {
        var setup = await InitializeAsync();
        var first = await CreateSessionAsync(setup.Sessions);
        var second = await CreateSessionAsync(setup.Sessions);
        await setup.Selections.SetAsync(first.Id, ["sample.integration/rule-a"]);
        await setup.Selections.SetAsync(second.Id, ["sample.integration/rule-b"]);

        Assert.Equal("sample.integration/rule-a", Assert.Single(await setup.Selections.GetAsync(first.Id)));
        Assert.Equal("sample.integration/rule-b", Assert.Single(await setup.Selections.GetAsync(second.Id)));

        var firstPrompt = await BuildSystemTextAsync(setup.Prompts, first);
        var secondPrompt = await BuildSystemTextAsync(setup.Prompts, second);
        Assert.Contains("PLUGIN_RULE_ALPHA", firstPrompt);
        Assert.DoesNotContain("PLUGIN_RULE_BETA", firstPrompt);
        Assert.Contains("PLUGIN_RULE_BETA", secondPrompt);
        Assert.DoesNotContain("PLUGIN_RULE_ALPHA", secondPrompt);

        await setup.Selections.SetAsync(first.Id, ["sample.integration/rule-b"]);
        Assert.Equal("sample.integration/rule-b", Assert.Single(await setup.Selections.GetAsync(first.Id)));
        Assert.Equal("sample.integration/rule-b", Assert.Single(await setup.Selections.GetAsync(second.Id)));
        Assert.DoesNotContain("PLUGIN_RULE_ALPHA", await BuildSystemTextAsync(setup.Prompts, first));
    }

    [Fact]
    public async Task DisablingOrUninstallingPluginRemovesItsEffectButKeepsUserPersonaAndSelection()
    {
        var setup = await InitializeAsync();
        var persona = new Persona { Name = "My persona", Content = "USER_PERSONA_KEEP" };
        await setup.UserPrompts.SavePersonaAsync(persona);
        var session = await CreateSessionAsync(setup.Sessions, persona.Id);
        await setup.Selections.SetAsync(session.Id, ["sample.integration/rule-a"]);

        var enabled = await BuildSystemTextAsync(setup.Prompts, session);
        Assert.Contains("USER_PERSONA_KEEP", enabled);
        Assert.Contains("PLUGIN_RULE_ALPHA", enabled);

        await setup.Plugins.SetEnabledAsync("sample.integration", false);
        var disabled = await BuildSystemTextAsync(setup.Prompts, session);
        Assert.Contains("USER_PERSONA_KEEP", disabled);
        Assert.DoesNotContain("PLUGIN_RULE_ALPHA", disabled);
        Assert.Equal("sample.integration/rule-a", Assert.Single(await setup.Selections.GetAsync(session.Id)));
        Assert.Equal(persona.Id, Assert.Single(await setup.UserPrompts.GetPersonasAsync()).Id);

        await setup.Plugins.SetEnabledAsync("sample.integration", true);
        Assert.Contains("PLUGIN_RULE_ALPHA", await BuildSystemTextAsync(setup.Prompts, session));

        await setup.Plugins.UninstallAsync("sample.integration");
        var uninstalled = await BuildSystemTextAsync(setup.Prompts, session);
        Assert.Contains("USER_PERSONA_KEEP", uninstalled);
        Assert.DoesNotContain("PLUGIN_RULE_ALPHA", uninstalled);
        Assert.Equal("sample.integration/rule-a", Assert.Single(await setup.Selections.GetAsync(session.Id)));
        Assert.Equal(persona.Id, Assert.Single(await setup.UserPrompts.GetPersonasAsync()).Id);
    }

    [Fact]
    public async Task ApplyingResourceDialogWhilePluginIsDisabledKeepsSavedChoice()
    {
        using var harness = new ChatViewModelHarness(directory);
        var plugins = new PluginService(Path.Combine(directory, "plugins"), NullLogger<PluginService>.Instance);
        await plugins.InstallPackageAsync(CreatePackage());
        var resources = new PluginPromptResourceService(plugins, NullLogger<PluginPromptResourceService>.Instance);
        var selections = new PluginResourceSelectionRepository(harness.Factory);
        var viewModel = CreateViewModel(harness, plugins, resources, selections);
        await viewModel.InitializeAsync();
        var session = Assert.IsType<ChatSession>(viewModel.CurrentSession);

        await viewModel.SaveSelectedPluginPromptResourcesAsync(["sample.integration/rule-a"]);
        await plugins.SetEnabledAsync("sample.integration", false);
        Assert.Contains(await viewModel.GetPluginPromptResourcesAsync(), item =>
            item.QualifiedId == "sample.integration/rule-a" && !item.IsEnabled);

        // The disabled checkbox is still checked when the dialog returns its SelectedIds.
        await viewModel.SaveSelectedPluginPromptResourcesAsync(["sample.integration/rule-a"]);
        Assert.Equal("sample.integration/rule-a",
            Assert.Single(await selections.GetAsync(session.Id)));

        await plugins.SetEnabledAsync("sample.integration", true);
        var active = await resources.GetActiveSectionsAsync(session, [], await selections.GetAsync(session.Id));
        Assert.Contains(active, section => section.Content == "PLUGIN_RULE_ALPHA");

        var groupOnly = Assert.Single(await resources.GetAvailableResourcesAsync(),
            item => item.ResourceId == "rule-group");
        Assert.Equal("group", groupOnly.Scope);
        var singleChoice = new PluginPromptResourcesDialog.ResourceChoice(groupOnly, false, false, false);
        Assert.False(singleChoice.CanSelect);
        Assert.Contains("仅适用于群聊", singleChoice.StateLabel);
        Assert.True(new PluginPromptResourcesDialog.ResourceChoice(groupOnly, false, true, true).CanSelect);
        await viewModel.SaveSelectedPluginPromptResourcesAsync([groupOnly.QualifiedId]);
        Assert.Empty(await selections.GetAsync(session.Id));
    }

    [Fact]
    public async Task ResourceDialogCannotApplyOldConversationChoicesToTheNewConversation()
    {
        using var harness = new ChatViewModelHarness(directory);
        var first = await harness.CreateSessionAsync("First");
        var second = await harness.CreateSessionAsync("Second", DateTimeOffset.UtcNow.AddMinutes(-1));
        var plugins = new PluginService(Path.Combine(directory, "plugins"), NullLogger<PluginService>.Instance);
        await plugins.InstallPackageAsync(CreatePackage());
        var resources = new PluginPromptResourceService(plugins, NullLogger<PluginPromptResourceService>.Instance);
        var selections = new PluginResourceSelectionRepository(harness.Factory);
        await selections.SetAsync(first.Id, ["sample.integration/rule-a"]);
        await selections.SetAsync(second.Id, ["sample.integration/rule-b"]);
        var viewModel = CreateViewModel(harness, plugins, resources, selections);
        await viewModel.InitializeAsync();
        Assert.Equal(first.Id, viewModel.CurrentSession?.Id);
        var oldDialogChoices = await viewModel.GetSelectedPluginPromptResourceIdsAsync();

        await viewModel.LoadSessionCoreAsync(second);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.SaveSelectedPluginPromptResourcesAsync(oldDialogChoices, first.Id));

        Assert.Contains("对话已切换", error.Message);
        Assert.Equal("sample.integration/rule-a", Assert.Single(await selections.GetAsync(first.Id)));
        Assert.Equal("sample.integration/rule-b", Assert.Single(await selections.GetAsync(second.Id)));
        await viewModel.SaveSelectedPluginPromptResourcesAsync(["sample.integration/rule-a"], second.Id);
        Assert.Equal("sample.integration/rule-a", Assert.Single(await selections.GetAsync(second.Id)));
    }

    private static ChatViewModel CreateViewModel(
        ChatViewModelHarness harness, PluginService plugins, PluginPromptResourceService resources,
        PluginResourceSelectionRepository selections)
    {
        var factory = harness.Factory;
        var messages = new ChatMessageRepository(factory);
        var characters = new CharacterRepository(factory);
        var settings = new SettingsService(new SettingsRepository(factory), new NoopProtector());
        return new ChatViewModel(
            harness.Service, new PromptRepository(factory), harness.Drafts, settings, harness.Scripts,
            new CharacterStatusService(new CharacterStatusRepository(factory), messages, characters,
                settings, plugins, harness.Provider, NullLogger<CharacterStatusService>.Instance),
            new ReplySuggestionService(messages, settings, harness.Provider,
                NullLogger<ReplySuggestionService>.Instance),
            plugins, new TrayService(), harness.Memory,
            new ConversationTitleService(messages, settings, harness.Provider),
            NullLogger<ChatViewModel>.Instance,
            pluginPromptResources: resources, pluginResourceSelections: selections);
    }

    private async Task<Setup> InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var factory = new DatabaseConnectionFactory(Path.Combine(directory, "data.db"));
        await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        var userPrompts = new PromptRepository(factory);
        var sessions = new ChatSessionRepository(factory);
        var selections = new PluginResourceSelectionRepository(factory);
        var plugins = new PluginService(Path.Combine(directory, "plugins"), NullLogger<PluginService>.Instance);
        await plugins.InstallPackageAsync(CreatePackage());
        var resources = new PluginPromptResourceService(plugins, NullLogger<PluginPromptResourceService>.Instance);
        var prompts = new PromptService(
            userPrompts,
            new CharacterRepository(factory),
            sessions,
            new KnowledgeService(new KnowledgeRepository(factory), NullLogger<KnowledgeService>.Instance),
            pluginResources: resources,
            pluginResourceSelections: selections,
            logger: NullLogger<PromptService>.Instance);
        return new Setup(userPrompts, sessions, selections, plugins, prompts);
    }

    private string CreatePackage()
    {
        var path = Path.Combine(directory, "sample.ntplugin");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = new PluginManifest
        {
            Id = "sample.integration",
            Name = "Selection integration",
            Version = "1.0.0",
            Capabilities = [PluginPromptResourceService.Capability]
        };
        var pack = new PluginPromptResourcePack
        {
            FormatVersion = 1,
            TextRules =
            [
                new PluginPackTextRule { Id = "rule-a", Name = "Rule A", Content = "PLUGIN_RULE_ALPHA" },
                new PluginPackTextRule { Id = "rule-b", Name = "Rule B", Content = "PLUGIN_RULE_BETA" },
                new PluginPackTextRule
                {
                    Id = "rule-group", Name = "Group Rule", Content = "PLUGIN_GROUP_ONLY", Scope = "group"
                }
            ]
        };
        WriteEntry(archive, "plugin.json", JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        WriteEntry(archive, PluginPromptResourceService.DataFileName, JsonSerializer.Serialize(pack, JsonDefaults.Options));
        return path;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static async Task<ChatSession> CreateSessionAsync(ChatSessionRepository sessions, long? personaId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new ChatSession { Title = "Test chat", PersonaId = personaId, CreatedAt = now, UpdatedAt = now };
        await sessions.CreateAsync(session);
        return session;
    }

    private static async Task<string> BuildSystemTextAsync(PromptService prompts, ChatSession session)
    {
        var history = new[] { new ChatMessage { Role = ChatRole.User, Content = "Hello" } };
        var result = await prompts.BuildAsync(session, history, new ProviderSettings
        {
            Model = "test-model",
            IncludeCharacterContext = false,
            IncludeKnowledgeContext = false,
            CharacterMemoryEnabled = false
        });
        return string.Join("\n", result.Messages.Where(message => message.Role == "system").Select(message => message.Content));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
    }

    private sealed record Setup(
        PromptRepository UserPrompts,
        ChatSessionRepository Sessions,
        PluginResourceSelectionRepository Selections,
        PluginService Plugins,
        PromptService Prompts);
}
