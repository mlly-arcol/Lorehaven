using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.Services;
using Xunit;

namespace NativeTavern.Tests;

public sealed class MessageMutationViewModelTests
{
    [Theory]
    [InlineData("edit", false)]
    [InlineData("delete", false)]
    [InlineData("swipe", false)]
    [InlineData("edit", true)]
    [InlineData("delete", true)]
    [InlineData("swipe", true)]
    public async Task ChangingEarlierHistoryRefreshesOnlyAnAutomaticSummary(string operation, bool manual)
    {
        using var harness = new ChatViewModelHarness();
        var session = await harness.CreateSessionAsync("长对话摘要");
        var original = await AddAssistantWithSwipesAsync(harness, session.Id);
        var messages = new ChatMessageRepository(harness.Factory);
        for (var index = 1; index < 25; index++)
            await messages.AddAsync(new ChatMessage
            {
                ChatSessionId = session.Id, Role = ChatRole.User, Content = $"后续消息 {index}",
                CreatedAt = DateTimeOffset.UtcNow
            });
        await harness.Service.RebuildStorySummaryAsync(session);
        const string manualSummary = "用户亲自写的剧情回顾，必须原样保留。";
        if (manual) await harness.Service.SaveStorySummaryAsync(session, manualSummary);
        harness.Provider.Release();
        var vm = harness.ViewModel;
        await vm.InitializeAsync();
        var bubble = vm.Messages.Single(x => x.Model.Id == original.Id);

        switch (operation)
        {
            case "edit":
                vm.BeginEditCommand.Execute(bubble);
                bubble.EditText = "修订后的早期剧情";
                await vm.SaveEditCommand.ExecuteAsync(bubble);
                break;
            case "delete":
                await vm.DeleteMessageAsync(bubble);
                break;
            case "swipe":
                await vm.SwipeLeftCommand.ExecuteAsync(bubble);
                break;
        }

        var persisted = (await harness.Sessions.GetAsync(session.Id))!;
        if (manual)
        {
            Assert.Equal(manualSummary, persisted.Summary);
            Assert.True(persisted.SummaryIsManual);
        }
        else
        {
            Assert.StartsWith(ConversationSummaryService.Header, persisted.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("第二个候选", persisted.Summary, StringComparison.Ordinal);
            if (operation == "edit") Assert.Contains("修订后的早期剧情", persisted.Summary, StringComparison.Ordinal);
            if (operation == "swipe") Assert.Contains("第一个候选", persisted.Summary, StringComparison.Ordinal);
            Assert.Equal(operation == "delete" ? 14 : 15, persisted.SummaryCoveredCount);
            Assert.False(persisted.SummaryIsManual);
        }
        Assert.Equal(persisted.Summary, vm.CurrentSession!.Summary);
        Assert.Equal(persisted.Summary, vm.StorySummary);
        Assert.Equal(persisted.SummaryCoveredCount, vm.StorySummaryCoveredCount);
    }

    [Fact]
    public async Task DeletingAMessageInvalidatesItsPendingReplySuggestions()
    {
        using var harness = new ChatViewModelHarness();
        var session = await harness.CreateSessionAsync("待删除的回复");
        var assistant = await AddAssistantWithSwipesAsync(harness, session.Id);
        await new ChatMessageRepository(harness.Factory).AddAsync(new ChatMessage
        {
            ChatSessionId = session.Id, Role = ChatRole.User,
            Content = "还没有助手回答的问题", CreatedAt = DateTimeOffset.UtcNow
        });
        harness.Provider.Scripts[0] = ["{\"suggestions\":[\"基于已删除回复的建议\",\"旧建议二\",\"旧建议三\"]}"];
        var vm = harness.ViewModel;
        await vm.InitializeAsync();
        var bubble = vm.Messages.Single(x => x.Model.Id == assistant.Id);
        var pending = vm.SwipeLeftCommand.ExecuteAsync(bubble);
        try
        {
            Assert.True(await ChatViewModelHarness.WaitForAsync(() => harness.Provider.Started > 0));
            await vm.DeleteMessageAsync(bubble);
        }
        finally { harness.Provider.Release(); }
        await pending;

        Assert.DoesNotContain(vm.Messages, x => x.Model.Id == assistant.Id);
        Assert.Empty(vm.ReplySuggestions);
        Assert.False(vm.HasReplySuggestions);
        Assert.False(vm.IsGeneratingSuggestions);
    }

    [Fact]
    public async Task AContextMenuFromThePreviousConversationCannotChangeItsMessages()
    {
        using var harness = new ChatViewModelHarness();
        var previous = await harness.CreateSessionAsync("之前的对话");
        var incoming = await harness.CreateSessionAsync("新对话", DateTimeOffset.UtcNow.AddMinutes(-1));
        var stored = await AddAssistantWithSwipesAsync(harness, previous.Id);
        harness.Provider.Release();
        var vm = harness.ViewModel;
        await vm.InitializeAsync();
        var stale = Assert.Single(vm.Messages);
        vm.SelectedSession = vm.Sessions.Single(x => x.Id == incoming.Id);
        Assert.True(await ChatViewModelHarness.WaitForAsync(
            () => vm.CurrentSession?.Id == incoming.Id && !vm.IsSessionLoading));

        vm.BeginEditCommand.Execute(stale);
        Assert.False(stale.IsEditing);
        stale.EditText = "迟到的编辑";
        await vm.SaveEditCommand.ExecuteAsync(stale);
        await vm.TogglePinCommand.ExecuteAsync(stale);
        await vm.SwipeLeftCommand.ExecuteAsync(stale);
        await vm.DeleteMessageAsync(stale);

        var persisted = Assert.Single(await new ChatMessageRepository(harness.Factory).GetBySessionAsync(previous.Id));
        Assert.Equal(stored.Content, persisted.Content);
        Assert.Equal(1, persisted.CurrentSwipeIndex);
        Assert.False(persisted.IsPinned);
        Assert.Empty(vm.Messages);
        Assert.Equal(incoming.Id, vm.CurrentSession?.Id);
    }

    private static async Task<ChatMessage> AddAssistantWithSwipesAsync(ChatViewModelHarness harness, long sessionId)
    {
        var assistant = new ChatMessage
        {
            ChatSessionId = sessionId, Role = ChatRole.Assistant, Content = "第二个候选",
            CurrentSwipeIndex = 1, CreatedAt = DateTimeOffset.UtcNow
        };
        assistant.Id = await new ChatMessageRepository(harness.Factory).AddAsync(assistant);
        var swipes = new MessageSwipeRepository(harness.Factory);
        await swipes.AddAsync(new MessageSwipe
        {
            ChatMessageId = assistant.Id, SwipeIndex = 0, Content = "第一个候选", CreatedAt = assistant.CreatedAt
        });
        await swipes.AddAsync(new MessageSwipe
        {
            ChatMessageId = assistant.Id, SwipeIndex = 1, Content = assistant.Content, CreatedAt = assistant.CreatedAt
        });
        return assistant;
    }
}
