using NativeTavern.Data.Repositories;
using NativeTavern.Models;
using Xunit;

namespace NativeTavern.Tests;

public sealed class JournalViewModelTests
{
    [Fact]
    public async Task NotesShowOnlyTheCurrentStoryRouteAndItsNextBranches()
    {
        using var harness = new ChatViewModelHarness();
        var root = await harness.CreateSessionAsync("抵达港口", DateTimeOffset.UtcNow.AddHours(-3));
        var current = await harness.CreateSessionAsync("留下修灯");
        current.ParentSessionId = root.Id;
        await harness.Sessions.UpdateAsync(current);
        var child = await harness.CreateSessionAsync("寻找零件", DateTimeOffset.UtcNow.AddHours(-1));
        child.ParentSessionId = current.Id;
        await harness.Sessions.UpdateAsync(child);
        await harness.CreateSessionAsync("另一段故事", DateTimeOffset.UtcNow.AddHours(-2));

        await harness.ViewModel.InitializeAsync();

        Assert.Equal(current.Id, harness.ViewModel.CurrentSession!.Id);
        Assert.Equal(new[] { root.Id, current.Id, child.Id }, harness.ViewModel.JournalBranches.Select(item => item.SessionId));
        Assert.Equal(current.Id, Assert.Single(harness.ViewModel.JournalBranches, item => item.IsCurrent).SessionId);
        await harness.ViewModel.RenameCurrentConversationAsync("约定修好灯塔");
        Assert.Equal("约定修好灯塔", Assert.Single(harness.ViewModel.JournalBranches, item => item.IsCurrent).Title);
    }

    [Fact]
    public async Task NotesBranchLinkNavigatesAndKeepsTheOutgoingDraft()
    {
        using var harness = new ChatViewModelHarness();
        var root = await harness.CreateSessionAsync("起点", DateTimeOffset.UtcNow.AddHours(-1));
        var branch = await harness.CreateSessionAsync("新路线");
        branch.ParentSessionId = root.Id;
        await harness.Sessions.UpdateAsync(branch);
        await harness.ViewModel.InitializeAsync();
        harness.ViewModel.InputText = "尚未发送的想法";

        await harness.ViewModel.OpenJournalBranchCommand.ExecuteAsync(harness.ViewModel.JournalBranches.First());

        Assert.Equal(root.Id, harness.ViewModel.CurrentSession!.Id);
        Assert.Equal("起点", harness.ViewModel.SessionTitle);
        Assert.Equal("尚未发送的想法", (await harness.Drafts.GetAllAsync()).Single(draft => draft.ChatSessionId == branch.Id).Text);
        Assert.Equal(0, harness.Provider.Started);
    }

    [Fact]
    public async Task NotesLoadPrivateMemoriesWithoutOpeningTheEditor()
    {
        using var harness = new ChatViewModelHarness();
        var repository = new CharacterRepository(harness.Factory);
        var people = new List<Character>();
        foreach (var name in new[] { "知夏", "行舟" })
        {
            var character = new Character { Name = name, GroupName = "同行", Scenario = "黄昏的港口", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            character.Id = await repository.CreateAsync(character);
            people.Add(character);
            await harness.Memory.AddManualAsync(character.Id, name + "的约定", true);
        }
        await harness.ViewModel.InitializeAsync();
        await harness.ViewModel.StartGroupChatAsync("同行");
        var vm = harness.ViewModel;
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => vm.CharacterMemories.Count == 1));
        Assert.False(vm.IsMemoryOpen);
        Assert.Equal("黄昏的港口", vm.JournalDescription);

        vm.SelectedMemoryCharacter = vm.MemoryCharacters.Single(person => person.Id == people[1].Id);

        Assert.True(await ChatViewModelHarness.WaitForAsync(() => vm.CharacterMemories.Count == 1 && vm.CharacterMemories[0].CharacterId == people[1].Id));
        Assert.Equal("行舟的约定", vm.CharacterMemories[0].Content);
        Assert.False(vm.IsMemoryOpen);
        Assert.Equal(0, harness.Provider.Started);

        await harness.Memory.AddManualAsync(people[1].Id, "后续新增的记忆", true);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => vm.CharacterMemories.Count == 2));
        var added = vm.CharacterMemories.Single(memory => memory.Content == "后续新增的记忆");
        await harness.Memory.DeleteAsync(added.Id);
        Assert.True(await ChatViewModelHarness.WaitForAsync(() => vm.CharacterMemories.Count == 1));
        Assert.Equal("行舟的约定", vm.CharacterMemories[0].Content);
    }
}
