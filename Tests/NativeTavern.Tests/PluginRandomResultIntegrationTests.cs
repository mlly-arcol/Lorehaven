using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Models;
using NativeTavern.Services;
using NativeTavern.ViewModels;
using Xunit;

namespace NativeTavern.Tests;

public sealed class PluginRandomResultIntegrationTests
{
    [Fact]
    public async Task ResultsRoundTripWithinTheirSessionAndDisappearWithThatSession()
    {
        using var harness = new ChatViewModelHarness();
        var firstSession = await harness.CreateSessionAsync("first");
        var secondSession = await harness.CreateSessionAsync("second");
        var repository = new PluginRandomResultRepository(harness.Factory);
        var earlier = new DiceRandomResult
        {
            Kind = DiceRandomKind.Dice,
            SourceLabel = "2d6+1",
            ResultText = "8",
            DetailText = "3 + 4 + 1 = 8",
            CreatedAtUtc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            IncludeInModelContext = true
        };
        var later = new DiceRandomResult
        {
            Kind = DiceRandomKind.Table,
            PluginId = "test.dice",
            SourceLabel = "Plot",
            ResultText = "A sealed letter arrives",
            DetailText = "Plot：A sealed letter arrives",
            CreatedAtUtc = earlier.CreatedAtUtc.AddMinutes(1)
        };
        var other = new DiceRandomResult
        {
            Kind = DiceRandomKind.Dice,
            SourceLabel = "d20",
            ResultText = "17",
            CreatedAtUtc = earlier.CreatedAtUtc.AddMinutes(2)
        };

        await repository.SaveAsync(firstSession.Id, earlier);
        await repository.SaveAsync(firstSession.Id, later);
        await repository.SaveAsync(secondSession.Id, other);

        var reloaded = new PluginRandomResultRepository(harness.Factory);
        var firstResults = await reloaded.GetRecentAsync(firstSession.Id);
        Assert.Equal(new[] { later.Id, earlier.Id }, firstResults.Select(x => x.Id));
        Assert.Equal(later.PluginId, firstResults[0].PluginId);
        Assert.Equal(later.Kind, firstResults[0].Kind);
        Assert.Equal(later.ResultText, firstResults[0].ResultText);
        Assert.Equal(later.DetailText, firstResults[0].DetailText);
        Assert.Equal(later.CreatedAtUtc, firstResults[0].CreatedAtUtc);
        Assert.False(firstResults[1].IncludeInModelContext);
        Assert.Null(firstResults[1].ModelContextText);
        Assert.Equal(other.Id, Assert.Single(await reloaded.GetRecentAsync(secondSession.Id)).Id);
        Assert.Equal(later.Id, Assert.Single(await reloaded.GetRecentAsync(firstSession.Id, 1)).Id);

        await harness.Sessions.DeleteAsync(firstSession.Id);
        Assert.Empty(await reloaded.GetRecentAsync(firstSession.Id));
        Assert.Equal(other.Id, Assert.Single(await reloaded.GetRecentAsync(secondSession.Id)).Id);
    }

