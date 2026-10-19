using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Providers;
using Xunit;

namespace NativeTavern.Tests;

public sealed class GroupPerformanceViewModelTests
{
    private static async Task<long[]> PrepareAsync(ChatViewModelHarness harness)
    {
        var repository = new CharacterRepository(harness.Factory);
        var ids = new List<long>();
        foreach (var name in new[] { "阿澄", "守墓人" })
            ids.Add(await repository.CreateAsync(new Character
            {
                Name = name, GroupName = "舞台", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            }));
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.StartGroupChatAsync("舞台");
        Assert.True(harness.ViewModel.CanConfigureGroupPerformance);
        return ids.ToArray();
    }

    [Fact]
    public async Task ConfiguredOrderRepeatsForExactlyTheRequestedRounds()
    {
        using var harness = new ChatViewModelHarness();
        var ids = await PrepareAsync(harness);
        harness.Provider.Release();
        await harness.ViewModel.StartGroupPerformanceAsync(2, [ids[1], ids[0]]);

        Assert.Equal(new long?[] { ids[1], ids[0], ids[1], ids[0] },
            harness.ViewModel.Messages.Select(x => x.Model.SpeakerCharacterId));
        Assert.False(harness.ViewModel.IsGroupPerformanceActive);
        Assert.False(harness.ViewModel.IsGenerating);
        Assert.Contains("演出完成", harness.ViewModel.GroupPerformanceStatus);
        Assert.Equal(4, harness.Provider.Started); // No suggestion request for every automatic turn.
    }

    [Fact]
    public async Task PauseFinishesCurrentReplyAndResumeStartsWithTheNextMember()
    {
        using var harness = new ChatViewModelHarness();
        var ids = await PrepareAsync(harness);
        var performance = harness.ViewModel.StartGroupPerformanceAsync(2, ids);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => harness.Provider.Started > 0));
        Assert.False(harness.ViewModel.NewChatCommand.CanExecute(null));
        Assert.False(harness.ViewModel.NextSpeakerCommand.CanExecute(null));
        harness.ViewModel.PauseGroupPerformanceCommand.Execute(null);
        harness.Provider.Release();
        await performance;

        Assert.True(harness.ViewModel.IsGroupPerformancePaused);
        Assert.Single(harness.ViewModel.Messages);
        Assert.Equal("夜色沉下来。", harness.ViewModel.Messages[0].Content);
        await harness.ViewModel.ResumeGroupPerformanceCommand.ExecuteAsync(null);
        Assert.Equal(new long?[] { ids[0], ids[1], ids[0], ids[1] },
            harness.ViewModel.Messages.Select(x => x.Model.SpeakerCharacterId));
        Assert.False(harness.ViewModel.IsGroupPerformanceActive);
    }

    [Fact]
    public async Task InterjectionIsSentBeforeAnyFurtherPerformanceTurnAndLeavesPerformancePaused()
    {
        using var harness = new ChatViewModelHarness();
        var ids = await PrepareAsync(harness);
        var performance = harness.ViewModel.StartGroupPerformanceAsync(2, ids);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => harness.Provider.Started > 0));
        harness.ViewModel.InputText = "等一下，我想问个问题。";
        await harness.ViewModel.SendCommand.ExecuteAsync(null);
        Assert.Single(harness.ViewModel.QueuedSends);
        harness.Provider.Release();
        await performance;

        Assert.True(harness.ViewModel.IsGroupPerformancePaused);
        Assert.Equal(3, harness.ViewModel.Messages.Count);
        Assert.Equal(ChatRole.User, harness.ViewModel.Messages[1].Model.Role);
        Assert.Equal("等一下，我想问个问题。", harness.ViewModel.Messages[1].Content);
        Assert.Empty(harness.ViewModel.QueuedSends);
        await harness.ViewModel.ResumeGroupPerformanceCommand.ExecuteAsync(null);
        Assert.Equal(ids[1], harness.ViewModel.Messages[3].Model.SpeakerCharacterId);
        Assert.Equal(6, harness.ViewModel.Messages.Count);
    }

    [Fact]
    public async Task EndingPerformanceDoesNotStartAnotherTurn()
    {
        using var harness = new ChatViewModelHarness();
        var ids = await PrepareAsync(harness);
        var performance = harness.ViewModel.StartGroupPerformanceAsync(20, ids);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => harness.Provider.Started > 0));
        harness.ViewModel.StopGroupPerformanceCommand.Execute(null);
        harness.Provider.Release();
        await performance;

        Assert.Equal(1, harness.Provider.Started);
        Assert.False(harness.ViewModel.IsGroupPerformanceActive);
        Assert.False(harness.ViewModel.ResumeGroupPerformanceCommand.CanExecute(null));
    }

    [Fact]
    public async Task SwitchingAwayFromPausedPerformanceClearsItsPlan()
    {
        using var harness = new ChatViewModelHarness();
        var ids = await PrepareAsync(harness);
        var performance = harness.ViewModel.StartGroupPerformanceAsync(2, ids);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => harness.Provider.Started > 0));
        harness.ViewModel.PauseGroupPerformanceCommand.Execute(null);
        harness.Provider.Release();
        await performance;
        var other = await harness.CreateSessionAsync("另一条路线");

        await harness.ViewModel.OpenConversationBranchAsync(other.Id);

        Assert.Equal(other.Id, harness.ViewModel.CurrentSession!.Id);
        Assert.False(harness.ViewModel.IsGroupPerformanceActive);
        Assert.False(harness.ViewModel.ResumeGroupPerformanceCommand.CanExecute(null));
        Assert.Empty(harness.ViewModel.Messages);
    }

    [Fact]
    public async Task UnknownOrIncompleteSpeakerOrderCannotStartAPerformance()
    {
        using var harness = new ChatViewModelHarness();
        var ids = await PrepareAsync(harness);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ViewModel.StartGroupPerformanceAsync(2, [ids[0], 999999]));
        Assert.False(harness.ViewModel.IsGroupPerformanceActive);
        Assert.Equal(0, harness.Provider.Started);
    }

    [Fact]
    public async Task ModelFailurePausesAtTheSameTurnAndCanBeRetried()
    {
        using var harness = new ChatViewModelHarness();
        var ids = await PrepareAsync(harness);
        harness.Provider.Router = _ => throw new ProviderException("连接中断");
        await harness.ViewModel.StartGroupPerformanceAsync(1, ids);
        Assert.True(harness.ViewModel.IsGroupPerformancePaused);
        Assert.Empty(harness.ViewModel.Messages);
        Assert.Equal("连接中断", harness.ViewModel.ErrorMessage);

        harness.Provider.Router = _ => ["现在继续。"];
        harness.Provider.Release();
        await harness.ViewModel.ResumeGroupPerformanceCommand.ExecuteAsync(null);
        Assert.Equal(ids.Select(x => (long?)x), harness.ViewModel.Messages.Select(x => x.Model.SpeakerCharacterId));
        Assert.False(harness.ViewModel.IsGroupPerformanceActive);
    }
}
