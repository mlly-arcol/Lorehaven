using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using Xunit;

namespace NativeTavern.Tests;

public sealed class ConversationTitleViewModelTests : IAsyncLifetime, IDisposable
{
    private readonly ChatViewModelHarness _harness = new();
    private ChatSession _older = null!;
    private ChatSession _current = null!;

    public async Task InitializeAsync()
    {
        _older = await _harness.CreateSessionAsync("旧对话", DateTimeOffset.UtcNow.AddDays(-1));
        _current = await _harness.CreateSessionAsync("待整理的对话");
        await new ChatMessageRepository(_harness.Factory).AddAsync(new ChatMessage
        {
            ChatSessionId = _current.Id, Role = ChatRole.User,
            Content = "我们在山门外找到了古老的界碑。", CreatedAt = DateTimeOffset.UtcNow
        });
        _harness.Provider.Scripts.Clear();
        _harness.Provider.Scripts.Add(["{\"title\":\"界碑之谜\"}"]);
        await _harness.ViewModel.InitializeAsync();
    }

    [Fact]
    public async Task GeneratedTitleIsSavedAndDisplayed()
    {
        _harness.Provider.Release();
        await _harness.ViewModel.GenerateConversationTitleCommand.ExecuteAsync(null);
        Assert.Equal("界碑之谜", _harness.ViewModel.SessionTitle);
        Assert.Equal("界碑之谜", (await _harness.Sessions.GetAsync(_current.Id))!.Title);
        Assert.False(_harness.ViewModel.IsGeneratingTitle);
    }

    [Fact]
    public async Task ManualRenameWinsOverAnInFlightGeneratedTitle()
    {
        var task = _harness.ViewModel.GenerateConversationTitleCommand.ExecuteAsync(null);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => _harness.Provider.Started > 0));
        try { await _harness.ViewModel.RenameCurrentConversationAsync("我自己的标题"); }
        finally { _harness.Provider.Release(); }
        await task;
        Assert.Equal("我自己的标题", _harness.ViewModel.SessionTitle);
        Assert.Equal("我自己的标题", (await _harness.Sessions.GetAsync(_current.Id))!.Title);
    }

    [Fact]
    public async Task SwitchingConversationsCancelsTheOriginalNamingRequest()
    {
        var vm = _harness.ViewModel;
        var task = vm.GenerateConversationTitleCommand.ExecuteAsync(null);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => _harness.Provider.Started > 0));
        try
        {
            vm.SelectedSession = vm.Sessions.Single(x => x.Id == _older.Id);
            Assert.True(await ChatViewModelHarness.WaitForAsync(() => vm.CurrentSession?.Id == _older.Id));
        }
        finally { _harness.Provider.Release(); }
        await task;
        Assert.Equal("旧对话", vm.SessionTitle);
        Assert.Equal("待整理的对话", (await _harness.Sessions.GetAsync(_current.Id))!.Title);
        Assert.False(vm.GenerateConversationTitleCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExplicitCancellationDoesNotChangeTheTitleOrShowAnError()
    {
        var vm = _harness.ViewModel;
        var task = vm.GenerateConversationTitleCommand.ExecuteAsync(null);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => vm.IsGeneratingTitle));
        Assert.True(vm.CancelConversationTitleCommand.CanExecute(null));
        vm.CancelConversationTitleCommand.Execute(null);
        _harness.Provider.Release();
        await task;
        Assert.Equal("待整理的对话", vm.SessionTitle);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsGeneratingTitle);
    }

    [Fact]
    public async Task MalformedModelOutputLeavesTheOriginalTitleIntact()
    {
        _harness.Provider.Scripts[0] = ["这是一个解释，不是标题 JSON"];
        _harness.Provider.Release();
        await _harness.ViewModel.GenerateConversationTitleCommand.ExecuteAsync(null);
        Assert.Equal("待整理的对话", _harness.ViewModel.SessionTitle);
        Assert.Contains("有效标题", _harness.ViewModel.ErrorMessage);
        Assert.Equal("待整理的对话", (await _harness.Sessions.GetAsync(_current.Id))!.Title);
    }

    [Fact]
    public async Task ANewerPersistedRenameCannotBeOverwrittenByTheModel()
    {
        var task = _harness.ViewModel.GenerateConversationTitleCommand.ExecuteAsync(null);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => _harness.Provider.Started > 0));
        try { await _harness.Sessions.SetTitleAsync(_current.Id, "后来的标题", DateTimeOffset.UtcNow); }
        finally { _harness.Provider.Release(); }
        await task;
        Assert.Equal("后来的标题", (await _harness.Sessions.GetAsync(_current.Id))!.Title);
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _harness.Dispose();
}