    [Fact]
    public async Task RandomOutcomeReachesModelOnlyAfterExplicitDraftInsertion()
    {
        using var harness = new ChatViewModelHarness();
        var now = DateTimeOffset.UtcNow;
        var otherSession = await harness.CreateSessionAsync("other", now.AddMinutes(-1));
        var currentSession = await harness.CreateSessionAsync("current", now);
        var token = "RANDOM_TABLE_MARKER_" + Guid.NewGuid().ToString("N");
        var pluginService = CreateInstalledDicePlugin(harness.RootDirectory, token);
        var resultRepository = new PluginRandomResultRepository(harness.Factory);
        var viewModel = CreateViewModel(harness, pluginService, resultRepository);
        await viewModel.InitializeAsync();
        Assert.Equal(currentSession.Id, viewModel.CurrentSession!.Id);

        var rolled = await viewModel.RollDiceAsync("d1");
        var drawn = await viewModel.DrawRandomTableAsync("test.dice/plot");
        Assert.Equal("1", rolled.ResultText);
        Assert.Equal(token, drawn.ResultText);
        Assert.False(drawn.IncludeInModelContext);
        Assert.Empty(harness.Provider.SnapshotRequests());
        Assert.Equal(2, (await viewModel.GetRecentRandomResultsAsync()).Count);

        viewModel.SelectedSession = viewModel.Sessions.Single(x => x.Id == otherSession.Id);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() =>
            viewModel.CurrentSession?.Id == otherSession.Id && !viewModel.IsSessionLoading));
        Assert.Empty(await viewModel.GetRecentRandomResultsAsync());
        viewModel.SelectedSession = viewModel.Sessions.Single(x => x.Id == currentSession.Id);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() =>
            viewModel.CurrentSession?.Id == currentSession.Id && !viewModel.IsSessionLoading));
        Assert.Equal(2, (await viewModel.GetRecentRandomResultsAsync()).Count);

        harness.Provider.Release();
        viewModel.InputText = "继续剧情";
        await viewModel.SendCommand.ExecuteAsync(null);
        var requestsBeforeInsertion = harness.Provider.SnapshotRequests();
        Assert.NotEmpty(requestsBeforeInsertion);
        Assert.DoesNotContain(requestsBeforeInsertion.SelectMany(x => x.Messages),
            x => x.Content.Contains(token, StringComparison.Ordinal));

        viewModel.AddRandomResultToDraft(drawn);
        Assert.Contains(token, viewModel.InputText);
        Assert.False(drawn.IncludeInModelContext);
        await viewModel.SendCommand.ExecuteAsync(null);
        var requestsAfterInsertion = harness.Provider.SnapshotRequests();
        Assert.Contains(requestsAfterInsertion.Skip(requestsBeforeInsertion.Count).SelectMany(x => x.Messages),
            message => message.Role == "user" && message.Content.Contains(token, StringComparison.Ordinal));
    }

    private static PluginService CreateInstalledDicePlugin(string root, string outcome)
    {
        var pluginDirectory = Path.Combine(root, "plugins");
        var directory = Path.Combine(pluginDirectory, "test.dice");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = "test.dice",
            Name = "Test dice",
            Version = "1.0.0",
            Capabilities = [DiceTableService.Capability]
        }, JsonDefaults.Options));
        File.WriteAllText(Path.Combine(directory, DiceTableService.TablesFileName), JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            tables = new[]
            {
                new
                {
                    id = "plot", name = "Plot",
                    entries = new[] { new { text = outcome, weight = 1 } }
                }
            }
        }, JsonDefaults.Options));
        return new PluginService(pluginDirectory, NullLogger<PluginService>.Instance);
    }

    private static ChatViewModel CreateViewModel(
        ChatViewModelHarness harness, PluginService pluginService,
        PluginRandomResultRepository resultRepository)
    {
        var factory = harness.Factory;
        var messages = new ChatMessageRepository(factory);
        var characters = new CharacterRepository(factory);
        var settings = new SettingsService(new SettingsRepository(factory), new NoopProtector());
        return new ChatViewModel(
            harness.Service,
            new PromptRepository(factory),
            harness.Drafts,
            settings,
            harness.Scripts,
            new CharacterStatusService(new CharacterStatusRepository(factory), messages, characters,
                settings, pluginService, harness.Provider, NullLogger<CharacterStatusService>.Instance),
            new ReplySuggestionService(messages, settings, harness.Provider,
                NullLogger<ReplySuggestionService>.Instance),
            pluginService,
            new TrayService(),
            harness.Memory,
            new ConversationTitleService(messages, settings, harness.Provider),
            NullLogger<ChatViewModel>.Instance,
            diceTableService: new DiceTableService(pluginService),
            randomResultRepository: resultRepository);
    }
}
