using System.Collections.Concurrent;
using System.Collections.Specialized;
using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using NativeTavern.ViewModels;
using Xunit;

namespace NativeTavern.Tests;

public sealed class SessionLoadingPerformanceTests
{
    [Fact]
    public void ReplacingLongConversationNotifiesTheViewOnlyOnce()
    {
        var messages = new MessageCollection();
        messages.Add(ViewMessage(0));
        var changes = new List<NotifyCollectionChangedAction>();
        messages.CollectionChanged += (_, change) => changes.Add(change.Action);
        var loaded = Enumerable.Range(1, 2000).Select(ViewMessage).ToArray();

        messages.ReplaceAll(loaded);

        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, changes);
        Assert.Equal(loaded.Select(x => x.Model.Id), messages.Select(x => x.Model.Id));
        messages.Add(ViewMessage(2001));
        Assert.Equal(NotifyCollectionChangedAction.Add, changes[^1]);
    }

    [Fact]
    public async Task StaleSelectionLoadCannotReplaceCurrentConversation()
    {
        using var harness = new ChatViewModelHarness();
        var current = await harness.CreateSessionAsync("当前对话", DateTimeOffset.UtcNow);
        var selected = await harness.CreateSessionAsync("新选择", DateTimeOffset.UtcNow.AddMinutes(-1));
        var stale = await harness.CreateSessionAsync("旧加载", DateTimeOffset.UtcNow.AddMinutes(-2));
        var repository = new ChatMessageRepository(harness.Factory);
        await AddMessageAsync(repository, selected.Id, "新选择的内容");
        await AddMessageAsync(repository, stale.Id, "旧加载的内容");
        var vm = harness.ViewModel;
        await vm.InitializeAsync();
        Assert.Equal(current.Id, vm.CurrentSession?.Id);

        var collectionChanges = new ConcurrentQueue<NotifyCollectionChangedAction>();
        vm.Messages.CollectionChanged += (_, change) => collectionChanges.Enqueue(change.Action);
        vm.SelectedSession = vm.Sessions.Single(x => x.Id == selected.Id);
        Assert.True(await ChatViewModelHarness.WaitForAsync(
            () => vm.CurrentSession?.Id == selected.Id && !vm.IsSessionLoading));
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, collectionChanges.ToArray());

        // Reproduce a previous selection's database work finishing after the new selection.
        await vm.LoadSessionCoreAsync(stale, requireSelectedSession: true);

        Assert.Equal(selected.Id, vm.CurrentSession?.Id);
        Assert.Equal(selected.Id, vm.SelectedSession?.Id);
        Assert.Equal("新选择的内容", Assert.Single(vm.Messages).Content);
    }

    [Fact]
    public async Task PromptPreviewUsesTheHistoryAlreadyLoadedForTheConversation()
    {
        using var harness = new ChatViewModelHarness();
        var session = await harness.CreateSessionAsync("预览");
        var repository = new ChatMessageRepository(harness.Factory);
        var storedId = await AddMessageAsync(repository, session.Id, "数据库里的消息");
        var loaded = new ChatMessage
        {
            Id = storedId + 1000, ChatSessionId = session.Id, Role = ChatRole.User,
            Content = "已经载入的消息", CreatedAt = DateTimeOffset.UtcNow
        };

        var preview = await harness.Service.GetPromptPreviewAsync(session, [loaded]);

        Assert.Contains(preview.Messages, x => x.SourceMessageId == loaded.Id && x.Content == loaded.Content);
        Assert.DoesNotContain(preview.Messages, x => x.SourceMessageId == storedId);
    }

    private static ChatMessageViewModel ViewMessage(int id) => new(new ChatMessage
    {
        Id = id, Role = ChatRole.User, Content = $"消息 {id}", CreatedAt = DateTimeOffset.UtcNow
    });

    private static Task<long> AddMessageAsync(ChatMessageRepository repository, long sessionId, string content) =>
        repository.AddAsync(new ChatMessage
        {
            ChatSessionId = sessionId, Role = ChatRole.User,
            Content = content, CreatedAt = DateTimeOffset.UtcNow
        });
}
