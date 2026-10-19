using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using Xunit;

namespace NativeTavern.Tests;

public sealed class ChatSearchViewModelTests : IAsyncLifetime, IDisposable
{
    private readonly ChatViewModelHarness _harness = new();
    private ChatSession _first = null!;
    private ChatSession _second = null!;
    private ChatMessage _firstPinned = null!;
    private ChatMessage _secondPinned = null!;

    public async Task InitializeAsync()
    {
        _first = await _harness.CreateSessionAsync("旧对话", DateTimeOffset.UtcNow.AddDays(-1));
        _second = await _harness.CreateSessionAsync("当前对话", DateTimeOffset.UtcNow);
        _firstPinned = await AddAsync(_first.Id, "剑在山门", true);
        _secondPinned = await AddAsync(_second.Id, "剑在界碑", true);
        await AddAsync(_second.Id, "没有收藏的消息", false);
        await _harness.ViewModel.InitializeAsync();
    }

    private async Task<ChatMessage> AddAsync(long sessionId, string content, bool pinned)
    {
        var message = new ChatMessage
        {
            ChatSessionId = sessionId, Content = content, Role = ChatRole.User,
            IsPinned = pinned, CreatedAt = DateTimeOffset.UtcNow
        };
        await new ChatMessageRepository(_harness.Factory).AddAsync(message);
        return message;
    }

    [Fact]
    public async Task OneCharacterKeywordsSearchAcrossConversations()
    {
        var vm = _harness.ViewModel;
        vm.SearchQuery = "剑";
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.SearchResults.Count);
        Assert.True(vm.IsSearchOpen);
        Assert.True(vm.HasSearchResults);
        Assert.False(vm.ShowNoSearchResults);
    }

    [Fact]
    public async Task CurrentConversationFilterCanBrowseWithoutAKeyword()
    {
        var vm = _harness.ViewModel;
        vm.SearchOnlyCurrentConversation = true;
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.SearchResults.Count);
        Assert.All(vm.SearchResults, result => Assert.Equal(_second.Id, result.ChatSessionId));
    }

    [Fact]
    public async Task FavoritesCanBeBrowsedAcrossAllConversationsOrJustTheCurrentOne()
    {
        var vm = _harness.ViewModel;
        vm.SearchPinnedOnly = true;
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        Assert.Equal(new[] { _secondPinned.Id, _firstPinned.Id }, vm.SearchResults.Select(x => x.MessageId));
        vm.SearchOnlyCurrentConversation = true;
        Assert.Empty(vm.SearchResults);
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        Assert.Equal(_secondPinned.Id, Assert.Single(vm.SearchResults).MessageId);
    }

    [Fact]
    public async Task ChangingTheQueryAndClearingSearchRemoveOldResults()
    {
        var vm = _harness.ViewModel;
        vm.SearchQuery = "剑";
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        vm.SearchQuery = "没有匹配";
        Assert.Empty(vm.SearchResults);
        Assert.False(vm.IsSearchOpen);
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        Assert.True(vm.ShowNoSearchResults);
        Assert.False(vm.HasSearchError);
        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, vm.SearchQuery);
        Assert.False(vm.IsSearchOpen);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task OpeningAResultAwaitsItsConversationBeforeRequestingTheJump()
    {
        var vm = _harness.ViewModel;
        vm.SearchQuery = "山门";
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        var result = Assert.Single(vm.SearchResults);
        long? jumpedMessage = null;
        vm.JumpToMessageRequested += messageId =>
        {
            Assert.Equal(_first.Id, vm.CurrentSession!.Id);
            Assert.Contains(vm.Messages, message => message.Model.Id == messageId);
            jumpedMessage = messageId;
        };
        await vm.OpenSearchResultCommand.ExecuteAsync(result);
        Assert.Equal(_firstPinned.Id, jumpedMessage);
        Assert.Equal(_first.Id, vm.SelectedSession!.Id);
        Assert.False(vm.IsSearchOpen);
    }

    [Fact]
    public async Task SwitchingConversationsInvalidatesScopedResults()
    {
        var vm = _harness.ViewModel;
        vm.SearchOnlyCurrentConversation = true;
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        vm.SelectedSession = vm.Sessions.Single(x => x.Id == _first.Id);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => vm.CurrentSession?.Id == _first.Id));
        Assert.Empty(vm.SearchResults);
        Assert.False(vm.IsSearchOpen);
        await vm.SearchMessagesCommand.ExecuteAsync(null);
        Assert.Equal(_firstPinned.Id, Assert.Single(vm.SearchResults).MessageId);
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _harness.Dispose();
}
